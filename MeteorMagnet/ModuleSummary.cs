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
            foreach (var net in StorageNetwork.Nets)
            {
                var r = new BuildingSummary { title = Readable.GetGroupName(net.controller.GetGroup()) };
                var core = net.coreId != null ? GroupsHandler.GetGroupViaId(net.coreId) : null;
                r.lines.Add(T("Ядро: ", "Core: ") + (core != null ? Readable.GetGroupName(core) : T("нет — сеть не работает", "none - the network is off")));
                r.lines.Add(T("Каналы: ", "Channels: ") + net.Used + "/" + Content.ChannelsText(net.channels)
                    + (net.unserved.Count > 0 ? T(", без канала: ", ", without a channel: ") + net.unserved.Count : ""));
                r.lines.Add(T("Радиус связи: ", "Link radius: ") + (Content.LinkRadius * net.radiusFactor).ToString("0")
                    + T(" м (устройства ", " m (devices ") + (Content.DeviceLinkRadius * net.radiusFactor).ToString("0.#") + T(" м)", " m)"));
                r.lines.Add(T("Дисководов: ", "Drives: ") + net.drives.Count + T(", терминалов: ", ", terminals: ") + net.terminals.Count
                    + T(", дисков: ", ", disks: ") + net.disks.Count);
                r.lines.Add(T("Предметов: ", "Items: ") + net.Stored + "/" + net.Capacity);
                r.lines.Add(T("Энергия: ", "Power: ") + net.Draw().ToString("0.#") + T(" кВт", " kW") + (powerOk ? "" : T(" — НЕ ХВАТАЕТ", " - NOT ENOUGH")));
                Summaries[net.controller.GetId()] = r;
                foreach (var drive in net.drives)
                {
                    var dr = new BuildingSummary { title = Readable.GetGroupName(drive.GetGroup()) };
                    foreach (var d in net.disks)
                    {
                        if (ModuleSlots.Get(drive, Content.StorageDrive.slots)?.GetInsideWorldObjects().Contains(d.disk) == true)
                        {
                            dr.lines.Add(Readable.GetGroupName(d.disk.GetGroup()) + ": " + d.Count + "/" + d.def.capacity
                                + T(", видов ", ", kinds ") + d.Kinds().Count + "/" + d.def.types);
                        }
                    }
                    if (dr.lines.Count == 0)
                    {
                        dr.lines.Add(T("Дисков нет", "No disks"));
                    }
                    Summaries[drive.GetId()] = dr;
                }
                foreach (var drive in net.unserved)
                {
                    Summaries[drive.GetId()] = new BuildingSummary { title = Readable.GetGroupName(drive.GetGroup()) };
                    Summaries[drive.GetId()].lines.Add(T("Нет свободного канала у контроллера — нужно ядро выше тиром", "No free controller channel - needs a higher tier core"));
                }
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
            r.lines.Add(T("Редкие метеориты: ", "Rare meteors: ") + (s.tier.seesRare ? T("видит", "seen") : T("не видит", "not seen")));
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
                * (m.effects.Contains(ResonatorEffect.Destroyer) ? m.Weaken(Content.DestroyerLootFactor) : 1f)
                * (1f + PlayerGear.MiningLootPerTier * m.Gear(GearType.MiningChip));
            r.lines.Add(T("Ресурсы: ×", "Loot: x") + mult.ToString("0.##")
                + (m.effects.Contains(ResonatorEffect.DuplicateChance) ? T(", удвоение ", ", doubling ") + Pct(Content.DuplicateChance * m.Strength) : "")
                + (m.effects.Contains(ResonatorEffect.EveryResourceType) ? T(", каждый вид", ", every type") + (m.Weakened ? T(" (½ метеоритов)", " (½ of meteors)") : "") : ""));
            if (m.effects.Contains(ResonatorEffect.AchievementHunter))
            {
                r.lines.Add(T("Охотник за ачивками: метеориты летят в тебя, ресурсы ×2", "Achievement hunter: meteors come at you, loot x2")
                    + (m.Weakened ? T(" (каждый второй)", " (every other one)") : ""));
                r.lines.Add(T("На волосок от метеорита: ", "Close calls: ") + MeteorStats.CloseCalls
                    + T(" (ключи на 1 / 10 / 25, пасхалка на 50)", " (keys at 1 / 10 / 25, the egg at 50)"));
                var calls = MeteorStats.CloseCalls;
                if (calls >= Content.HunterKeys[3])
                {
                    Cheer(m, r, "egg", T("Пасхалка Холлидея найдена!", "Halliday's egg found!"));
                }
                else if (calls >= Content.HunterKeys[2])
                {
                    Cheer(m, r, "crystal", T("Хрустальный ключ у тебя", "You hold the crystal key"));
                }
                else if (calls >= Content.HunterKeys[1])
                {
                    Cheer(m, r, "jade", T("Нефритовый ключ у тебя", "You hold the jade key"));
                }
                else if (calls >= Content.HunterKeys[0])
                {
                    Cheer(m, r, "copper", T("Медный ключ у тебя", "You hold the copper key"));
                }
            }
            var dominant = m.effects.Contains(ResonatorEffect.Dominant);
            var recessive = m.effects.Contains(ResonatorEffect.Recessive);
            if (dominant != recessive)
            {
                r.lines.Add((dominant ? T("Доминантный: получает метеориты первым", "Dominant: gets meteors first")
                    : T("Рецессивный: получает метеориты последним", "Recessive: gets meteors last"))
                    + (m.Weakened ? T(" (на половине метеоритов)", " (on half of the meteors)") : ""));
            }
            else if (dominant)
            {
                r.lines.Add(T("Доминантный и рецессивный гасят друг друга", "Dominant and recessive cancel out"));
            }
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

            if (m.effects.Contains(ResonatorEffect.StorageLink))
            {
                var linked = StorageNetwork.NetOf(m.wo);
                r.lines.Add(T("Сеть хранения: ", "Storage network: ") + (linked == null
                    ? T("не подключён (нет сети рядом или свободного канала) — лут копится в хранилище", "not linked (no network in reach or no free channel) - loot waits in storage")
                    : powerOk ? T("подключён, лут идёт на диски", "linked, loot goes to the disks")
                    : T("подключён, но нет энергии — лут копится в хранилище", "linked, but no power - loot waits in storage")));
            }
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
            if (b.networkModules.Count > 0)
            {
                r.lines.Add(T("Из сети: ", "From the network: ") + string.Join(", ", b.networkModules));
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
