using System.Net;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;

string host = "127.0.0.1";
string community = "public";
var endpoint = new IPEndPoint(IPAddress.Loopback, 161);

// Named OIDs we care about for HP printer info via HP SNMP Proxy.
var named = new (string Label, string Oid)[]
{
    ("sysDescr",                  "1.3.6.1.2.1.1.1.0"),
    ("sysName",                   "1.3.6.1.2.1.1.5.0"),
    ("hrDeviceDescr.1",           "1.3.6.1.2.1.25.3.2.1.3.1"),
    ("prtGeneralSerialNumber",    "1.3.6.1.2.1.43.5.1.1.17.1"),
    ("prtMarkerLifeCount.1.1",    "1.3.6.1.2.1.43.10.2.1.4.1.1"),
    ("prtMarkerCounterUnit.1.1",  "1.3.6.1.2.1.43.10.2.1.3.1.1"),
    ("prtConsoleDisplayBuffer.1", "1.3.6.1.2.1.43.16.5.1.2.1.1"),
    ("hpModel",                   "1.3.6.1.4.1.11.2.3.9.1.1.7.0"),
};

Console.WriteLine($"=== SNMP probe {host}:161 community '{community}' ===");
Console.WriteLine();

foreach (var (label, oid) in named)
{
    await GetOne(label, oid);
}

Console.WriteLine();
Console.WriteLine("=== snmpwalk Printer MIB (1.3.6.1.2.1.43) ===");
await Walk(new ObjectIdentifier("1.3.6.1.2.1.43"));

async Task GetOne(string label, string oid)
{
    try
    {
        var result = await Messenger.GetAsync(
            VersionCode.V1,
            endpoint,
            new OctetString(community),
            new List<Variable> { new(new ObjectIdentifier(oid)) });
        foreach (var v in result)
        {
            Console.WriteLine($"{label,-28} {v.Id}  =  {Render(v.Data)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{label,-28} {oid}  =  ERROR: {ex.GetType().Name}: {ex.Message}");
    }
}

async Task Walk(ObjectIdentifier root)
{
    var bag = new List<Variable>();
    try
    {
        await Messenger.WalkAsync(
            VersionCode.V1,
            endpoint,
            new OctetString(community),
            root,
            bag,
            WalkMode.WithinSubtree);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"WALK ERROR: {ex.GetType().Name}: {ex.Message}");
    }
    foreach (var v in bag)
    {
        Console.WriteLine($"{v.Id}  =  {Render(v.Data)}");
    }
    Console.WriteLine($"({bag.Count} variables)");
}

static string Render(ISnmpData data)
{
    return data switch
    {
        OctetString os => $"\"{os}\"",
        _ => $"[{data.TypeCode}] {data}"
    };
}
