using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Skopje.Modding;
using UnityEngine;

namespace Skopje.CustomPlayerSkins
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class CustomPlayerSkinsPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.customplayerskins";
        public const string PluginName = "Skopje Custom Player Skins";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<bool> Enabled;

        static Harmony _harmony;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Skin", "Enabled", true, "Replace each player outfit with its own PNG in the CustomSkins folder next to this plugin.");

            _harmony = new Harmony(PluginGuid);
            var refresh = AccessTools.Method(typeof(SkopjePlayer), "RefreshActiveModels");
            if (refresh == null)
                LogSource.LogWarning("SkopjePlayer.RefreshActiveModels was not found. The skins are applied when a player spawns.");
            else
                _harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(SkinPatches), nameof(SkinPatches.AfterRefreshModels)));

            _onPlayerStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(SkinReplacer.ApplyPlayer));
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Edit the PNGs in CustomSkins: singleplayer, ranger, medic, engineer, and fighter (the Warrior class).");
        }

        public override bool Unload()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();
            SkinReplacer.Restore();
            return true;
        }
    }

    static class SkinPatches
    {
        public static void AfterRefreshModels(SkopjePlayer __instance)
        {
            SkinReplacer.ApplyPlayer(__instance);
        }
    }

    static class SkinReplacer
    {
        sealed class Outfit
        {
            public string FileName;
            public string DumpMaterial;
            public string[] Materials;
            public Texture2D Texture;
            public DateTime WriteTimeUtc;
        }

        // Fighter is the multiplayer class shown as Warrior on the player icons.
        static readonly Outfit[] Outfits =
        {
            new Outfit
            {
                FileName = "singleplayer.png",
                DumpMaterial = "MAT_Pioneer_Default_Clothes",
                Materials = new[] { "MAT_Pioneer_Default_Clothes", "MAT_Pioneer_Default_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "ranger.png",
                DumpMaterial = "MAT_Pioneer_Ranger_Clothes",
                Materials = new[] { "MAT_Pioneer_Ranger_Clothes", "MAT_Pioneer_Ranger_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "medic.png",
                DumpMaterial = "MAT_Pioneer_Medic_Clothes",
                Materials = new[] { "MAT_Pioneer_Medic_Clothes", "MAT_Pioneer_Medic_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "engineer.png",
                DumpMaterial = "MAT_Pioneer_Engi_Clothes",
                Materials = new[] { "MAT_Pioneer_Engi_Clothes", "MAT_Pioneer_Engi_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "fighter.png",
                DumpMaterial = "MAT_Pioneer_Fighter_Clothes",
                Materials = new[] { "MAT_Pioneer_Fighter_Clothes", "MAT_Pioneer_Fighter_Clothes_FP" },
            },
        };

        static readonly Dictionary<int, Texture> _originals = new Dictionary<int, Texture>();
        static float _nextFileCheck;
        static bool _loggedMiss;

        public static void ApplyPlayer(SkopjePlayer player)
        {
            if (!CustomPlayerSkinsPlugin.Enabled.Value)
            {
                RevertMaterials();
                return;
            }
            if (!EnsureTextures())
                return;

            int applied = ApplyLoadedMaterials();
            if (player != null)
            {
                applied += ApplySkins(player.PlayerSkins);
                applied += ApplyRenderer(player.FirstPersonRenderer);
                applied += ApplyRenderer(player.ThirdPersonRenderer);
            }

            if (applied == 0 && !_loggedMiss)
            {
                _loggedMiss = true;
                CustomPlayerSkinsPlugin.LogSource.LogWarning("Custom skins are loaded, but the player clothes materials are not in memory yet.");
            }
            else if (applied > 0)
            {
                _loggedMiss = false;
                CustomPlayerSkinsPlugin.LogSource.LogInfo($"Applied {applied} player clothes material{(applied == 1 ? "" : "s")} from CustomSkins.");
            }
        }

        public static void Restore()
        {
            RevertMaterials();
            _originals.Clear();
            for (int i = 0; i < Outfits.Length; i++)
            {
                if (Outfits[i].Texture == null)
                    continue;
                UnityEngine.Object.Destroy(Outfits[i].Texture);
                Outfits[i].Texture = null;
            }
        }

        static void RevertMaterials()
        {
            foreach (var pair in _originals)
            {
                var material = FindMaterial(pair.Key);
                if (material != null)
                    material.SetTexture("_MainTex", pair.Value);
            }
        }

        static int ApplyLoadedMaterials()
        {
            int applied = 0;
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < materials.Length; i++)
                applied += ApplyMaterial(materials[i]);
            return applied;
        }

        static int ApplySkins(Il2CppReferenceArray<PlayerSkin> skins)
        {
            if (skins == null)
                return 0;
            int applied = 0;
            for (int i = 0; i < skins.Length; i++)
            {
                PlayerSkin skin = skins[i];
                if (skin == null)
                    continue;
                applied += ApplyMaterialArray(skin.FPMaterials);
                applied += ApplyMaterialArray(skin.TPMaterials);
            }
            return applied;
        }

        static int ApplyRenderer(Renderer renderer)
        {
            if (renderer == null)
                return 0;
            var materials = renderer.sharedMaterials;
            if (materials == null)
                return 0;
            int applied = 0;
            for (int i = 0; i < materials.Length; i++)
                applied += ApplyMaterial(materials[i]);
            return applied;
        }

        static int ApplyMaterialArray(Il2CppReferenceArray<Material> materials)
        {
            if (materials == null)
                return 0;
            int applied = 0;
            for (int i = 0; i < materials.Length; i++)
                applied += ApplyMaterial(materials[i]);
            return applied;
        }

        static int ApplyMaterial(Material material)
        {
            if (material == null || !material.HasProperty("_MainTex"))
                return 0;
            Outfit outfit = FindOutfit(material.name);
            if (outfit == null || outfit.Texture == null)
                return 0;

            int id = material.GetInstanceID();
            if (!_originals.ContainsKey(id))
            {
                var current = material.GetTexture("_MainTex");
                _originals[id] = current;
                CopySampling(outfit.Texture, current);
            }

            if (material.GetTexture("_MainTex") == outfit.Texture)
                return 0;

            material.SetTexture("_MainTex", outfit.Texture);
            return 1;
        }

        static Outfit FindOutfit(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return null;
            for (int i = 0; i < Outfits.Length; i++)
            {
                string[] names = Outfits[i].Materials;
                for (int n = 0; n < names.Length; n++)
                {
                    string expected = names[n];
                    if (materialName == expected || materialName.StartsWith(expected + " (", StringComparison.Ordinal))
                        return Outfits[i];
                }
            }
            return null;
        }

        static void CopySampling(Texture2D target, Texture source)
        {
            if (target == null || source == null)
                return;
            target.wrapMode = source.wrapMode;
            target.filterMode = source.filterMode;
            target.anisoLevel = source.anisoLevel;
        }

        static Material FindMaterial(int instanceId)
        {
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material != null && material.GetInstanceID() == instanceId)
                    return material;
            }
            return null;
        }

        static bool EnsureTextures()
        {
            if (AnyLoaded() && Time.unscaledTime < _nextFileCheck)
                return true;
            _nextFileCheck = Time.unscaledTime + 1f;

            string directory = SkinsDirectory();
            int ready = 0;
            for (int i = 0; i < Outfits.Length; i++)
            {
                if (EnsureOutfit(Outfits[i], directory))
                    ready++;
            }

            if (ready == 0)
            {
                CustomPlayerSkinsPlugin.LogSource.LogWarning("No outfit PNGs are ready in " + directory);
                return false;
            }
            return true;
        }

        static bool AnyLoaded()
        {
            for (int i = 0; i < Outfits.Length; i++)
            {
                if (Outfits[i].Texture != null)
                    return true;
            }
            return false;
        }

        static bool EnsureOutfit(Outfit outfit, string directory)
        {
            string path = Path.Combine(directory, outfit.FileName);
            if (!File.Exists(path))
            {
                if (!TryDumpSample(outfit, path))
                    return outfit.Texture != null;
            }

            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(path); }
            catch (Exception ex)
            {
                CustomPlayerSkinsPlugin.LogSource.LogError("Could not read " + outfit.FileName + ": " + ex.Message);
                return outfit.Texture != null;
            }

            if (outfit.Texture != null && outfit.WriteTimeUtc == writeTime)
                return true;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                CustomPlayerSkinsPlugin.LogSource.LogError("Could not read " + outfit.FileName + ": " + ex.Message);
                return outfit.Texture != null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            texture.name = "SkopjeCustomSkin_" + Path.GetFileNameWithoutExtension(outfit.FileName);
            texture.hideFlags = HideFlags.HideAndDontSave;
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                CustomPlayerSkinsPlugin.LogSource.LogError(outfit.FileName + " is not a readable image: " + path);
                return outfit.Texture != null;
            }

            if (outfit.Texture != null)
                UnityEngine.Object.Destroy(outfit.Texture);
            outfit.Texture = texture;
            outfit.WriteTimeUtc = writeTime;
            _loggedMiss = false;
            CustomPlayerSkinsPlugin.LogSource.LogInfo($"Loaded {outfit.FileName} ({texture.width}x{texture.height})");
            return true;
        }

        static bool TryDumpSample(Outfit outfit, string path)
        {
            Texture source = null;
            var materials = Resources.FindObjectsOfTypeAll<Material>();
            for (int i = 0; i < materials.Length && source == null; i++)
            {
                Material material = materials[i];
                if (material == null || material.name != outfit.DumpMaterial)
                    continue;
                if (!material.HasProperty("_MainTex"))
                    continue;
                Texture current = material.GetTexture("_MainTex");
                if (_originals.TryGetValue(material.GetInstanceID(), out Texture original))
                    current = original;
                source = current;
            }
            if (source == null)
                return false;

            byte[] png = EncodePng(source);
            if (png == null || png.Length == 0)
                return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
                CustomPlayerSkinsPlugin.LogSource.LogInfo("Dumped " + outfit.FileName + " from the game into CustomSkins.");
                return true;
            }
            catch (Exception ex)
            {
                CustomPlayerSkinsPlugin.LogSource.LogError("Could not write " + outfit.FileName + ": " + ex.Message);
                return false;
            }
        }

        static byte[] EncodePng(Texture source)
        {
            var readable = source as Texture2D;
            bool temporary = false;
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;
            try
            {
                if (readable == null || !readable.isReadable)
                {
                    rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;
                    readable = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                    readable.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                    readable.Apply();
                    temporary = true;
                }

                var encoded = ImageConversion.EncodeToPNG(readable);
                if (encoded == null)
                    return null;
                var bytes = new byte[encoded.Length];
                for (int i = 0; i < encoded.Length; i++)
                    bytes[i] = encoded[i];
                return bytes;
            }
            finally
            {
                RenderTexture.active = previous;
                if (rt != null)
                    RenderTexture.ReleaseTemporary(rt);
                if (temporary && readable != null)
                    UnityEngine.Object.Destroy(readable);
            }
        }

        static string SkinsDirectory()
        {
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(dllDir))
                return Path.Combine(dllDir, "CustomSkins");
            return Path.Combine(Paths.PluginPath, "CustomSkins");
        }
    }
}
