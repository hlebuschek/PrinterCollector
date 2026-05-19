using Microsoft.Win32;
using PrinterCollector.Models;

namespace PrinterCollector.Services;

public sealed class PrinterReader
{
    private readonly PrinterStatusChecker _statusChecker = new();

    public sealed class ReadResult
    {
        public PrinterReading? Reading { get; init; }
        public string? Error { get; init; }
        public PrinterStatusChecker.CheckResult? Status { get; init; }
        public string? AutoSerial { get; init; }
        public string? UsbDeviceId { get; init; }
        public string? SerialDetectionWarning { get; init; }
        public bool Success => Reading != null;
    }

    public ReadResult Read(string printerName, string? overrideSerial,
                           CounterFormat counterFormat = CounterFormat.BwA4,
                           bool skipConnectionCheck = false,
                           Action<string>? log = null)
    {
        PrinterStatusChecker.CheckResult status;
        string? deviceInstanceId;

        if (skipConnectionCheck)
        {
            deviceInstanceId = ReadDeviceInstanceIdFromRegistry(printerName);
            status = new PrinterStatusChecker.CheckResult
            {
                IsOnline = true,
                DeviceInstanceId = deviceInstanceId,
                Reason = "Проверка подключения отключена (тестовый режим)"
            };
        }
        else
        {
            status = _statusChecker.Check(printerName);
            deviceInstanceId = status.DeviceInstanceId;
            if (!status.IsOnline)
            {
                return new ReadResult { Status = status, Error = status.Reason };
            }
        }

        var portName = ReadPortNameFromRegistry(printerName) ?? "";
        int? pageCount = ReadPageCountFromRegistry(printerName);
        string pageCountSource = pageCount != null ? "registry" : "";
        string? pmlError = null;

        if (pageCount == null && PmlPrinterReader.IsSupportedPort(portName))
        {
            var pml = PmlPrinterReader.TryReadPageCount(portName, log);
            if (pml.PageCount != null)
            {
                pageCount = pml.PageCount;
                pageCountSource = "pml";
            }
            else
            {
                pmlError = pml.Error;
            }
        }

        if (pageCount == null)
        {
            var msg = "В реестре не найдено значение PageCount " +
                      $@"(HKLM\...\Printers\{printerName}\PrinterDriverData\PageCount).";
            if (PmlPrinterReader.IsSupportedPort(portName))
                msg += $" PML-fallback на порту '{portName}' тоже не сработал: {pmlError}";
            else if (!string.IsNullOrEmpty(portName))
                msg += $" Порт '{portName}' не поддерживается PML-чтением (нужен DOT4_xxx или USBxxx).";
            return new ReadResult { Status = status, Error = msg };
        }

        var usb = !string.IsNullOrEmpty(deviceInstanceId)
            ? UsbSerialReader.Read(deviceInstanceId)
            : new UsbSerialReader.Result(null, null, "DeviceInstanceId не получен");

        var trimmedOverride = overrideSerial?.Trim();
        var effectiveSerial = !string.IsNullOrWhiteSpace(trimmedOverride) ? trimmedOverride : usb.Serial;
        string source;

        if (string.IsNullOrWhiteSpace(effectiveSerial))
        {
            if (skipConnectionCheck)
            {
                effectiveSerial = "TEST-" + SanitizeSerial(printerName);
                source = "test";
            }
            else
            {
                return new ReadResult
                {
                    Status = status,
                    AutoSerial = usb.Serial,
                    UsbDeviceId = usb.UsbDeviceId,
                    SerialDetectionWarning = usb.Error,
                    Error = "Не удалось определить серийный номер автоматически. Введите вручную и опросите снова."
                };
            }
        }
        else
        {
            source = !string.IsNullOrWhiteSpace(trimmedOverride) && trimmedOverride != usb.Serial
                ? "manual"
                : "device";
        }

        var reading = new PrinterReading
        {
            Timestamp = DateTime.UtcNow,
            PrinterName = printerName,
            Model = ReadDriverName(printerName) ?? printerName,
            SerialNumber = new SerialInfo { Source = source, Value = effectiveSerial! },
            Counters = BuildCounters(pageCount.Value, counterFormat),
            ConnectionVerified = !skipConnectionCheck,
            DeviceInstanceId = deviceInstanceId ?? "",
            PortName = portName,
            PageCountSource = pageCountSource
        };

        return new ReadResult
        {
            Reading = reading,
            Status = status,
            AutoSerial = usb.Serial,
            UsbDeviceId = usb.UsbDeviceId,
            SerialDetectionWarning = usb.Error
        };
    }

    private static string SanitizeSerial(string s)
    {
        var cleaned = new string(s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        return string.IsNullOrEmpty(cleaned) ? "UNKNOWN" : cleaned;
    }

    private static string? ReadDeviceInstanceIdFromRegistry(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}\PnPData");
            return key?.GetValue("DeviceInstanceId") as string;
        }
        catch { return null; }
    }

    private static Counters BuildCounters(int pageCount, CounterFormat format)
    {
        var c = new Counters { TotalPages = pageCount };
        switch (format)
        {
            case CounterFormat.BwA4: c.BwA4 = pageCount; break;
            case CounterFormat.ColorA4: c.ColorA4 = pageCount; break;
            case CounterFormat.BwA3: c.BwA3 = pageCount; break;
            case CounterFormat.ColorA3: c.ColorA3 = pageCount; break;
            case CounterFormat.Total:
            default: break;
        }
        return c;
    }

    private static string? ReadPortNameFromRegistry(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}");
            return key?.GetValue("Port") as string;
        }
        catch { return null; }
    }

    private static int? ReadPageCountFromRegistry(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}\PrinterDriverData");
            if (key == null) return null;
            var v = key.GetValue("PageCount");
            return v is int i ? i : null;
        }
        catch { return null; }
    }

    private static string? ReadDriverName(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}");
            return key?.GetValue("Printer Driver") as string;
        }
        catch { return null; }
    }
}
