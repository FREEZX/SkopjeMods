using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Doozy.Engine.UI;
using FishNet.Object.Synchronizing;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Skopje.Modding;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Skopje.BossHealth
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class BossHealthPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.bosshealth";
        public const string PluginName = "Skopje Boss Health";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;
        internal static bool ScanEnabled;

        // Il2Cpp delegates are collected if nothing managed keeps them alive.
        static Il2CppSystem.Action _onClientStarted;
        static Il2CppSystem.Action _onClientStopped;
        static Il2CppSystem.Action<SkopjeGameMode> _onGameModeStarted;
        static Il2CppSystem.Action<SkopjeGameStatus> _onGameStatusChanged;
        static Il2CppSystem.Action _onGameOver;

        public override void Load()
        {
            LogSource = Log;
            ClassInjector.RegisterTypeInIl2Cpp<BossHealthDisplay>();

            // Client events only. A dedicated server never creates the display.
            _onClientStarted = ToIl2Cpp(new System.Action(OnClientStarted));
            _onClientStopped = ToIl2Cpp(new System.Action(OnClientStopped));
            _onGameModeStarted = ToIl2Cpp<SkopjeGameMode>(new System.Action<SkopjeGameMode>(_ => ScanEnabled = true));
            _onGameStatusChanged = ToIl2Cpp<SkopjeGameStatus>(new System.Action<SkopjeGameStatus>(OnGameStatusChanged));
            _onGameOver = ToIl2Cpp(new System.Action(() =>
            {
                ScanEnabled = false;
                BossHealthDisplay.ClearFights();
            }));

            ModAPI.add_OnClientStarted(_onClientStarted);
            ModAPI.add_OnClientStopped(_onClientStopped);
            ModAPI.add_OnGameModeStartedClient(_onGameModeStarted);
            ModAPI.add_OnGameStatusChanged(_onGameStatusChanged);
            ModAPI.add_OnGameOver(_onGameOver);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        static void OnClientStarted()
        {
            if (BossHealthDisplay.Instance != null)
                return;

            var overlay = new GameObject("BossHealthDisplay");
            UnityEngine.Object.DontDestroyOnLoad(overlay);
            overlay.AddComponent<BossHealthDisplay>();
            LogSource.LogInfo("Boss health display created.");
        }

        static void OnClientStopped()
        {
            ScanEnabled = false;
            if (BossHealthDisplay.Instance != null)
                UnityEngine.Object.Destroy(BossHealthDisplay.Instance.gameObject);
        }

        static Il2CppSystem.Action ToIl2Cpp(System.Action action)
        {
            return DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(action);
        }

        static Il2CppSystem.Action<T> ToIl2Cpp<T>(System.Action<T> action)
        {
            return DelegateSupport.ConvertDelegate<Il2CppSystem.Action<T>>(action);
        }

        static void OnGameStatusChanged(SkopjeGameStatus status)
        {
            ScanEnabled = status == SkopjeGameStatus.Active;
            if (!ScanEnabled)
                BossHealthDisplay.ClearFights();
        }
    }

    public class BossHealthDisplay : MonoBehaviour
    {
        const float IdleScanSeconds = 2f;
        const float FightReadSeconds = 0.1f;

        public static BossHealthDisplay Instance { get; private set; }

        readonly Fight[] _fights = new Fight[4];
        int _fightCount;
        float _nextScanTime;
        float _nextReadTime;

        UIView _ingameView;
        UIView _pauseView;
        UIView _mpPauseView;
        IngameMenuController _menu;
        GameObject _barRoot;
        RectTransform _barRect;
        CompassViewController _compass;
        Image _healthImage;
        Image _delayImage;
        TextMeshProUGUI _label;
        string _bossName = string.Empty;
        float _health = 1f;
        float _delay = 1f;
        float _delayWait;
        bool _hasSample;
        bool _loggedParent;
        bool _loggedMissingView;
        float _nextViewSearch;

        public BossHealthDisplay(IntPtr pointer) : base(pointer)
        {
        }

        public static void ClearFights()
        {
            if (Instance == null)
                return;
            Instance._fightCount = 0;
            Instance._hasSample = false;
            Instance._bossName = string.Empty;
            Instance.ApplyVisibility();
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (_barRoot != null)
                Destroy(_barRoot);
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            if (!BossHealthPlugin.ScanEnabled)
            {
                _fightCount = 0;
                _hasSample = false;
                _bossName = string.Empty;
                ApplyVisibility();
                return;
            }

            float now = Time.unscaledTime;
            if (_fightCount == 0)
            {
                if (now >= _nextScanTime)
                {
                    _nextScanTime = now + IdleScanSeconds;
                    Scan();
                }
            }
            else if (now >= _nextReadTime)
            {
                _nextReadTime = now + FightReadSeconds;
                Refresh();
            }

            if (_fightCount > 0)
                AnimateDelay();
            PlaceBar();
            ApplyVisibility();
        }

        void Scan()
        {
            _fightCount = 0;
            ConsiderAna();
            ConsiderVesna();
            ConsiderMetalsky();
            ConsiderTeofan();
            if (_fightCount > 0)
            {
                _nextReadTime = Time.unscaledTime + FightReadSeconds;
                if (_fights[0].StillGoing(out float ratio))
                    Present(_fights[0].Name, ratio);
            }
            else
            {
                _hasSample = false;
                _bossName = string.Empty;
            }
        }

        void Refresh()
        {
            int write = 0;
            for (int i = 0; i < _fightCount; i++)
            {
                Fight fight = _fights[i];
                if (!fight.StillGoing(out _))
                    continue;
                _fights[write++] = fight;
            }

            _fightCount = write;
            if (_fightCount == 0)
            {
                _hasSample = false;
                _bossName = string.Empty;
                _nextScanTime = Time.unscaledTime + IdleScanSeconds;
                return;
            }

            if (_fights[0].StillGoing(out float ratio))
                Present(_fights[0].Name, ratio);
        }

        void Present(string bossName, float ratio)
        {
            EnsureBar();
            if (!_hasSample || _bossName != bossName)
            {
                _bossName = bossName;
                _health = ratio;
                _delay = ratio;
                _delayWait = 0f;
                _hasSample = true;
            }
            else if (ratio < _health - 0.001f)
            {
                _delayWait = BossBarStyle.DelaySeconds;
                _health = ratio;
            }
            else
            {
                if (ratio > _delay)
                {
                    _delay = ratio;
                    _delayWait = 0f;
                }
                _health = ratio;
            }

            if (_label != null)
                _label.text = bossName;
            if (_healthImage != null)
                _healthImage.fillAmount = _health;
            if (_delayImage != null)
                _delayImage.fillAmount = _delay;
        }

        void AnimateDelay()
        {
            if (_delayImage == null || !_hasSample)
                return;

            if (_delayWait > 0f)
                _delayWait -= Time.unscaledDeltaTime;
            else if (_delay > _health)
                _delay = Mathf.MoveTowards(_delay, _health, BossBarStyle.DelayDrainPerSecond * Time.unscaledDeltaTime);

            _delayImage.fillAmount = _delay;
        }

        void ApplyVisibility()
        {
            if (_barRoot == null)
                return;

            bool show = _fightCount > 0 && _hasSample && HudAvailable();
            if (_barRoot.activeSelf != show)
                _barRoot.SetActive(show);
        }

        bool HudAvailable()
        {
            if (_ingameView == null || !_ingameView.gameObject.activeInHierarchy)
                return false;
            return !PauseOpen();
        }

        bool PauseOpen()
        {
            if (ViewShown(_pauseView) || ViewShown(_mpPauseView))
                return true;

            try
            {
                if (_menu == null)
                    return false;
                string node = _menu.ActiveNode;
                string pause = _menu.PauseNodeName;
                return !string.IsNullOrEmpty(node) && node == pause;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool ViewShown(UIView view)
        {
            if (view == null || !view.gameObject.activeInHierarchy)
                return false;
            try
            {
                return view.IsVisible || view.IsShowing;
            }
            catch (Exception)
            {
                return false;
            }
        }

        void EnsureBar()
        {
            ResolveViews();
            if (_ingameView == null)
            {
                if (!_loggedMissingView)
                {
                    _loggedMissingView = true;
                    BossHealthPlugin.LogSource.LogWarning("Boss bar could not find the Ingame view.");
                }
                return;
            }
            if (_barRoot != null)
                return;
            BuildBar();
        }

        void ResolveViews()
        {
            if (_ingameView != null && _menu != null && _compass != null)
                return;
            if (Time.unscaledTime < _nextViewSearch)
                return;

            _nextViewSearch = Time.unscaledTime + 2f;
            if (_ingameView == null)
                _ingameView = FindIngameView();
            if (_pauseView == null || _menu == null)
                FindPause();
            if (_compass == null)
                _compass = FindCompass();
        }

        static UIView FindIngameView()
        {
            UIView inactive = null;
            var views = Resources.FindObjectsOfTypeAll<UIView>();
            for (int i = 0; i < views.Length; i++)
            {
                UIView view = views[i];
                if (!IsSceneObject(view) || !IsIngameView(view))
                    continue;
                if (view.gameObject.activeInHierarchy)
                    return view;
                inactive ??= view;
            }

            return inactive;
        }

        static bool IsIngameView(UIView view)
        {
            string objectName = view.name;
            if (objectName == "View - Ingame" || objectName == "View - Ingam")
                return true;

            try
            {
                string viewName = view.ViewName;
                return viewName == "Ingame" || viewName == "Ingam";
            }
            catch (Exception)
            {
                return false;
            }
        }

        void FindPause()
        {
            var views = Resources.FindObjectsOfTypeAll<UIView>();
            for (int i = 0; i < views.Length; i++)
            {
                UIView view = views[i];
                if (!IsSceneObject(view) || !IsPauseView(view))
                    continue;
                if (_pauseView == null)
                    _pauseView = view;
                else if (_mpPauseView == null && view != _pauseView)
                    _mpPauseView = view;
            }

            if (_menu != null)
                return;

            var menus = Resources.FindObjectsOfTypeAll<IngameMenuController>();
            for (int i = 0; i < menus.Length; i++)
            {
                if (!IsSceneObject(menus[i]))
                    continue;
                _menu = menus[i];
                return;
            }
        }

        static bool IsPauseView(UIView view)
        {
            string objectName = view.name;
            if (objectName.StartsWith("View - Pause") || objectName.StartsWith("View - MPPaus"))
                return true;

            try
            {
                string viewName = view.ViewName;
                return viewName == "Pause" || viewName == "MPPause" || viewName == "MPPaus";
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool IsSceneObject(Component component)
        {
            return component != null && component.gameObject.scene.IsValid();
        }

        void BuildBar()
        {
            XpProgressBar xpBar = FindXpBar();
            Image xpImage = xpBar != null ? xpBar.ProgressBar : null;
            Sprite xpSprite = xpImage != null ? xpImage.sprite : null;
            Sprite healthSprite = ResolveSprite(BossBarStyle.HealthSpriteName, xpSprite) ?? SolidSprite();
            Sprite delaySprite = ResolveSprite(BossBarStyle.DelaySpriteName, healthSprite) ?? healthSprite;
            Sprite backgroundSprite = ResolveSprite(BossBarStyle.BackgroundSpriteName, FrameSprite(xpImage, healthSprite));

            _barRoot = new GameObject("BossHealthBar");
            _barRoot.AddComponent<RectTransform>();
            _barRoot.transform.SetParent(_ingameView.transform, false);

            _barRect = _barRoot.GetComponent<RectTransform>();
            _barRect.anchorMin = new Vector2(0.5f, 1f);
            _barRect.anchorMax = new Vector2(0.5f, 1f);
            _barRect.pivot = new Vector2(0.5f, 1f);
            _barRect.sizeDelta = BossBarStyle.BarSize;
            _barRect.localScale = Vector3.one;
            PlaceBar();

            Color healthColor = BossBarStyle.HealthColor;

            CreateImage("Background", backgroundSprite, Image.Type.Simple, BossBarStyle.BackgroundColor, 0f);
            _delayImage = CreateImage("DelayFill", delaySprite, Image.Type.Filled, BossBarStyle.DelayColor, BossBarStyle.TrackPadding);
            _healthImage = CreateImage("HealthFill", healthSprite, Image.Type.Filled, healthColor, BossBarStyle.TrackPadding);
            if (xpImage != null && xpImage.material != null)
            {
                _healthImage.material = xpImage.material;
                _delayImage.material = xpImage.material;
            }

            _healthImage.fillAmount = _health;
            _delayImage.fillAmount = _delay;
            CreateLabel(xpBar);
            _barRoot.SetActive(false);

            _barRoot.transform.SetAsLastSibling();
            if (!_loggedParent)
            {
                _loggedParent = true;
                string spriteName = healthSprite != null ? healthSprite.name : "(solid white)";
                BossHealthPlugin.LogSource.LogInfo(
                    "Boss bar parented to '" + _ingameView.name + "' using sprite '" + spriteName + "'. Edit BossBarStyle.cs to change sprites.");
            }
        }

        Image CreateImage(string name, Sprite sprite, Image.Type type, Color color, float inset)
        {
            var go = new GameObject(name);
            go.AddComponent<RectTransform>();
            go.transform.SetParent(_barRoot.transform, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
            rect.localScale = Vector3.one;

            Image image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.type = type;
            image.color = color;
            image.raycastTarget = false;
            if (type == Image.Type.Filled)
            {
                image.fillMethod = Image.FillMethod.Horizontal;
                image.fillOrigin = (int)Image.OriginHorizontal.Left;
                image.fillAmount = 1f;
            }

            return image;
        }

        void PlaceBar()
        {
            if (_barRect == null)
                return;

            float y = -(CompassHeight() + BossBarStyle.TopClearance);
            if (Mathf.Abs(_barRect.anchoredPosition.y - y) < 0.5f)
                return;
            _barRect.anchoredPosition = new Vector2(0f, y);
        }

        float CompassHeight()
        {
            if (_compass == null || _barRect == null)
                return 0f;

            try
            {
                if (!_compass.compassEnabled)
                    return 0f;
            }
            catch (Exception)
            {
                return 0f;
            }

            RectTransform compass = null;
            CompassView view = _compass.CompassView;
            if (view != null)
                compass = view.Compass;
            if (compass == null && _compass.CompassGO != null)
                compass = _compass.CompassGO.GetComponent<RectTransform>();
            if (compass == null || !compass.gameObject.activeInHierarchy)
                return 0f;

            float height = Mathf.Abs(compass.rect.height);
            if (height < 1f)
                height = Mathf.Abs(compass.sizeDelta.y);
            if (height < 1f)
                return 0f;

            float barScale = Mathf.Abs(_barRect.lossyScale.y);
            float compassScale = Mathf.Abs(compass.lossyScale.y);
            if (barScale > 0.0001f)
                height *= compassScale / barScale;
            return height;
        }

        static CompassViewController FindCompass()
        {
            var controllers = Resources.FindObjectsOfTypeAll<CompassViewController>();
            CompassViewController inactive = null;
            for (int i = 0; i < controllers.Length; i++)
            {
                CompassViewController controller = controllers[i];
                if (!IsSceneObject(controller))
                    continue;
                if (controller.gameObject.activeInHierarchy)
                    return controller;
                inactive ??= controller;
            }

            return inactive;
        }

        void CreateLabel(XpProgressBar xpBar)
        {
            TMP_FontAsset font = null;
            Material fontMaterial = null;
            if (xpBar != null && xpBar.LevelText != null)
            {
                font = xpBar.LevelText.font;
                fontMaterial = xpBar.LevelText.fontSharedMaterial;
            }

            if (font == null)
            {
                var texts = Resources.FindObjectsOfTypeAll<TextMeshProUGUI>();
                for (int i = 0; i < texts.Length; i++)
                {
                    TextMeshProUGUI text = texts[i];
                    if (!IsSceneObject(text) || text.font == null)
                        continue;
                    font = text.font;
                    fontMaterial = text.fontSharedMaterial;
                    break;
                }
            }

            if (font == null)
                return;

            var go = new GameObject("Label");
            go.AddComponent<RectTransform>();
            go.transform.SetParent(_barRoot.transform, false);

            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 2f);
            rect.sizeDelta = new Vector2(0f, BossBarStyle.LabelHeight);
            rect.localScale = Vector3.one;

            _label = go.AddComponent<TextMeshProUGUI>();
            _label.font = font;
            if (fontMaterial != null)
                _label.fontSharedMaterial = fontMaterial;
            _label.fontSize = BossBarStyle.LabelFontSize;
            _label.alignment = TextAlignmentOptions.Center;
            _label.color = BossBarStyle.LabelColor;
            _label.raycastTarget = false;
            _label.enableWordWrapping = false;
            _label.text = _bossName;
        }

        static XpProgressBar FindXpBar()
        {
            var bars = Resources.FindObjectsOfTypeAll<XpProgressBar>();
            XpProgressBar inactive = null;
            for (int i = 0; i < bars.Length; i++)
            {
                XpProgressBar bar = bars[i];
                if (!IsSceneObject(bar))
                    continue;
                if (bar.gameObject.activeInHierarchy)
                    return bar;
                inactive ??= bar;
            }

            return inactive;
        }

        static Sprite FrameSprite(Image xpImage, Sprite healthSprite)
        {
            if (xpImage == null || xpImage.transform.parent == null)
                return null;

            Image frame = xpImage.transform.parent.GetComponent<Image>();
            if (frame == null || frame.sprite == null || frame.sprite == healthSprite)
                return null;
            return frame.sprite;
        }

        static Sprite _solidSprite;

        static Sprite SolidSprite()
        {
            if (_solidSprite != null)
                return _solidSprite;

            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            _solidSprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            return _solidSprite;
        }

        static Sprite ResolveSprite(string overrideName, Sprite fallback)
        {
            if (string.IsNullOrEmpty(overrideName))
                return fallback;

            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            for (int i = 0; i < sprites.Length; i++)
            {
                Sprite sprite = sprites[i];
                if (sprite != null && sprite.name == overrideName)
                    return sprite;
            }

            BossHealthPlugin.LogSource.LogWarning(
                "Boss bar sprite '" + overrideName + "' was not found. Using the XP bar sprite instead.");
            return fallback;
        }

        void AddFight(Fight fight)
        {
            if (fight == null || _fightCount >= _fights.Length)
                return;
            _fights[_fightCount++] = fight;
            BossHealthPlugin.LogSource.LogInfo($"Boss fight started: {fight.Name}");
        }

        void ConsiderAna()
        {
            var bosses = UnityEngine.Object.FindObjectsOfType<AnaBoss>();
            for (int i = 0; i < bosses.Length; i++)
            {
                AnaBoss boss = bosses[i];
                if (!IsAlive(boss) || !boss.isActiveAndEnabled)
                    continue;
                if (!TryRatio(boss.EnemyControl, out _))
                    continue;
                if (IsTrue(boss.Dead))
                    continue;
                if (!IsTrue(boss.Visibility) && !IsTrue(boss.IsVulnerable))
                    continue;

                AddFight(new AnaFight(boss));
                return;
            }
        }

        void ConsiderVesna()
        {
            var bosses = UnityEngine.Object.FindObjectsOfType<VesnaBoss>();
            for (int i = 0; i < bosses.Length; i++)
            {
                VesnaBoss boss = bosses[i];
                if (!IsAlive(boss) || !boss.isActiveAndEnabled)
                    continue;
                if (!TryRatio(boss.EnemyControl, out _))
                    continue;
                if (IsTrue(boss.Dead) || !IsTrue(boss.Visibility))
                    continue;

                AddFight(new VesnaFight(boss));
                return;
            }
        }

        void ConsiderMetalsky()
        {
            var bosses = UnityEngine.Object.FindObjectsOfType<MetalskyBoss>();
            for (int i = 0; i < bosses.Length; i++)
            {
                MetalskyBoss boss = bosses[i];
                if (!IsAlive(boss) || !boss.isActiveAndEnabled)
                    continue;
                if (!IsTrue(boss.BossBattleState) || SafeDead(boss))
                    continue;
                if (!TryRatio(boss.PawnStatus, out _))
                    continue;

                AddFight(new MetalskyFight(boss));
                return;
            }
        }

        void ConsiderTeofan()
        {
            var brains = UnityEngine.Object.FindObjectsOfType<TeofanBossBrain>();
            for (int i = 0; i < brains.Length; i++)
            {
                TeofanBossBrain brain = brains[i];
                if (!IsAlive(brain) || !brain.isActiveAndEnabled || SafeDead(brain))
                    continue;

                EnemyControlAStar control = brain.GetComponent<EnemyControlAStar>();
                if (control == null)
                    control = brain.GetComponentInParent<EnemyControlAStar>();
                if (control == null)
                    control = brain.GetComponentInChildren<EnemyControlAStar>(true);
                if (!TryRatio(control, out _))
                    continue;
                if (!TeofanPhaseStarted())
                    continue;

                AddFight(new TeofanFight(brain));
                return;
            }
        }

        static bool TeofanPhaseStarted()
        {
            var arenas = UnityEngine.Object.FindObjectsOfType<TeofanBossArena>();
            if (arenas == null || arenas.Length == 0)
                return true;

            for (int i = 0; i < arenas.Length; i++)
            {
                TeofanBossArena arena = arenas[i];
                if (!IsAlive(arena) || arena.Phase == null)
                    continue;
                if (arena.Phase.Value > 0)
                    return true;
            }

            return false;
        }

        static bool TryRatio(EnemyControlAStar control, out float ratio)
        {
            ratio = 0f;
            if (!IsAlive(control))
                return false;
            return TryRatio(control.PawnStatus, out ratio);
        }

        static bool TryRatio(PawnStatus status, out float ratio)
        {
            ratio = 0f;
            try
            {
                if (!IsAlive(status))
                    return false;

                float max = status.InitialHealth;
                if (max <= 0.01f)
                    return false;

                SyncVar<float> health = status.Health;
                if (health == null)
                    return false;

                ratio = Mathf.Clamp01(health.Value / max);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool IsTrue(SyncVar<bool> value)
        {
            try
            {
                return value != null && value.Value;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static bool IsAlive(UnityEngine.Object obj)
        {
            return obj != null;
        }

        static bool SafeDead(MetalskyBoss boss)
        {
            try
            {
                return boss.IsDead;
            }
            catch (Exception)
            {
                return true;
            }
        }

        static bool SafeDead(TeofanBossBrain brain)
        {
            try
            {
                return brain.dead;
            }
            catch (Exception)
            {
                return true;
            }
        }

        abstract class Fight
        {
            public string Name;

            public abstract bool StillGoing(out float ratio);
        }

        sealed class AnaFight : Fight
        {
            readonly AnaBoss _boss;
            bool _engaged;

            public AnaFight(AnaBoss boss)
            {
                _boss = boss;
                Name = "Ana";
                _engaged = true;
            }

            public override bool StillGoing(out float ratio)
            {
                ratio = 0f;
                if (!IsAlive(_boss))
                    return false;
                if (IsTrue(_boss.Dead))
                    return false;

                // Ana disappears between attacks. Keep the counter until she is actually dead.
                if (IsTrue(_boss.Visibility) || IsTrue(_boss.IsVulnerable))
                    _engaged = true;
                if (!_engaged)
                    return false;
                return TryRatio(_boss.EnemyControl, out ratio);
            }
        }

        sealed class VesnaFight : Fight
        {
            readonly VesnaBoss _boss;
            bool _engaged;

            public VesnaFight(VesnaBoss boss)
            {
                _boss = boss;
                Name = "Vesna";
                _engaged = true;
            }

            public override bool StillGoing(out float ratio)
            {
                ratio = 0f;
                if (!IsAlive(_boss))
                    return false;
                if (IsTrue(_boss.Dead))
                    return false;
                if (IsTrue(_boss.Visibility))
                    _engaged = true;
                if (!_engaged)
                    return false;
                return TryRatio(_boss.EnemyControl, out ratio);
            }
        }

        sealed class MetalskyFight : Fight
        {
            readonly MetalskyBoss _boss;

            public MetalskyFight(MetalskyBoss boss)
            {
                _boss = boss;
                Name = "Metalsky";
            }

            public override bool StillGoing(out float ratio)
            {
                ratio = 0f;
                if (!IsAlive(_boss) || SafeDead(_boss) || !IsTrue(_boss.BossBattleState))
                    return false;
                return TryRatio(_boss.PawnStatus, out ratio);
            }
        }

        sealed class TeofanFight : Fight
        {
            readonly TeofanBossBrain _brain;

            public TeofanFight(TeofanBossBrain brain)
            {
                _brain = brain;
                Name = "Teofan";
            }

            public override bool StillGoing(out float ratio)
            {
                ratio = 0f;
                if (!IsAlive(_brain) || SafeDead(_brain))
                    return false;

                EnemyControlAStar control = _brain.GetComponent<EnemyControlAStar>();
                if (control == null)
                    control = _brain.GetComponentInParent<EnemyControlAStar>();
                if (control == null)
                    control = _brain.GetComponentInChildren<EnemyControlAStar>(true);
                return TryRatio(control, out ratio);
            }
        }
    }
}
