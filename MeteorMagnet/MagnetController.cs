using SpaceCraft;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Runs the meteor network: scanners detect meteors and lock on to them, then hand them to a free
    /// magnet inside their link area (around the scanner and linked relays). The magnet pulls the meteor
    /// in and, once it lands, puts its resources into storage (Mk2/Mk3, respecting the filter) or drops
    /// them in a pile next to it (Mk1, filtered out, storage full).
    /// </summary>
    internal partial class MagnetController : MonoBehaviour
    {
        const float ScanInterval = 0.25f;

        // If a captured meteor neither crashes nor disappears in this time, deliver the loot anyway.
        const float CaptureTimeout = 90f;

        /// <summary>A placed mod building with the modules found in its storage.</summary>
        abstract class Building
        {
            internal WorldObject wo;
            internal Vector3 position;
            internal Inventory inventory;
            internal readonly HashSet<ResonatorEffect> effects = [];
            /// <summary>Highest tier of each rover upgrade inside the storage.</summary>
            internal readonly Dictionary<RoverUpgradeType, int> upgrades = [];

            /// <summary>Power draw of this building, the power saver included.</summary>
            /// <summary>Rover oxygen (through the rover simulation): own life support, works and draws nothing without base power.</summary>
            internal bool Autonomous => Upgrade(RoverUpgradeType.Oxygen) > 0;

            internal float Draw => Autonomous ? 0f : Plugin.EnergyUse(wo.GetGroup().GetId())
                * (effects.Contains(ResonatorEffect.PowerSaver) ? Weaken(Content.PowerSaverFactor) : 1f);

            /// <summary>Emergency expansion inside: twice the module slots, every other module at half strength.</summary>
            internal bool Weakened => effects.Contains(ResonatorEffect.EmergencyExpansion);

            /// <summary>Strength of the other modules: 1, or 0.5 with the emergency expansion.</summary>
            internal float Strength => Weakened ? Content.EmergencyStrength : 1f;

            /// <summary>A module multiplier at the building's strength: x2 becomes x1.5, x0.5 becomes x0.75.</summary>
            internal float Weaken(float factor) => 1f + (factor - 1f) * Strength;

            /// <summary>
            /// Every rover upgrade / piece of equipment in the slots, working or not (duplicates, no effect here,
            /// no effect at all): each adds to the simulation bonus while the matching simulation module is there.
            /// </summary>
            internal int roverItems, gearItems;

            /// <summary>Highest tier of each kind of player equipment in the module slots.</summary>
            internal readonly Dictionary<GearType, int> gear = [];

            internal int Gear(GearType t) => gear.TryGetValue(t, out var v) ? v : 0;

            /// <summary>Pile light and glow: the rover lights or the multitool torch chip, whichever is higher.</summary>
            internal int LightTier => Math.Max(Upgrade(RoverUpgradeType.Lights), Gear(GearType.TorchChip));

            internal int Upgrade(RoverUpgradeType t) => upgrades.TryGetValue(t, out var v) ? v : 0;

            /// <summary>The building's separate module slots (see <see cref="ModuleSlots"/>).</summary>
            internal Inventory moduleInventory;

            /// <summary>What each item in the module slots does here, for the debug log.</summary>
            internal readonly List<string> moduleReport = [];

            // Rover upgrades each building type makes use of.
            static readonly HashSet<RoverUpgradeType> scannerUpgrades = [RoverUpgradeType.Equipment, RoverUpgradeType.Beacon, RoverUpgradeType.Oxygen];
            static readonly HashSet<GearType> scannerGear = [GearType.Exoskeleton, GearType.Boots, GearType.OxygenTank, GearType.Compass];
            static readonly HashSet<GearType> magnetGear =
                [GearType.Exoskeleton, GearType.Backpack, GearType.Jetpack, GearType.MiningChip, GearType.TorchChip, GearType.DeconstructChip];
            static readonly HashSet<RoverUpgradeType> magnetUpgrades =
                [RoverUpgradeType.Storage, RoverUpgradeType.Speed, RoverUpgradeType.Lights, RoverUpgradeType.Logistics, RoverUpgradeType.Jetpack,
                    RoverUpgradeType.Oxygen];

            internal void ReadModules()
            {
                if (moduleInventory == null)
                {
                    return;
                }
                var isScanner = this is Scanner;
                var here = isScanner ? "scanner" : "magnet";
                foreach (var item in moduleInventory.GetInsideWorldObjects())
                {
                    var itemId = item?.GetGroup()?.GetId();
                    if (itemId == Content.Blueprint.id)
                    {
                        moduleReport.Add(itemId + ": " + DecodeBlueprint(item, moduleInventory));
                        continue;
                    }
                    var res = Content.ResonatorOf(itemId);
                    if (res != null)
                    {
                        // A module only works in the building it is made for.
                        if (res.forBeacon)
                        {
                            moduleReport.Add(itemId + ": IGNORED, works only in the meteor beacon");
                        }
                        else if (!res.forBoth && res.forScanner != isScanner)
                        {
                            moduleReport.Add(itemId + ": IGNORED, works only in a " + (res.forScanner ? "scanner" : "magnet"));
                        }
                        else if (!effects.Add(res.effect))
                        {
                            moduleReport.Add(itemId + ": IGNORED, " + res.effect + " is already active (duplicates do not stack)");
                        }
                        else
                        {
                            moduleReport.Add(itemId + ": active -> " + res.effect);
                        }
                        continue;
                    }
                    if (RoverUpgrades.TryGet(itemId, out var type, out var upTier))
                    {
                        roverItems++;
                        var used = isScanner ? scannerUpgrades : magnetUpgrades;
                        if (!used.Contains(type))
                        {
                            moduleReport.Add(itemId + ": IGNORED, rover " + type + " has no effect in a " + here);
                        }
                        else if (upTier > Upgrade(type))
                        {
                            moduleReport.Add(itemId + ": active -> rover " + type + " T" + upTier
                                + (upgrades.ContainsKey(type) ? " (replaces the lower tier)" : ""));
                            upgrades[type] = upTier;
                        }
                        else
                        {
                            moduleReport.Add(itemId + ": IGNORED, " + type + " T" + Upgrade(type) + " or higher is already active");
                        }
                        continue;
                    }
                    if (PlayerGear.TryGet(itemId, out var gearType, out var gearTier))
                    {
                        gearItems++;
                        var usedGear = isScanner ? scannerGear : magnetGear;
                        if (!usedGear.Contains(gearType))
                        {
                            moduleReport.Add(itemId + ": IGNORED, equipment " + gearType + " has no effect in a " + here);
                        }
                        else if (gearTier > Gear(gearType))
                        {
                            moduleReport.Add(itemId + ": active -> equipment " + gearType + " T" + gearTier
                                + (gear.ContainsKey(gearType) ? " (replaces the lower tier)" : ""));
                            gear[gearType] = gearTier;
                        }
                        else
                        {
                            moduleReport.Add(itemId + ": IGNORED, " + gearType + " T" + Gear(gearType) + " or higher is already active");
                        }
                        continue;
                    }
                    if (PlayerGear.IsOtherEquipment(itemId))
                    {
                        gearItems++;
                        moduleReport.Add(itemId + ": equipment without an effect, counts for the simulation bonus");
                        continue;
                    }
                    moduleReport.Add((itemId ?? "?") + ": not a module");
                }

                // Rover upgrades and player equipment are made for other machines: they work only through
                // the matching simulation module in the same building.
                if (roverItems != 0 && !effects.Contains(ResonatorEffect.RoverSimulation))
                {
                    moduleReport.Add("rover upgrades INACTIVE (" + roverItems + " items): no Rover Simulation module here");
                    upgrades.Clear();
                    roverItems = 0;
                }
                if (gearItems != 0 && !effects.Contains(ResonatorEffect.GearSimulation))
                {
                    moduleReport.Add("equipment INACTIVE (" + gearItems + " items): no Equipment Simulation module here");
                    gear.Clear();
                    gearItems = 0;
                }

                // Emergency expansion: tiers are halved, rounded down (a T1 item stops working).
                if (Weakened)
                {
                    moduleReport.Add("EMERGENCY EXPANSION: module slots x2, other modules at " + (Content.EmergencyStrength * 100f).ToString("0") + "%");
                    HalveTiers(upgrades, "rover");
                    HalveTiers(gear, "equipment");
                }
            }

            void HalveTiers<T>(Dictionary<T, int> tiers, string what)
            {
                foreach (var key in new List<T>(tiers.Keys))
                {
                    var halved = Mathf.FloorToInt(tiers[key] * Content.EmergencyStrength);
                    moduleReport.Add("  " + what + " " + key + " T" + tiers[key] + " -> " + (halved == 0 ? "off" : "T" + halved));
                    if (halved == 0)
                    {
                        tiers.Remove(key);
                    }
                    else
                    {
                        tiers[key] = halved;
                    }
                }
            }
        }

        class Magnet : Building
        {
            internal MagnetTier tier;
            /// <summary>Group ids that go to storage; empty = everything.</summary>
            internal readonly HashSet<string> filter = [];
            /// <summary>Meteors this magnet is pulling in right now.</summary>
            internal int busy;

            internal int CaptureSlots => tier.captureSlots + (effects.Contains(ResonatorEffect.ExtraCaptureSlot) ? Mathf.FloorToInt(Strength) : 0);
        }

        class Scanner : Building
        {
            internal ScannerTier tier;
            /// <summary>Meteors this scanner is tracking right now.</summary>
            internal int tracking;
            /// <summary>Link area: the scanner's own circle plus those of its linked relays.</summary>
            internal readonly List<(Vector3 center, float radius)> link = [];
            /// <summary>Magnets inside the link area.</summary>
            internal readonly List<Magnet> network = [];
            /// <summary>Resource ids the scanner looks for; empty = every meteor.</summary>
            internal readonly HashSet<string> filter = [];

            /// <summary>Mk3 with a Space Link module over a planet that has a satellite in orbit.</summary>
            internal bool planetWide;

            internal float ScanRadius => planetWide ? float.PositiveInfinity : BaseRadius;

            /// <summary>Scan radius without the satellite.</summary>
            internal float BaseRadius => tier.scanRadius * (effects.Contains(ResonatorEffect.RadiusBoost) ? Weaken(Content.RadiusBoostFactor) : 1f)
                * (1f + PlayerGear.OxygenRadiusPerTier * Gear(GearType.OxygenTank));

            internal float LinkFactor => Upgrade(RoverUpgradeType.Beacon) > 0 ? RoverUpgrades.BeaconLinkFactor : 1f;

            internal float LockTime => tier.lockTime
                * (effects.Contains(ResonatorEffect.FasterLock) ? Weaken(Content.FasterLockFactor) : 1f)
                * Mathf.Max(0.1f, 1f - PlayerGear.BootsLockPerTier * Gear(GearType.Boots))
                * (planetWide && effects.Contains(ResonatorEffect.RadiusBoost) ? Weaken(Content.LensLockFactor) : 1f);

            internal int TrackSlots => tier.trackSlots + RoverUpgrades.EquipmentTargets(Upgrade(RoverUpgradeType.Equipment));
        }

        class Capture
        {
            internal Magnet magnet;
            /// <summary>The scanner that found the meteor, for the log and the debug beam.</summary>
            internal Scanner scanner;
            internal Vector3 target;
            internal Vector3 pile;
            internal List<Group> loot;
            internal float capturedAt;
            internal bool delivered;
            /// <summary>Bad failure: the meteor is thrown away, keeps its vanilla loot and gets no delivery.</summary>
            internal bool deflected;
            /// <summary>Meteor position on the previous frame, to measure its speed.</summary>
            internal Vector3? lastPosition;
            /// <summary>Final dive started: the meteor is aimed into the ground and no longer steered.</summary>
            internal bool released;
            /// <summary>The meteor was caught passing through the ground once and put back on the surface.</summary>
            internal bool unTunneled;
            /// <summary>The game's acceleration before capture, to put back if the meteor stalls.</summary>
            internal float originalAcceleration = float.NaN;
            /// <summary>When the meteor was first seen standing still (0 = moving).</summary>
            internal float stalledSince;
            internal bool accelerationRestored;
            /// <summary>Achievement hunter: the meteor is pulled at the player instead of next to the magnet.</summary>
            internal bool hunting;
        }

        /// <summary>A meteor a scanner is tracking: locking on, then waiting for a free magnet.</summary>
        class Track
        {
            internal WorldObject scanner;
            internal float since;
            /// <summary>Lock-on finished and was logged.</summary>
            internal bool lockLogged;
            /// <summary>"Waiting for a free magnet" was logged.</summary>
            internal bool waitLogged;
        }

        readonly Dictionary<Asteroid, Capture> captures = [];
        readonly Dictionary<Asteroid, Track> tracks = [];
        // Meteors already decided (captured, failed or not worth it), so each one is decided only once.
        readonly HashSet<int> seen = [];
        readonly List<Magnet> magnets = [];
        readonly List<Scanner> scanners = [];
        readonly List<(WorldObject wo, Vector3 position)> relays = [];
        readonly List<WorldObject> beacons = [];
        readonly List<WorldObject> storageControllers = [];
        readonly List<WorldObject> storageDrives = [];
        readonly List<WorldObject> storageTerminals = [];
        // Slipped meteors, followed to the ground to mark where they land (last seen position).
        readonly Dictionary<Asteroid, Vector3> slippedWatch = [];
        // When the beacons call the next shower (NaN = no beacon yet).
        float nextShower = float.NaN;
        int displayCount;
        readonly Dictionary<int, Light> glows = [];
        // Vanilla container sizes by group id, used as the base size of Mk2/Mk3 storage.
        readonly Dictionary<string, int> baseSizes = [];
        static readonly HashSet<string> warnedNoBaseSize = [];
        // Magnets already reported as outside every link area.
        readonly HashSet<int> warnedUnlinked = [];
        readonly List<Asteroid> done = [];
        readonly SignalLines signalLines = new("MeteorMagnet_SignalLines", true);
        // Debug beams from buildings to the meteors they are tracking or pulling, redrawn every frame.
        readonly SignalLines meteorLines = new("MeteorMagnet_MeteorLines", false);
        // Meteors already reported as seen by no scanner, so the reason is logged once per meteor.
        readonly HashSet<int> loggedUnseen = [];

        float nextScan;
        float nextOrphanCheck;
        WorldObjectsHandler lastWorld;

        void Update()
        {
            if (!Plugin.Enabled || WorldObjectsHandler.Instance == null || InventoriesHandler.Instance == null)
            {
                return;
            }
            // Inventories and item creation are authoritative on the host.
            if (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer)
            {
                return;
            }

            if (WorldObjectsHandler.Instance != lastWorld)
            {
                // Another world was loaded: nothing from the previous one applies.
                lastWorld = WorldObjectsHandler.Instance;
                worldLoadedAt = Time.time;
                ModuleSlots.Reset();
                loggedModules.Clear();
                signalLines.Clear();
                meteorLines.Clear();
                loggedUnseen.Clear();
                MeteorDisplay.Reset();
                StorageNetwork.Reset();
                MeteorMarkers.Clear();
                slippedWatch.Clear();
                nextShower = float.NaN;
                captures.Clear();
                tracks.Clear();
                seen.Clear();
            }
            if (Time.time >= nextOrphanCheck)
            {
                nextOrphanCheck = Time.time + 5f;
                ModuleSlots.DropOrphans();
            }

            if (Time.time >= nextScan)
            {
                nextScan = Time.time + ScanInterval;
                RefreshBuildings();
                BuildNetworks();
                UpdatePower();
                ScanForMeteors();
                UpdateDisplays();
                RunBeacons();
            }

            if (Plugin.PullMeteors)
            {
                SteerCaptured();
            }
            CheckLanded();
            WatchSlipped();
            DrawMeteorLines();
        }

        void RefreshBuildings()
        {
            magnets.Clear();
            scanners.Clear();
            relays.Clear();
            beacons.Clear();
            storageControllers.Clear();
            storageDrives.Clear();
            storageTerminals.Clear();
            beaconEffects.Clear();
            beaconModules.Clear();
            displayCount = 0;
            var planetHash = CurrentPlanetHash();
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                var gid = wo?.GetGroup()?.GetId();
                var tier = Content.TierOf(gid);
                var scannerTier = Content.ScannerOf(gid);
                var relay = Content.IsRelay(gid);
                if (gid == Content.StorageController.id || gid == Content.StorageDrive.id || gid == Content.StorageTerminal.id)
                {
                    if (wo.GetIsPlaced() && (planetHash == 0 || wo.GetPlanetHash() == planetHash))
                    {
                        var list = gid == Content.StorageController.id ? storageControllers
                            : gid == Content.StorageDrive.id ? storageDrives : storageTerminals;
                        list.Add(wo);
                        var def = gid == Content.StorageController.id ? Content.StorageController
                            : gid == Content.StorageDrive.id ? Content.StorageDrive : Content.StorageTerminal;
                        Decorate(wo, def.color, def.level, 0);
                    }
                    continue;
                }
                if (gid == Content.Display.id || gid == Content.Beacon.id)
                {
                    if (wo.GetIsPlaced() && (planetHash == 0 || wo.GetPlanetHash() == planetHash))
                    {
                        if (gid == Content.Beacon.id)
                        {
                            beacons.Add(wo);
                            ReadBeaconModules(wo);
                            Decorate(wo, Content.Beacon.color, Content.Beacon.level, 0);
                        }
                        else
                        {
                            displayCount++;
                        }
                    }
                    continue;
                }
                if (tier == null && scannerTier == null && !relay)
                {
                    RememberBaseSize(wo, gid);
                    continue;
                }
                if (!wo.GetIsPlaced() || (planetHash != 0 && wo.GetPlanetHash() != planetHash))
                {
                    continue;
                }

                if (relay)
                {
                    relays.Add((wo, wo.GetPosition()));
                    Decorate(wo, Content.Relay.color, 1, 0);
                }
                else if (scannerTier != null)
                {
                    var scanner = new Scanner
                    {
                        wo = wo,
                        tier = scannerTier,
                        position = wo.GetPosition(),
                        moduleInventory = ModuleSlots.Get(wo, scannerTier.moduleSlots),
                    };
                    foreach (var g in wo.GetLinkedGroups() ?? [])
                    {
                        if (g != null)
                        {
                            scanner.filter.Add(g.GetId());
                        }
                    }
                    scanner.ReadModules();
                    ModuleSlots.SetExtraSlots(wo.GetId(), ExtraModuleSlots(scanner, scannerTier.moduleSlots));
                    LogModules(scanner);
                    scanner.planetWide = scannerTier.usesSatellite
                        && scanner.effects.Contains(ResonatorEffect.SpaceLink)
                        && Satellite.IsLaunched(planetHash != 0 ? planetHash : wo.GetPlanetHash());
                    scanners.Add(scanner);
                    Decorate(wo, scannerTier.color, scannerTier.level, 0);
                }
                else
                {
                    var magnet = new Magnet
                    {
                        wo = wo,
                        tier = tier,
                        position = wo.GetPosition(),
                        inventory = tier.hasStorage ? InventoriesHandler.Instance.GetInventoryById(wo.GetLinkedInventoryId()) : null,
                    };
                    foreach (var g in wo.GetLinkedGroups() ?? [])
                    {
                        if (g != null)
                        {
                            magnet.filter.Add(g.GetId());
                        }
                    }
                    magnet.moduleInventory = ModuleSlots.Get(wo, tier.moduleSlots);
                    magnet.ReadModules();
                    ModuleSlots.SetExtraSlots(wo.GetId(), ExtraModuleSlots(magnet, tier.moduleSlots));
                    LogModules(magnet);
                    if (magnet.inventory != null)
                    {
                        ApplyStorageSize(magnet);
                    }
                    magnets.Add(magnet);
                    Decorate(wo, tier.color, tier.level, magnet.LightTier);
                }
            }
            var linkedMagnets = new List<WorldObject>();
            foreach (var m in magnets)
            {
                if (m.effects.Contains(ResonatorEffect.StorageLink))
                {
                    linkedMagnets.Add(m.wo);
                }
            }
            StorageNetwork.Build(storageControllers, storageDrives, storageTerminals, linkedMagnets, relays.ConvertAll(r => r.position));
            FlushBuffers();
            UpdateSummaries();
        }

        /// <summary>
        /// Works out each scanner's link area (its own circle, grown through chains of relays)
        /// and which magnets stand inside it. Also warns once about magnets no scanner reaches.
        /// </summary>
        void BuildNetworks()
        {
            var linked = new HashSet<Magnet>();
            var drawLines = Plugin.ShowSignalLines;
            if (drawLines)
            {
                signalLines.Begin();
            }
            foreach (var s in scanners)
            {
                var factor = s.LinkFactor;
                s.link.Add((s.position, s.tier.linkRadius * factor));
                // Who each circle of the link area belongs to, for the signal lines.
                var nodes = new List<(int id, Vector3 pos, Color color)> { (s.wo.GetId(), s.position, s.tier.color) };

                // Relays join when they stand inside the area reached so far; repeat until nothing new joins.
                var joined = new bool[relays.Count];
                bool grew;
                do
                {
                    grew = false;
                    for (int i = 0; i < relays.Count; i++)
                    {
                        if (joined[i])
                        {
                            continue;
                        }
                        var parent = CoveringNode(s.link, relays[i].position);
                        if (parent >= 0)
                        {
                            joined[i] = true;
                            s.link.Add((relays[i].position, Content.Relay.linkRadius * factor));
                            var relayNode = (relays[i].wo.GetId(), relays[i].position, Content.Relay.color);
                            nodes.Add(relayNode);
                            if (drawLines)
                            {
                                var from = nodes[parent];
                                signalLines.Edge(from.id, from.pos, from.color, relayNode.Item1, relayNode.Item2, relayNode.Item3);
                            }
                            grew = true;
                        }
                    }
                }
                while (grew);

                foreach (var m in magnets)
                {
                    var parent = CoveringNode(s.link, m.position);
                    if (parent >= 0)
                    {
                        s.network.Add(m);
                        linked.Add(m);
                        if (drawLines)
                        {
                            var from = nodes[parent];
                            signalLines.Edge(from.id, from.pos, from.color, m.wo.GetId(), m.position, m.tier.color);
                        }
                    }
                }
            }
            if (drawLines)
            {
                signalLines.End();
            }
            else
            {
                signalLines.Clear();
            }

            foreach (var m in magnets)
            {
                if (!linked.Contains(m) && warnedUnlinked.Add(m.wo.GetId()))
                {
                    NotifyText("MeteorMagnet_NoScanner", DataConfig.UiInformationsType.OutInventory);
                }
                else if (linked.Contains(m))
                {
                    warnedUnlinked.Remove(m.wo.GetId());
                }
            }
        }

        /// <summary>The nearest circle of the area that covers the point, or -1.</summary>
        static int CoveringNode(List<(Vector3 center, float radius)> area, Vector3 p)
        {
            var best = -1;
            var bestDist = float.MaxValue;
            for (int i = 0; i < area.Count; i++)
            {
                var d = area[i].center - p;
                d.y = 0;
                var dist = d.sqrMagnitude;
                if (dist <= area[i].radius * area[i].radius && dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
            return best;
        }

        // What each building's modules were last time, so changes are logged once.
        readonly Dictionary<int, string> loggedModules = [];

        /// <summary>
        /// Logs, when anything changes, what is in a building's module slots, what each item does there,
        /// and the resulting numbers of the building.
        /// </summary>
        void LogModules(Building b)
        {
            var lines = new List<string>();
            lines.Add(b.moduleInventory == null
                ? "module slots not ready yet"
                : "module slots " + b.moduleInventory.GetInsideWorldObjects().Count + "/" + b.moduleInventory.GetSize());
            foreach (var r in b.moduleReport)
            {
                lines.Add("  " + r);
            }
            if (b is Scanner sc)
            {
                var lens = sc.effects.Contains(ResonatorEffect.RadiusBoost);
                lines.Add("  => scan radius: " + (sc.planetWide ? "whole planet (satellite + Space Link)"
                    : sc.ScanRadius.ToString("0") + " m" + (lens ? " (base " + sc.tier.scanRadius.ToString("0") + " x" + Content.RadiusBoostFactor + " lens)" : "")));
                if (sc.tier.usesSatellite && !sc.planetWide)
                {
                    lines.Add("     whole planet needs: " + (sc.effects.Contains(ResonatorEffect.SpaceLink) ? "" : "Space Link module ")
                        + (Satellite.IsLaunched(sc.wo.GetPlanetHash()) ? "" : "satellite launched over this planet"));
                }
                lines.Add("  => link radius: " + (sc.tier.linkRadius * sc.LinkFactor).ToString("0") + " m (relays "
                    + (Content.Relay.linkRadius * sc.LinkFactor).ToString("0") + " m)" + (sc.LinkFactor > 1f ? " incl. Beacon x" + sc.LinkFactor : ""));
                lines.Add("  => tracks " + sc.TrackSlots + " meteors (base " + sc.tier.trackSlots + ")"
                    + ", lock-on " + sc.LockTime.ToString("0.0") + " s (rare x" + Content.RareLockFactor + ")"
                    + ", rare meteors " + (sc.tier.seesRare ? "seen" : "NOT seen")
                    + (sc.effects.Contains(ResonatorEffect.CaptureBarren) ? ", barren meteors seen" : ""));
                lines.Add("  => equipment: scan radius x" + (1f + PlayerGear.OxygenRadiusPerTier * sc.Gear(GearType.OxygenTank)).ToString("0.##")
                    + " (oxygen tank), lock-on x" + Mathf.Max(0.1f, 1f - PlayerGear.BootsLockPerTier * sc.Gear(GearType.Boots)).ToString("0.##")
                    + " (boots), compass tags " + (sc.Gear(GearType.Compass) > 0 ? "on" : "off"));
                lines.Add("  => filter: " + (sc.filter.Count == 0 ? "none, tracks every meteor" : "only meteors with " + string.Join("/", sc.filter)));
            }
            else if (b is Magnet m)
            {
                var stabilized = m.effects.Contains(ResonatorEffect.Stabilizer);
                lines.Add("  => holds " + m.CaptureSlots + " meteors, fail chance " + (FailChance(m, false) * 100f).ToString("0.#") + "% ("
                    + (FailChance(m, true) * 100f).ToString("0.#") + "% for rare, scanner rover bonus not included)" + (stabilized ? ", field stabilizer x" + m.Weaken(Content.StabilizerFailFactor).ToString("0.##") : ""));
                var loot = new List<string>();
                if (m.effects.Contains(ResonatorEffect.LootBonus))
                {
                    loot.Add("x" + m.Weaken(Content.LootBonusFactor).ToString("0.##") + " amount");
                }
                if (m.effects.Contains(ResonatorEffect.DuplicateChance))
                {
                    loot.Add((Content.DuplicateChance * m.Strength * 100f).ToString("0.#") + "% duplicate per item");
                }
                if (m.effects.Contains(ResonatorEffect.EveryResourceType))
                {
                    loot.Add("at least one of each resource type" + (m.Weakened ? " (on half of the meteors)" : ""));
                }
                if (m.effects.Contains(ResonatorEffect.Overflow))
                {
                    loot.Add("overflow to other magnets of the network" + (m.Weakened ? " (half of the items)" : ""));
                }
                lines.Add("  => loot: " + (loot.Count == 0 ? "as the meteor carries" : string.Join(", ", loot)));
                lines.Add("  => pull turn speed x" + (1f + RoverUpgrades.SpeedTurnRatePerTier * m.Upgrade(RoverUpgradeType.Speed))
                    + ", storage +" + RoverUpgrades.StorageSlots(m.Upgrade(RoverUpgradeType.Storage)) + " slots"
                    + (m.Upgrade(RoverUpgradeType.Logistics) > 0 ? ", overflow to chests within " + Content.ForwardRadius.ToString("0") + " m" : "")
                    + (m.Upgrade(RoverUpgradeType.Jetpack) > 0 ? ", loot delivered at capture" : "")
                    + (m.LightTier > 0 ? ", pile light T" + m.LightTier : ""));
                lines.Add("  => equipment: storage +" + PlayerGear.BackpackSlotsPerTier * m.Gear(GearType.Backpack) + " slots (backpack)"
                    + ", meteor acceleration x" + CapturedAcceleration(m).ToString("0.##") + " (jetpack)"
                    + ", loot x" + (1f + PlayerGear.MiningLootPerTier * m.Gear(GearType.MiningChip)).ToString("0.##") + " (mining chip)"
                    + ", core chance " + (CoreChance(m) * 100f).ToString("0.#") + "% (deconstruction chip, equipment simulation; scanner bonus not included)");
            }
            lines.Add("  => power draw " + b.Draw.ToString("0.#") + " kW"
                + (b.effects.Contains(ResonatorEffect.PowerSaver) ? " (power saver x" + b.Weaken(Content.PowerSaverFactor) + ")" : ""));
            if (b.gearItems + b.roverItems > 0)
            {
                lines.Add("  => simulation bonus: meteor core chance +" + (GearBonus(b) * 100f).ToString("0.##") + " points (" + b.gearItems
                    + " equipment items), fail chance -" + (RoverBonus(b) * 100f).ToString("0.##") + " points (" + b.roverItems + " rover upgrades)"
                    + (b is Scanner ? ", for the meteors this scanner hands over" : ""));
            }
            var text = string.Join("\n", lines);
            var id = b.wo.GetId();
            if (!loggedModules.TryGetValue(id, out var last) || last != text)
            {
                loggedModules[id] = text;
                Plugin.Log(b.wo.GetGroup().GetId() + " #" + id + ": " + text);
            }
        }

        void RememberBaseSize(WorldObject wo, string gid)
        {
            if (gid == null || baseSizes.ContainsKey(gid) || !gid.StartsWith("Container", StringComparison.Ordinal))
            {
                return;
            }
            var inv = InventoriesHandler.Instance.GetInventoryById(wo.GetLinkedInventoryId());
            if (inv != null)
            {
                baseSizes[gid] = inv.GetSize();
            }
        }

        /// <summary>
        /// Storage upgrades add slots on top of the size of the container the magnet is built from.
        /// The base size is taken from a vanilla container of the same kind, since the magnet's
        /// own (saved) size may already include a bonus.
        /// </summary>
        void ApplyStorageSize(Magnet magnet)
        {
            var baseId = magnet.tier.resolvedBaseId;
            if (baseId == null || !baseSizes.TryGetValue(baseId, out var baseSize))
            {
                if (magnet.Upgrade(RoverUpgradeType.Storage) > 0 && baseId != null && warnedNoBaseSize.Add(baseId))
                {
                    Plugin.LogWarning("Storage upgrade needs at least one ordinary " + baseId + " built on this planet to know the base size.");
                }
                return;
            }
            var wanted = baseSize + RoverUpgrades.StorageSlots(magnet.Upgrade(RoverUpgradeType.Storage))
                + PlayerGear.BackpackSlotsPerTier * magnet.Gear(GearType.Backpack);
            // Never shrink below what is stored right now.
            wanted = Mathf.Max(wanted, magnet.inventory.GetInsideWorldObjects().Count);
            if (magnet.inventory.GetSize() != wanted)
            {
                magnet.inventory.SetSize(wanted);
                Plugin.Log("Magnet " + magnet.wo.GetId() + " storage size -> " + wanted);
            }
        }

        static int CurrentPlanetHash()
        {
            var pl = Managers.GetManager<PlanetLoader>();
            return pl?.GetCurrentPlanetData()?.GetPlanetHash() ?? 0;
        }

        // Whether the base has power for the network; autonomous buildings (rover oxygen) work without it.
        static bool powerOk = true;

        internal static bool NetworkPowered => powerOk;

        // When the world was loaded: the base's energy is not counted yet for the first seconds.
        float worldLoadedAt;
        const float PowerGraceSeconds = 10f;

        void UpdatePower()
        {
            if (Time.time - worldLoadedAt < PowerGraceSeconds)
            {
                powerOk = true;
                return;
            }
            var hadPower = powerOk;
            powerOk = Plugin.PowerOk(NetworkDemand(), out var powerDetails);
            if (!powerOk && hadPower)
            {
                Plugin.Log("No power for the meteor network (" + powerDetails + "), dropping " + tracks.Count + " tracked meteors");
                NotifyText("MeteorMagnet_NoPower", DataConfig.UiInformationsType.OutInventory);
            }
        }

        void ScanForMeteors()
        {
            if (scanners.Count == 0 || magnets.Count == 0)
            {
                tracks.Clear();
                return;
            }
            if (!powerOk)
            {
                // Without power only autonomous scanners keep their targets.
                var lost = new List<Asteroid>();
                foreach (var kv in tracks)
                {
                    var s = FindScanner(kv.Value.scanner);
                    if (s == null || !s.Autonomous)
                    {
                        lost.Add(kv.Key);
                    }
                }
                foreach (var a in lost)
                {
                    tracks.Remove(a);
                }
            }
            CountLoad();
            var alive = new HashSet<Asteroid>();
            foreach (var asteroid in FindObjectsByType<Asteroid>(FindObjectsSortMode.None))
            {
                if (asteroid == null || seen.Contains(asteroid.GetInstanceID()))
                {
                    continue;
                }
                if (Plugin.HasCrashed(asteroid))
                {
                    if (tracks.TryGetValue(asteroid, out var lost))
                    {
                        tracks.Remove(asteroid);
                        var lostScanner = FindScanner(lost.scanner);
                        var rareLost = asteroid.groupsSelected != null && asteroid.groupsSelected.Count != 0 && IsRare(asteroid);
                        var lockNeeded = lostScanner != null ? lostScanner.LockTime * (rareLost ? Content.RareLockFactor : 1f) : 0f;
                        var tracked = Time.time - lost.since;
                        Plugin.Log(Describe(asteroid) + " crashed while tracked by " + Describe(lostScanner) + ": " + (tracked < lockNeeded ? "lock not done" : "no free magnet")
                            + " (tracked " + tracked.ToString("0.0") + " s, lock " + lockNeeded.ToString("0.0") + " s"
                            + ", resources=" + string.Join("/", (asteroid.groupsSelected ?? []).ConvertAll(g => g != null ? g.id : "?")) + ")");
                    }
                    continue;
                }
                alive.Add(asteroid);
                // Barren meteors (plain rocks) are only seen by scanners with the Prime resonator.
                var barren = asteroid.groupsSelected == null || asteroid.groupsSelected.Count == 0;
                var rare = !barren && IsRare(asteroid);
                var landing = PredictLanding(asteroid.transform);

                tracks.TryGetValue(asteroid, out var track);
                var scanner = track != null ? FindScanner(track.scanner) : null;
                if (scanner == null || !CanSee(scanner, asteroid, landing, barren, rare))
                {
                    if (track != null)
                    {
                        tracks.Remove(asteroid);
                    }
                    // Pick up a new target: the nearest scanner that sees it and has a free tracking slot.
                    var lostBy = scanner;
                    scanner = PickScanner(asteroid, landing, barren, rare);
                    if (scanner != null)
                    {
                        tracks[asteroid] = new Track { scanner = scanner.wo, since = Time.time };
                        scanner.tracking++;
                        loggedUnseen.Remove(asteroid.GetInstanceID());
                        Plugin.Log(Describe(scanner) + " started tracking " + Describe(asteroid)
                            + (lostBy != null ? " (taken over from " + Describe(lostBy) + ")" : "")
                            + ": meteor at " + asteroid.transform.position + ", predicted landing " + landing
                            + " " + HorizontalDistance(scanner.position, landing).ToString("0") + " m from the scanner (scan radius "
                            + (float.IsInfinity(scanner.ScanRadius) ? "whole planet" : scanner.ScanRadius.ToString("0") + " m") + ")"
                            + ", tracking " + scanner.tracking + "/" + scanner.TrackSlots
                            + ", lock-on needs " + (scanner.LockTime * (rare ? Content.RareLockFactor : 1f)).ToString("0.0") + " s");
                    }
                    else if (loggedUnseen.Add(asteroid.GetInstanceID()))
                    {
                        Plugin.Log(Describe(asteroid) + " not tracked, landing " + landing + ": " + WhyUnseen(asteroid, landing, barren, rare));
                    }
                    continue;
                }

                // Lock on first, then wait for a magnet with a free slot.
                var lockTime = scanner.LockTime * (rare ? Content.RareLockFactor : 1f);
                if (Time.time - track.since < lockTime)
                {
                    continue;
                }
                if (!track.lockLogged)
                {
                    track.lockLogged = true;
                    Plugin.Log(Describe(scanner) + " locked on " + Describe(asteroid) + " after " + (Time.time - track.since).ToString("0.0")
                        + " s (needed " + lockTime.ToString("0.0") + " s), meteor at " + asteroid.transform.position);
                }
                var magnet = PickMagnet(scanner, asteroid, landing, out var pickedBecause);
                if (magnet == null)
                {
                    if (!track.waitLogged)
                    {
                        track.waitLogged = true;
                        Plugin.Log(Describe(asteroid) + " waits for a free magnet: " + DescribeNetwork(scanner));
                    }
                    continue;
                }
                tracks.Remove(asteroid);
                scanner.tracking--;
                seen.Add(asteroid.GetInstanceID());
                magnet.busy++;

                var failChance = FailChance(magnet, rare, scanner);
                var failRoll = UnityEngine.Random.value;
                Plugin.Log(Describe(scanner) + " hands " + Describe(asteroid) + " to " + Describe(magnet)
                    + " (" + HorizontalDistance(magnet.position, landing).ToString("0") + " m from the predicted landing, slots "
                    + magnet.busy + "/" + magnet.CaptureSlots + ", picked: " + pickedBecause + "): fail chance " + (failChance * 100f).ToString("0") + "%"
                    + (rare ? " incl. rare +" + (Content.RareFailBonus * 100f).ToString("0") + "%" : "")
                    + (magnet.effects.Contains(ResonatorEffect.Stabilizer) ? " x" + magnet.Weaken(Content.StabilizerFailFactor).ToString("0.##") + " stabilizer" : "")
                    + (RoverBonus(magnet) + RoverBonus(scanner) > 0f ? " -" + ((RoverBonus(magnet) + RoverBonus(scanner)) * 100f).ToString("0.#")
                        + " points rover simulation (" + magnet.roverItems + " in magnet, " + scanner.roverItems + " in scanner)" : "")
                    + ", roll " + (failRoll * 100f).ToString("0") + " -> " + (failRoll < failChance ? "FAILED" : "ok"));
                if (failRoll < failChance)
                {
                    var badRoll = UnityEngine.Random.value;
                    if (badRoll < Content.BadFailShare)
                    {
                        Plugin.Log("Bad failure roll " + (badRoll * 100f).ToString("0") + " < " + (Content.BadFailShare * 100f).ToString("0") + ": meteor is thrown away");
                        Deflect(asteroid, magnet, scanner);
                    }
                    else
                    {
                        magnet.busy--;
                        Plugin.Log("Capture slipped (bad failure roll " + (badRoll * 100f).ToString("0") + " >= "
                            + (Content.BadFailShare * 100f).ToString("0") + "), " + Describe(asteroid) + " keeps its course to " + landing);
                        NotifyText("MeteorMagnet_Slipped", DataConfig.UiInformationsType.OutInventory);
                        MeteorStats.AddSlipped();
                        slippedWatch[asteroid] = asteroid.transform.position;
                    }
                    continue;
                }

                var loot = barren ? RollBarrenLoot(asteroid, magnet) : RollLoot(asteroid, magnet);
                var coreRoll = UnityEngine.Random.value;
                var core = GroupsHandler.GetGroupViaId(Content.Core.id);
                // No cap: every full 100% is a sure core, the rest is the chance of one more.
                var coreChance = CoreChance(magnet, scanner);
                var cores = Mathf.FloorToInt(coreChance) + (coreRoll < coreChance - Mathf.Floor(coreChance) ? 1 : 0);
                if (core != null && loot.Count != 0 && cores > 0)
                {
                    for (int i = 0; i < cores; i++)
                    {
                        loot.Add(core);
                    }
                    NotifyText("MeteorMagnet_CoreFound", DataConfig.UiInformationsType.InInventory);
                }
                var blueprintRoll = UnityEngine.Random.value;
                var blueprint = GroupsHandler.GetGroupViaId(Content.Blueprint.id);
                if (blueprint != null && loot.Count != 0 && blueprintRoll < Plugin.BlueprintDropChance)
                {
                    loot.Add(blueprint);
                    NotifyText("MeteorMagnet_BlueprintFound", DataConfig.UiInformationsType.InInventory);
                }
                Plugin.Log("Meteor blueprint roll " + (blueprintRoll * 100f).ToString("0.0") + " vs " + (Plugin.BlueprintDropChance * 100f).ToString("0.0")
                    + "% -> " + (blueprint == null ? "blueprint item missing" : blueprintRoll < Plugin.BlueprintDropChance ? "BLUEPRINT FOUND" : "none"));
                Plugin.Log("Meteor core roll " + (coreRoll * 100f).ToString("0.0") + " vs " + (coreChance * 100f).ToString("0.0")
                    + "% -> " + (core == null ? "core item missing" : cores > 0 ? cores + " CORE(S) FOUND" : "no core"));
                if (loot.Count == 0)
                {
                    magnet.busy--;
                    Plugin.Log(Describe(asteroid) + ": loot roll gave nothing, meteor left alone");
                    continue;
                }

                // Neutralize the impact: no resources on the ground, no damage.
                var originalAcceleration = Plugin.LimitAcceleration(asteroid, CapturedAcceleration(magnet));
                asteroid.numberOfResourceInDebris = 0;
                // Achievement hunter: the meteor goes for the player, keeps its damage and doubles its loot.
                var hunting = magnet.effects.Contains(ResonatorEffect.AchievementHunter) && (!magnet.Weakened || Coin(asteroid));
                if (hunting)
                {
                    // Only the resources: cores and blueprints are not doubled.
                    loot.AddRange(loot.FindAll(g => g.GetId() != Content.Core.id && g.GetId() != Content.Blueprint.id));
                }
                asteroid.doDamage = hunting;
                asteroid.shakeOnImpact = hunting || Plugin.ShakeOnImpact;
                if (!Plugin.LeaveAsteroidBody)
                {
                    asteroid.placeAsteroidBody = false;
                }

                var away = landing - magnet.position;
                away.y = 0;
                if (away.sqrMagnitude < 0.01f)
                {
                    away = Vector3.forward;
                }

                var capture = new Capture
                {
                    magnet = magnet,
                    scanner = scanner,
                    target = hunting ? PlayerPosition() : GroundPoint(magnet.position + away.normalized * Plugin.ImpactOffset),
                    pile = magnet.position + away.normalized * 3f + Vector3.up * 1.5f,
                    loot = loot,
                    capturedAt = Time.time,
                    originalAcceleration = originalAcceleration,
                    hunting = hunting,
                };
                if (hunting)
                {
                    NotifyText("MeteorMagnet_ReadyPlayerOne", DataConfig.UiInformationsType.OutInventory);
                    Plugin.Log("Achievement hunter: " + Describe(asteroid) + " goes for the player at " + capture.target + ", loot doubled to " + loot.Count);
                }
                if (magnet.Upgrade(RoverUpgradeType.Jetpack) > 0)
                {
                    Deliver(capture);
                    capture.delivered = true;
                }
                captures[asteroid] = capture;
                MeteorStats.AddCaptured();
                CaptureEffects.Flash(asteroid.transform.position, magnet.tier.color, 8f, 80f);
                CaptureEffects.Flash(magnet.position + Vector3.up * 3f, magnet.tier.color, 4f, 15f);
                CaptureEffects.Trail(asteroid.gameObject, magnet.tier.color);
                Plugin.Log("Captured " + Describe(asteroid) + " with " + loot.Count + " resources for " + Describe(magnet)
                    + ", found by " + Describe(scanner) + " (tracked " + (Time.time - track.since).ToString("0.0") + " s)"
                    + ": meteor at " + asteroid.transform.position + ", would land " + landing + ", pulled to " + capture.target
                    + " (magnet at " + magnet.position + "), loot: " + string.Join("/", loot.ConvertAll(g => g.GetId()))
                    + (capture.delivered ? ", delivered at once (Jetpack)" : ""));
            }

            // Forget tracks of meteors that are gone.
            var stale = new List<Asteroid>();
            foreach (var a in tracks.Keys)
            {
                if (a == null || !alive.Contains(a))
                {
                    stale.Add(a);
                }
            }
            foreach (var a in stale)
            {
                tracks.Remove(a);
            }

            if (seen.Count > 4096)
            {
                seen.Clear();
            }
            if (loggedUnseen.Count > 4096)
            {
                loggedUnseen.Clear();
            }
        }

        static string Describe(Building b)
        {
            return b == null ? "(no building)" : b.wo.GetGroup()?.GetId() + " #" + b.wo.GetId();
        }

        /// <summary>"meteor 12345 [Iron x4, Silicon x3, rare]" for the log.</summary>
        static string Describe(Asteroid a)
        {
            if (a == null)
            {
                return "meteor (gone)";
            }
            var counts = new Dictionary<string, int>();
            var order = new List<string>();
            foreach (var g in a.groupsSelected ?? [])
            {
                var id = g != null ? g.id : "?";
                if (!counts.ContainsKey(id))
                {
                    counts[id] = 0;
                    order.Add(id);
                }
                counts[id]++;
            }
            var parts = order.ConvertAll(id => id + " x" + counts[id]);
            if (order.Count == 0)
            {
                parts.Add("barren");
            }
            else if (IsRare(a))
            {
                parts.Add("rare");
            }
            return "meteor " + a.GetInstanceID() + " [" + string.Join(", ", parts) + "]";
        }

        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var d = a - b;
            d.y = 0;
            return d.magnitude;
        }

        /// <summary>Why no scanner picked up a meteor, scanner by scanner.</summary>
        string WhyUnseen(Asteroid asteroid, Vector3 landing, bool barren, bool rare)
        {
            var reasons = new List<string>();
            foreach (var s in scanners)
            {
                string why;
                if (!powerOk && !s.Autonomous)
                {
                    why = "no power (and no rover oxygen for autonomy)";
                }
                else if (!PassesFilter(s, asteroid))
                {
                    why = "filtered out (looks for " + string.Join("/", s.filter) + ")";
                }
                else if (barren && !SeesBarren(s, asteroid))
                {
                    why = "barren meteor, needs the Prime resonator";
                }
                else if (rare && !s.tier.seesRare)
                {
                    why = "rare meteor, Mk1 does not see rare ones";
                }
                else if (s.network.Count == 0)
                {
                    why = "no magnet in its link area";
                }
                else if (HorizontalDistance(s.position, landing) > Radius(s, asteroid))
                {
                    why = "out of range " + HorizontalDistance(s.position, landing).ToString("0") + " > " + Radius(s, asteroid).ToString("0") + " m"
                        + (s.planetWide && s.Weakened ? " (whole-planet scan at half strength misses this one)" : "");
                }
                else if (s.tracking >= s.TrackSlots)
                {
                    why = "all " + s.TrackSlots + " tracking slots busy";
                }
                else
                {
                    why = "?";
                }
                reasons.Add(Describe(s) + ": " + why);
            }
            return string.Join("; ", reasons);
        }

        static string DescribeNetwork(Scanner s)
        {
            var parts = s.network.ConvertAll(m => Describe(m) + " " + m.busy + "/" + m.CaptureSlots + " busy");
            return parts.Count == 0 ? "no magnets linked to " + Describe(s) : string.Join(", ", parts);
        }

        /// <summary>
        /// Debug beams: scanner -> meteor it tracks (faint while locking on, bright once locked),
        /// magnet -> meteor it pulls (red for a meteor being thrown away).
        /// </summary>
        void DrawMeteorLines()
        {
            UpdateCompassTags();
            if (!Plugin.ShowMeteorLines)
            {
                meteorLines.Clear();
                return;
            }
            meteorLines.Begin();
            foreach (var kv in tracks)
            {
                var s = kv.Key != null && !Plugin.HasCrashed(kv.Key) ? FindScanner(kv.Value.scanner) : null;
                if (s == null)
                {
                    continue;
                }
                var locked = Time.time - kv.Value.since >= s.LockTime * (IsRare(kv.Key) ? Content.RareLockFactor : 1f);
                var c = s.tier.color;
                c.a = locked ? 1f : 0.4f;
                meteorLines.Edge(s.wo.GetId(), s.position, c, kv.Key.GetInstanceID(), kv.Key.transform.position, c);
            }
            foreach (var kv in captures)
            {
                if (kv.Key == null || Plugin.HasCrashed(kv.Key))
                {
                    continue;
                }
                var c = kv.Value.deflected ? Color.red : kv.Value.magnet.tier.color;
                meteorLines.Edge(kv.Value.magnet.wo.GetId(), kv.Value.magnet.position, c, kv.Key.GetInstanceID(), kv.Key.transform.position, c);
            }
            meteorLines.End();
        }

        /// <summary>Follows slipped meteors down and marks where they land.</summary>
        void WatchSlipped()
        {
            if (slippedWatch.Count == 0)
            {
                return;
            }
            var landed = new List<Asteroid>();
            var moved = new List<Asteroid>();
            foreach (var kv in slippedWatch)
            {
                if (kv.Key == null || Plugin.HasCrashed(kv.Key))
                {
                    var pos = kv.Key != null ? kv.Key.transform.position : kv.Value;
                    MeteorMarkers.Add(pos, new Color(1f, 0.75f, 0.2f), "slipped meteor " + (kv.Key != null ? "landed" : "disappeared"));
                    landed.Add(kv.Key);
                }
                else
                {
                    moved.Add(kv.Key);
                }
            }
            foreach (var a in moved)
            {
                slippedWatch[a] = a.transform.position;
            }
            foreach (var a in landed)
            {
                slippedWatch.Remove(a);
            }
        }

        /// <summary>
        /// Meteor beacons call a shower every IntervalMinutes while the network has power.
        /// The timer starts when the first beacon is built; a busy sky postpones the shower by a minute.
        /// </summary>
        // Effects of the modules in all beacons (they do not stack: one of a kind is enough) and each beacon's slots.
        readonly HashSet<ResonatorEffect> beaconEffects = [];
        readonly Dictionary<int, Inventory> beaconModules = [];

        void ReadBeaconModules(WorldObject wo)
        {
            var inv = ModuleSlots.Get(wo, Content.Beacon.moduleSlots);
            beaconModules[wo.GetId()] = inv;
            var report = new List<string>();
            foreach (var item in (IEnumerable<WorldObject>)inv?.GetInsideWorldObjects() ?? [])
            {
                var itemId = item?.GetGroup()?.GetId();
                var res = Content.ResonatorOf(itemId);
                if (res != null && res.forBeacon)
                {
                    beaconEffects.Add(res.effect);
                    report.Add(itemId + ": active -> " + res.effect);
                }
                else
                {
                    report.Add((itemId ?? "?") + ": IGNORED, not a beacon module");
                }
            }
            var text = string.Join(", ", report);
            if (!loggedModules.TryGetValue(wo.GetId(), out var last) || last != text)
            {
                loggedModules[wo.GetId()] = text;
                Plugin.Log("MeteorBeacon #" + wo.GetId() + " modules: " + (text.Length == 0 ? "none" : text));
            }
        }

        float BeaconInterval => Plugin.BeaconIntervalSeconds
            * (beaconEffects.Contains(ResonatorEffect.BeaconFrequency) ? Content.BeaconFrequencyFactor : 1f);

        void RunBeacons()
        {
            if (beacons.Count == 0)
            {
                if (!float.IsNaN(nextShower))
                {
                    Plugin.Log("No meteor beacons left, shower timer stopped");
                }
                nextShower = float.NaN;
                return;
            }
            if (float.IsNaN(nextShower))
            {
                nextShower = Time.time + BeaconInterval;
                Plugin.Log(beacons.Count + " meteor beacon(s): next shower in " + (BeaconInterval / 60f).ToString("0.#") + " min");
                return;
            }
            if (Time.time < nextShower)
            {
                return;
            }
            if (!powerOk)
            {
                nextShower = Time.time + 60f;
                Plugin.Log("Meteor beacon: no power, shower postponed by a minute");
                return;
            }
            if (MeteorBeacon.TrySummon(beaconEffects.Contains(ResonatorEffect.BeaconCommon), out var result))
            {
                nextShower = Time.time + BeaconInterval;
                NotifyText("MeteorMagnet_BeaconShower", DataConfig.UiInformationsType.OutInventory);
                Plugin.Log("Meteor beacon: " + result + ", next in " + (BeaconInterval / 60f).ToString("0.#") + " min");
            }
            else
            {
                nextShower = Time.time + 60f;
                Plugin.Log("Meteor beacon: " + result + ", retry in a minute");
            }
        }

        string FreeText()
        {
            var free = Plugin.FreePower();
            return float.IsInfinity(free) ? "?" : (free - NetworkDemand()).ToString("0.#");
        }

        /// <summary>Power the whole meteor network draws, modules included.</summary>
        float NetworkDemand()
        {
            var demand = 0f;
            foreach (var s in scanners)
            {
                demand += s.Draw;
            }
            foreach (var m in magnets)
            {
                demand += m.Draw;
            }
            demand += relays.Count * Plugin.EnergyUse(Content.Relay.id);
            demand += displayCount * Plugin.EnergyUse(Content.Display.id);
            demand += beacons.Count * Plugin.EnergyUse(Content.Beacon.id);
            demand += StorageNetwork.Demand();
            return demand;
        }

        /// <summary>Statistics for the wall displays; also saves them into the world.</summary>
        void UpdateDisplays()
        {
            MeteorStats.EnsureLoaded();
            MeteorStats.Save();
            int tracking = 0, trackSlots = 0, pulling = 0, pullSlots = 0;
            foreach (var s in scanners)
            {
                tracking += s.tracking;
                trackSlots += s.TrackSlots;
            }
            foreach (var m in magnets)
            {
                pulling += m.busy;
                pullSlots += m.CaptureSlots;
            }
            var ru = Localization.GetLocalizedString("MeteorMagnet_DisplayTitle") != "Meteor network";
            var lines = new List<string>
            {
                Localization.GetLocalizedString("MeteorMagnet_DisplayTitle"),
                (ru ? "Поймано: " : "Captured: ") + MeteorStats.Captured
                    + (ru ? "  Сорвалось: " : "  Slipped: ") + MeteorStats.Slipped
                    + (ru ? "  Отброшено: " : "  Thrown away: ") + MeteorStats.Deflected,
                (ru ? "Ресурсов собрано: " : "Resources collected: ") + MeteorStats.ResourceTotal,
                MeteorStats.Top(3),
                (ru ? "Сканеры: " : "Scanners: ") + scanners.Count + (ru ? ", ведут " : ", tracking ") + tracking + "/" + trackSlots,
                (ru ? "Захваты: " : "Magnets: ") + magnets.Count + (ru ? ", тянут " : ", pulling ") + pulling + "/" + pullSlots,
                (ru ? "Энергия: " : "Power: ") + (powerOk ? (ru ? "есть" : "ok") : (ru ? "НЕ ХВАТАЕТ" : "NOT ENOUGH"))
                    + (ru ? ", сеть " : ", network ") + NetworkDemand().ToString("0.#")
                    + (ru ? " кВт, свободно " : " kW, free ") + FreeText(),
                (ru ? "Ядер метеорита: " : "Meteor cores: ") + (MeteorStats.Resources.TryGetValue(Content.Core.id, out var cores) ? cores : 0),
                (ru ? "Охота за ачивками: на волосок от метеорита " : "Achievement hunt: close calls ") + MeteorStats.CloseCalls + (ru ? " раз" : ""),
            };
            MeteorDisplay.Update(lines);
        }

        readonly List<(Vector3 position, Color color)> compassTags = [];

        /// <summary>Compass in a scanner: the meteors it tracks are tagged on screen while they fall.</summary>
        void UpdateCompassTags()
        {
            compassTags.Clear();
            foreach (var kv in tracks)
            {
                if (kv.Key == null || Plugin.HasCrashed(kv.Key))
                {
                    continue;
                }
                var s = FindScanner(kv.Value.scanner);
                if (s != null && s.Gear(GearType.Compass) > 0)
                {
                    compassTags.Add((kv.Key.transform.position, s.tier.color));
                }
            }
            MeteorMarkers.SetLive(compassTags);
        }

        Scanner FindScanner(WorldObject wo)
        {
            foreach (var s in scanners)
            {
                if (s.wo == wo)
                {
                    return s;
                }
            }
            return null;
        }

        /// <summary>Whether the meteor passes the scanner's resource filter.</summary>
        static bool PassesFilter(Scanner s, Asteroid asteroid)
        {
            if (s.filter.Count == 0)
            {
                return true;
            }
            foreach (var gd in asteroid.groupsSelected ?? [])
            {
                if (gd != null && s.filter.Contains(gd.id))
                {
                    return true;
                }
            }
            return false;
        }

        static bool CanSee(Scanner s, Asteroid asteroid, Vector3 landing, bool barren, bool rare)
        {
            if (!powerOk && !s.Autonomous)
            {
                return false;
            }
            if (!PassesFilter(s, asteroid))
            {
                return false;
            }
            if (barren && !SeesBarren(s, asteroid))
            {
                return false;
            }
            if (rare && !s.tier.seesRare)
            {
                return false;
            }
            if (s.network.Count == 0)
            {
                return false;
            }
            var d = s.position - landing;
            d.y = 0;
            return d.magnitude <= Radius(s, asteroid);
        }

        /// <summary>Prime resonator; at half strength it sees every other barren meteor.</summary>
        static bool SeesBarren(Scanner s, Asteroid asteroid)
        {
            return s.effects.Contains(ResonatorEffect.CaptureBarren) && (!s.Weakened || Coin(asteroid));
        }

        /// <summary>Scan radius for this meteor; a weakened whole-planet scanner sees every other meteor beyond its own radius.</summary>
        static float Radius(Scanner s, Asteroid asteroid)
        {
            return s.planetWide && s.Weakened && !Coin(asteroid) ? s.BaseRadius : s.ScanRadius;
        }

        Scanner PickScanner(Asteroid asteroid, Vector3 landing, bool barren, bool rare)
        {
            Scanner best = null;
            var bestDist = float.MaxValue;
            foreach (var s in scanners)
            {
                if (s.tracking >= s.TrackSlots || !CanSee(s, asteroid, landing, barren, rare))
                {
                    continue;
                }
                var dist = Vector3.Distance(s.position, landing);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = s;
                }
            }
            return best;
        }

        /// <summary>
        /// The magnet in the scanner's link area with a free slot, nearest to where the meteor would land.
        /// A magnet with full storage still takes meteors; the overflow is dropped next to it.
        /// </summary>
        static Magnet PickMagnet(Scanner scanner, Asteroid asteroid, Vector3 landing, out string why)
        {
            Magnet best = null;
            var bestPriority = int.MaxValue;
            var bestRank = int.MaxValue;
            var bestDist = float.MaxValue;
            foreach (var m in scanner.network)
            {
                if (m.busy >= m.CaptureSlots || (!powerOk && !m.Autonomous))
                {
                    continue;
                }
                // Dominant / recessive modules come before the filter and the distance.
                var priority = Priority(m, asteroid);
                var rank = Plugin.RouteByFilter ? FilterRank(m, asteroid) : 0;
                var dist = Vector3.Distance(m.position, landing);
                if (priority < bestPriority || (priority == bestPriority && (rank < bestRank || (rank == bestRank && dist < bestDist))))
                {
                    bestPriority = priority;
                    bestRank = rank;
                    bestDist = dist;
                    best = m;
                }
            }
            why = bestRank switch
            {
                0 when Plugin.RouteByFilter => "its filter wants this meteor",
                1 => "no filter, stores everything",
                2 => "nearest free one, its filter does not want this meteor",
                _ => "nearest free one",
            } + (bestPriority == 0 ? ", DOMINANT" : bestPriority == 2 ? ", recessive (no other magnet was free)" : "");
            return best;
        }

        /// <summary>
        /// 0: the magnet's filter wants a resource of the meteor; 1: no filter (or no storage, a Mk1 pile);
        /// 2: the filter wants none of it, the loot would be dropped next to the magnet.
        /// </summary>
        /// <summary>
        /// 0: dominant (gets meteors first), 1: normal, 2: recessive (gets them last). Both modules cancel out;
        /// at half strength the module works on every other meteor.
        /// </summary>
        static int Priority(Magnet m, Asteroid asteroid)
        {
            var dominant = m.effects.Contains(ResonatorEffect.Dominant);
            var recessive = m.effects.Contains(ResonatorEffect.Recessive);
            if (dominant == recessive || (m.Weakened && !Coin(asteroid)))
            {
                return 1;
            }
            return dominant ? 0 : 2;
        }

        static int FilterRank(Magnet m, Asteroid asteroid)
        {
            if (!m.tier.hasStorage || m.filter.Count == 0)
            {
                return 1;
            }
            foreach (var gd in asteroid.groupsSelected ?? [])
            {
                if (gd != null && m.filter.Contains(gd.id))
                {
                    return 0;
                }
            }
            return 2;
        }

        /// <summary>
        /// Current load: meteors each scanner is tracking and each magnet is pulling in.
        /// Limited slots are what keep a rain of small meteors from being caught whole.
        /// </summary>
        void CountLoad()
        {
            var scannerByWo = new Dictionary<WorldObject, Scanner>();
            foreach (var s in scanners)
            {
                scannerByWo[s.wo] = s;
            }
            foreach (var kv in tracks)
            {
                if (kv.Key != null && scannerByWo.TryGetValue(kv.Value.scanner, out var s))
                {
                    s.tracking++;
                }
            }
            var magnetByWo = new Dictionary<WorldObject, Magnet>();
            foreach (var m in magnets)
            {
                magnetByWo[m.wo] = m;
            }
            foreach (var kv in captures)
            {
                if (kv.Key != null && !kv.Value.deflected && !Plugin.HasCrashed(kv.Key)
                    && magnetByWo.TryGetValue(kv.Value.magnet.wo, out var m))
                {
                    m.busy++;
                }
            }
        }

        /// <summary>Share of the game's acceleration a pulled meteor keeps; the jetpack raises it.</summary>
        static float CapturedAcceleration(Magnet m)
        {
            return Mathf.Min(1f, Content.CapturedAccelerationFactor + PlayerGear.JetpackAccelerationPerTier * m.Gear(GearType.Jetpack));
        }

        /// <summary>
        /// Simulation bonus: every working kind of player equipment (through the equipment simulation) raises
        /// the meteor core chance, every working kind of rover upgrade (through the rover simulation) lowers the
        /// fail chance, by 0.5 points each, at the building's strength.
        /// </summary>
        static float GearBonus(Building b) => b == null ? 0f : b.gearItems * Content.SimulationBonusPerItem * b.Strength;

        static float RoverBonus(Building b) => b == null ? 0f : b.roverItems * Content.SimulationBonusPerItem * b.Strength;

        /// <summary>Meteor core chance: base, deconstruction chip, and the equipment bonus of the magnet and the scanner that found the meteor.</summary>
        static float CoreChance(Magnet m, Scanner s = null)
        {
            return Mathf.Max(0f, Plugin.CoreDropChance + PlayerGear.DeconstructCorePerTier * m.Gear(GearType.DeconstructChip)
                + GearBonus(m) + GearBonus(s));
        }

        /// <summary>Module slots on top of the building's own: the exoskeleton, then x2 for the emergency expansion.</summary>
        static int ExtraModuleSlots(Building b, int baseSlots)
        {
            var total = baseSlots + b.Gear(GearType.Exoskeleton);
            if (b.Weakened)
            {
                total *= 2;
            }
            return total - baseSlots;
        }

        /// <summary>
        /// Half-strength on/off effects: a meteor gets it or not, decided once per meteor (the same on every scan).
        /// </summary>
        static bool Coin(Asteroid a) => ((uint)a.GetInstanceID() * 2654435761u >> 16 & 1u) == 0u;

        /// <summary>Fail chance: tier, rare bonus, stabilizer, then the rover bonus of the magnet and the scanner that found the meteor.</summary>
        // Blueprints being decoded (their removal from the slot is asynchronous), so each is decoded once.
        static readonly HashSet<int> decoding = [];

        /// <summary>
        /// A meteor blueprint in a module slot: removed, and the recipe of a random locked module opens
        /// (for everybody, saved with the world). With nothing left to open it turns into a meteor core.
        /// </summary>
        static string DecodeBlueprint(WorldObject item, Inventory inv)
        {
            if (!decoding.Add(item.GetId()))
            {
                return "being decoded";
            }
            // Beacon modules only once the beacon itself can be built, so a blueprint never opens a module for nothing.
            var beacon = GroupsHandler.GetGroupViaId(Content.Beacon.id);
            var beaconOpen = beacon != null && (UnlockedGroupsHandler.Instance.IsGloballyUnlocked(beacon) || beacon.GetUnlockingInfos().GetIsUnlocked());
            var locked = new List<Group>();
            foreach (var r in Content.Resonators)
            {
                var g = r.locked && (beaconOpen || !r.forBeacon) ? GroupsHandler.GetGroupViaId(r.id) : null;
                if (g != null && !UnlockedGroupsHandler.Instance.IsGloballyUnlocked(g))
                {
                    locked.Add(g);
                }
            }
            InventoriesHandler.Instance.RemoveItemFromInventory(item, inv, true, null);
            if (locked.Count == 0)
            {
                var core = GroupsHandler.GetGroupViaId(Content.Core.id);
                if (core != null)
                {
                    InventoriesHandler.Instance.AddItemToInventory(core, inv, (success, id) =>
                    {
                        if (!success)
                        {
                            DestroyFailed(id);
                            WorldObjectsHandler.Instance.CreateAndDropOnFloor(core, Managers.GetManager<PlayersManager>()
                                .GetActivePlayerController().transform.position + Vector3.up);
                        }
                    });
                }
                Plugin.LogInfo("Meteor blueprint decoded: every module is already open, turned into a meteor core");
                return "decoded, nothing left to open -> meteor core";
            }
            var pick = locked[UnityEngine.Random.Range(0, locked.Count)];
            UnlockedGroupsHandler.Instance.UnlockGroupGlobally(pick);
            Plugin.LogInfo("Meteor blueprint decoded: " + pick.GetId() + " is open (" + (locked.Count - 1) + " locked modules left)");
            try
            {
                Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer()?.AddInformation(5f,
                    Localization.GetLocalizedString("MeteorMagnet_BlueprintDecoded") + Readable.GetGroupName(pick),
                    DataConfig.UiInformationsType.InInventory, pick.GetImage());
            }
            catch (Exception ex)
            {
                Plugin.LogWarning("Blueprint message failed: " + ex.Message);
            }
            return "decoded -> " + pick.GetId();
        }

        static Vector3 PlayerPosition()
        {
            var pc = Managers.GetManager<PlayersManager>()?.GetActivePlayerController();
            return pc != null ? pc.transform.position : Vector3.zero;
        }

        /// <summary>
        /// Achievement hunter: a meteor that lands close to the player counts as a close call. Like the hunt for
        /// Halliday's egg in "Ready Player One": the copper, jade and crystal keys, then the egg itself.
        /// </summary>
        static void HunterImpact(Vector3 impact)
        {
            var dist = Vector3.Distance(impact, PlayerPosition());
            Plugin.Log("Achievement hunter meteor hit " + dist.ToString("0") + " m from the player");
            if (dist > Content.HunterCloseCallDistance)
            {
                return;
            }
            MeteorStats.AddCloseCall();
            var n = MeteorStats.CloseCalls;
            var key = n == Content.HunterKeys[0] ? "MeteorMagnet_CopperKey"
                : n == Content.HunterKeys[1] ? "MeteorMagnet_JadeKey"
                : n == Content.HunterKeys[2] ? "MeteorMagnet_CrystalKey"
                : n == Content.HunterKeys[3] ? "MeteorMagnet_HallidayEgg"
                : null;
            Plugin.LogInfo("Close call #" + n + (key != null ? ": " + key : ""));
            if (key != null)
            {
                try
                {
                    Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer()
                        ?.AddInformation(6f, Localization.GetLocalizedString(key), DataConfig.UiInformationsType.InInventory, null);
                }
                catch (Exception ex)
                {
                    Plugin.LogWarning("Key message failed: " + ex.Message);
                }
            }
        }

        static float FailChance(Magnet m, bool rare, Scanner s = null)
        {
            var chance = m.tier.failChance + (rare ? Content.RareFailBonus : 0f);
            if (m.effects.Contains(ResonatorEffect.Stabilizer))
            {
                chance *= m.Weaken(Content.StabilizerFailFactor);
            }
            if (m.effects.Contains(ResonatorEffect.Destroyer))
            {
                // The meteor is crushed in the field instead of held: it never slips (at half strength: half the chance).
                chance *= 1f - m.Strength;
            }
            return Mathf.Max(0f, chance - RoverBonus(m) - RoverBonus(s));
        }

        static bool IsRare(Asteroid asteroid)
        {
            foreach (var gd in asteroid.groupsSelected ?? [])
            {
                if (gd != null && Content.RareResources.Contains(gd.id))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Bad failure: the field kicks the meteor off sideways or back, far away from the magnet.
        /// It keeps its vanilla resources and impact, just somewhere else.
        /// </summary>
        void Deflect(Asteroid asteroid, Magnet magnet, Scanner scanner)
        {
            var heading = asteroid.transform.forward;
            heading.y = 0;
            if (heading.sqrMagnitude < 0.01f)
            {
                heading = Vector3.forward;
            }
            // 120..240 degrees off the original course: anywhere but where it was going.
            var angle = UnityEngine.Random.Range(120f, 240f);
            var dir = Quaternion.AngleAxis(angle, Vector3.up) * heading.normalized;
            var dist = UnityEngine.Random.Range(Content.DeflectMinDistance, Content.DeflectMaxDistance);
            var originalAcceleration = Plugin.LimitAcceleration(asteroid, Content.CapturedAccelerationFactor);

            captures[asteroid] = new Capture
            {
                originalAcceleration = originalAcceleration,
                magnet = magnet,
                scanner = scanner,
                target = GroundPoint(magnet.position + dir * dist),
                loot = [],
                capturedAt = Time.time,
                delivered = true,
                deflected = true,
            };
            Plugin.Log("Capture failed badly, " + Describe(asteroid) + " thrown " + dist.ToString("0") + " m away from " + Describe(magnet)
                + " (turned " + angle.ToString("0") + " degrees), new target " + captures[asteroid].target + ", keeps its vanilla loot");
            NotifyText("MeteorMagnet_Deflected", DataConfig.UiInformationsType.OutInventory);
            MeteorStats.AddDeflected();
            CaptureEffects.Flash(asteroid.transform.position, Color.red, 8f, 80f);
            CaptureEffects.Trail(asteroid.gameObject, Color.red);
        }

        /// <summary>
        /// First ground (not a meteor) between two points of a meteor's path.
        /// </summary>
        static bool TryGroundBetween(Vector3 from, Vector3 to, out Vector3 point)
        {
            point = default;
            var d = to - from;
            var len = d.magnitude;
            if (len < 0.01f)
            {
                return false;
            }
            var best = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(from, d / len, len, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.GetComponentInParent<Asteroid>() != null || hit.distance >= best)
                {
                    continue;
                }
                best = hit.distance;
                point = hit.point;
            }
            return best < float.MaxValue;
        }

        /// <summary>
        /// The terrain (or whatever stands there) right below/above a point, so a target never floats in the air
        /// or hides underground when the ground there is lower or higher than the magnet.
        /// </summary>
        static Vector3 GroundPoint(Vector3 p)
        {
            var from = p + Vector3.up * 500f;
            var best = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(from, Vector3.down, 1500f, ~0, QueryTriggerInteraction.Ignore))
            {
                // Skip meteors in the air.
                if (hit.collider.GetComponentInParent<Asteroid>() != null)
                {
                    continue;
                }
                if (hit.distance < best)
                {
                    best = hit.distance;
                }
            }
            return best < float.MaxValue ? from + Vector3.down * best : p;
        }

        Vector3 PredictLanding(Transform t)
        {
            var p = t.position;
            var f = t.forward;
            var bestHit = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(p, f, 5000f, ~0, QueryTriggerInteraction.Ignore))
            {
                // Skip the meteor's own colliders.
                if (hit.transform.IsChildOf(t) || hit.distance >= bestHit)
                {
                    continue;
                }
                bestHit = hit.distance;
            }
            if (bestHit < float.MaxValue)
            {
                return p + f * bestHit;
            }
            // No terrain hit (not loaded yet): intersect with the horizontal plane of the magnets.
            var groundY = 0f;
            foreach (var m in magnets)
            {
                groundY += m.position.y;
            }
            groundY /= magnets.Count;
            if (f.y < -0.01f && p.y > groundY)
            {
                return p + f * ((p.y - groundY) / -f.y);
            }
            return p;
        }

        static List<Group> RollLoot(Asteroid asteroid, Magnet magnet)
        {
            // numberOfResourceInDebris is how many of the debris pieces are resources. In-game values:
            // basic ores 4, Uranium 5, Osmium 5, Iridium 6, Super Alloy 1.
            var n = asteroid.numberOfResourceInDebris;
            // Vanilla drops vary around this number (Iridium with 6 gives "up to 10"): roll 50%..150% of it.
            var count = n >= 1f
                ? UnityEngine.Random.Range(Mathf.CeilToInt(n * 0.5f), Mathf.FloorToInt(n * 1.5f) + 1)
                : n;
            Plugin.Log("Meteor debris=" + asteroid.debrisNumber + ", resourceInDebris=" + n
                + ", debrisSize=" + asteroid.debrisSize + ", scale=" + asteroid.transform.localScale.x.ToString("0.00")
                + ", resources=" + string.Join("/", asteroid.groupsSelected.ConvertAll(g => g != null ? g.id : "?")));

            var candidates = new List<Group>();
            foreach (var gd in asteroid.groupsSelected)
            {
                AddGroup(candidates, gd?.id);
            }
            return Roll(candidates, count, magnet);
        }

        static List<Group> RollBarrenLoot(Asteroid asteroid, Magnet magnet)
        {
            var candidates = new List<Group>();
            foreach (var id in Content.BarrenLoot)
            {
                AddGroup(candidates, id);
            }
            // Barren meteors carry no resources of their own: one piece of ore each.
            return Roll(candidates, 1f, magnet);
        }

        static void AddGroup(List<Group> list, string id)
        {
            var g = id != null ? GroupsHandler.GetGroupViaId(id) : null;
            if (g != null)
            {
                list.Add(g);
            }
        }

        static List<Group> Roll(List<Group> candidates, float baseCount, Magnet magnet)
        {
            var result = new List<Group>();
            if (candidates.Count == 0)
            {
                return result;
            }
            var mult = Plugin.LootMultiplier;
            if (magnet.effects.Contains(ResonatorEffect.LootBonus))
            {
                mult *= magnet.Weaken(Content.LootBonusFactor);
            }
            if (magnet.effects.Contains(ResonatorEffect.Destroyer))
            {
                mult *= magnet.Weaken(Content.DestroyerLootFactor);
            }
            mult *= 1f + PlayerGear.MiningLootPerTier * magnet.Gear(GearType.MiningChip);
            var total = Mathf.Max(Plugin.MinLootPerMeteor, Mathf.RoundToInt(baseCount * mult));
            var extraTypes = 0;
            var extraDuplicates = 0;
            var duplicates = magnet.effects.Contains(ResonatorEffect.DuplicateChance);

            var picks = new List<Group>();
            if (magnet.effects.Contains(ResonatorEffect.EveryResourceType) && UnityEngine.Random.value < magnet.Strength)
            {
                // One of each distinct type first, the rest is random as usual.
                var distinct = new HashSet<string>();
                foreach (var g in candidates)
                {
                    if (distinct.Add(g.GetId()))
                    {
                        picks.Add(g);
                    }
                }
                extraTypes = Mathf.Max(0, picks.Count - total);
            }
            while (picks.Count < total)
            {
                picks.Add(candidates[UnityEngine.Random.Range(0, candidates.Count)]);
            }

            foreach (var g in picks)
            {
                result.Add(g);
                if (duplicates && UnityEngine.Random.value < Content.DuplicateChance * magnet.Strength)
                {
                    result.Add(g);
                    extraDuplicates++;
                }
            }
            Plugin.Log("Loot roll for #" + magnet.wo.GetId() + ": base " + baseCount.ToString("0.#") + " x" + mult.ToString("0.##")
                + (magnet.effects.Contains(ResonatorEffect.LootBonus) ? " (Humble x" + magnet.Weaken(Content.LootBonusFactor).ToString("0.##") + ")" : "") + " = " + total
                + (extraTypes > 0 ? ", +" + extraTypes + " for every type (Skeo)" : "")
                + (extraDuplicates > 0 ? ", +" + extraDuplicates + " duplicated (Toxicity)" : "")
                + " -> " + result.Count + " items");
            return result;
        }

        void SteerCaptured()
        {
            var dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }
            foreach (var kv in captures)
            {
                var c = kv.Value;
                var asteroid = kv.Key;
                if (asteroid == null || Plugin.HasCrashed(asteroid))
                {
                    continue;
                }
                var t = asteroid.transform;
                var pos = t.position;
                if (c.hunting && !c.released)
                {
                    // Follows the player until the final dive.
                    c.target = PlayerPosition();
                }
                var dir = c.target - pos;
                var dist = dir.magnitude;
                var prev = c.lastPosition;
                var speed = prev.HasValue ? (pos - prev.Value).magnitude / dt : 0f;
                c.lastPosition = pos;

                // Safety net: a meteor that hangs in the air gets the game's own acceleration back.
                if (prev.HasValue && !c.accelerationRestored)
                {
                    if (speed >= Content.StallSpeed)
                    {
                        c.stalledSince = 0f;
                    }
                    else if (c.stalledSince == 0f)
                    {
                        c.stalledSince = Time.time;
                    }
                    else if (Time.time - c.stalledSince > Content.StallTime)
                    {
                        c.accelerationRestored = true;
                        Plugin.SetAcceleration(asteroid, c.originalAcceleration);
                        Plugin.Log("Captured meteor stalled at " + pos + " (speed " + speed.ToString("0.0")
                            + " m/s), maxAcceleration restored to " + c.originalAcceleration);
                    }
                }

                // Too fast for the game's own impact check: it went through the ground this frame and will only
                // crash deep below. Show the impact where it hit the surface and hide the meteor from then on.
                if (prev.HasValue && !c.unTunneled && TryGroundBetween(prev.Value, pos, out var surface))
                {
                    c.unTunneled = true;
                    CaptureEffects.Flash(surface + Vector3.up * 2f, c.deflected ? Color.red : c.magnet.tier.color, 8f, 40f);
                    if (c.hunting)
                    {
                        HunterImpact(surface);
                    }
                    var hidden = 0;
                    foreach (var r in asteroid.GetComponentsInChildren<Renderer>())
                    {
                        if (r.enabled)
                        {
                            r.enabled = false;
                            hidden++;
                        }
                    }
                    Plugin.Log("Meteor hit the ground at " + surface + " (speed " + speed.ToString("0")
                        + " m/s): impact shown there, meteor hidden (" + hidden + " renderers) until the game registers the crash");
                    if (!loggedAsteroidAnatomy)
                    {
                        loggedAsteroidAnatomy = true;
                        LogAsteroidAnatomy(asteroid);
                    }
                    continue;
                }
                if (dist < 1f)
                {
                    continue;
                }

                if (c.released)
                {
                    continue;
                }
                var wanted = Quaternion.LookRotation(dir / dist);
                // Close to the target there is no room left to curve in: aim once into the ground just below
                // the target and stop steering, so the meteor dives in instead of circling or bouncing around it.
                if (dist < Content.SnapDistance)
                {
                    t.rotation = Quaternion.LookRotation((c.target + Vector3.down * Content.DiveDepth - pos).normalized);
                    c.released = true;
                    Plugin.Log("Meteor dives in: " + dist.ToString("0") + " m from target " + c.target + ", speed " + speed.ToString("0") + " m/s"
                        + (c.deflected ? " (thrown away)" : ""));
                }
                else
                {
                    var factor = c.deflected
                        ? Content.DeflectTurnFactor
                        : 1f + RoverUpgrades.SpeedTurnRatePerTier * c.magnet.Upgrade(RoverUpgradeType.Speed);
                    var baseRate = Plugin.PullTurnRate * factor;
                    // Turn fast enough to close the remaining angle in half the time left to reach the target.
                    var angle = Quaternion.Angle(t.rotation, wanted);
                    var needed = speed > 0f ? 2f * angle * speed / dist : 0f;
                    t.rotation = Quaternion.RotateTowards(t.rotation, wanted, Mathf.Max(baseRate, needed) * dt);
                }

                if (asteroid.TryGetComponent<Rigidbody>(out var rb) && !rb.isKinematic)
                {
                    rb.velocity = t.forward * rb.velocity.magnitude;
                }
            }
        }

        static bool loggedAsteroidAnatomy;

        /// <summary>
        /// Diagnostics, once per session: the meteor's fields, methods, physics components and colliders,
        /// to find out how the game detects the impact.
        /// </summary>
        static void LogAsteroidAnatomy(Asteroid asteroid)
        {
            try
            {
                var lines = new List<string>();
                const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
                foreach (var f in typeof(Asteroid).GetFields(all))
                {
                    object v;
                    try
                    {
                        v = f.GetValue(asteroid);
                    }
                    catch
                    {
                        v = "?";
                    }
                    lines.Add("  field " + f.FieldType.Name + " " + f.Name + " = " + v);
                }
                foreach (var m in typeof(Asteroid).GetMethods(all))
                {
                    lines.Add("  method " + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ",
                        Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name)) + ")");
                }
                foreach (var comp in asteroid.GetComponentsInChildren<Component>(true))
                {
                    var extra = comp is Rigidbody rb ? " kinematic=" + rb.isKinematic + " velocity=" + rb.velocity + " detection=" + rb.collisionDetectionMode
                        : comp is Collider col ? " trigger=" + col.isTrigger + " enabled=" + col.enabled
                        : "";
                    lines.Add("  component " + comp.GetType().Name + " on " + comp.gameObject.name + extra);
                }
                Plugin.Log("Asteroid anatomy (base type " + typeof(Asteroid).BaseType?.Name + "):\n" + string.Join("\n", lines));
            }
            catch (Exception ex)
            {
                Plugin.LogWarning("Asteroid anatomy failed: " + ex.Message);
            }
        }

        void CheckLanded()
        {
            if (captures.Count == 0)
            {
                return;
            }
            done.Clear();
            foreach (var kv in captures)
            {
                var asteroid = kv.Key;
                var gone = asteroid == null;
                var crashed = !gone && Plugin.HasCrashed(asteroid);
                var timedOut = !gone && !crashed && Time.time - kv.Value.capturedAt > CaptureTimeout;
                if (gone || crashed || timedOut)
                {
                    Plugin.Log("Captured " + (gone ? "meteor" : Describe(asteroid)) + " of " + Describe(kv.Value.magnet) + " " + (crashed ? "crashed" : gone ? "disappeared" : "timed out (never hit the ground)")
                        + " after " + (Time.time - kv.Value.capturedAt).ToString("0.0") + " s"
                        + (gone ? "" : " at " + asteroid.transform.position) + (kv.Value.deflected ? ", thrown away" : ""));
                    done.Add(asteroid);
                    if (kv.Value.deflected && !gone)
                    {
                        MeteorMarkers.Add(asteroid.transform.position, new Color(1f, 0.3f, 0.2f), "thrown-away " + Describe(asteroid) + " landed");
                    }
                    if (crashed && kv.Value.hunting && !kv.Value.unTunneled)
                    {
                        HunterImpact(asteroid.transform.position);
                    }
                    if (crashed)
                    {
                        CaptureEffects.Flash(asteroid.transform.position + Vector3.up * 2f,
                            kv.Value.deflected ? Color.red : kv.Value.magnet.tier.color, 6f, 30f);
                    }
                    if (!kv.Value.delivered)
                    {
                        Deliver(kv.Value);
                    }
                }
            }
            foreach (var a in done)
            {
                captures.Remove(a);
            }
        }

        static void Deliver(Capture c)
        {
            Inventory inv = null;
            if (c.magnet.tier.hasStorage)
            {
                // Re-fetch: the magnet may have been deconstructed while the meteor was falling.
                inv = InventoriesHandler.Instance.GetInventoryById(c.magnet.wo.GetLinkedInventoryId());
            }
            var forward = c.magnet.Upgrade(RoverUpgradeType.Logistics) > 0
                ? FindNearbyContainers(c.magnet)
                : null;

            int stored = 0, dropped = 0, filteredOut = 0, full = 0, overflowed = 0;
            if (c.magnet.tier.hasStorage && inv == null)
            {
                Plugin.Log("Magnet #" + c.magnet.wo.GetId() + " storage inventory not found, everything goes to the pile");
            }
            // Pulsar transmitter: what passes the filter goes straight to the storage network while it has room and power.
            // At half strength (emergency expansion) only every other meteor's loot is sent.
            var net = c.magnet.effects.Contains(ResonatorEffect.StorageLink) && powerOk
                && (!c.magnet.Weakened || UnityEngine.Random.value < c.magnet.Strength) ? StorageNetwork.NetOf(c.magnet.wo) : null;
            var planned = new Dictionary<NetworkDisk, (int count, HashSet<string> kinds)>();
            var networked = 0;
            foreach (var g in c.loot)
            {
                // The meteor core is always kept, whatever the filter says.
                var passes = c.magnet.filter.Count == 0 || c.magnet.filter.Contains(g.GetId()) || g.GetId() == Content.Core.id || g.GetId() == Content.Blueprint.id;
                if (net != null && passes && SendToNetwork(net, g, planned))
                {
                    networked++;
                    continue;
                }
                var isFull = inv != null && inv.IsFull();
                if (inv != null && !passes)
                {
                    filteredOut++;
                }
                else if (isFull)
                {
                    full++;
                }
                var toStorage = inv != null && passes && !isFull;
                if (toStorage)
                {
                    stored++;
                    var group = g;
                    InventoriesHandler.Instance.AddItemToInventory(group, inv, (success, id) =>
                    {
                        if (!success)
                        {
                            // Storage full: do not lose the item.
                            DestroyFailed(id);
                            Overflow(group, forward, c.pile);
                        }
                    });
                }
                else if (TryNetworkOverflow(c, g, forward))
                {
                    overflowed++;
                }
                else if (Overflow(g, forward, c.pile))
                {
                    stored++;
                }
                else
                {
                    dropped++;
                }
            }

            if (dropped != 0 && c.magnet.LightTier > 0)
            {
                MarkPile(c.pile, c.magnet.LightTier);
            }

            MeteorStats.AddResources(c.loot);
            Plugin.Log("Delivered " + c.loot.Count + " items from magnet #" + c.magnet.wo.GetId() + ": " + stored + " stored/forwarded, "
                + dropped + " dropped at " + c.pile + (networked > 0 ? ", " + networked + " sent to the storage network" : "")
                + (c.magnet.effects.Contains(ResonatorEffect.StorageLink) && net == null ? ", storage network unreachable (no network, channel or power)" : "")
                + (overflowed > 0 ? ", " + overflowed + " overflowed to other magnets (Aqualis)" : "")
                + (inv != null ? " (storage " + inv.GetInsideWorldObjects().Count + "/" + inv.GetSize() + ", filtered out " + filteredOut
                    + ", storage full " + full + ", filter: " + (c.magnet.filter.Count == 0 ? "none" : string.Join("/", c.magnet.filter)) + ")" : ""));
            if (Plugin.ShowNotification && c.loot.Count != 0)
            {
                Notify(c.loot[0], stored, dropped);
            }
        }

        /// <summary>
        /// Lights upgrade: a tall light over the loot pile for a while, so it is easy to spot.
        /// </summary>
        static void MarkPile(Vector3 pile, int tier)
        {
            var go = new GameObject("MeteorMagnet_PileMarker");
            go.transform.position = pile + Vector3.up * 4f;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.85f, 0.3f);
            light.range = 10f + 5f * tier;
            light.intensity = 4f + 2f * tier;
            Destroy(go, 120f * tier);
        }

        /// <summary>
        /// Puts an item that does not go into the magnet's own storage into a nearby container
        /// (Vehicle - Logistics) or on the pile. Returns true if it went to a container.
        /// </summary>
        /// <summary>
        /// Phosphorus overflow (Aqualis): what the magnet cannot keep flows to another magnet of the same network
        /// whose filter wants it (first) or that stores everything, if it has room. At half strength: every other item.
        /// </summary>
        static bool TryNetworkOverflow(Capture c, Group g, List<Inventory> forward)
        {
            if (!c.magnet.effects.Contains(ResonatorEffect.Overflow) || c.scanner == null
                || (c.magnet.Weakened && UnityEngine.Random.value >= c.magnet.Strength))
            {
                return false;
            }
            Magnet best = null;
            Inventory bestInv = null;
            var bestRank = int.MaxValue;
            foreach (var m in c.scanner.network)
            {
                if (m == c.magnet || !m.tier.hasStorage)
                {
                    continue;
                }
                var rank = m.filter.Count == 0 ? 1 : m.filter.Contains(g.GetId()) ? 0 : -1;
                if (rank < 0 || rank >= bestRank)
                {
                    continue;
                }
                var inv = InventoriesHandler.Instance.GetInventoryById(m.wo.GetLinkedInventoryId());
                if (inv == null || inv.IsFull())
                {
                    continue;
                }
                best = m;
                bestInv = inv;
                bestRank = rank;
            }
            if (best == null)
            {
                return false;
            }
            InventoriesHandler.Instance.AddItemToInventory(g, bestInv, (success, id) =>
            {
                if (!success)
                {
                    DestroyFailed(id);
                    Overflow(g, forward, c.pile);
                }
            });
            Plugin.Log("Overflow: " + g.GetId() + " from " + Describe(c.magnet) + " to " + Describe(best) + (bestRank == 0 ? " (its filter wants it)" : " (stores everything)"));
            return true;
        }

        /// <summary>Puts one item on a disk of the network; false if no disk has room for it.</summary>
        static bool SendToNetwork(StorageNet net, Group g, Dictionary<NetworkDisk, (int count, HashSet<string> kinds)> planned)
        {
            var disk = StorageNetwork.DiskFor(net, g.GetId(), planned);
            if (disk == null)
            {
                return false;
            }
            var p = planned[disk];
            p.kinds.Add(g.GetId());
            planned[disk] = (p.count + 1, p.kinds);
            var target = disk.inventory;
            InventoriesHandler.Instance.AddItemToInventory(g, target, (success, id) =>
            {
                if (!success)
                {
                    DestroyFailed(id);
                    Plugin.LogWarning("Storage network: could not add " + g.GetId() + " to a disk, item lost");
                }
            });
            return true;
        }

        const int FlushPerRefresh = 10;

        /// <summary>
        /// Linked magnets send what waits in their own storage (the buffer) to the network, a few items per refresh,
        /// as soon as the network is reachable, powered and has room.
        /// </summary>
        void FlushBuffers()
        {
            if (!powerOk)
            {
                return;
            }
            foreach (var m in magnets)
            {
                if (m.inventory == null || !m.effects.Contains(ResonatorEffect.StorageLink))
                {
                    continue;
                }
                var net = StorageNetwork.NetOf(m.wo);
                if (net == null)
                {
                    continue;
                }
                var planned = new Dictionary<NetworkDisk, (int count, HashSet<string> kinds)>();
                var sent = 0;
                foreach (var item in new List<WorldObject>(m.inventory.GetInsideWorldObjects()))
                {
                    if (sent >= FlushPerRefresh)
                    {
                        break;
                    }
                    var g = item?.GetGroup();
                    if (g == null || item.GetLinkedInventoryId() > 0 || !string.IsNullOrEmpty(item.GetText()))
                    {
                        continue;
                    }
                    var disk = StorageNetwork.DiskFor(net, g.GetId(), planned);
                    if (disk == null)
                    {
                        continue;
                    }
                    var p = planned[disk];
                    p.kinds.Add(g.GetId());
                    planned[disk] = (p.count + 1, p.kinds);
                    var source = m.inventory;
                    var target = disk.inventory;
                    InventoriesHandler.Instance.RemoveItemFromInventory(item, source, true, null);
                    InventoriesHandler.Instance.AddItemToInventory(g, target, (success, id) =>
                    {
                        if (!success)
                        {
                            DestroyFailed(id);
                            InventoriesHandler.Instance.AddItemToInventory(g, source, null);
                        }
                    });
                    sent++;
                }
                if (sent > 0)
                {
                    Plugin.Log("Magnet #" + m.wo.GetId() + " sent " + sent + " buffered items to the storage network");
                }
            }
        }

        static bool Overflow(Group g, List<Inventory> forward, Vector3 pile)
        {
            if (forward != null)
            {
                foreach (var target in forward)
                {
                    if (target.IsFull())
                    {
                        continue;
                    }
                    InventoriesHandler.Instance.AddItemToInventory(g, target, (success, id) =>
                    {
                        if (!success)
                        {
                            DestroyFailed(id);
                            DropOnPile(g, pile);
                        }
                    });
                    return true;
                }
            }
            DropOnPile(g, pile);
            return false;
        }

        static void DestroyFailed(int id)
        {
            if (id != 0)
            {
                WorldObjectsHandler.Instance.DestroyWorldObject(id);
            }
        }

        /// <summary>
        /// Regular containers around the magnet, nearest first.
        /// </summary>
        static List<Inventory> FindNearbyContainers(Magnet magnet)
        {
            var found = new List<(float, Inventory)>();
            var planetHash = magnet.wo.GetPlanetHash();
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                var gid = wo?.GetGroup()?.GetId();
                if (gid == null || !gid.StartsWith("Container", StringComparison.Ordinal) || !wo.GetIsPlaced())
                {
                    continue;
                }
                if (wo.GetPlanetHash() != planetHash)
                {
                    continue;
                }
                var dist = Vector3.Distance(wo.GetPosition(), magnet.position);
                if (dist > Content.ForwardRadius)
                {
                    continue;
                }
                var inv = InventoriesHandler.Instance.GetInventoryById(wo.GetLinkedInventoryId());
                if (inv != null)
                {
                    found.Add((dist, inv));
                }
            }
            found.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var result = new List<Inventory>();
            foreach (var f in found)
            {
                result.Add(f.Item2);
            }
            return result;
        }

        static void DropOnPile(Group g, Vector3 pile)
        {
            var jitter = UnityEngine.Random.insideUnitCircle * 0.5f;
            WorldObjectsHandler.Instance.CreateAndDropOnFloor(g, pile + new Vector3(jitter.x, 0f, jitter.y));
        }

        static void NotifyText(string key, DataConfig.UiInformationsType type)
        {
            if (!Plugin.ShowNotification)
            {
                return;
            }
            try
            {
                Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer()
                    ?.AddInformation(3f, Localization.GetLocalizedString(key), type, null);
            }
            catch (Exception ex)
            {
                Plugin.Log("Notification failed: " + ex.Message);
            }
        }

        static void Notify(Group icon, int stored, int dropped)
        {
            try
            {
                var displayer = Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer();
                if (displayer == null)
                {
                    return;
                }
                var text = Localization.GetLocalizedString("MeteorMagnet_Captured") + ":";
                if (stored != 0)
                {
                    text += " +" + stored + " " + Localization.GetLocalizedString("MeteorMagnet_Stored");
                }
                if (dropped != 0)
                {
                    text += (stored != 0 ? "," : "") + " +" + dropped + " " + Localization.GetLocalizedString("MeteorMagnet_Dropped");
                }
                var type = stored != 0 ? DataConfig.UiInformationsType.InInventory : DataConfig.UiInformationsType.DropOnFloor;
                displayer.AddInformation(3f, text, type, icon.GetImage());
            }
            catch (Exception ex)
            {
                Plugin.Log("Notification failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Tints a placed mod building in its color and adds a glow so it does not look like a plain container.
        /// The Lights upgrade makes a magnet's glow brighter and wider.
        /// </summary>
        void Decorate(WorldObject wo, Color color, int level, int lights)
        {
            var go = wo.GetGameObject();
            if (go == null)
            {
                return;
            }
            var key = go.GetInstanceID();
            if (!glows.TryGetValue(key, out var light) || light == null)
            {
                foreach (var r in go.GetComponentsInChildren<MeshRenderer>())
                {
                    foreach (var m in r.materials)
                    {
                        if (m.HasProperty("_BaseColor"))
                        {
                            m.SetColor("_BaseColor", Color.Lerp(m.GetColor("_BaseColor"), color, 0.5f));
                        }
                        else if (m.HasProperty("_Color"))
                        {
                            m.color = Color.Lerp(m.color, color, 0.5f);
                        }
                    }
                }
                var lightGo = new GameObject("MeteorMagnet_Glow");
                lightGo.transform.SetParent(go.transform, false);
                lightGo.transform.localPosition = new Vector3(0, 2.5f, 0);
                light = lightGo.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = color;
                glows[key] = light;
            }
            light.range = 6f + 3f * level + 8f * lights;
            light.intensity = 2f + level + 3f * lights;
        }
    }
}
