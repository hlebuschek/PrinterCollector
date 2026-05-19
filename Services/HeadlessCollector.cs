namespace PrinterCollector.Services;

public static class HeadlessCollector
{
    public static int Run()
    {
        var logger = new FileLogger(AppSettings.DefaultLogPath);

        try
        {
            logger.Log("=== Запуск автосбора ===");
            var settings = AppSettings.Load();
            if (string.IsNullOrWhiteSpace(settings.PrinterName))
            {
                logger.Log("ОШИБКА: в settings.json не задан PrinterName. Запустите GUI и выберите принтер.");
                return 2;
            }
            logger.Log($"Принтер: {settings.PrinterName}");
            logger.Log($"Формат счётчика: {settings.CounterFormat}");
            logger.Log($"Папка XML: {settings.OutputFolder}");

            var overrides = new OverrideStore();
            var reader = new PrinterReader();
            if (settings.SkipConnectionCheck)
                logger.Log("Тестовый режим: проверка подключения отключена");
            var result = reader.Read(
                settings.PrinterName,
                overrides.Get(settings.PrinterName),
                settings.CounterFormat,
                settings.SkipConnectionCheck,
                logger.Log);

            if (result.Status != null)
                logger.Log($"Подключение: {result.Status.Reason}");

            if (!result.Success)
            {
                logger.Log("ПРОПУСК: " + (result.Error ?? "неизвестная ошибка"));
                TryFlushQueue(settings, logger);
                return result.Status?.IsOnline == false ? 0 : 1;
            }

            var path = ReadingStorage.Save(result.Reading!, settings.OutputFolder);
            logger.Log($"OK: TotalPages={result.Reading!.Counters.TotalPages}, " +
                       $"Serial={result.Reading.SerialNumber.Value} ({result.Reading.SerialNumber.Source}) -> {path}");

            if (string.IsNullOrWhiteSpace(settings.ApiEndpoint))
            {
                logger.Log("ApiEndpoint не задан — отправка пропущена (только локальный XML)");
                return 0;
            }

            UploadOrQueue(settings, result.Reading!, logger);
            return 0;
        }
        catch (Exception ex)
        {
            logger.Log("FATAL: " + ex);
            return 3;
        }
    }

    private static void UploadOrQueue(AppSettings settings, Models.PrinterReading reading, FileLogger logger)
    {
        var queue = new OfflineQueue(log: logger.Log);
        queue.Enqueue(reading);
        logger.Log($"В очереди: {queue.Count} элементов");

        using var client = new ApiClient(settings, log: logger.Log);
        try
        {
            var flush = queue.FlushAsync(client).GetAwaiter().GetResult();
            if (flush.Error != null)
                logger.Log($"Очередь не отправлена: {flush.Error}");
            else
                logger.Log($"Отправлено: {flush.Sent}, осталось: {flush.Failed}");
        }
        catch (Exception ex)
        {
            logger.Log($"Ошибка отправки: {ex.Message}");
        }
    }

    private static void TryFlushQueue(AppSettings settings, FileLogger logger)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiEndpoint)) return;
        try
        {
            var queue = new OfflineQueue(log: logger.Log);
            if (queue.Count == 0) return;
            using var client = new ApiClient(settings, log: logger.Log);
            var flush = queue.FlushAsync(client).GetAwaiter().GetResult();
            logger.Log($"Очередь (без нового опроса): отправлено {flush.Sent}, осталось {flush.Failed}");
        }
        catch (Exception ex)
        {
            logger.Log($"Очередь: исключение {ex.Message}");
        }
    }

    public sealed class FileLogger
    {
        private readonly string _path;
        public FileLogger(string path)
        {
            _path = path;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        public void Log(string message)
        {
            try
            {
                File.AppendAllText(_path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch { /* лог упал — ничего не делаем */ }
        }
    }
}
