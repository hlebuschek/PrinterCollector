using System.Text.Json;

namespace PrinterCollector.Services;

public sealed class OverrideStore
{
    private readonly string _path;
    private Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    private sealed class FileShape
    {
        public Dictionary<string, string> SerialOverrides { get; set; } = new();
    }

    public OverrideStore(string? path = null)
    {
        _path = path ?? AppSettings.DefaultOverridesPath;
        Load();
    }

    public string? Get(string printerName) =>
        _overrides.TryGetValue(printerName, out var v) ? v : null;

    public void Set(string printerName, string serial)
    {
        _overrides[printerName] = serial;
        Save();
    }

    public void Clear(string printerName)
    {
        if (_overrides.Remove(printerName)) Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var data = JsonSerializer.Deserialize<FileShape>(json);
            if (data?.SerialOverrides != null)
                _overrides = new Dictionary<string, string>(data.SerialOverrides, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* битый файл — стартуем с пустого словаря */ }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(
            new FileShape { SerialOverrides = _overrides },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_path, json);
    }
}
