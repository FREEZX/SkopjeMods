using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeePeeMod
{
    static class PeeVisual
    {
        const int Steps = 20;
        const int Droplets = 10;
        const float StepTime = 0.04f;
        const float Speed = 7.5f;
        const float SplatterLife = 0.25f;
        static readonly Color PeeColor = new Color(1f, 0.92f, 0.05f, 1f);

        static readonly Dictionary<int, Stream> _streams = new Dictionary<int, Stream>();
        static readonly Dictionary<int, ushort> _seq = new Dictionary<int, ushort>();
        static readonly List<Puddle> _puddles = new List<Puddle>();
        static readonly List<int> _dropIds = new List<int>();
        static readonly Vector3[] _points = new Vector3[Steps];

        static Mesh _sphere;
        static Mesh _cube;
        static Material _material;
        static MaterialPropertyBlock _block;

        sealed class Stream
        {
            public int Id;
            public SkopjePlayer Player;
            public bool Local;
            public Vector3 Aim;
            public float Heard;
            public Transform Hips;
            public bool SearchedHips;
            public GameObject Root;
            public LineRenderer Line;
            public Transform[] Drops;
            public float[] Seeds;
        }

        sealed class Puddle
        {
            public GameObject Object;
            public float Born;
            public Vector3 Scale;
        }

        public static Vector3 AimOf(SkopjePlayer player)
        {
            if (player != null && player.IsOwner && player.SkopjeFPSCamera != null && player.SkopjeFPSCamera.mainCamera != null)
                return player.SkopjeFPSCamera.mainCamera.transform.forward;
            if (player != null && player.IsOwner && Camera.main != null)
                return Camera.main.transform.forward;

            Transform body = Body(player);
            if (body == null)
                return Vector3.forward;
            return (body.forward + Vector3.down * 0.45f).normalized;
        }

        public static void ShowLocal(SkopjePlayer player, Vector3 aim)
        {
            Show(player, aim, true);
        }

        public static void ShowRemote(SkopjePlayer player, Vector3 aim)
        {
            Show(player, aim, false);
        }

        public static bool Accept(int objectId, ushort seq)
        {
            ushort last;
            if (!_seq.TryGetValue(objectId, out last))
            {
                _seq[objectId] = seq;
                return true;
            }

            ushort delta = (ushort)(seq - last);
            if (delta == 0 || delta >= 32768)
                return false;

            _seq[objectId] = seq;
            return true;
        }

        public static void Forget(int objectId)
        {
            _seq.Remove(objectId);
        }

        public static void Hide(int objectId)
        {
            Stream stream;
            if (!_streams.TryGetValue(objectId, out stream))
                return;
            _streams.Remove(objectId);
            DestroyStream(stream);
        }

        public static void Clear()
        {
            foreach (Stream stream in _streams.Values)
                DestroyStream(stream);
            _streams.Clear();
            _seq.Clear();

            for (int i = 0; i < _puddles.Count; i++)
            {
                if (_puddles[i].Object != null)
                    UnityEngine.Object.Destroy(_puddles[i].Object);
            }

            _puddles.Clear();
        }

        public static void Tick()
        {
            float now = Time.unscaledTime;
            _dropIds.Clear();
            foreach (KeyValuePair<int, Stream> pair in _streams)
            {
                Stream stream = pair.Value;
                if (stream.Player == null)
                {
                    _dropIds.Add(pair.Key);
                    continue;
                }

                if (!stream.Local && now - stream.Heard > 0.5f)
                {
                    _dropIds.Add(pair.Key);
                    continue;
                }

                Draw(stream);
            }

            for (int i = 0; i < _dropIds.Count; i++)
                Hide(_dropIds[i]);

            for (int i = _puddles.Count - 1; i >= 0; i--)
            {
                Puddle puddle = _puddles[i];
                if (puddle.Object == null)
                {
                    _puddles.RemoveAt(i);
                    continue;
                }

                float age = now - puddle.Born;
                if (age >= SplatterLife)
                {
                    UnityEngine.Object.Destroy(puddle.Object);
                    _puddles.RemoveAt(i);
                    continue;
                }

                float shrink = 1f - (age / SplatterLife);
                puddle.Object.transform.localScale = puddle.Scale * Mathf.Max(0.2f, shrink);
            }
        }

        static void Show(SkopjePlayer player, Vector3 aim, bool local)
        {
            if (player == null || player.NetworkObject == null || !player.NetworkObject.IsSpawned)
                return;
            if (aim.sqrMagnitude < 0.0001f)
                aim = Vector3.forward;
            else
                aim.Normalize();

            Stream stream;
            if (!_streams.TryGetValue(player.ObjectId, out stream) || stream == null)
            {
                stream = Create(player);
                _streams[player.ObjectId] = stream;
            }

            stream.Player = player;
            stream.Local = local;
            stream.Aim = aim;
            stream.Heard = Time.unscaledTime;
        }

        static Stream Create(SkopjePlayer player)
        {
            EnsureArt();
            var stream = new Stream();
            stream.Id = player.ObjectId;
            stream.Player = player;
            stream.Root = new GameObject("PeeStream");
            if (PeePeePlugin.Host != null)
                stream.Root.transform.SetParent(PeePeePlugin.Host.transform, false);

            var lineObject = new GameObject("Stream");
            lineObject.transform.SetParent(stream.Root.transform, false);
            stream.Line = lineObject.AddComponent<LineRenderer>();
            stream.Line.positionCount = 0;
            stream.Line.startWidth = 0.035f;
            stream.Line.endWidth = 0.018f;
            stream.Line.numCapVertices = 4;
            stream.Line.numCornerVertices = 3;
            stream.Line.useWorldSpace = true;
            stream.Line.shadowCastingMode = ShadowCastingMode.Off;
            stream.Line.receiveShadows = false;
            stream.Line.lightProbeUsage = LightProbeUsage.Off;
            stream.Line.reflectionProbeUsage = ReflectionProbeUsage.Off;
            if (_material != null)
            {
                stream.Line.material = _material;
                Paint(stream.Line.material);
            }
            stream.Line.startColor = PeeColor;
            stream.Line.endColor = PeeColor;
            Paint(stream.Line);

            stream.Drops = new Transform[Droplets];
            stream.Seeds = new float[Droplets];
            for (int i = 0; i < Droplets; i++)
            {
                stream.Drops[i] = MakeDrop(stream.Root.transform, 0.04f);
                stream.Seeds[i] = i / (float)Droplets;
            }

            return stream;
        }

        static void Draw(Stream stream)
        {
            if (stream.Line == null)
                return;

            Vector3 aim = stream.Aim.sqrMagnitude > 0.0001f ? stream.Aim.normalized : Vector3.forward;
            Vector3 origin = Origin(stream, aim);
            Vector3 velocity = aim * Speed;
            Vector3 pos = origin;
            int count = 0;
            bool landed = false;
            RaycastHit landing = default;

            for (int i = 0; i < Steps; i++)
            {
                Vector3 next = pos + velocity * StepTime + 0.5f * Physics.gravity * (StepTime * StepTime);
                velocity += Physics.gravity * StepTime;
                RaycastHit hit;
                if (HitWorld(pos, next, stream.Player.transform, out hit))
                {
                    _points[i] = hit.point;
                    count = i + 1;
                    landed = true;
                    landing = hit;
                    break;
                }

                _points[i] = next;
                pos = next;
                count = i + 1;
            }

            if (count < 2)
                return;

            stream.Line.positionCount = count;
            for (int i = 0; i < count; i++)
                stream.Line.SetPosition(i, _points[i]);

            float now = Time.time;
            for (int i = 0; i < stream.Drops.Length; i++)
            {
                if (stream.Drops[i] == null)
                    continue;
                float travel = Mathf.Repeat(now * 1.7f + stream.Seeds[i], 1f);
                Vector3 at = Along(_points, count, travel);
                Vector3 side = Vector3.Cross(aim, Vector3.up);
                if (side.sqrMagnitude < 0.0001f)
                    side = Vector3.right;
                side.Normalize();
                at += side * Mathf.Sin((now + stream.Seeds[i]) * 16f) * 0.015f;
                stream.Drops[i].position = at;
                float size = Mathf.Lerp(0.025f, 0.048f, travel);
                stream.Drops[i].localScale = new Vector3(size, size, size);
            }

            if (landed)
                SpawnPuddle(landing);
        }

        static Vector3 Origin(Stream stream, Vector3 aim)
        {
            SkopjePlayer player = stream.Player;
            if (player != null && player.IsOwner && !BodyVisible(player))
            {
                Camera cam = null;
                if (player.SkopjeFPSCamera != null)
                    cam = player.SkopjeFPSCamera.mainCamera;
                if (cam == null)
                    cam = Camera.main;
                if (cam != null)
                    return cam.transform.position + cam.transform.forward * 0.55f - cam.transform.up * 0.28f;
            }

            Transform hips = Hips(stream);
            Vector3 at = hips != null ? hips.position : (player != null ? player.transform.position + Vector3.up * 0.85f : aim);
            return at + aim * 0.16f + Vector3.down * 0.06f;
        }

        static Transform Hips(Stream stream)
        {
            if (stream.SearchedHips)
                return stream.Hips;

            stream.SearchedHips = true;
            Transform body = Body(stream.Player);
            if (body == null)
                return null;

            Transform[] all = body.GetComponentsInChildren<Transform>(true);
            if (all == null)
                return null;

            for (int i = 0; i < all.Length; i++)
            {
                string name = all[i].name;
                if (name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    stream.Hips = all[i];
                    return stream.Hips;
                }
            }

            return null;
        }

        static Transform Body(SkopjePlayer player)
        {
            if (player == null)
                return null;
            if (player.ThirdPerson != null)
                return player.ThirdPerson.transform;
            return player.transform;
        }

        static bool BodyVisible(SkopjePlayer player)
        {
            Renderer renderer = player != null ? player.ThirdPersonRenderer : null;
            return renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy;
        }

        static bool HitWorld(Vector3 from, Vector3 to, Transform ignore, out RaycastHit closest)
        {
            closest = default;
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            if (dist < 0.001f)
                return false;

            RaycastHit[] hits = Physics.RaycastAll(from, delta / dist, dist, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
                return false;

            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider == null)
                    continue;
                Transform hit = collider.transform;
                if (ignore != null && (hit == ignore || hit.IsChildOf(ignore)))
                    continue;
                if (hits[i].distance < best)
                {
                    best = hits[i].distance;
                    closest = hits[i];
                    found = true;
                }
            }

            return found;
        }

        static Vector3 Along(Vector3[] points, int count, float t)
        {
            if (count <= 1)
                return points[0];

            float total = 0f;
            for (int i = 1; i < count; i++)
                total += Vector3.Distance(points[i - 1], points[i]);

            float want = total * Mathf.Clamp01(t);
            float walked = 0f;
            for (int i = 1; i < count; i++)
            {
                float seg = Vector3.Distance(points[i - 1], points[i]);
                if (walked + seg >= want)
                {
                    float u = seg < 0.0001f ? 0f : (want - walked) / seg;
                    return Vector3.Lerp(points[i - 1], points[i], u);
                }

                walked += seg;
            }

            return points[count - 1];
        }

        static void SpawnPuddle(RaycastHit hit)
        {
            EnsureArt();
            if (_cube == null || _material == null)
                return;

            var puddle = new GameObject("PeePuddle");
            var filter = puddle.AddComponent<MeshFilter>();
            filter.sharedMesh = _cube;
            var renderer = puddle.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Paint(renderer);

            Vector3 normal = hit.normal.sqrMagnitude > 0.001f ? hit.normal : Vector3.up;
            puddle.transform.position = hit.point + normal * 0.02f;
            puddle.transform.rotation = Quaternion.FromToRotation(Vector3.up, normal) * Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            float width = UnityEngine.Random.Range(0.16f, 0.3f);
            puddle.transform.localScale = new Vector3(width, 0.012f, width * UnityEngine.Random.Range(0.75f, 1.15f));

            _puddles.Add(new Puddle
            {
                Object = puddle,
                Born = Time.unscaledTime,
                Scale = puddle.transform.localScale,
            });
        }

        static Transform MakeDrop(Transform parent, float size)
        {
            var drop = new GameObject("Drop");
            drop.transform.SetParent(parent, false);
            drop.transform.localScale = Vector3.one * size;
            var filter = drop.AddComponent<MeshFilter>();
            filter.sharedMesh = _sphere;
            var renderer = drop.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = _material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            Paint(renderer);
            return drop.transform;
        }

        static void Paint(Renderer renderer)
        {
            if (renderer == null || _material == null)
                return;
            if (_block == null)
                _block = new MaterialPropertyBlock();
            _block.SetColor("_BaseColor", PeeColor);
            _block.SetColor("_Color", PeeColor);
            _block.SetColor("_EmissionColor", PeeColor);
            renderer.SetPropertyBlock(_block);
        }

        static void Paint(Material material)
        {
            if (material == null)
                return;
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", PeeColor);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", PeeColor);
            material.color = PeeColor;
            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", PeeColor * 1.4f);
            }
        }

        static Shader YellowShader()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Sprites/Default");
            return shader;
        }

        static void EnsureArt()
        {
            if (_material != null && _sphere != null && _cube != null)
                return;

            if (_material == null)
            {
                Shader shader = YellowShader();
                if (shader != null)
                {
                    _material = new Material(shader);
                    Paint(_material);
                }
            }

            if (_sphere == null)
            {
                var temp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                var filter = temp.GetComponent<MeshFilter>();
                if (filter != null)
                    _sphere = filter.sharedMesh;
                if (_material == null)
                {
                    var renderer = temp.GetComponent<MeshRenderer>();
                    if (renderer != null && renderer.sharedMaterial != null)
                    {
                        _material = new Material(renderer.sharedMaterial);
                        Paint(_material);
                    }
                }

                UnityEngine.Object.Destroy(temp);
            }

            if (_cube == null)
            {
                var temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var filter = temp.GetComponent<MeshFilter>();
                if (filter != null)
                    _cube = filter.sharedMesh;
                UnityEngine.Object.Destroy(temp);
            }
        }

        static void DestroyStream(Stream stream)
        {
            if (stream != null && stream.Root != null)
                UnityEngine.Object.Destroy(stream.Root);
        }
    }
}
