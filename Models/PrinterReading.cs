using System.Text.Json.Serialization;

namespace PrinterCollector.Models;

public class PrinterReading
{
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("printer_name")]
    public string PrinterName { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("serial_number")]
    public SerialInfo SerialNumber { get; set; } = new();

    [JsonPropertyName("counters")]
    public Counters Counters { get; set; } = new();

    [JsonPropertyName("connection_verified")]
    public bool ConnectionVerified { get; set; }

    [JsonPropertyName("device_instance_id")]
    public string DeviceInstanceId { get; set; } = "";

    [JsonPropertyName("port_name")]
    public string PortName { get; set; } = "";

    [JsonPropertyName("page_count_source")]
    public string PageCountSource { get; set; } = "";
}

public class SerialInfo
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "manual";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public class Counters
{
    [JsonPropertyName("total_pages")]
    public int? TotalPages { get; set; }

    [JsonPropertyName("bw_a4")]
    public int? BwA4 { get; set; }

    [JsonPropertyName("color_a4")]
    public int? ColorA4 { get; set; }

    [JsonPropertyName("bw_a3")]
    public int? BwA3 { get; set; }

    [JsonPropertyName("color_a3")]
    public int? ColorA3 { get; set; }
}
