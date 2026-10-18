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
using UnityEngine.Rendering;

namespace Skopje.CustomPlayerSkins
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class CustomPlayerSkinsPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.customplayerskins";
        public const string PluginName = "Skopje Custom Player Skins";
        public const string PluginVersion = "1.3.0";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<bool> Enabled;

        static Harmony _harmony;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Skin", "Enabled", true, "Replace each player outfit with its own PNG in the CustomSkins folder next to this plugin.");

            _harmony = new Harmony(PluginGuid);
            var setTexture = AccessTools.Method(typeof(Material), "SetTexture", new[] { typeof(string), typeof(Texture) });
            if (setTexture != null)
                _harmony.Patch(setTexture, prefix: new HarmonyMethod(typeof(SkinPatches), nameof(SkinPatches.BeforeSetTexture)));
            var setTextureId = AccessTools.Method(typeof(Material), "SetTexture", new[] { typeof(int), typeof(Texture) });
            if (setTextureId != null)
                _harmony.Patch(setTextureId, prefix: new HarmonyMethod(typeof(SkinPatches), nameof(SkinPatches.BeforeSetTextureId)));

            var refresh = AccessTools.Method(typeof(SkopjePlayer), "RefreshActiveModels");
            if (refresh == null)
                LogSource.LogWarning("SkopjePlayer.RefreshActiveModels was not found. The skins are applied when a player spawns.");
            else
                _harmony.Patch(refresh, postfix: new HarmonyMethod(typeof(SkinPatches), nameof(SkinPatches.AfterRefreshModels)));

            _onPlayerStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(SkinReplacer.ApplyPlayer));
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Edit the PNGs in CustomSkins: singleplayer, ranger, medic, engineer, fighter (Warrior), prp, and hands.");
        }

        public override bool Unload()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();
            if (_onPlayerStarted != null)
                ModAPI.remove_OnPlayerStartedClient(_onPlayerStarted);
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

        public static void BeforeSetTexture(string name, ref Texture value)
        {
            if (name == "_MainTex")
                SkinReplacer.Redirect(ref value);
        }

        public static void BeforeSetTextureId(int nameID, ref Texture value)
        {
            if (nameID == Shader.PropertyToID("_MainTex"))
                SkinReplacer.Redirect(ref value);
        }
    }

    static class SkinReplacer
    {
        sealed class Outfit
        {
            public string FileName;
            public string DumpMaterial;
            public string SourceTexture;
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
                SourceTexture = "DIF_Pioneer_Clothes_Default",
                Materials = new[] { "MAT_Pioneer_Default_Clothes", "MAT_Pioneer_Default_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "ranger.png",
                DumpMaterial = "MAT_Pioneer_Ranger_Clothes",
                SourceTexture = "DIF_Pioneer_Clothes_Ranger",
                Materials = new[] { "MAT_Pioneer_Ranger_Clothes", "MAT_Pioneer_Ranger_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "medic.png",
                DumpMaterial = "MAT_Pioneer_Medic_Clothes",
                SourceTexture = "DIF_Pioneer_Clothes_Medic",
                Materials = new[] { "MAT_Pioneer_Medic_Clothes", "MAT_Pioneer_Medic_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "engineer.png",
                DumpMaterial = "MAT_Pioneer_Engi_Clothes",
                SourceTexture = "DIF_Pioneer_Clothes_Engi",
                Materials = new[] { "MAT_Pioneer_Engi_Clothes", "MAT_Pioneer_Engi_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "fighter.png",
                DumpMaterial = "MAT_Pioneer_Fighter_Clothes",
                SourceTexture = "DIF_Pioneer_Clothes_Fighter",
                Materials = new[] { "MAT_Pioneer_Fighter_Clothes", "MAT_Pioneer_Fighter_Clothes_FP" },
            },
            new Outfit
            {
                FileName = "prp.png",
                DumpMaterial = "MAT_PRP",
                SourceTexture = "DIF_PRP",
                Materials = new[] { "MAT_PRP", "MAT_PRP_FP" },
            },
            new Outfit
            {
                FileName = "hands.png",
                DumpMaterial = "MAT_Pioneer_Naked",
                SourceTexture = "DIF_FPSHands_Naked",
                Materials = new[] { "MAT_Pioneer_Naked" },
            },
        };

        static readonly Dictionary<int, Texture> _originals = new Dictionary<int, Texture>();
        static readonly Dictionary<int, Material> _materials = new Dictionary<int, Material>();
        static readonly Dictionary<int, Renderer> _renderers = new Dictionary<int, Renderer>();
        static readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        static float _nextFileCheck;
        static bool _loggedApply;

        public static void ApplyPlayer(SkopjePlayer player)
        {
            if (!CustomPlayerSkinsPlugin.Enabled.Value)
            {
                RevertLive();
                return;
            }
            if (player == null || !EnsureTextures(player))
                return;

            int applied = ApplyPlayerRenderers(player);
            if (applied > 0 && !_loggedApply)
            {
                _loggedApply = true;
                CustomPlayerSkinsPlugin.LogSource.LogInfo($"Applied {applied} player material{(applied == 1 ? "" : "s")} from CustomSkins.");
            }
        }

        public static void Restore()
        {
            RevertLive();
            _originals.Clear();
            _materials.Clear();
            _renderers.Clear();
            for (int i = 0; i < Outfits.Length; i++)
            {
                if (Outfits[i].Texture == null)
                    continue;
                UnityEngine.Object.Destroy(Outfits[i].Texture);
                Outfits[i].Texture = null;
            }
        }

        static void RevertLive()
        {
            RevertMaterials();
            ClearRendererOverrides();
        }

        static void RevertMaterials()
        {
            foreach (var pair in _materials)
            {
                Material material = pair.Value;
                if (material != null && _originals.TryGetValue(pair.Key, out Texture original))
                    material.SetTexture("_MainTex", original);
            }
        }

        static void ClearRendererOverrides()
        {
            foreach (var pair in _renderers)
            {
                Renderer renderer = pair.Value;
                if (renderer == null)
                    continue;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null)
                    continue;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null || !_originals.TryGetValue(material.GetInstanceID(), out Texture original))
                        continue;
                    renderer.GetPropertyBlock(_block, slot);
                    _block.SetTexture("_MainTex", original);
                    renderer.SetPropertyBlock(_block, slot);
                }
            }
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

        static int ApplyPlayerRenderers(SkopjePlayer player)
        {
            if (player == null)
                return 0;
            int applied = ApplySkins(player.PlayerSkins);
            applied += ApplyRenderer(player.FirstPersonRenderer);
            applied += ApplyRenderer(player.ThirdPersonRenderer);
            applied += ApplyRenderer(player.ThirdPersonBackpackRenderer);
            applied += ApplyOn(player.ThirdPerson);
            applied += ApplyOn(player.FirstPerson);
            if (player.SkopjeCharacterController != null)
                applied += ApplyOn(player.SkopjeCharacterController.MeshRoot);
            if (player.UtilityController != null)
                applied += ApplyOn(player.UtilityController.FPSHolder);
            if (player.WeaponsController != null)
                applied += ApplyWeapons(player.WeaponsController);
            return applied;
        }

        static int ApplyWeapons(WeaponsController weapons)
        {
            if (weapons == null)
                return 0;
            int applied = 0;
            applied += ApplyOn(weapons.Socket);
            applied += ApplyOn(weapons.ThrowSocket);
            applied += ApplyOn(weapons.PRPObject);
            applied += ApplyOn(weapons.BrokenPRPObject);
            applied += ApplyOn(weapons.KnifeObject);
            var list = weapons.AvailableWeapons;
            if (list == null)
                return applied;
            for (int i = 0; i < list.Count; i++)
            {
                Weapon weapon = list[i];
                if (weapon == null)
                    continue;
                applied += ApplyOn(weapon.WeaponModel);
                applied += ApplyOn(weapon.LeftHandWeaponModel);
                applied += ApplyOn(weapon.PhysicsWeaponModel);
                applied += ApplyOn(weapon.PhysicsLeftHandWeaponModel);
            }
            return applied;
        }

        static int ApplyOn(GameObject root)
        {
            if (root == null)
                return 0;
            return ApplyUnder(root.transform);
        }

        static int ApplyOn(Component root)
        {
            if (root == null)
                return 0;
            return ApplyUnder(root.transform);
        }

        static int ApplyUnder(Transform root)
        {
            if (root == null)
                return 0;
            Renderer[] found = root.GetComponentsInChildren<Renderer>(true);
            int applied = 0;
            for (int i = 0; i < found.Length; i++)
                applied += ApplyRenderer(found[i]);
            return applied;
        }

        static int ApplyRenderer(Renderer renderer)
        {
            if (renderer == null)
                return 0;
            int applied = ApplyMaterialSlots(renderer, renderer.sharedMaterials, false);
            applied += ApplyMaterialSlots(renderer, renderer.materials, true);
            return applied;
        }

        static int ApplyMaterialSlots(Renderer renderer, Material[] materials, bool propertyBlock)
        {
            if (materials == null)
                return 0;
            int applied = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                Outfit outfit = Resolve(materials[i]);
                if (outfit == null)
                    continue;
                applied += ApplyMaterial(materials[i]);
                _renderers[renderer.GetInstanceID()] = renderer;
                if (!propertyBlock)
                    continue;
                renderer.GetPropertyBlock(_block, i);
                _block.SetTexture("_MainTex", outfit.Texture);
                renderer.SetPropertyBlock(_block, i);
            }
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
            Outfit outfit = Resolve(material);
            if (outfit == null)
                return 0;

            int id = material.GetInstanceID();
            if (!_originals.ContainsKey(id))
            {
                var current = material.GetTexture("_MainTex");
                _originals[id] = current;
                _materials[id] = material;
                CopySampling(outfit.Texture, current);
            }

            if (material.GetTexture("_MainTex") == outfit.Texture)
                return 0;

            material.SetTexture("_MainTex", outfit.Texture);
            return 1;
        }

        static Outfit Resolve(Material material)
        {
            if (material == null || !material.HasProperty("_MainTex"))
                return null;
            Outfit byName = FindOutfit(material.name);
            if (byName != null && byName.Texture != null)
                return byName;

            Texture current = material.GetTexture("_MainTex");
            if (current == null)
                return null;
            for (int i = 0; i < Outfits.Length; i++)
            {
                Outfit outfit = Outfits[i];
                if (outfit.Texture == null)
                    continue;
                if (current == outfit.Texture)
                    return outfit;
                if (TextureNameMatches(current.name, outfit.SourceTexture))
                    return outfit;
            }
            return null;
        }

        public static void Redirect(ref Texture value)
        {
            if (value == null || !CustomPlayerSkinsPlugin.Enabled.Value)
                return;
            for (int i = 0; i < Outfits.Length; i++)
            {
                Outfit outfit = Outfits[i];
                if (outfit.Texture == null || value == outfit.Texture)
                    continue;
                if (TextureNameMatches(value.name, outfit.SourceTexture))
                {
                    value = outfit.Texture;
                    return;
                }
            }
        }

        static bool TextureNameMatches(string textureName, string sourceName)
        {
            if (string.IsNullOrEmpty(textureName) || string.IsNullOrEmpty(sourceName))
                return false;
            return textureName == sourceName || textureName.StartsWith(sourceName + " ", StringComparison.Ordinal);
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

        static bool EnsureTextures(Component source)
        {
            if (AnyLoaded() && Time.unscaledTime < _nextFileCheck)
                return true;
            _nextFileCheck = Time.unscaledTime + 1f;

            string directory = SkinsDirectory();
            int ready = 0;
            for (int i = 0; i < Outfits.Length; i++)
            {
                if (EnsureOutfit(Outfits[i], directory, source))
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

        static bool EnsureOutfit(Outfit outfit, string directory, Component source)
        {
            string path = Path.Combine(directory, outfit.FileName);
            if (!File.Exists(path))
            {
                if (!TryDumpSample(outfit, path, source))
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
            _loggedApply = false;
            CustomPlayerSkinsPlugin.LogSource.LogInfo($"Loaded {outfit.FileName} ({texture.width}x{texture.height})");
            return true;
        }

        static Texture FindSourceTexture(Component root, string materialName)
        {
            if (root == null || string.IsNullOrEmpty(materialName))
                return null;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;
                Material[] materials = renderer.sharedMaterials;
                if (materials == null)
                    continue;
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    Material material = materials[slot];
                    if (material == null || !NameIs(material.name, materialName) || !material.HasProperty("_MainTex"))
                        continue;
                    Texture current = material.GetTexture("_MainTex");
                    if (_originals.TryGetValue(material.GetInstanceID(), out Texture original) && original != null)
                        current = original;
                    if (current != null)
                        return current;
                }
            }
            return null;
        }

        static bool NameIs(string materialName, string expected)
        {
            return materialName == expected || materialName.StartsWith(expected + " (", StringComparison.Ordinal);
        }

        static bool TryDumpSample(Outfit outfit, string path, Component root)
        {
            Texture source = FindSourceTexture(root, outfit.DumpMaterial);
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
