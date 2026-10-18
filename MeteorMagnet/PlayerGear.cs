using SpaceCraft;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MeteorMagnet
{
    internal enum GearType
    {
        /// <summary>Magnet: more storage slots.</summary>
        Backpack,
        /// <summary>Scanner and magnet: more module slots.</summary>
        Exoskeleton,
        /// <summary>Magnet: pulled meteors keep more of their acceleration and come down sooner.</summary>
        Jetpack,
        /// <summary>Scanner: locks on faster.</summary>
        Boots,
        /// <summary>Scanner: wider scan radius.</summary>
        OxygenTank,
        /// <summary>Magnet: more loot.</summary>
        MiningChip,
        /// <summary>Magnet: brighter glow and a light over the loot pile.</summary>
        TorchChip,
        /// <summary>Magnet: higher chance of a meteor core.</summary>
        DeconstructChip,
        /// <summary>Scanner: its tracked meteors are tagged on screen while they fall.</summary>
        Compass,
    }

    /// <summary>
    /// The player's own equipment (backpacks, exoskeletons, jetpacks, boots, oxygen tanks, multitool chips,
    /// the compass) works as a module too. Item ids are matched by the game's naming ("Backpack3",
    /// "MultiToolMineSpeed2"), or listed explicitly in the config.
    /// </summary>
    internal static class PlayerGear
    {
        internal const int BackpackSlotsPerTier = 4;
        internal const float JetpackAccelerationPerTier = 0.15f;
        internal const float BootsLockPerTier = 0.1f;
        internal const float OxygenRadiusPerTier = 0.1f;
        internal const float MiningLootPerTier = 0.1f;
        internal const float DeconstructCorePerTier = 0.01f;

        static readonly (Regex pattern, GearType type)[] patterns =
        [
            (new Regex(@"^Backpack(\d+)$"), GearType.Backpack),
            (new Regex(@"^EquipmentIncrease(\d+)$"), GearType.Exoskeleton),
            (new Regex(@"^Jetpack(\d+)$"), GearType.Jetpack),
            (new Regex(@"^BootsSpeed(\d+)$"), GearType.Boots),
            (new Regex(@"^OxygenTank(\d+)$"), GearType.OxygenTank),
            (new Regex(@"^MultiToolMineSpeed(\d+)$"), GearType.MiningChip),
            (new Regex(@"^MultiToolLight(\d+)$"), GearType.TorchChip),
            (new Regex(@"^MultiToolDeconstruct(\d+)$"), GearType.DeconstructChip),
            (new Regex(@"^HudCompass$"), GearType.Compass),
        ];

        // Ids that look like player equipment, to log the ones no pattern matched.
        static readonly string[] hints = ["backpack", "oxygen", "jetpack", "boots", "equipment", "exoskeleton", "multitool", "hud", "compass", "filter", "map"];

        static readonly Dictionary<string, (GearType type, int tier)> byId = [];
        // Wearable items without an effect of their own (filters, map, skins...): they still count for the simulation bonus.
        static readonly HashSet<string> other = [];

        internal static IEnumerable<string> AllIds()
        {
            foreach (var id in byId.Keys)
            {
                yield return id;
            }
            foreach (var id in other)
            {
                yield return id;
            }
        }

        internal static bool IsOtherEquipment(string id) => id != null && other.Contains(id);

        internal static bool TryGet(string id, out GearType type, out int tier)
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

        /// <summary>Pattern match over the game's items plus the explicit config list ("Id=Type:Tier,...").</summary>
        internal static void Detect(List<GroupData> groupsData, string explicitList)
        {
            byId.Clear();
            other.Clear();
            var unmatched = new List<string>();
            // The game marks wearable items with an equipable type; read by name so a rename only falls back to id hints.
            var fEquipable = HarmonyLib.AccessTools.Field(typeof(GroupDataItem), "equipableType");
            foreach (var gd in groupsData)
            {
                if (gd is not GroupDataItem || gd.id == null || gd.id.StartsWith("Vehicle", StringComparison.Ordinal)
                    || gd.id.IndexOf("blueprint", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }
                var matched = false;
                foreach (var (pattern, type) in patterns)
                {
                    var m = pattern.Match(gd.id);
                    if (m.Success)
                    {
                        var tier = m.Groups.Count > 1 && int.TryParse(m.Groups[1].Value, out var t) && t > 0 ? t : 1;
                        byId[gd.id] = (type, tier);
                        matched = true;
                        break;
                    }
                }
                if (matched)
                {
                    continue;
                }
                var equipable = fEquipable?.GetValue(gd)?.ToString();
                var lower = gd.id.ToLowerInvariant();
                if (fEquipable != null ? equipable != null && equipable != "Null" && equipable != "None"
                    : Array.Exists(hints, h => lower.Contains(h)))
                {
                    other.Add(gd.id);
                    unmatched.Add(gd.id + (equipable != null ? " (" + equipable + ")" : ""));
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
                var spec = entry.Substring(eq + 1).Split(':');
                if (!Enum.TryParse<GearType>(spec[0].Trim(), true, out var type))
                {
                    Plugin.LogWarning("PlayerGear: unknown type in '" + entry + "'");
                    continue;
                }
                var tier = spec.Length > 1 && int.TryParse(spec[1].Trim(), out var t) ? Math.Max(1, t) : 1;
                byId[entry.Substring(0, eq).Trim()] = (type, tier);
            }

            var found = new List<string>();
            foreach (var kv in byId)
            {
                found.Add(kv.Key + " -> " + kv.Value.type + " T" + kv.Value.tier);
            }
            found.Sort(StringComparer.Ordinal);
            unmatched.Sort(StringComparer.Ordinal);
            Plugin.LogInfo("Player equipment usable as modules: " + (found.Count == 0 ? "none" : string.Join(", ", found)));
            Plugin.LogInfo("Equipment without an effect (counts for the simulation bonus only"
                + (fEquipable == null ? ", found by id hints: equipableType field missing" : "") + "): "
                + (unmatched.Count == 0 ? "none" : string.Join(", ", unmatched)));
        }
    }
}
