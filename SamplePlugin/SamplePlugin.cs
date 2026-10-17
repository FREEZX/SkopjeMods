using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Skopje.Modding;

namespace Skopje.SamplePlugin
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SamplePlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.sampleplugin";
        public const string PluginName = "Skopje Sample Plugin";
        public const string PluginVersion = "1.0.0";

        static ManualLogSource LogSource;

        public override void Load()
        {
            LogSource = Log;
            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Subscribe to ModAPI from Load().");

            ModAPI.OnServerStarted += () => LogSource.LogInfo("[ModAPI] OnServerStarted");
            ModAPI.OnServerStopped += () => LogSource.LogInfo("[ModAPI] OnServerStopped");
            ModAPI.OnClientStarted += () => LogSource.LogInfo("[ModAPI] OnClientStarted");
            ModAPI.OnClientStopped += () => LogSource.LogInfo("[ModAPI] OnClientStopped");

            ModAPI.OnPlayerStartedServer += player =>
                LogSource.LogInfo($"[ModAPI] OnPlayerStartedServer idx={player?.PlayerIdx}");
            ModAPI.OnPlayerStoppedServer += player =>
                LogSource.LogInfo($"[ModAPI] OnPlayerStoppedServer idx={player?.PlayerIdx}");
            ModAPI.OnPlayerStartedClient += player =>
                LogSource.LogInfo($"[ModAPI] OnPlayerStartedClient idx={player?.PlayerIdx}");
            ModAPI.OnPlayerStoppedClient += player =>
                LogSource.LogInfo($"[ModAPI] OnPlayerStoppedClient idx={player?.PlayerIdx}");

            ModAPI.OnGameModeStartedServer += OnGameModeStartedServer;
            ModAPI.OnGameModeStartedClient += gm =>
                LogSource.LogInfo($"[ModAPI] OnGameModeStartedClient scene={gm?.ActiveSceneName}");
            ModAPI.OnGameStatusChanged += status =>
                LogSource.LogInfo($"[ModAPI] OnGameStatusChanged {status}");
            ModAPI.OnGameOver += () => LogSource.LogInfo("[ModAPI] OnGameOver");
            ModAPI.OnNewDay += () => LogSource.LogInfo("[ModAPI] OnNewDay");
            ModAPI.OnDay += () => LogSource.LogInfo("[ModAPI] OnDay");
            ModAPI.OnNight += () => LogSource.LogInfo("[ModAPI] OnNight");

            ModAPI.OnModCheckFailed += message =>
                LogSource.LogError($"[ModAPI] OnModCheckFailed\n{message}");
        }

        static void OnGameModeStartedServer(SkopjeGameMode gameMode)
        {
            LogSource.LogInfo($"[ModAPI] OnGameModeStartedServer scene={gameMode?.ActiveSceneName}");

            // Example: mutate an existing baked ScriptableObject after the GUID DB is ready.
            // Replace YOUR_ITEM_GUID with a real Guid from the game’s ScriptableObject assets.
            // Gameplay overrides must run on the dedicated server and every client.
            //
            // const int YourItemGuid = 0;
            // if (ModAPI.TryGetGuidObject<ItemData>(YourItemGuid, out ItemData item))
            // {
            //     item.ItemStackSize = 99;
            //     LogSource.LogInfo($"Overrode stack size for {item.name}");
            // }
        }
    }
}
