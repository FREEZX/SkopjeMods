# Sample BepInEx plugin

Minimal BepInEx **6 IL2CPP** plugin that logs `Skopje.Modding.ModAPI` events and shows a commented ScriptableObject override pattern.

Full guide: [`Docs/Modding.md`](../../Docs/Modding.md).

## Setup

1. Install BepInEx 6 IL2CPP for the game build.
2. Generate or copy Il2CppInterop assemblies from a run of that build (BepInEx `interop` / unhollowed output — folder names vary by pack).
3. Edit [`SamplePlugin.csproj`](SamplePlugin.csproj):
   - Set `GameInteropDir` to that assemblies folder (must contain `SkopjeAssembly.dll` and BepInEx / Il2Cpp references as needed).
4. `dotnet build -c Release`
5. Copy `bin/Release/net6.0/Skopje.SamplePlugin.dll` to `BepInEx/plugins/`.

Gameplay mods that change stats must be installed on the dedicated server and every client. Shared JSON/CFG override files belong next to the DLL under `plugins/`, not in `BepInEx/config/`.
