using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrinterCollector.Services;

public enum CounterFormat
{
    Total,
    BwA4,
    ColorA4,
    BwA3,
    ColorA3
}

public sealed class AppSettings
{
    public string PrinterName { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    public string ApiEndpoint { get; set; } = "";
    public string MasterKey { get; set; } = "";
    public string AgentId { get; set; } = "";

    [JsonPropertyName("ApiTokenProtected")]
    public string ApiTokenProtected { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CounterFormat CounterFormat { get; set; } = CounterFormat.BwA4;

    public int ScheduleIntervalMinutes { get; set; } = 60;

    public bool SkipConnectionCheck { get; set; } = false;

    // Все рабочие данные в ProgramData (machine-wide), чтобы schtasks под SYSTEM
    // и GUI любого юзера работали с одним и тем же settings.json и очередью.
    // Ранее использовался ApplicationData (per-user) — см. project memory о переходе.
    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PrinterCollector");

    public static string DefaultPath => Path.Combine(BaseDir, "settings.json");
    public static string DefaultOutputFolder => Path.Combine(BaseDir, "data");
    public static string DefaultQueueFolder => Path.Combine(BaseDir, "queue");
    public static string DefaultLogPath => Path.Combine(BaseDir, "collect.log");
    public static string DefaultOverridesPath => Path.Combine(BaseDir, "overrides.json");

    /// <summary>
    /// Создаёт BaseDir и (если у нас есть admin) выставляет ACL так, чтобы:
    ///  1) обычные пользователи могли читать/писать через GUI;
    ///  2) служба под LocalService с sidtype=restricted могла писать через
    ///     per-service SID NT SERVICE\PrinterCollector.
    ///
    /// Второй ACE обязателен: write-restricted token пропускает запись только туда,
    /// где явно дан ACE одному из restricted SID. Без этого служба не сможет
    /// сохранить XML или обновить очередь, даже если BUILTIN\Users:M уже стоит.
    ///
    /// Метод best-effort: если icacls вернёт ошибку (нет admin-прав) — игнорируем,
    /// ACL должен быть установлен при следующем запуске под admin (как часть
    /// установки службы из GUI).
    /// </summary>
    public static void EnsureBaseDirAccessible()
    {
        var dir = BaseDir;
        try { Directory.CreateDirectory(dir); }
        catch { return; }

        // ACE для GUI: любой локальный юзер может читать/писать через интерфейс.
        // *S-1-5-32-545 = BUILTIN\Users; (OI)(CI) — наследование на файлы и папки;
        // M = Modify (read + write + delete, без смены permissions).
        Icacls(dir, "*S-1-5-32-545:(OI)(CI)M");

        // ACE для службы: per-service SID, добавляемый Windows в токен при
        // sidtype=restricted. Имя резолвится через LSA после sc create.
        // Если служба ещё не создана — icacls вернёт ошибку, это ок: вызывающий
        // вызовет EnsureBaseDirAccessible повторно после ServiceInstaller.Install.
        Icacls(dir, "NT SERVICE\\PrinterCollector:(OI)(CI)M");
    }

    private static void Icacls(string dir, string grant)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "icacls.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add("/grant");
            psi.ArgumentList.Add(grant);
            psi.ArgumentList.Add("/T");  // применить к существующим subfolders
            psi.ArgumentList.Add("/C");  // continue on errors
            psi.ArgumentList.Add("/Q");  // quiet
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { /* best-effort */ }
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return Normalize(s);
            }

            // Backward-compat: автомиграция со старого %APPDATA%\PrinterCollector\settings.json
            // (там DPAPI был CurrentUser; здесь становится LocalMachine).
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "PrinterCollector", "settings.json");
            if (File.Exists(legacy))
            {
                var legacyJson = File.ReadAllText(legacy);
                var legacySettings = JsonSerializer.Deserialize<AppSettings>(legacyJson);
                if (legacySettings != null)
                {
                    // Если OutputFolder указывал на старый %APPDATA% — сбросим на новый
                    // ProgramData-путь, чтобы под другими юзерами/SYSTEM работала запись.
                    if (!string.IsNullOrEmpty(legacySettings.OutputFolder) &&
                        legacySettings.OutputFolder.Contains(@"\AppData\Roaming\PrinterCollector",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        legacySettings.OutputFolder = "";  // пересоздастся в Normalize
                    }
                    var migrated = Normalize(legacySettings);
                    // Перешифруем токен под LocalMachine, если он расшифровывается
                    // (только если миграция запущена тем же юзером, что регистрировал агента).
                    var legacyPlain = TryUnprotectLegacyCurrentUser(migrated.ApiTokenProtected);
                    migrated.ApiTokenProtected = legacyPlain != null
                        ? ProtectToLocalMachine(legacyPlain)
                        : "";  // токен не расшифровался → надо перерегистрироваться
                    migrated.Save(path);
                    return migrated;
                }
            }
        }
        catch { /* битый файл — стартуем с пустого */ }
        return Normalize(new AppSettings());
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        // Atomic-write: tmp + Move чтобы крах процесса не оставил повреждённый settings.json.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public string GetApiToken()
    {
        if (string.IsNullOrEmpty(ApiTokenProtected)) return "";
        try
        {
            var bytes = Convert.FromBase64String(ApiTokenProtected);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return ""; }
    }

    public void SetApiToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            ApiTokenProtected = "";
            return;
        }
        ApiTokenProtected = ProtectToLocalMachine(token);
    }

    private static string ProtectToLocalMachine(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    /// <summary>Расшифровать DPAPI-blob, зашифрованный под CurrentUser (legacy)
    /// — для миграции старых установок.</summary>
    private static string? TryUnprotectLegacyCurrentUser(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(protectedBase64);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch { return null; }
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.PrinterName ??= "";
        s.ApiEndpoint ??= "";
        s.MasterKey ??= "";
        s.ApiTokenProtected ??= "";
        // Сбрасываем legacy-путь %APPDATA%\PrinterCollector\... на новый ProgramData,
        // даже если миграция уже была проведена ранее (для случая когда OutputFolder
        // остался от старой версии настроек).
        if (!string.IsNullOrEmpty(s.OutputFolder) &&
            s.OutputFolder.Contains(@"\AppData\Roaming\PrinterCollector",
                StringComparison.OrdinalIgnoreCase))
        {
            s.OutputFolder = "";
        }
        if (string.IsNullOrWhiteSpace(s.OutputFolder))
            s.OutputFolder = DefaultOutputFolder;
        if (string.IsNullOrWhiteSpace(s.AgentId))
            s.AgentId = Environment.MachineName;
        return s;
    }
}
