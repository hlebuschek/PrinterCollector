using System.Reflection.PortableExecutable;

if (args.Length < 2) { Console.Error.WriteLine("usage: pe_lookup <dll> <rva-hex>"); return 1; }
string dll = args[0];
uint targetRva = Convert.ToUInt32(args[1].Replace("0x",""), 16);

using var fs = File.OpenRead(dll);
using var pe = new PEReader(fs);
var hdr = pe.PEHeaders;
var exportDir = hdr.PEHeader!.ExportTableDirectory;
if (exportDir.Size == 0) { Console.WriteLine("no export directory"); return 0; }

var blob = pe.GetSectionData(exportDir.RelativeVirtualAddress);
var rdr = blob.GetReader();
rdr.ReadUInt32(); rdr.ReadUInt32(); rdr.ReadUInt16(); rdr.ReadUInt16(); rdr.ReadUInt32(); rdr.ReadUInt32();
uint numFunc = rdr.ReadUInt32();
uint numName = rdr.ReadUInt32();
uint addrFuncs = rdr.ReadUInt32();
uint addrNames = rdr.ReadUInt32();
uint addrOrd = rdr.ReadUInt32();

var funcs = pe.GetSectionData((int)addrFuncs).GetReader();
var rvas = new uint[numFunc];
for (int i = 0; i < numFunc; i++) rvas[i] = funcs.ReadUInt32();

var names = pe.GetSectionData((int)addrNames).GetReader();
var nameRvas = new uint[numName];
for (int i = 0; i < numName; i++) nameRvas[i] = names.ReadUInt32();

var ords = pe.GetSectionData((int)addrOrd).GetReader();
var ordVals = new ushort[numName];
for (int i = 0; i < numName; i++) ordVals[i] = ords.ReadUInt16();

var byOrd = new Dictionary<int, string>();
for (int i = 0; i < numName; i++)
{
    var nameRdr = pe.GetSectionData((int)nameRvas[i]).GetReader();
    var sb = new System.Text.StringBuilder();
    while (true) { byte b = nameRdr.ReadByte(); if (b == 0) break; sb.Append((char)b); }
    byOrd[ordVals[i]] = sb.ToString();
}

(string name, uint rva, long delta)[] near = new (string, uint, long)[5];
for (int k = 0; k < 5; k++) near[k] = ("", 0, long.MaxValue);

for (int i = 0; i < numFunc; i++)
{
    if (rvas[i] == 0) continue;
    long d = (long)targetRva - rvas[i];
    if (d < 0) continue;
    string name = byOrd.TryGetValue(i, out var n) ? n : $"<ord{i}>";
    for (int k = 0; k < 5; k++)
    {
        if (d < near[k].delta) { for (int m = 4; m > k; m--) near[m] = near[m-1]; near[k] = (name, rvas[i], d); break; }
    }
}

Console.WriteLine($"target RVA: 0x{targetRva:x}  in  {Path.GetFileName(dll)}");
foreach (var n in near) { if (n.delta == long.MaxValue) continue;
    Console.WriteLine($"  {n.name,-50} RVA 0x{n.rva:x8}  +0x{n.delta:x}"); }
return 0;
