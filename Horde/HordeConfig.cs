using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Skopje.Horde
{
    // One row here becomes a setting on Global and on every zone.
    // Zone values are used only when that zone's Override is true.
    static class HordeConfig
    {
        internal const string PopulationKey = "PopulationMultiplier";
        internal const string StatKey = "StatMultiplier";

        sealed class FieldDef
        {
            public string Key;
            public bool IsFloat;
            public int IntDefault = 0;
            public float FloatDefault;
            public int IntMin = 0;
            public int IntMax = 0;
            public float FloatMin;
            public float FloatMax;
        }

        sealed class District
        {
            public string Name;
            public string[] Zones;

            public District(string name, params string[] zones)
            {
                Name = name;
                Zones = zones;
            }
        }

        internal sealed class ZoneSettings
        {
            public string District;
            public string Name;
            public ConfigEntry<bool> Override;
            public Dictionary<string, ConfigEntry<int>> Ints = new Dictionary<string, ConfigEntry<int>>();
            public Dictionary<string, ConfigEntry<float>> Floats = new Dictionary<string, ConfigEntry<float>>();
        }

        const string Explanation =
            "Global multipliers apply to every zone whose Override is false.\n" +
            "PopulationMultiplier multiplies that zone's enemy count, remaining kills, and alive cap. Decimal, range 0.01 to 64, default 4.\n" +
            "StatMultiplier multiplies health and damage of enemies that zone spawns. Range 0.01 to 10, default 0.5.\n" +
            "Set a zone's Override to true to use that zone's own PopulationMultiplier and StatMultiplier instead of Global.";

        static readonly FieldDef[] Fields =
        {
            new FieldDef
            {
                Key = PopulationKey,
                IsFloat = true,
                FloatDefault = 4f,
                FloatMin = 0.01f,
                FloatMax = 64f,
            },
            new FieldDef
            {
                Key = StatKey,
                IsFloat = true,
                FloatDefault = 0.5f,
                FloatMin = 0.01f,
                FloatMax = 10f,
            },
        };

        // Neighborhoods from the game's zone table, grouped into the district that owns them.
        static readonly District[] Districts =
        {
            new District("Metalsky",
                "Kale",
                "Chair - West",
                "Chair - East",
                "Chair",
                "Zhelezara",
                "Avtokomanda",
                "Triangla"),
            new District("Vesna",
                "Vesna",
                "Karposh",
                "Kozle",
                "Debar Maalo",
                "Taftalidze",
                "Treska",
                "Stadium"),
            new District("Teofan",
                "Teofan",
                "Ostrovo",
                "Michurin",
                "Kisela Voda",
                "Kisela Voda - East",
                "Crniche",
                "Aerodrom",
                "Sandanski"),
            new District("Eva",
                "Eva"),
            new District("Ana",
                "Ana",
                "UKIM",
                "Kapishtec",
                "Gradski Dzid"),
        };

        static readonly Dictionary<string, ConfigEntry<int>> GlobalInts = new Dictionary<string, ConfigEntry<int>>();
        static readonly Dictionary<string, ConfigEntry<float>> GlobalFloats = new Dictionary<string, ConfigEntry<float>>();
        static readonly Dictionary<string, ZoneSettings> Zones = new Dictionary<string, ZoneSettings>(StringComparer.OrdinalIgnoreCase);

        public static float GlobalPopulation
        {
            get { return GlobalFloats[PopulationKey].Value; }
        }

        public static float GlobalStat
        {
            get { return GlobalFloats[StatKey].Value; }
        }

        public static void Bind(ConfigFile config)
        {
            GlobalInts.Clear();
            GlobalFloats.Clear();
            Zones.Clear();

            for (int i = 0; i < Fields.Length; i++)
                BindField(config, "Global", Fields[i], null, true);

            for (int d = 0; d < Districts.Length; d++)
            {
                District district = Districts[d];
                for (int z = 0; z < district.Zones.Length; z++)
                    BindZone(config, district.Name, district.Zones[z]);
            }
        }

        public static float Population(SpawnZone zone)
        {
            return FloatSetting(zone, PopulationKey);
        }

        public static float Stat(SpawnZone zone)
        {
            return FloatSetting(zone, StatKey);
        }

        // Shared alive budget has to cover the largest multiplier actually in use.
        public static float BudgetPopulation()
        {
            float budget = GlobalPopulation;
            foreach (KeyValuePair<string, ZoneSettings> pair in Zones)
            {
                ZoneSettings zone = pair.Value;
                if (!zone.Override.Value)
                    continue;

                float population = zone.Floats[PopulationKey].Value;
                if (population > budget)
                    budget = population;
            }

            return budget < 0.01f ? 0.01f : budget;
        }

        public static void CollectStats(List<float> into)
        {
            into.Clear();
            AddStat(into, GlobalStat);
            foreach (KeyValuePair<string, ZoneSettings> pair in Zones)
            {
                ZoneSettings zone = pair.Value;
                if (zone.Override.Value)
                    AddStat(into, zone.Floats[StatKey].Value);
            }
        }

        public static int IntSetting(SpawnZone zone, string key)
        {
            ZoneSettings settings;
            if (TryZone(zone, out settings) && settings.Override.Value)
                return settings.Ints[key].Value;
            return GlobalInts[key].Value;
        }

        public static float FloatSetting(SpawnZone zone, string key)
        {
            ZoneSettings settings;
            if (TryZone(zone, out settings) && settings.Override.Value)
                return settings.Floats[key].Value;
            return GlobalFloats[key].Value;
        }

        public static bool TryZone(SpawnZone zone, out ZoneSettings settings)
        {
            settings = null;
            if (zone == null)
                return false;

            string name = zone.ZoneName;
            if (string.IsNullOrEmpty(name))
                return false;

            return Zones.TryGetValue(name.Trim(), out settings);
        }

        static void BindZone(ConfigFile config, string district, string zone)
        {
            string section = district + "." + zone;
            var settings = new ZoneSettings
            {
                District = district,
                Name = zone,
                Override = config.Bind(section, "Override", false, (string)null),
            };

            for (int i = 0; i < Fields.Length; i++)
                BindField(config, section, Fields[i], settings, false);

            Zones[zone] = settings;
        }

        static void BindField(ConfigFile config, string section, FieldDef field, ZoneSettings zone, bool global)
        {
            // The explanation is written once, above the first global setting.
            string description = global && field.Key == PopulationKey ? Explanation : null;
            ConfigDescription configDescription = field.IsFloat
                ? new ConfigDescription(description, new AcceptableValueRange<float>(field.FloatMin, field.FloatMax))
                : new ConfigDescription(description, new AcceptableValueRange<int>(field.IntMin, field.IntMax));

            if (!field.IsFloat)
            {
                ConfigEntry<int> entry = config.Bind(section, field.Key, field.IntDefault, configDescription);
                if (zone == null)
                    GlobalInts[field.Key] = entry;
                else
                    zone.Ints[field.Key] = entry;
                return;
            }

            ConfigEntry<float> floating = config.Bind(section, field.Key, field.FloatDefault, configDescription);
            if (zone == null)
                GlobalFloats[field.Key] = floating;
            else
                zone.Floats[field.Key] = floating;
        }

        static void AddStat(List<float> into, float value)
        {
            for (int i = 0; i < into.Count; i++)
            {
                if (Math.Abs(into[i] - value) < 0.0001f)
                    return;
            }

            into.Add(value);
        }
    }
}
