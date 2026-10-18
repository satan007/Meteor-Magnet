using SpaceCraft;
using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>What a building's modules add up to, shown next to its module slots.</summary>
    internal class BuildingSummary
    {
        internal string title;
        internal readonly List<string> lines = [];
        /// <summary>Limits the player broke through: shown highlighted.</summary>
        internal readonly List<string> cheers = [];
    }

    /// <summary>
    /// Player-facing summary of every scanner and magnet: the totals of all its modules, in the game's language,
    /// for the panel next to the module slots. Breaking a logical limit (100% core chance, no failures...)
    /// is congratulated right there in the panel.
    /// </summary>
    internal partial class MagnetController
    {
        internal static readonly Dictionary<int, BuildingSummary> Summaries = [];
        static readonly HashSet<string> cheered = [];

        static bool Ru => Localization.GetLocalizedString("MeteorMagnet_DisplayTitle") != "Meteor network";

        static string T(string ru, string en) => Ru ? ru : en;

        static string Pct(float v) => (v * 100f).ToString("0.##") + "%";

        void UpdateSummaries()
        {
            Summaries.Clear();
            foreach (var s in scanners)
            {
                Summaries[s.wo.GetId()] = Summarize(s);
            }
            foreach (var m in magnets)
            {
                Summaries[m.wo.GetId()] = Summarize(m);
            }
            foreach (var b in beacons)
            {
                var r = new BuildingSummary { title = Readable.GetGroupName(b.GetGroup()) };
                r.lines.Add(T("Дождь каждые ", "A shower every ") + (BeaconInterval / 60f).ToString("0.#") + T(" мин", " min"));
                if (!float.IsNaN(nextShower))
                {
                    r.lines.Add(T("Следующий через ", "Next in ") + (Mathf.Max(0f, nextShower - Time.time) / 60f).ToString("0.#") + T(" мин", " min"));
                }
                r.lines.Add(beaconEffects.Contains(ResonatorEffect.BeaconCommon)
                    ? T("Только дожди обычных ресурсов", "Only showers of common resources")
                    : T("Любые дожди с ресурсами этой планеты", "Any resource shower of this planet"));
                if (beacons.Count > 1)
                {
                    r.lines.Add(T("Маяков: ", "Beacons: ") + beacons.Count + T(" (не складываются, модули общие)", " (do not stack, modules are shared)"));
                }
                if (beaconModules.TryGetValue(b.GetId(), out var inv) && inv != null)
                {
                    r.lines.Add(T("Ячейки модулей: ", "Module slots: ") + inv.GetInsideWorldObjects().Count + "/" + inv.GetSize());
                }
                r.lines.Add(T("Энергия: ", "Power: ") + Plugin.EnergyUse(Content.Beacon.id).ToString("0.#") + T(" кВт", " kW")
                    + (powerOk ? "" : T(" — НЕ ХВАТАЕТ", " - NOT ENOUGH")));
                Summaries[b.GetId()] = r;
            }
        }

        static BuildingSummary Summarize(Scanner s)
        {
            var r = new BuildingSummary { title = Readable.GetGroupName(s.wo.GetGroup()) };
            r.lines.Add(T("Радиус сканирования: ", "Scan radius: ") + (s.planetWide
                ? T("вся планета", "whole planet") + (s.Weakened ? T(" (за своим радиусом — каждый второй метеорит)", " (beyond its own radius: every other meteor)") : "")
                : s.BaseRadius.ToString("0") + T(" м", " m")));
            r.lines.Add(T("Целей одновременно: ", "Targets at once: ") + s.TrackSlots);
            r.lines.Add(T("Захват цели: ", "Lock-on: ") + s.LockTime.ToString("0.##") + T(" с", " s")
                + T(" (редкие ×", " (rare x") + Content.RareLockFactor + ")");
            r.lines.Add(T("Редкие метеориты: ", "Rare meteors: ") + (s.tier.seesRare ? T("видит", "seen") : T("не видит", "not seen"))
                + T(", пустые: ", ", barren: ") + (s.effects.Contains(ResonatorEffect.CaptureBarren)
                    ? (s.Weakened ? T("каждый второй", "every other") : T("видит", "seen")) : T("не видит", "not seen")));
            r.lines.Add(T("Зона связи: ", "Link radius: ") + (s.tier.linkRadius * s.LinkFactor).ToString("0") + T(" м", " m"));
            if (s.filter.Count != 0)
            {
                r.lines.Add(T("Фильтр: ", "Filter: ") + Names(s.filter));
            }
            if (s.Gear(GearType.Compass) > 0)
            {
                r.lines.Add(T("Компас: цели отмечаются на экране", "Compass: targets are tagged on screen"));
            }
            AddCommon(s, r);
            if (GearBonus(s) > 0f || RoverBonus(s) > 0f)
            {
                r.lines.Add(T("Передаваемым метеоритам: ядро +", "Meteors it hands over: core +") + Pct(GearBonus(s))
                    + T(", срыв −", ", failure -") + Pct(RoverBonus(s)));
            }
            if (s.planetWide && !s.Weakened)
            {
                Cheer(s, r, "planet", T("Вся планета под наблюдением!", "The whole planet is watched!"));
            }
            if (s.LockTime <= 0.5f)
            {
                Cheer(s, r, "lock", T("Мгновенное наведение!", "Instant lock-on!"));
            }
            return r;
        }

        static BuildingSummary Summarize(Magnet m)
        {
            var r = new BuildingSummary { title = Readable.GetGroupName(m.wo.GetGroup()) };
            r.lines.Add(T("Метеоритов одновременно: ", "Meteors at once: ") + m.CaptureSlots);
            r.lines.Add(T("Шанс срыва: ", "Fail chance: ") + Pct(FailChance(m, false)) + T(", у редких ", ", rare ") + Pct(FailChance(m, true)));
            var mult = (m.effects.Contains(ResonatorEffect.LootBonus) ? m.Weaken(Content.LootBonusFactor) : 1f)
                * (1f + PlayerGear.MiningLootPerTier * m.Gear(GearType.MiningChip));
            r.lines.Add(T("Ресурсы: ×", "Loot: x") + mult.ToString("0.##")
                + (m.effects.Contains(ResonatorEffect.DuplicateChance) ? T(", удвоение ", ", doubling ") + Pct(Content.DuplicateChance * m.Strength) : "")
                + (m.effects.Contains(ResonatorEffect.EveryResourceType) ? T(", каждый вид", ", every type") + (m.Weakened ? T(" (½ метеоритов)", " (½ of meteors)") : "") : ""));
            r.lines.Add(T("Шанс ядра метеорита: ", "Meteor core chance: ") + Pct(CoreChance(m)) + T(" (+ бонус сканера)", " (+ scanner bonus)"));
            if (m.effects.Contains(ResonatorEffect.Overflow))
            {
                r.lines.Add(T("Перелив излишков в другие захваты сети", "Overflow to other magnets of the network")
                    + (m.Weakened ? T(" (каждый второй предмет)", " (every other item)") : ""));
            }
            if (m.inventory != null)
            {
                r.lines.Add(T("Хранилище: ", "Storage: ") + m.inventory.GetInsideWorldObjects().Count + "/" + m.inventory.GetSize()
                    + (m.filter.Count != 0 ? T(", фильтр: ", ", filter: ") + Names(m.filter) : ""));
            }
            var extras = new List<string>();
            if (m.Gear(GearType.Jetpack) > 0)
            {
                extras.Add(T("падение ×", "fall x") + (CapturedAcceleration(m) / Content.CapturedAccelerationFactor).ToString("0.##"));
            }
            if (m.Upgrade(RoverUpgradeType.Speed) > 0)
            {
                extras.Add(T("доворот ×", "turn x") + (1f + RoverUpgrades.SpeedTurnRatePerTier * m.Upgrade(RoverUpgradeType.Speed)));
            }
            if (m.Upgrade(RoverUpgradeType.Jetpack) > 0)
            {
                extras.Add(T("лут сразу при захвате", "loot at capture"));
            }
            if (m.Upgrade(RoverUpgradeType.Logistics) > 0)
            {
                extras.Add(T("излишки в сундуки", "overflow to chests"));
            }
            if (m.LightTier > 0)
            {
                extras.Add(T("свет над кучкой T", "pile light T") + m.LightTier);
            }
            if (extras.Count != 0)
            {
                r.lines.Add(string.Join(", ", extras));
            }
            AddCommon(m, r);

            var core = CoreChance(m);
            if (core >= 2f)
            {
                Cheer(m, r, "core200", T("Шанс ядра ", "Core chance ") + Pct(core) + T("! Каждый метеорит — россыпь ядер!", "! Every meteor is a pile of cores!"));
            }
            else if (core >= 1f)
            {
                Cheer(m, r, "core100", T("Шанс ядра 100%! Каждый метеорит приносит ядро!", "100% core chance! Every meteor brings a core!"));
            }
            if (FailChance(m, true) <= 0f)
            {
                Cheer(m, r, "nofail", T("Ни одного срыва, даже у редких!", "No failures, not even rare meteors!"));
            }
            else if (FailChance(m, false) <= 0f)
            {
                Cheer(m, r, "nofail-common", T("Обычные метеориты больше не срываются!", "Common meteors never slip anymore!"));
            }
            if (m.CaptureSlots >= 5)
            {
                Cheer(m, r, "slots", T("Захват держит ", "Holds ") + m.CaptureSlots + T(" метеоритов сразу!", " meteors at once!"));
            }
            return r;
        }

        /// <summary>Lines both kinds share: slots, simulation, emergency expansion, power, what is inactive.</summary>
        static void AddCommon(Building b, BuildingSummary r)
        {
            if (b.moduleInventory != null)
            {
                r.lines.Add(T("Ячейки модулей: ", "Module slots: ") + b.moduleInventory.GetInsideWorldObjects().Count + "/" + b.moduleInventory.GetSize());
            }
            if (b.Weakened)
            {
                r.lines.Add(T("Экстренное расширение: ячеек ×2, модули вдвое слабее", "Emergency expansion: slots x2, modules at half strength"));
            }
            if (b.gearItems > 0)
            {
                r.lines.Add(T("Симуляция экипировки: ", "Equipment simulation: ") + b.gearItems + T(" предм. → ядро +", " items -> core +") + Pct(GearBonus(b)));
            }
            if (b.roverItems > 0)
            {
                r.lines.Add(T("Симуляция ровера: ", "Rover simulation: ") + b.roverItems + T(" улучш. → срыв −", " upgrades -> failure -") + Pct(RoverBonus(b)));
            }
            foreach (var line in b.moduleReport)
            {
                if (line.Contains("INACTIVE"))
                {
                    r.lines.Add(line.Contains("Rover")
                        ? T("Улучшения ровера не работают: нужен модуль симуляции ровера", "Rover upgrades inactive: needs the Rover Simulation module")
                        : T("Экипировка не работает: нужен модуль симуляции экипировки", "Equipment inactive: needs the Equipment Simulation module"));
                }
            }
            r.lines.Add(T("Энергия: ", "Power: ") + (b.Autonomous
                ? T("автономно (кислород ровера), от базы не берёт", "autonomous (rover oxygen), draws nothing from the base")
                : b.Draw.ToString("0.#") + T(" кВт", " kW")));
        }

        static string Names(HashSet<string> ids)
        {
            var names = new List<string>();
            foreach (var id in ids)
            {
                var g = GroupsHandler.GetGroupViaId(id);
                names.Add(g != null ? Readable.GetGroupName(g) : id);
            }
            return string.Join(", ", names);
        }

        /// <summary>Highlights a broken limit in the panel (logged once per building and session).</summary>
        static void Cheer(Building b, BuildingSummary r, string kind, string text)
        {
            r.cheers.Add(text);
            if (cheered.Add(b.wo.GetId() + ":" + kind))
            {
                Plugin.Log("Limit broken by " + b.wo.GetGroup().GetId() + " #" + b.wo.GetId() + ": " + kind);
            }
        }
    }
}
