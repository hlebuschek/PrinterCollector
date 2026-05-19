using System.Drawing.Printing;
using Microsoft.Win32;

namespace PrinterCollector.Services;

/// <summary>
/// CLI-режимы, доступные при настройке агента через терминал (PsExec/SSH/RDP)
/// без запуска GUI. Используются в MSI CustomAction'ах и руками админом
/// на машинах из 250-ПК-парка.
/// </summary>
public static class Cli
{
    public static int ListPrinters()
    {
        var rows = EnumeratePrinters();
        if (rows.Count == 0)
        {
            Console.WriteLine("Локальных принтеров не найдено.");
            return 0;
        }

        // Колоночная ширина — по фактическим данным, но с разумным потолком.
        int nameW = Math.Min(40, Math.Max(8, rows.Max(r => r.Name.Length)));
        int portW = Math.Min(20, Math.Max(4, rows.Max(r => r.Port.Length)));

        Console.WriteLine($"{"#",-3} {"Имя".PadRight(nameW)} {"Порт".PadRight(portW)} {"Тип",-6} {"PML",-4}");
        Console.WriteLine(new string('-', 3 + 1 + nameW + 1 + portW + 1 + 6 + 1 + 4));
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            Console.WriteLine(
                $"{(i + 1),-3} {Trunc(r.Name, nameW).PadRight(nameW)} " +
                $"{Trunc(r.Port, portW).PadRight(portW)} {r.PortType,-6} {(r.PmlEligible ? "yes" : "—"),-4}");
        }
        Console.WriteLine();
        Console.WriteLine("Дальше:  PrinterCollector.exe --probe \"<имя из колонки 'Имя'>\"");
        return 0;
    }

    public static int Probe(string? printerNameArg)
    {
        var settings = AppSettings.Load();
        var printerName = !string.IsNullOrWhiteSpace(printerNameArg)
            ? printerNameArg!.Trim()
            : settings.PrinterName;

        if (string.IsNullOrWhiteSpace(printerName))
        {
            Console.Error.WriteLine("Не указан принтер. Вызовите --list-printers и передайте имя:");
            Console.Error.WriteLine("  PrinterCollector.exe --probe \"HP LaserJet 1320\"");
            return 1;
        }

        Console.WriteLine($"=== Probe: {printerName} ===");

        var overrides = new OverrideStore();
        var savedOverride = overrides.Get(printerName);
        var reader = new PrinterReader();
        var result = reader.Read(
            printerName,
            savedOverride,
            settings.CounterFormat,
            settings.SkipConnectionCheck,
            log: msg => Console.WriteLine($"  · {msg}"));

        if (result.Status != null)
            Console.WriteLine($"Статус:           {result.Status.Reason}");

        if (!string.IsNullOrEmpty(result.UsbDeviceId))
            Console.WriteLine($"USB-родитель:     {result.UsbDeviceId}");

        if (savedOverride != null)
            Console.WriteLine($"Override S/N:     {savedOverride} (из overrides.json)");

        if (!result.Success)
        {
            Console.WriteLine();
            Console.Error.WriteLine("Ошибка: " + (result.Error ?? "неизвестная"));
            if (result.AutoSerial != null)
                Console.Error.WriteLine($"USB-серийник прочитан: {result.AutoSerial} (но reading не собран — см. ошибку выше)");
            return 1;
        }

        var r = result.Reading!;
        Console.WriteLine($"DeviceInstanceId: {r.DeviceInstanceId}");
        Console.WriteLine($"Порт:             {r.PortName}");
        Console.WriteLine($"Модель:           {r.Model}");
        Console.WriteLine($"PageCount:        {r.Counters.TotalPages} (source={r.PageCountSource})");
        Console.WriteLine($"Серийник:         {r.SerialNumber.Value} (source={r.SerialNumber.Source})");

        // Пишем XML — рабочий probe = последний успешный reading в %ProgramData%\data\.
        // Это даёт админу артефакт, который можно показать пользователю/ИБ.
        // На сервер НЕ отправляется — это read-only-режим по сети.
        try
        {
            var path = ReadingStorage.Save(r, settings.OutputFolder);
            Console.WriteLine($"XML записан:      {path}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось записать XML: {ex.Message}");
            // Reading прочитан успешно — exit 0 даже если запись XML упала
            // (например, нет прав на каталог при запуске без admin).
        }

        if (r.SerialNumber.Source != "manual" && r.SerialNumber.Source != "override")
        {
            Console.WriteLine();
            Console.WriteLine("Если на корпусе принтера наклеен другой серийник (после замены");
            Console.WriteLine("платы форматера), переопределите его:");
            Console.WriteLine($"  PrinterCollector.exe --set-override PRINTER=\"{printerName}\" SERIAL=<с-наклейки>");
        }
        return 0;
    }

    public static int SetOverride(IDictionary<string, string> kv)
    {
        if (!kv.TryGetValue("PRINTER", out var printer) || string.IsNullOrWhiteSpace(printer))
        {
            Console.Error.WriteLine("--set-override требует PRINTER=<имя>");
            return 1;
        }
        if (!kv.TryGetValue("SERIAL", out var serial))
        {
            Console.Error.WriteLine("--set-override требует SERIAL=<значение> (пустая строка чтобы очистить)");
            return 1;
        }

        var store = new OverrideStore();
        if (string.IsNullOrWhiteSpace(serial))
        {
            store.Clear(printer);
            Console.WriteLine($"Override для '{printer}' удалён.");
        }
        else
        {
            store.Set(printer, serial.Trim());
            Console.WriteLine($"Override записан: '{printer}' -> '{serial.Trim()}'");
        }
        return 0;
    }

    public static int ApplyConfig(IDictionary<string, string> kv)
    {
        var settings = AppSettings.Load();
        bool changed = false;

        if (kv.TryGetValue("ENDPOINT", out var endpoint))
        {
            settings.ApiEndpoint = endpoint.Trim();
            changed = true;
        }
        if (kv.TryGetValue("MASTERKEY", out var masterKey))
        {
            settings.MasterKey = masterKey.Trim();
            changed = true;
        }
        if (kv.TryGetValue("PRINTER", out var printer))
        {
            settings.PrinterName = printer.Trim();
            changed = true;
        }
        if (kv.TryGetValue("AGENTID", out var agentId) && !string.IsNullOrWhiteSpace(agentId))
        {
            settings.AgentId = agentId.Trim();
            changed = true;
        }
        if (kv.TryGetValue("INTERVAL", out var intervalStr))
        {
            if (!int.TryParse(intervalStr, out var minutes) || minutes < 1)
            {
                Console.Error.WriteLine($"INTERVAL должен быть положительным целым числом минут, получено '{intervalStr}'");
                return 1;
            }
            settings.ScheduleIntervalMinutes = minutes;
            changed = true;
        }
        if (kv.TryGetValue("COUNTERFORMAT", out var cfStr))
        {
            if (!Enum.TryParse<CounterFormat>(cfStr, ignoreCase: true, out var cf))
            {
                Console.Error.WriteLine($"COUNTERFORMAT: ожидается одно из {string.Join("/", Enum.GetNames<CounterFormat>())}, получено '{cfStr}'");
                return 1;
            }
            settings.CounterFormat = cf;
            changed = true;
        }

        if (!changed)
        {
            Console.Error.WriteLine("Не указано ни одного KEY=VALUE для --apply-config.");
            Console.Error.WriteLine("Допустимые ключи: ENDPOINT, MASTERKEY, PRINTER, AGENTID, INTERVAL, COUNTERFORMAT.");
            return 1;
        }

        settings.Save();
        Console.WriteLine($"Настройки записаны: {AppSettings.DefaultPath}");
        // Не печатаем MasterKey и не показываем endpoint целиком (хотя последнее не секрет).
        Console.WriteLine($"  ApiEndpoint = {settings.ApiEndpoint}");
        Console.WriteLine($"  PrinterName = {settings.PrinterName}");
        Console.WriteLine($"  AgentId     = {settings.AgentId}");
        Console.WriteLine($"  Interval    = {settings.ScheduleIntervalMinutes} мин");
        Console.WriteLine($"  CounterFmt  = {settings.CounterFormat}");
        Console.WriteLine($"  MasterKey   = {(string.IsNullOrEmpty(settings.MasterKey) ? "(пусто)" : "***")}");
        return 0;
    }

    /// <summary>
    /// Разбирает KEY=VALUE-аргументы. Значение может быть в кавычках (для имён принтеров с пробелами).
    /// Дубликаты ключей: последний выигрывает. Регистронезависимые ключи.
    /// </summary>
    public static IDictionary<string, string> ParseKvArgs(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in args)
        {
            var arg = raw;
            if (arg.StartsWith("--")) continue;
            var eq = arg.IndexOf('=');
            if (eq <= 0) continue;
            var key = arg.Substring(0, eq).Trim();
            var value = arg.Substring(eq + 1).Trim();
            if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
                value = value.Substring(1, value.Length - 2);
            result[key] = value;
        }
        return result;
    }

    // ===== Внутреннее: перечисление принтеров =====

    private sealed record PrinterRow(string Name, string Port, string PortType, bool PmlEligible);

    private static List<PrinterRow> EnumeratePrinters()
    {
        var rows = new List<PrinterRow>();
        foreach (string name in PrinterSettings.InstalledPrinters)
        {
            var port = ReadPortFromRegistry(name) ?? "";
            rows.Add(new PrinterRow(name, port, ClassifyPort(port), PmlPrinterReader.IsSupportedPort(port)));
        }
        return rows;
    }

    private static string? ReadPortFromRegistry(string printerName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Print\Printers\{printerName}");
            return key?.GetValue("Port") as string;
        }
        catch { return null; }
    }

    private static string ClassifyPort(string port)
    {
        if (string.IsNullOrEmpty(port)) return "?";
        if (port.StartsWith("DOT4_", StringComparison.OrdinalIgnoreCase)) return "DOT4";
        if (port.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return "USB";
        if (port.StartsWith("IP_", StringComparison.OrdinalIgnoreCase)) return "IP";
        if (port.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase)) return "WSD";
        if (port.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) return "LPT";
        if (port.StartsWith("COM", StringComparison.OrdinalIgnoreCase)) return "COM";
        if (port.StartsWith("PORTPROMPT", StringComparison.OrdinalIgnoreCase)) return "VIRT";
        if (port.Contains("FILE", StringComparison.OrdinalIgnoreCase) ||
            port.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
            port.Contains("XPS", StringComparison.OrdinalIgnoreCase)) return "VIRT";
        return "?";
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}
