using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using FishNet.Object.Synchronizing;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Skopje.Horde
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class HordePlugin : BasePlugin
    {
        public const string PluginGuid = "com.skopje.horde";
        public const string PluginName = "Skopje Horde";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource LogSource;

        static Harmony _harmony;

        public override void Load()
        {
            LogSource = Log;
            HordeConfig.Bind(Config);
            _harmony = new Harmony(PluginGuid);

            Patch(AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.Awake)), nameof(HordePatches.AfterSpawnSystemAwake), null);
            Patch(AccessTools.Method(typeof(SpawnSystem), nameof(SpawnSystem.OnStartServer)), nameof(HordePatches.AfterSpawnSystemReady), null);
            Patch(AccessTools.Method(typeof(SpawnZone), nameof(SpawnZone.OnStartServer)), nameof(HordePatches.AfterZoneServerStart), nameof(HordePatches.BeforeZoneServerStart));
            Patch(AccessTools.Method(typeof(SpawnZone), nameof(SpawnZone.ResetZone)), null, nameof(HordePatches.BeforeZoneServerStart));
            Patch(AccessTools.Method(typeof(SpawnZone), "get_EnemiesLeft"), nameof(HordePatches.AfterReadEnemiesLeft), null);
            Patch(AccessTools.Method(typeof(SpawnZone), "set_EnemiesLeft"), null, nameof(HordePatches.BeforeWriteEnemiesLeft));
            Patch(AccessTools.Method(typeof(RunSaveManager), "SaveSpawnZones"), nameof(HordePatches.AfterSaveZones), nameof(HordePatches.BeforeSaveZones));
            Patch(AccessTools.Method(typeof(RunSaveManager), "LoadSpawnZones"), nameof(HordePatches.AfterLoadZones), nameof(HordePatches.BeforeLoadZones));
            Patch(AccessTools.Method(typeof(EnemyControlAStar), nameof(EnemyControlAStar.OnStartServer)), nameof(HordePatches.AfterEnemyServerStart), null);
            Patch(AccessTools.Method(typeof(EnemyControlAStar), nameof(EnemyControlAStar.OnStartClient)), nameof(HordePatches.AfterEnemyClientStart), null);
            Patch(AccessTools.Method(typeof(EnemyControlAStar), nameof(EnemyControlAStar.OnStopServer)), nameof(HordePatches.AfterEnemyServerStop), null);
            Patch(AccessTools.Method(typeof(PawnStatus), "get_Damage"), nameof(HordePatches.AfterReadDamage), null);

            LogSource.LogInfo($"{PluginName} {PluginVersion} loaded. Global population x{HordeConfig.GlobalPopulation}, stats {HordeConfig.GlobalStat:0%}. Config: {Config.ConfigFilePath}");
        }

        public override bool Unload()
        {
            if (_harmony != null)
                _harmony.UnpatchSelf();
            HordePatches.Clear();
            return true;
        }

        static void Patch(MethodInfo method, string postfix, string prefix)
        {
            if (method == null)
            {
                LogSource.LogWarning("Horde hook was not found: " + (postfix ?? prefix));
                return;
            }

            _harmony.Patch(
                method,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(HordePatches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(HordePatches), postfix));
        }
    }

    static class HordePatches
    {
        sealed class CapState
        {
            public float Vanilla;
            public float Applied;
            public bool Captured;
        }

        static readonly Dictionary<int, int> _vanillaInitial = new Dictionary<int, int>();
        static readonly Dictionary<int, float> _appliedPopulation = new Dictionary<int, float>();
        static readonly Dictionary<int, int> _loadedLeft = new Dictionary<int, int>();
        static readonly Dictionary<int, int> _vanillaBudget = new Dictionary<int, int>();
        static readonly Dictionary<int, SpawnZone> _trackedZones = new Dictionary<int, SpawnZone>();
        static readonly Dictionary<int, CapState> _caps = new Dictionary<int, CapState>();
        static readonly HashSet<int> _healthScaled = new HashSet<int>();
        static readonly Dictionary<int, float> _pawnStats = new Dictionary<int, float>();
        static readonly Dictionary<int, int> _pawnByEnemy = new Dictionary<int, int>();
        static readonly HashSet<string> _unknownZones = new HashSet<string>();
        static readonly List<float> _statCandidates = new List<float>();

        static float _budgetMultiplier;
        static int _loading;
        static bool _saving;
        static bool _applying;

        public static void Clear()
        {
            _vanillaInitial.Clear();
            _appliedPopulation.Clear();
            _loadedLeft.Clear();
            _vanillaBudget.Clear();
            _trackedZones.Clear();
            _caps.Clear();
            _healthScaled.Clear();
            _pawnStats.Clear();
            _pawnByEnemy.Clear();
            _unknownZones.Clear();
            _budgetMultiplier = 0;
            _loading = 0;
            _saving = false;
            _applying = false;
        }

        public static void AfterSpawnSystemAwake(SpawnSystem __instance)
        {
            ScaleBudget(__instance);
        }

        public static void AfterSpawnSystemReady(SpawnSystem __instance)
        {
            ScaleBudget(__instance);
        }

        public static void BeforeZoneServerStart(SpawnZone __instance)
        {
            ScaleQuota(__instance);
        }

        public static void AfterZoneServerStart(SpawnZone __instance)
        {
            if (__instance == null)
                return;

            int id = __instance.GetInstanceID();
            if (!_loadedLeft.Remove(id, out int left) || __instance.EnemiesLeft == left)
                return;

            _applying = true;
            try
            {
                __instance.EnemiesLeft = left;
            }
            finally
            {
                _applying = false;
            }
        }

        public static void BeforeSaveZones()
        {
            _saving = true;
        }

        public static void AfterSaveZones()
        {
            _saving = false;
        }

        public static void BeforeLoadZones()
        {
            _loading++;
        }

        public static void AfterLoadZones()
        {
            if (_loading > 0)
                _loading--;

            foreach (KeyValuePair<int, SpawnZone> pair in _trackedZones)
            {
                if (pair.Value != null)
                    ApplyCap(pair.Value);
            }
        }

        // Saves store the vanilla kill count. While a save is reading the property, report that count.
        public static void AfterReadEnemiesLeft(SpawnZone __instance, ref int __result)
        {
            if (!_saving || __instance == null)
                return;

            int id = __instance.GetInstanceID();
            if (!_vanillaInitial.ContainsKey(id))
                return;

            float population = AppliedPopulation(__instance);
            if (population <= 0f)
                return;

            __result = Mathf.CeilToInt(__result / population);
        }

        // Loaded kills are the vanilla remaining count. Stretch them to the horde quota.
        public static void BeforeWriteEnemiesLeft(SpawnZone __instance, [HarmonyArgument(0)] ref int value)
        {
            if (_applying || _loading <= 0)
                return;

            value = ScaleCount(value, AppliedPopulation(__instance));
            if (__instance != null)
                _loadedLeft[__instance.GetInstanceID()] = value;
        }

        public static void AfterEnemyServerStart(EnemyControlAStar __instance)
        {
            try
            {
                Weaken(__instance);
            }
            catch (Exception ex)
            {
                HordePlugin.LogSource.LogWarning("Horde could not weaken a zone enemy: " + ex.Message);
            }
        }

        public static void AfterEnemyClientStart(EnemyControlAStar __instance)
        {
            try
            {
                MatchClientHealth(__instance);
            }
            catch (Exception ex)
            {
                HordePlugin.LogSource.LogWarning("Horde could not match client health: " + ex.Message);
            }
        }

        public static void AfterEnemyServerStop(EnemyControlAStar __instance)
        {
            if (__instance == null)
                return;

            int enemyId = __instance.GetInstanceID();
            _healthScaled.Remove(enemyId);
            if (_pawnByEnemy.TryGetValue(enemyId, out int pawnId))
            {
                _pawnStats.Remove(pawnId);
                _pawnByEnemy.Remove(enemyId);
            }
        }

        public static void AfterReadDamage(PawnStatus __instance, ref float __result)
        {
            if (__instance == null)
                return;

            if (_pawnStats.TryGetValue(__instance.GetInstanceID(), out float multiplier))
                __result *= multiplier;
        }

        static void ScaleBudget(SpawnSystem system)
        {
            if (system == null)
                return;

            int id = system.GetInstanceID();
            if (!_vanillaBudget.TryGetValue(id, out int vanilla))
            {
                int cap = system.EnemiesCap;
                if (cap <= 0)
                    return;

                vanilla = cap;
                _vanillaBudget[id] = vanilla;
            }

            float budget = HordeConfig.BudgetPopulation();
            if (budget <= 0f)
                budget = 1f;

            _budgetMultiplier = budget;
            int scaled = ScaleCount(vanilla, budget);
            if (system.EnemiesCap != scaled)
            {
                system.EnemiesCap = scaled;
                HordePlugin.LogSource.LogInfo($"Horde enemy cap {vanilla} -> {scaled}.");
            }

            foreach (KeyValuePair<int, SpawnZone> pair in _trackedZones)
            {
                if (pair.Value != null)
                    ApplyCap(pair.Value);
            }
        }

        static void ScaleQuota(SpawnZone zone)
        {
            if (zone == null)
                return;

            int id = zone.GetInstanceID();
            _trackedZones[id] = zone;
            NoteZone(zone);

            if (!_vanillaInitial.TryGetValue(id, out int vanilla))
            {
                vanilla = zone.InitialEnemies;
                _vanillaInitial[id] = vanilla;
            }

            float population = HordeConfig.Population(zone);
            if (population <= 0f)
                population = 1f;

            _appliedPopulation[id] = population;
            int scaled = ScaleCount(vanilla, population);
            if (zone.InitialEnemies != scaled)
                zone.InitialEnemies = scaled;

            ApplyCap(zone);
        }

        static float AppliedPopulation(SpawnZone zone)
        {
            if (zone != null && _appliedPopulation.TryGetValue(zone.GetInstanceID(), out float applied) && applied > 0f)
                return applied;

            float population = HordeConfig.Population(zone);
            return population <= 0f ? 1f : population;
        }

        static int ScaleCount(int count, float multiplier)
        {
            if (count <= 0 || multiplier <= 0f)
                return 0;

            return Mathf.Max(0, Mathf.RoundToInt(count * multiplier));
        }

        // The shared cap is one number. A zone's own share is its multiplier over that shared budget.
        static void ApplyCap(SpawnZone zone)
        {
            float population = HordeConfig.Population(zone);
            if (population <= 0f)
                population = 1f;

            float budget = _budgetMultiplier > 0f ? _budgetMultiplier : HordeConfig.BudgetPopulation();
            if (budget <= 0f)
                budget = 1f;

            int id = zone.GetInstanceID();
            if (!_caps.TryGetValue(id, out CapState state))
            {
                state = new CapState();
                _caps[id] = state;
            }

            float current = zone.CapMultiplier;
            if (!state.Captured)
            {
                state.Vanilla = current;
                state.Captured = true;
            }
            else if (Mathf.Abs(current - state.Applied) > 0.0001f)
            {
                state.Vanilla = current;
            }

            float next = state.Vanilla * population / budget;
            state.Applied = next;
            if (Mathf.Abs(current - next) > 0.0001f)
                zone.CapMultiplier = next;
        }

        static void NoteZone(SpawnZone zone)
        {
            if (HordeConfig.TryZone(zone, out _))
                return;

            string name = zone.ZoneName;
            if (string.IsNullOrEmpty(name) || !_unknownZones.Add(name))
                return;

            HordePlugin.LogSource.LogInfo("Horde has no config entry for zone '" + name + "'. Using global multipliers.");
        }

        static void Weaken(EnemyControlAStar enemy)
        {
            if (enemy == null || enemy.SpawnZone == null)
                return;

            PawnStatus status = enemy.PawnStatus;
            if (status == null)
                return;

            int enemyId = enemy.GetInstanceID();
            if (!_healthScaled.Add(enemyId))
                return;

            float stat = HordeConfig.Stat(enemy.SpawnZone);
            if (Mathf.Abs(stat - 1f) < 0.0001f)
            {
                _healthScaled.Remove(enemyId);
                return;
            }

            status.InitialHealth *= stat;

            SyncVar<float> health = status.Health;
            if (health != null)
                health.Value *= stat;

            ScaleParts(enemy.damageableParts, stat);

            int pawnId = status.GetInstanceID();
            _pawnStats[pawnId] = stat;
            _pawnByEnemy[enemyId] = pawnId;
        }

        // The zone reference is not networked. A client sees the replicated health already
        // halved and has to lower the local max so the bar still starts full.
        static void MatchClientHealth(EnemyControlAStar enemy)
        {
            if (enemy == null)
                return;

            int enemyId = enemy.GetInstanceID();
            if (_healthScaled.Contains(enemyId))
                return;

            PawnStatus status = enemy.PawnStatus;
            if (status == null)
                return;

            float stat;
            if (enemy.SpawnZone != null)
            {
                stat = HordeConfig.Stat(enemy.SpawnZone);
            }
            else
            {
                SyncVar<float> health = status.Health;
                float initial = status.InitialHealth;
                if (health == null || initial <= 1f || !TryMatchStat(health.Value, initial, out stat))
                    return;
            }

            if (Mathf.Abs(stat - 1f) < 0.0001f)
                return;

            _healthScaled.Add(enemyId);
            status.InitialHealth *= stat;
        }

        static bool TryMatchStat(float health, float initial, out float multiplier)
        {
            multiplier = 1f;
            HordeConfig.CollectStats(_statCandidates);
            float best = 0.05f;
            bool found = false;
            for (int i = 0; i < _statCandidates.Count; i++)
            {
                float candidate = _statCandidates[i];
                float delta = Mathf.Abs(health - initial * candidate);
                if (delta > best)
                    continue;

                best = delta;
                multiplier = candidate;
                found = true;
            }

            return found;
        }

        static void ScaleParts(Il2CppReferenceArray<DamageablePart> parts, float stat)
        {
            if (parts == null)
                return;

            for (int i = 0; i < parts.Length; i++)
            {
                DamageablePart part = parts[i];
                if (part == null || part.MaxHealth <= 0f)
                    continue;

                part.MaxHealth *= stat;
                SyncVar<float> current = part.CurrentHealth;
                if (current != null)
                    current.Value *= stat;
            }
        }
    }
}
