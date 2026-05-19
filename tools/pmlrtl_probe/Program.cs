using System.ComponentModel;
using System.Runtime.InteropServices;

// Sanity probe: dynamically load HP PMLRTL DLL and verify the exported API
// surface that hpgenpml.dll calls. Only calls no-arg init/de-init — does NOT
// open the printer, does NOT send any PML query.

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
static extern IntPtr LoadLibraryA(string name);

[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

[DllImport("kernel32.dll", SetLastError = true)]
static extern bool FreeLibrary(IntPtr hModule);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern uint GetModuleFileName(IntPtr hModule, System.Text.StringBuilder lpFilename, uint nSize);

const string DllName = "HPZipr12.dll";
Console.WriteLine($"Loading {DllName}...");
IntPtr h = LoadLibraryA(DllName);
if (h == IntPtr.Zero)
{
    int err = Marshal.GetLastWin32Error();
    Console.Error.WriteLine($"LoadLibrary failed: {err} ({new Win32Exception(err).Message})");
    return 1;
}

var sb = new System.Text.StringBuilder(260);
GetModuleFileName(h, sb, (uint)sb.Capacity);
Console.WriteLine($"Loaded from: {sb}");
Console.WriteLine();

string[] wanted = {
    // canonical names
    "PMLActualInit", "PMLActualDeInit",
    "PMLRegister", "PMLRegisterEx", "PMLRegisterByName",
    "PMLUnRegister", "PMLUnRegisterEx",
    "PMLGetObjectValue", "PMLGetObjectValueEx", "PMLGetNextObjectValueEx",
    "PMLSetObjectValue", "PMLSetObjectValueEx",
    "PMLDeviceId",
    "PMLEnableNotification", "PMLDisableNotification",
    "PMLGetErrorDetails", "PMLMakeSetXferSyntax",
    "PMLSetDriverType", "PMLInitRtlProcs", "PMLSetConfig",
    // stdcall-decorated variants seen in the binary
    "_PMLActualInit@0", "_PMLActualDeInit@4",
    "_PMLRegisterEx@24", "_PMLRegisterByName@24",
    "_PMLGetObjectValueEx@32",
    "_PMLUnRegisterEx@8",
    "_PMLDeviceId@20",
};

int found = 0, missing = 0;
foreach (var name in wanted)
{
    IntPtr p = GetProcAddress(h, name);
    if (p != IntPtr.Zero)
    {
        Console.WriteLine($"  [+] {name,-32} @ 0x{p.ToInt64():x}");
        found++;
    }
    else
    {
        missing++;
    }
}
Console.WriteLine();
Console.WriteLine($"Found {found} / missing {missing} (showed only resolved names).");

// Optional: try the no-arg PMLActualInit if it resolved. Stays read-only.
IntPtr pInit   = GetProcAddress(h, "PMLActualInit");
IntPtr pDeInit = GetProcAddress(h, "PMLActualDeInit");
if (pInit != IntPtr.Zero && pDeInit != IntPtr.Zero)
{
    Console.WriteLine();
    Console.WriteLine("Calling PMLActualInit()...");
    var init = Marshal.GetDelegateForFunctionPointer<PMLActualInitDel>(pInit);
    int rc = init();
    Console.WriteLine($"  PMLActualInit returned {rc}");

    if (rc == 0)
    {
        // Call DeInit so we leave no global state behind.
        var deinit = Marshal.GetDelegateForFunctionPointer<PMLActualDeInitDel>(pDeInit);
        int rc2 = deinit(IntPtr.Zero);
        Console.WriteLine($"  PMLActualDeInit(NULL) returned {rc2}");
    }
}

FreeLibrary(h);
return 0;

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int PMLActualInitDel();

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int PMLActualDeInitDel(IntPtr ctx);
