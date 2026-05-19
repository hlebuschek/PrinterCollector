using System.Diagnostics;
using System.ServiceProcess;

namespace PrinterCollector.Services;

/// <summary>
/// Установка/удаление Windows-службы PrinterCollector.
/// Заменил собой schtasks-обвязку. Конструкция:
///   - obj= "NT AUTHORITY\LocalService"     — минимальная сервисная учётка без пароля
///   - sidtype= restricted                   — write-restricted token + per-service SID
///   - sc privs                              — отзыв SeImpersonate (не нужен агенту)
///   - sc failure                            — авто-рестарт при падении
/// Грант ACL на %ProgramData%\PrinterCollector\ для NT SERVICE\PrinterCollector
/// делается отдельно через AppSettings.EnsureBaseDirAccessible.
/// </summary>
public static class ServiceInstaller
{
    public const string ServiceName = "PrinterCollector";
    public const string DisplayName = "PrinterCollector Agent";
    public const string Description = "USB Printer page-counter collector agent";

    public sealed record ScResult(int ExitCode, string Output);

    public static ScResult Install(string exePath)
    {
        // sc.exe принимает binPath с аргументами. --service сообщает Program.Main,
        // что нужно подняться как Windows Service, а не как WinForms GUI.
        var binPath = $"\"{exePath}\" --service";

        // Создание службы. start= auto-delayed — чтобы агент не конкурировал с загрузкой
        // системы и не задерживал логон, но всё равно поднялся без интерактивного юзера.
        var create = RunSc(new[]
        {
            "create", ServiceName,
            "binPath=", binPath,
            "DisplayName=", DisplayName,
            "start=", "delayed-auto",
            "obj=", "NT AUTHORITY\\LocalService"
        });
        if (create.ExitCode != 0) return create;

        // Описание для services.msc / sc qdescription.
        RunSc(new[] { "description", ServiceName, Description });

        // Per-service SID в restricted режиме: помимо LocalService в токене будет
        // NT SERVICE\PrinterCollector, и токен становится write-restricted —
        // запись разрешена только туда, где явно дан ACE на этот SID.
        RunSc(new[] { "sidtype", ServiceName, "restricted" });

        // Срезаем привилегии до минимума. SeImpersonatePrivilege агенту не нужен;
        // оставляем только то, без чего обычная служба в .NET не работает.
        RunSc(new[]
        {
            "privs", ServiceName,
            "SeChangeNotifyPrivilege/SeCreateGlobalPrivilege/SeIncreaseWorkingSetPrivilege"
        });

        // Авто-рестарт: при падении ждать минуту и попробовать снова.
        // reset= 86400 — счётчик неудач сбрасывается раз в сутки.
        RunSc(new[]
        {
            "failure", ServiceName,
            "reset=", "86400",
            "actions=", "restart/60000/restart/60000/restart/60000"
        });

        return create;
    }

    public static ScResult Uninstall()
    {
        // Перед delete обязательно stop и ждать STOPPED, иначе .exe останется залочен
        // и при следующем обновлении бинарника будет ошибка.
        Stop(timeoutSeconds: 30);
        return RunSc(new[] { "delete", ServiceName });
    }

    public static ScResult Start()
    {
        var r = RunSc(new[] { "start", ServiceName });
        if (r.ExitCode == 0) WaitForStatus(ServiceControllerStatus.Running, 30);
        return r;
    }

    public static ScResult Stop(int timeoutSeconds = 30)
    {
        if (!Exists()) return new ScResult(0, "service not installed");
        var r = RunSc(new[] { "stop", ServiceName });
        WaitForStatus(ServiceControllerStatus.Stopped, timeoutSeconds);
        return r;
    }

    public static bool Exists()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            // Доступ к Status кидает InvalidOperationException, если службы нет.
            _ = sc.Status;
            return true;
        }
        catch { return false; }
    }

    public static ServiceControllerStatus? Status()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch { return null; }
    }

    private static void WaitForStatus(ServiceControllerStatus target, int timeoutSeconds)
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.WaitForStatus(target, TimeSpan.FromSeconds(timeoutSeconds));
        }
        catch { /* best-effort; вызывающий проверит реальный статус */ }
    }

    private static ScResult RunSc(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "sc.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var combined = (stdout + stderr).Trim();
        return new ScResult(proc.ExitCode, combined);
    }
}
