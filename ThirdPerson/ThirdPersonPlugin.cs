using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Skopje.Modding;
using UnityEngine;
using UnityEngine.Rendering;

namespace Skopje.ThirdPerson
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ThirdPersonPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.thirdperson";
        public const string PluginName = "Skopje Third Person";
        public const string PluginVersion = "1.3.0";

        internal static ManualLogSource LogSource;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> FaceMovement;
        internal static ConfigEntry<float> Distance;
        internal static ConfigEntry<float> Shoulder;
        internal static ConfigEntry<float> Height;
        internal static ConfigEntry<float> PivotHeight;
        internal static ConfigEntry<float> AdsDistance;
        internal static ConfigEntry<float> AdsShoulder;
        internal static ConfigEntry<float> FovBonus;
        internal static ConfigEntry<float> AimDistance;

        static Harmony _harmony;
        static GameObject _driverHost;

        static Il2CppSystem.Action _onClientStarted;
        static Il2CppSystem.Action _onClientStopped;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStopped;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Camera", "Enabled", true, "Over-the-shoulder camera on the right. The body keeps its own facing so the camera can orbit around to the face.");
            FaceMovement = Config.Bind("Camera", "FaceMovement", false, "Turn the body toward the direction you move. Leave this off to keep the body still while the camera orbits.");
            Distance = Config.Bind("Camera", "Distance", 3.6f, "Meters behind the eyes.");
            Shoulder = Config.Bind("Camera", "Shoulder", 1.05f, "Meters to the right of the eyes. Kept even when a wall shortens the boom.");
            Height = Config.Bind("Camera", "Height", 0.4f, "Meters above the eyes.");
            PivotHeight = Config.Bind("Camera", "PivotHeight", 1.25f, "Unused. The view is offset from the eye camera so interiors stay aligned.");
            AdsDistance = Config.Bind("Camera", "AdsDistance", 2.1f, "Distance while aiming down sights.");
            AdsShoulder = Config.Bind("Camera", "AdsShoulder", 0.75f, "Right offset while aiming down sights.");
            FovBonus = Config.Bind("Camera", "FovBonus", 8f, "Extra field of view. Drops to zero while aiming.");
            AimDistance = Config.Bind("Camera", "AimDistance", 80f, "Crosshair trace length when nothing is under the reticle.");

            ClassInjector.RegisterTypeInIl2Cpp<ThirdPersonDriver>();

            _onClientStarted = ToIl2Cpp(new System.Action(ThirdPersonRig.OnClientStarted));
            _onClientStopped = ToIl2Cpp(new System.Action(ThirdPersonRig.OnClientStopped));
            _onPlayerStarted = ToIl2Cpp<SkopjePlayer>(new System.Action<SkopjePlayer>(ThirdPersonRig.OnPlayerStarted));
            _onPlayerStopped = ToIl2Cpp<SkopjePlayer>(new System.Action<SkopjePlayer>(ThirdPersonRig.OnPlayerStopped));

            ModAPI.add_OnClientStarted(_onClientStarted);
            ModAPI.add_OnClientStopped(_onClientStopped);
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);
            ModAPI.add_OnPlayerStoppedClient(_onPlayerStopped);

            _driverHost = new GameObject("SkopjeThirdPerson");
            UnityEngine.Object.DontDestroyOnLoad(_driverHost);
            _driverHost.hideFlags = HideFlags.HideAndDontSave;
            _driverHost.AddComponent<ThirdPersonDriver>();

            _harmony = new Harmony(PluginGuid);
            Patch(AccessTools.Method(typeof(RenderPipelineManager), "BeginCameraRendering"), nameof(ThirdPersonPatches.AfterBeginCamera));
            Patch(AccessTools.Method(typeof(RenderPipelineManager), "EndCameraRendering"), nameof(ThirdPersonPatches.AfterEndCamera));
            Patch(AccessTools.Method(typeof(SkopjeCharacterController), "UpdateRotation"), nameof(ThirdPersonPatches.AfterBodyRotation), nameof(ThirdPersonPatches.BeforeBodyRotation));

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Config: {Config.ConfigFilePath}");
        }

        public override bool Unload()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();
            ThirdPersonRig.Shutdown();
            if (_driverHost != null)
            {
                UnityEngine.Object.Destroy(_driverHost);
                _driverHost = null;
            }

            return true;
        }

        static void Patch(MethodInfo method, string postfix, string prefix = null)
        {
            if (method == null)
            {
                LogSource.LogWarning("Third person hook was not found: " + (postfix ?? prefix));
                return;
            }

            _harmony.Patch(
                method,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(ThirdPersonPatches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(ThirdPersonPatches), postfix));
        }

        static Il2CppSystem.Action ToIl2Cpp(System.Action action)
        {
            return DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action);
        }

        static Il2CppSystem.Action<T> ToIl2Cpp<T>(System.Action<T> action)
        {
            return DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T>>(action);
        }
    }

    public static class ThirdPersonPatches
    {
        public static void AfterBeginCamera(ScriptableRenderContext context, Camera camera)
        {
            ThirdPersonRig.PushView(camera);
        }

        public static void AfterEndCamera(ScriptableRenderContext context, Camera camera)
        {
            ThirdPersonRig.PopView(camera);
        }

        public static void BeforeBodyRotation(SkopjeCharacterController __instance, ref Quaternion currentRotation)
        {
            ThirdPersonRig.RememberBody(__instance, currentRotation);
        }

        // The motor turns the capsule toward the look yaw. Put the previous facing back so mouse-look only orbits the camera.
        public static void AfterBodyRotation(SkopjeCharacterController __instance, ref Quaternion currentRotation, float deltaTime)
        {
            ThirdPersonRig.HoldBodyFacing(__instance, ref currentRotation, deltaTime);
        }

    }
}
