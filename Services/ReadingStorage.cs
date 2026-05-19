using System.Text.RegularExpressions;
using System.Xml.Serialization;
using PrinterCollector.Models;

namespace PrinterCollector.Services;

public static class ReadingStorage
{
    public static string Save(PrinterReading reading, string folder)
    {
        Directory.CreateDirectory(folder);

        var safeSerial = SanitizeForFileName(reading.SerialNumber.Value);
        if (string.IsNullOrEmpty(safeSerial)) safeSerial = "unknown";

        var fileName = $"{safeSerial}_{reading.Timestamp:yyyyMMdd_HHmmss}.xml";
        var path = Path.Combine(folder, fileName);

        var serializer = new XmlSerializer(typeof(PrinterReading));
        using var fs = File.Create(path);
        serializer.Serialize(fs, reading);
        return path;
    }

    private static string SanitizeForFileName(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(s.Where(c => !invalid.Contains(c) && c != ' ').ToArray());
        return Regex.Replace(cleaned, @"[^A-Za-z0-9._-]", "_");
    }
}
