using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
            ThirdPersonRig.PrepareView();
        }
    }

    static class ThirdPersonRig
    {
        const float MoveThreshold = 0.2f;
        const float ExternalTurn = 35f;

        static readonly List<Renderer> BodyRenderers = new List<Renderer>();
        static readonly List<Renderer> ArmRenderers = new List<Renderer>();
        static readonly List<Renderer> ShadowOnly = new List<Renderer>();
        static readonly List<Collider> InteractColliders = new List<Collider>();
        static readonly RaycastHit[] Hits = new RaycastHit[24];

        static SkopjePlayer _player;
        static bool _holding;
        static bool _hasFacing;
        static Quaternion _facing;
        static Quaternion _incomingFacing;
        static bool _hasIncoming;
        static float _boom = 2.7f;
        static float _ads;
        static bool _capturedIk;
        static bool _aimWasEnabled;
        static bool _lookWasEnabled;
        static float _nextScan;
        static int _errors;
        static bool _logged;
        static int _viewId;
        static bool _hasView;
        static Vector3 _viewPos;
        static Quaternion _viewRot;
        static float _viewFov;
        static bool _pushed;
        static Vector3 _savedPos;
        static Quaternion _savedRot;
        static float _savedFov;
        static bool _maskSaved;
        static int _savedMask;
        static int _bodyLayers;
        static GameObject _thirdRoot;
        static Vector3 _thirdScale;
        static bool _scaledThird;
        static bool _control;
        static IntPtr _controllerPtr;
        static Camera _viewCamera;
        static Vector3 _movePlanar;
        static float _turnSharpness;
        static float _nextColliderScan;

        public static void OnClientStarted()
        {
        }

        public static void OnClientStopped()
        {
            Release();
            _player = null;
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
            }
        }

        public static void Shutdown()
        {
            Release();
            _player = null;
        }

        public static void TickBody()
        {
            RefreshControl();
            if (!_control)
            {
                if (_holding)
                    Release();
                return;
            }

            try
            {
                if (Time.unscaledTime >= _nextColliderScan)
                {
                    _nextColliderScan = Time.unscaledTime + 0.5f;
                    SilenceBodyColliders();
                }

                Present(_player, null);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        // UpdateRotation runs inside an IL2CPP call. Touching IsOwner from here re-enters the runtime and crashes the process.
        public static void RememberBody(SkopjeCharacterController controller, Quaternion currentRotation)
        {
            if (!_control || controller is null || controller.Pointer != _controllerPtr)
            {
                _hasIncoming = false;
                return;
            }

            _incomingFacing = FlatYaw(currentRotation);
            _hasIncoming = true;
        }

        public static void HoldBodyFacing(SkopjeCharacterController controller, ref Quaternion currentRotation, float deltaTime)
        {
            if (!_hasIncoming || !_control || controller is null || controller.Pointer != _controllerPtr)
                return;

            Quaternion held = _hasFacing ? _facing : _incomingFacing;
            if (!_hasFacing || Quaternion.Angle(held, _incomingFacing) > ExternalTurn)
                held = _incomingFacing;

            if (ThirdPersonPlugin.FaceMovement.Value && _movePlanar.sqrMagnitude > MoveThreshold * MoveThreshold)
            {
                float sharp = _turnSharpness > 0.01f ? _turnSharpness : 12f;
                float blend = 1f - Mathf.Exp(-sharp * Mathf.Max(0f, deltaTime));
                held = Quaternion.Slerp(held, Quaternion.LookRotation(_movePlanar.normalized, Vector3.up), blend);
            }

            _facing = FlatYaw(held);
            _hasFacing = true;
            currentRotation = _facing;
        }

        static void RefreshControl()
        {
            _control = false;
            _controllerPtr = IntPtr.Zero;
            if (!CanControl(_player))
                return;

            SkopjeCharacterController body = _player.SkopjeCharacterController;
            SkopjeFPSCamera camera = _player.SkopjeFPSCamera;
            if (body is null || camera is null || body.Pointer == IntPtr.Zero || camera.Pointer == IntPtr.Zero)
                return;

            _controllerPtr = body.Pointer;
            _movePlanar = body.moveInputVector;
            _movePlanar.y = 0f;
            _turnSharpness = body.OrientationSharpness;
            _control = true;
        }

        public static void PrepareView()
        {
            if (_pushed && _viewCamera != null)
            {
                _viewCamera.transform.SetPositionAndRotation(_savedPos, _savedRot);
                _viewCamera.fieldOfView = _savedFov;
                _pushed = false;
            }

            _hasView = false;
            _viewId = 0;
            if (!_control || _player is null)
                return;

            SkopjeFPSCamera fps = _player.SkopjeFPSCamera;
            if (fps is null)
                return;

            Camera cam = fps.mainCamera != null ? fps.mainCamera : Camera.main;
            if (cam == null)
                return;

            try
            {
                // Offset from the live eye camera. Offset interiors move that camera, not the root position.
                Quaternion look = ReadLook(fps, cam);
                Vector3 eye = cam.transform.position;
                float adsTarget = fps.adsMode ? 1f : 0f;
                _ads = Mathf.MoveTowards(_ads, adsTarget, Time.deltaTime / 0.12f);

                float distance = Mathf.Lerp(Clamp(ThirdPersonPlugin.Distance.Value, 0.8f, 8f), Clamp(ThirdPersonPlugin.AdsDistance.Value, 0.6f, 6f), _ads);
                float shoulder = Mathf.Lerp(Clamp(ThirdPersonPlugin.Shoulder.Value, 0f, 2.5f), Clamp(ThirdPersonPlugin.AdsShoulder.Value, 0f, 2f), _ads);
                float height = Clamp(ThirdPersonPlugin.Height.Value, -1f, 2f);
                if (_boom <= 0.01f)
                    _boom = distance;
                _boom = Mathf.Lerp(_boom, distance, 1f - Mathf.Exp(-16f * Time.deltaTime));

                Vector3 side = eye + look * new Vector3(shoulder, height, 0f);
                Vector3 back = look * Vector3.back;
                float allowed = PullIn(_player, side, back, _boom);
                Vector3 camPos = side + back * allowed;
                Vector3 aim = AimPoint(_player, eye, look * Vector3.forward);
                Vector3 toAim = aim - camPos;
                Vector3 up = look * Vector3.up;

                _viewPos = camPos;
                _viewFov = cam.fieldOfView + Clamp(ThirdPersonPlugin.FovBonus.Value, 0f, 40f) * (1f - _ads);
                if (toAim.sqrMagnitude < 0.0001f || up.sqrMagnitude < 0.0001f || Mathf.Abs(Vector3.Dot(toAim.normalized, up.normalized)) > 0.995f)
                    _viewRot = look;
                else
                    _viewRot = Quaternion.LookRotation(toAim, up);

                _viewId = cam.GetInstanceID();
                _viewCamera = cam;
                _hasView = true;
                Present(_player, cam);

                if (!_logged)
                {
                    _logged = true;
                    ThirdPersonPlugin.LogSource.LogInfo("Right-shoulder view is active. World streaming still uses the eye camera.");
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        // Render-only. The gameplay camera stays on the eyes so chunk streaming does not follow the shoulder.
        public static void PushView(Camera camera)
        {
            if (!_hasView || camera == null || camera.GetInstanceID() != _viewId || _pushed)
                return;

            _savedPos = camera.transform.position;
            _savedRot = camera.transform.rotation;
            _savedFov = camera.fieldOfView;
            camera.transform.SetPositionAndRotation(_viewPos, _viewRot);
            camera.fieldOfView = _viewFov;
            if (_maskSaved)
                camera.cullingMask |= _bodyLayers;
            RevealCached();
            _pushed = true;
        }

        public static void PopView(Camera camera)
        {
            if (!_pushed || camera == null || camera.GetInstanceID() != _viewId)
                return;

            camera.transform.SetPositionAndRotation(_savedPos, _savedRot);
            camera.fieldOfView = _savedFov;
            _pushed = false;
        }

        static Quaternion ReadLook(SkopjeFPSCamera fps, Camera cam)
        {
            if (fps.VCamera != null && fps.VCamera.transform != cam.transform)
                return fps.VCamera.transform.rotation;
            if (fps.SocketTransform != null && fps.SocketTransform != cam.transform)
                return fps.SocketTransform.rotation;
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

        static void Present(SkopjePlayer player, Camera cam)
        {
            GameObject third = player.ThirdPerson;
            if (third != null)
            {
                if (!third.activeSelf)
                    third.SetActive(true);
                if (!_scaledThird && third.transform.localScale.sqrMagnitude < 0.0001f)
                {
                    _thirdScale = third.transform.localScale;
                    third.transform.localScale = Vector3.one;
                    _scaledThird = true;
                    _thirdRoot = third;
                }
            }

            if (player.ThirdPersonBackpackParent != null && !player.ThirdPersonBackpackParent.activeSelf)
                player.ThirdPersonBackpackParent.SetActive(true);

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

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.25f;
                CacheVisuals(player);
            }

            RevealCached();
            if (cam != null)
                IncludeBodyLayers(cam);

            _holding = true;
        }

        static void CacheVisuals(SkopjePlayer player)
        {
            RestoreArms();
            BodyRenderers.Clear();
            ArmRenderers.Clear();
            _bodyLayers = 0;

            Transform third = player.ThirdPerson != null ? player.ThirdPerson.transform : null;
            if (player.ThirdPersonRenderer != null)
                AddBody(player.ThirdPersonRenderer);
            if (third != null)
            {
                Renderer[] found = third.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < found.Length; i++)
                    AddBody(found[i]);
            }

            if (player.SkopjeCharacterController != null && player.SkopjeCharacterController.MeshRoot != null)
            {
                Renderer[] found = player.SkopjeCharacterController.MeshRoot.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < found.Length; i++)
                {
                    Renderer renderer = found[i];
                    if (renderer == null)
                        continue;
                    if (third != null && (renderer.transform == third || renderer.transform.IsChildOf(third)))
                        AddBody(renderer);
                    else if (player.FirstPerson == null || !renderer.transform.IsChildOf(player.FirstPerson.transform))
                        AddBody(renderer);
                }
            }

            CollectArms(player.FirstPerson, third);
        }

        static void AddBody(Renderer renderer)
        {
            if (renderer == null || BodyRenderers.Contains(renderer))
                return;

            BodyRenderers.Add(renderer);
            _bodyLayers |= 1 << renderer.gameObject.layer;
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly && !ShadowOnly.Contains(renderer))
            {
                ShadowOnly.Add(renderer);
                renderer.shadowCastingMode = ShadowCastingMode.On;
            }
        }

        static void CollectArms(GameObject root, Transform thirdPerson)
        {
            if (root == null)
                return;
            if (thirdPerson != null && (root.transform == thirdPerson || root.transform.IsChildOf(thirdPerson)))
                return;

            Renderer[] found = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < found.Length; i++)
            {
                Renderer renderer = found[i];
                if (renderer == null || BodyRenderers.Contains(renderer) || ArmRenderers.Contains(renderer))
                    continue;
                if (thirdPerson != null && renderer.transform.IsChildOf(thirdPerson))
                    continue;
                ArmRenderers.Add(renderer);
            }
        }

        static void RevealCached()
        {
            if (_thirdRoot != null && !_thirdRoot.activeSelf)
                _thirdRoot.SetActive(true);

            for (int i = 0; i < BodyRenderers.Count; i++)
            {
                Renderer renderer = BodyRenderers[i];
                if (renderer == null)
                    continue;
                renderer.forceRenderingOff = false;
                renderer.enabled = true;
                if (renderer is SkinnedMeshRenderer skin)
                    skin.updateWhenOffscreen = true;
            }

            for (int i = 0; i < ArmRenderers.Count; i++)
            {
                if (ArmRenderers[i] != null)
                    ArmRenderers[i].forceRenderingOff = true;
            }
        }

        static void IncludeBodyLayers(Camera cam)
        {
            if (_bodyLayers == 0)
                return;
            if (!_maskSaved)
            {
                _savedMask = cam.cullingMask;
                _maskSaved = true;
            }

            cam.cullingMask |= _bodyLayers;
        }

        static void SilenceBodyColliders()
        {
            if (_player == null || _player.ThirdPerson == null)
                return;

            Collider owner = _player.OwnerCollider;
            Collider[] found = _player.ThirdPerson.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < found.Length; i++)
            {
                Collider collider = found[i];
                if (collider == null || collider.isTrigger || collider == owner || !collider.enabled)
                    continue;
                if (InteractColliders.Contains(collider))
                    continue;

                collider.enabled = false;
                InteractColliders.Add(collider);
            }
        }

        static void RestoreBodyColliders()
        {
            for (int i = 0; i < InteractColliders.Count; i++)
            {
                if (InteractColliders[i] != null)
                    InteractColliders[i].enabled = true;
            }

            InteractColliders.Clear();
        }

        static void RestoreArms()
        {
            for (int i = 0; i < ArmRenderers.Count; i++)
            {
                if (ArmRenderers[i] != null)
                    ArmRenderers[i].forceRenderingOff = false;
            }
        }

        static void Release()
        {
            if (_pushed && _viewCamera != null)
            {
                _viewCamera.transform.SetPositionAndRotation(_savedPos, _savedRot);
                _viewCamera.fieldOfView = _savedFov;
            }

            if (_maskSaved && _viewCamera != null)
                _viewCamera.cullingMask = _savedMask;

            for (int i = 0; i < ShadowOnly.Count; i++)
            {
                if (ShadowOnly[i] != null)
                    ShadowOnly[i].shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }

            if (_scaledThird && _thirdRoot != null)
                _thirdRoot.transform.localScale = _thirdScale;

            RestoreBodyColliders();
            RestoreArms();
            BodyRenderers.Clear();
            ArmRenderers.Clear();
            ShadowOnly.Clear();

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
            _holding = false;
            _hasFacing = false;
            _hasIncoming = false;
            _ads = 0f;
            _logged = false;
            _control = false;
            _controllerPtr = IntPtr.Zero;
            _hasView = false;
            _pushed = false;
            _maskSaved = false;
            _scaledThird = false;
            _thirdRoot = null;
            _viewCamera = null;
            _bodyLayers = 0;
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
