using SpaceCraft;
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
        /// <summary>Power draw in the game's energy units (kW); the network works only while the base has enough power.</summary>
        internal float energy;
        /// <summary>Earlier default power draws: a config still holding one is moved to the current default.</summary>
        internal float[] previousEnergy => [energy / 10f];
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
        /// <summary>Separate slots for modules, opened with the module key.</summary>
        internal int moduleSlots;

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
        /// <summary>Separate slots for modules, opened with the module key.</summary>
        internal int moduleSlots;

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

    /// <summary>
    /// Wall display with the meteor network statistics. Cloned from one of the game's screens,
    /// which one is picked at registration.
    /// </summary>
    internal class DisplayDef : BuildingDef
    {
        internal override string ConfigSection => "Display";
    }

    /// <summary>
    /// The meteor beacon: draws a meteor shower every so often while the base has power.
    /// </summary>
    internal class BeaconDef : BuildingDef
    {
        internal float intervalMinutes;
        internal int moduleSlots;

        internal override string ConfigSection => "Meteor Beacon";
    }

    /// <summary>
    /// A building of the item storage network (AE2-style): controller, drive or terminal.
    /// </summary>
    internal class StorageDef : BuildingDef
    {
        /// <summary>Slots of the building: the core of a controller, the disks of a drive.</summary>
        internal int slots;
        internal override string ConfigSection => "Storage " + id.Replace("MeteorStorage", "");
    }

    /// <summary>
    /// A craftable item of the storage network: a tiered core (channels of a controller) or a storage disk.
    /// </summary>
    internal class StorageItemDef
    {
        internal string id;
        internal string recipe;
        internal DataConfig.CraftableIn craftStation = DataConfig.CraftableIn.CraftStationT2;
        /// <summary>Item to clone (its model is used); first existing one wins.</summary>
        internal string[] baseItemIds;
        /// <summary>Cores: how many devices a controller with it can serve.</summary>
        internal int channels;
        /// <summary>Disks: how many items and how many kinds they hold, and their power draw.</summary>
        internal int capacity, types;
        internal float energy;
        /// <summary>Terraformation (Ti) at which the recipe opens: with the storage buildings, later tiers a bit later.</summary>
        internal float unlockTi;
        internal string nameRu, nameEn, descRu, descEn;
        internal bool IsDisk => capacity > 0;
        internal string ConfigSection => "Storage " + id.Replace("MeteorStorage", "").Replace("MeteorCore", "Core ");
    }

    /// <summary>A rare item found only in captured meteors; ingredient of the advanced modules and the beacon.</summary>
    internal class CoreDef
    {
        internal string id;
        /// <summary>Item to clone (its model is used); first existing one wins.</summary>
        internal string[] baseItemIds;
        internal float dropChance;
        internal string nameRu, nameEn, descRu, descEn;
    }

    internal enum ResonatorEffect
    {
        /// <summary>Scanner: also detects meteors that carry no resources; magnets turn them into common ore.</summary>
        CaptureBarren,
        /// <summary>Magnet: loot goes straight to the storage network; the magnet's own storage is a buffer.</summary>
        StorageLink,
        /// <summary>+50% loot.</summary>
        LootBonus,
        /// <summary>Scanner: doubles the scan radius (whole-planet scanner: locks on faster instead).</summary>
        RadiusBoost,
        /// <summary>Every captured item has a chance to be duplicated.</summary>
        DuplicateChance,
        /// <summary>What the magnet cannot keep flows to another magnet of the same network.</summary>
        Overflow,
        /// <summary>Every meteor gives at least one of each resource type it carries.</summary>
        EveryResourceType,
        /// <summary>Scanner Mk3: with a satellite in orbit over the planet, scans the whole planet.</summary>
        SpaceLink,
        /// <summary>Magnet: halves the chance that a capture fails.</summary>
        Stabilizer,
        /// <summary>Scanner or magnet: draws less power.</summary>
        PowerSaver,
        /// <summary>Magnet: holds one more meteor at a time.</summary>
        ExtraCaptureSlot,
        /// <summary>Scanner: locks on faster.</summary>
        FasterLock,
        /// <summary>Scanner or magnet: rover upgrades in the same building work only with it.</summary>
        RoverSimulation,
        /// <summary>Scanner or magnet: player equipment in the same building works only with it.</summary>
        GearSimulation,
        /// <summary>Scanner or magnet: twice the module slots, every other module at half strength.</summary>
        EmergencyExpansion,
        /// <summary>Meteor beacon: calls showers more often.</summary>
        BeaconFrequency,
        /// <summary>Meteor beacon: calls only showers of common resources (rare ones cannot be targeted).</summary>
        BeaconCommon,
        /// <summary>Magnet: crushes the meteor in the field - never slips, but less loot.</summary>
        Destroyer,
        /// <summary>Magnet: scanners hand meteors to it before any other magnet.</summary>
        Dominant,
        /// <summary>Magnet: scanners hand meteors to it only when no other magnet is free.</summary>
        Recessive,
        /// <summary>Magnet: captured meteors go for the player, keep their damage and double their loot.</summary>
        AchievementHunter,
    }

    /// <summary>
    /// A module crafted from resources that exist on a single planet. Put it into the storage of the
    /// building it is made for (a scanner or a magnet); elsewhere it does nothing.
    /// Each kind counts once per building.
    /// </summary>
    internal class Resonator
    {
        internal string id;
        /// <summary>True for scanner modules, false for magnet modules.</summary>
        internal bool forScanner;
        /// <summary>Works in scanners and magnets alike.</summary>
        internal bool forBoth;
        /// <summary>Works only in the meteor beacon.</summary>
        internal bool forBeacon;
        /// <summary>Closed at the start: its recipe opens with a meteor blueprint decoded in a module slot.</summary>
        internal bool locked;
        /// <summary>Terraformation (Ti) at which the recipe opens, for modules that are not locked behind blueprints.</summary>
        internal float unlockTi;
        internal string planet;
        internal ResonatorEffect effect;
        /// <summary>Crafting station: planet resonators on T2, advanced modules on T3.</summary>
        internal DataConfig.CraftableIn craftStation = DataConfig.CraftableIn.CraftStationT2;
        /// <summary>Config section for the recipe; planet resonators by default.</summary>
        internal string ConfigSection => planet != null ? "Resonator " + planet : "Module " + effect;
        internal string recipe;
        /// <summary>
        /// Earlier default recipes. A config still holding one of them is moved to the current default,
        /// since BepInEx keeps the value written by an older version.
        /// </summary>
        internal string[] previousRecipes = [];
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
        /// <summary>Share of the game's acceleration a captured meteor keeps (full speed tunnels through the ground, zero freezes it).</summary>
        internal const float CapturedAccelerationFactor = 0.25f;
        /// <summary>A captured meteor slower than this (m/s) for StallTime seconds gets its acceleration back.</summary>
        internal const float StallSpeed = 2f;
        internal const float StallTime = 1.5f;
        /// <summary>How far below the target the final dive aims, so the meteor surely hits the ground.</summary>
        internal const float DiveDepth = 10f;
        internal const float DuplicateChance = 0.25f;
        internal const float ForwardRadius = 40f;

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
        /// <summary>Fail chance multiplier of a magnet with the field stabilizer.</summary>
        internal const float StabilizerFailFactor = 0.5f;
        /// <summary>Power draw multiplier of a building with the power saver.</summary>
        internal const float PowerSaverFactor = 0.6f;
        /// <summary>Lock-on time multiplier of a scanner with the guidance module.</summary>
        internal const float FasterLockFactor = 0.7f;
        /// <summary>Strength of the other modules next to the emergency expansion; tiers are rounded down.</summary>
        internal const float EmergencyStrength = 0.5f;
        /// <summary>Shower interval multiplier of a beacon with the frequency module.</summary>
        internal const float BeaconFrequencyFactor = 0.75f;
        /// <summary>Loot multiplier of a magnet with the destroyer.</summary>
        internal const float DestroyerLootFactor = 0.5f;
        /// <summary>An achievement hunter meteor landing this close to the player is a close call.</summary>
        internal const float HunterCloseCallDistance = 15f;
        /// <summary>Close calls for the copper, jade and crystal keys and Halliday's egg.</summary>
        internal static readonly int[] HunterKeys = [1, 10, 25, 50];
        /// <summary>Bonus of each working kind of equipment (core chance) or rover upgrade (lower fail chance), in absolute chance.</summary>
        internal const float SimulationBonusPerItem = 0.005f;
        /// <summary>How long a mark over an uncaught meteor stays, seconds.</summary>
        internal const float MarkerLifetime = 120f;
        internal const float RareFailBonus = 0.10f;

        /// <summary>What a barren meteor turns into with the Prime resonator.</summary>
        internal static readonly string[] BarrenLoot = ["Iron", "Titanium", "Magnesium", "Silicon", "Cobalt", "Aluminium"];

        internal static readonly MagnetTier[] Tiers =
        [
            new MagnetTier
            {
                id = "MeteorMagnet1", energy = 20f, level = 1,
                baseGroupIds = ["Beacon", "Container1"],
                hasStorage = false,
                captureSlots = 1,
                failChance = 0.20f,
                moduleSlots = 1,
                recipe = "Iron,Iron,Iron,Titanium,Titanium,Titanium,Magnesium,Magnesium,Silicon,Silicon",
                unlockTi = 0f,
                color = new Color(0.35f, 0.55f, 1f),
                nameRu = "Гравитационный захват Mk1",
                nameEn = "Meteor Magnet Mk1",
                descRu = "Притягивает метеориты, которые ему передаёт сканер (ставить в зоне связи сканера или реле). Держит 1 метеорит за раз. Ресурсы ссыпаются кучкой рядом. 1 ячейка под модуль (клавиша K).",
                descEn = "Pulls in meteors handed over by a scanner (place it in the link area of a scanner or relay). Holds 1 meteor at a time. Resources drop in a pile next to it. 1 module slot (K key).",
            },
            new MagnetTier
            {
                id = "MeteorMagnet2", energy = 60f, level = 2,
                baseGroupIds = ["Container1", "Container2"],
                hasStorage = true,
                captureSlots = 2,
                failChance = 0.12f,
                moduleSlots = 3,
                recipe = "Alloy,Alloy,Alloy,Aluminium,Aluminium,Cobalt,Cobalt,Titanium,Titanium,Iridium",
                unlockTi = 1000000f,
                color = new Color(0.55f, 0.25f, 1f),
                nameRu = "Гравитационный захват Mk2",
                nameEn = "Meteor Magnet Mk2",
                descRu = "Притягивает метеориты, которые ему передаёт сканер, по 2 за раз. Хранилище размером с малый сундук, фильтр — клавиша N. Модули захвата ставятся в отдельные ячейки (клавиша K).",
                descEn = "Pulls in meteors handed over by a scanner, 2 at a time. Small chest storage, filter on the N key. Magnet modules go into separate slots (K key).",
            },
            new MagnetTier
            {
                id = "MeteorMagnet3", energy = 150f, level = 3,
                baseGroupIds = ["Container2", "Container1"],
                hasStorage = true,
                captureSlots = 3,
                failChance = 0.08f,
                moduleSlots = 5,
                recipe = "Alloy,Alloy,Alloy,Alloy,Rod-uranium,Rod-uranium,Iridium,Iridium,Osmium,Osmium,Zeolite",
                unlockTi = 100000000f,
                color = new Color(1f, 0.3f, 0.75f),
                nameRu = "Гравитационный захват Mk3",
                nameEn = "Meteor Magnet Mk3",
                descRu = "Притягивает метеориты, которые ему передаёт сканер, по 3 за раз. Хранилище размером с большой сундук, фильтр — клавиша N. Модули захвата ставятся в отдельные ячейки (клавиша K).",
                descEn = "Pulls in meteors handed over by a scanner, 3 at a time. Big chest storage, filter on the N key. Magnet modules go into separate slots (K key).",
            },
        ];

        internal static readonly ScannerTier[] Scanners =
        [
            new ScannerTier
            {
                id = "MeteorScanner1", energy = 30f, level = 1,
                baseGroupIds = ["Beacon", "Container1"],
                moduleSlots = 2,
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
                descRu = "Замечает метеориты в радиусе 300 м, ведёт 2 цели и передаёт их захватам в радиусе связи 60 м. Редкие метеориты не видит. Фильтр метеоритов — клавиша N, модули — отдельные ячейки (клавиша K).",
                descEn = "Detects meteors within 300 m, tracks 2 targets and hands them to magnets within a 60 m link radius. Does not see rare meteors. Meteor filter on the N key, modules in separate slots (K key).",
            },
            new ScannerTier
            {
                id = "MeteorScanner2", energy = 100f, level = 2,
                baseGroupIds = ["Beacon", "Container1"],
                moduleSlots = 3,
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
                descRu = "Замечает метеориты в радиусе 800 м, ведёт 4 цели и передаёт их захватам в радиусе связи 100 м. Фильтр метеоритов — клавиша N, модули — отдельные ячейки (клавиша K).",
                descEn = "Detects meteors within 800 m, tracks 4 targets and hands them to magnets within a 100 m link radius. Meteor filter on the N key, modules in separate slots (K key).",
            },
            new ScannerTier
            {
                id = "MeteorScanner3", energy = 250f, level = 3,
                baseGroupIds = ["Beacon", "Container1"],
                moduleSlots = 4,
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
                descRu = "Замечает метеориты в радиусе 2000 м, ведёт 6 целей и передаёт их захватам в радиусе связи 150 м. С модулем космической связи и спутником на орбите видит всю планету. Фильтр метеоритов — клавиша N, модули — отдельные ячейки (клавиша K).",
                descEn = "Detects meteors within 2000 m, tracks 6 targets and hands them to magnets within a 150 m link radius. With a Space Link module and a satellite in orbit it sees the whole planet. Meteor filter on the N key, modules in separate slots (K key).",
            },
        ];

        internal static readonly RelayDef Relay = new()
        {
            id = "MeteorRelay", energy = 10f, level = 1,
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

        internal static readonly DisplayDef Display = new()
        {
            id = "MeteorDisplay", energy = 5f, level = 1,
            // Filled at registration with the game's screens.
            baseGroupIds = [],
            recipe = "Iron,Iron,Silicon,Silicon,Cobalt",
            unlockTi = 0f,
            color = new Color(1f, 0.9f, 0.4f),
            nameRu = "Дисплей метеорной сети",
            nameEn = "Meteor Network Display",
            descRu = "Экран сети: воспользуйся им (или наведись и нажми N) — откроется статистика: сколько метеоритов поймано, сорвалось и отброшено, сколько ресурсов и ядер собрано, загрузка сканеров и захватов, энергия сети.",
            descEn = "Network screen: use it (or aim and press N) to open the statistics: meteors captured, slipped and thrown away, resources and cores collected, scanner and magnet load, network power.",
        };

        internal static readonly BeaconDef Beacon = new()
        {
            id = "MeteorBeacon", energy = 500f, level = 3,
            baseGroupIds = ["Beacon", "Container1"],
            intervalMinutes = 20f,
            moduleSlots = 2,
            recipe = "MeteorCore,MeteorCore,MeteorCore,Alloy,Alloy,Alloy,Osmium,Osmium,Rod-uranium,Rod-uranium,Iridium,Iridium",
            unlockTi = 100000000f,
            color = new Color(1f, 0.45f, 0.15f),
            nameRu = "Метеорный маяк",
            nameEn = "Meteor Beacon",
            descRu = "Раз в 20 минут вызывает метеоритный дождь с ресурсами, если на базе хватает энергии. Очень прожорлив: 500 кВт. 2 ячейки под модули маяка (клавиша K).",
            descEn = "Draws a meteor shower with resources every 20 minutes while the base has power. Very power hungry: 500 kW. 2 slots for beacon modules (K key).",
        };

        internal static readonly CoreDef Core = new()
        {
            id = "MeteorCore",
            baseItemIds = ["Iridium", "Osmium", "Rod-uranium"],
            dropChance = 0.03f,
            nameRu = "Ядро метеорита",
            nameEn = "Meteor Core",
            descRu = "Плотное ядро, которое иногда остаётся от пойманного метеорита (3%). Нужно для всех модулей и метеорного маяка. Всегда попадает в хранилище захвата мимо фильтра (у Mk1 — в кучку). Продаётся торговой ракетой.",
            descEn = "A dense core sometimes left by a captured meteor (3%). Needed for every module and the meteor beacon. Always goes to the magnet's storage past the filter (Mk1: to the pile). Sells by trade rocket.",
        };

        /// <summary>
        /// Rare drop of captured meteors. Put into any scanner's or magnet's module slot, it is decoded and opens
        /// the recipe of a random locked module.
        /// </summary>
        internal static readonly CoreDef Blueprint = new()
        {
            id = "MeteorBlueprint",
            baseItemIds = ["BlueprintT1", "Rod-uranium"],
            dropChance = 0.02f,
            nameRu = "Метеорный чертёж",
            nameEn = "Meteor Blueprint",
            descRu = "Данные, записанные в ядре пойманного метеорита (2%). Положи в ячейку модулей сканера или захвата: чертёж расшифруется и откроет рецепт случайного закрытого модуля. Если закрытых не осталось — превратится в ядро метеорита.",
            descEn = "Data stored in the core of a captured meteor (2%). Put it into a scanner's or magnet's module slot: it is decoded and opens the recipe of a random locked module. With none left, it turns into a meteor core.",
        };

        internal const float StorageLinkRadius = 40f;

        internal static readonly StorageDef StorageController = new()
        {
            id = "MeteorStorageController", energy = 50f, level = 2, slots = 1,
            baseGroupIds = ["Beacon", "Container1"],
            recipe = "Alloy,Alloy,Iridium,Iridium,Silicon,Silicon,Silicon,Cobalt,Cobalt",
            unlockTi = 1000000f,
            color = new Color(0.2f, 0.9f, 1f),
            nameRu = "Контроллер хранилища",
            nameEn = "Storage Controller",
            descRu = "Сердце сети хранения. В ячейку (клавиша K) кладётся ядро: его тир задаёт, сколько дисководов и терминалов сеть обслуживает. Устройства работают в 40 м от контроллера или реле.",
            descEn = "Heart of the storage network. A core goes into its slot (K key): its tier sets how many drives and terminals the network serves. Devices work within 40 m of the controller or a relay.",
        };

        internal static readonly StorageDef StorageDrive = new()
        {
            id = "MeteorStorageDrive", energy = 10f, level = 2, slots = 4,
            baseGroupIds = ["Container1", "Beacon"],
            recipe = "Iron,Iron,Iron,Silicon,Silicon,Aluminium,Aluminium,Cobalt",
            unlockTi = 1000000f,
            color = new Color(0.4f, 0.7f, 1f),
            nameRu = "Дисковод",
            nameEn = "Disk Drive",
            descRu = "4 ячейки под диски хранения (клавиша K). Занимает один канал контроллера.",
            descEn = "4 slots for storage disks (K key). Takes one channel of the controller.",
        };

        internal static readonly StorageDef StorageTerminal = new()
        {
            id = "MeteorStorageTerminal", energy = 5f, level = 2, slots = 0,
            // Filled at registration with the game's screens, like the display.
            baseGroupIds = [],
            recipe = "Iron,Iron,Silicon,Silicon,Silicon,Cobalt",
            unlockTi = 1000000f,
            color = new Color(0.3f, 1f, 0.8f),
            nameRu = "Терминал хранилища",
            nameEn = "Storage Terminal",
            descRu = "Всё содержимое дисков сети одним списком, по одному значку на предмет. Воспользуйся им (или наведись и нажми N). Занимает один канал контроллера.",
            descEn = "Everything on the network's disks in one list, one icon per item. Use it (or aim and press N). Takes one channel of the controller.",
        };

        internal static readonly StorageDef[] StorageBuildings = [StorageController, StorageDrive, StorageTerminal];

        static StorageItemDef CoreTier(string id, string recipe, int channels, float unlockTi, string ru, string en, DataConfig.CraftableIn station, string descRu, string descEn) => new()
        {
            id = id, recipe = recipe, channels = channels, craftStation = station, unlockTi = unlockTi,
            baseItemIds = ["Iridium", "Osmium", "Rod-uranium"],
            nameRu = ru, nameEn = en,
            descRu = descRu + " В контроллере хранилища: " + channels + " каналов.",
            descEn = descEn + " In a storage controller: " + channels + " channels.",
        };

        /// <summary>Planet cores (tier 5): each made with a resource found only on that planet.</summary>
        internal static readonly (string planet, string resource, string ru, string en)[] PlanetCores =
        [
            ("Prime", "PulsarQuartz", "Ядро Прайма", "Prime Core"),
            ("Humble", "Dolomite", "Ядро Хамбла", "Humble Core"),
            ("Selenea", "Selenium", "Ядро Селении", "Selenea Core"),
            ("Aqualis", "Phosphorus", "Ядро Аквалиса", "Aqualis Core"),
            ("Toxicity", "ToxicGoo", "Ядро Токсисити", "Toxicity Core"),
            ("Skeo", "Amber", "Ядро Скео", "Skeo Core"),
        ];

        static string Times(string id, int n) => string.Join(",", System.Linq.Enumerable.Repeat(id, n));

        /// <summary>Cores in crafting order: each tier is 8 of the previous one plus one rare resource.</summary>
        internal static readonly List<StorageItemDef> StorageItems = BuildStorageItems();

        static List<StorageItemDef> BuildStorageItems()
        {
            var t2 = DataConfig.CraftableIn.CraftStationT2;
            var t3 = DataConfig.CraftableIn.CraftStationT3;
            var list = new List<StorageItemDef>
            {
                CoreTier("MeteorCore2", Times("MeteorCore", 8) + ",Osmium", 4, 1e6f, "Сжатое ядро", "Compressed Core", t2,
                    "Восемь ядер метеорита, спрессованных вокруг осмия.", "Eight meteor cores pressed around osmium."),
                CoreTier("MeteorCore3", Times("MeteorCore2", 8) + ",Rod-iridium", 6, 5e6f, "Плотное ядро", "Dense Core", t2,
                    "Восемь сжатых ядер, стянутых иридиевым стержнем.", "Eight compressed cores bound by an iridium rod."),
                CoreTier("MeteorCore4", Times("MeteorCore3", 8) + ",PulsarQuartz", 10, 5e7f, "Кристаллическое ядро", "Crystal Core", t3,
                    "Восемь плотных ядер, выращенных в пульсарный кристалл.", "Eight dense cores grown into a pulsar crystal."),
            };
            foreach (var (planet, resource, ru, en) in PlanetCores)
            {
                list.Add(CoreTier("MeteorCore" + planet, Times("MeteorCore4", 8) + "," + resource, 16, 5e8f, ru, en, t3,
                    "Ядро пятого тира, настроенное на планету " + planet + ". Шесть разных ядер планет собираются в сингулярность.",
                    "A tier 5 core attuned to " + planet + ". Six different planet cores make a singularity."));
            }
            list.Add(CoreTier("MeteorCoreSingularity",
                string.Join(",", System.Linq.Enumerable.Select(PlanetCores, p => "MeteorCore" + p.planet)) + ",CosmicQuartz,MagnetarQuartz",
                32, 5e9f, "Ядро-сингулярность", "Singularity Core", t3,
                "Ядра всех шести планет, сжатые в одну точку.", "The cores of all six planets crushed into one point."));

            // Disk T1 opens together with the storage buildings, each next tier with the core it is made of.
            (int tier, string recipe, int capacity, int types, float energy, float unlockTi)[] disks =
            [
                (1, "MeteorCore,Iron,Iron,Silicon,Silicon,Cobalt", 15, 3, 5f, 1e6f),
                (2, "MeteorCore2,Aluminium,Aluminium,Silicon,Silicon,Iridium", 30, 5, 15f, 5e6f),
                (3, "MeteorCore3,Alloy,Alloy,Silicon,Silicon,Osmium", 60, 8, 40f, 5e7f),
                (4, "MeteorCore4,Alloy,Alloy,Alloy,Iridium,Iridium,PulsarQuartz", 120, 12, 100f, 5e8f),
                (5, "MeteorCoreSingularity,Alloy,Alloy,Alloy,Osmium,Osmium,CosmicQuartz", 240, 16, 250f, 5e9f),
            ];
            foreach (var (tier, recipe, capacity, types, energy, unlockTi) in disks)
            {
                list.Add(new StorageItemDef
                {
                    id = "MeteorStorageDisk" + tier, recipe = recipe, capacity = capacity, types = types, energy = energy, unlockTi = unlockTi,
                    craftStation = tier >= 4 ? t3 : t2,
                    baseItemIds = ["Rod-uranium", "Iridium"],
                    nameRu = "Диск хранения T" + tier,
                    nameEn = "Storage Disk T" + tier,
                    descRu = "Вставляется в дисковод. Хранит до " + capacity + " предметов не больше " + types + " видов. Вещи остаются на диске, если его вынуть. Потребляет " + energy + " кВт.",
                    descEn = "Goes into a disk drive. Holds up to " + capacity + " items of at most " + types + " kinds. Items stay on the disk when it is taken out. Draws " + energy + " kW.",
                });
            }
            return list;
        }

        static Dictionary<string, StorageItemDef> storageItemById;

        internal static StorageItemDef StorageItemOf(string id)
        {
            storageItemById ??= BuildIndex(StorageItems.ToArray(), d => d.id);
            return id != null && storageItemById.TryGetValue(id, out var d) ? d : null;
        }

        /// <summary>Channels a core gives a controller: the meteor core 2, then the crafted tiers.</summary>
        internal static int ChannelsOf(string itemId) => itemId == Core.id ? 2 : StorageItemOf(itemId) is { IsDisk: false } d ? d.channels : 0;

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
                yield return Display;
                yield return Beacon;
                foreach (var s in StorageBuildings)
                {
                    yield return s;
                }
            }
        }

        internal static readonly Resonator[] Resonators =
        [
            new Resonator
            {
                id = "MeteorResonatorPrime", planet = "Prime", forScanner = false,
                effect = ResonatorEffect.StorageLink,
                recipe = "MeteorCore,PulsarQuartz,PulsarQuartz,Alloy",
                previousRecipes = ["PulsarQuartz,PulsarQuartz,Alloy"],
                color = new Color(0.3f, 0.8f, 1f),
                nameRu = "Пульсарный передатчик",
                nameEn = "Pulsar Transmitter",
                descRu = "Модуль гравитационного захвата. Соединяет захват с сетью хранения: лут сразу уходит на диски. Если сети нет, она без энергии или диски заполнены — лут ложится во внутреннее хранилище захвата и потом сам досылается в сеть. Захват должен стоять в зоне сети и занимает канал контроллера.",
                descEn = "Meteor Magnet module. Links the magnet to the storage network: loot goes straight to the disks. With no network, no power or full disks the loot goes to the magnet's own storage and is sent on later. The magnet must stand within the network's reach and takes a controller channel.",
            },
            new Resonator
            {
                id = "MeteorResonatorHumble", planet = "Humble",
                effect = ResonatorEffect.LootBonus,
                recipe = "MeteorCore,Dolomite,Dolomite,Bauxite,Bauxite,Uraninite",
                previousRecipes = ["Dolomite,Dolomite,Bauxite,Bauxite,Uraninite"],
                color = new Color(1f, 0.6f, 0.2f),
                nameRu = "Доломитовый концентратор",
                nameEn = "Dolomite Concentrator",
                descRu = "Модуль гравитационного захвата. +50% ресурсов с каждого метеорита.",
                descEn = "Meteor Magnet module. +50% resources from every meteor.",
            },
            new Resonator
            {
                id = "MeteorResonatorSelenea", planet = "Selenea", forScanner = true,
                effect = ResonatorEffect.RadiusBoost,
                recipe = "MeteorCore,Selenium,Selenium,BalzarQuartz",
                previousRecipes = ["Selenium,Selenium,BalzarQuartz"],
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
                recipe = "MeteorCore,ToxicGoo,ToxicGoo,ToxicGoo,Alloy",
                previousRecipes = ["ToxicGoo,ToxicGoo,ToxicGoo,Alloy"],
                color = new Color(0.7f, 1f, 0.1f),
                nameRu = "Токсичный катализатор",
                nameEn = "Toxic Catalyst",
                descRu = "Модуль гравитационного захвата. Каждый пойманный ресурс с шансом 25% удваивается.",
                descEn = "Meteor Magnet module. Every captured resource has a 25% chance to be doubled.",
            },
            new Resonator
            {
                id = "MeteorResonatorAqualis", planet = "Aqualis",
                effect = ResonatorEffect.Overflow,
                recipe = "MeteorCore,Phosphorus,Phosphorus,Phosphorus,Alloy",
                previousRecipes = ["Phosphorus,Phosphorus,Phosphorus,Alloy"],
                color = new Color(0.2f, 0.5f, 1f),
                nameRu = "Фосфорный гидроперелив",
                nameEn = "Phosphorus Overflow",
                descRu = "Модуль гравитационного захвата. Что не влезло в хранилище, не прошло фильтр или (у Mk1) упало бы кучкой, перетекает в другой захват той же сети: сначала в тот, чей фильтр это хочет, потом в захват без фильтра.",
                descEn = "Meteor Magnet module. What does not fit, does not pass the filter or (Mk1) would drop in a pile flows to another magnet of the same network: first one whose filter wants it, then one without a filter.",
            },
            new Resonator
            {
                id = "MeteorResonatorSkeo", planet = "Skeo",
                effect = ResonatorEffect.EveryResourceType,
                recipe = "MeteorCore,Amber,Amber,Amber,Alloy",
                previousRecipes = ["Amber,Amber,Amber,Alloy"],
                color = new Color(1f, 0.75f, 0f),
                nameRu = "Янтарный сепаратор",
                nameEn = "Amber Separator",
                descRu = "Модуль гравитационного захвата. С каждого метеорита гарантированно приходит хотя бы по одному ресурсу каждого вида, который он несёт.",
                descEn = "Meteor Magnet module. Every meteor gives at least one of each resource type it carries.",
            },
            new Resonator
            {
                id = "MeteorSpaceLink", planet = null, forScanner = true,
                // Useful only with a Mk3 scanner and the satellite rocket: opens with them.
                unlockTi = 100000000f,
                craftStation = DataConfig.CraftableIn.CraftStationT3,
                effect = ResonatorEffect.SpaceLink,
                recipe = "MeteorCore,MeteorCore,Alloy,Alloy,Osmium,Iridium,Iridium,Silicon,Silicon",
                previousRecipes = ["Alloy,Alloy,Osmium,Iridium,Iridium,Silicon,Silicon"],
                color = new Color(0.75f, 0.6f, 1f),
                nameRu = "Модуль космической связи",
                nameEn = "Space Link Module",
                descRu = "Модуль сканера Mk3. Если над планетой запущена метеорная спутниковая ракета, сканер видит метеориты по всей планете.",
                descEn = "Scanner Mk3 module. With a meteor satellite rocket launched over the planet, the scanner sees meteors all over the planet.",
            },
            new Resonator
            {
                id = "MeteorFieldStabilizer", planet = null, locked = true, forScanner = false,
                effect = ResonatorEffect.Stabilizer,
                recipe = "MeteorCore,Alloy,Alloy,Iridium,Iridium,Aluminium,Aluminium,Rod-uranium",
                previousRecipes = ["Alloy,Alloy,Iridium,Iridium,Aluminium,Aluminium,Rod-uranium"],
                color = new Color(0.4f, 0.9f, 1f),
                nameRu = "Стабилизатор поля",
                nameEn = "Field Stabilizer",
                descRu = "Модуль гравитационного захвата. Вдвое снижает шанс срыва захвата, в том числе для редких метеоритов.",
                descEn = "Meteor Magnet module. Halves the chance that a capture fails, rare meteors included.",
            },
            new Resonator
            {
                id = "MeteorPowerSaver", planet = null, locked = true, forBoth = true,
                effect = ResonatorEffect.PowerSaver,
                recipe = "MeteorCore,Aluminium,Aluminium,Silicon,Silicon,Cobalt",
                color = new Color(0.5f, 1f, 0.4f),
                nameRu = "Модуль экономии",
                nameEn = "Power Saver Module",
                descRu = "Модуль сканера или захвата. Постройка потребляет на 40% меньше энергии.",
                descEn = "Scanner or magnet module. The building draws 40% less power.",
            },
            new Resonator
            {
                id = "MeteorExpansion", planet = null, locked = true, forScanner = false,
                craftStation = DataConfig.CraftableIn.CraftStationT3,
                effect = ResonatorEffect.ExtraCaptureSlot,
                recipe = "MeteorCore,MeteorCore,Alloy,Alloy,Iridium,Iridium",
                color = new Color(1f, 0.85f, 0.3f),
                nameRu = "Модуль расширения",
                nameEn = "Expansion Module",
                descRu = "Модуль гравитационного захвата. Захват держит на один метеорит больше.",
                descEn = "Meteor Magnet module. The magnet holds one more meteor at a time.",
            },
            new Resonator
            {
                id = "MeteorGuidance", planet = null, locked = true, forScanner = true,
                effect = ResonatorEffect.FasterLock,
                recipe = "MeteorCore,Alloy,Silicon,Silicon,Iridium",
                color = new Color(1f, 0.35f, 0.35f),
                nameRu = "Модуль наведения",
                nameEn = "Guidance Module",
                descRu = "Модуль сканера. Захват цели на 30% быстрее.",
                descEn = "Scanner module. Locks on 30% faster.",
            },
            new Resonator
            {
                id = "MeteorRoverSimulation", planet = null, locked = true, forBoth = true,
                effect = ResonatorEffect.RoverSimulation,
                recipe = "MeteorCore,Aluminium,Aluminium,Silicon,Silicon,Iron",
                color = new Color(1f, 0.6f, 0.2f),
                nameRu = "Модуль симуляции ровера",
                nameEn = "Rover Simulation Module",
                descRu = "Модуль сканера или захвата. Эмулирует бортовую систему ровера: без него улучшения ровера в этой постройке не работают.",
                descEn = "Scanner or magnet module. Emulates a rover's onboard system: rover upgrades in this building work only with it.",
            },
            new Resonator
            {
                id = "MeteorGearSimulation", planet = null, locked = true, forBoth = true,
                effect = ResonatorEffect.GearSimulation,
                recipe = "MeteorCore,Silicon,Silicon,Cobalt,Cobalt,Titanium",
                color = new Color(0.85f, 0.85f, 0.95f),
                nameRu = "Модуль симуляции экипировки",
                nameEn = "Equipment Simulation Module",
                descRu = "Модуль сканера или захвата. Эмулирует скафандр: без него экипировка игрока в этой постройке не работает.",
                descEn = "Scanner or magnet module. Emulates a spacesuit: player equipment in this building works only with it.",
            },
            new Resonator
            {
                id = "MeteorBeaconFrequency", planet = null, locked = true, forBeacon = true,
                effect = ResonatorEffect.BeaconFrequency,
                recipe = "MeteorCore,Alloy,Alloy,Silicon,Silicon,Cobalt",
                color = new Color(1f, 0.55f, 0.1f),
                nameRu = "Модуль частоты маяка",
                nameEn = "Beacon Frequency Module",
                descRu = "Модуль метеорного маяка. Маяк вызывает дождь на 25% чаще.",
                descEn = "Meteor beacon module. The beacon calls showers 25% more often.",
            },
            new Resonator
            {
                id = "MeteorBeaconCommon", planet = null, locked = true, forBeacon = true,
                effect = ResonatorEffect.BeaconCommon,
                recipe = "MeteorCore,Iron,Iron,Titanium,Titanium,Magnesium",
                color = new Color(0.8f, 0.7f, 0.5f),
                nameRu = "Модуль настройки маяка",
                nameEn = "Beacon Tuning Module",
                descRu = "Модуль метеорного маяка. Маяк вызывает только дожди обычных ресурсов, без редких (суперсплав, осмий, кварцы). Настроить маяк на редкие нельзя.",
                descEn = "Meteor beacon module. The beacon calls only showers of common resources, no rare ones (super alloy, osmium, quartzes). It cannot be tuned to rare ones.",
            },
            new Resonator
            {
                id = "MeteorDestroyer", planet = null, locked = true, forScanner = false,
                effect = ResonatorEffect.Destroyer,
                recipe = "MeteorCore,MeteorCore,Alloy,Alloy,Iridium,Osmium",
                color = new Color(1f, 0.4f, 0.1f),
                nameRu = "Модуль разрушителя",
                nameEn = "Destroyer Module",
                descRu = "Модуль гравитационного захвата. Поле не держит метеорит, а дробит его: захват никогда не срывается, но ресурсов вдвое меньше.",
                descEn = "Meteor Magnet module. The field crushes the meteor instead of holding it: the capture never slips, but there is half the loot.",
            },
            new Resonator
            {
                id = "MeteorDominant", planet = null, locked = true, forScanner = false,
                effect = ResonatorEffect.Dominant,
                recipe = "MeteorCore,Silicon,Silicon,Cobalt,Aluminium",
                color = new Color(0.3f, 1f, 0.5f),
                nameRu = "Доминантный модуль",
                nameEn = "Dominant Module",
                descRu = "Модуль гравитационного захвата. Сканеры отдают метеориты этому захвату первым, раньше фильтров и расстояния.",
                descEn = "Meteor Magnet module. Scanners hand meteors to this magnet first, before filters and distance.",
            },
            new Resonator
            {
                id = "MeteorRecessive", planet = null, locked = true, forScanner = false,
                effect = ResonatorEffect.Recessive,
                recipe = "MeteorCore,Silicon,Silicon,Cobalt,Iron",
                color = new Color(0.5f, 0.5f, 1f),
                nameRu = "Рецессивный модуль",
                nameEn = "Recessive Module",
                descRu = "Модуль гравитационного захвата. Этот захват получает метеорит, только когда все остальные заняты — запасной.",
                descEn = "Meteor Magnet module. This magnet gets a meteor only when every other one is busy - a backup.",
            },
            new Resonator
            {
                id = "MeteorAchievementHunter", planet = null, locked = true, forScanner = false,
                effect = ResonatorEffect.AchievementHunter,
                recipe = "MeteorCore,MeteorCore,MeteorCore,Alloy,Uranim,Silicon",
                color = new Color(1f, 0.85f, 0.2f),
                nameRu = "Охотник за ачивками",
                nameEn = "Achievement Hunter",
                descRu = "Модуль гравитационного захвата. «Первому игроку приготовиться!» Пойманные метеориты летят прямо в тебя, зато ресурсов вдвое больше (ядра и чертежи не удваиваются). Удар ближе 15 м от тебя — «на волосок»: за 1, 10 и 25 таких — медный, нефритовый и хрустальный ключи, за 50 — пасхалка Холлидея.",
                descEn = "Meteor Magnet module. \"Ready Player One!\" Captured meteors come straight at you, but bring double resources (cores and blueprints are not doubled). An impact within 15 m of you is a close call: 1, 10 and 25 of them give the copper, jade and crystal keys, 50 - Halliday's egg.",
            },
            new Resonator
            {
                id = "MeteorEmergencyExpansion", planet = null, locked = true, forBoth = true,
                effect = ResonatorEffect.EmergencyExpansion,
                recipe = "MeteorCore,MeteorCore,Alloy,Iron,Iron,Silicon,Silicon",
                color = new Color(1f, 0.2f, 0.2f),
                nameRu = "Модуль экстренного расширения",
                nameEn = "Emergency Expansion Module",
                descRu = "Модуль сканера или захвата. Удваивает число ячеек модулей, но все остальные модули в постройке работают вдвое слабее (тиры округляются вниз: T1 перестаёт работать).",
                descEn = "Scanner or magnet module. Doubles the module slots, but every other module in the building works at half strength (tiers round down: T1 stops working).",
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
