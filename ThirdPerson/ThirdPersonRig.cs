using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skopje.ThirdPerson
{
    public class ThirdPersonDriver : MonoBehaviour
    {
        void Update()
        {
            ThirdPersonRig.TickBody();
        }

        void LateUpdate()
        {
            ThirdPersonRig.Apply(ThirdPersonRig.LocalCamera);
        }
    }

    static class ThirdPersonRig
    {
        const float MoveThreshold = 0.2f;
        const float ExternalTurn = 35f;

        static readonly List<Renderer> Hidden = new List<Renderer>();
        static readonly RaycastHit[] Hits = new RaycastHit[24];

        internal static SkopjeFPSCamera LocalCamera;

        static SkopjePlayer _player;
        static bool _holding;
        static bool _hasFacing;
        static Quaternion _facing;
        static Quaternion _incomingFacing;
        static bool _hasIncoming;
        static bool _hasLook;
        static Quaternion _look = Quaternion.identity;
        static bool _hasPlacement;
        static Vector3 _placedPos;
        static float _baseFov;
        static float _boom = 2.7f;
        static float _ads;
        static bool _capturedIk;
        static bool _aimWasEnabled;
        static bool _lookWasEnabled;
        static bool _followWasEnabled;
        static bool _disabledFollow;
        static float _nextScan;
        static int _errors;
        static bool _logged;

        public static void OnClientStarted()
        {
        }

        public static void OnClientStopped()
        {
            Release();
            _player = null;
            LocalCamera = null;
        }

        public static void OnPlayerStarted(SkopjePlayer player)
        {
            if (player != null && player.IsOwner)
                _player = player;
        }

        public static void OnPlayerStopped(SkopjePlayer player)
        {
            if (_player == player)
            {
                Release();
                _player = null;
                LocalCamera = null;
            }
        }

        public static void Shutdown()
        {
            Release();
            _player = null;
            LocalCamera = null;
        }

        public static void TickBody()
        {
            if (!CanControl(_player))
            {
                if (_holding)
                    Release();
                return;
            }

            try
            {
                HoldPresentation(_player);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        public static void Apply(SkopjeFPSCamera fps)
        {
            if (fps == null || !CanControl(fps.Player))
                return;

            Camera cam = fps.mainCamera != null ? fps.mainCamera : Camera.main;
            if (cam == null)
                return;

            LocalCamera = fps;
            if (_player == null)
                _player = fps.Player;

            try
            {
                Place(fps, cam);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        public static void RememberBody(SkopjeCharacterController controller, Quaternion currentRotation)
        {
            if (!CanControl(controller != null ? controller.Player : null))
            {
                _hasIncoming = false;
                return;
            }

            _incomingFacing = FlatYaw(currentRotation);
            _hasIncoming = true;
        }

        public static void HoldBodyFacing(SkopjeCharacterController controller, ref Quaternion currentRotation, float deltaTime)
        {
            if (!_hasIncoming || controller == null || !CanControl(controller.Player))
                return;

            Quaternion held = _hasFacing ? _facing : _incomingFacing;
            if (!_hasFacing || Quaternion.Angle(held, _incomingFacing) > ExternalTurn)
                held = _incomingFacing;

            if (ThirdPersonPlugin.FaceMovement.Value)
            {
                Vector3 move = controller.moveInputVector;
                move.y = 0f;
                if (move.sqrMagnitude > MoveThreshold * MoveThreshold)
                {
                    float sharp = controller.OrientationSharpness;
                    if (sharp < 0.01f)
                        sharp = 12f;
                    float blend = 1f - Mathf.Exp(-sharp * Mathf.Max(0f, deltaTime));
                    held = Quaternion.Slerp(held, Quaternion.LookRotation(move.normalized, Vector3.up), blend);
                }
            }

            _facing = FlatYaw(held);
            _hasFacing = true;
            currentRotation = _facing;
        }

        static void Place(SkopjeFPSCamera fps, Camera cam)
        {
            Quaternion look = ReadLook(fps, cam);
            _look = look;
            _hasLook = true;

            bool fresh = !_hasPlacement || (cam.transform.position - _placedPos).sqrMagnitude > 0.0004f;
            if (fresh)
                _baseFov = cam.fieldOfView;

            float adsTarget = fps.adsMode ? 1f : 0f;
            _ads = Mathf.MoveTowards(_ads, adsTarget, Time.deltaTime / 0.12f);

            float distance = Mathf.Lerp(Clamp(ThirdPersonPlugin.Distance.Value, 0.8f, 8f), Clamp(ThirdPersonPlugin.AdsDistance.Value, 0.6f, 6f), _ads);
            float shoulder = Mathf.Lerp(Clamp(ThirdPersonPlugin.Shoulder.Value, 0f, 2f), Clamp(ThirdPersonPlugin.AdsShoulder.Value, 0f, 1.5f), _ads);
            float height = Clamp(ThirdPersonPlugin.Height.Value, -1f, 2f);
            float boom = new Vector3(shoulder, height, -distance).magnitude;
            if (!_hasPlacement)
                _boom = boom;
            _boom = Mathf.Lerp(_boom, boom, 1f - Mathf.Exp(-16f * Time.deltaTime));

            Vector3 pivot = Pivot(fps.Player);
            Vector3 offset = look * new Vector3(shoulder, height, -distance);
            if (offset.sqrMagnitude < 0.0001f)
                offset = look * Vector3.back;
            Vector3 dir = offset.normalized;
            float allowed = PullIn(fps.Player, pivot, dir, _boom);
            Vector3 camPos = pivot + dir * allowed;

            Vector3 aim = AimPoint(fps.Player, pivot, look * Vector3.forward);
            Vector3 toAim = aim - camPos;
            Vector3 up = look * Vector3.up;
            if (toAim.sqrMagnitude < 0.0001f || Mathf.Abs(Vector3.Dot(toAim.normalized, up.normalized)) > 0.995f)
                cam.transform.rotation = look;
            else
                cam.transform.rotation = Quaternion.LookRotation(toAim, up);

            cam.transform.position = camPos;
            cam.fieldOfView = _baseFov + Clamp(ThirdPersonPlugin.FovBonus.Value, 0f, 40f) * (1f - _ads);
            _placedPos = camPos;
            _hasPlacement = true;
            KeepBodyYaw(fps.Player);

            if (!_logged)
            {
                _logged = true;
                ThirdPersonPlugin.LogSource.LogInfo("Right-shoulder camera is active. The body does not turn with the mouse.");
            }
        }

        static void KeepBodyYaw(SkopjePlayer player)
        {
            if (!_hasFacing || player == null)
                return;

            Transform body = player.transform;
            Vector3 up = body.up;
            Vector3 forward = Vector3.ProjectOnPlane(_facing * Vector3.forward, up);
            if (forward.sqrMagnitude < 0.0001f)
                return;

            body.rotation = Quaternion.LookRotation(forward.normalized, up);
        }

        static Quaternion ReadLook(SkopjeFPSCamera fps, Camera cam)
        {
            if (fps.VCamera != null && fps.VCamera.transform != cam.transform)
                return fps.VCamera.transform.rotation;
            if (fps.SocketTransform != null && fps.SocketTransform != cam.transform)
                return fps.SocketTransform.rotation;
            if (_hasLook && _hasPlacement && (cam.transform.position - _placedPos).sqrMagnitude < 0.0004f)
                return _look;
            return cam.transform.rotation;
        }

        static Vector3 Pivot(SkopjePlayer player)
        {
            float height = Clamp(ThirdPersonPlugin.PivotHeight.Value, 0.4f, 2.4f);
            var body = player.SkopjeCharacterController;
            if (body != null)
            {
                if (body.standingHeight > 0.5f)
                    height = Mathf.Min(height, body.standingHeight * 0.78f);
                float crouch = body.CrouchedHeightMult > 0.05f ? body.CrouchedHeightMult : 0.55f;
                height *= Mathf.Lerp(1f, crouch, Mathf.Clamp01(body.currentCrouch01));
            }

            return player.transform.position + Vector3.up * height;
        }

        static Vector3 AimPoint(SkopjePlayer player, Vector3 origin, Vector3 direction)
        {
            float reach = Clamp(ThirdPersonPlugin.AimDistance.Value, 5f, 200f);
            if (direction.sqrMagnitude < 0.0001f)
                direction = Vector3.forward;
            direction.Normalize();

            int count = Physics.RaycastNonAlloc(origin, direction, Hits, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            Vector3 point = origin + direction * reach;
            for (int i = 0; i < count; i++)
            {
                if (Hits[i].collider == null || IsOwn(player, Hits[i].collider))
                    continue;
                if (Hits[i].distance < best)
                {
                    best = Hits[i].distance;
                    point = Hits[i].point;
                }
            }

            return point;
        }

        static float PullIn(SkopjePlayer player, Vector3 origin, Vector3 direction, float distance)
        {
            const float radius = 0.18f;
            int count = Physics.SphereCastNonAlloc(origin, radius, direction, Hits, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = distance;
            for (int i = 0; i < count; i++)
            {
                if (Hits[i].collider == null || IsOwn(player, Hits[i].collider))
                    continue;
                float room = Mathf.Max(0.35f, Hits[i].distance);
                if (room < best)
                    best = room;
            }

            return best;
        }

        static void HoldPresentation(SkopjePlayer player)
        {
            GameObject third = player.ThirdPerson;
            if (third != null && !third.activeSelf)
                third.SetActive(true);

            GameObject pack = player.ThirdPersonBackpackParent;
            if (pack != null && !pack.activeSelf)
                pack.SetActive(true);

            var anim = player.TPAnimationController;
            if (anim != null)
            {
                if (!_capturedIk)
                {
                    _aimWasEnabled = anim.AimAtMotor != null && anim.AimAtMotor.enabled;
                    _lookWasEnabled = anim.LookAtMotor != null && anim.LookAtMotor.enabled;
                    _capturedIk = true;
                }

                if (anim.AimAtMotor != null)
                    anim.AimAtMotor.enabled = false;
                if (anim.LookAtMotor != null)
                    anim.LookAtMotor.enabled = false;
            }

            var follow = player.ThirdPersonFollowCam;
            if (follow != null && follow.Look != null && follow.Look.enabled)
            {
                _followWasEnabled = true;
                follow.Look.enabled = false;
                _disabledFollow = true;
            }

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.25f;
                RebuildHidden(player);
            }

            for (int i = 0; i < Hidden.Count; i++)
            {
                if (Hidden[i] != null)
                    Hidden[i].forceRenderingOff = true;
            }

            _holding = true;
        }

        static void RebuildHidden(SkopjePlayer player)
        {
            var next = new List<Renderer>();
            Transform third = player.ThirdPerson != null ? player.ThirdPerson.transform : null;
            Collect(player.FirstPerson, third, next);

            var weapons = player.WeaponsController;
            if (weapons != null)
            {
                if (weapons.Socket != null)
                    Collect(weapons.Socket.gameObject, third, next);
                if (weapons.ThrowSocket != null)
                    Collect(weapons.ThrowSocket.gameObject, third, next);
            }

            for (int i = 0; i < Hidden.Count; i++)
            {
                Renderer old = Hidden[i];
                if (old != null && !next.Contains(old))
                    old.forceRenderingOff = false;
            }

            Hidden.Clear();
            Hidden.AddRange(next);
        }

        static void Collect(GameObject root, Transform thirdPerson, List<Renderer> into)
        {
            if (root == null)
                return;
            if (thirdPerson != null && (root.transform == thirdPerson || root.transform.IsChildOf(thirdPerson)))
                return;

            Renderer[] found = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < found.Length; i++)
            {
                Renderer renderer = found[i];
                if (renderer == null)
                    continue;
                if (thirdPerson != null && renderer.transform.IsChildOf(thirdPerson))
                    continue;
                if (!into.Contains(renderer))
                    into.Add(renderer);
            }
        }

        static void Release()
        {
            for (int i = 0; i < Hidden.Count; i++)
            {
                if (Hidden[i] != null)
                    Hidden[i].forceRenderingOff = false;
            }

            Hidden.Clear();

            if (_player != null)
            {
                var anim = _player.TPAnimationController;
                if (_capturedIk && anim != null)
                {
                    if (anim.AimAtMotor != null)
                        anim.AimAtMotor.enabled = _aimWasEnabled;
                    if (anim.LookAtMotor != null)
                        anim.LookAtMotor.enabled = _lookWasEnabled;
                }

                if (_disabledFollow && _player.ThirdPersonFollowCam != null && _player.ThirdPersonFollowCam.Look != null)
                    _player.ThirdPersonFollowCam.Look.enabled = _followWasEnabled;

                try
                {
                    _player.RefreshActiveModels();
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }

            _capturedIk = false;
            _disabledFollow = false;
            _holding = false;
            _hasFacing = false;
            _hasIncoming = false;
            _hasPlacement = false;
            _hasLook = false;
            _ads = 0f;
            _logged = false;
        }

        static bool CanControl(SkopjePlayer player)
        {
            if (!ThirdPersonPlugin.Enabled.Value || player == null || !player.IsOwner)
                return false;
            if (player.IsDead || player.IsFallen || player.IsSpectating || player.IsPaused || player.InInventory)
                return false;
            if (player.InsideVehicle != null)
                return false;

            var cine = player._cinematicController;
            if (cine != null && cine.PlayingCinematic != null && cine.PlayingCinematic.PlayingCinematic != null)
                return false;

            return player.SkopjeFPSCamera != null;
        }

        static bool IsOwn(SkopjePlayer player, Collider collider)
        {
            if (player == null || collider == null)
                return false;
            Transform transform = collider.transform;
            Transform root = player.transform;
            return transform == root || transform.IsChildOf(root);
        }

        static Quaternion FlatYaw(Quaternion rotation)
        {
            Vector3 forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.ProjectOnPlane(rotation * Vector3.up, Vector3.up);
            if (forward.sqrMagnitude < 0.0001f)
                forward = Vector3.forward;
            return Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        static float Clamp(float value, float min, float max)
        {
            return Mathf.Clamp(value, min, max);
        }

        static void Fail(Exception ex)
        {
            if (_errors++ < 5 && ThirdPersonPlugin.LogSource != null)
                ThirdPersonPlugin.LogSource.LogWarning("Third person failed: " + ex.Message);
        }
    }
}
