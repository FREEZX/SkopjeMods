using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Skopje.Modding;
using UnityEngine;

namespace Skopje.DamageNumbers
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class DamageNumbersPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.damagenumbers";
        public const string PluginName = "Skopje Damage Numbers";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;
        internal static bool ClientRunning;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> OnlyYourDamage;
        internal static ConfigEntry<float> Lifetime;
        internal static ConfigEntry<float> Size;
        internal static ConfigEntry<float> ValueMultiplier;
        internal static ConfigEntry<int> CritTextAbove;

        static Harmony _harmony;
        static SkopjePlayer _localPlayer;
        static int _errorLogs;
        static int _traceLogs;

        const int Recent = 32;
        static readonly int[] _recentEnemy = new int[Recent];
        static readonly int[] _recentAmount = new int[Recent];
        static readonly int[] _recentCell = new int[Recent];
        static readonly int[] _recentFrame = new int[Recent];
        static int _recentWrite;

        static Il2CppSystem.Action _onClientStarted;
        static Il2CppSystem.Action _onClientStopped;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStopped;
        static Il2CppSystem.Action _onGameOver;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Display", "Enabled", true, "Show floating damage numbers.");
            OnlyYourDamage = Config.Bind("Display", "OnlyYourDamage", true, "Only show hits dealt by your pawn. Hits with no owner still show.");
            Lifetime = Config.Bind("Display", "Lifetime", 0.8f, "Seconds each number stays on screen.");
            Size = Config.Bind("Display", "Size", 1f, "Size multiplier.");
            ValueMultiplier = Config.Bind("Display", "ValueMultiplier", 1f, "Extra multiplier on the displayed number. The built-in scale stays in place.");
            CritTextAbove = Config.Bind("Display", "CritTextAbove", 100, "Show the CRIT label when the displayed number is greater than this.");

            ClassInjector.RegisterTypeInIl2Cpp<DamageNumbersDisplay>();

            _onClientStarted = ToIl2Cpp(new System.Action(OnClientStarted));
            _onClientStopped = ToIl2Cpp(new System.Action(OnClientStopped));
            _onPlayerStarted = ToIl2Cpp<SkopjePlayer>(new System.Action<SkopjePlayer>(OnPlayerStarted));
            _onPlayerStopped = ToIl2Cpp<SkopjePlayer>(new System.Action<SkopjePlayer>(OnPlayerStopped));
            _onGameOver = ToIl2Cpp(new System.Action(OnGameOver));

            ModAPI.add_OnClientStarted(_onClientStarted);
            ModAPI.add_OnClientStopped(_onClientStopped);
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);
            ModAPI.add_OnPlayerStoppedClient(_onPlayerStopped);
            ModAPI.add_OnGameOver(_onGameOver);

            _harmony = new Harmony(PluginGuid);
            Patch(AccessTools.Method(typeof(EnemyControlAStar), nameof(EnemyControlAStar.TakeDamage)), nameof(AfterTakeDamage));
            // Observers RPC body. Parameter names are stripped, so the postfix binds them by index.
            Patch(AccessTools.Method(typeof(EnemyControlAStar), "RpcLogic___RpcDamageTaken_3262335728"), nameof(AfterObservedDamage));

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        public override bool Unload()
        {
            ClientRunning = false;
            if (_harmony != null)
                _harmony.UnpatchSelf();
            DamageNumbersDisplay.DestroyHost();
            return true;
        }

        static void Patch(System.Reflection.MethodInfo method, string postfix)
        {
            if (method == null)
            {
                LogSource.LogWarning("Damage number hook was not found: " + postfix);
                return;
            }

            _harmony.Patch(method, postfix: new HarmonyMethod(typeof(DamageNumbersPlugin), postfix));
        }

        static void OnClientStarted()
        {
            ClientRunning = true;
            DamageNumbersDisplay.Ensure();
            LogSource.LogInfo("Damage numbers ready.");
        }

        static void OnClientStopped()
        {
            ClientRunning = false;
            _localPlayer = null;
            DamageNumbersDisplay.DestroyHost();
        }

        static void OnPlayerStarted(SkopjePlayer player)
        {
            if (player != null && player.IsOwner)
                _localPlayer = player;
        }

        static void OnPlayerStopped(SkopjePlayer player)
        {
            if (_localPlayer == player)
                _localPlayer = null;
        }

        static void OnGameOver()
        {
            DamageNumbersDisplay.Clear();
        }

        static void AfterTakeDamage(EnemyControlAStar __instance, HitData hitData)
        {
            try
            {
                Present(__instance, hitData, null, true);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        static void AfterObservedDamage(
            EnemyControlAStar __instance,
            [HarmonyArgument(0)] HitData hitData,
            [HarmonyArgument(1)] DamageablePart part)
        {
            try
            {
                Present(__instance, hitData, part, false);
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        static void Present(EnemyControlAStar enemy, HitData hit, DamageablePart part, bool allowLastPart)
        {
            if (!ClientRunning || Enabled == null || !Enabled.Value)
                return;
            if (enemy == null || hit == null || hit.isDespawn)
                return;

            DamageType type = hit.DamageType;
            if (HasFlag(type, DamageType.Enemy) || HasFlag(type, DamageType.Fall))
                return;
            if (!IsYourHit(hit))
            {
                Trace("skipped somebody else's hit");
                return;
            }

            float raw = hit.damageAmount;
            if (float.IsNaN(raw) || float.IsInfinity(raw) || raw <= 0.01f)
                return;

            int rawAmount = Mathf.Max(1, Mathf.RoundToInt(raw));
            Vector3 pos = HitPosition(enemy, hit);
            if (!Claim(enemy, rawAmount, pos))
                return;

            if (part == null && allowLastPart && enemy.lastDamagedPart != null)
                part = enemy.lastDamagedPart.Value;

            bool crit = IsCrit(part);
            float displayScale = DisplayScale(crit);
            int shown = ShownDamage(raw, displayScale);
            DamageTier.Apply(shown, displayScale, out Color color, out float tierScale);
            float punch = PunchFor(rawAmount, crit, type) * tierScale;
            pos += new Vector3(
                UnityEngine.Random.Range(-0.22f, 0.22f),
                UnityEngine.Random.Range(0.05f, 0.28f),
                UnityEngine.Random.Range(-0.22f, 0.22f));

            DamageNumbersDisplay.Ensure();
            DamageNumbersDisplay.Spawn(pos, shown, color, punch, crit);
            Trace("hit " + shown + " " + type + (crit ? " crit" : "") + " at " + pos);
        }

        static bool IsYourHit(HitData hit)
        {
            if (OnlyYourDamage == null || !OnlyYourDamage.Value)
                return true;

            PawnStatus owner = hit.HitOwner;
            if (owner == null)
                return true;

            PawnStatus local = LocalPawn();
            if (local == null)
                return true;

            return owner.Pointer == local.Pointer;
        }

        static PawnStatus LocalPawn()
        {
            if (_localPlayer == null)
                _localPlayer = FindLocalPlayer();
            if (_localPlayer == null)
                return null;

            try
            {
                return _localPlayer.PawnStatus;
            }
            catch (Exception)
            {
                _localPlayer = null;
                return null;
            }
        }

        static SkopjePlayer FindLocalPlayer()
        {
            var players = UnityEngine.Object.FindObjectsOfType<SkopjePlayer>();
            if (players == null)
                return null;

            for (int i = 0; i < players.Length; i++)
            {
                SkopjePlayer player = players[i];
                if (player != null && player.IsOwner)
                    return player;
            }

            return null;
        }

        static Vector3 HitPosition(EnemyControlAStar enemy, HitData hit)
        {
            Vector3 pos = hit.hitPosition;
            if (pos.sqrMagnitude > 0.0001f)
                return pos;

            pos = enemy.lastHitPosition;
            if (pos.sqrMagnitude > 0.0001f)
                return pos;

            if (enemy.transform != null)
                return enemy.transform.position + Vector3.up * 1.4f;
            return Vector3.up;
        }

        static bool IsCrit(DamageablePart part)
        {
            // part.name goes through UnityEngine.Object and can access-violate on a
            // networked part that is not a live Unity object. Weakspot fields are enough.
            if (part == null)
                return false;

            try
            {
                if (part.WeakspotType != WeakspotType.None)
                    return true;
                return part.PartDamageModifier > 1.05f;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Real hits in this game are often 1, 2, or 3. Scale them up so the popup
        // still tracks the hit, but reads like a chunky score. Crits are doubled.
        static float DisplayScale(bool crit)
        {
            float extra = 1f;
            if (ValueMultiplier != null && !float.IsNaN(ValueMultiplier.Value) && !float.IsInfinity(ValueMultiplier.Value))
                extra = ValueMultiplier.Value;
            return (crit ? 200f : 100f) * extra;
        }

        static int ShownDamage(float raw, float displayScale)
        {
            float roll = UnityEngine.Random.Range(0.94f, 1.06f);
            return Mathf.Max(1, Mathf.RoundToInt(raw * displayScale * roll));
        }

        internal static bool ShowCritText(int shown, bool crit)
        {
            if (!crit)
                return false;
            int above = CritTextAbove != null ? CritTextAbove.Value : 100;
            return shown > above;
        }

        static bool Claim(EnemyControlAStar enemy, int amount, Vector3 pos)
        {
            int id = enemy.GetInstanceID();
            int cell = (int)(pos.x * 8f) * 73856093
                ^ (int)(pos.y * 8f) * 19349663
                ^ (int)(pos.z * 8f) * 83492791;
            int frame = Time.frameCount;
            for (int i = 0; i < Recent; i++)
            {
                if (_recentFrame[i] < frame - 1)
                    continue;
                if (_recentEnemy[i] == id && _recentAmount[i] == amount && _recentCell[i] == cell)
                    return false;
            }

            int slot = _recentWrite++ % Recent;
            if (_recentWrite < 0)
                _recentWrite = 0;
            _recentEnemy[slot] = id;
            _recentAmount[slot] = amount;
            _recentCell[slot] = cell;
            _recentFrame[slot] = frame;
            return true;
        }

        static float PunchFor(int amount, bool crit, DamageType type)
        {
            float punch = 1f + Mathf.Clamp(Mathf.Log10(Mathf.Max(amount, 1)) * 0.14f, 0f, 0.5f);
            if (crit)
                punch *= 1.45f;
            if (HasFlag(type, DamageType.Explosion))
                punch *= 1.2f;
            return punch;
        }

        static bool HasFlag(DamageType value, DamageType flag)
        {
            return ((int)value & (int)flag) != 0;
        }

        static void Fail(Exception ex)
        {
            if (_errorLogs++ < 5)
                LogSource.LogWarning("Damage number failed: " + ex.Message);
        }

        static void Trace(string message)
        {
            if (_traceLogs++ < 8)
                LogSource.LogInfo(message);
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

    public class DamageNumbersDisplay : MonoBehaviour
    {
        const int Capacity = 48;

        public static DamageNumbersDisplay Instance { get; private set; }

        readonly Number[] _numbers = new Number[Capacity];
        GUIStyle _style;
        Camera _camera;
        float _nextCameraSearch;

        public DamageNumbersDisplay(IntPtr pointer) : base(pointer)
        {
        }

        public static void Ensure()
        {
            if (Instance != null)
                return;

            var overlay = new GameObject("DamageNumbersDisplay");
            UnityEngine.Object.DontDestroyOnLoad(overlay);
            overlay.AddComponent<DamageNumbersDisplay>();
        }

        public static void DestroyHost()
        {
            if (Instance != null)
                UnityEngine.Object.Destroy(Instance.gameObject);
        }

        public static void Clear()
        {
            if (Instance == null)
                return;
            for (int i = 0; i < Instance._numbers.Length; i++)
                Instance._numbers[i].Used = false;
        }

        public static void Spawn(Vector3 worldPos, int amount, Color color, float punch, bool crit)
        {
            DamageNumbersDisplay display = Instance;
            if (display == null)
                return;

            float life = 0.8f;
            if (DamageNumbersPlugin.Lifetime != null)
                life = Mathf.Clamp(DamageNumbersPlugin.Lifetime.Value, 0.25f, 2.5f);
            if (crit)
                life *= 1.15f;

            int slot = display.FindSlot();
            display._numbers[slot] = new Number
            {
                Used = true,
                Origin = worldPos,
                Drift = new Vector3(
                    UnityEngine.Random.Range(-0.55f, 0.55f),
                    UnityEngine.Random.Range(crit ? 1.15f : 0.9f, crit ? 1.65f : 1.35f),
                    UnityEngine.Random.Range(-0.2f, 0.2f)),
                Age = 0f,
                Life = life,
                Punch = punch,
                Tilt = UnityEngine.Random.Range(crit ? -18f : -14f, crit ? 18f : 14f),
                Amount = amount,
                Color = color,
                Text = DamageNumbersPlugin.ShowCritText(amount, crit) ? "CRIT\n" + amount : amount.ToString()
            };
            Measure(display._numbers[slot].Text, out display._numbers[slot].Lines, out display._numbers[slot].Longest);
        }

        static void Measure(string text, out int lines, out int longest)
        {
            lines = 1;
            longest = 0;
            int run = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    lines++;
                    if (run > longest)
                        longest = run;
                    run = 0;
                }
                else
                {
                    run++;
                }
            }

            if (run > longest)
                longest = run;
        }

        int FindSlot()
        {
            int oldest = 0;
            float oldestAge = -1f;
            for (int i = 0; i < _numbers.Length; i++)
            {
                if (!_numbers[i].Used)
                    return i;
                if (_numbers[i].Age > oldestAge)
                {
                    oldestAge = _numbers[i].Age;
                    oldest = i;
                }
            }

            return oldest;
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            float step = Time.deltaTime;
            for (int i = 0; i < _numbers.Length; i++)
            {
                if (!_numbers[i].Used)
                    continue;
                _numbers[i].Age += step;
                if (_numbers[i].Age >= _numbers[i].Life)
                    _numbers[i].Used = false;
            }
        }

        void OnGUI()
        {
            Camera cam = CameraNow();
            if (cam == null)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    richText = false
                };
            }

            float size = DamageNumbersPlugin.Size != null
                ? Mathf.Clamp(DamageNumbersPlugin.Size.Value, 0.25f, 4f)
                : 1f;

            for (int i = 0; i < _numbers.Length; i++)
            {
                if (_numbers[i].Used)
                    Draw(cam, _numbers[i], size);
            }
        }

        Camera CameraNow()
        {
            if (_camera != null && _camera.isActiveAndEnabled)
                return _camera;
            if (Time.unscaledTime < _nextCameraSearch)
                return null;

            _nextCameraSearch = Time.unscaledTime + 0.5f;
            _camera = Camera.main;
            if (_camera == null)
                _camera = UnityEngine.Object.FindObjectOfType<Camera>();
            return _camera;
        }

        void Draw(Camera cam, Number number, float size)
        {
            float t = Mathf.Clamp01(number.Age / number.Life);
            float rise = 1f - (1f - t) * (1f - t);
            Vector3 world = number.Origin + number.Drift * rise;
            Vector3 screen = cam.WorldToScreenPoint(world);
            if (screen.z <= 0.05f)
                return;
            if (screen.x < -60f || screen.y < -60f || screen.x > Screen.width + 60f || screen.y > Screen.height + 60f)
                return;

            float dist = Vector3.Distance(cam.transform.position, world);
            float perspective = Mathf.Clamp(8f / Mathf.Max(dist, 0.75f), 0.4f, 1.8f);
            float pop = PopScale(t);
            int font = Mathf.Clamp(Mathf.RoundToInt(36f * perspective * pop * number.Punch * size), 12, 140);

            float alpha = t < 0.62f ? 1f : 1f - (t - 0.62f) / 0.38f;
            Color color = number.Color;
            color.a *= alpha;

            float width = font * Mathf.Max(2, number.Longest) * 0.7f + 20f;
            float height = font * number.Lines + 12f;
            float x = screen.x - width * 0.5f;
            float y = Screen.height - screen.y - height * 0.5f;
            var rect = new Rect(x, y, width, height);
            var pivot = new Vector2(screen.x, Screen.height - screen.y);

            Matrix4x4 matrix = GUI.matrix;
            Color previous = GUI.color;
            _style.fontSize = font;
            GUIUtility.RotateAroundPivot(number.Tilt * (1f - t), pivot);

            float outline = Mathf.Max(1f, font / 16f);
            GUI.color = new Color(0f, 0f, 0f, color.a * 0.92f);
            GUI.Label(new Rect(rect.x - outline, rect.y, rect.width, rect.height), number.Text, _style);
            GUI.Label(new Rect(rect.x + outline, rect.y, rect.width, rect.height), number.Text, _style);
            GUI.Label(new Rect(rect.x, rect.y - outline, rect.width, rect.height), number.Text, _style);
            GUI.Label(new Rect(rect.x, rect.y + outline, rect.width, rect.height), number.Text, _style);
            GUI.color = color;
            GUI.Label(rect, number.Text, _style);

            GUI.color = previous;
            GUI.matrix = matrix;
        }

        static float PopScale(float t)
        {
            if (t < 0.08f)
                return Mathf.Lerp(0.35f, 1.4f, t / 0.08f);
            if (t < 0.18f)
                return Mathf.Lerp(1.4f, 1f, (t - 0.08f) / 0.1f);
            return 1f;
        }

        struct Number
        {
            public bool Used;
            public Vector3 Origin;
            public Vector3 Drift;
            public float Age;
            public float Life;
            public float Punch;
            public float Tilt;
            public int Amount;
            public int Lines;
            public int Longest;
            public Color Color;
            public string Text;
        }
    }

    static class DamageTier
    {
        // Chosen once per hit. Draw only reads the stored color and punch.
        static readonly Color White = new Color(1f, 1f, 1f, 1f);
        static readonly Color LightYellow = new Color(1f, 0.96f, 0.55f, 1f);
        static readonly Color LightOrange = new Color(1f, 0.74f, 0.38f, 1f);
        static readonly Color Orange = new Color(1f, 0.48f, 0.08f, 1f);

        public static void Apply(int amount, float displayScale, out Color color, out float sizeScale)
        {
            int yellow = Band(25, displayScale);
            int lightOrange = Band(40, displayScale);
            int orange = Band(50, displayScale);

            if (amount >= orange)
            {
                color = Orange;
                sizeScale = 1.15f;
                return;
            }

            sizeScale = 1f;
            if (amount >= lightOrange)
                color = LightOrange;
            else if (amount >= yellow)
                color = LightYellow;
            else
                color = White;
        }

        static int Band(int baseValue, float displayScale)
        {
            return Mathf.Max(1, Mathf.RoundToInt(baseValue * displayScale));
        }
    }
}
