using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Standalone probe: read HP LaserJet 1320 prtMarkerLifeCount directly via the
// USBPRINT device interface and DOT4/PML protocol, bypassing HP SNMP Proxy
// and the Windows SNMP service. Wire bytes captured with USBPcap.

string? devicePath = null;
foreach (var a in args)
{
    if (a.StartsWith(@"\\?\")) devicePath = a;
}

if (devicePath is null)
{
    var found = Probe.EnumDevices();
    Console.WriteLine($"Found {found.Count} USBPRINT device interface(s):");
    foreach (var p in found) Console.WriteLine($"  {p}");
    devicePath = found.Find(p =>
        p.Contains("VID_03F0", StringComparison.OrdinalIgnoreCase) &&
        p.Contains("PID_1D17", StringComparison.OrdinalIgnoreCase));
    if (devicePath is null)
    {
        Console.Error.WriteLine("HP LaserJet 1320 (VID_03F0&PID_1D17) not present.");
        return 1;
    }
}

Console.WriteLine();
Console.WriteLine($"Opening: {devicePath}");

using var h = Native.CreateFile(devicePath,
    Native.GENERIC_READ | Native.GENERIC_WRITE,
    Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
    IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);

if (h.IsInvalid)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"CreateFile failed: err={err} ({new Win32Exception(err).Message})");
    if (err == 32)
        Console.Error.WriteLine("  (ERROR_SHARING_VIOLATION — stop Windows SNMP service so HP DLL releases the handle.)");
    return 2;
}
Console.WriteLine("Handle opened.");

// Replay captured DOT4 framing for prtMarkerLifeCount (SNMP 1.3.6.1.2.1.43.10.2.1.4.1.1):
byte[] credit       = { 0x01, 0x01, 0x00, 0x10, 0x01, 0x00 };
byte[] getLifeCount = { 0x00, 0x00, 0x07, 0x02, 0x0a, 0x02, 0x01, 0x04, 0x01, 0x01 };

Probe.Write(h, credit, "credit");
Probe.Write(h, getLifeCount, "GetReq prtMarkerLifeCount");

byte[] ack = Probe.Read(h, 64);
Console.WriteLine($"READ ack  ({ack.Length}): {Probe.Hex(ack)}");

byte[] data = Probe.Read(h, 64);
Console.WriteLine($"READ data ({data.Length}): {Probe.Hex(data)}");

// Expected: [80|81] 00 00 07 02 0a 02 01 04 01 01 08 <len> <BE bytes>
if (data.Length < 13 || (data[0] != 0x80 && data[0] != 0x81) || data[11] != 0x08)
{
    Console.Error.WriteLine("Response did not match expected PML INTEGER layout.");
    return 3;
}

int valLen = data[12];
if (data.Length < 13 + valLen)
{
    Console.Error.WriteLine($"Response truncated, expected {valLen} value bytes after offset 13.");
    return 4;
}
int value = 0;
for (int i = 0; i < valLen; i++) value = (value << 8) | data[13 + i];
Console.WriteLine();
Console.WriteLine($"prtMarkerLifeCount = {value}");
return 0;


static class Native
{
    public static readonly Guid GUID_DEVINTERFACE_USBPRINT =
        new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    public const uint DIGCF_PRESENT = 0x02;
    public const uint DIGCF_DEVICEINTERFACE = 0x10;

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x1;
    public const uint FILE_SHARE_WRITE = 0x2;
    public const uint OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevs(
        in Guid ClassGuid, IntPtr Enumerator, IntPtr hwndParent, uint Flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        in Guid InterfaceClassGuid, uint MemberIndex,
        ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);
}

static class Probe
{
    public static string Hex(ReadOnlySpan<byte> b) =>
        BitConverter.ToString(b.ToArray()).Replace("-", " ").ToLowerInvariant();

    public static List<string> EnumDevices()
    {
        var result = new List<string>();
        IntPtr set = Native.SetupDiGetClassDevs(
            Native.GUID_DEVINTERFACE_USBPRINT, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs");
        try
        {
            uint idx = 0;
            while (true)
            {
                var did = new Native.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = (uint)Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>()
                };
                if (!Native.SetupDiEnumDeviceInterfaces(
                        set, IntPtr.Zero, Native.GUID_DEVINTERFACE_USBPRINT, idx++, ref did))
                    break;

                Native.SetupDiGetDeviceInterfaceDetail(set, ref did, IntPtr.Zero, 0, out uint req, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal((int)req);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 6 on x86, 8 on x64.
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref did, buf, req, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail");
                    string? path = Marshal.PtrToStringUni(buf + 4);
                    if (path is not null) result.Add(path);
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return result;
    }

    public static void Write(SafeFileHandle h, byte[] data, string label)
    {
        if (!Native.WriteFile(h, data, (uint)data.Length, out uint n, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WriteFile({label})");
        Console.WriteLine($"WROTE {label} ({n}): {Hex(data)}");
    }

    public static byte[] Read(SafeFileHandle h, int max)
    {
        byte[] buf = new byte[max];
        if (!Native.ReadFile(h, buf, (uint)max, out uint n, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadFile");
        return buf.AsSpan(0, (int)n).ToArray();
    }
}
