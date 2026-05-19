using System.Runtime.InteropServices;
using System.Text;

namespace PrinterCollector.Services;

// Читает live-счётчик принтера через HPZipr12.dll (HP CIO 12.x PMLRTL),
// БЕЗ Windows SNMP Service и БЕЗ HP SNMP Proxy.
//
// Применимо к HP-принтерам с реестровой PrinterDriverData\PageCount = пусто
// (например, HP LaserJet 1320 — счётчик живёт в firmware, отдаётся только
// через PML over DOT4).
//
// Платформа: проверено на Windows 10 x64 (2026-05-19). HPZipr12.dll
// CIO 12.x — 64-битная сборка с x86-style decorated экспортами (@N).
//
// Зависимости (ставятся самим HP-драйвером принтера):
//   - C:\Windows\System32\HPZipr12.dll
//   - svchost service "Pml Driver HPZ12"  (IPC backend)
//   - svchost service "Net Driver HPZ12"  (для сетевых, иногда не нужен)
//
// Реверс-инженерия HPZipr12 — см. memory/project_hp1320_proxyless.md (сессия 9).
//
// Circuit breaker: после MaxConsecutiveFailures подряд PML-сбоев ветка отключается.
// Счётчик сбоев хранится в %ProgramData%\PrinterCollector\pml_fail.count и
// инкрементируется ДО нативного вызова, чтобы AV/hang в DLL тоже считались
// (после AV процесс умирает, SCM рестартует — но файл-счётчик уже инкрементирован).
// Reset: успешное чтение или ручное удаление файла.
public static class PmlPrinterReader
{
    private const string ZIPR = "HPZipr12.dll";

    // PML wire OID для prtMarkerLifeCount.1.1
    // (SNMP 1.3.6.1.2.1.43.10.2.1.4.1.1 → PML с ведущим 02 = context/agent)
    private static readonly byte[] PrtMarkerLifeCountOid =
        { 0x02, 0x0a, 0x02, 0x01, 0x04, 0x01, 0x01 };

    [DllImport(ZIPR, EntryPoint = "_OpenOsDevice@8", CallingConvention = CallingConvention.StdCall)]
    private static extern int OpenOsDevice(IntPtr deviceInfoStruct, IntPtr outBuffer);

    [DllImport(ZIPR, EntryPoint = "_CloseOsDevice@4", CallingConvention = CallingConvention.StdCall)]
    private static extern int CloseOsDevice(IntPtr handle);

    [DllImport(ZIPR, EntryPoint = "_PMLRegisterEx@24", CallingConvention = CallingConvention.StdCall)]
    private static extern int PMLRegisterEx(IntPtr ctx, uint zero, IntPtr cb, ushort magic, IntPtr nullPtr, out uint outAgentId);

    [DllImport(ZIPR, EntryPoint = "_PMLUnRegisterEx@8", CallingConvention = CallingConvention.StdCall)]
    private static extern int PMLUnRegisterEx(IntPtr ctx, uint agentId);

    [DllImport(ZIPR, EntryPoint = "_PMLGetObjectValueEx@32", CallingConvention = CallingConvention.StdCall)]
    private static extern int PMLGetObjectValueEx(
        IntPtr ctx, uint agentId,
        byte[] oid, uint oidLen,
        byte[] outBuf, uint bufLen,
        IntPtr outValuePtr,
        IntPtr outType);

    public sealed record Result(int? PageCount, string? Error);

    // Защита от параллельных нативных вызовов в одном процессе (GUI «Опросить»
    // во время тика службы — теоретически разные процессы, но если будем когда-нибудь
    // в одном процессе читать два принтера подряд, HPZipr12 имеет process-global state).
    private static readonly object _lock = new();

    private const int MaxConsecutiveFailures = 3;

    private static string FailCountPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "PrinterCollector", "pml_fail.count");

    /// <summary>
    /// Порт пригоден для PML-чтения: DOT4_NNN или USBNNN.
    /// IP-порты, LPT, PORTPROMPT — не поддерживаются.
    /// </summary>
    public static bool IsSupportedPort(string? portName)
    {
        if (string.IsNullOrEmpty(portName)) return false;
        if (portName.StartsWith("DOT4_", StringComparison.OrdinalIgnoreCase)) return true;
        if (!portName.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return false;
        if (portName.Length <= 3) return false;
        for (int i = 3; i < portName.Length; i++)
            if (!char.IsDigit(portName[i])) return false;
        return true;
    }

    public static Result TryReadPageCount(string portName, Action<string>? log = null)
    {
        if (!IsSupportedPort(portName))
            return new Result(null, $"Порт '{portName}' не поддерживается PML-чтением (нужен DOT4_xxx или USBxxx).");

        lock (_lock)
        {
            var prevFails = ReadFailCount();
            if (prevFails >= MaxConsecutiveFailures)
            {
                var msg = $"PML-фоллбек отключён circuit-breaker'ом: {prevFails} подряд сбоев. " +
                          $"Сброс — удалить файл {FailCountPath}.";
                log?.Invoke(msg);
                return new Result(null, msg);
            }
            // Инкрементируем ДО нативного вызова: если HPZipr12 сделает AV и убьёт процесс,
            // SCM рестартует, и следующий тик увидит увеличенный счётчик в файле.
            WriteFailCount(prevFails + 1);
            log?.Invoke($"PML: попытка чтения '{portName}' (предыдущих сбоев подряд: {prevFails})");

            var result = TryReadInner(portName, log);
            if (result.PageCount != null)
            {
                ResetFailCount();
                log?.Invoke($"PML: успех — PageCount={result.PageCount}, счётчик сбоев сброшен");
            }
            else
            {
                log?.Invoke($"PML: ошибка — {result.Error}");
            }
            return result;
        }
    }

    private static Result TryReadInner(string portName, Action<string>? log)
    {
        // 64-byte device-info struct (точная раскладка из реверса hpgenpml!CPMLInterface::OpenDevice):
        //   byte[0x00]   = 0x20  type code, должно быть > 0x1F
        //   byte[0x10]   = 0xff  LPT marker — 0xff bypass-ит LPT-ветку
        //   uint[0x14]   = 3     USB mode (bit 0x10 clear = ANSI string)
        //   qword[0x18]  = ANSI device-name pointer
        byte[] deviceInfo = new byte[64];
        byte[] nameAnsi = Encoding.ASCII.GetBytes(portName + "\0");
        byte[] outBufRaw = new byte[64];

        var diPin = GCHandle.Alloc(deviceInfo, GCHandleType.Pinned);
        var namePin = GCHandle.Alloc(nameAnsi, GCHandleType.Pinned);
        var outBufPin = GCHandle.Alloc(outBufRaw, GCHandleType.Pinned);
        try
        {
            deviceInfo[0x00] = 0x20;
            deviceInfo[0x10] = 0xff;
            deviceInfo[0x14] = 0x03;
            BitConverter.GetBytes(namePin.AddrOfPinnedObject().ToInt64()).CopyTo(deviceInfo, 0x18);

            int rcOpen;
            try { rcOpen = OpenOsDevice(diPin.AddrOfPinnedObject(), outBufPin.AddrOfPinnedObject()); }
            catch (DllNotFoundException) { return new Result(null, "HPZipr12.dll не найдена. Установлен ли HP-драйвер принтера?"); }
            catch (Exception ex) { return new Result(null, $"OpenOsDevice exception: {ex.GetType().Name}: {ex.Message}"); }

            log?.Invoke($"PML: OpenOsDevice rc=0x{rcOpen:X8}");
            if (rcOpen != 0)
                return new Result(null, $"OpenOsDevice('{portName}') rc=0x{rcOpen:X8}");

            IntPtr ctx = (IntPtr)BitConverter.ToInt64(outBufRaw, 0);
            if (ctx == IntPtr.Zero)
                return new Result(null, $"OpenOsDevice('{portName}') вернул rc=0, но ctx=0");

            uint agentId = 0;
            try
            {
                // magic=0x482 — наблюдаемое значение в трассе snmp.exe / HP SNMP Proxy на CIO 12.x
                // (см. memory/project_hp1320_proxyless.md, session 3). Семантика — версия PML
                // протокола / тип клиента; другие значения не пробовали.
                int rcReg = PMLRegisterEx(ctx, 0, IntPtr.Zero, 0x482, IntPtr.Zero, out agentId);
                log?.Invoke($"PML: PMLRegisterEx rc=0x{rcReg:X8}, agentId=0x{agentId:X}");
                if (rcReg != 0)
                    return new Result(null, $"PMLRegisterEx rc=0x{rcReg:X8}");

                byte[] valueBuf = new byte[1024];
                IntPtr outValuePtr = Marshal.AllocHGlobal(8);
                IntPtr outType = Marshal.AllocHGlobal(8);
                try
                {
                    Marshal.WriteInt64(outValuePtr, 0);
                    Marshal.WriteInt64(outType, 0);
                    int rcGet = PMLGetObjectValueEx(ctx, agentId,
                        PrtMarkerLifeCountOid, (uint)PrtMarkerLifeCountOid.Length,
                        valueBuf, (uint)valueBuf.Length, outValuePtr, outType);

                    int typeVal = Marshal.ReadInt32(outType);
                    log?.Invoke($"PML: PMLGetObjectValueEx rc=0x{rcGet:X8}, type=0x{typeVal:X}");
                    if (rcGet != 0)
                        return new Result(null, $"PMLGetObjectValueEx rc=0x{rcGet:X8}");

                    int? pageCount = DecodeIntegerValue(valueBuf, typeVal);
                    if (pageCount == null)
                        return new Result(null, "PMLGetObjectValueEx вернул значение, которое не получилось декодировать как INTEGER.");

                    return new Result(pageCount, null);
                }
                finally
                {
                    Marshal.FreeHGlobal(outValuePtr);
                    Marshal.FreeHGlobal(outType);
                }
            }
            finally
            {
                try { if (agentId != 0) PMLUnRegisterEx(ctx, agentId); } catch { }
                try { CloseOsDevice(ctx); } catch { }
            }
        }
        finally
        {
            diPin.Free();
            namePin.Free();
            outBufPin.Free();
        }
    }

    private static int ReadFailCount()
    {
        try
        {
            if (!File.Exists(FailCountPath)) return 0;
            return int.TryParse(File.ReadAllText(FailCountPath).Trim(), out var n) ? n : 0;
        }
        catch { return 0; }
    }

    private static void WriteFailCount(int n)
    {
        try
        {
            var dir = Path.GetDirectoryName(FailCountPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FailCountPath, n.ToString());
        }
        catch { /* best-effort: если каталог недоступен, circuit-breaker деградирует до in-process только */ }
    }

    private static void ResetFailCount()
    {
        try { if (File.Exists(FailCountPath)) File.Delete(FailCountPath); }
        catch { }
    }

    // PML возвращает либо raw INTEGER в LE (type=4, наблюдается у HP 1320 через hpzipr12),
    // либо PML-wire frame `08 <len> <BE bytes>` (type=8, как в pcap).
    // Принимаем оба варианта. buf — фиксированно 1024 байта, см. TryReadInner.
    private static int? DecodeIntegerValue(byte[] buf, int type)
    {
        if (type == 4)
            return BitConverter.ToInt32(buf, 0);

        for (int i = 0; i < Math.Min(buf.Length - 1, 16); i++)
        {
            if (buf[i] == 0x08)
            {
                int vlen = buf[i + 1];
                if (vlen >= 1 && vlen <= 4 && i + 2 + vlen <= buf.Length)
                {
                    int v = 0;
                    for (int k = 0; k < vlen; k++) v = (v << 8) | buf[i + 2 + k];
                    return v;
                }
            }
        }
        return null;
    }
}
