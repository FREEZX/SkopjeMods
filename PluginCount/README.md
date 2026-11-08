# PluginCount

A client-side mod for **Skopje '83** (BepInEx 6 IL2CPP) that displays the count of loaded plugins on the main menu by appending it to the game's version string.

## Feature

Hooks into the game's `VersionDisplay` component (from `VersionDisplay.cs`) and appends the loaded plugin count to the build version:

- **Original:** `1.1.0001`
- **Modified:** `1.1.0001 (N plugins loaded)`

## How it works

1. Harmony patches `VersionDisplay.Start` on the main menu.
2. Reads the active plugin count dynamically from `BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance.Plugins.Count`.
3. Updates the `TextMeshProUGUI` component attached to `VersionDisplay` with the formatted string.

## Configuration

The config file is located at `BepInEx/config/com.skopje.plugincount.cfg`:

- **`CountSelf`** (Default: `true`): Whether to include `PluginCount` itself in the loaded plugin count displayed in the main menu. Changes take effect immediately.

## Building and Installation

1. Build with dotnet:
   ```bash
   dotnet build -c Release
   ```
2. Copy `bin/Release/net6.0/Skopje.PluginCount.dll` to your game's `BepInEx/plugins/` directory:
   ```text
   <GameRoot>/BepInEx/plugins/Skopje.PluginCount.dll
   ```

