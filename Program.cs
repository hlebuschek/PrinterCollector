using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PrinterCollector.Services;

namespace PrinterCollector;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --service: запуск под SCM в качестве Windows-службы (LocalService с restricted SID).
        if (HasFlag(args, "--service"))
        {
            RunAsService(args);
            return 0;
        }

        // --collect: разовый прогон сборщика для ручной отладки. Не используется службой,
        // оставлен для запуска из консоли, чтобы воспроизводить поведение тика отдельно.
        if (HasFlag(args, "--collect"))
            return HeadlessCollector.Run();

        // CLI-режимы для setup-времени (MSI CustomActions и ручной запуск админом):
        //   --list-printers                 нумерованный список локальных принтеров
        //   --probe "<имя>"                 один опрос без отправки на сервер, с записью XML
        //   --set-override PRINTER=.. SERIAL=..      ручной серийник в overrides.json
        //   --apply-config ENDPOINT=.. MASTERKEY=.. PRINTER=.. [INTERVAL=..] [COUNTERFORMAT=..]
        if (HasFlag(args, "--list-printers"))
            return Cli.ListPrinters();

        if (HasFlag(args, "--probe"))
        {
            // Первый non-flag-аргумент после --probe — имя принтера; необязательно
            // (если пусто, возьмём из settings.json).
            var probeName = args
                .SkipWhile(a => !string.Equals(a, "--probe", StringComparison.OrdinalIgnoreCase))
                .Skip(1)
                .FirstOrDefault(a => !a.StartsWith("--"));
            return Cli.Probe(probeName);
        }

        if (HasFlag(args, "--set-override"))
            return Cli.SetOverride(Cli.ParseKvArgs(args));

        if (HasFlag(args, "--apply-config"))
            return Cli.ApplyConfig(Cli.ParseKvArgs(args));

        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
        return 0;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static void RunAsService(string[] args)
    {
        var builder = Host.CreateDefaultBuilder(args)
            .UseWindowsService(o => o.ServiceName = ServiceInstaller.ServiceName)
            .ConfigureLogging(logging =>
            {
                logging.AddEventLog(new EventLogSettings
                {
                    SourceName = ServiceInstaller.ServiceName,
                    LogName = "Application"
                });
            })
            .ConfigureServices(services =>
            {
                services.AddHostedService<CollectorWorker>();
            });

        builder.Build().Run();
    }
}
