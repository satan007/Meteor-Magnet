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
        /// <summary>Magnet: brighter glow, and a light over the loot pile so it is easy to find.</summary>
        Lights,
        /// <summary>Scanner: wider link area (scanner and its relays).</summary>
        Beacon,
        /// <summary>No effect yet.</summary>
        Oxygen,
        /// <summary>Loot arrives right at capture instead of after the impact.</summary>
        Jetpack,
        /// <summary>Scanner: tracks more meteors at once.</summary>
        Equipment,
        /// <summary>Filtered-out and overflow loot goes to nearby containers.</summary>
        Logistics,
    }

    /// <summary>
    /// Rover (vehicle) upgrades can be put into a Mk2/Mk3 magnet's storage as well.
    /// The game's item ids are not known in advance, so they are matched by their English
    /// display name ("Vehicle - T2 Inventory increase"), by keywords in the id, or listed
    /// explicitly in the config.
    /// </summary>
    internal static class RoverUpgrades
    {
        internal const float SpeedTurnRatePerTier = 1f;

        internal const float BeaconLinkFactor = 1.5f;

        // Same numbers as on the rover (wiki): Inventory T1..T3 = +18/+34/+58.
        static readonly int[] storageSlots = [0, 18, 34, 58];
        // Equipment T1..T2: extra scanner targets.
        static readonly int[] equipmentTargets = [0, 1, 2];

        internal static int StorageSlots(int tier) => storageSlots[Math.Min(tier, storageSlots.Length - 1)];
        internal static int EquipmentTargets(int tier) => equipmentTargets[Math.Min(tier, equipmentTargets.Length - 1)];

        static readonly Dictionary<string, (RoverUpgradeType type, int tier)> byKeyword = [];
        static readonly Dictionary<string, (RoverUpgradeType type, int tier)> byName = [];
        static readonly Dictionary<string, (RoverUpgradeType type, int tier)> byConfig = [];

        static readonly (string keyword, RoverUpgradeType type)[] keywords =
        [
            ("jetpack", RoverUpgradeType.Jetpack),
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
            ("equipment", RoverUpgradeType.Equipment),
            ("logistic", RoverUpgradeType.Logistics),
        ];

        static readonly string[] vehicleMarkers = ["vehicle", "rover", "truck"];

        internal static bool TryGet(string id, out RoverUpgradeType type, out int tier)
        {
            if (id != null
                && (byConfig.TryGetValue(id, out var v) || byName.TryGetValue(id, out v) || byKeyword.TryGetValue(id, out v)))
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
        /// Keyword match on item ids plus the explicit config list ("ItemId=Speed:2,OtherId=Storage:1").
        /// </summary>
        internal static void Detect(List<GroupData> groupsData, string explicitList)
        {
            byKeyword.Clear();
            byConfig.Clear();

            foreach (var gd in groupsData)
            {
                if (gd is not GroupDataItem || gd.id == null)
                {
                    continue;
                }
                var lower = gd.id.ToLowerInvariant();
                // Blueprint chips that unlock an upgrade are not the upgrade itself.
                if (lower.Contains("blueprint")
                    || !Array.Exists(vehicleMarkers, m => lower.Contains(m)) || !TryKeyword(lower, out var type))
                {
                    continue;
                }
                byKeyword[gd.id] = (type, TierFrom(gd.id));
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
                byConfig[id] = (type, Math.Max(1, tier));
            }
            LogFound();
        }

        /// <summary>
        /// Matches English item names like "Vehicle - T3 Speed increase" or "Vehicle - Beacon".
        /// </summary>
        internal static void DetectByName(Dictionary<string, string> english)
        {
            byName.Clear();
            const string prefix = "GROUP_NAME_";
            foreach (var kv in english)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal) || kv.Value == null
                    || kv.Key.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                var name = kv.Value.Trim();
                var lowerName = name.ToLowerInvariant();
                // "Jetpack For Vehicle" does not follow the "Vehicle - ..." pattern.
                if (lowerName.Contains("jetpack") && lowerName.Contains("vehicle"))
                {
                    byName[kv.Key.Substring(prefix.Length)] = (RoverUpgradeType.Jetpack, 1);
                    continue;
                }
                var m = Regex.Match(name, @"^Vehicle\s*-\s*(?:T(\d+)\s+)?(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success || !TryKeyword(m.Groups[2].Value.ToLowerInvariant(), out var type))
                {
                    continue;
                }
                var tier = m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var t) ? t : 1;
                byName[kv.Key.Substring(prefix.Length)] = (type, tier);
            }
            LogFound();
        }

        static bool TryKeyword(string lower, out RoverUpgradeType type)
        {
            foreach (var (keyword, t) in keywords)
            {
                if (lower.Contains(keyword))
                {
                    type = t;
                    return true;
                }
            }
            type = default;
            return false;
        }

        // What was logged last, so the list is printed once, not on every localization reload.
        static string lastLogged;

        static void LogFound()
        {
            var all = new Dictionary<string, (RoverUpgradeType type, int tier)>(byKeyword);
            foreach (var kv in byName)
            {
                all[kv.Key] = kv.Value;
            }
            foreach (var kv in byConfig)
            {
                all[kv.Key] = kv.Value;
            }
            var lines = new List<string>();
            foreach (var kv in all)
            {
                lines.Add(kv.Key + " -> " + kv.Value.type + " T" + kv.Value.tier);
            }
            lines.Sort(StringComparer.Ordinal);
            var text = string.Join(", ", lines);
            if (text == lastLogged)
            {
                return;
            }
            lastLogged = text;
            if (all.Count == 0)
            {
                Plugin.LogWarning("No rover upgrades detected yet. If none show up later, set [RoverUpgrades] Items in the config.");
                return;
            }
            Plugin.LogInfo("Rover upgrades usable in magnets: " + text);
        }

        static int TierFrom(string id)
        {
            var m = Regex.Match(id, @"(\d+)\D*$");
            return m.Success && int.TryParse(m.Groups[1].Value, out var t) && t > 0 ? t : 1;
        }
    }
}
