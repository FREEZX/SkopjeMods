using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

var built = new[]
{
    @"c:\Users\andre\Desktop\SkopjeMods\_fix\Il2CppInterop\bin\Il2CppInterop.Runtime\net6.0\Il2CppInterop.Runtime.dll",
    @"c:\Users\andre\Desktop\SkopjeMods\_fix\Il2CppInterop\bin\Il2CppInterop.Common\netstandard2.0\Il2CppInterop.Common.dll",
};
var builtMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
foreach (var dll in built)
{
    using var fs = File.OpenRead(dll);
    using var pe = new PEReader(fs);
    var reader = pe.GetMetadataReader();
    var asm = reader.GetAssemblyDefinition();
    Console.WriteLine("BUILT " + Path.GetFileName(dll) + " " + asm.Version);
    foreach (var ah in reader.AssemblyReferences)
    {
        var a = reader.GetAssemblyReference(ah);
        var n = reader.GetString(a.Name);
        Console.WriteLine("  ref " + n + " " + a.Version);
    }
    foreach (var th in reader.TypeDefinitions)
    {
        var t = reader.GetTypeDefinition(th);
        var name = reader.GetString(t.Namespace) + "." + reader.GetString(t.Name);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mh in t.GetMethods()) set.Add(reader.GetString(reader.GetMethodDefinition(mh).Name));
        foreach (var fh in t.GetFields()) set.Add(reader.GetString(reader.GetFieldDefinition(fh).Name));
        builtMembers[name] = set;
    }
}

foreach (var caller in new[]
{
    @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\core\Il2CppInterop.HarmonySupport.dll",
    @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\core\BepInEx.Unity.IL2CPP.dll",
    @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\plugins\Skopje.BossHealth.dll",
})
{
    using var fs = File.OpenRead(caller);
    using var pe = new PEReader(fs);
    var reader = pe.GetMetadataReader();
    int miss = 0;
    foreach (var mh in reader.MemberReferences)
    {
        var m = reader.GetMemberReference(mh);
        if (m.Parent.Kind != HandleKind.TypeReference) continue;
        var tr = reader.GetTypeReference((TypeReferenceHandle)m.Parent);
        if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
        var scope = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
        if (scope != "Il2CppInterop.Runtime" && scope != "Il2CppInterop.Common") continue;
        var typeName = reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
        var member = reader.GetString(m.Name);
        if (!builtMembers.TryGetValue(typeName, out var set) || !set.Contains(member))
        {
            miss++;
            Console.WriteLine("MISS " + Path.GetFileName(caller) + " " + typeName + "::" + member);
        }
    }
    Console.WriteLine(Path.GetFileName(caller) + " missing " + miss);
}
return;

var bep = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\core\BepInEx.Unity.IL2CPP.dll";
var newDlls = new[]
{
    @"C:\Users\andre\AppData\Local\Temp\cursor-sandbox-cache\a0404811fc6e51c4c5761fd11591789f\nuget\il2cppinterop.runtime\1.5.3\lib\net6.0\Il2CppInterop.Runtime.dll",
    @"C:\Users\andre\AppData\Local\Temp\cursor-sandbox-cache\a0404811fc6e51c4c5761fd11591789f\nuget\il2cppinterop.common\1.5.3\lib\netstandard2.0\Il2CppInterop.Common.dll",
    @"C:\Users\andre\AppData\Local\Temp\cursor-sandbox-cache\a0404811fc6e51c4c5761fd11591789f\nuget\il2cppinterop.harmonysupport\1.5.3\lib\net6.0\Il2CppInterop.HarmonySupport.dll",
};

var members = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
foreach (var dll in newDlls)
{
    if (!File.Exists(dll)) { Console.WriteLine("MISSING " + dll); continue; }
    using var fs = File.OpenRead(dll);
    using var pe = new PEReader(fs);
    var reader = pe.GetMetadataReader();
    foreach (var th in reader.TypeDefinitions)
    {
        var t = reader.GetTypeDefinition(th);
        var name = reader.GetString(t.Namespace) + "." + reader.GetString(t.Name);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mh in t.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            set.Add(reader.GetString(m.Name));
        }
        foreach (var fh in t.GetFields())
            set.Add(reader.GetString(reader.GetFieldDefinition(fh).Name));
        members[name] = set;
    }
}

using var bfs = File.OpenRead(bep);
using var bpe = new PEReader(bfs);
var br = bpe.GetMetadataReader();
int missing = 0;
foreach (var mh in br.MemberReferences)
{
    var m = br.GetMemberReference(mh);
    if (m.Parent.Kind != HandleKind.TypeReference) continue;
    var tr = br.GetTypeReference((TypeReferenceHandle)m.Parent);
    if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
    var scope = br.GetString(br.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
    if (!scope.StartsWith("Il2CppInterop")) continue;
    var typeName = br.GetString(tr.Namespace) + "." + br.GetString(tr.Name);
    var member = br.GetString(m.Name);
    if (!members.TryGetValue(typeName, out var set) || !set.Contains(member))
    {
        missing++;
        Console.WriteLine($"MISSING {scope} {typeName}::{member}");
    }
}
Console.WriteLine("bepinex missing count " + missing);

var interopDir = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\interop";
var seen = new HashSet<string>(StringComparer.Ordinal);
int interopMissing = 0;
foreach (var file in Directory.EnumerateFiles(interopDir, "*.dll"))
{
    using var fs = File.OpenRead(file);
    using var pe = new PEReader(fs);
    if (!pe.HasMetadata) continue;
    var reader = pe.GetMetadataReader();
    foreach (var mh in reader.MemberReferences)
    {
        var m = reader.GetMemberReference(mh);
        if (m.Parent.Kind != HandleKind.TypeReference) continue;
        var tr = reader.GetTypeReference((TypeReferenceHandle)m.Parent);
        if (tr.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
        var scope = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)tr.ResolutionScope).Name);
        if (scope != "Il2CppInterop.Runtime" && scope != "Il2CppInterop.Common") continue;
        var typeName = reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
        var member = reader.GetString(m.Name);
        var key = typeName + "::" + member;
        if (!seen.Add(key)) continue;
        if (!members.TryGetValue(typeName, out var set) || !set.Contains(member))
        {
            interopMissing++;
            if (interopMissing <= 40)
            {
                var raw = reader.GetBlobReader(m.Signature);
                Console.WriteLine("INTEROP MISSING " + key + " in " + Path.GetFileName(file) + " sig=" + Convert.ToHexString(raw.ReadBytes(raw.Length)));
            }
        }
    }
}
Console.WriteLine("interop missing count " + interopMissing);

static string ReadType(MetadataReader reader, BlobReader blob)
{
    var et = blob.ReadByte();
    switch (et)
    {
        case 0x01: return "void";
        case 0x02: return "bool";
        case 0x03: return "char";
        case 0x04: return "sbyte";
        case 0x05: return "byte";
        case 0x06: return "short";
        case 0x07: return "ushort";
        case 0x08: return "int";
        case 0x09: return "uint";
        case 0x0A: return "long";
        case 0x0B: return "ulong";
        case 0x0C: return "float";
        case 0x0D: return "double";
        case 0x0E: return "string";
        case 0x0F: return ReadType(reader, blob) + "*";
        case 0x10: return ReadType(reader, blob) + "&";
        case 0x18: return "nint";
        case 0x19: return "nuint";
        case 0x1C: return "object";
        case 0x1D: return ReadType(reader, blob) + "[]";
        case 0x13: return "!!" + blob.ReadCompressedInteger();
        case 0x1E: return "!" + blob.ReadCompressedInteger();
        case 0x11:
        case 0x12:
        {
            var coded = blob.ReadCompressedInteger();
            var index = coded >> 2;
            var tag = coded & 3;
            if (tag == 0)
            {
                var td = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(index));
                return reader.GetString(td.Namespace) + "." + reader.GetString(td.Name);
            }
            if (tag == 1)
            {
                var tr = reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(index));
                return reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
            }
            if (tag == 2)
            {
                return "typespec" + index;
            }
            return "type" + tag + ":" + index;
        }
        case 0x15:
        {
            var g = ReadType(reader, blob);
            var n = blob.ReadCompressedInteger();
            var args = new List<string>();
            for (int i = 0; i < n; i++) args.Add(ReadType(reader, blob));
            return g + "<" + string.Join(",", args) + ">";
        }
        default: return "et" + et.ToString("X2");
    }
}

var runtimePath = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\core\Il2CppInterop.Runtime.dll";
using (var fs = File.OpenRead(runtimePath))
using (var pe = new PEReader(fs))
{
    var reader = pe.GetMetadataReader();
    foreach (var th in reader.TypeDefinitions)
    {
        var t = reader.GetTypeDefinition(th);
        foreach (var mh in t.GetMethods())
        {
            var m = reader.GetMethodDefinition(mh);
            if (reader.GetString(m.Name) != "WrapNativeGenericArrayPointer") continue;
            var raw = reader.GetBlobReader(m.Signature);
            Console.WriteLine("OLD RAW " + Convert.ToHexString(raw.ReadBytes(raw.Length)) + " parent=" + reader.GetString(t.Name));
            var blob = reader.GetBlobReader(m.Signature);
            var cc = blob.ReadByte();
            int gen = 0;
            if ((cc & 0x10) != 0) gen = blob.ReadCompressedInteger();
            var pc = blob.ReadCompressedInteger();
            var ret = ReadType(reader, blob);
            var ps = new List<string>();
            for (int i = 0; i < pc; i++) ps.Add(ReadType(reader, blob));
            Console.WriteLine($"OLD {reader.GetString(t.Namespace)}.{reader.GetString(t.Name)} gen={gen} {ret} ({string.Join(", ", ps)})");
        }
    }
}

var sample = Directory.GetFiles(@"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\interop", "UnityEngine.CoreModule.dll").First();
using (var fs = File.OpenRead(sample))
using (var pe = new PEReader(fs))
{
    var reader = pe.GetMetadataReader();
    foreach (var mh in reader.MemberReferences)
    {
        var m = reader.GetMemberReference(mh);
        if (reader.GetString(m.Name) != "WrapNativeGenericArrayPointer") continue;
        string parent;
        if (m.Parent.Kind == HandleKind.TypeReference)
        {
            var tr = reader.GetTypeReference((TypeReferenceHandle)m.Parent);
            parent = "typeref " + reader.GetString(tr.Namespace) + "." + reader.GetString(tr.Name);
        }
        else parent = m.Parent.Kind.ToString();
        var raw = reader.GetBlobReader(m.Signature);
        var bytes = raw.ReadBytes(raw.Length);
        Console.WriteLine("REF parent=" + parent + " sig=" + Convert.ToHexString(bytes));
    }
}
foreach (var dll in newDlls)
{
    using var fs = File.OpenRead(dll);
    using var pe = new PEReader(fs);
    var reader = pe.GetMetadataReader();
    Console.WriteLine("REFS " + Path.GetFileName(dll));
    foreach (var ah in reader.AssemblyReferences)
    {
        var a = reader.GetAssemblyReference(ah);
        Console.WriteLine("  " + reader.GetString(a.Name) + " " + a.Version);
    }
}
