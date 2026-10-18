using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FishNet;
using FishNet.Connection;
using FishNet.Managing.Client;
using FishNet.Managing.Server;
using FishNet.Object;
using FishNet.Serializing;
using FishNet.Transporting;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using Skopje.Modding;
using UnityEngine;

namespace PeePeeMod
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class PeePeePlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.peepeemod";
        public const string PluginName = "PeePeeMod";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<KeyCode> Key;
        internal static ConfigEntry<bool> Toggle;

        internal static GameObject Host;

        static Harmony _harmony;
        static SkopjePlayer _local;
        static bool _pissing;
        static bool _latched;
        static float _nextSend;
        static int _errors;
        static bool _loggedInput;

        static Il2CppSystem.Action _onClientStarted;
        static Il2CppSystem.Action _onClientStopped;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStarted;
        static Il2CppSystem.Action<SkopjePlayer> _onPlayerStopped;
        static Il2CppSystem.Action _onGameOver;

        public override void Load()
        {
            LogSource = Log;
            Enabled = Config.Bind("Pee", "Enabled", true, "Hold the key to piss. Everyone in the session sees the stream.");
            Key = Config.Bind("Pee", "Key", KeyCode.P, "Button that starts pissing.");
            Toggle = Config.Bind("Pee", "Toggle", false, "Press once to start and again to stop. Off means hold the key, like Postal.");

            ClassInjector.RegisterTypeInIl2Cpp<PeeDriver>();

            _onClientStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnClientStarted));
            _onClientStopped = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnClientStopped));
            _onPlayerStarted = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(OnPlayerStarted));
            _onPlayerStopped = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<SkopjePlayer>>(new Action<SkopjePlayer>(OnPlayerStopped));
            _onGameOver = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(new Action(OnGameOver));

            ModAPI.add_OnClientStarted(_onClientStarted);
            ModAPI.add_OnClientStopped(_onClientStopped);
            ModAPI.add_OnPlayerStartedClient(_onPlayerStarted);
            ModAPI.add_OnPlayerStoppedClient(_onPlayerStopped);
            ModAPI.add_OnGameOver(_onGameOver);

            _harmony = new Harmony(PluginGuid);
            Patch(AccessTools.Method(typeof(ServerManager), "ParseBroadcast"), nameof(PeeNet.BeforeServerBroadcast));
            Patch(AccessTools.Method(typeof(ClientManager), "ParseBroadcast"), nameof(PeeNet.BeforeClientBroadcast));

            Host = new GameObject("PeePeeMod");
            UnityEngine.Object.DontDestroyOnLoad(Host);
            Host.hideFlags = HideFlags.HideAndDontSave;
            Host.AddComponent<PeeDriver>();

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Hold {Key.Value} to piss. The stream is a server RPC relayed to every client.");
        }

        public override bool Unload()
        {
            _pissing = false;
            _latched = false;
            _local = null;
            if (_harmony != null)
                _harmony.UnpatchSelf();
            if (_onClientStarted != null)
                ModAPI.remove_OnClientStarted(_onClientStarted);
            if (_onClientStopped != null)
                ModAPI.remove_OnClientStopped(_onClientStopped);
            if (_onPlayerStarted != null)
                ModAPI.remove_OnPlayerStartedClient(_onPlayerStarted);
            if (_onPlayerStopped != null)
                ModAPI.remove_OnPlayerStoppedClient(_onPlayerStopped);
            if (_onGameOver != null)
                ModAPI.remove_OnGameOver(_onGameOver);
            PeeVisual.Clear();
            PeePlayers.Clear();
            if (Host != null)
            {
                UnityEngine.Object.Destroy(Host);
                Host = null;
            }

            return true;
        }

        static void Patch(System.Reflection.MethodInfo method, string prefix)
        {
            if (method == null)
            {
                LogSource.LogWarning("Pee RPC hook was not found: " + prefix);
                return;
            }

            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(PeeNet), prefix));
        }

        internal static void Fail(Exception ex)
        {
            if (_errors >= 8 || LogSource == null)
                return;
            _errors++;
            LogSource.LogError("PeePeeMod failed: " + ex.Message);
        }

        internal static void Tick()
        {
            SkopjePlayer player = FindLocal();
            bool want = WantPee(player);
            Vector3 aim = player != null ? PeeVisual.AimOf(player) : Vector3.forward;

            if (want != _pissing)
            {
                _pissing = want;
                _nextSend = Time.unscaledTime + 0.12f;
                if (want && player != null)
                    PeeVisual.ShowLocal(player, aim);
                else if (player != null)
                    PeeVisual.Hide(player.ObjectId);
                if (player != null)
                    PeeNet.ServerRpc(player, want, aim, true);
            }
            else if (want && player != null)
            {
                PeeVisual.ShowLocal(player, aim);
                if (Time.unscaledTime >= _nextSend)
                {
                    _nextSend = Time.unscaledTime + 0.12f;
                    PeeNet.ServerRpc(player, true, aim, false);
                }
            }

            PeeVisual.Tick();
        }

        static bool WantPee(SkopjePlayer player)
        {
            if (Enabled == null || !Enabled.Value || player == null || !CanPee(player))
            {
                _latched = false;
                return false;
            }

            KeyCode key = Key != null ? Key.Value : KeyCode.None;
            if (Toggle != null && Toggle.Value)
            {
                if (Pressed(key))
                    _latched = !_latched;
                return _latched;
            }

            return Held(key);
        }

        static bool CanPee(SkopjePlayer player)
        {
            if (player.IsDead || player.IsFallen || player.IsSpectating || player.IsPaused || player.InInventory)
                return false;
            if (player.InsideVehicle != null)
                return false;
            return player.NetworkObject != null && player.NetworkObject.IsSpawned;
        }

        static SkopjePlayer FindLocal()
        {
            return _local;
        }

        static void OnClientStarted()
        {
            _pissing = false;
            _latched = false;
        }

        static void OnClientStopped()
        {
            _pissing = false;
            _latched = false;
            _local = null;
            PeeVisual.Clear();
            PeePlayers.Clear();
        }

        static void OnPlayerStarted(SkopjePlayer player)
        {
            if (player != null && player.IsOwner)
                _local = player;
            PeePlayers.Remember(player);
        }

        static void OnPlayerStopped(SkopjePlayer player)
        {
            if (player != null)
            {
                PeeVisual.Hide(player.ObjectId);
                PeeVisual.Forget(player.ObjectId);
            }
            PeePlayers.Forget(player);
            if (_local == player)
            {
                _local = null;
                _pissing = false;
                _latched = false;
            }
        }

        static void OnGameOver()
        {
            _pissing = false;
            _latched = false;
            PeeVisual.Clear();
        }

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
                    LogSource.LogWarning("Keyboard input is unavailable: " + ex.Message);
                }

                return false;
            }
        }

        static bool Held(KeyCode key)
        {
            if (key == KeyCode.None)
                return false;
            try
            {
                return Input.GetKey(key);
            }
            catch (Exception ex)
            {
                if (!_loggedInput)
                {
                    _loggedInput = true;
                    LogSource.LogWarning("Keyboard input is unavailable: " + ex.Message);
                }

                return false;
            }
        }
    }

    public class PeeDriver : MonoBehaviour
    {
        void Update()
        {
            try
            {
                PeePeePlugin.Tick();
            }
            catch (Exception ex)
            {
                PeePeePlugin.Fail(ex);
            }
        }
    }

    static class PeePlayers
    {
        static readonly Dictionary<int, SkopjePlayer> _players = new Dictionary<int, SkopjePlayer>();

        public static void Remember(SkopjePlayer player)
        {
            if (player == null)
                return;
            try
            {
                if (player.NetworkObject == null)
                    return;
                _players[player.ObjectId] = player;
            }
            catch (Exception)
            {
            }
        }

        public static void Forget(SkopjePlayer player)
        {
            if (player == null)
                return;
            _players.Remove(player.ObjectId);
        }

        public static void Clear()
        {
            _players.Clear();
        }

        public static SkopjePlayer Find(int objectId)
        {
            SkopjePlayer player;
            if (_players.TryGetValue(objectId, out player))
                return player;
            return null;
        }
    }

    static class PeeNet
    {
        const byte Magic = 0xA7;
        static readonly ushort Key = Hash("Skopje.PeePeeMod.PeeBroadcast");

        static ushort _seq = 1;
        static readonly Dictionary<int, float> _relayAt = new Dictionary<int, float>();
        static bool _loggedRelay;
        static bool _loggedSeen;

        public static void ServerRpc(SkopjePlayer player, bool active, Vector3 aim, bool reliable)
        {
            if (player == null || player.NetworkObject == null || !player.NetworkObject.IsSpawned)
                return;
            if (!InstanceFinder.IsClientStarted)
                return;

            if (aim.sqrMagnitude < 0.0001f)
                aim = Vector3.forward;
            else
                aim.Normalize();

            ushort seq = _seq++;
            if (_seq == 0)
                _seq = 1;

            Send(true, reliable ? Channel.Reliable : Channel.Unreliable, player.ObjectId, active, aim, seq);
        }

        public static bool BeforeServerBroadcast(PooledReader reader, NetworkConnection conn, Channel channel)
        {
            int start = reader != null ? reader.Position : 0;
            int end;
            int objectId;
            bool active;
            Vector3 aim;
            ushort seq;
            if (!TryRead(reader, start, out end, out objectId, out active, out aim, out seq))
                return true;

            try
            {
                Relay(conn, channel, objectId, active, aim, seq);
            }
            catch (Exception ex)
            {
                PeePeePlugin.Fail(ex);
            }

            return false;
        }

        public static bool BeforeClientBroadcast(PooledReader reader, Channel channel)
        {
            int start = reader != null ? reader.Position : 0;
            int end;
            int objectId;
            bool active;
            Vector3 aim;
            ushort seq;
            if (!TryRead(reader, start, out end, out objectId, out active, out aim, out seq))
                return true;

            try
            {
                ObserversRpc(objectId, active, aim, seq);
            }
            catch (Exception ex)
            {
                PeePeePlugin.Fail(ex);
            }

            return false;
        }

        static void Relay(NetworkConnection conn, Channel channel, int objectId, bool active, Vector3 aim, ushort seq)
        {
            if (conn == null || !conn.IsAuthenticated || !InstanceFinder.IsServerStarted)
                return;

            SkopjePlayer player = PeePlayers.Find(objectId);
            if (player == null || !Owns(player, conn))
                return;
            if (active && !Allow(conn.ClientId))
                return;

            if (!_loggedRelay && PeePeePlugin.LogSource != null)
            {
                _loggedRelay = true;
                PeePeePlugin.LogSource.LogInfo("Pee server RPC accepted. Relaying to observers.");
            }

            Send(false, channel, objectId, active, aim, seq);
        }

        static void ObserversRpc(int objectId, bool active, Vector3 aim, ushort seq)
        {
            SkopjePlayer player = PeePlayers.Find(objectId);
            if (player == null || player.IsOwner)
                return;
            if (!PeeVisual.Accept(objectId, seq))
                return;

            if (!_loggedSeen && PeePeePlugin.LogSource != null)
            {
                _loggedSeen = true;
                PeePeePlugin.LogSource.LogInfo("Pee observers RPC received.");
            }

            if (active)
                PeeVisual.ShowRemote(player, aim);
            else
                PeeVisual.Hide(objectId);
        }

        static bool Owns(SkopjePlayer player, NetworkConnection conn)
        {
            if (player.Owner == conn)
                return true;
            return player.OwnerId == conn.ClientId;
        }

        static bool Allow(int clientId)
        {
            float now = Time.unscaledTime;
            float last;
            if (_relayAt.TryGetValue(clientId, out last) && now - last < 0.05f)
                return false;
            _relayAt[clientId] = now;
            return true;
        }

        static void Send(bool toServer, Channel channel, int objectId, bool active, Vector3 aim, ushort seq)
        {
            var manager = InstanceFinder.NetworkManager;
            if (manager == null || manager.TransportManager == null)
                return;
            if (toServer && !InstanceFinder.IsClientStarted)
                return;
            if (!toServer && !InstanceFinder.IsServerStarted)
                return;

            PooledWriter data = WriterPool.Retrieve();
            PooledWriter writer = WriterPool.Retrieve();
            try
            {
                data.WriteByte(Magic);
                data.WriteInt32(objectId);
                data.WriteBoolean(active);
                data.WriteUInt16(seq);
                data.WriteSingle(aim.x);
                data.WriteSingle(aim.y);
                data.WriteSingle(aim.z);

                writer.WriteUInt16((ushort)PacketId.Broadcast);
                writer.WriteUInt16(Key);
                writer.WriteInt32(data.Length);
                writer.WriteArraySegment(data.GetArraySegment());

                var segment = writer.GetArraySegment();
                if (toServer)
                    manager.TransportManager.SendToServer((byte)channel, segment, true, DataOrderType.Default);
                else
                    manager.TransportManager.SendToClients((byte)channel, segment, true);
            }
            finally
            {
                WriterPool.Store(data);
                WriterPool.Store(writer);
            }
        }

        static bool TryRead(PooledReader reader, int start, out int end, out int objectId, out bool active, out Vector3 aim, out ushort seq)
        {
            end = start;
            objectId = 0;
            active = false;
            aim = Vector3.forward;
            seq = 0;
            if (reader == null)
                return false;

            try
            {
                if (reader.Remaining < 2)
                    return false;

                ushort key = reader.ReadUInt16();
                if (key != Key)
                {
                    reader.Position = start;
                    return false;
                }

                int length = reader.ReadInt32();
                int payload = reader.Position;
                if (length < 1 || length > 128 || reader.Remaining < length)
                {
                    reader.Position = start;
                    return false;
                }

                end = payload + length;
                if (reader.ReadByte() != Magic)
                {
                    reader.Position = start;
                    return false;
                }

                objectId = reader.ReadInt32();
                active = reader.ReadBoolean();
                seq = reader.ReadUInt16();
                aim = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                reader.Position = end;
                return true;
            }
            catch (Exception ex)
            {
                PeePeePlugin.Fail(ex);
                try
                {
                    reader.Position = start;
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        static ushort Hash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < text.Length; i++)
                {
                    hash *= 16777619;
                    hash ^= text[i];
                }

                return (ushort)((hash >> 16) ^ hash);
            }
        }
    }
}
