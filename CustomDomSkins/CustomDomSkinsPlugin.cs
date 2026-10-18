using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Skopje.Modding;
using UnityEngine;

namespace Skopje.CustomDomSkins
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class CustomDomSkinsPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.customdomskins";
        public const string PluginName = "Skopje Custom DOM Skins";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<bool> Enabled;

        static Il2CppSystem.Action<SkopjeGameMode> _onGameModeStarted;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Skin", "Enabled", true, "Replace the DOM bus exterior with the PNGs in the DomSkins folder next to this plugin.");

            _onGameModeStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjeGameMode>>(new Action<SkopjeGameMode>(OnGameModeStarted));
            ModAPI.add_OnGameModeStartedClient(_onGameModeStarted);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Edit DomSkins/singleplayer.png and DomSkins/multiplayer.png. Those files are the dumped outer-shell textures. Interior, wheels, and lights are left alone.");
        }

        public override bool Unload()
        {
            if (_onGameModeStarted != null)
                ModAPI.remove_OnGameModeStartedClient(_onGameModeStarted);
            DomSkinReplacer.Restore();
            return true;
        }

        static void OnGameModeStarted(SkopjeGameMode gameMode)
        {
            if (gameMode == null)
                return;

            SkopjeVehicle bus = null;
            try
            {
                bus = gameMode.Bus;
            }
            catch (Exception ex)
            {
                LogSource.LogWarning("Could not read the DOM from the game mode: " + ex.Message);
                return;
            }

            if (bus == null)
            {
                LogSource.LogInfo("Game mode started without a DOM, so the exterior skin was not applied.");
                return;
            }

            DomSkinReplacer.Apply(bus);
        }
    }

    static class DomSkinReplacer
    {
        sealed class Shell
        {
            public string FileName;
            public string DumpMaterial;
            public string SourceTexture;
            public string MaterialName;
            public Texture2D Texture;
            public DateTime WriteTimeUtc;
        }

        // The two buses do not share a UV layout. Each file is that bus's outer shell only.
        static readonly Shell[] Shells =
        {
            new Shell
            {
                FileName = "singleplayer.png",
                DumpMaterial = "MAT_BusOutside",
                SourceTexture = "DIF_DOM_OuterShell",
                MaterialName = "MAT_BusOutside",
            },
            new Shell
            {
                FileName = "multiplayer.png",
                DumpMaterial = "MAT_DOMmp_Outside",
                SourceTexture = "DIF_DOMmp_Outside",
                MaterialName = "MAT_DOMmp_Outside",
            },
        };

        static readonly Dictionary<int, Texture> _originals = new Dictionary<int, Texture>();
        static readonly Dictionary<int, Material> _materials = new Dictionary<int, Material>();
        static readonly Dictionary<int, Renderer> _renderers = new Dictionary<int, Renderer>();
        static readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        static float _nextFileCheck;
        static bool _loggedApply;
        static bool _applied;

        public static void Apply(SkopjeVehicle bus)
        {
            if (!CustomDomSkinsPlugin.Enabled.Value)
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
            if (bus == null || !EnsureTextures(bus))
                return;

            int applied = ApplyUnder(bus.transform);
            if (applied > 0)
            {
                _applied = true;
                if (!_loggedApply)
                {
                    _loggedApply = true;
                    CustomDomSkinsPlugin.LogSource.LogInfo($"Applied {applied} DOM exterior material{(applied == 1 ? "" : "s")} from DomSkins.");
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
            for (int i = 0; i < Shells.Length; i++)
            {
                if (Shells[i].Texture == null)
                    continue;
                UnityEngine.Object.Destroy(Shells[i].Texture);
                Shells[i].Texture = null;
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
                Shell shell = Resolve(materials[i]);
                if (shell == null)
                    continue;
                applied += ApplyMaterial(materials[i]);
                _renderers[renderer.GetInstanceID()] = renderer;
                renderer.GetPropertyBlock(_block, i);
                _block.SetTexture("_MainTex", shell.Texture);
                renderer.SetPropertyBlock(_block, i);
            }
            return applied;
        }

        static int ApplyMaterial(Material material)
        {
            Shell shell = Resolve(material);
            if (shell == null)
                return 0;

            int id = material.GetInstanceID();
            if (!_originals.ContainsKey(id))
            {
                Texture current = material.GetTexture("_MainTex");
                _originals[id] = current;
                _materials[id] = material;
                CopySampling(shell.Texture, current);
            }

            if (material.GetTexture("_MainTex") == shell.Texture)
                return 0;

            material.SetTexture("_MainTex", shell.Texture);
            return 1;
        }

        static Shell Resolve(Material material)
        {
            if (material == null || !material.HasProperty("_MainTex"))
                return null;

            string materialName = BaseName(material.name);
            for (int i = 0; i < Shells.Length; i++)
            {
                Shell shell = Shells[i];
                if (shell.Texture == null)
                    continue;
                if (materialName == shell.MaterialName)
                    return shell;
            }

            Texture current = material.GetTexture("_MainTex");
            if (current == null)
                return null;
            for (int i = 0; i < Shells.Length; i++)
            {
                Shell shell = Shells[i];
                if (shell.Texture == null)
                    continue;
                if (current == shell.Texture)
                    return shell;
                if (BaseName(current.name) == shell.SourceTexture)
                    return shell;
            }
            return null;
        }

        static string BaseName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            int cut = name.IndexOf(" (", StringComparison.Ordinal);
            if (cut > 0)
                return name.Substring(0, cut);
            return name;
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
            for (int i = 0; i < Shells.Length; i++)
            {
                if (EnsureShell(Shells[i], directory, source))
                    ready++;
            }

            if (ready == 0)
            {
                CustomDomSkinsPlugin.LogSource.LogWarning("No DOM shell PNGs are ready in " + directory);
                return false;
            }
            return true;
        }

        static bool AnyLoaded()
        {
            for (int i = 0; i < Shells.Length; i++)
            {
                if (Shells[i].Texture != null)
                    return true;
            }
            return false;
        }

        static bool EnsureShell(Shell shell, string directory, Component source)
        {
            string path = Path.Combine(directory, shell.FileName);
            if (!File.Exists(path) && !TryDumpSample(shell, path, source))
                return shell.Texture != null;

            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(path); }
            catch (Exception ex)
            {
                CustomDomSkinsPlugin.LogSource.LogError("Could not read " + shell.FileName + ": " + ex.Message);
                return shell.Texture != null;
            }

            if (shell.Texture != null && shell.WriteTimeUtc == writeTime)
                return true;

            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex)
            {
                CustomDomSkinsPlugin.LogSource.LogError("Could not read " + shell.FileName + ": " + ex.Message);
                return shell.Texture != null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            texture.name = "SkopjeCustomDomSkin_" + Path.GetFileNameWithoutExtension(shell.FileName);
            texture.hideFlags = HideFlags.HideAndDontSave;
            if (!ImageConversion.LoadImage(texture, bytes))
            {
                UnityEngine.Object.Destroy(texture);
                CustomDomSkinsPlugin.LogSource.LogError(shell.FileName + " is not a readable image: " + path);
                return shell.Texture != null;
            }

            if (shell.Texture != null)
                UnityEngine.Object.Destroy(shell.Texture);
            shell.Texture = texture;
            shell.WriteTimeUtc = writeTime;
            _loggedApply = false;
            CustomDomSkinsPlugin.LogSource.LogInfo($"Loaded {shell.FileName} ({texture.width}x{texture.height})");
            return true;
        }

        static Texture FindSourceTexture(Component root, string materialName, Texture replacement)
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
                    if (material == null || BaseName(material.name) != materialName || !material.HasProperty("_MainTex"))
                        continue;
                    Texture current = material.GetTexture("_MainTex");
                    if (_originals.TryGetValue(material.GetInstanceID(), out Texture original) && original != null)
                        current = original;
                    if (current == null || current == replacement)
                        continue;
                    return current;
                }
            }
            return null;
        }

        static bool TryDumpSample(Shell shell, string path, Component root)
        {
            Texture source = FindSourceTexture(root, shell.DumpMaterial, shell.Texture);
            if (source == null)
                return false;

            byte[] png = EncodePng(source);
            if (png == null || png.Length == 0)
                return false;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, png);
                CustomDomSkinsPlugin.LogSource.LogInfo("Dumped " + shell.FileName + " from the DOM outer shell into DomSkins.");
                return true;
            }
            catch (Exception ex)
            {
                CustomDomSkinsPlugin.LogSource.LogError("Could not write " + shell.FileName + ": " + ex.Message);
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
                return Path.Combine(dllDir, "DomSkins");
            return Path.Combine(Paths.PluginPath, "DomSkins");
        }
    }
}
