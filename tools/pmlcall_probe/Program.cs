// pmlcall_probe — пробует прочитать prtMarkerLifeCount у USB-принтера, дёргая
// HPZipr12.dll напрямую, без Windows SNMP Service и без HP SNMP Proxy.
//
// После API Monitor-трассировки snmp.exe (2026-05-19, session 3) выяснено:
//   1. hpgenpml.dll грузит ОДНУ DLL — hpzipr12.dll — и резолвит из неё ВСЕ функции:
//      _OpenOsDevice@8, _CloseOsDevice@4, _PMLRegisterEx@24, _PMLUnRegisterEx@8,
//      а также PMLActualInit/DeInit/PMLGetObjectValueEx и т.д.
//      HPZidr12.dll напрямую не используется (она внутренний слой,
//      svchost Pml Driver HPZ12 → HPZipm12 → HPZidr12, не наш процесс).
//   2. snmp.exe сам НЕ делает CreateFile/DeviceIoControl на USB.
//      Весь USB-I/O делает svchost Pml Driver HPZ12. snmp.exe общается с ним
//      через 3 named mutex + shared memory.
//   3. Никаких CreateWindowEx/FindWindow/IsWindow — HWND для PMLRegisterEx
//      НЕ нужен, R8 = NULL.
//   4. Device name для OpenOsDevice неважен — hpzipr12 читает Base Name + Port
//      Number из реестра сама. Передаём короткое имя для логирования.
//
// Грабли (см. memory/project_hp1320_proxyless.md):
//   • НЕ повторять запуск без ребута, если предыдущий АВ-нулся или завис.
//   • НЕ вызывать CreateWindowExA в main thread между Init и Register.
//   • НЕ закрывать PMLI_TrapWindow руками.

using System.Runtime.InteropServices;
using System.Text;

const string ZIPR = "HPZipr12.dll";
const string KERNEL = "kernel32.dll";
const string SETUPAPI = "setupapi.dll";
const string CFGMGR = "cfgmgr32.dll";
const string SHARED_MEM_NAME = "Global\\HPZipm12.exeCommandMapPort";
const uint FILE_MAP_READ = 0x0004;

// GUID_DEVINTERFACE_USBPRINT (same as hpzipr12 uses at .rdata 0x4019e8)
Guid GUID_USBPRINT = new Guid("28d78fad-5a12-11d1-ae5b-0000f803a8c2");
const uint DIGCF_PRESENT = 0x02;
const uint DIGCF_DEVICEINTERFACE = 0x10;
const uint CR_SUCCESS = 0;

[DllImport(KERNEL, EntryPoint = "OpenFileMappingA", CharSet = CharSet.Ansi, SetLastError = true)]
static extern IntPtr OpenFileMappingA(uint desiredAccess, bool inheritHandle, [MarshalAs(UnmanagedType.LPStr)] string name);

[DllImport(KERNEL, SetLastError = true)]
static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess, uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

[DllImport(KERNEL, SetLastError = true)]
static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

[DllImport(KERNEL, SetLastError = true)]
static extern bool CloseHandle(IntPtr hObject);

[DllImport(KERNEL, SetLastError = true)]
static extern UIntPtr VirtualQuery(IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

// === setupapi / cfgmgr32 (mirror what hpzipr12!FUN_00409490 does internally) ===
[DllImport(SETUPAPI, CharSet = CharSet.Ansi, SetLastError = true, EntryPoint = "SetupDiGetClassDevsA")]
static extern IntPtr SetupDiGetClassDevsA(ref Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

[DllImport(SETUPAPI, SetLastError = true)]
static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

[DllImport(SETUPAPI, SetLastError = true)]
static extern bool SetupDiEnumDeviceInfo(IntPtr DeviceInfoSet, uint MemberIndex, ref SP_DEVINFO_DATA DeviceInfoData);

[DllImport(CFGMGR, CharSet = CharSet.Ansi, EntryPoint = "CM_Get_Device_IDA")]
static extern int CM_Get_Device_IDA(uint dnDevInst, [Out] byte[] Buffer, uint BufferLen, uint ulFlags);

[DllImport(CFGMGR, EntryPoint = "CM_Get_Device_ID_Size")]
static extern int CM_Get_Device_ID_Size(out uint pulLen, uint dnDevInst, uint ulFlags);

[DllImport(CFGMGR)]
static extern int CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

[DllImport(CFGMGR)]
static extern int CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

[DllImport(KERNEL)]
static extern IntPtr GetCurrentProcess();

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

[DllImport("advapi32.dll", SetLastError = true)]
static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass, IntPtr TokenInformation, uint TokenInformationLength, out uint ReturnLength);

// arg1 is a struct pointer (NOT a string!). Layout discovered via Ghidra of FUN_004044b0:
//   byte[0x00]   = type/count, must be > 0x1f
//   qword[0x10]  = flags
//   byte[0x14]   = string-mode flag (bit 0x10 set = Wide, clear = ANSI)
//   qword[0x18]  = pointer to device name string
// arg2 is the output handle slot.
[DllImport(ZIPR, EntryPoint = "_OpenOsDevice@8", CallingConvention = CallingConvention.StdCall)]
static extern int OpenOsDevice(IntPtr deviceInfoStruct, IntPtr outBuffer);

[DllImport(ZIPR, EntryPoint = "_CloseOsDevice@4", CallingConvention = CallingConvention.StdCall)]
static extern int CloseOsDevice(IntPtr handle);

[DllImport(ZIPR, EntryPoint = "_PMLActualInit@0", CallingConvention = CallingConvention.StdCall)]
static extern int PMLActualInit();

[DllImport(ZIPR, EntryPoint = "_PMLActualDeInit@4", CallingConvention = CallingConvention.StdCall)]
static extern int PMLActualDeInit(IntPtr unused);

// _PMLRegisterEx@24 (6 args): HANDLE ctx, ULONG zero, void* cb, USHORT magic, void* nullPtr, ULONG* outAgentId
[DllImport(ZIPR, EntryPoint = "_PMLRegisterEx@24", CallingConvention = CallingConvention.StdCall)]
static extern int PMLRegisterEx(IntPtr ctx, uint zero, IntPtr cb, ushort magic, IntPtr nullPtr, out uint outAgentId);

[DllImport(ZIPR, EntryPoint = "_PMLUnRegisterEx@8", CallingConvention = CallingConvention.StdCall)]
static extern int PMLUnRegisterEx(IntPtr ctx, uint agentId);

// _PMLGetObjectValueEx@32 (8 args):
//   HANDLE ctx, ULONG agentId, BYTE* oid, ULONG oidLen,
//   BYTE* outBuf, ULONG bufLen, void* outValuePtr, ULONG* outType
[DllImport(ZIPR, EntryPoint = "_PMLGetObjectValueEx@32", CallingConvention = CallingConvention.StdCall)]
static extern int PMLGetObjectValueEx(
    IntPtr ctx, uint agentId,
    byte[] oid, uint oidLen,
    byte[] outBuf, uint bufLen,
    IntPtr outValuePtr,
    IntPtr outType);

string deviceName = args.FirstOrDefault(a => !a.StartsWith("--")) ?? "USB003";
bool dryRun = args.Contains("--dry-run");
bool sniff = args.Contains("--sniff") || args.Contains("--sniff-only");
bool sniffOnly = args.Contains("--sniff-only");
bool noInit = args.Contains("--no-init");
bool setupDiProbe = args.Contains("--setupdi-probe");
int sniffBytes = 4096;

// prtMarkerLifeCount.1.1  =  OID 1.3.6.1.2.1.43.10.2.1.4.1.1 в PML wire-format
byte[] prtMarkerLifeCountOid = { 0x02, 0x0a, 0x02, 0x01, 0x04, 0x01, 0x01 };

Console.WriteLine($"=== pmlcall_probe (device='{deviceName}', dryRun={dryRun}, sniff={sniff}, sniffOnly={sniffOnly}) ===");

// Diagnostic: read DAT_0040d7d0 (global ctx pointer) after LoadLibrary("hpzipr12.dll")
{
    IntPtr hZipr = NativeLibrary.Load(ZIPR);
    Console.WriteLine($"=== HPZipr12 loaded at 0x{hZipr.ToInt64():x} ===");
    long ctxPtrAddr = hZipr.ToInt64() + 0xd7d0;        // DAT_0040d7d0
    long ctxPtr = Marshal.ReadInt64((IntPtr)ctxPtrAddr);
    Console.WriteLine($"  DAT_0040d7d0 (ctx-pointer) @ 0x{ctxPtrAddr:x} = 0x{ctxPtr:x}");
    if (ctxPtr != 0)
    {
        Console.WriteLine($"  ctx[0x14] (hInstance)       = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 0x14*8)):x}");
        Console.WriteLine($"  ctx[0x15] (ApiMtx handle)   = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 0x15*8)):x}");
        Console.WriteLine($"  ctx[0x16] (TermMtx handle)  = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 0x16*8)):x}");
        Console.WriteLine($"  ctx[1]    (event1?)         = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 1*8)):x}");
        Console.WriteLine($"  ctx[2]    (event2?)         = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 2*8)):x}");
        Console.WriteLine($"  ctx[0]    (handle0)         = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 0)):x}");
        Console.WriteLine($"  ctx[0x19] (cmd-buf-ptr)     = 0x{Marshal.ReadInt64((IntPtr)(ctxPtr + 0x19*8)):x}");
    }
    long errAddr = hZipr.ToInt64() + 0xd7b0;            // _DAT_0040d7b0 (last error code)
    int lastErr = Marshal.ReadInt32((IntPtr)errAddr);
    Console.WriteLine($"  _DAT_0040d7b0 (last err code) = 0x{lastErr:x}");
    Console.WriteLine();
}

if (setupDiProbe)
{
    Console.WriteLine("=== --setupdi-probe: replicate hpzipr12!FUN_00409490 USB enumeration ===");
    // Show process identity context first
    try
    {
        // TOKEN_ELEVATION = 20; TokenIntegrityLevel = 25
        if (OpenProcessToken(GetCurrentProcess(), 0x0008 /*TOKEN_QUERY*/, out IntPtr token))
        {
            try
            {
                IntPtr buf = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(buf, 0);
                    if (GetTokenInformation(token, 20 /*TokenElevation*/, buf, 4, out _))
                        Console.WriteLine($"  process.elevated = {Marshal.ReadInt32(buf) != 0}");
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(token); }
        }
        Console.WriteLine($"  process.is64bit  = {Environment.Is64BitProcess}");
        Console.WriteLine($"  process.user     = {Environment.UserDomainName}\\{Environment.UserName}");
        Console.WriteLine($"  process.pid      = {Environment.ProcessId}");
    }
    catch (Exception ex) { Console.WriteLine($"  identity probe failed: {ex.GetType().Name}: {ex.Message}"); }

    Console.WriteLine();
    Console.WriteLine($"  GUID_DEVINTERFACE_USBPRINT = {{{GUID_USBPRINT}}}");
    Console.WriteLine($"  Flags = DIGCF_PRESENT|DIGCF_DEVICEINTERFACE = 0x{(DIGCF_PRESENT|DIGCF_DEVICEINTERFACE):x}");
    IntPtr hDevInfo = SetupDiGetClassDevsA(ref GUID_USBPRINT, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
    int gle = Marshal.GetLastWin32Error();
    Console.WriteLine($"  SetupDiGetClassDevsA -> 0x{hDevInfo.ToInt64():x}  GLE={gle}");
    if (hDevInfo == new IntPtr(-1))
    {
        Console.WriteLine("  *** INVALID_HANDLE_VALUE — same failure as inside hpzipr12 ***");
        return 3;
    }

    try
    {
        Console.WriteLine();
        Console.WriteLine("  Enumerating SetupDiEnumDeviceInfo + CM_Get_Device_IDA:");
        var dinfo = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
        byte[] idBuf = new byte[512];
        for (uint i = 0; ; i++)
        {
            if (!SetupDiEnumDeviceInfo(hDevInfo, i, ref dinfo))
            {
                int e = Marshal.GetLastWin32Error();
                Console.WriteLine($"  enum stopped at i={i} GLE={e} (259=ERROR_NO_MORE_ITEMS)");
                break;
            }
            Array.Clear(idBuf);
            int cr = CM_Get_Device_IDA(dinfo.DevInst, idBuf, (uint)idBuf.Length, 0);
            string id = cr == 0 ? Encoding.ASCII.GetString(idBuf).TrimEnd('\0') : $"<CM err {cr}>";
            Console.WriteLine($"    [{i}] devInst=0x{dinfo.DevInst:x}  id='{id}'");

            // Walk children (DOT4/USBPRINT children expected per FUN_00409490)
            if (CM_Get_Child(out uint child, dinfo.DevInst, 0) == CR_SUCCESS)
            {
                uint c = child;
                int depth = 0;
                while (depth < 8 && c != 0)
                {
                    Array.Clear(idBuf);
                    int crc = CM_Get_Device_IDA(c, idBuf, (uint)idBuf.Length, 0);
                    string cid = crc == 0 ? Encoding.ASCII.GetString(idBuf).TrimEnd('\0') : $"<CM err {crc}>";
                    Console.WriteLine($"        child[{depth}] devInst=0x{c:x}  id='{cid}'");
                    if (CM_Get_Sibling(out uint sib, c, 0) != CR_SUCCESS) break;
                    c = sib;
                    depth++;
                }
            }
            if (i > 50) { Console.WriteLine("    (stop, too many)"); break; }
        }
    }
    finally { SetupDiDestroyDeviceInfoList(hDevInfo); }
    return 0;
}

if (dryRun)
{
    Console.WriteLine("--dry-run: only resolving exports, no calls.");
    IntPtr h = NativeLibrary.Load(ZIPR);
    foreach (var fn in new[] { "_OpenOsDevice@8", "_CloseOsDevice@4", "_PMLActualInit@0",
                               "_PMLActualDeInit@4", "_PMLRegisterEx@24", "_PMLUnRegisterEx@8",
                               "_PMLGetObjectValueEx@32" })
    {
        bool ok = NativeLibrary.TryGetExport(h, fn, out IntPtr addr);
        Console.WriteLine($"  {fn,-30} = {(ok ? $"0x{addr.ToInt64():x}" : "MISSING")}");
    }
    return 0;
}

if (sniffOnly)
{
    SniffSharedMemory("--sniff-only baseline (before any PML call)", sniffBytes);
    return 0;
}

if (sniff) SniffSharedMemory("snapshot #0: before PMLActualInit", sniffBytes);

if (!noInit)
{
    Console.WriteLine("=== PMLActualInit() ===");
    int rcInit;
    try { rcInit = PMLActualInit(); }
    catch (Exception ex) { Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}"); return 1; }
    Console.WriteLine($"  -> {rcInit}");
    if (rcInit != 0) { Console.WriteLine("PMLActualInit failed."); return 1; }
}
else
{
    Console.WriteLine("=== PMLActualInit() SKIPPED (--no-init), HP SNMP Proxy doesn't call it ===");
}

if (sniff) SniffSharedMemory("snapshot #1: after PMLActualInit, before OpenOsDevice", sniffBytes);

int exitCode = 2;
try
{
    Console.WriteLine($"=== OpenOsDevice(\"{deviceName}\", &ctx) ===");
    // Build device-info STRUCT exactly as CPMLInterface::OpenDevice does in hpgenpml.dll:
    //   byte[0x00]   = 0x20  (type code, > 0x1f)
    //   byte[0x10]   = 0xff  (LPT marker — 0xff bypasses LPT branch)
    //   uint[0x14]   = 3     (USB mode, bit 0x10 clear = ANSI string mode)
    //   qword[0x18]  = ANSI device-name string pointer
    //   qword[0x20]  = optional CPMLInterface* context (we set to 0)
    //   int[0x28]    = 0
    byte[] deviceInfo = new byte[64];
    var diPin = GCHandle.Alloc(deviceInfo, GCHandleType.Pinned);
    // Pin ANSI device-name string
    byte[] nameAnsi = System.Text.Encoding.ASCII.GetBytes(deviceName + "\0");
    var namePin = GCHandle.Alloc(nameAnsi, GCHandleType.Pinned);
    // Populate struct
    deviceInfo[0x00] = 0x20;                                       // type code > 0x1f
    deviceInfo[0x10] = 0xff;                                       // LPT marker — bypass LPT branch
    deviceInfo[0x14] = 0x03;                                       // USB mode, ANSI string
    deviceInfo[0x15] = 0x00;
    deviceInfo[0x16] = 0x00;
    deviceInfo[0x17] = 0x00;
    long nameAddr = namePin.AddrOfPinnedObject().ToInt64();
    BitConverter.GetBytes(nameAddr).CopyTo(deviceInfo, 0x18);      // device-name ptr

    // Output buffer (handle slot)
    byte[] outBufRaw = new byte[64];
    var outBufPin = GCHandle.Alloc(outBufRaw, GCHandleType.Pinned);

    IntPtr ctx = IntPtr.Zero;
    int rcOpen;
    try
    {
        try { rcOpen = OpenOsDevice(diPin.AddrOfPinnedObject(), outBufPin.AddrOfPinnedObject()); }
        catch (Exception ex)
        {
            Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}");
            diPin.Free(); namePin.Free(); outBufPin.Free();
            return 1;
        }
        Console.Write($"  -> rc={rcOpen}, outBuf[0..0x20]:");
        for (int i = 0; i < 0x20; i += 8) {
            long q = BitConverter.ToInt64(outBufRaw, i);
            if (q != 0) Console.Write($" [+0x{i:x2}]=0x{q:x}");
        }
        Console.WriteLine();
        // Read HPZipr12 last-error log + cfgmgr/setupapi state after OpenOsDevice
        IntPtr hZipr2 = NativeLibrary.Load(ZIPR);
        long zb = hZipr2.ToInt64();
        Console.WriteLine($"  _DAT_0040d7b0 (last err)         = 0x{Marshal.ReadInt32((IntPtr)(zb + 0xd7b0)):x}");
        Console.WriteLine($"  _DAT_0040d788 (err-flag)         = 0x{Marshal.ReadInt32((IntPtr)(zb + 0xd788)):x}");
        Console.WriteLine($"  DAT_0040d158 (setupapi HMODULE)  = 0x{Marshal.ReadInt64((IntPtr)(zb + 0xd158)):x}");
        Console.WriteLine($"  DAT_0040d160 (cfgmgr32 HMODULE)  = 0x{Marshal.ReadInt64((IntPtr)(zb + 0xd160)):x}");
        Console.WriteLine($"  DAT_0040d1f0 (SetupDi handle)    = 0x{Marshal.ReadInt64((IntPtr)(zb + 0xd1f0)):x}");
        Console.WriteLine($"  DAT_0040d1f8 (enum idx)          = 0x{Marshal.ReadInt32((IntPtr)(zb + 0xd1f8)):x}");
        Console.WriteLine($"  DAT_0040d178 (USB-dev ptr)       = 0x{Marshal.ReadInt64((IntPtr)(zb + 0xd178)):x}");
        Console.WriteLine($"  DAT_0040d180 (USB-dev field)     = 0x{Marshal.ReadInt64((IntPtr)(zb + 0xd180)):x}");
        // GUID at 0x40019e8
        Console.Write("  GUID at 0x4019e8:                = ");
        for (int i = 0; i < 16; i++) Console.Write($"{Marshal.ReadByte((IntPtr)(zb + 0x19e8 + i)):x2} ");
        Console.WriteLine();
        ctx = (IntPtr)BitConverter.ToInt64(outBufRaw, 0);
    }
    finally { /* keep pins alive until after the call returns */ }

    if (sniff) SniffSharedMemory($"snapshot #2: after OpenOsDevice(rc={rcOpen})", sniffBytes);

    if (rcOpen != 0 || ctx == IntPtr.Zero) {
        diPin.Free(); namePin.Free(); outBufPin.Free();
        return 2;
    }

    try
    {
        Console.WriteLine("=== PMLRegisterEx(ctx, 0, NULL, 0x482, NULL, &agentId) ===");
        uint agentId = 0;
        int rcReg;
        try { rcReg = PMLRegisterEx(ctx, 0, IntPtr.Zero, 0x482, IntPtr.Zero, out agentId); }
        catch (Exception ex) { Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}"); return 1; }
        Console.WriteLine($"  -> rc={rcReg}, agentId=0x{agentId:x}");
        if (rcReg != 0) return 2;

        try
        {
            Console.WriteLine("=== PMLGetObjectValueEx(prtMarkerLifeCount) ===");
            byte[] outBuf = new byte[1024];
            IntPtr outValuePtr = Marshal.AllocHGlobal(8);
            IntPtr outType = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt64(outValuePtr, 0);
                Marshal.WriteInt64(outType, 0);
                int rcGet;
                try
                {
                    rcGet = PMLGetObjectValueEx(ctx, agentId,
                        prtMarkerLifeCountOid, (uint)prtMarkerLifeCountOid.Length,
                        outBuf, (uint)outBuf.Length, outValuePtr, outType);
                }
                catch (Exception ex) { Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}"); return 1; }

                long valuePtrDeref = Marshal.ReadInt64(outValuePtr);
                long typeVal = Marshal.ReadInt64(outType);
                Console.WriteLine($"  -> rc={rcGet}, *outValuePtr=0x{valuePtrDeref:x}, *outType=0x{typeVal:x}");

                if (rcGet == 0)
                {
                    int dumpLen = 32;
                    Console.Write($"  buf[0..{dumpLen}]:");
                    for (int i = 0; i < dumpLen; i++) Console.Write($" {outBuf[i]:x2}");
                    Console.WriteLine();
                    TryDecodePmlValue(outBuf, dumpLen);
                    exitCode = 0;
                }
            }
            finally { Marshal.FreeHGlobal(outValuePtr); Marshal.FreeHGlobal(outType); }
        }
        finally
        {
            Console.WriteLine($"=== PMLUnRegisterEx(ctx, 0x{agentId:x}) ===");
            try { Console.WriteLine($"  -> {PMLUnRegisterEx(ctx, agentId)}"); }
            catch (Exception ex) { Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}"); }
        }
    }
    finally
    {
        Console.WriteLine("=== CloseOsDevice(ctx) ===");
        try { Console.WriteLine($"  -> {CloseOsDevice(ctx)}"); }
        catch (Exception ex) { Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}"); }
    }
}
finally
{
    if (!noInit)
    {
        Console.WriteLine();
        Console.WriteLine("=== PMLActualDeInit(NULL) ===");
        try { Console.WriteLine($"  -> {PMLActualDeInit(IntPtr.Zero)}"); }
        catch (Exception ex) { Console.WriteLine($"  exception: {ex.GetType().Name}: {ex.Message}"); }
    }
}

return exitCode;

// PML/SNMP value framing (from pcap on 1320):
//   80 00 00 07 02 0a 02 01 04 01 01 08 03 00 db e8
//                                 ^^ ^^ ^^^^^^^^
//                                 type=08 (INTEGER) len=03 value=0xDBE8
static void TryDecodePmlValue(byte[] buf, int len)
{
    for (int i = 0; i < len - 1; i++)
    {
        if (buf[i] == 0x08 && i + 2 < len)
        {
            int vlen = buf[i + 1];
            if (vlen >= 1 && vlen <= 8 && i + 2 + vlen <= len)
            {
                ulong v = 0;
                for (int k = 0; k < vlen; k++) v = (v << 8) | buf[i + 2 + k];
                Console.WriteLine($"  decoded INTEGER @ buf[{i}]: len={vlen}, value={v} (0x{v:x})");
                return;
            }
        }
    }
    if (len >= 4)
    {
        uint be = ((uint)buf[0] << 24) | ((uint)buf[1] << 16) | ((uint)buf[2] << 8) | buf[3];
        uint le = (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
        Console.WriteLine($"  raw first 4 bytes: BE={be} (0x{be:x}), LE={le} (0x{le:x})");
    }
}

static void SniffSharedMemory(string label, int dumpBytes)
{
    Console.WriteLine();
    Console.WriteLine($"--- SNIFF: {label} ---");
    IntPtr hMap = OpenFileMappingA(FILE_MAP_READ, false, SHARED_MEM_NAME);
    if (hMap == IntPtr.Zero)
    {
        int err = Marshal.GetLastWin32Error();
        Console.WriteLine($"  OpenFileMappingA(\"{SHARED_MEM_NAME}\") failed: GLE={err}");
        return;
    }
    try
    {
        IntPtr view = MapViewOfFile(hMap, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        if (view == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"  MapViewOfFile failed: GLE={err}");
            return;
        }
        try
        {
            int regionSize = -1;
            if (VirtualQuery(view, out var mbi, (UIntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) != UIntPtr.Zero)
            {
                regionSize = (int)Math.Min((ulong)mbi.RegionSize, int.MaxValue);
            }
            int n = Math.Min(dumpBytes, regionSize > 0 ? regionSize : dumpBytes);
            byte[] buf = new byte[n];
            Marshal.Copy(view, buf, 0, n);

            Console.WriteLine($"  view=0x{view.ToInt64():x}, regionSize=0x{regionSize:x} ({regionSize}), dumping {n} bytes");

            int firstNonZero = -1, lastNonZero = -1;
            for (int i = 0; i < n; i++) { if (buf[i] != 0) { if (firstNonZero < 0) firstNonZero = i; lastNonZero = i; } }
            Console.WriteLine($"  non-zero range: [{firstNonZero}..{lastNonZero}]");

            int hexFrom = firstNonZero < 0 ? 0 : Math.Max(0, firstNonZero & ~0xF);
            int hexTo = lastNonZero < 0 ? Math.Min(64, n) : Math.Min(n, ((lastNonZero + 16) & ~0xF));
            for (int off = hexFrom; off < hexTo; off += 16)
            {
                var sb = new StringBuilder();
                sb.Append($"  {off:x4}: ");
                for (int k = 0; k < 16; k++)
                {
                    if (off + k < n) sb.Append($"{buf[off + k]:x2} "); else sb.Append("   ");
                    if (k == 7) sb.Append(' ');
                }
                sb.Append(' ');
                for (int k = 0; k < 16 && off + k < n; k++)
                {
                    byte b = buf[off + k];
                    sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                Console.WriteLine(sb.ToString());
            }

            // ASCII strings ≥ 4 chars
            Console.WriteLine("  ASCII strings (≥4):");
            var asb = new StringBuilder();
            for (int i = 0; i <= n; i++)
            {
                if (i < n && buf[i] >= 0x20 && buf[i] < 0x7F) asb.Append((char)buf[i]);
                else { if (asb.Length >= 4) Console.WriteLine($"    @{i - asb.Length:x4}: {asb}"); asb.Clear(); }
            }
            // UTF-16LE strings ≥ 4 chars
            Console.WriteLine("  UTF-16LE strings (≥4):");
            var wsb = new StringBuilder();
            int wStart = -1;
            for (int i = 0; i + 1 < n; i += 2)
            {
                ushort ch = (ushort)(buf[i] | (buf[i + 1] << 8));
                if (ch >= 0x20 && ch < 0x7F)
                {
                    if (wsb.Length == 0) wStart = i;
                    wsb.Append((char)ch);
                }
                else { if (wsb.Length >= 4) Console.WriteLine($"    @{wStart:x4}: {wsb}"); wsb.Clear(); }
            }
        }
        finally { UnmapViewOfFile(view); }
    }
    finally { CloseHandle(hMap); }
    Console.WriteLine($"--- /SNIFF ---");
}

[StructLayout(LayoutKind.Sequential)]
struct MEMORY_BASIC_INFORMATION
{
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public ushort PartitionId;
    public UIntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

[StructLayout(LayoutKind.Sequential)]
struct SP_DEVINFO_DATA
{
    public uint cbSize;
    public Guid ClassGuid;
    public uint DevInst;
    public IntPtr Reserved;
}
