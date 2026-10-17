# Skopje modding (BepInEx 6 IL2CPP)

This game loads [BepInEx 6](https://docs.bepinex.dev/) plugins for Unity **IL2CPP**. Official hooks live in the interop assembly **`SkopjeAssembly`**, namespace `Skopje.Modding`.

## Requirements

| Item | Value |
|------|--------|
| Unity (game build) | 2022.3.62 |
| Loader | BepInEx **6** for Unity **IL2CPP** |
| Game assembly to reference | `SkopjeAssembly` (from a built game’s Il2CppInterop / dummy assemblies) |
| Hook API | `Skopje.Modding.ModAPI` |

## Install layout

Install BepInEx next to the game executable (same folder as the `.exe`). Typical layout:

```text
GameRoot/
  Game.exe
  winhttp.dll              # Doorstop (Windows)
  doorstop_config.ini
  BepInEx/
    core/
    config/                # Local settings — NOT compared on join
    plugins/               # Your mod DLLs (+ shared data files)
    patchers/              # Optional patcher DLLs (+ shared data files)
    LogOutput.log
```

Put gameplay plugins in `BepInEx/plugins/`. Shared override tables (JSON/CFG/etc.) must sit under `plugins/` or `patchers/` next to the DLL, **not** under `BepInEx/config`.

## Minimal plugin

Subscribe in `BasePlugin.Load` with `+=` (plugins load before the first scene):

```csharp
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Skopje.Modding;

[BepInPlugin("com.example.skopje.hello", "Hello Skopje", "1.0.0")]
public class HelloPlugin : BasePlugin
{
    public override void Load()
    {
        ModAPI.OnServerStarted += () => Log.LogInfo("Server started");
        ModAPI.OnGameModeStartedServer += gm => Log.LogInfo($"GameMode server: {gm}");
        ModAPI.OnModCheckFailed += msg => Log.LogError(msg);
    }
}
```

A full logging sample lives in [`Modding/SamplePlugin/`](../Modding/SamplePlugin/).

## ModAPI events

| Event | Where it runs | Notes |
|-------|----------------|-------|
| `OnServerStarted` / `OnServerStopped` | Dedicated server, listen host | FishNet local server state |
| `OnClientStarted` / `OnClientStopped` | Pure client, listen host | FishNet local client state |
| `OnPlayerStartedServer` / `OnPlayerStoppedServer` | Server / host | `SkopjePlayer` |
| `OnPlayerStartedClient` / `OnPlayerStoppedClient` | Clients / host | `SkopjePlayer` |
| `OnGameModeStartedServer` | Server / host | After `SkopjeGameMode.OnStartServer` |
| `OnGameModeStartedClient` | Clients / host | After `SkopjeGameMode.OnStartClient` |
| `OnGameStatusChanged` | Server/host and client-only | `NotStarted`, `Active`, `GameOver` |
| `OnGameOver` | Same peers as status | When status becomes `GameOver` |
| `OnNewDay` / `OnDay` / `OnNight` | Server and clients | Day/night cycle |
| `OnModCheckFailed` | Local client | Join rejected; message lists path diffs |

A **listen host** raises both server and client events. Prefer **server** events for gameplay changes so dedicated servers work.

### ScriptableObject lookup helpers

```csharp
if (ModAPI.TryGetGuidObject(123456, out GuidScriptableObject obj)) { /* ... */ }
if (ModAPI.TryGetGuidObject<WeaponData>(123456, out var weapon)) { /* mutate fields */ }
```

Call these after `OnServerStarted` or `OnGameModeStartedServer` so the GUID database is registered.

## ScriptableObject / item stat overrides

Items and many definitions are baked `ScriptableObject`s keyed by integer `Guid` in `GuidScriptableObjectDBManager`.

**Supported today**

- Mutate **public fields** on existing instances (`ItemData`, `WeaponData`, `UtilityData`, …) at runtime after the DB is ready.
- Same plugin must run on the **dedicated server and every client** for gameplay stats.

**Not supported yet**

- Adding brand-new items/recipes as new assets.
- Replacing loose `.asset` files on disk (IL2CPP does not load those as Mono would).

Example pattern (also commented in the sample plugin):

```csharp
ModAPI.OnGameModeStartedServer += _ =>
{
    if (ModAPI.TryGetGuidObject<WeaponData>(YOUR_GUID, out var weapon))
    {
        // weapon.SomePublicStatField = newValue;
    }
};
```

## Multiplayer contract

- FishNet is **server-authoritative**. Client-only stat changes do not define combat/inventory outcomes.
- Gameplay mods must be **identical** on the host/dedicated server and every joining client.
- Client-only mods (UI, camera, cosmetics) should subscribe only to **client** events and must not change server-owned results.
- Put shared override data under `BepInEx/plugins/` (or `patchers/`) so the join check can see it. Local keybinds and BepInEx settings stay in `BepInEx/config/` and are **not** compared.

## Manifest join check

Before a remote client is authenticated, the game hashes:

- `BepInEx/plugins/**` and `BepInEx/patchers/**`
- Extensions: `.dll`, `.json`, `.cfg`, `.txt`, `.xml`

Relative paths (forward slashes) + SHA-256 must match the server’s install. If `BepInEx` is missing on both sides, both look vanilla and match.

**Not hashed:** `BepInEx/core`, `BepInEx/config`, logs.

**Note:** A DLL present under `plugins/` still counts even if disabled in BepInEx config. Remove or rename the file to drop it from the check.

On mismatch the client is disconnected, `ModAPI.OnModCheckFailed` runs with a detail string, and a Doozy game event named **`ModCheckFailed`** is sent (UI can listen for that name).

## Troubleshooting

| Symptom | What to check |
|---------|----------------|
| Join rejected / disconnect | Log message and `OnModCheckFailed`; compare listed missing/extra/changed paths under `plugins`/`patchers` |
| Plugin never loads | BepInEx 6 IL2CPP pack; `LogOutput.log`; Doorstop `winhttp.dll` next to the exe |
| Overrides work solo but not in MP | Same plugin + sidecar files on **server and clients**; use server hooks for gameplay |
| Different stats with “matching” mods | Shared data must live under `plugins/`, not only in `BepInEx/config/` |
| Disabled mod still blocks join | File still hashed; remove the DLL/data file from `plugins` |

Logs: `BepInEx/LogOutput.log` and the Unity player log.
