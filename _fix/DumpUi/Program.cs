using System.Reflection;

var interop = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\interop";
var core = @"D:\SteamLibrary\steamapps\common\Skopje83\BepInEx\core";
var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
var paths = new List<string>();
paths.AddRange(Directory.GetFiles(interop, "*.dll"));
paths.AddRange(Directory.GetFiles(core, "*.dll"));
paths.AddRange(Directory.GetFiles(runtime, "*.dll"));
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths));

void Dump(string file, string typeName)
{
    var asm = mlc.LoadFromAssemblyPath(Path.Combine(interop, file));
    foreach (var type in asm.GetTypes())
    {
        if (type.Name != typeName) continue;
        Console.WriteLine("TYPE " + type.FullName);
        foreach (var p in type.GetProperties())
        {
            if (p.DeclaringType != type) continue;
            var n = p.Name;
            if (n.Contains("fill") || n.Contains("Fill") || n.Contains("sprite") || n.Contains("Sprite") || n == "color" || n == "type" || n == "material" || n == "font" || n == "text" || n == "fontSize" || n == "alignment" || n == "raycastTarget" || n == "fontSharedMaterial")
                Console.WriteLine("  P " + p.PropertyType.Name + " " + n);
        }
        foreach (var nested in type.GetNestedTypes())
            Console.WriteLine("  NEST " + nested.Name + " " + (nested.IsEnum ? string.Join(",", EnumNames(nested)) : ""));
    }
}

static string EnumNames(Type t) => string.Join(",", t.GetEnumNames());

var skopje = mlc.LoadFromAssemblyPath(Path.Combine(interop, "SkopjeAssembly.dll"));
string[] names = { "EnemyBlockHelper", "NotDamageable", "IDamageable" };
foreach (var type in skopje.GetTypes())
{
    if (!names.Contains(type.Name)) continue;
    Console.WriteLine("TYPE " + type.Name);
    if (type.IsEnum)
    {
        Console.WriteLine("  " + string.Join(", ", type.GetEnumNames()));
        continue;
    }
    foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        Console.WriteLine("  P " + p.PropertyType.Name + " " + p.Name);
    foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
    {
        if (m.IsSpecialName) continue;
        Console.WriteLine("  M " + m.ReturnType.Name + " " + m.Name);
    }
}
