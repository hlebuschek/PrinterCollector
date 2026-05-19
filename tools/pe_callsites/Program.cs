// pe_callsites — поиск call sites в PE (x64).
//
// Режимы:
//   pe_callsites <dll>                              — листинг всех IAT-импортов
//   pe_callsites <dll> <Imp1> [Imp2 ...]            — call/jmp [IAT] для каждого статического импорта
//   pe_callsites <dll> --via-string <Name> [--via-string ...]
//                                                   — рантайм-резолв через GetProcAddress("Name")
//                                                     + все call'ы через сохранённый глобал-указатель

using System.Reflection.PortableExecutable;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;

if (args.Length < 1) { Usage(); return 1; }

string dllPath = args[0];
List<string> viaStrings = new();
List<string> wantedImports = new();
List<long> storeDisps = new();
int contextInsns = 30;

for (int ai = 1; ai < args.Length; ai++)
{
    if (args[ai] == "--via-string" && ai + 1 < args.Length) viaStrings.Add(args[++ai]);
    else if (args[ai] == "--store-disp" && ai + 1 < args.Length)
    {
        string s = args[++ai];
        long v = s.StartsWith("0x") ? Convert.ToInt64(s.Substring(2), 16) : long.Parse(s);
        storeDisps.Add(v);
    }
    else if (args[ai] == "--context" && ai + 1 < args.Length) contextInsns = int.Parse(args[++ai]);
    else wantedImports.Add(args[ai]);
}

byte[] bytes = File.ReadAllBytes(dllPath);
using var stream = new MemoryStream(bytes);
using var pe = new PEReader(stream);
var hdrs = pe.PEHeaders;
ulong imageBase = hdrs.PEHeader!.ImageBase;
bool is64 = hdrs.PEHeader.Magic == PEMagic.PE32Plus;

Console.WriteLine($"Image: {dllPath}");
Console.WriteLine($"Base : 0x{imageBase:x}");
Console.WriteLine($"Arch : {hdrs.CoffHeader.Machine}");
Console.WriteLine();

if (!is64)
{
    Console.Error.WriteLine("This tool is x64-only.");
    return 1;
}

int RvaToOffset(uint rva)
{
    foreach (var s in hdrs.SectionHeaders)
    {
        if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
            return (int)(rva - s.VirtualAddress + s.PointerToRawData);
    }
    return -1;
}

string ReadCString(byte[] b, int off)
{
    int end = off;
    while (end < b.Length && b[end] != 0) end++;
    return System.Text.Encoding.ASCII.GetString(b, off, end - off);
}

// --- Walk imports: VA -> "Dll!Func"; also short-name alias ---
Dictionary<ulong, string> iatVaToName = new();
Dictionary<string, ulong> nameToIatVa = new();
{
    var importDir = hdrs.PEHeader.ImportTableDirectory;
    int impOff = RvaToOffset((uint)importDir.RelativeVirtualAddress);
    if (impOff < 0) { Console.Error.WriteLine("no import directory"); return 2; }
    const int thunkSize = 8;
    const ulong topBit = 0x8000_0000_0000_0000UL;
    int p = impOff;
    while (true)
    {
        uint oft = BitConverter.ToUInt32(bytes, p);
        uint nameRva = BitConverter.ToUInt32(bytes, p + 12);
        uint ft = BitConverter.ToUInt32(bytes, p + 16);
        if (oft == 0 && nameRva == 0 && ft == 0) break;
        string dll = ReadCString(bytes, RvaToOffset(nameRva));
        uint thunkRva = oft != 0 ? oft : ft;
        int thunkOff = RvaToOffset(thunkRva);
        uint iatRva = ft;
        int iatIdx = 0;
        while (true)
        {
            ulong entry = BitConverter.ToUInt64(bytes, thunkOff);
            if (entry == 0) break;
            if ((entry & topBit) == 0)
            {
                uint hintRva = (uint)(entry & 0x7FFFFFFF);
                int hintOff = RvaToOffset(hintRva);
                string fname = ReadCString(bytes, hintOff + 2);
                ulong iatVa = imageBase + iatRva + (ulong)(iatIdx * thunkSize);
                string q = dll + "!" + fname;
                iatVaToName[iatVa] = q;
                nameToIatVa[q] = iatVa;
                if (!nameToIatVa.ContainsKey(fname)) nameToIatVa[fname] = iatVa;
            }
            thunkOff += thunkSize;
            iatIdx++;
        }
        p += 20;
    }
}
Console.WriteLine($"Total imports: {iatVaToName.Count}");

if (wantedImports.Count == 0 && viaStrings.Count == 0 && storeDisps.Count == 0)
{
    foreach (var kv in iatVaToName.OrderBy(k => k.Key))
        Console.WriteLine($"  0x{kv.Key:x}  {kv.Value}");
    return 0;
}

// --- store-disp mode ---
if (storeDisps.Count > 0)
{
    var insnsForStore = DecodeAllExecutable();
    Console.WriteLine($"Decoded {insnsForStore.Count} instructions for store-disp scan.");
    foreach (long disp in storeDisps)
    {
        Console.WriteLine();
        Console.WriteLine($"########## store-disp 0x{disp:x} ##########");
        ScanStoreDisp(disp, insnsForStore);
    }
}

// --- Static imports (legacy mode) ---
if (wantedImports.Count > 0)
{
    HashSet<ulong> targets = new();
    Dictionary<ulong, string> labels = new();
    foreach (var f in wantedImports)
    {
        bool found = false;
        foreach (var kv in iatVaToName)
        {
            if (kv.Value == f || kv.Value.EndsWith("!" + f, StringComparison.Ordinal))
            {
                targets.Add(kv.Key);
                labels[kv.Key] = kv.Value;
                found = true;
            }
        }
        if (!found) Console.Error.WriteLine($"  ! import not found: {f}");
    }
    if (targets.Count > 0) ScanByteCallSites(targets, labels);
}

// --- via-string mode ---
if (viaStrings.Count > 0)
{
    if (!nameToIatVa.TryGetValue("GetProcAddress", out ulong gpaIatVa))
    {
        Console.Error.WriteLine("GetProcAddress not imported by this DLL. --via-string requires it.");
        return 4;
    }
    Console.WriteLine($"GetProcAddress IAT VA = 0x{gpaIatVa:x}");

    var insns = DecodeAllExecutable();
    Console.WriteLine($"Decoded {insns.Count} instructions across executable sections.");
    var insnIdxByIp = new Dictionary<ulong, int>(insns.Count);
    for (int i = 0; i < insns.Count; i++) insnIdxByIp[insns[i].IP] = i;

    foreach (var fn in viaStrings)
    {
        Console.WriteLine();
        Console.WriteLine($"########## via-string: {fn} ##########");
        RunViaString(fn, gpaIatVa, insns);
    }
}

return 0;

// ---------------- helpers ----------------

List<Instruction> DecodeAllExecutable()
{
    var list = new List<Instruction>(1 << 16);
    foreach (var s in hdrs.SectionHeaders)
    {
        if ((s.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0) continue;
        int sStart = s.PointerToRawData;
        int sLen = (int)Math.Min(s.SizeOfRawData, (uint)(bytes.Length - sStart));
        if (sLen <= 0) continue;
        ulong sva = imageBase + (uint)s.VirtualAddress;
        var reader = new ByteArrayCodeReader(bytes, sStart, sLen);
        var decoder = Decoder.Create(64, reader);
        decoder.IP = sva;
        ulong endVa = sva + (ulong)sLen;
        while (decoder.IP < endVa)
        {
            var ins = decoder.Decode();
            list.Add(ins);
        }
    }
    return list;
}

void RunViaString(string funcName, ulong gpaIatVa, List<Instruction> insns)
{
    // 1. Locate funcName\0 in any section.
    byte[] needle = new byte[funcName.Length + 1];
    System.Text.Encoding.ASCII.GetBytes(funcName, 0, funcName.Length, needle, 0); // last byte = 0
    List<ulong> stringVas = new();
    foreach (var s in hdrs.SectionHeaders)
    {
        int sStart = s.PointerToRawData;
        int sLen = (int)Math.Min(s.SizeOfRawData, (uint)(bytes.Length - sStart));
        if (sLen <= 0) continue;
        ReadOnlySpan<byte> hay = bytes.AsSpan(sStart, sLen);
        int from = 0;
        while (true)
        {
            int idx = hay.Slice(from).IndexOf(needle.AsSpan());
            if (idx < 0) break;
            ulong va = imageBase + (uint)s.VirtualAddress + (uint)(from + idx);
            stringVas.Add(va);
            from += idx + needle.Length;
        }
    }
    if (stringVas.Count == 0)
    {
        Console.WriteLine($"  string \"{funcName}\" not found in any section.");
        return;
    }
    foreach (var v in stringVas)
        Console.WriteLine($"  string @ VA 0x{v:x}");
    var stringVaSet = new HashSet<ulong>(stringVas);

    // 2. Find lea r64, [rip+disp] -> any of those VAs.
    List<int> leaIdx = new();
    for (int i = 0; i < insns.Count; i++)
    {
        var ins = insns[i];
        if (ins.Mnemonic != Mnemonic.Lea) continue;
        if (!ins.IsIPRelativeMemoryOperand) continue;
        if (stringVaSet.Contains(ins.IPRelativeMemoryAddress))
        {
            leaIdx.Add(i);
            Console.WriteLine($"  lea -> string @ 0x{ins.IP:x16}: {Fmt(ins)}");
        }
    }
    if (leaIdx.Count == 0)
    {
        Console.WriteLine($"  no `lea r,[rip+'{funcName}']` found.");
        return;
    }

    // 3. From each lea, find nearest forward call qword ptr [GetProcAddress IAT].
    HashSet<int> gpaCallIdx = new();
    foreach (int li in leaIdx)
    {
        int found = -1;
        int limit = Math.Min(li + 20, insns.Count);
        for (int j = li + 1; j < limit; j++)
        {
            var ins = insns[j];
            if (ins.Mnemonic == Mnemonic.Call && ins.IsIPRelativeMemoryOperand
                && ins.IPRelativeMemoryAddress == gpaIatVa)
            {
                found = j; break;
            }
            if (ins.FlowControl == FlowControl.Return || ins.FlowControl == FlowControl.UnconditionalBranch) break;
        }
        if (found < 0)
        {
            Console.WriteLine($"  lea @ 0x{insns[li].IP:x16}: no nearby `call [GetProcAddress]`");
            continue;
        }
        if (gpaCallIdx.Add(found))
            Console.WriteLine($"  GetProcAddress call @ 0x{insns[found].IP:x16}");
    }
    if (gpaCallIdx.Count == 0) return;

    // 4. After each GPA call, find storage of rax. Two patterns:
    //    (a) directly:        `mov [rip+disp]/[reg+disp], rax`
    //    (b) via temp reg:    `mov rTmp, rax` then `mov [rip+disp]/[reg+disp], rTmp`
    HashSet<ulong> ripStorageVas = new();
    HashSet<long> structFieldOffsets = new(); // disp only, base reg ignored (assume offset is unique per function)
    foreach (int ci in gpaCallIdx)
    {
        Console.WriteLine($"  post-GPA disasm @ 0x{insns[ci].IP:x16}:");
        int limit = Math.Min(ci + 20, insns.Count);
        for (int j = ci; j < limit; j++)
            Console.WriteLine($"    0x{insns[j].IP:x16}  {Fmt(insns[j])}");

        // Trace which register currently holds the resolved pointer. Starts as RAX after the call.
        var holders = new HashSet<Register> { Register.RAX };
        for (int j = ci + 1; j < limit; j++)
        {
            var ins = insns[j];
            // Store to memory?
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Memory
                && ins.Op1Kind == OpKind.Register && holders.Contains(ins.Op1Register))
            {
                if (ins.IsIPRelativeMemoryOperand)
                {
                    if (ripStorageVas.Add(ins.IPRelativeMemoryAddress))
                        Console.WriteLine($"    -> stored to [0x{ins.IPRelativeMemoryAddress:x}] (RIP-rel)");
                }
                else if (ins.MemoryBase != Register.None)
                {
                    long disp = (long)(int)ins.MemoryDisplacement64;
                    if (structFieldOffsets.Add(disp))
                        Console.WriteLine($"    -> stored to [{ins.MemoryBase}+0x{disp:x}] (struct field, base reg varies — tracking by disp)");
                }
                break;
            }
            // Propagate via `mov rTmp, rAlreadyHolding`
            if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register
                && ins.Op1Kind == OpKind.Register && holders.Contains(ins.Op1Register))
            {
                holders.Add(ins.Op0Register);
            }
            // If the holder reg is clobbered as a destination by some non-mov-from-rax instr, remove it.
            else if (ins.Op0Kind == OpKind.Register && holders.Contains(ins.Op0Register))
            {
                holders.Remove(ins.Op0Register);
                if (holders.Count == 0) break;
            }
            if (ins.FlowControl == FlowControl.UnconditionalBranch) break;
        }
    }

    if (ripStorageVas.Count == 0 && structFieldOffsets.Count == 0)
    {
        Console.WriteLine("  no storage slot located.");
        return;
    }

    // 5. Scan all uses of the storage slots.
    Console.WriteLine();
    Console.WriteLine($"  ---- uses of {funcName} via {ripStorageVas.Count} RIP slot(s), struct disp(s) {{{string.Join(",", structFieldOffsets.Select(d => "0x" + d.ToString("x")))}}} ----");
    int hits = 0;
    for (int i = 0; i < insns.Count; i++)
    {
        var ins = insns[i];
        // Skip the storage `mov [...], reg` instruction itself.
        bool isStorage =
            ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Memory
            && ins.Op1Kind == OpKind.Register;

        bool matchRip = ins.IsIPRelativeMemoryOperand && ripStorageVas.Contains(ins.IPRelativeMemoryAddress);
        bool matchStruct = !ins.IsIPRelativeMemoryOperand && ins.MemoryBase != Register.None
            && structFieldOffsets.Contains((long)(int)ins.MemoryDisplacement64);
        if (!matchRip && !matchStruct) continue;
        if (isStorage) continue;

        bool isCall = ins.Mnemonic == Mnemonic.Call;
        bool isJmp = ins.Mnemonic == Mnemonic.Jmp;
        bool isLoad = ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register;
        if (!isCall && !isJmp && !isLoad) continue;

        string kind = isCall ? "CALL" : isJmp ? "JMP" : "LOAD";
        string slot = matchRip
            ? $"[0x{ins.IPRelativeMemoryAddress:x}]"
            : $"[{ins.MemoryBase}+0x{(long)(int)ins.MemoryDisplacement64:x}]";
        hits++;
        Console.WriteLine();
        Console.WriteLine($"  --- {kind} {funcName} via {slot} @ 0x{ins.IP:x16} ---");
        int from = Math.Max(0, i - contextInsns);
        for (int j = from; j <= i; j++)
            Console.WriteLine($"  0x{insns[j].IP:x16}  {Fmt(insns[j])}");
    }
    Console.WriteLine();
    Console.WriteLine($"  total use sites for {funcName}: {hits}");
}

void ScanByteCallSites(HashSet<ulong> targetIatVas, Dictionary<ulong, string> labels)
{
    foreach (var s in hdrs.SectionHeaders)
    {
        if ((s.SectionCharacteristics & SectionCharacteristics.MemExecute) == 0) continue;
        int start = s.PointerToRawData;
        int len = (int)Math.Min(s.SizeOfRawData, (uint)(bytes.Length - start));
        Console.WriteLine();
        Console.WriteLine($"=== scan section {s.Name} ({len} bytes, VA 0x{imageBase + (uint)s.VirtualAddress:x}) ===");
        for (int i = 0; i + 6 <= len; i++)
        {
            byte op0 = bytes[start + i];
            byte op1 = bytes[start + i + 1];
            if (op0 != 0xFF) continue;
            if (op1 != 0x15 && op1 != 0x25) continue;
            int disp = BitConverter.ToInt32(bytes, start + i + 2);
            ulong instrEndVa = imageBase + (uint)s.VirtualAddress + (uint)(i + 6);
            ulong targetVa = (ulong)((long)instrEndVa + disp);
            if (!targetIatVas.Contains(targetVa)) continue;
            ulong callVa = imageBase + (uint)s.VirtualAddress + (uint)i;
            Console.WriteLine();
            Console.WriteLine($"--- call to {labels[targetVa]} at 0x{callVa:x} (offset 0x{start + i:x}) ---");
            int window = 96;
            int wstart = Math.Max(0, start + i - window);
            ulong wva = imageBase + (uint)s.VirtualAddress + (uint)(wstart - start);
            var reader = new ByteArrayCodeReader(bytes, wstart, (start + i + 6) - wstart);
            var decoder = Decoder.Create(64, reader);
            decoder.IP = wva;
            while (decoder.IP < callVa + 6)
            {
                var ins = decoder.Decode();
                Console.WriteLine($"  0x{ins.IP:x16}  {Fmt(ins)}");
            }
        }
    }
}

void ScanStoreDisp(long targetDisp, List<Instruction> insns)
{
    int hits = 0;
    for (int i = 0; i < insns.Count; i++)
    {
        var ins = insns[i];
        if (ins.Mnemonic != Mnemonic.Mov) continue;
        if (ins.Op0Kind != OpKind.Memory) continue;
        if (ins.Op1Kind != OpKind.Register) continue;
        if (ins.IsIPRelativeMemoryOperand) continue;       // we want struct-relative, not global
        if (ins.MemoryBase == Register.None) continue;
        long disp = (long)(int)ins.MemoryDisplacement64;
        if (disp != targetDisp) continue;

        hits++;
        Console.WriteLine();
        Console.WriteLine($"  --- store to [{ins.MemoryBase}+0x{disp:x}] @ 0x{ins.IP:x16} ---");
        // Trace the source register backwards.
        Register srcReg = ins.Op1Register;
        int from = Math.Max(0, i - contextInsns);
        for (int j = from; j <= i; j++)
            Console.WriteLine($"  0x{insns[j].IP:x16}  {Fmt(insns[j])}");

        // Try to identify the most recent `lea srcReg, [rip+disp]` or `lea srcReg, [rip+...]` providing the source.
        ulong leaTargetVa = 0;
        for (int j = i - 1; j >= Math.Max(0, i - 50); j--)
        {
            var pi = insns[j];
            if (pi.Op0Kind == OpKind.Register && pi.Op0Register == srcReg)
            {
                if (pi.Mnemonic == Mnemonic.Lea && pi.IsIPRelativeMemoryOperand)
                {
                    leaTargetVa = pi.IPRelativeMemoryAddress;
                    Console.WriteLine($"  >> source: lea {srcReg},[rip+...] -> VA 0x{leaTargetVa:x16}");
                    break;
                }
                if (pi.Mnemonic == Mnemonic.Mov && pi.Op1Kind == OpKind.Register)
                {
                    srcReg = pi.Op1Register;  // follow upstream
                    continue;
                }
                if (pi.Mnemonic == Mnemonic.Mov && pi.Op1Kind == OpKind.Memory && pi.IsIPRelativeMemoryOperand)
                {
                    Console.WriteLine($"  >> source: mov {srcReg},[rip+...] (load from 0x{pi.IPRelativeMemoryAddress:x16})");
                    leaTargetVa = pi.IPRelativeMemoryAddress;
                    break;
                }
                Console.WriteLine($"  >> source: {srcReg} set by {Fmt(pi)}");
                break;
            }
        }

        // If lea target is in executable section, disasm a few insns starting there — that's the function.
        if (leaTargetVa != 0)
        {
            foreach (var s in hdrs.SectionHeaders)
            {
                ulong sva = imageBase + (uint)s.VirtualAddress;
                ulong sEnd = sva + (ulong)(uint)Math.Max(s.VirtualSize, s.SizeOfRawData);
                if (leaTargetVa < sva || leaTargetVa >= sEnd) continue;
                bool exec = (s.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0;
                Console.WriteLine($"  >> target is in section {s.Name} (exec={exec})");
                if (exec)
                {
                    int off = (int)(leaTargetVa - sva) + s.PointerToRawData;
                    var reader = new ByteArrayCodeReader(bytes, off, Math.Min(80, bytes.Length - off));
                    var decoder = Decoder.Create(64, reader);
                    decoder.IP = leaTargetVa;
                    Console.WriteLine($"  -- disasm of callback function @ 0x{leaTargetVa:x16} --");
                    int budget = 15;
                    while (budget-- > 0 && decoder.IP < leaTargetVa + 80)
                    {
                        var fi = decoder.Decode();
                        Console.WriteLine($"    0x{fi.IP:x16}  {Fmt(fi)}");
                        if (fi.FlowControl == FlowControl.Return) break;
                    }
                }
                else
                {
                    // Data: dump raw bytes at the target.
                    int off = (int)(leaTargetVa - sva) + s.PointerToRawData;
                    int n = Math.Min(64, bytes.Length - off);
                    Console.Write($"  -- raw data @ 0x{leaTargetVa:x16}:");
                    for (int k = 0; k < n; k++) Console.Write($" {bytes[off + k]:x2}");
                    Console.WriteLine();
                }
                break;
            }
        }
    }
    Console.WriteLine();
    Console.WriteLine($"  total store sites for disp 0x{targetDisp:x}: {hits}");
}

static string Fmt(Instruction ins)
{
    var fmt = new NasmFormatter();
    var so = new StringOutput();
    fmt.Format(ins, so);
    return so.ToStringAndReset();
}

static void Usage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  pe_callsites <dll>                               # list IAT imports");
    Console.Error.WriteLine("  pe_callsites <dll> <ImportedFunc> [Imp2 ...]     # call sites of static imports");
    Console.Error.WriteLine("  pe_callsites <dll> --via-string <Name> [...]     # call sites of runtime-resolved funcs");
    Console.Error.WriteLine("  pe_callsites <dll> --store-disp <hex> [...]      # find `mov [reg+disp], rXX` + trace source");
    Console.Error.WriteLine("  [--context N]                                    # context insns before each site (default 30)");
}
