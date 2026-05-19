using System.Text.Json;
using PrinterCollector.Models;

namespace PrinterCollector.Services;

public sealed class OfflineQueue
{
    private readonly string _dir;
    private readonly Action<string>? _log;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public OfflineQueue(string? directory = null, Action<string>? log = null)
    {
        _dir = directory ?? AppSettings.DefaultQueueFolder;
        _log = log;
        Directory.CreateDirectory(_dir);
    }

    public string Enqueue(PrinterReading reading)
    {
        var safeSerial = SanitizeForFileName(reading.SerialNumber.Value);
        if (string.IsNullOrEmpty(safeSerial)) safeSerial = "unknown";

        var fileName = $"{safeSerial}_{reading.Timestamp:yyyyMMddHHmmss}.json";
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(reading, JsonOpts));
        return path;
    }

    public int Count => Directory.Exists(_dir)
        ? Directory.GetFiles(_dir, "*.json").Length
        : 0;

    public List<(string Path, PrinterReading Reading)> LoadAll()
    {
        var result = new List<(string, PrinterReading)>();
        if (!Directory.Exists(_dir)) return result;

        foreach (var path in Directory.GetFiles(_dir, "*.json").OrderBy(p => p))
        {
            try
            {
                var reading = JsonSerializer.Deserialize<PrinterReading>(File.ReadAllText(path));
                if (reading != null) result.Add((path, reading));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Очередь: не удалось прочитать {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        return result;
    }

    public async Task<(int Sent, int Failed, string? Error)> FlushAsync(
        ApiClient client, CancellationToken ct = default)
    {
        var items = LoadAll();
        if (items.Count == 0) return (0, 0, null);

        var readings = items.Select(i => i.Reading).ToList();
        var result = await client.UploadAsync(readings, ct).ConfigureAwait(false);

        if (!result.Success)
        {
            _log?.Invoke($"Очередь: отправка не удалась ({items.Count} элементов остаются): {result.Message}");
            return (0, items.Count, result.Message);
        }

        // Парсим тело ответа: даже при HTTP 200 отдельные readings могут быть
        // отвергнуты сервером (S/N не в /contracts/, validation_error, и т.п.).
        // Reading удаляется из очереди в любом случае — повторная отправка не поможет
        // пока админ не разберётся, а очередь должна освободиться.
        // Но логируем каждую ошибку чтобы было видно что не дошло.
        LogPerReadingResults(result.Message);

        int deleted = 0;
        foreach (var (path, _) in items)
        {
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Очередь: не удалось удалить {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        _log?.Invoke($"Очередь: отправлено {deleted}/{items.Count}");
        return (deleted, items.Count - deleted, null);
    }

    private void LogPerReadingResults(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return;

            int ok = 0, dup = 0, err = 0;
            foreach (var r in arr.EnumerateArray())
            {
                var status = r.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
                var serial = r.TryGetProperty("serial_number", out var sn) ? sn.GetString() ?? "?" : "?";
                var errMsg = r.TryGetProperty("error", out var e) ? e.GetString() : null;

                if (status == "success") ok++;
                else if (status == "duplicate") dup++;
                else
                {
                    err++;
                    _log?.Invoke($"  ✗ S/N={serial}: {errMsg ?? status}");
                }
            }
            if (err > 0 || dup > 0)
                _log?.Invoke($"Сервер: успех={ok}, дубликат={dup}, ошибка={err}");
        }
        catch
        {
            // Если ответ не JSON — просто пропускаем детальный разбор.
        }
    }

    private static string SanitizeForFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Where(c => !invalid.Contains(c) && c != ' ').ToArray());
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"[^A-Za-z0-9._-]", "_");
    }
}
