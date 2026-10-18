using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// A building added by the mod: magnet, scanner or relay.
    /// </summary>
    internal abstract class BuildingDef
    {
        internal string id;
        internal int level;
        /// <summary>Buildings to clone, first existing one wins.</summary>
        internal string[] baseGroupIds;
        /// <summary>The base building actually used, set at registration.</summary>
        internal string resolvedBaseId;
        internal string recipe;
        internal float unlockTi;
        internal Color color;
        internal string nameRu, nameEn, descRu, descEn;

        /// <summary>Config section for the recipe and unlock threshold.</summary>
        internal abstract string ConfigSection { get; }
    }

    /// <summary>
    /// The gravity magnet: pulls in meteors a scanner hands to it and processes their loot.
    /// It works only inside a scanner's link area (around the scanner or a linked relay).
    /// </summary>
    internal class MagnetTier : BuildingDef
    {
        /// <summary>Mk1 only redirects meteors and drops the loot next to itself; higher tiers store it.</summary>
        internal bool hasStorage;
        /// <summary>How many meteors the magnet can pull in at once.</summary>
        internal int captureSlots;
        /// <summary>Chance that a capture fails.</summary>
        internal float failChance;

        internal override string ConfigSection => "Magnet Mk" + level;
    }

    /// <summary>
    /// The scanner: detects meteors falling within its scan radius, locks on to them and hands them
    /// to magnets inside its (smaller) link radius. Relays extend the link area.
    /// </summary>
    internal class ScannerTier : BuildingDef
    {
        /// <summary>Detection radius in meters.</summary>
        internal float scanRadius;
        /// <summary>
        /// With a Space Link module and a satellite in orbit over the planet, the scanner sees the whole planet.
        /// </summary>
        internal bool usesSatellite;
        /// <summary>Magnets within this radius of the scanner (or of a linked relay) get its targets.</summary>
        internal float linkRadius;
        /// <summary>How many meteors the scanner tracks at once (locking on or waiting for a magnet).</summary>
        internal int trackSlots;
        /// <summary>Seconds a meteor must be tracked before it can be handed to a magnet.</summary>
        internal float lockTime;
        /// <summary>Whether meteors with rare resources are detected at all.</summary>
        internal bool seesRare;

        internal override string ConfigSection => "Scanner Mk" + level;
    }

    /// <summary>
    /// The relay: passes a scanner's targets on. It scans nothing, it only extends the link area.
    /// A relay is linked when it stands inside the link area of a scanner or of another linked relay.
    /// </summary>
    internal class RelayDef : BuildingDef
    {
        internal float linkRadius;

        internal override string ConfigSection => "Relay";
    }

    internal enum ResonatorEffect
    {
        /// <summary>Scanner: also detects meteors that carry no resources; magnets turn them into common ore.</summary>
        CaptureBarren,
        /// <summary>+50% loot.</summary>
        LootBonus,
        /// <summary>Scanner: doubles the scan radius (whole-planet scanner: locks on faster instead).</summary>
        RadiusBoost,
        /// <summary>Every captured item has a chance to be duplicated.</summary>
        DuplicateChance,
        /// <summary>Pulls loose meteor resources lying near the magnet into its storage.</summary>
        Vortex,
        /// <summary>Every meteor gives at least one of each resource type it carries.</summary>
        EveryResourceType,
        /// <summary>Scanner Mk3: with a satellite in orbit over the planet, scans the whole planet.</summary>
        SpaceLink,
    }

    /// <summary>
    /// A module crafted from resources that exist on a single planet. Put it into the storage of the
    /// building it is made for (a scanner or a Mk2/Mk3 magnet); elsewhere it does nothing.
    /// Each kind counts once per building.
    /// </summary>
    internal class Resonator
    {
        internal string id;
        /// <summary>True for scanner modules, false for magnet modules.</summary>
        internal bool forScanner;
        internal string planet;
        internal ResonatorEffect effect;
        /// <summary>Config section for the recipe; planet resonators by default.</summary>
        internal string ConfigSection => planet != null ? "Resonator " + planet : "Module " + effect;
        internal string recipe;
        internal Color color;
        internal string nameRu, nameEn, descRu, descEn;
    }

    /// <summary>The satellite rocket item; cloned from one of the game's rockets.</summary>
    internal class SatelliteRocketDef
    {
        internal string id;
        internal string recipe;
        internal float unlockTi;
        internal string nameRu, nameEn, descRu, descEn;
    }

    internal static class Content
    {
        internal const float LootBonusFactor = 1.5f;
        internal const float RadiusBoostFactor = 2f;
        /// <summary>Selenea lens on a whole-planet scanner: lock time multiplier instead of radius.</summary>
        internal const float LensLockFactor = 0.5f;
        /// <summary>Share of failed captures that throw the meteor away instead of just letting it go.</summary>
        internal const float BadFailShare = 0.35f;
        internal const float DeflectMinDistance = 250f;
        internal const float DeflectMaxDistance = 500f;
        /// <summary>How much faster a thrown-away meteor turns than a captured one.</summary>
        internal const float DeflectTurnFactor = 3f;
        /// <summary>Within this distance a pulled meteor aims straight at its target.</summary>
        internal const float SnapDistance = 40f;
        internal const float DuplicateChance = 0.25f;
        internal const float ForwardRadius = 40f;
        internal const float VortexRadius = 30f;
        internal const float VortexInterval = 2f;

        /// <summary>
        /// Rare meteor resources: such meteors take longer to lock on, fail more often and a Mk1 scanner does not see them.
        /// A meteor is rare if any of its resources is in this list.
        /// </summary>
        internal static readonly HashSet<string> RareResources =
        [
            "Alloy", "Osmium",
            "PulsarQuartz", "PulsarShard", "SolarQuartz", "MagnetarQuartz", "BalzarQuartz", "QuasarQuartz", "CosmicQuartz",
        ];
        internal const float RareLockFactor = 1.5f;
        internal const float RareFailBonus = 0.10f;

        /// <summary>What a barren meteor turns into with the Prime resonator.</summary>
        internal static readonly string[] BarrenLoot = ["Iron", "Titanium", "Magnesium", "Silicon", "Cobalt", "Aluminium"];

        internal static readonly MagnetTier[] Tiers =
        [
            new MagnetTier
            {
                id = "MeteorMagnet1", level = 1,
                baseGroupIds = ["Beacon", "Container1"],
                hasStorage = false,
                captureSlots = 1,
                failChance = 0.20f,
                recipe = "Iron,Iron,Iron,Titanium,Titanium,Titanium,Magnesium,Magnesium,Silicon,Silicon",
                unlockTi = 0f,
                color = new Color(0.35f, 0.55f, 1f),
                nameRu = "Гравитационный захват Mk1",
                nameEn = "Meteor Magnet Mk1",
                descRu = "Притягивает метеориты, которые ему передаёт сканер (ставить в зоне связи сканера или реле). Держит 1 метеорит за раз. Ресурсы ссыпаются кучкой рядом.",
                descEn = "Pulls in meteors handed over by a scanner (place it in the link area of a scanner or relay). Holds 1 meteor at a time. Resources drop in a pile next to it.",
            },
            new MagnetTier
            {
                id = "MeteorMagnet2", level = 2,
                baseGroupIds = ["Container1", "Container2"],
                hasStorage = true,
                captureSlots = 2,
                failChance = 0.12f,
                recipe = "Alloy,Alloy,Alloy,Aluminium,Aluminium,Cobalt,Cobalt,Titanium,Titanium,Iridium",
                unlockTi = 1000000f,
                color = new Color(0.55f, 0.25f, 1f),
                nameRu = "Гравитационный захват Mk2",
                nameEn = "Meteor Magnet Mk2",
                descRu = "Притягивает метеориты, которые ему передаёт сканер, по 2 за раз. Хранилище размером с малый сундук, фильтр по клавише. Модули захвата кладутся внутрь.",
                descEn = "Pulls in meteors handed over by a scanner, 2 at a time. Small chest storage, filter on a key. Magnet modules go inside.",
            },
            new MagnetTier
            {
                id = "MeteorMagnet3", level = 3,
                baseGroupIds = ["Container2", "Container1"],
                hasStorage = true,
                captureSlots = 3,
                failChance = 0.08f,
                recipe = "Alloy,Alloy,Alloy,Alloy,Rod-uranium,Rod-uranium,Iridium,Iridium,Osmium,Osmium,Zeolite",
                unlockTi = 100000000f,
                color = new Color(1f, 0.3f, 0.75f),
                nameRu = "Гравитационный захват Mk3",
                nameEn = "Meteor Magnet Mk3",
                descRu = "Притягивает метеориты, которые ему передаёт сканер, по 3 за раз. Хранилище размером с большой сундук, фильтр по клавише. Модули захвата кладутся внутрь.",
                descEn = "Pulls in meteors handed over by a scanner, 3 at a time. Big chest storage, filter on a key. Magnet modules go inside.",
            },
        ];

        internal static readonly ScannerTier[] Scanners =
        [
            new ScannerTier
            {
                id = "MeteorScanner1", level = 1,
                baseGroupIds = ["Container1"],
                scanRadius = 300f,
                linkRadius = 60f,
                trackSlots = 2,
                lockTime = 2.5f,
                seesRare = false,
                recipe = "Iron,Iron,Iron,Silicon,Silicon,Silicon,Cobalt,Cobalt,Titanium,Titanium",
                unlockTi = 0f,
                color = new Color(0.3f, 1f, 0.9f),
                nameRu = "Метеорный сканер Mk1",
                nameEn = "Meteor Scanner Mk1",
                descRu = "Замечает метеориты в радиусе 300 м, ведёт 2 цели и передаёт их захватам в радиусе связи 60 м. Редкие метеориты не видит. Модули сканера кладутся внутрь.",
                descEn = "Detects meteors within 300 m, tracks 2 targets and hands them to magnets within a 60 m link radius. Does not see rare meteors. Scanner modules go inside.",
            },
            new ScannerTier
            {
                id = "MeteorScanner2", level = 2,
                baseGroupIds = ["Container1"],
                scanRadius = 800f,
                linkRadius = 100f,
                trackSlots = 4,
                lockTime = 2.0f,
                seesRare = true,
                recipe = "Alloy,Alloy,Aluminium,Aluminium,Silicon,Silicon,Cobalt,Cobalt,Iridium,Iridium",
                unlockTi = 1000000f,
                color = new Color(0.3f, 0.85f, 1f),
                nameRu = "Метеорный сканер Mk2",
                nameEn = "Meteor Scanner Mk2",
                descRu = "Замечает метеориты в радиусе 800 м, ведёт 4 цели и передаёт их захватам в радиусе связи 100 м. Модули сканера кладутся внутрь.",
                descEn = "Detects meteors within 800 m, tracks 4 targets and hands them to magnets within a 100 m link radius. Scanner modules go inside.",
            },
            new ScannerTier
            {
                id = "MeteorScanner3", level = 3,
                baseGroupIds = ["Container1"],
                scanRadius = 2000f,
                usesSatellite = true,
                linkRadius = 150f,
                trackSlots = 6,
                lockTime = 1.5f,
                seesRare = true,
                recipe = "Alloy,Alloy,Alloy,Osmium,Osmium,Iridium,Iridium,Rod-uranium,Rod-uranium,PulsarQuartz,MagnetarQuartz,Zeolite",
                unlockTi = 100000000f,
                color = new Color(0.6f, 0.95f, 1f),
                nameRu = "Метеорный сканер Mk3",
                nameEn = "Meteor Scanner Mk3",
                descRu = "Замечает метеориты в радиусе 2000 м, ведёт 6 целей и передаёт их захватам в радиусе связи 150 м. С модулем космической связи и спутником на орбите видит всю планету. Модули сканера кладутся внутрь.",
                descEn = "Detects meteors within 2000 m, tracks 6 targets and hands them to magnets within a 150 m link radius. With a Space Link module and a satellite in orbit it sees the whole planet. Scanner modules go inside.",
            },
        ];

        internal static readonly RelayDef Relay = new()
        {
            id = "MeteorRelay", level = 1,
            baseGroupIds = ["Beacon", "Container1"],
            linkRadius = 60f,
            recipe = "Iron,Iron,Silicon,Silicon,Cobalt,Magnesium",
            unlockTi = 0f,
            color = new Color(1f, 0.9f, 0.4f),
            nameRu = "Реле сканера",
            nameEn = "Scanner Relay",
            descRu = "Продлевает зону связи сканера на 60 м вокруг себя. Ставить в зоне связи сканера или другого реле, можно цепочкой. Само ничего не сканирует.",
            descEn = "Extends a scanner's link area by 60 m around itself. Place it inside the link area of a scanner or another relay; chains work. Scans nothing itself.",
        };

        internal static IEnumerable<BuildingDef> Buildings
        {
            get
            {
                foreach (var t in Tiers)
                {
                    yield return t;
                }
                foreach (var t in Scanners)
                {
                    yield return t;
                }
                yield return Relay;
            }
        }

        internal static readonly Resonator[] Resonators =
        [
            new Resonator
            {
                id = "MeteorResonatorPrime", planet = "Prime", forScanner = true,
                effect = ResonatorEffect.CaptureBarren,
                recipe = "PulsarQuartz,PulsarQuartz,Alloy",
                color = new Color(0.3f, 0.8f, 1f),
                nameRu = "Пульсарный резонатор",
                nameEn = "Pulsar Resonator",
                descRu = "Модуль сканера. Сканер замечает и пустые метеориты, а захват превращает их в обычную руду.",
                descEn = "Scanner module. The scanner also detects barren meteors; magnets turn them into common ore.",
            },
            new Resonator
            {
                id = "MeteorResonatorHumble", planet = "Humble",
                effect = ResonatorEffect.LootBonus,
                recipe = "Dolomite,Dolomite,Bauxite,Bauxite,Uraninite",
                color = new Color(1f, 0.6f, 0.2f),
                nameRu = "Доломитовый концентратор",
                nameEn = "Dolomite Concentrator",
                descRu = "Модуль захвата Mk2/Mk3. +50% ресурсов с каждого метеорита.",
                descEn = "Magnet Mk2/Mk3 module. +50% resources from every meteor.",
            },
            new Resonator
            {
                id = "MeteorResonatorSelenea", planet = "Selenea", forScanner = true,
                effect = ResonatorEffect.RadiusBoost,
                recipe = "Selenium,Selenium,BalzarQuartz",
                color = new Color(0.3f, 1f, 0.6f),
                nameRu = "Селеновая линза",
                nameEn = "Selenium Lens",
                descRu = "Модуль сканера. Удваивает радиус сканирования. На сканере Mk3 (вся планета) вместо этого наводится вдвое быстрее.",
                descEn = "Scanner module. Doubles the scan radius. On a Mk3 scanner (whole planet) it locks on twice as fast instead.",
            },
            new Resonator
            {
                id = "MeteorResonatorToxicity", planet = "Toxicity",
                effect = ResonatorEffect.DuplicateChance,
                recipe = "ToxicGoo,ToxicGoo,ToxicGoo,Alloy",
                color = new Color(0.7f, 1f, 0.1f),
                nameRu = "Токсичный катализатор",
                nameEn = "Toxic Catalyst",
                descRu = "Модуль захвата Mk2/Mk3. Каждый пойманный ресурс с шансом 25% удваивается.",
                descEn = "Magnet Mk2/Mk3 module. Every captured resource has a 25% chance to be doubled.",
            },
            new Resonator
            {
                id = "MeteorResonatorAqualis", planet = "Aqualis",
                effect = ResonatorEffect.Vortex,
                recipe = "Phosphorus,Phosphorus,Phosphorus,Alloy",
                color = new Color(0.2f, 0.5f, 1f),
                nameRu = "Фосфорный водоворот",
                nameEn = "Phosphorus Vortex",
                descRu = "Модуль захвата Mk2/Mk3. Затягивает в хранилище ресурсы метеоритов, лежащие на земле в радиусе 30 м (с учётом фильтра) — например, кучки от Mk1 или старые обломки.",
                descEn = "Magnet Mk2/Mk3 module. Pulls meteor resources lying on the ground within 30 m into storage (respecting the filter), e.g. Mk1 piles or old debris.",
            },
            new Resonator
            {
                id = "MeteorResonatorSkeo", planet = "Skeo",
                effect = ResonatorEffect.EveryResourceType,
                recipe = "Amber,Amber,Amber,Alloy",
                color = new Color(1f, 0.75f, 0f),
                nameRu = "Янтарный сепаратор",
                nameEn = "Amber Separator",
                descRu = "Модуль захвата Mk2/Mk3. С каждого метеорита гарантированно приходит хотя бы по одному ресурсу каждого вида, который он несёт.",
                descEn = "Magnet Mk2/Mk3 module. Every meteor gives at least one of each resource type it carries.",
            },
            new Resonator
            {
                id = "MeteorSpaceLink", planet = null, forScanner = true,
                effect = ResonatorEffect.SpaceLink,
                recipe = "Alloy,Alloy,Osmium,Iridium,Iridium,Silicon,Silicon",
                color = new Color(0.75f, 0.6f, 1f),
                nameRu = "Модуль космической связи",
                nameEn = "Space Link Module",
                descRu = "Модуль сканера Mk3. Если над планетой запущена метеорная спутниковая ракета, сканер видит метеориты по всей планете.",
                descEn = "Scanner Mk3 module. With a meteor satellite rocket launched over the planet, the scanner sees meteors all over the planet.",
            },
        ];

        /// <summary>
        /// The satellite rocket: crafted and launched like other rockets. Once launched over a planet,
        /// Mk3 scanners with a Space Link module there scan the whole planet. One launch per planet is enough.
        /// </summary>
        internal static readonly SatelliteRocketDef SatelliteRocket = new()
        {
            id = "RocketMeteorSatellite",
            recipe = "CosmicQuartz,CosmicQuartz,CosmicQuartz,Alloy,Alloy,Alloy,Rod-uranium,Rod-uranium,Osmium",
            unlockTi = 100000000f,
            nameRu = "Метеорная спутниковая ракета",
            nameEn = "Meteor Satellite Rocket",
            descRu = "Выводит на орбиту спутник слежения за метеоритами. Сканеры Mk3 с модулем космической связи на этой планете видят метеориты по всей планете. Достаточно одного запуска на планету.",
            descEn = "Puts a meteor tracking satellite into orbit. Mk3 scanners with a Space Link module on this planet see meteors all over the planet. One launch per planet is enough.",
        };

        static Dictionary<string, MagnetTier> tierById;
        static Dictionary<string, ScannerTier> scannerById;
        static Dictionary<string, Resonator> resonatorById;

        internal static ScannerTier ScannerOf(string groupId)
        {
            scannerById ??= BuildIndex(Scanners, t => t.id);
            return groupId != null && scannerById.TryGetValue(groupId, out var t) ? t : null;
        }

        internal static bool IsRelay(string groupId) => groupId == Relay.id;

        internal static MagnetTier TierOf(string groupId)
        {
            tierById ??= BuildIndex(Tiers, t => t.id);
            return groupId != null && tierById.TryGetValue(groupId, out var t) ? t : null;
        }

        internal static Resonator ResonatorOf(string groupId)
        {
            resonatorById ??= BuildIndex(Resonators, r => r.id);
            return groupId != null && resonatorById.TryGetValue(groupId, out var r) ? r : null;
        }

        static Dictionary<string, T> BuildIndex<T>(T[] items, System.Func<T, string> key)
        {
            var d = new Dictionary<string, T>();
            foreach (var item in items)
            {
                d[key(item)] = item;
            }
            return d;
        }
    }
}
