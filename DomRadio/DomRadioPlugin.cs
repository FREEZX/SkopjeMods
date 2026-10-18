using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FMODUnity;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Skopje.Modding;
using UnityEngine;

namespace Skopje.DomRadio
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class DomRadioPlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.domradio";
        public const string PluginName = "Skopje Dom Radio";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;

        internal static ConfigEntry<KeyCode> Toggle;
        internal static ConfigEntry<KeyCode> NextTrack;
        internal static ConfigEntry<KeyCode> PreviousTrack;
        internal static ConfigEntry<bool> Shuffle;
        internal static ConfigEntry<bool> MuteGameMusic;
        internal static ConfigEntry<string> MusicBusFilter;
        internal static ConfigEntry<string> Folder;
        internal static ConfigEntry<bool> ShowNowPlaying;

        internal static SkopjePlayer LocalPlayer;

        static GameObject _host;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStopped;

        public override void Load()
        {
            LogSource = Log;
            Toggle = Config.Bind("Radio", "Toggle", KeyCode.F8, "Turn the radio on or off.");
            NextTrack = Config.Bind("Radio", "NextTrack", KeyCode.F10, "Skip to the next file.");
            PreviousTrack = Config.Bind("Radio", "PreviousTrack", KeyCode.F9, "Go back to the previous file.");
            Shuffle = Config.Bind("Radio", "Shuffle", false, "Play the folder in a random order. Off plays files by name.");
            MuteGameMusic = Config.Bind("Radio", "MuteGameMusic", true, "While the radio is on, mute the game's music buses. That includes the Dom bus track. Sound effects stay on.");
            MusicBusFilter = Config.Bind("Radio", "MusicBusFilter", "Music", "Mute FMOD buses whose path contains this text. Music covers the Dom bus home track.");
            Folder = Config.Bind("Radio", "Folder", "", "Folder of .mp3 and .wav files. Leave empty to use BepInEx/plugins/DomRadio/Music.");
            ShowNowPlaying = Config.Bind("Radio", "ShowNowPlaying", true, "Show the current file name while the radio is on.");

            ClassInjector.RegisterTypeInIl2Cpp<DomRadioPlayer>();

            try
            {
                Directory.CreateDirectory(MusicDirectory);
            }
            catch (Exception ex)
            {
                Log.LogWarning("Could not create the music folder: " + ex.Message);
            }

            _onPlayerStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(OnPlayerStarted));
            _onPlayerStopped = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(OnPlayerStopped));
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);
            ModAPI.add_OnPlayerStoppedClient(_onPlayerStopped);

            _host = new GameObject("SkopjeDomRadio");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _host.hideFlags = HideFlags.HideAndDontSave;
            _host.AddComponent<DomRadioPlayer>();

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. Put .mp3 and .wav files in {MusicDirectory}. Toggle {Toggle.Value}, next {NextTrack.Value}, previous {PreviousTrack.Value}.");
        }

        public override bool Unload()
        {
            if (DomRadioPlayer.Instance != null)
                DomRadioPlayer.Instance.TurnOff(false);
            GameMusic.Release();
            LocalPlayer = null;
            if (_onPlayerStarted != null)
                ModAPI.remove_OnPlayerStartedClient(_onPlayerStarted);
            if (_onPlayerStopped != null)
                ModAPI.remove_OnPlayerStoppedClient(_onPlayerStopped);
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }

            return true;
        }

        internal static string MusicDirectory
        {
            get
            {
                string custom = Folder == null ? null : Folder.Value;
                if (!string.IsNullOrWhiteSpace(custom))
                    return custom;
                return Path.Combine(Paths.PluginPath, "DomRadio", "Music");
            }
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

        internal static float MusicLevel()
        {
            try
            {
                OptionsManager manager = OptionsManager.Instance;
                if (manager == null || manager.OptionsData == null)
                    return 1f;

                return Unit(manager.OptionsData.MasterVolume) * Unit(manager.OptionsData.MusicVolume);
            }
            catch (Exception)
            {
                return 1f;
            }
        }

        static float Unit(float value)
        {
            if (value > 1f)
                value *= 0.01f;
            return Mathf.Clamp01(value);
        }
    }

    public class DomRadioPlayer : MonoBehaviour
    {
        const float MissedPlaySeconds = 2f;

        public static DomRadioPlayer Instance { get; private set; }

        readonly List<string> _tracks = new List<string>();
        FMOD.Sound _sound;
        FMOD.Channel _channel;
        bool _hasSound;
        int _index;
        int _failures;
        bool _on;
        bool _suspended;
        bool _skipPending;
        bool _heardPlayback;
        bool _loggedEmpty;
        float _playDeadline;
        string _nowPlaying = string.Empty;
        string _notice = string.Empty;
        float _noticeUntil;
        GUIStyle _label;
        Texture2D _backdrop;
        int _styleHeight;

        public DomRadioPlayer(IntPtr pointer) : base(pointer)
        {
        }

        void Awake()
        {
            Instance = this;
        }

        void OnDestroy()
        {
            TurnOff(false);
            if (_backdrop != null)
                Destroy(_backdrop);
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            bool inside = InDom();
            if (Pressed(DomRadioPlugin.Toggle.Value))
            {
                if (_on)
                    TurnOff(true);
                else if (inside)
                    TurnOn();
                else
                    Notify("Radio only plays in the Dom", 2f);
            }

            if (!_on)
                return;

            if (!inside)
            {
                if (!_suspended)
                    Suspend();
                return;
            }

            if (_suspended)
                Resume();

            if (Pressed(DomRadioPlugin.NextTrack.Value))
                Advance(1);
            else if (Pressed(DomRadioPlugin.PreviousTrack.Value))
                Advance(-1);

            PumpLoad();
            // Mute the Dom track only while one of our files is actually audible.
            if (DomRadioPlugin.MuteGameMusic.Value && IsAudible())
                GameMusic.Hold();
            else if (!DomRadioPlugin.MuteGameMusic.Value)
                GameMusic.Release();
        }

        void OnGUI()
        {
            if (Event.current == null || Event.current.type != EventType.Repaint || GUI.skin == null)
                return;

            EnsureStyle();
            const float width = 640f;
            const float height = 36f;
            float textWidth = width - _label.padding.left - _label.padding.right;

            string text = null;
            if (_on && !_suspended && DomRadioPlugin.ShowNowPlaying.Value && !string.IsNullOrEmpty(_nowPlaying))
                text = Fit(_nowPlaying, textWidth);
            else if (Time.unscaledTime <= _noticeUntil && !string.IsNullOrEmpty(_notice))
                text = Fit(_notice, textWidth);
            if (text == null)
                return;

            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 96f, width, height);
            Color previous = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.62f);
            GUI.DrawTexture(rect, _backdrop);
            GUI.color = previous;
            GUI.Label(rect, text, _label);
        }

        public void TurnOn()
        {
            ReloadTracks();
            if (_tracks.Count == 0)
            {
                if (!_loggedEmpty)
                {
                    _loggedEmpty = true;
                    DomRadioPlugin.LogSource.LogInfo("Radio disabled. No .mp3 or .wav files in " + DomRadioPlugin.MusicDirectory + ". Dom music will keep playing.");
                }

                return;
            }

            _loggedEmpty = false;
            _on = true;
            _failures = 0;
            _index = 0;
            _nowPlaying = TrackLabel(_tracks[0]);
            try
            {
                PlayFile(_tracks[0]);
            }
            catch (Exception ex)
            {
                DomRadioPlugin.LogSource.LogWarning("Could not start the radio: " + ex.Message);
                TurnOff(false);
                DomRadioPlugin.LogSource.LogInfo("Radio disabled. Dom music restored.");
                return;
            }

            DomRadioPlugin.LogSource.LogInfo("Radio on. " + _tracks.Count + " file(s) in " + DomRadioPlugin.MusicDirectory);
        }

        public void TurnOff(bool announce)
        {
            _on = false;
            _suspended = false;
            _skipPending = false;
            _heardPlayback = false;
            _nowPlaying = string.Empty;
            StopSound();
            GameMusic.Release();
            if (announce)
            {
                Notify("Radio off", 2f);
                DomRadioPlugin.LogSource.LogInfo("Radio off. Dom music restored.");
            }
        }

        void Advance(int direction)
        {
            if (_tracks.Count == 0)
            {
                TurnOff(true);
                return;
            }

            int next = _index + direction;
            if (next >= _tracks.Count)
                next = 0;
            else if (next < 0)
                next = _tracks.Count - 1;

            _index = next;
            _nowPlaying = TrackLabel(_tracks[_index]);
            PlayFile(_tracks[_index]);
        }

        void ReloadTracks()
        {
            _tracks.Clear();
            string folder = DomRadioPlugin.MusicDirectory;
            if (!Directory.Exists(folder))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                }
                catch (Exception ex)
                {
                    DomRadioPlugin.LogSource.LogWarning("Could not create the music folder: " + ex.Message);
                    return;
                }
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception ex)
            {
                DomRadioPlugin.LogSource.LogWarning("Could not read the music folder: " + ex.Message);
                return;
            }

            for (int i = 0; i < files.Length; i++)
            {
                string extension = Path.GetExtension(files[i]);
                if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
                    _tracks.Add(files[i]);
            }

            _tracks.Sort(StringComparer.OrdinalIgnoreCase);
            if (DomRadioPlugin.Shuffle.Value)
                ShuffleTracks();
        }

        void ShuffleTracks()
        {
            var random = new System.Random();
            for (int i = _tracks.Count - 1; i > 0; i--)
            {
                int swap = random.Next(i + 1);
                string file = _tracks[i];
                _tracks[i] = _tracks[swap];
                _tracks[swap] = file;
            }
        }

        void PlayFile(string path)
        {
            StopSound();
            _heardPlayback = false;
            _nowPlaying = TrackLabel(path);
            if (!RuntimeManager.IsInitialized)
            {
                DomRadioPlugin.LogSource.LogWarning("Audio system is not ready yet.");
                _skipPending = true;
                return;
            }

            FMOD.System core = RuntimeManager.CoreSystem;
            if (!core.hasHandle())
            {
                DomRadioPlugin.LogSource.LogWarning("Audio system is not ready yet.");
                _skipPending = true;
                return;
            }

            FMOD.MODE mode = FMOD.MODE.CREATESTREAM | FMOD.MODE._2D | FMOD.MODE.LOOP_OFF;
            FMOD.Sound sound;
            FMOD.RESULT result = core.createSound(path, mode, out sound);
            if (result != FMOD.RESULT.OK || !sound.hasHandle())
            {
                DomRadioPlugin.LogSource.LogWarning("Could not open " + Path.GetFileName(path) + " (" + result + ").");
                _skipPending = true;
                return;
            }

            FMOD.ChannelGroup master;
            core.getMasterChannelGroup(out master);
            FMOD.Channel channel;
            result = core.playSound(sound, master, false, out channel);
            if (result != FMOD.RESULT.OK || !channel.hasHandle())
            {
                sound.release();
                DomRadioPlugin.LogSource.LogWarning("Could not play " + Path.GetFileName(path) + " (" + result + ").");
                _skipPending = true;
                return;
            }

            channel.setVolume(DomRadioPlugin.MusicLevel());
            _sound = sound;
            _channel = channel;
            _hasSound = true;
            _playDeadline = Time.unscaledTime + MissedPlaySeconds;
        }

        void PumpLoad()
        {
            if (_skipPending)
            {
                _skipPending = false;
                OnTrackFailed();
                return;
            }

            if (!_hasSound)
                return;

            if (IsAudible())
            {
                _heardPlayback = true;
                _failures = 0;
                _channel.setVolume(DomRadioPlugin.MusicLevel());
                return;
            }

            if (_heardPlayback || Time.unscaledTime >= _playDeadline)
                Advance(1);
        }

        bool IsAudible()
        {
            if (!_hasSound || !_channel.hasHandle())
                return false;
            bool playing = false;
            return _channel.isPlaying(out playing) == FMOD.RESULT.OK && playing;
        }

        void OnTrackFailed()
        {
            _failures++;
            if (_tracks.Count == 0 || _failures >= _tracks.Count)
            {
                TurnOff(false);
                DomRadioPlugin.LogSource.LogInfo("Radio disabled. No playable files in " + DomRadioPlugin.MusicDirectory + ". Dom music restored.");
                return;
            }

            Advance(1);
        }

        void StopSound()
        {
            if (_hasSound && _channel.hasHandle())
                _channel.stop();
            if (_hasSound && _sound.hasHandle())
                _sound.release();
            _hasSound = false;
        }

        void Notify(string text, float seconds)
        {
            _notice = text ?? string.Empty;
            _noticeUntil = Time.unscaledTime + seconds;
        }

        static bool _loggedInput;

        static bool Pressed(KeyCode key)
        {
            if (key == KeyCode.None)
                return false;
            try
            {
                return Input.GetKeyDown(key);
            }
            catch (Exception ex)
            {
                if (!_loggedInput)
                {
                    _loggedInput = true;
                    DomRadioPlugin.LogSource.LogWarning("Keyboard input is unavailable: " + ex.Message);
                }

                return false;
            }
        }

        bool InDom()
        {
            SkopjePlayer player = DomRadioPlugin.FindLocalPlayer();
            if (player == null)
                return false;

            try
            {
                return player.InsideVehicle != null;
            }
            catch (Exception)
            {
                DomRadioPlugin.LocalPlayer = null;
                return false;
            }
        }

        void Suspend()
        {
            _suspended = true;
            if (_hasSound && _channel.hasHandle())
                _channel.setPaused(true);
            GameMusic.Release();
        }

        void Resume()
        {
            _suspended = false;
            if (_hasSound && _channel.hasHandle())
            {
                _channel.setPaused(false);
                _channel.setVolume(DomRadioPlugin.MusicLevel());
                return;
            }

            if (_index >= 0 && _index < _tracks.Count)
                PlayFile(_tracks[_index]);
        }

        string Fit(string text, float width)
        {
            if (string.IsNullOrEmpty(text) || _label.CalcSize(new GUIContent(text)).x <= width)
                return text;

            const string ellipsis = "...";
            int low = 0;
            int high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                string candidate = text.Substring(0, mid).TrimEnd() + ellipsis;
                if (_label.CalcSize(new GUIContent(candidate)).x <= width)
                    low = mid;
                else
                    high = mid - 1;
            }

            if (low <= 0)
                return ellipsis;
            return text.Substring(0, low).TrimEnd() + ellipsis;
        }

        static string TrackLabel(string path)
        {
            return "Radio — " + Path.GetFileNameWithoutExtension(path);
        }

        void EnsureStyle()
        {
            if (_backdrop == null)
            {
                _backdrop = new Texture2D(1, 1);
                _backdrop.SetPixel(0, 0, Color.white);
                _backdrop.Apply();
            }

            if (_label != null && _styleHeight == Screen.height)
                return;

            _styleHeight = Screen.height;
            _label = new GUIStyle(GUI.skin.label);
            _label.alignment = TextAnchor.MiddleCenter;
            _label.wordWrap = false;
            _label.clipping = TextClipping.Clip;
            _label.padding = new RectOffset(12, 12, 0, 0);
            _label.fontSize = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.018f), 14, 28);
            _label.normal.textColor = Color.white;
        }
    }

    // The Dom bus soundtrack is event:/Music/Bus/Home, on the game's Music buses.
    // Muting those buses leaves the events running, so the track returns when the radio turns off.
    static class GameMusic
    {
        sealed class HeldBus
        {
            public FMOD.Studio.Bus Bus;
            public bool WasMuted;
        }

        static readonly List<HeldBus> _held = new List<HeldBus>();
        static bool _loggedMiss;
        static float _nextDiscover;

        public static void Hold()
        {
            if (!DomRadioPlugin.MuteGameMusic.Value)
                return;
            if (!RuntimeManager.IsInitialized)
                return;

            if (_held.Count == 0 && Time.unscaledTime >= _nextDiscover)
            {
                _nextDiscover = Time.unscaledTime + 1f;
                Discover();
            }

            int valid = 0;
            for (int i = 0; i < _held.Count; i++)
            {
                FMOD.Studio.Bus bus = _held[i].Bus;
                if (!bus.isValid())
                    continue;
                valid++;
                bus.setMute(true);
            }

            // Banks reload with a new level. Drop dead handles so the next pass can find the music buses again.
            if (_held.Count > 0 && valid == 0)
            {
                _held.Clear();
                _nextDiscover = 0f;
            }
        }

        public static void Release()
        {
            for (int i = 0; i < _held.Count; i++)
            {
                FMOD.Studio.Bus bus = _held[i].Bus;
                if (bus.isValid())
                    bus.setMute(_held[i].WasMuted);
            }

            _held.Clear();
            _loggedMiss = false;
            _nextDiscover = 0f;
        }

        static void Discover()
        {
            try
            {
                var studio = RuntimeManager.StudioSystem;
                if (!studio.isValid())
                    return;

                Il2CppStructArray<FMOD.Studio.Bank> banks = null;
                if (studio.getBankList(out banks) != FMOD.RESULT.OK || banks == null || banks.Length == 0)
                    return;

                string filter = DomRadioPlugin.MusicBusFilter.Value ?? string.Empty;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var others = new List<string>();
                for (int i = 0; i < banks.Length; i++)
                {
                    FMOD.Studio.Bank bank = banks[i];
                    if (!bank.isValid())
                        continue;

                    Il2CppStructArray<FMOD.Studio.Bus> buses = null;
                    if (bank.getBusList(out buses) != FMOD.RESULT.OK || buses == null)
                        continue;

                    for (int b = 0; b < buses.Length; b++)
                    {
                        FMOD.Studio.Bus bus = buses[b];
                        if (!bus.isValid())
                            continue;

                        string path = null;
                        if (bus.getPath(out path) != FMOD.RESULT.OK || string.IsNullOrEmpty(path))
                            continue;
                        if (!seen.Add(path))
                            continue;
                        if (filter.Length == 0 || path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            others.Add(path);
                            continue;
                        }

                        bool wasMuted = false;
                        bus.getMute(out wasMuted);
                        _held.Add(new HeldBus { Bus = bus, WasMuted = wasMuted });
                        DomRadioPlugin.LogSource.LogInfo("Muting " + path);
                    }
                }

                if (_held.Count == 0 && !_loggedMiss)
                {
                    _loggedMiss = true;
                    DomRadioPlugin.LogSource.LogWarning("No music bus matched \"" + filter + "\". Dom music will keep playing. Buses: " + string.Join(", ", others));
                }
            }
            catch (Exception ex)
            {
                DomRadioPlugin.LogSource.LogWarning("Could not find the music buses: " + ex.Message);
            }
        }
    }
}
