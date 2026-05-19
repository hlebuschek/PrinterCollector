using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace PrinterCollector.Services;

public static class UsbSerialReader
{
    private const int CR_SUCCESS = 0;
    private const int MAX_DEVICE_ID_LEN = 200;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint dnDevInst, [Out] StringBuilder Buffer, int BufferLen, uint ulFlags);

    private static readonly Regex UsbTopLevelRegex = new(
        @"^USB\\VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}\\([^\\]+)$",
        RegexOptions.Compiled);

    public sealed record Result(string? Serial, string? UsbDeviceId, string? Error);

    public static Result Read(string startDeviceInstanceId)
    {
        if (string.IsNullOrWhiteSpace(startDeviceInstanceId))
            return new Result(null, null, "Пустой DeviceInstanceId");

        if (CM_Locate_DevNodeW(out var dn, startDeviceInstanceId, 0) != CR_SUCCESS)
            return new Result(null, null, "CM_Locate_DevNode не нашёл устройство в системе");

        for (int hop = 0; hop < 10; hop++)
        {
            if (CM_Get_Parent(out var parent, dn, 0) != CR_SUCCESS)
                return new Result(null, null, $"Дошли до корня дерева (hop={hop}), USB-устройство с серийником не найдено");

            var sb = new StringBuilder(MAX_DEVICE_ID_LEN);
            if (CM_Get_Device_IDW(parent, sb, sb.Capacity, 0) != CR_SUCCESS)
            {
                dn = parent;
                continue;
            }

            var id = sb.ToString();
            var match = UsbTopLevelRegex.Match(id);
            if (match.Success)
            {
                var raw = match.Groups[1].Value;
                if (raw.Contains('&'))
                    return new Result(null, id, "USB-устройство найдено, но iSerialNumber отсутствует — Windows сгенерировал суррогатный ID");
                var trimmed = raw.TrimStart('0');
                if (trimmed.Length == 0) trimmed = raw;
                return new Result(trimmed, id, null);
            }

            dn = parent;
        }

        return new Result(null, null, "Превышен лимит обхода дерева");
    }
}
