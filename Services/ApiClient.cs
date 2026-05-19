using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrinterCollector.Models;

namespace PrinterCollector.Services;

public sealed class ApiClient : IDisposable
{
    private readonly AppSettings _settings;
    private readonly string _settingsPath;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public const string AgentVersion = "0.3.2";

    public ApiClient(AppSettings settings, string? settingsPath = null, Action<string>? log = null)
    {
        _settings = settings;
        _settingsPath = settingsPath ?? AppSettings.DefaultPath;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public sealed record ApiResult(bool Success, string Message, int StatusCode = 0);

    /// <summary>
    /// Маскирует 64-hex-char последовательности (master key и Bearer-токен имеют такую длину)
    /// в строке перед записью в лог-файл или возвратом в UI.
    /// </summary>
    private static string MaskSecrets(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return System.Text.RegularExpressions.Regex.Replace(
            s, @"\b[0-9a-fA-F]{64}\b", "***");
    }

    /// <summary>
    /// Проверяет что endpoint использует HTTPS. HTTP допускается только для localhost
    /// (для отладки в dev-окружении).
    /// </summary>
    private static (bool Ok, string? Error) ValidateEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return (false, "Не задан ApiEndpoint");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return (false, "ApiEndpoint некорректен");
        if (uri.Scheme == Uri.UriSchemeHttps) return (true, null);
        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            var host = uri.Host;
            if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                return (true, null);
            return (false, "ApiEndpoint должен использовать HTTPS (HTTP разрешён только для localhost)");
        }
        return (false, $"Неподдерживаемая схема: {uri.Scheme}");
    }

    public async Task<ApiResult> EnsureRegisteredAsync(CancellationToken ct = default)
    {
        var token = _settings.GetApiToken();
        if (!string.IsNullOrEmpty(token))
        {
            ApplyAuth(token);
            return new ApiResult(true, "Уже зарегистрирован");
        }

        var (epOk, epError) = ValidateEndpoint(_settings.ApiEndpoint);
        if (!epOk) return new ApiResult(false, epError ?? "Невалидный ApiEndpoint");
        if (string.IsNullOrWhiteSpace(_settings.MasterKey))
            return new ApiResult(false, "Не задан MasterKey для регистрации");

        var url = JoinUrl(_settings.ApiEndpoint, "usb-agents/register/");
        var payload = new
        {
            agent_id = _settings.AgentId ?? "",
            hostname = Environment.MachineName,
            registration_key = _settings.MasterKey ?? ""
        };
        var bodyJson = JsonSerializer.Serialize(payload);
        // НЕ ЛОГИРУЕМ полный body: он содержит master key.
        _log?.Invoke($"Регистрация: POST {url} agent_id={_settings.AgentId} hostname={Environment.MachineName}");

        try
        {
            using var content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Регистрация: HTTP {(int)resp.StatusCode} {MaskSecrets(body)}");
                return new ApiResult(false, $"HTTP {(int)resp.StatusCode}: {MaskSecrets(body)}", (int)resp.StatusCode);
            }

            var reg = JsonSerializer.Deserialize<RegistrationResponse>(body);
            if (reg == null || string.IsNullOrEmpty(reg.Token))
                return new ApiResult(false, "Ответ сервера без токена");

            _settings.SetApiToken(reg.Token);
            // После успешной регистрации master key больше не нужен — токен заменяет его.
            // Стираем чтобы не висел в settings.json plaintext (минимизация поверхности атаки).
            _settings.MasterKey = "";
            _settings.Save(_settingsPath);
            ApplyAuth(reg.Token);
            _log?.Invoke($"Зарегистрирован агент {_settings.AgentId}; master key стёрт из settings");
            return new ApiResult(true, "Регистрация успешна", (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Ошибка регистрации: {ex.Message}");
            return new ApiResult(false, ex.Message);
        }
    }

    public async Task<ApiResult> UploadAsync(IReadOnlyList<PrinterReading> readings, CancellationToken ct = default)
    {
        if (readings.Count == 0)
            return new ApiResult(true, "Нет данных для отправки");

        var (epOk, epError) = ValidateEndpoint(_settings.ApiEndpoint);
        if (!epOk) return new ApiResult(false, epError ?? "Невалидный ApiEndpoint");

        var reg = await EnsureRegisteredAsync(ct).ConfigureAwait(false);
        if (!reg.Success) return reg;

        var url = JoinUrl(_settings.ApiEndpoint, "usb-readings/");
        var payload = new UploadPayload
        {
            AgentId = _settings.AgentId ?? "",
            AgentVersion = AgentVersion,
            Readings = readings.ToList()
        };
        var bodyJson = JsonSerializer.Serialize(payload, JsonOpts);
        _log?.Invoke($"Upload: POST {url} bytes={bodyJson.Length} count={readings.Count}");

        try
        {
            using var content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                _settings.SetApiToken("");
                _settings.Save(_settingsPath);
                return new ApiResult(false, "Токен отклонён (401), требуется повторная регистрация", 401);
            }
            if (!resp.IsSuccessStatusCode)
            {
                _log?.Invoke($"Upload: HTTP {(int)resp.StatusCode} {MaskSecrets(body)}");
                return new ApiResult(false, $"HTTP {(int)resp.StatusCode}: {MaskSecrets(body)}", (int)resp.StatusCode);
            }
            return new ApiResult(true, MaskSecrets(body), (int)resp.StatusCode);
        }
        catch (TaskCanceledException)
        {
            return new ApiResult(false, "Таймаут запроса");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Ошибка отправки: {ex.Message}");
            return new ApiResult(false, ex.Message);
        }
    }

    public async Task<ApiResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var (epOk, epError) = ValidateEndpoint(_settings.ApiEndpoint);
        if (!epOk) return new ApiResult(false, epError ?? "Невалидный ApiEndpoint");

        // Дёргаем health-endpoint вместо корня /api/v1/inventory — корень
        // отвечает 404 (это префикс, не view), что путает пользователя.
        var url = JoinUrl(_settings.ApiEndpoint, "health/");
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
                return new ApiResult(true, $"Сервер ответил HTTP {code}", code);
            // Если /health/ нет (старый сервер) — фоллбэк-сообщение чтобы было понятно
            // что endpoint достижим, но версия сервера не поддерживает health-check.
            return new ApiResult(true,
                $"Сервер достижим, но {url} вернул HTTP {code} — обновите серверную часть для поддержки health-check",
                code);
        }
        catch (Exception ex)
        {
            return new ApiResult(false, ex.Message);
        }
    }

    private void ApplyAuth(string token)
    {
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private static string JoinUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = path.TrimStart('/');
        return $"{b}/{p}";
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class RegistrationResponse
    {
        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; } = "";
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";
        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    private sealed class UploadPayload
    {
        [JsonPropertyName("agent_id")]
        public string AgentId { get; set; } = "";
        [JsonPropertyName("agent_version")]
        public string AgentVersion { get; set; } = "";
        [JsonPropertyName("readings")]
        public List<PrinterReading> Readings { get; set; } = new();
    }

    public void Dispose() => _http.Dispose();
}
