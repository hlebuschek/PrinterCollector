using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

const string printerName = "hp LaserJet 1320 PCL 5";

Console.WriteLine($"=== printerdata probe on: {printerName} ===");
Console.WriteLine();

if (!Win32.OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter");

try
{
    // 1) EnumPrinterData — root namespace (this is what GetPrinterData reads)
    Console.WriteLine("--- EnumPrinterData (root namespace) ---");
    EnumRoot(hPrinter);

    Console.WriteLine();
    Console.WriteLine("--- EnumPrinterKey (subkeys for GetPrinterDataEx) ---");
    var keys = EnumPrinterKey(hPrinter, "");
    foreach (var k in keys) Console.WriteLine($"  {k}");

    foreach (var key in keys)
    {
        Console.WriteLine();
        Console.WriteLine($"--- EnumPrinterDataEx: '{key}' ---");
        EnumEx(hPrinter, key);

        var subkeys = EnumPrinterKey(hPrinter, key);
        foreach (var sk in subkeys)
        {
            var full = key + "\\" + sk;
            Console.WriteLine($"--- EnumPrinterDataEx: '{full}' ---");
            EnumEx(hPrinter, full);
        }
    }

    Console.WriteLine();
    Console.WriteLine("--- Targeted GetPrinterData probes ---");
    foreach (var name in new[] {
        "PageCount", "PrinterPageCount", "MarkerLifeCount",
        "TotalPages", "SerialNumber", "PrinterStatus",
        "PJL.PAGECOUNT", "PML.PageCount", "BiDi.PageCount",
        "Counter1", "Counter", "PrinterCounter", "DeviceStatus",
        "Status", "PML PageCount", "PrinterPMLProductId" })
    {
        TryGet(hPrinter, name);
    }
}
finally { Win32.ClosePrinter(hPrinter); }


static void EnumRoot(IntPtr hPrinter)
{
    uint index = 0;
    while (true)
    {
        var nameBuf = new StringBuilder(512);
        uint nameSize = (uint)nameBuf.Capacity;
        uint cbNeeded = 0, type = 0;
        // First pass — get sizes
        int r = Win32.EnumPrinterData(hPrinter, index, nameBuf, nameSize, out uint nameOut,
            out type, IntPtr.Zero, 0, out cbNeeded);
        if (r == 259 /* ERROR_NO_MORE_ITEMS */) break;
        if (r != 0 && r != 234 /* ERROR_MORE_DATA */)
        {
            Console.WriteLine($"  EnumPrinterData[{index}] returned win32={r} — stop");
            break;
        }
        // Buffer for value
        var data = Marshal.AllocHGlobal((int)cbNeeded);
        try
        {
            nameBuf.Length = 0;
            r = Win32.EnumPrinterData(hPrinter, index, nameBuf, (uint)nameBuf.Capacity, out nameOut,
                out type, data, cbNeeded, out cbNeeded);
            if (r != 0)
            {
                Console.WriteLine($"  EnumPrinterData[{index}] retry returned win32={r}");
                break;
            }
            string val = RenderRegValue(type, data, cbNeeded);
            Console.WriteLine($"  [{index}] {nameBuf}  ({TypeName(type)})  =  {val}");
        }
        finally { Marshal.FreeHGlobal(data); }
        index++;
        if (index > 200) { Console.WriteLine("  (cut at 200)"); break; }
    }
}

static List<string> EnumPrinterKey(IntPtr hPrinter, string keyName)
{
    var result = new List<string>();
    uint cbNeeded = 0;
    int r = Win32.EnumPrinterKey(hPrinter, keyName, IntPtr.Zero, 0, out cbNeeded);
    if (cbNeeded == 0) return result;
    var buf = Marshal.AllocHGlobal((int)cbNeeded);
    try
    {
        r = Win32.EnumPrinterKey(hPrinter, keyName, buf, cbNeeded, out cbNeeded);
        if (r != 0) return result;
        // Multi-string: pairs of WCHAR strings, terminated by extra '\0'.
        int offset = 0;
        while (offset < cbNeeded)
        {
            string s = Marshal.PtrToStringUni(IntPtr.Add(buf, offset)) ?? "";
            if (s.Length == 0) break;
            result.Add(s);
            offset += (s.Length + 1) * 2;
        }
    }
    finally { Marshal.FreeHGlobal(buf); }
    return result;
}

static void EnumEx(IntPtr hPrinter, string key)
{
    uint cbNeeded = 0, cValues = 0;
    int r = Win32.EnumPrinterDataEx(hPrinter, key, IntPtr.Zero, 0, out cbNeeded, out cValues);
    if (cbNeeded == 0) { Console.WriteLine("  (empty)"); return; }
    var buf = Marshal.AllocHGlobal((int)cbNeeded);
    try
    {
        r = Win32.EnumPrinterDataEx(hPrinter, key, buf, cbNeeded, out cbNeeded, out cValues);
        if (r != 0) { Console.WriteLine($"  ERROR win32={r}"); return; }
        int structSize = Marshal.SizeOf<Win32.PRINTER_ENUM_VALUES>();
        for (int i = 0; i < cValues; i++)
        {
            var p = IntPtr.Add(buf, i * structSize);
            var ev = Marshal.PtrToStructure<Win32.PRINTER_ENUM_VALUES>(p);
            string name = Marshal.PtrToStringUni(ev.pValueName) ?? "";
            string val = RenderRegValue(ev.dwType, ev.pData, ev.cbData);
            Console.WriteLine($"  {name}  ({TypeName(ev.dwType)}, {ev.cbData}B)  =  {val}");
        }
    }
    finally { Marshal.FreeHGlobal(buf); }
}

static void TryGet(IntPtr hPrinter, string name)
{
    uint type = 0, cbNeeded = 0;
    int r = Win32.GetPrinterData(hPrinter, name, out type, IntPtr.Zero, 0, out cbNeeded);
    if (r == 2 /* FILE_NOT_FOUND */) return; // not registered, skip
    if (r != 0 && r != 234) { Console.WriteLine($"  '{name}'  win32={r}"); return; }
    var buf = Marshal.AllocHGlobal((int)cbNeeded);
    try
    {
        r = Win32.GetPrinterData(hPrinter, name, out type, buf, cbNeeded, out cbNeeded);
        if (r != 0) { Console.WriteLine($"  '{name}'  win32={r}"); return; }
        Console.WriteLine($"  '{name}'  ({TypeName(type)}, {cbNeeded}B)  =  {RenderRegValue(type, buf, cbNeeded)}");
    }
    finally { Marshal.FreeHGlobal(buf); }
}

static string TypeName(uint t) => t switch
{
    0 => "NONE",
    1 => "SZ",
    2 => "EXPAND_SZ",
    3 => "BINARY",
    4 => "DWORD",
    7 => "MULTI_SZ",
    _ => "T=" + t
};

static string RenderRegValue(uint type, IntPtr data, uint size)
{
    if (data == IntPtr.Zero || size == 0) return "<empty>";
    var bytes = new byte[size];
    Marshal.Copy(data, bytes, 0, (int)size);
    switch (type)
    {
        case 1: case 2: case 7:
            var s = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Replace("\0", "|");
            return "\"" + s + "\"";
        case 4:
            return bytes.Length >= 4 ? BitConverter.ToInt32(bytes, 0).ToString() : "<short>";
        default:
            int show = Math.Min(64, bytes.Length);
            var hex = BitConverter.ToString(bytes, 0, show);
            return $"{hex}{(bytes.Length > show ? "..." : "")}";
    }
}

static class Win32
{
    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDataW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int EnumPrinterData(IntPtr hPrinter, uint dwIndex,
        StringBuilder pValueName, uint cbValueName, out uint pcbValueName,
        out uint pType, IntPtr pData, uint nSize, out uint pcbData);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterDataExW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int EnumPrinterDataEx(IntPtr hPrinter,
        [MarshalAs(UnmanagedType.LPWStr)] string pKeyName,
        IntPtr pEnumValues, uint cbEnumValues, out uint pcbEnumValues, out uint pnEnumValues);

    [DllImport("winspool.drv", EntryPoint = "EnumPrinterKeyW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int EnumPrinterKey(IntPtr hPrinter,
        [MarshalAs(UnmanagedType.LPWStr)] string pKeyName,
        IntPtr pSubkey, uint cbSubkey, out uint pcbSubkey);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterDataW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetPrinterData(IntPtr hPrinter,
        [MarshalAs(UnmanagedType.LPWStr)] string pValueName,
        out uint pType, IntPtr pData, uint nSize, out uint pcbNeeded);

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_ENUM_VALUES
    {
        public IntPtr pValueName;
        public uint cbValueName;
        public uint dwType;
        public IntPtr pData;
        public uint cbData;
    }
}
