using SpaceCraft;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MeteorMagnet
{
    internal enum RoverUpgradeType
    {
        /// <summary>Meteors turn towards the magnet faster.</summary>
        Speed,
        /// <summary>More storage slots.</summary>
        Storage,
        /// <summary>Brighter, wider glow of the magnet.</summary>
        Lights,
        /// <summary>A light marks the loot pile so it is easy to find.</summary>
        Beacon,
        /// <summary>Loot arrives right at capture instead of after the impact.</summary>
        Oxygen,
    }

    /// <summary>
    /// Rover (vehicle) upgrades can be put into a Mk2/Mk3 magnet's storage as well.
    /// The game's item ids are not known in advance, so they are detected by name
    /// (or listed explicitly in the config) when the static data loads.
    /// </summary>
    internal static class RoverUpgrades
    {
        internal const int StorageSlotsPerTier = 12;
        internal const float SpeedTurnRatePerTier = 1f;

        static readonly Dictionary<string, (RoverUpgradeType type, int tier)> byId = [];

        static readonly (string keyword, RoverUpgradeType type)[] keywords =
        [
            ("speed", RoverUpgradeType.Speed),
            ("inventory", RoverUpgradeType.Storage),
            ("storage", RoverUpgradeType.Storage),
            ("container", RoverUpgradeType.Storage),
            ("trunk", RoverUpgradeType.Storage),
            ("light", RoverUpgradeType.Lights),
            ("lamp", RoverUpgradeType.Lights),
            ("beacon", RoverUpgradeType.Beacon),
            ("oxygen", RoverUpgradeType.Oxygen),
            ("o2", RoverUpgradeType.Oxygen),
        ];

        static readonly string[] vehicleMarkers = ["vehicle", "rover", "truck"];

        internal static bool TryGet(string id, out RoverUpgradeType type, out int tier)
        {
            if (id != null && byId.TryGetValue(id, out var v))
            {
                type = v.type;
                tier = v.tier;
                return true;
            }
            type = default;
            tier = 0;
            return false;
        }

        /// <summary>
        /// Builds the id table. Explicit entries look like "ItemId=Speed:2,OtherId=Storage:1".
        /// </summary>
        internal static void Detect(List<GroupData> groupsData, string explicitList)
        {
            byId.Clear();

            foreach (var gd in groupsData)
            {
                if (gd is not GroupDataItem || gd.id == null)
                {
                    continue;
                }
                var lower = gd.id.ToLowerInvariant();
                if (!Array.Exists(vehicleMarkers, m => lower.Contains(m)))
                {
                    continue;
                }
                foreach (var (keyword, type) in keywords)
                {
                    if (lower.Contains(keyword))
                    {
                        byId[gd.id] = (type, TierFromId(gd.id));
                        break;
                    }
                }
            }

            foreach (var raw in (explicitList ?? "").Split(','))
            {
                var entry = raw.Trim();
                var eq = entry.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                var id = entry.Substring(0, eq).Trim();
                var spec = entry.Substring(eq + 1).Split(':');
                if (!Enum.TryParse<RoverUpgradeType>(spec[0].Trim(), true, out var type))
                {
                    Plugin.LogWarning("RoverUpgrades: unknown type in '" + entry + "'");
                    continue;
                }
                var tier = spec.Length > 1 && int.TryParse(spec[1].Trim(), out var t) ? t : 1;
                byId[id] = (type, Math.Max(1, tier));
            }

            if (byId.Count == 0)
            {
                Plugin.LogWarning("No rover upgrades detected. Set [RoverUpgrades] Items in the config to enable them.");
            }
            foreach (var kv in byId)
            {
                Plugin.LogInfo("Rover upgrade usable in magnets: " + kv.Key + " -> " + kv.Value.type + " T" + kv.Value.tier);
            }
        }

        static int TierFromId(string id)
        {
            var m = Regex.Match(id, @"(\d+)\D*$");
            return m.Success && int.TryParse(m.Groups[1].Value, out var t) && t > 0 ? t : 1;
        }
    }
}
