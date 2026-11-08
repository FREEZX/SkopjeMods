using System;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace Skopje.PluginCount
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class PluginCountPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.plugincount";
        public const string PluginName = "PluginCount";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<bool> CountSelf;
        private static Harmony _harmony;

        public override void Load()
        {
            LogSource = Log;

            try
            {
                CountSelf = Config.Bind(
                    "General",
                    "CountSelf",
                    true,
                    "Whether to include PluginCount itself in the loaded plugin count displayed in the main menu."
                );
                CountSelf.SettingChanged += (_, _) => UpdateAllDisplays();

                _harmony = new Harmony(PluginGuid);
                var targetMethod = AccessTools.Method(typeof(global::VersionDisplay), "Start");
                if (targetMethod != null)
                {
                    _harmony.Patch(
                        targetMethod,
                        postfix: new HarmonyMethod(typeof(PluginCountPatches), nameof(PluginCountPatches.VersionDisplay_Start_Postfix))
                    );
                    LogSource.LogInfo("Successfully hooked VersionDisplay.Start.");
                }
                else
                {
                    LogSource.LogWarning("Could not find VersionDisplay.Start to patch.");
                }

                // If VersionDisplay is already loaded in the active scene, apply count immediately
                UpdateAllDisplays();

                LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. CountSelf={CountSelf.Value}");
            }
            catch (Exception ex)
            {
                LogSource.LogError($"Failed to initialize {PluginName}: {ex}");
            }
        }

        public override bool Unload()
        {
            try
            {
                _harmony?.UnpatchSelf();
            }
            catch (Exception ex)
            {
                LogSource.LogWarning($"Failed to unpatch Harmony: {ex.Message}");
            }
            return true;
        }

        public static int GetLoadedPluginCount()
        {
            try
            {
                var chainloader = IL2CPPChainloader.Instance;
                if (chainloader?.Plugins != null)
                {
                    int count = chainloader.Plugins.Count;
                    if (CountSelf != null && !CountSelf.Value)
                    {
                        if (chainloader.Plugins.ContainsKey(PluginGuid))
                            count--;
                        else
                            count = Math.Max(0, count - 1);
                    }
                    return Math.Max(0, count);
                }
            }
            catch (Exception ex)
            {
                LogSource?.LogWarning($"Failed to query loaded plugins count: {ex.Message}");
            }
            return 0;
        }

        public static void UpdateAllDisplays()
        {
            try
            {
                var displays = UnityEngine.Object.FindObjectsOfType<global::VersionDisplay>();
                if (displays != null)
                {
                    for (int i = 0; i < displays.Length; i++)
                    {
                        var display = displays[i];
                        if (display != null)
                        {
                            PluginCountPatches.ApplyPluginCount(display);
                        }
                    }
                }
            }
            catch
            {
                // Engine might not be fully initialized yet when Load() runs; safe to ignore
            }
        }
    }

    public static class PluginCountPatches
    {
        private static readonly Regex PluginCountRegex = new Regex(@"\s*\(\d+\s+plugins?\s+loaded\)", RegexOptions.Compiled);

        public static void VersionDisplay_Start_Postfix(global::VersionDisplay __instance)
        {
            ApplyPluginCount(__instance);
        }

        public static void ApplyPluginCount(global::VersionDisplay instance)
        {
            if (instance == null)
                return;

            try
            {
                var textComponent = instance.Text;
                if (textComponent == null)
                    textComponent = instance.GetComponent<TextMeshProUGUI>();
                if (textComponent == null)
                    textComponent = instance.GetComponentInChildren<TextMeshProUGUI>();

                if (textComponent == null)
                {
                    PluginCountPlugin.LogSource?.LogWarning("VersionDisplay does not have an accessible TextMeshProUGUI component.");
                    return;
                }

                string currentText = textComponent.text;
                if (string.IsNullOrEmpty(currentText))
                {
                    currentText = UnityEngine.Application.version;
                    if (string.IsNullOrEmpty(currentText))
                        return;
                }

                int count = PluginCountPlugin.GetLoadedPluginCount();
                string suffix = $" ({count} plugins loaded)";

                if (PluginCountRegex.IsMatch(currentText))
                {
                    textComponent.text = PluginCountRegex.Replace(currentText, suffix);
                }
                else
                {
                    textComponent.text = $"{currentText.TrimEnd()}{suffix}";
                }

                PluginCountPlugin.LogSource?.LogInfo($"Updated version display: \"{textComponent.text}\"");
            }
            catch (Exception ex)
            {
                PluginCountPlugin.LogSource?.LogError($"Error in ApplyPluginCount: {ex}");
            }
        }
    }
}
