using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

const string printerName = "hp LaserJet 1320 PCL 5";

const string UEL = "\x1B%-12345X";

string job =
    UEL +
    "@PJL COMMENT PrinterCollector probe\r\n" +
    "@PJL INFO PAGECOUNT\r\n" +
    "@PJL INFO PRODINFO\r\n" +
    "@PJL INFO ID\r\n" +
    "@PJL INFO STATUS\r\n" +
    "@PJL ECHO PJLPROBE_DONE\r\n" +
    UEL;

Console.WriteLine($"Opening printer: {printerName}");
if (!Win32.OpenPrinter(printerName, out var hPrinter, IntPtr.Zero))
    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter");

try
{
    var di = new Win32.DOC_INFO_1
    {
        pDocName = "PrinterCollector PJL probe",
        pOutputFile = null,
        pDataType = "RAW"
    };
    int jobId = Win32.StartDocPrinter(hPrinter, 1, ref di);
    if (jobId == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter");
    Console.WriteLine($"StartDocPrinter OK, jobId={jobId}");
    try
    {
        if (!Win32.StartPagePrinter(hPrinter))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter");
        try
        {
            var data = Encoding.ASCII.GetBytes(job);
            if (!Win32.WritePrinter(hPrinter, data, data.Length, out int written))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter");
            Console.WriteLine($"WritePrinter wrote {written}/{data.Length} bytes");

            var sb = new StringBuilder();
            var buf = new byte[8192];
            var start = DateTime.UtcNow;
            int idleLoops = 0;
            while ((DateTime.UtcNow - start).TotalSeconds < 15)
            {
                if (Win32.ReadPrinter(hPrinter, buf, buf.Length, out int read) && read > 0)
                {
                    sb.Append(Encoding.ASCII.GetString(buf, 0, read));
                    Console.WriteLine($"  +{read} bytes  ({sb.Length} total)");
                    idleLoops = 0;
                    if (sb.ToString().Contains("PJLPROBE_DONE")) break;
                }
                else
                {
                    idleLoops++;
                    if (idleLoops > 50) break;
                    Thread.Sleep(200);
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== RAW RESPONSE ===");
            Console.WriteLine(sb.ToString());
            Console.WriteLine("=== END ===");
            Console.WriteLine($"bytes received: {sb.Length}");
        }
        finally { Win32.EndPagePrinter(hPrinter); }
    }
    finally { Win32.EndDocPrinter(hPrinter); }
}
finally { Win32.ClosePrinter(hPrinter); }

static class Win32
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DOC_INFO_1
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string pDataType;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOC_INFO_1 di);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);

    [DllImport("winspool.drv", SetLastError = true)]
    public static extern bool ReadPrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwRead);
}
