using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using KinematicCharacterController;
using Skopje.Modding;
using UnityEngine;

namespace PioneerSize
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class PioneerSizePlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.pioneersize";
        public const string PluginName = "PioneerSize";
        public const string PluginVersion = "1.0.2";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<float> Height;
        internal static ConfigEntry<float> SpeedMultiplier;

        static Harmony _harmony;
        static GameObject _host;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStartedClient;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStoppedClient;

        public override void Load()
        {
            LogSource = Log;
            Height = Config.Bind("Size", "Height", 1.8f, new ConfigDescription(
                "Standing height in meters. Crouch keeps the game's own crouch fraction of this height.",
                new AcceptableValueRange<float>(0.5f, 3f)));
            SpeedMultiplier = Config.Bind("Size", "SpeedMultiplier", 1f, new ConfigDescription(
                "Multiplies walk, sprint, crouch, air, jump, and dash speed. 1 is normal.",
                new AcceptableValueRange<float>(0.05f, 10f)));

            ClassInjector.RegisterTypeInIl2Cpp<PioneerSizeDriver>();

            _onPlayerStartedClient = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(PioneerSizeApply.OnPlayerStartedClient));
            _onPlayerStoppedClient = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(PioneerSizeApply.OnPlayerStoppedClient));
            ModAPI.add_OnPlayerStartedClient(_onPlayerStartedClient);
            ModAPI.add_OnPlayerStoppedClient(_onPlayerStoppedClient);

            _harmony = new Harmony(PluginGuid);
            Patch(AccessTools.Method(typeof(SkopjePlayer), "OnStartClient"), nameof(PioneerSizeApply.AfterPlayer));
            Patch(AccessTools.Method(typeof(SkopjeCharacterController), "Start"), nameof(PioneerSizeApply.AfterController));
            Patch(AccessTools.Method(typeof(SkopjeCharacterController), "ResetController"), nameof(PioneerSizeApply.AfterReset));
            Patch(AccessTools.Method(typeof(SkopjePlayer), "RefreshActiveModels"), nameof(PioneerSizeApply.AfterPlayer));
            Patch(AccessTools.Method(typeof(SkopjePlayer), "RefreshPlayerCollider"), nameof(PioneerSizeApply.AfterPlayer));

            _host = new GameObject("PioneerSize");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
            _host.AddComponent<PioneerSizeDriver>();

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Height {Height.Value} m, speed {SpeedMultiplier.Value}. Config: {Config.ConfigFilePath}");
        }

        public override bool Unload()
        {
            PioneerSizeApply.RestoreAll();
            if (_harmony != null)
                _harmony.UnpatchSelf();
            if (_onPlayerStartedClient != null)
                ModAPI.remove_OnPlayerStartedClient(_onPlayerStartedClient);
            if (_onPlayerStoppedClient != null)
                ModAPI.remove_OnPlayerStoppedClient(_onPlayerStoppedClient);
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }

            return true;
        }

        static void Patch(MethodInfo method, string postfix)
        {
            if (method == null)
            {
                LogSource.LogWarning("PioneerSize hook was not found: " + postfix);
                return;
            }

            _harmony.Patch(method, postfix: new HarmonyMethod(typeof(PioneerSizeApply), postfix));
        }
    }

    public class PioneerSizeDriver : MonoBehaviour
    {
        void Update()
        {
            PioneerSizeApply.Pump();
        }
    }

    static class PioneerSizeApply
    {
        const float MinHeight = 0.5f;
        const float MaxHeight = 3f;
        const float MinSpeed = 0.05f;
        const float MaxSpeed = 10f;

        sealed class Body
        {
            public float Walk;
            public float Air;
            public float AirAccel;
            public float Crouch;
            public float Sprint;
            public float Jump;
            public float Dash;
            public float Height;
            public float Radius;
            public float Step;
            public float YOffset;
            public Vector3 Head;
            public Vector3 Chest;
            public Vector3 Center;
            public bool HeadLocal;
            public bool ChestLocal;
            public bool CenterLocal;
            public float AppliedScale = -1f;
            public float AppliedHeight = -1f;
            public float AppliedSpeed = -1f;
            public bool Logged;
        }

        sealed class Avatar
        {
            public Vector3 Mesh = Vector3.one;
            public bool HasMesh;
            public Vector3 First = Vector3.one;
            public bool HasFirst;
            public Vector3 Third = Vector3.one;
            public bool HasThird;
            public float FirstPersonPlayerScale = 1f;
            public Vector3 CameraLocal;
            public bool HasCamera;
            public bool CameraUnderVisual;
            public float OwnerHeight;
            public float OwnerRadius;
            public Vector3 OwnerCenter;
            public bool HasOwner;
            public bool OwnerIsMotor;
            public float AppliedScale = -1f;
        }

        static readonly Dictionary<IntPtr, Body> Bodies = new Dictionary<IntPtr, Body>();
        static readonly Dictionary<IntPtr, Avatar> Avatars = new Dictionary<IntPtr, Avatar>();
        static readonly List<SkopjePlayer> Watching = new List<SkopjePlayer>();
        static bool _restoring;
        static int _errors;

        public static void OnPlayerStartedClient(SkopjePlayer player)
        {
            if (!Alive(player))
                return;

            Watch(player);
            bool applied = Apply(player, false);
            if (PioneerSizePlugin.LogSource != null)
            {
                PioneerSizePlugin.LogSource.LogInfo(applied
                    ? $"OnPlayerStartedClient applied size to player {player.PlayerIdx}."
                    : $"OnPlayerStartedClient queued player {player.PlayerIdx}. The body is not ready yet.");
            }
        }

        public static void OnPlayerStoppedClient(SkopjePlayer player)
        {
            IntPtr key = Key(player);
            if (key == IntPtr.Zero)
                return;

            for (int i = Watching.Count - 1; i >= 0; i--)
            {
                if (Key(Watching[i]) == key)
                    Watching.RemoveAt(i);
            }

            Avatars.Remove(key);
            SkopjeCharacterController controller = player.SkopjeCharacterController;
            IntPtr bodyKey = Key(controller);
            if (bodyKey != IntPtr.Zero)
                Bodies.Remove(bodyKey);
        }

        public static void AfterPlayer(SkopjePlayer __instance)
        {
            if (!Alive(__instance))
                return;

            Watch(__instance);
            Apply(__instance, false);
        }

        public static void AfterController(SkopjeCharacterController __instance)
        {
            if (!Alive(__instance))
                return;

            SkopjePlayer player = __instance.Player;
            if (!Alive(player))
                return;

            Watch(player);
            Apply(player, false);
        }

        public static void AfterReset(SkopjeCharacterController __instance)
        {
            if (!Alive(__instance))
                return;

            SkopjePlayer player = __instance.Player;
            if (!Alive(player))
                return;

            Watch(player);
            Apply(player, true);
        }

        public static void Pump()
        {
            for (int i = Watching.Count - 1; i >= 0; i--)
            {
                SkopjePlayer player = Watching[i];
                if (!Alive(player))
                {
                    Watching.RemoveAt(i);
                    continue;
                }

                Apply(player, false);
            }
        }

        public static void RestoreAll()
        {
            _restoring = true;
            try
            {
                for (int i = 0; i < Watching.Count; i++)
                {
                    if (Alive(Watching[i]))
                        Apply(Watching[i], true);
                }
            }
            finally
            {
                _restoring = false;
                Watching.Clear();
                Bodies.Clear();
                Avatars.Clear();
            }
        }

        static bool Apply(SkopjePlayer player, bool reset)
        {
            if (!Alive(player))
                return false;

            try
            {
                SkopjeCharacterController controller = player.SkopjeCharacterController;
                if (!Alive(controller))
                    return false;

                IntPtr id = Key(controller);
                if (!Bodies.TryGetValue(id, out Body body))
                {
                    if (!Ready(controller))
                        return false;

                    body = Capture(controller);
                    Bodies[id] = body;
                }

                float height = _restoring ? body.Height : ReadHeight();
                float speed = ReadSpeed();
                float scale = body.Height > 0.01f ? height / body.Height : 1f;
                bool sizeDirty = reset || body.AppliedScale < 0f || Mathf.Abs(body.AppliedHeight - height) > 0.0001f;
                bool speedDirty = reset || body.AppliedSpeed < 0f || Mathf.Abs(body.AppliedSpeed - speed) > 0.0001f;
                if (!sizeDirty && !speedDirty)
                    return true;

                if (speedDirty)
                    WriteSpeeds(controller, body, speed);
                if (sizeDirty)
                    WriteSize(controller, player, body, scale, reset || body.AppliedScale < 0f);

                body.AppliedScale = scale;
                body.AppliedHeight = height;
                body.AppliedSpeed = speed;

                if (!body.Logged && PioneerSizePlugin.LogSource != null)
                {
                    body.Logged = true;
                    PioneerSizePlugin.LogSource.LogInfo($"Pioneer height {height:0.##} m, stock {body.Height:0.##} m, speed x{speed:0.##} on player {player.PlayerIdx}.");
                }

                return true;
            }
            catch (Exception ex)
            {
                LogOnce(ex);
                return false;
            }
        }

        static void WriteSpeeds(SkopjeCharacterController controller, Body body, float speed)
        {
            controller.MaxStableMoveSpeed = body.Walk * speed;
            controller.MaxAirMoveSpeed = body.Air * speed;
            controller.AirAccelerationSpeed = body.AirAccel * speed;
            controller.CrouchMoveSpeed = body.Crouch * speed;
            controller.SprintSpeed = body.Sprint * speed;
            controller.JumpSpeed = body.Jump * speed;
            controller.DashSpeed = body.Dash * speed;
        }

        // Standing height is the crouch system's full-height reference. The live capsule is scaled
        // from wherever the stance currently is, so a crouch in progress is not popped back up.
        static void WriteSize(SkopjeCharacterController controller, SkopjePlayer player, Body body, float scale, bool fromVanilla)
        {
            float ratio = fromVanilla || body.AppliedScale <= 0.0001f ? scale : scale / body.AppliedScale;
            controller.standingHeight = body.Height * scale;
            controller.playerRadius = body.Radius * scale;

            KinematicCharacterMotor motor = controller.Motor;
            if (Alive(motor))
            {
                motor.MaxStepHeight = body.Step * scale;
                float radius = fromVanilla ? body.Radius * scale : motor.CapsuleRadius * ratio;
                float height = fromVanilla ? body.Height * scale : motor.CapsuleHeight * ratio;
                float yOffset = fromVanilla
                    ? (body.YOffset > 0.001f ? body.YOffset * scale : height * 0.5f)
                    : motor.CapsuleYOffset * ratio;
                motor.SetCapsuleDimensions(radius, height, yOffset);
            }

            if (body.HeadLocal)
                controller.originalHeadPosition = (fromVanilla ? body.Head : controller.originalHeadPosition) * (fromVanilla ? scale : ratio);
            if (body.ChestLocal)
                controller.originalChestPosition = (fromVanilla ? body.Chest : controller.originalChestPosition) * (fromVanilla ? scale : ratio);
            if (body.CenterLocal)
                controller.originalCenterPosition = (fromVanilla ? body.Center : controller.originalCenterPosition) * (fromVanilla ? scale : ratio);

            ScaleVisuals(player, controller, scale, ratio, fromVanilla);
        }

        static void ScaleVisuals(SkopjePlayer player, SkopjeCharacterController controller, float scale, float ratio, bool fromVanilla)
        {
            IntPtr id = Key(player);
            if (id == IntPtr.Zero || Mathf.Abs(ratio - 1f) < 0.0001f)
                return;

            if (!Avatars.TryGetValue(id, out Avatar avatar))
            {
                avatar = new Avatar();
                Avatars[id] = avatar;
            }

            CaptureAvatar(player, avatar);

            Transform mesh = controller.MeshRoot;
            if (avatar.HasMesh && mesh != null)
                mesh.localScale = fromVanilla ? avatar.Mesh * scale : mesh.localScale * ratio;

            if (avatar.HasFirst && player.FirstPerson != null && player.FirstPerson.transform.localScale.sqrMagnitude > 0.0001f)
                player.FirstPerson.transform.localScale = fromVanilla ? avatar.First * scale : player.FirstPerson.transform.localScale * ratio;

            if (avatar.HasThird && player.ThirdPerson != null && player.ThirdPerson.transform.localScale.sqrMagnitude > 0.0001f)
                player.ThirdPerson.transform.localScale = fromVanilla ? avatar.Third * scale : player.ThirdPerson.transform.localScale * ratio;

            player.FirstPersonPlayerScale = fromVanilla ? avatar.FirstPersonPlayerScale * scale : player.FirstPersonPlayerScale * ratio;

            if (avatar.HasCamera && !avatar.CameraUnderVisual && player.SkopjeFPSCamera != null)
            {
                Transform cam = player.SkopjeFPSCamera.transform;
                cam.localPosition = fromVanilla ? avatar.CameraLocal * scale : cam.localPosition * ratio;
            }

            CapsuleCollider owner = OwnerCapsule(player);
            if (avatar.HasOwner && owner != null && !avatar.OwnerIsMotor)
            {
                owner.height = fromVanilla ? avatar.OwnerHeight * scale : owner.height * ratio;
                owner.radius = fromVanilla ? avatar.OwnerRadius * scale : owner.radius * ratio;
                owner.center = fromVanilla ? avatar.OwnerCenter * scale : owner.center * ratio;
            }
        }

        static void CaptureAvatar(SkopjePlayer player, Avatar avatar)
        {
            Transform mesh = Alive(player.SkopjeCharacterController) ? player.SkopjeCharacterController.MeshRoot : null;
            if (!avatar.HasMesh && mesh != null && mesh.localScale.sqrMagnitude > 0.0001f)
            {
                avatar.Mesh = mesh.localScale;
                avatar.HasMesh = true;
            }

            if (!avatar.HasFirst && player.FirstPerson != null && player.FirstPerson.transform.localScale.sqrMagnitude > 0.0001f)
            {
                avatar.First = player.FirstPerson.transform.localScale;
                avatar.HasFirst = true;
            }

            if (!avatar.HasThird && player.ThirdPerson != null && player.ThirdPerson.transform.localScale.sqrMagnitude > 0.0001f)
            {
                avatar.Third = player.ThirdPerson.transform.localScale;
                avatar.HasThird = true;
            }

            if (avatar.AppliedScale < 0f)
            {
                avatar.FirstPersonPlayerScale = player.FirstPersonPlayerScale;
                avatar.AppliedScale = 1f;
            }

            if (!avatar.HasCamera && player.SkopjeFPSCamera != null)
            {
                avatar.CameraLocal = player.SkopjeFPSCamera.transform.localPosition;
                avatar.HasCamera = true;
                avatar.CameraUnderVisual = IsUnder(player.SkopjeFPSCamera.transform, mesh)
                    || (player.FirstPerson != null && IsUnder(player.SkopjeFPSCamera.transform, player.FirstPerson.transform))
                    || (player.ThirdPerson != null && IsUnder(player.SkopjeFPSCamera.transform, player.ThirdPerson.transform));
            }

            if (!avatar.HasOwner)
            {
                CapsuleCollider owner = OwnerCapsule(player);
                KinematicCharacterMotor motor = Alive(player.SkopjeCharacterController) ? player.SkopjeCharacterController.Motor : null;
                if (owner != null)
                {
                    avatar.OwnerHeight = owner.height;
                    avatar.OwnerRadius = owner.radius;
                    avatar.OwnerCenter = owner.center;
                    avatar.HasOwner = true;
                    avatar.OwnerIsMotor = Alive(motor) && Same(motor.Capsule, owner);
                }
            }
        }

        static Body Capture(SkopjeCharacterController controller)
        {
            KinematicCharacterMotor motor = controller.Motor;
            float height = controller.standingHeight;
            float radius = controller.playerRadius;
            float yOffset = 0f;
            float step = 0.3f;
            if (Alive(motor))
            {
                if (height <= 0.01f)
                    height = motor.CapsuleHeight;
                if (radius <= 0.01f)
                    radius = motor.CapsuleRadius;
                yOffset = motor.CapsuleYOffset;
                step = motor.MaxStepHeight;
            }

            return new Body
            {
                Walk = controller.MaxStableMoveSpeed,
                Air = controller.MaxAirMoveSpeed,
                AirAccel = controller.AirAccelerationSpeed,
                Crouch = controller.CrouchMoveSpeed,
                Sprint = controller.SprintSpeed,
                Jump = controller.JumpSpeed,
                Dash = controller.DashSpeed,
                Height = height,
                Radius = radius,
                Step = step,
                YOffset = yOffset,
                Head = controller.originalHeadPosition,
                Chest = controller.originalChestPosition,
                Center = controller.originalCenterPosition,
                HeadLocal = IsLocalOffset(controller.originalHeadPosition),
                ChestLocal = IsLocalOffset(controller.originalChestPosition),
                CenterLocal = IsLocalOffset(controller.originalCenterPosition),
            };
        }

        static bool Ready(SkopjeCharacterController controller)
        {
            if (!Alive(controller.Motor))
                return false;
            if (controller.MaxStableMoveSpeed <= 0.01f)
                return false;
            return controller.standingHeight > 0.01f || controller.Motor.CapsuleHeight > 0.01f;
        }

        static void Watch(SkopjePlayer player)
        {
            IntPtr key = Key(player);
            if (key == IntPtr.Zero)
                return;

            for (int i = 0; i < Watching.Count; i++)
            {
                if (Key(Watching[i]) == key)
                    return;
            }

            Watching.Add(player);
        }

        static bool Alive(Il2CppObjectBase obj)
        {
            return obj != null && obj.Pointer != IntPtr.Zero;
        }

        static IntPtr Key(Il2CppObjectBase obj)
        {
            return obj == null ? IntPtr.Zero : obj.Pointer;
        }

        static bool Same(UnityEngine.Object left, UnityEngine.Object right)
        {
            IntPtr a = Key(left as Il2CppObjectBase);
            IntPtr b = Key(right as Il2CppObjectBase);
            return a != IntPtr.Zero && a == b;
        }

        static bool IsLocalOffset(Vector3 value)
        {
            return value.sqrMagnitude > 0.0001f && value.sqrMagnitude < 25f && value.y > 0f && value.y < 3f;
        }

        static bool IsUnder(Transform child, Transform parent)
        {
            if (child == null || parent == null)
                return false;

            for (Transform cursor = child; cursor != null; cursor = cursor.parent)
            {
                if (cursor == parent)
                    return true;
            }

            return false;
        }

        static CapsuleCollider OwnerCapsule(SkopjePlayer player)
        {
            if (player.OwnerCollider == null)
                return null;
            return player.OwnerCollider.TryCast<CapsuleCollider>();
        }

        static float ReadHeight()
        {
            float value = PioneerSizePlugin.Height != null ? PioneerSizePlugin.Height.Value : 1.8f;
            return Mathf.Clamp(value, MinHeight, MaxHeight);
        }

        static float ReadSpeed()
        {
            if (_restoring)
                return 1f;
            float value = PioneerSizePlugin.SpeedMultiplier != null ? PioneerSizePlugin.SpeedMultiplier.Value : 1f;
            return Mathf.Clamp(value, MinSpeed, MaxSpeed);
        }

        static void LogOnce(Exception ex)
        {
            if (_errors++ > 4 || PioneerSizePlugin.LogSource == null)
                return;
            PioneerSizePlugin.LogSource.LogError("PioneerSize failed to resize the player:\n" + ex);
        }
    }
}
