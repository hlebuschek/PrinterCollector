using System.Drawing.Printing;
using System.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PrinterCollector.Services;

/// <summary>
/// Долгоживущий BackgroundService, который заменяет schtasks-задачу.
/// На старте: jitter 0..30 сек (размазать burst при MSI-rollout 250 ПК),
/// auto-pick принтера, явная регистрация (стирает MasterKey даже если первый тик
/// не дал readings), затем один тик сразу и далее PeriodicTimer.
/// Интервал берётся при старте; смена интервала требует перезапуска службы.
/// </summary>
public sealed class CollectorWorker : BackgroundService
{
    private readonly ILogger<CollectorWorker> _logger;

    // Максимум jitter перед первой регистрацией/тиком. 30 сек на 250 ПК даёт
    // ~8 регистраций/сек на сервер при синхронном GPO-rollout — сервер справится.
    private static readonly TimeSpan StartupJitterMax = TimeSpan.FromSeconds(30);

    public CollectorWorker(ILogger<CollectorWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = AppSettings.Load();
        var minutes = Math.Max(1, settings.ScheduleIntervalMinutes);
        var interval = TimeSpan.FromMinutes(minutes);
        _logger.LogInformation("CollectorWorker запущен, интервал = {Minutes} мин", minutes);

        // Jitter: при массовом GPO-rollout все ПК поднимают службу одновременно,
        // без jitter сервер получит burst /register/ и /usb-readings/ в одну секунду.
        var jitterMs = Random.Shared.Next(0, (int)StartupJitterMax.TotalMilliseconds);
        _logger.LogInformation("Стартовый jitter: {Ms} мс", jitterMs);
        try { await Task.Delay(jitterMs, stoppingToken); }
        catch (OperationCanceledException) { return; }

        // Auto-pick: если PrinterName в settings.json пустой (например MSI поставили без
        // PRINTER=..), пробуем выбрать единственный локальный принтер. Если их 0 или >1 —
        // не угадываем, логируем и не тикаем до ручной правки.
        TryAutoPickPrinter(ref settings);

        // Явная регистрация ДО первого тика: стирает MasterKey даже если первый тик
        // не даст reading (принтер offline в момент старта). Раньше регистрация
        // триггерилась только из UploadAsync, и MasterKey мог висеть в settings часами.
        await TryRegisterAsync(settings, stoppingToken);

        RunOneTick();

        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RunOneTick();
        }
        catch (OperationCanceledException)
        {
            // нормальная остановка службы
        }

        _logger.LogInformation("CollectorWorker остановлен");
    }

    private void TryAutoPickPrinter(ref AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.PrinterName)) return;

        var localPrinters = EnumerateLocalPrinters();
        if (localPrinters.Count == 1)
        {
            settings.PrinterName = localPrinters[0];
            settings.Save();
            _logger.LogInformation("Auto-pick: выбран единственный локальный принтер '{Name}'", settings.PrinterName);
        }
        else if (localPrinters.Count == 0)
        {
            _logger.LogWarning("PrinterName в settings.json не задан и локальных принтеров не найдено. " +
                               "Тики будут пропускаться до правки settings.json или вызова --apply-config PRINTER=...");
        }
        else
        {
            _logger.LogWarning("PrinterName не задан, найдено {Count} локальных принтеров: {List}. " +
                               "Auto-pick не делаем — задайте PRINTER явно через --apply-config.",
                               localPrinters.Count, string.Join(", ", localPrinters));
        }
    }

    /// <summary>
    /// Возвращает только локальные не-виртуальные принтеры. Виртуальные (PDF/XPS/FILE)
    /// отсеиваем, чтобы auto-pick не выбрал «Microsoft Print to PDF».
    /// </summary>
    private static List<string> EnumerateLocalPrinters()
    {
        var result = new List<string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, Local, Network FROM Win32_Printer WHERE Local=TRUE AND Network=FALSE");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["Name"]?.ToString();
                    if (string.IsNullOrEmpty(name)) continue;
                    if (IsVirtual(name)) continue;
                    result.Add(name);
                }
            }
        }
        catch
        {
            // Fallback на System.Drawing.Printing если WMI недоступен.
            foreach (string p in PrinterSettings.InstalledPrinters)
                if (!IsVirtual(p)) result.Add(p);
        }
        return result;
    }

    private static bool IsVirtual(string name) =>
        name.Contains("PDF", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("XPS", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("OneNote", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Fax", StringComparison.OrdinalIgnoreCase);

    private async Task TryRegisterAsync(AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiEndpoint))
        {
            _logger.LogInformation("ApiEndpoint не задан — регистрацию пропускаем");
            return;
        }
        try
        {
            using var client = new ApiClient(settings, log: m => _logger.LogInformation("{Msg}", m));
            var r = await client.EnsureRegisteredAsync(ct);
            if (r.Success)
                _logger.LogInformation("Регистрация: {Message}", r.Message);
            else
                _logger.LogWarning("Регистрация не удалась: {Message}. Будет повторено при первой отправке.", r.Message);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Регистрация: исключение");
        }
    }

    private void RunOneTick()
    {
        try
        {
            var code = HeadlessCollector.Run();
            if (code != 0)
                _logger.LogWarning("HeadlessCollector завершился с кодом {Code}", code);
        }
        catch (Exception ex)
        {
            // Любая необработанная ошибка не должна положить службу — таймер продолжит тикать.
            _logger.LogError(ex, "Тик сборщика упал с исключением");
        }
    }
}
