using System.Reflection;

var interop = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\interop";
var core = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\core";
var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var paths = new List<string>();
paths.AddRange(Directory.GetFiles(interop, "*.dll"));
paths.AddRange(Directory.GetFiles(core, "*.dll"));
paths.AddRange(Directory.GetFiles(runtime, "*.dll"));
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));

var names = new HashSet<string>(args);
var skopje = mlc.LoadFromAssemblyPath(Path.Combine(interop, "SkopjeAssembly.dll"));
foreach (var type in skopje.GetTypes())
{
    if (!names.Contains(type.Name))
        continue;
    Console.WriteLine("TYPE " + type.FullName + " : " + type.BaseType?.FullName);
    var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    foreach (var p in type.GetProperties(flags))
        Console.WriteLine("  P " + Format(p.PropertyType) + " " + p.Name);
    foreach (var f in type.GetFields(flags))
    {
        if (f.Name.StartsWith("Native")) continue;
        Console.WriteLine("  F " + Format(f.FieldType) + " " + f.Name);
    }
    foreach (var m in type.GetMethods(flags))
    {
        if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.Contains("Native")) continue;
        var parms = string.Join(", ", m.GetParameters().Select(p => Format(p.ParameterType) + " " + p.Name));
        Console.WriteLine("  M " + Format(m.ReturnType) + " " + m.Name + "(" + parms + ")");
    }
}

static string Format(Type t)
{
    if (t == null) return "?";
    if (t.IsGenericType)
    {
        var def = t.Name;
        var tick = def.IndexOf('`');
        if (tick >= 0) def = def.Substring(0, tick);
        return def + "<" + string.Join(", ", t.GetGenericArguments().Select(Format)) + ">";
    }
    if (t.IsArray)
        return Format(t.GetElementType()) + "[]";
    return t.Name;
}
