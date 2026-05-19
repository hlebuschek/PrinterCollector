using System.Management;
using Microsoft.Win32;

namespace PrinterCollector.Services;

public sealed class PrinterStatusChecker
{
    public sealed class CheckResult
    {
        public bool IsOnline { get; init; }
        public string? DeviceInstanceId { get; init; }
        public string? PnpStatus { get; init; }
        public uint? ConfigManagerErrorCode { get; init; }
        public bool WorkOffline { get; init; }
        public string Reason { get; init; } = "";
    }

    public CheckResult Check(string printerName)
    {
        var deviceInstanceId = ReadDeviceInstanceId(printerName);
        var workOffline = ReadWorkOffline(printerName);

        if (workOffline == true)
        {
            return new CheckResult
            {
                IsOnline = false,
                DeviceInstanceId = deviceInstanceId,
                WorkOffline = true,
                Reason = "Принтер помечен как «Использовать в автономном режиме» (WorkOffline=true)."
            };
        }

        if (string.IsNullOrEmpty(deviceInstanceId))
        {
            return new CheckResult
            {
                IsOnline = false,
                Reason = "Не удалось прочитать DeviceInstanceId из реестра принтера. " +
                         "Возможно это сетевой принтер — для него отдельная логика проверки пока не реализована."
            };
        }

        var pnp = QueryPnpEntity(deviceInstanceId);
        if (pnp == null)
        {
            return new CheckResult
            {
                IsOnline = false,
                DeviceInstanceId = deviceInstanceId,
                WorkOffline = false,
                Reason = "USB-устройство не найдено в системе (Win32_PnPEntity). " +
                         "Принтер выключен или физически отключён."
            };
        }

        var statusOk = string.Equals(pnp.Status, "OK", StringComparison.OrdinalIgnoreCase);
        var cmErrorOk = !pnp.ConfigManagerErrorCode.HasValue || pnp.ConfigManagerErrorCode.Value == 0;

        if (!statusOk || !cmErrorOk)
        {
            return new CheckResult
            {
                IsOnline = false,
                DeviceInstanceId = deviceInstanceId,
                PnpStatus = pnp.Status,
                ConfigManagerErrorCode = pnp.ConfigManagerErrorCode,
                Reason = $"USB-устройство есть, но Status='{pnp.Status}', " +
                         $"ConfigManagerErrorCode={pnp.ConfigManagerErrorCode?.ToString() ?? "?"} — устройство неисправно."
            };
        }

        return new CheckResult
        {
            IsOnline = true,
            DeviceInstanceId = deviceInstanceId,
            PnpStatus = pnp.Status,
            ConfigManagerErrorCode = pnp.ConfigManagerErrorCode,
            WorkOffline = false,
            Reason = "Принтер физически подключён и распознан системой."
        };
    }

    private static string? ReadDeviceInstanceId(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}\PnPData");
            return key?.GetValue("DeviceInstanceId") as string;
        }
        catch { return null; }
    }

    private static bool? ReadWorkOffline(string printerName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT WorkOffline FROM Win32_Printer WHERE Name = '{EscapeWql(printerName)}'");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var v = mo["WorkOffline"];
                    return v is bool b ? b : (bool?)null;
                }
            }
            return null;
        }
        catch { return null; }
    }

    private sealed record PnpInfo(string? Status, uint? ConfigManagerErrorCode);

    private static PnpInfo? QueryPnpEntity(string deviceInstanceId)
    {
        try
        {
            var query = $"SELECT Status, ConfigManagerErrorCode FROM Win32_PnPEntity " +
                        $"WHERE DeviceID = '{EscapeWql(deviceInstanceId)}'";
            using var searcher = new ManagementObjectSearcher(query);
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    return new PnpInfo(
                        mo["Status"]?.ToString(),
                        mo["ConfigManagerErrorCode"] is uint code ? code : (uint?)null);
                }
            }
            return null;
        }
        catch { return null; }
    }

    private static string EscapeWql(string s) => s.Replace(@"\", @"\\").Replace("'", @"\'");
}
