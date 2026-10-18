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
using Skopje.Modding;
using UnityEngine;

namespace Skopje.CustomWeaponSkins
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class CustomWeaponSkinsPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.customweaponskins";
        public const string PluginName = "Skopje Custom Weapon Skins";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<bool> Enabled;

        static Harmony _harmony;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Skin", "Enabled", true, "Replace each player weapon with its own PNG in the WeaponSkins folder next to this plugin.");

            _harmony = new Harmony(PluginGuid);
            var setTexture = AccessTools.Method(typeof(Material), "SetTexture", new[] { typeof(string), typeof(Texture) });
            if (setTexture != null)
                _harmony.Patch(setTexture, prefix: new HarmonyMethod(typeof(WeaponSkinPatches), nameof(WeaponSkinPatches.BeforeSetTexture)));
            var setTextureId = AccessTools.Method(typeof(Material), "SetTexture", new[] { typeof(int), typeof(Texture) });
            if (setTextureId != null)
                _harmony.Patch(setTextureId, prefix: new HarmonyMethod(typeof(WeaponSkinPatches), nameof(WeaponSkinPatches.BeforeSetTextureId)));

            _onPlayerStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(WeaponSkinReplacer.ApplyPlayer));
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Edit the PNGs in WeaponSkins. Each file is one weapon: kalash, m143, knife, lmg_heavy, lmg_medium, bozdogun, hammer, rusty_pipe, wrench, toxic_axe, electric_blade, udar, revolver, pupin, karab, osa, vector, gruev_shotgun, winko, skull, grom, snooper, spore_gun, tesla_gun, and frag_grenade.");
        }

        public override bool Unload()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();
            if (_onPlayerStarted != null)
                ModAPI.remove_OnPlayerStartedClient(_onPlayerStarted);
            WeaponSkinReplacer.Restore();
            return true;
        }
    }

    static class WeaponSkinPatches
    {
        public static void BeforeSetTexture(string name, ref Texture value)
        {
            if (name == "_MainTex")
                WeaponSkinReplacer.Redirect(ref value);
        }

        public static void BeforeSetTextureId(int nameID, ref Texture value)
        {
            if (nameID == Shader.PropertyToID("_MainTex"))
                WeaponSkinReplacer.Redirect(ref value);
        }
    }

    static class WeaponSkinReplacer
    {
        sealed class WeaponSkin
        {
            public string FileName;
            public string DumpMaterial;
            public string SourceTexture;
            public string[] Materials;
            public Texture2D Texture;
            public DateTime WriteTimeUtc;
        }

        // One file per weapon. First-person, item, scope, and charged variants share that weapon's albedo.
        static readonly WeaponSkin[] Weapons =
        {
            new WeaponSkin
            {
                FileName = "kalash.png",
                DumpMaterial = "MAT_Weapon_Kalash",
                SourceTexture = "DIF_Weapon_Kalash",
                Materials = new[] { "MAT_Weapon_Kalash", "MAT_Weapon_Kalash_FP", "MAT_Weapon_Kalash_Item" },
            },
            new WeaponSkin
            {
                FileName = "m143.png",
                DumpMaterial = "MAT_Weapon_M143",
                SourceTexture = "DIF_Weapon_M143",
                Materials = new[] { "MAT_Weapon_M143", "MAT_Weapon_M143_FP", "MAT_Weapon_M143_Item" },
            },
            new WeaponSkin
            {
                FileName = "knife.png",
                DumpMaterial = "MAT_Knife_FP",
                SourceTexture = "DIF_Knife",
                Materials = new[] { "MAT_Knife_FP" },
            },
            new WeaponSkin
            {
                FileName = "lmg_heavy.png",
                DumpMaterial = "MAT_Weapon_LMG_Heavy",
                SourceTexture = "DIF_Weapon_LMG_Heavy",
                Materials = new[] { "MAT_Weapon_LMG_Heavy", "MAT_Weapon_LMG_Heavy_FP", "MAT_Weapon_LMG_Heavy_Item" },
            },
            new WeaponSkin
            {
                FileName = "lmg_medium.png",
                DumpMaterial = "MAT_Weapon_LMGMedium",
                SourceTexture = "DIF_Weapon_LMGMedium",
                Materials = new[] { "MAT_Weapon_LMGMedium", "MAT_Weapon_LMGMedium_FP", "MAT_Weapon_LMGMedium_Item" },
            },
            new WeaponSkin
            {
                FileName = "bozdogun.png",
                DumpMaterial = "MAT_Weapon_Bozdogun",
                SourceTexture = "DIF_Weapon_Bozdogun",
                Materials = new[] { "MAT_Weapon_Bozdogun", "MAT_Weapon_Bozdogun_FP", "MAT_Weapon_Bozdogun_FP_Charged", "MAT_Weapon_Bozdogun_Item" },
            },
            new WeaponSkin
            {
                FileName = "hammer.png",
                DumpMaterial = "MAT_Weapon_Hammer",
                SourceTexture = "DIF_Weapon_Slab",
                Materials = new[] { "MAT_Weapon_Hammer", "MAT_Weapon_Hammer_FP", "MAT_Weapon_Hammer_Item" },
            },
            new WeaponSkin
            {
                FileName = "rusty_pipe.png",
                DumpMaterial = "MAT_Weapon_Rusty_Pipe",
                SourceTexture = "DIF_Rusty_Pipe",
                Materials = new[] { "MAT_Weapon_Rusty_Pipe", "MAT_Weapon_Rusty_Pipe_FP", "MAT_Weapon_Rusty_Pipe_Item" },
            },
            new WeaponSkin
            {
                FileName = "wrench.png",
                DumpMaterial = "MAT_Weapon_Wrench",
                SourceTexture = "DIF_Weapon_Wrench",
                Materials = new[] { "MAT_Weapon_Wrench", "MAT_Weapon_Wrench_FP", "MAT_Weapon_Wrench_Item" },
            },
            new WeaponSkin
            {
                FileName = "toxic_axe.png",
                DumpMaterial = "MAT_Weapon_Toxic_Axe",
                SourceTexture = "DIF_Toxic_Axe",
                Materials = new[] { "MAT_Weapon_Toxic_Axe", "MAT_Weapon_Toxic_Axe_FP" },
            },
            new WeaponSkin
            {
                FileName = "electric_blade.png",
                DumpMaterial = "MAT_Weapon_Electric_Blade",
                SourceTexture = "DIF_ElectricBlade",
                Materials = new[] { "MAT_Weapon_Electric_Blade", "MAT_Weapon_Electric_Blade_FP", "MAT_Weapon_Electric_Blade_Item" },
            },
            new WeaponSkin
            {
                FileName = "udar.png",
                DumpMaterial = "MAT_Weapon_UDAR",
                SourceTexture = "DIF_Weapon_UDAR",
                Materials = new[] { "MAT_Weapon_UDAR", "MAT_Weapon_UDAR_FP", "MAT_Weapon_UDAR_Item" },
            },
            new WeaponSkin
            {
                FileName = "revolver.png",
                DumpMaterial = "MAT_Weapon_Revolver",
                SourceTexture = "DIF_Weapon_Revolver",
                Materials = new[] { "MAT_Weapon_Revolver", "MAT_Weapon_Revolver_FP", "MAT_Weapon_Revolver_Item" },
            },
            new WeaponSkin
            {
                FileName = "pupin.png",
                DumpMaterial = "MAT_Pupin",
                SourceTexture = "DIF_Weapon_Pupin",
                Materials = new[] { "MAT_Pupin", "MAT_Pupin_FP", "MAT_Pupin_Item" },
            },
            new WeaponSkin
            {
                FileName = "karab.png",
                DumpMaterial = "MAT_Karab",
                SourceTexture = "DIF_Weapon_Karab",
                Materials = new[] { "MAT_Karab", "MAT_Karab_FP", "MAT_Karab_Item" },
            },
            new WeaponSkin
            {
                FileName = "osa.png",
                DumpMaterial = "MAT_Osa",
                SourceTexture = "DIF_Weapon_Osa",
                Materials = new[] { "MAT_Osa", "MAT_Osa_FP", "MAT_Osa_Item" },
            },
            new WeaponSkin
            {
                FileName = "vector.png",
                DumpMaterial = "MAT_Weapon_Vector",
                SourceTexture = "DIF_Weapon_Vector",
                Materials = new[] { "MAT_Weapon_Vector", "MAT_Weapon_Vector_FP", "MAT_Weapon_Vector_Item" },
            },
            new WeaponSkin
            {
                FileName = "gruev_shotgun.png",
                DumpMaterial = "MAT_GruevShotgun",
                SourceTexture = "DIF_Weapon_Gruev_Shotgun",
                Materials = new[] { "MAT_GruevShotgun", "MAT_GruevShotgun_FP", "MAT_GruevShotgun_Item" },
            },
            new WeaponSkin
            {
                FileName = "winko.png",
                DumpMaterial = "MAT_Weapon_Winko",
                SourceTexture = "DIF_Weapon_Winko",
                Materials = new[] { "MAT_Weapon_Winko", "MAT_Weapon_Winko_FP", "MAT_Weapon_Winko_Item" },
            },
            new WeaponSkin
            {
                FileName = "skull.png",
                DumpMaterial = "MAT_Weapon_Skull",
                SourceTexture = "DIF_Weapon_Skull",
                Materials = new[] { "MAT_Weapon_Skull", "MAT_Weapon_Skull_FP", "MAT_Weapon_Skull_Item", "MAT_Weapon_Skull_Empty" },
            },
            new WeaponSkin
            {
                FileName = "grom.png",
                DumpMaterial = "MAT_Weapon_Grom",
                SourceTexture = "DIF_Weapon_Grom",
                Materials = new[] { "MAT_Weapon_Grom", "MAT_Weapon_Grom_FP", "MAT_Weapon_Grom_Item" },
            },
            new WeaponSkin
            {
                FileName = "snooper.png",
                DumpMaterial = "MAT_Weapon_Snooper",
                SourceTexture = "DIF_Weapon_Snooper",
                Materials = new[] { "MAT_Weapon_Snooper", "MAT_Weapon_Snooper_FP", "MAT_Weapon_Snooper_Item", "MAT_Weapon_Snooper_Scope", "MAT_Weapon_Snooper_Scope_FP" },
            },
            new WeaponSkin
            {
                FileName = "spore_gun.png",
                DumpMaterial = "MAT_Weapon_SporeGun",
                SourceTexture = "DIF_Weapon_SporeGun",
                Materials = new[] { "MAT_Weapon_SporeGun", "MAT_Weapon_SporeGun_FP", "MAT_Weapon_SporeGun_Item" },
            },
            new WeaponSkin
            {
                FileName = "tesla_gun.png",
                DumpMaterial = "MAT_Weapon_TeslaGun",
                SourceTexture = "DIF_Weapon_Teslagun",
                Materials = new[] { "MAT_Weapon_TeslaGun", "MAT_Weapon_TeslaGun_FP", "MAT_Weapon_TeslaGun_Item" },
            },
            new WeaponSkin
            {
                FileName = "frag_grenade.png",
                DumpMaterial = "MAT_FragGrenade",
                SourceTexture = "DIF_FragGrenade",
                Materials = new[] { "MAT_FragGrenade" },
            },
        };

        static readonly Dictionary<int, Texture> _originals = new Dictionary<int, Texture>();
        static readonly Dictionary<int, Material> _materials = new Dictionary<int, Material>();
        static readonly Dictionary<int, Renderer> _renderers = new Dictionary<int, Renderer>();
        static readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        static float _nextFileCheck;
        static bool _loggedApply;
        static bool _applied;

        public static void ApplyPlayer(SkopjePlayer player)
        {
            if (!CustomWeaponSkinsPlugin.Enabled.Value)
            {
                if (_applied)
                {
                    RevertMaterials();
                    ClearRendererOverrides();
                    _applied = false;
                    _loggedApply = false;
                }
                return;
            }
            if (player == null || !EnsureTextures(player))
                return;

            int applied = ApplyPlayerWeapons(player);
            if (applied > 0)
            {
                _applied = true;
                if (!_loggedApply)
                {
                    _loggedApply = true;
                    CustomWeaponSkinsPlugin.LogSource.LogInfo($"Applied {applied} weapon material{(applied == 1 ? "" : "s")} from WeaponSkins.");
                }
            }
        }

        public static void Restore()
        {
            RevertMaterials();
            ClearRendererOverrides();
            _applied = false;
            _originals.Clear();
            _materials.Clear();
            _renderers.Clear();
            for (int i = 0; i < Weapons.Length; i++)
            {
                if (Weapons[i].Texture == null)
                    continue;
                UnityEngine.Object.Destroy(Weapons[i].Texture);
                Weapons[i].Texture = null;
            }
        }

        public static void Redirect(ref Texture value)
        {
            if (value == null || !CustomWeaponSkinsPlugin.Enabled.Value)
                return;
            for (int i = 0; i < Weapons.Length; i++)
            {
                WeaponSkin weapon = Weapons[i];
                if (weapon.Texture == null || value == weapon.Texture)
                    continue;
                if (TextureNameMatches(value.name, weapon.SourceTexture))
                {
                    value = weapon.Texture;
                    return;
                }
            }
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

        static int ApplyPlayerWeapons(SkopjePlayer player)
        {
            WeaponsController weapons = player.WeaponsController;
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
            Material[] materials = renderer.sharedMaterials;
            if (materials == null)
                return 0;

            int applied = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                WeaponSkin weapon = Resolve(materials[i]);
                if (weapon == null)
                    continue;
                applied += ApplyMaterial(materials[i]);
                _renderers[renderer.GetInstanceID()] = renderer;
                renderer.GetPropertyBlock(_block, i);
                _block.SetTexture("_MainTex", weapon.Texture);
                renderer.SetPropertyBlock(_block, i);
            }
            return applied;
        }

        static int ApplyMaterial(Material material)
        {
            WeaponSkin weapon = Resolve(material);
            if (weapon == null)
                return 0;

            int id = material.GetInstanceID();
            if (!_originals.ContainsKey(id))
            {
                Texture current = material.GetTexture("_MainTex");
                _originals[id] = current;
                _materials[id] = material;
                CopySampling(weapon.Texture, current);
            }

            if (material.GetTexture("_MainTex") == weapon.Texture)
                return 0;

            material.SetTexture("_MainTex", weapon.Texture);
            return 1;
        }

        static WeaponSkin Resolve(Material material)
        {
            if (material == null || !material.HasProperty("_MainTex"))
                return null;
            WeaponSkin byName = FindWeapon(material.name);
            if (byName != null && byName.Texture != null)
                return byName;

            Texture current = material.GetTexture("_MainTex");
            if (current == null)
                return null;
            for (int i = 0; i < Weapons.Length; i++)
            {
                WeaponSkin weapon = Weapons[i];
                if (weapon.Texture == null)
                    continue;
                if (current == weapon.Texture)
                    return weapon;
                if (TextureNameMatches(current.name, weapon.SourceTexture))
                    return weapon;
            }
            return null;
        }

        static bool TextureNameMatches(string textureName, string sourceName)
        {
            if (string.IsNullOrEmpty(textureName) || string.IsNullOrEmpty(sourceName))
                return false;
            return textureName == sourceName || textureName.StartsWith(sourceName + " ", StringComparison.Ordinal);
        }

        static WeaponSkin FindWeapon(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
                return null;
            for (int i = 0; i < Weapons.Length; i++)
            {
                string[] names = Weapons[i].Materials;
                for (int n = 0; n < names.Length; n++)
                {
                    string expected = names[n];
                    if (materialName == expected || materialName.StartsWith(expected + " (", StringComparison.Ordinal))
                        return Weapons[i];
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
            for (int i = 0; i < Weapons.Length; i++)
            {
                if (EnsureWeapon(Weapons[i], directory, source))
                    ready++;
            }

            if (ready == 0)
            {
                CustomWeaponSkinsPlugin.LogSource.LogWarning("No weapon PNGs are ready in " + directory);
                return false;
            }
            return true;
        }

        static bool AnyLoaded()
        {
            for (int i = 0; i < Weapons.Length; i++)
            {
                if (Weapons[i].Texture != null)
                    return true;
            }
            return false;
        }

        static bool EnsureWeapon(WeaponSkin weapon, string directory, Component source)
        {
            string path = Path.Combine(directory, weapon.FileName);
            if (!File.Exists(path))
            {
                if (!TryDumpSample(weapon, path, source))
                    return weapon.Texture != null;
            }

            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(path); }
            catch (Exception ex)
            {
                CustomWeaponSkinsPlugin.LogSource.LogError("Could not read " + weapon.FileName + ": " + ex.Message);
                return weapon.Texture != null;
            }

            if (weapon.Texture != null && weapon.WriteTimeUtc == writeTime)
                return true;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                CustomWeaponSkinsPlugin.LogSource.LogError("Could not read " + weapon.FileName + ": " + ex.Message);
                return weapon.Texture != null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            texture.name = "SkopjeCustomWeaponSkin_" + Path.GetFileNameWithoutExtension(weapon.FileName);
            texture.hideFlags = HideFlags.HideAndDontSave;
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                CustomWeaponSkinsPlugin.LogSource.LogError(weapon.FileName + " is not a readable image: " + path);
                return weapon.Texture != null;
            }

            if (weapon.Texture != null)
                UnityEngine.Object.Destroy(weapon.Texture);
            weapon.Texture = texture;
            weapon.WriteTimeUtc = writeTime;
            _loggedApply = false;
            CustomWeaponSkinsPlugin.LogSource.LogInfo($"Loaded {weapon.FileName} ({texture.width}x{texture.height})");
            return true;
        }

        static bool TryDumpSample(WeaponSkin weapon, string path, Component root)
        {
            Texture source = FindSourceTexture(root, weapon.DumpMaterial);
            if (source == null || IsCustomTexture(source))
                return false;

            byte[] png = EncodePng(source);
            if (png == null || png.Length == 0)
                return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
                CustomWeaponSkinsPlugin.LogSource.LogInfo("Dumped " + weapon.FileName + " from the game into WeaponSkins.");
                return true;
            }
            catch (Exception ex)
            {
                CustomWeaponSkinsPlugin.LogSource.LogError("Could not write " + weapon.FileName + ": " + ex.Message);
                return false;
            }
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
                    if (_originals.TryGetValue(material.GetInstanceID(), out Texture original) && original != null && !IsCustomTexture(original))
                        current = original;
                    if (current == null || IsCustomTexture(current))
                        continue;
                    return current;
                }
            }
            return null;
        }

        static bool NameIs(string materialName, string expected)
        {
            return materialName == expected || materialName.StartsWith(expected + " (", StringComparison.Ordinal);
        }

        static bool IsCustomTexture(Texture texture)
        {
            if (texture == null)
                return false;
            for (int i = 0; i < Weapons.Length; i++)
            {
                if (Weapons[i].Texture == texture)
                    return true;
            }
            return false;
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
                return Path.Combine(dllDir, "WeaponSkins");
            return Path.Combine(Paths.PluginPath, "WeaponSkins");
        }
    }
}
