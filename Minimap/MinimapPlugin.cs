using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Doozy.Engine.UI;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Skopje.Modding;
using UnityEngine;
using UnityEngine.UI;

namespace Skopje.Minimap
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class MinimapPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.minimap";
        public const string PluginName = "Skopje Minimap";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> Size;
        internal static ConfigEntry<float> ViewRadius;
        internal static ConfigEntry<float> Gap;
        internal static ConfigEntry<bool> ShowMarkers;
        internal static ConfigEntry<float> MarkerSize;

        internal static SkopjePlayer LocalPlayer;

        // Il2Cpp delegates are collected if nothing managed keeps them alive.
        static Il2CppSystem.Action _onClientStarted;
        static Il2CppSystem.Action _onClientStopped;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStopped;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Minimap", "Enabled", true, "Show the minimap under the clock.");
            Size = Config.Bind("Minimap", "Size", 256f, new ConfigDescription(
                "Width and height of the minimap, in the same units as the rest of the HUD.",
                new AcceptableValueRange<float>(96f, 512f)));
            ViewRadius = Config.Bind("Minimap", "ViewRadius", 100f, new ConfigDescription(
                "Meters from the player to the edge of the minimap.",
                new AcceptableValueRange<float>(25f, 600f)));
            Gap = Config.Bind("Minimap", "Gap", 6f, new ConfigDescription(
                "Space between the bottom of the clock and the top of the minimap.",
                new AcceptableValueRange<float>(0f, 80f)));
            ShowMarkers = Config.Bind("Minimap", "ShowMarkers", true, "Show the same map icons the full map uses, when they are inside the view.");
            MarkerSize = Config.Bind("Minimap", "MarkerSize", 16f, new ConfigDescription(
                "Icon size on the minimap.",
                new AcceptableValueRange<float>(8f, 40f)));

            ClassInjector.RegisterTypeInIl2Cpp<MinimapDisplay>();

            _onClientStarted = ToIl2Cpp(new System.Action(OnClientStarted));
            _onClientStopped = ToIl2Cpp(new System.Action(OnClientStopped));
            _onPlayerStarted = ToIl2Cpp<SkopjePlayer>(new System.Action<SkopjePlayer>(OnPlayerStarted));
            _onPlayerStopped = ToIl2Cpp<SkopjePlayer>(new System.Action<SkopjePlayer>(OnPlayerStopped));

            ModAPI.add_OnClientStarted(_onClientStarted);
            ModAPI.add_OnClientStopped(_onClientStopped);
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);
            ModAPI.add_OnPlayerStoppedClient(_onPlayerStopped);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Config: {Config.ConfigFilePath}");
        }

        public override bool Unload()
        {
            LocalPlayer = null;
            if (MinimapDisplay.Instance != null)
                UnityEngine.Object.Destroy(MinimapDisplay.Instance.gameObject);
            return true;
        }

        static void OnClientStarted()
        {
            if (MinimapDisplay.Instance != null)
                return;

            var host = new GameObject("SkopjeMinimap");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<MinimapDisplay>();
            LogSource.LogInfo("Minimap display created.");
        }

        static void OnClientStopped()
        {
            LocalPlayer = null;
            if (MinimapDisplay.Instance != null)
                UnityEngine.Object.Destroy(MinimapDisplay.Instance.gameObject);
        }

        static void OnPlayerStarted(SkopjePlayer player)
        {
            if (player != null && player.IsOwner)
                LocalPlayer = player;
        }

        static void OnPlayerStopped(SkopjePlayer player)
        {
            if (LocalPlayer == player)
                LocalPlayer = null;
        }

        internal static SkopjePlayer FindLocalPlayer()
        {
            return LocalPlayer;
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

    public class MinimapDisplay : MonoBehaviour
    {
        const float Border = 2f;
        const int MaxMarkers = 40;
        const float SampleMeters = 20f;

        public static MinimapDisplay Instance { get; private set; }

        TimeDisplay _time;
        UIView _hud;
        MapControls _map;
        LevelBounds _bounds;
        Image _source;
        Sprite _mapSprite;

        RectTransform _root;
        RectTransform _window;
        RectTransform _content;
        RectTransform _mapRect;
        Image _mapImage;
        RectTransform _markersRoot;
        RectTransform _heading;

        bool _useTopDown = true;
        Vector2 _fallbackSize;
        bool _laidOut;
        bool _loggedReady;
        bool _loggedTick;
        bool _loggedWait;
        float _nextSearch;
        float _nextMarkerScan;
        int _errorLogs;

        readonly List<MarkerSlot> _slots = new List<MarkerSlot>();
        readonly List<MapIndicator> _nearby = new List<MapIndicator>();
        readonly List<float> _nearbyDist = new List<float>();
        readonly Vector3[] _corners = new Vector3[4];

        static Sprite _arrowSprite;
        static Sprite _dotSprite;

        public MinimapDisplay(IntPtr pointer) : base(pointer)
        {
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            if (_root != null)
                Destroy(_root.gameObject);
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                if (_errorLogs < 3)
                {
                    _errorLogs++;
                    MinimapPlugin.LogSource.LogWarning("Minimap update failed: " + ex.Message);
                }
            }
        }

        void Tick()
        {
            if (!_loggedTick)
            {
                _loggedTick = true;
                MinimapPlugin.LogSource.LogInfo("Minimap ticking.");
            }

            if (!MinimapPlugin.Enabled.Value)
            {
                if (_root != null)
                    _root.gameObject.SetActive(false);
                return;
            }

            if (MinimapPlugin.LocalPlayer == null)
            {
                if (_root != null)
                    _root.gameObject.SetActive(false);
                return;
            }

            float now = Time.unscaledTime;
            bool hudMissing = _hud == null || !_hud.gameObject.activeInHierarchy;
            if ((_time == null || hudMissing || _mapSprite == null || _bounds == null) && now >= _nextSearch)
            {
                _nextSearch = now + 1.5f;
                ResolveSources();
            }

            bool hudOn = _hud != null && _hud.gameObject.activeInHierarchy;
            if (!hudOn)
            {
                if (_root != null)
                    _root.gameObject.SetActive(false);
                if (!_loggedWait && now > 8f)
                {
                    _loggedWait = true;
                    MinimapPlugin.LogSource.LogInfo("Minimap hidden: the in-game HUD is not active.");
                }
                return;
            }

            EnsureWidget();
            if (_root == null)
                return;

            if (!_root.gameObject.activeSelf)
                _root.gameObject.SetActive(true);

            PlaceUnderTimer();

            SkopjePlayer player = MinimapPlugin.FindLocalPlayer();
            if (_mapSprite == null || _bounds == null)
            {
                if (!_loggedWait && now > 8f)
                {
                    _loggedWait = true;
                    MinimapPlugin.LogSource.LogInfo(
                        "Minimap frame is showing. Map image found: " + (_mapSprite != null) + ". Level bounds found: " + (_bounds != null) + ".");
                }
                return;
            }

            if (!_laidOut || _mapImage.sprite != _mapSprite)
                LayoutMap();

            if (player != null && !Follow(player))
                ShowWholeMap(player);
            else if (player == null)
                ShowWholeMap(null);

            if (player != null && MinimapPlugin.ShowMarkers.Value)
            {
                if (now >= _nextMarkerScan)
                {
                    _nextMarkerScan = now + 0.75f;
                    RescanMarkers(player);
                }
                MoveMarkers();
            }
            else if (_slots.Count > 0)
            {
                HideMarkers();
            }
        }

        void ResolveSources()
        {
            if (_time == null)
                _time = PickTimer();
            if (_hud == null || !_hud.gameObject.activeInHierarchy)
            {
                UIView found = FindIngameView();
                if (found != null)
                    _hud = found;
            }
            if (_mapSprite == null)
            {
                _map = PickMap();
                _source = _map != null ? FindMapGraphic(_map) : null;
                if (_source != null)
                    _mapSprite = _source.sprite;
                if (_mapSprite == null)
                    _mapSprite = FindLooseMapSprite();
                _laidOut = false;
            }
            if (_bounds == null)
                _bounds = PickBounds();
        }

        void EnsureWidget()
        {
            if (_root != null)
                return;

            RectTransform parent = _hud.transform as RectTransform;
            if (parent == null)
                return;

            int layer = parent.gameObject.layer;
            _root = CreateRect("Minimap", parent, layer);
            _root.anchorMin = _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);

            Image border = CreateImage("Border", _root, DotSprite(), new Color(0.95f, 0.93f, 0.86f, 0.95f));
            Stretch(border.rectTransform, 0f);

            _window = CreateRect("Window", _root, layer);
            Stretch(_window, Border);
            _window.gameObject.AddComponent<RectMask2D>();

            Image backdrop = CreateImage("Backdrop", _window, DotSprite(), new Color(0.07f, 0.09f, 0.08f, 0.94f));
            Stretch(backdrop.rectTransform, 0f);

            _content = CreateRect("Content", _window, layer);
            _content.anchorMin = _content.anchorMax = new Vector2(0.5f, 0.5f);
            _content.pivot = Vector2.zero;
            _content.anchoredPosition = Vector2.zero;

            _mapImage = CreateImage("Map", _content, _mapSprite, Color.white);
            _mapRect = _mapImage.rectTransform;
            _mapRect.anchorMin = _mapRect.anchorMax = Vector2.zero;
            _mapRect.pivot = new Vector2(0.5f, 0.5f);

            _markersRoot = CreateRect("Markers", _window, layer);
            Stretch(_markersRoot, 0f);

            _heading = CreateRect("Heading", _window, layer);
            _heading.anchorMin = _heading.anchorMax = new Vector2(0.5f, 0.5f);
            _heading.pivot = new Vector2(0.5f, 0.5f);
            _heading.anchoredPosition = Vector2.zero;
            _heading.sizeDelta = Vector2.zero;

            Image shadow = CreateImage("ArrowShadow", _heading, ArrowSprite(), new Color(0f, 0f, 0f, 0.9f));
            shadow.rectTransform.anchorMin = shadow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            shadow.rectTransform.sizeDelta = new Vector2(22f, 22f);
            Image arrow = CreateImage("Arrow", _heading, ArrowSprite(), Color.white);
            arrow.rectTransform.anchorMin = arrow.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            arrow.rectTransform.sizeDelta = new Vector2(15f, 15f);

            _root.SetAsLastSibling();
            _laidOut = false;
            MinimapPlugin.LogSource.LogInfo("Minimap parented to '" + parent.name + "'.");
        }

        void LayoutMap()
        {
            if (_mapSprite == null)
                return;

            _mapImage.sprite = _mapSprite;
            _mapImage.color = Color.white;
            _mapImage.type = Image.Type.Simple;
            _mapImage.preserveAspect = false;

            Vector2 bl = new Vector2(0f, 0f);
            Vector2 tr = new Vector2(1f, 1f);
            _useTopDown = _bounds != null;
            if (_bounds != null)
            {
                try
                {
                    bl = _bounds.GetTopDownPositionFrom01(new Vector2(0f, 0f));
                    tr = _bounds.GetTopDownPositionFrom01(new Vector2(1f, 1f));
                }
                catch (Exception)
                {
                    _useTopDown = false;
                }
            }

            float span = Mathf.Max(Mathf.Abs(tr.x - bl.x), Mathf.Abs(tr.y - bl.y));
            if (!_useTopDown || span < 0.0001f || float.IsNaN(span))
            {
                _useTopDown = false;
                Rect spriteRect = _mapSprite.rect;
                _fallbackSize = new Vector2(Mathf.Max(spriteRect.width, 1f), Mathf.Max(spriteRect.height, 1f));
                _mapRect.anchoredPosition = _fallbackSize * 0.5f;
                _mapRect.sizeDelta = _fallbackSize;
                _mapRect.localScale = Vector3.one;
            }
            else
            {
                _mapRect.anchoredPosition = (bl + tr) * 0.5f;
                _mapRect.sizeDelta = new Vector2(Mathf.Abs(tr.x - bl.x), Mathf.Abs(tr.y - bl.y));
                _mapRect.localScale = new Vector3(tr.x < bl.x ? -1f : 1f, tr.y < bl.y ? -1f : 1f, 1f);
            }

            _laidOut = true;
            if (!_loggedReady)
            {
                _loggedReady = true;
                MinimapPlugin.LogSource.LogInfo("Minimap using sprite '" + _mapSprite.name + "'.");
            }
        }

        void PlaceUnderTimer()
        {
            RectTransform parent = _hud.transform as RectTransform;
            if (parent == null)
                return;

            if (_root.parent != parent)
            {
                _root.SetParent(parent, false);
                AdoptLayer(_root, parent.gameObject.layer);
                _root.SetAsLastSibling();
            }

            float size = Mathf.Clamp(MinimapPlugin.Size.Value, 96f, 512f);
            _root.anchorMin = _root.anchorMax = new Vector2(0f, 1f);
            _root.pivot = new Vector2(0f, 1f);
            _root.localScale = Vector3.one;
            _root.sizeDelta = new Vector2(size, size);

            float x = 16f;
            float y = -72f - MinimapPlugin.Gap.Value;
            RectTransform clock = ClockRect();
            if (clock != null)
            {
                clock.GetWorldCorners(_corners);
                Vector3 bottomLeft = _corners[0];
                Vector3 down = _corners[0] - _corners[1];
                float paneHeight = down.magnitude;
                float clockHeight = Mathf.Abs(clock.rect.height);
                if (clockHeight < 1f)
                    clockHeight = Mathf.Abs(clock.sizeDelta.y);
                if (paneHeight > 0.5f && clockHeight >= 1f)
                {
                    bottomLeft += down / paneHeight * (paneHeight * (MinimapPlugin.Gap.Value / clockHeight));
                    Rect parentRect = parent.rect;
                    Vector3 local = parent.InverseTransformPoint(bottomLeft);
                    x = local.x - parentRect.xMin;
                    y = local.y - parentRect.yMax;
                    if (float.IsNaN(x) || float.IsNaN(y) || Mathf.Abs(x) > 4000f || Mathf.Abs(y) > 4000f)
                    {
                        x = 16f;
                        y = -72f - MinimapPlugin.Gap.Value;
                    }
                }
            }

            _root.anchoredPosition = new Vector2(x, y);
        }

        RectTransform ClockRect()
        {
            if (_time == null || _time.TimePane == null)
                return null;
            return _time.TimePane;
        }

        bool Follow(SkopjePlayer player)
        {
            Vector3 pos = player.transform.position;
            Vector2 here = ToMap(pos);
            Vector2 east = ToMap(pos + Vector3.right * SampleMeters) - here;
            Vector2 north = ToMap(pos + Vector3.forward * SampleMeters) - here;
            float ppm = east.magnitude / SampleMeters;
            if (ppm < 0.0000001f)
                ppm = north.magnitude / SampleMeters;
            if (ppm < 0.0000001f || float.IsNaN(ppm) || float.IsInfinity(ppm))
                return false;

            float inner = Mathf.Max(16f, _root.sizeDelta.x - Border * 2f);
            float radius = Mathf.Clamp(MinimapPlugin.ViewRadius.Value, 25f, 600f);
            float zoom = (inner * 0.5f) / (radius * ppm);
            if (float.IsNaN(zoom) || float.IsInfinity(zoom))
                return false;

            _content.anchoredPosition = -here * zoom;
            _content.localScale = new Vector3(zoom, zoom, 1f);
            _heading.anchoredPosition = Vector2.zero;

            Vector3 forward = player.transform.forward;
            Vector2 mapForward = east * forward.x + north * forward.z;
            if (mapForward.sqrMagnitude > 0.000001f)
            {
                float degrees = Mathf.Atan2(mapForward.x, mapForward.y) * Mathf.Rad2Deg;
                _heading.localRotation = Quaternion.Euler(0f, 0f, -degrees);
            }

            _playerMap = here;
            _zoom = zoom;
            return true;
        }

        void ShowWholeMap(SkopjePlayer player)
        {
            if (_mapRect == null)
                return;

            Vector2 size = _mapRect.sizeDelta;
            float inner = Mathf.Max(16f, _root.sizeDelta.x - Border * 2f);
            float zoom = inner / Mathf.Max(Mathf.Max(size.x, size.y), 1f);
            Vector2 center = _mapRect.anchoredPosition;
            _content.anchoredPosition = -center * zoom;
            _content.localScale = new Vector3(zoom, zoom, 1f);
            _playerMap = center;
            _zoom = zoom;

            if (player == null || _bounds == null)
            {
                _heading.anchoredPosition = Vector2.zero;
                return;
            }

            try
            {
                Vector2 here = ToMap(player.transform.position);
                _heading.anchoredPosition = (here - center) * zoom;
            }
            catch (Exception)
            {
                _heading.anchoredPosition = Vector2.zero;
            }
        }

        Vector2 _playerMap;
        float _zoom = 1f;

        Vector2 ToMap(Vector3 world)
        {
            Vector2 uv = _bounds.GetLevelObjectPosition01(world);
            if (!_useTopDown)
                return new Vector2(uv.x * _fallbackSize.x, uv.y * _fallbackSize.y);
            return _bounds.GetTopDownPositionFrom01(uv);
        }

        void RescanMarkers(SkopjePlayer player)
        {
            _nearby.Clear();
            _nearbyDist.Clear();

            float radius = Mathf.Clamp(MinimapPlugin.ViewRadius.Value, 25f, 600f) * 1.25f;
            float limitSq = radius * radius;
            Vector3 origin = player.transform.position;

            var indicators = Resources.FindObjectsOfTypeAll<MapIndicator>();
            for (int i = 0; i < indicators.Length; i++)
            {
                MapIndicator indicator = indicators[i];
                if (!IsSceneObject(indicator) || !indicator.gameObject.activeInHierarchy)
                    continue;
                if (IsLocalPlayerMarker(indicator, player))
                    continue;

                Vector3 delta = indicator.transform.position - origin;
                delta.y = 0f;
                float distSq = delta.sqrMagnitude;
                if (distSq > limitSq)
                    continue;

                InsertNearest(indicator, distSq);
            }

            int generation = Time.frameCount;
            float markerSize = Mathf.Clamp(MinimapPlugin.MarkerSize.Value, 8f, 40f);
            for (int i = 0; i < _nearby.Count; i++)
            {
                MapIndicator indicator = _nearby[i];
                MarkerSlot slot = SlotFor(indicator.GetInstanceID());
                slot.Indicator = indicator;
                slot.Generation = generation;
                slot.Rect.gameObject.SetActive(true);

                Sprite icon = null;
                try
                {
                    icon = indicator.GetIcon();
                }
                catch (Exception)
                {
                    icon = null;
                }

                slot.Image.sprite = icon != null ? icon : DotSprite();
                slot.Image.color = icon != null ? indicator.Color : new Color(1f, 0.82f, 0.25f, 1f);
                if (slot.Image.color.a < 0.05f)
                    slot.Image.color = icon != null ? Color.white : new Color(1f, 0.82f, 0.25f, 1f);

                float scale = 1f;
                try
                {
                    if (indicator.Scale > 0.05f)
                        scale = Mathf.Clamp(indicator.Scale, 0.4f, 2.5f);
                }
                catch (Exception)
                {
                    scale = 1f;
                }

                slot.Rect.sizeDelta = Vector2.one * (markerSize * scale);
            }

            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Generation != generation)
                    _slots[i].Rect.gameObject.SetActive(false);
            }
        }

        void InsertNearest(MapIndicator indicator, float distSq)
        {
            int index = _nearby.Count;
            for (int i = 0; i < _nearby.Count; i++)
            {
                if (distSq < _nearbyDist[i])
                {
                    index = i;
                    break;
                }
            }

            if (index >= MaxMarkers)
                return;

            _nearby.Insert(index, indicator);
            _nearbyDist.Insert(index, distSq);
            if (_nearby.Count > MaxMarkers)
            {
                _nearby.RemoveAt(_nearby.Count - 1);
                _nearbyDist.RemoveAt(_nearbyDist.Count - 1);
            }
        }

        void MoveMarkers()
        {
            float limit = (_root.sizeDelta.x - Border * 2f) * 0.5f + 8f;
            for (int i = 0; i < _slots.Count; i++)
            {
                MarkerSlot slot = _slots[i];
                if (!slot.Rect.gameObject.activeSelf)
                    continue;
                if (slot.Indicator == null)
                {
                    slot.Rect.gameObject.SetActive(false);
                    continue;
                }

                Vector2 point = ToMap(slot.Indicator.transform.position);
                Vector2 local = (point - _playerMap) * _zoom;
                if (Mathf.Abs(local.x) > limit || Mathf.Abs(local.y) > limit)
                {
                    slot.Image.enabled = false;
                    continue;
                }

                slot.Image.enabled = true;
                slot.Rect.anchoredPosition = local;
            }
        }

        void HideMarkers()
        {
            for (int i = 0; i < _slots.Count; i++)
                _slots[i].Rect.gameObject.SetActive(false);
        }

        MarkerSlot SlotFor(int id)
        {
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Id == id)
                    return _slots[i];
            }

            var rect = CreateRect("Marker", _markersRoot, _markersRoot.gameObject.layer);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.one * 16f;

            Image image = rect.gameObject.AddComponent<Image>();
            image.raycastTarget = false;
            image.maskable = true;
            image.preserveAspect = true;

            var slot = new MarkerSlot
            {
                Id = id,
                Rect = rect,
                Image = image
            };
            _slots.Add(slot);
            return slot;
        }

        static bool IsLocalPlayerMarker(MapIndicator indicator, SkopjePlayer player)
        {
            if (indicator.transform == player.transform || indicator.transform.IsChildOf(player.transform))
                return true;

            try
            {
                PlayerMapIndicator playerMarker = indicator.GetComponent<PlayerMapIndicator>();
                return playerMarker != null && playerMarker.Player != null && playerMarker.Player.IsOwner;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static UIView FindIngameView()
        {
            var views = Resources.FindObjectsOfTypeAll<UIView>();
            UIView inactive = null;
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

        static Sprite FindLooseMapSprite()
        {
            var sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            Sprite best = null;
            float bestArea = 256f * 256f;
            for (int i = 0; i < sprites.Length; i++)
            {
                Sprite sprite = sprites[i];
                if (sprite == null)
                    continue;
                string name = sprite.name;
                if (name.IndexOf("map", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (name.IndexOf("icon", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("pin", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                Rect rect = sprite.rect;
                float area = rect.width * rect.height;
                if (area > bestArea)
                {
                    best = sprite;
                    bestArea = area;
                }
            }

            return best;
        }

        static TimeDisplay PickTimer()
        {
            var displays = Resources.FindObjectsOfTypeAll<TimeDisplay>();
            TimeDisplay fallback = null;
            for (int i = 0; i < displays.Length; i++)
            {
                TimeDisplay display = displays[i];
                if (!IsSceneObject(display) || display.TimePane == null)
                    continue;
                if (display.gameObject.activeInHierarchy)
                    return display;
                fallback ??= display;
            }

            return fallback;
        }

        static MapControls PickMap()
        {
            var maps = Resources.FindObjectsOfTypeAll<MapControls>();
            MapControls fallback = null;
            for (int i = 0; i < maps.Length; i++)
            {
                MapControls map = maps[i];
                if (!IsSceneObject(map) || FindMapGraphic(map) == null)
                    continue;
                if (map.gameObject.activeInHierarchy)
                    return map;
                fallback ??= map;
            }

            return fallback;
        }

        static Image FindMapGraphic(MapControls map)
        {
            if (map == null)
                return null;

            if (map.MapBg != null && map.MapBg.sprite != null && map.MapBg.sprite.rect.width >= 128f)
                return map.MapBg;

            Image best = map.MapBg != null && map.MapBg.sprite != null ? map.MapBg : null;
            float bestArea = SpriteArea(best);
            if (map.MapTransform != null)
                Consider(map.MapTransform, ref best, ref bestArea);

            var extras = map.FallbackMapTransforms;
            if (extras != null)
            {
                for (int i = 0; i < extras.Length; i++)
                {
                    if (extras[i] != null)
                        Consider(extras[i], ref best, ref bestArea);
                }
            }

            return best;
        }

        static void Consider(Transform root, ref Image best, ref float bestArea)
        {
            var images = root.GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                float area = SpriteArea(images[i]);
                if (area > bestArea)
                {
                    best = images[i];
                    bestArea = area;
                }
            }
        }

        static float SpriteArea(Image image)
        {
            if (image == null || image.sprite == null)
                return 0f;
            Rect rect = image.sprite.rect;
            return rect.width * rect.height;
        }

        static LevelBounds PickBounds()
        {
            var all = Resources.FindObjectsOfTypeAll<LevelBounds>();
            LevelBounds best = null;
            float bestArea = -1f;
            for (int i = 0; i < all.Length; i++)
            {
                LevelBounds bounds = all[i];
                if (!IsSceneObject(bounds))
                    continue;

                float area = 0f;
                try
                {
                    BoxCollider box = bounds.BoxCollider;
                    if (box != null)
                    {
                        Vector3 size = box.bounds.size;
                        area = Mathf.Abs(size.x * size.z);
                    }
                }
                catch (Exception)
                {
                    area = 0f;
                }

                if (area > bestArea)
                {
                    bestArea = area;
                    best = bounds;
                }
            }

            return best;
        }

        static bool IsSceneObject(Component component)
        {
            return component != null && component.gameObject.scene.IsValid();
        }

        static RectTransform CreateRect(string name, Transform parent, int layer)
        {
            var go = new GameObject(name);
            go.layer = layer;
            RectTransform rect = go.AddComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            return rect;
        }

        static Image CreateImage(string name, Transform parent, Sprite sprite, Color color)
        {
            RectTransform rect = CreateRect(name, parent, parent.gameObject.layer);
            Image image = rect.gameObject.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;
            image.maskable = true;
            return image;
        }

        static void Stretch(RectTransform rect, float inset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = new Vector2(inset, inset);
            rect.offsetMax = new Vector2(-inset, -inset);
            rect.localScale = Vector3.one;
        }

        static void AdoptLayer(Transform target, int layer)
        {
            target.gameObject.layer = layer;
            for (int i = 0; i < target.childCount; i++)
                AdoptLayer(target.GetChild(i), layer);
        }

        static Sprite DotSprite()
        {
            if (_dotSprite != null)
                return _dotSprite;

            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.name = "MinimapDot";
            texture.SetPixel(0, 0, Color.white);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            _dotSprite = Sprite.Create(texture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
            _dotSprite.hideFlags = HideFlags.HideAndDontSave;
            return _dotSprite;
        }

        static Sprite ArrowSprite()
        {
            if (_arrowSprite != null)
                return _arrowSprite;

            const int n = 32;
            var texture = new Texture2D(n, n, TextureFormat.RGBA32, false);
            texture.name = "MinimapArrow";
            texture.filterMode = FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;
            var clear = new Color(1f, 1f, 1f, 0f);
            Vector2 a = new Vector2(15.5f, 29.5f);
            Vector2 b = new Vector2(4f, 8.5f);
            Vector2 c = new Vector2(27f, 8.5f);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                    texture.SetPixel(x, y, PointInTriangle(new Vector2(x + 0.5f, y + 0.5f), a, b, c) ? Color.white : clear);
            }

            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            _arrowSprite = Sprite.Create(texture, new Rect(0f, 0f, n, n), new Vector2(0.5f, 0.5f), n);
            _arrowSprite.hideFlags = HideFlags.HideAndDontSave;
            return _arrowSprite;
        }

        static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sign(p, a, b);
            float d2 = Sign(p, b, c);
            float d3 = Sign(p, c, a);
            bool hasNeg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool hasPos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(hasNeg && hasPos);
        }

        static float Sign(Vector2 p, Vector2 a, Vector2 b)
        {
            return (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
        }

        sealed class MarkerSlot
        {
            public int Id;
            public int Generation;
            public MapIndicator Indicator;
            public RectTransform Rect;
            public Image Image;
        }
    }
}
