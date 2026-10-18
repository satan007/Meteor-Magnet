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
    internal class MagnetController : MonoBehaviour
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
            internal float Draw => Plugin.EnergyUse(wo.GetGroup().GetId())
                * (effects.Contains(ResonatorEffect.PowerSaver) ? Content.PowerSaverFactor : 1f);

            internal int Upgrade(RoverUpgradeType t) => upgrades.TryGetValue(t, out var v) ? v : 0;

            /// <summary>The building's separate module slots (see <see cref="ModuleSlots"/>).</summary>
            internal Inventory moduleInventory;

            /// <summary>What each item in the module slots does here, for the debug log.</summary>
            internal readonly List<string> moduleReport = [];

            // Rover upgrades each building type makes use of.
            static readonly HashSet<RoverUpgradeType> scannerUpgrades = [RoverUpgradeType.Equipment, RoverUpgradeType.Beacon];
            static readonly HashSet<RoverUpgradeType> magnetUpgrades =
                [RoverUpgradeType.Storage, RoverUpgradeType.Speed, RoverUpgradeType.Lights, RoverUpgradeType.Logistics, RoverUpgradeType.Jetpack];

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
                    var res = Content.ResonatorOf(itemId);
                    if (res != null)
                    {
                        // A module only works in the building it is made for.
                        if (!res.forBoth && res.forScanner != isScanner)
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
                    moduleReport.Add((itemId ?? "?") + ": not a module");
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

            internal int CaptureSlots => tier.captureSlots + (effects.Contains(ResonatorEffect.ExtraCaptureSlot) ? 1 : 0);
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

            internal float ScanRadius => planetWide
                ? float.PositiveInfinity
                : tier.scanRadius * (effects.Contains(ResonatorEffect.RadiusBoost) ? Content.RadiusBoostFactor : 1f);

            internal float LinkFactor => Upgrade(RoverUpgradeType.Beacon) > 0 ? RoverUpgrades.BeaconLinkFactor : 1f;

            internal float LockTime => tier.lockTime
                * (effects.Contains(ResonatorEffect.FasterLock) ? Content.FasterLockFactor : 1f)
                * (planetWide && effects.Contains(ResonatorEffect.RadiusBoost) ? Content.LensLockFactor : 1f);

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
        float nextVortex;
        // Items already being moved by the vortex, so they are not added twice.
        readonly HashSet<int> vortexPending = [];

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
                ModuleSlots.Reset();
                loggedModules.Clear();
                signalLines.Clear();
                meteorLines.Clear();
                loggedUnseen.Clear();
                MeteorDisplay.Reset();
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

            if (Time.time >= nextVortex)
            {
                nextVortex = Time.time + Content.VortexInterval;
                RunVortex();
            }
        }

        /// <summary>
        /// Phosphorus Vortex: magnets with it pull loose meteor resources lying nearby into their storage.
        /// Only resources meteors can bring on this planet are touched, so tools and other dropped items stay put.
        /// </summary>
        void RunVortex()
        {
            List<Magnet> vortexMagnets = null;
            foreach (var m in magnets)
            {
                if (m.inventory != null && m.effects.Contains(ResonatorEffect.Vortex) && !m.inventory.IsFull())
                {
                    (vortexMagnets ??= []).Add(m);
                }
            }
            if (vortexMagnets == null)
            {
                return;
            }

            var resourceIds = PlanetMeteors.ResourceIds();
            foreach (var grabable in FindObjectsByType<ActionGrabable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (grabable == null || !grabable.GetCanGrab())
                {
                    continue;
                }
                var wo = grabable.GetComponentInParent<WorldObjectAssociated>()?.GetWorldObject();
                var gid = wo?.GetGroup()?.GetId();
                if (gid == null || !resourceIds.Contains(gid) || vortexPending.Contains(wo.GetId()))
                {
                    continue;
                }
                var pos = grabable.transform.position;
                foreach (var m in vortexMagnets)
                {
                    if (Vector3.Distance(pos, m.position) > Content.VortexRadius)
                    {
                        continue;
                    }
                    if (m.filter.Count != 0 && !m.filter.Contains(gid))
                    {
                        continue;
                    }
                    if (m.inventory.IsFull())
                    {
                        continue;
                    }
                    var id = wo.GetId();
                    vortexPending.Add(id);
                    InventoriesHandler.Instance.AddWorldObjectToInventory(wo, m.inventory, grabbed: true, success =>
                    {
                        vortexPending.Remove(id);
                        Plugin.Log("Vortex pulled " + gid + " #" + id + ": " + success);
                    });
                    break;
                }
            }
        }

        void RefreshBuildings()
        {
            magnets.Clear();
            scanners.Clear();
            relays.Clear();
            beacons.Clear();
            displayCount = 0;
            var planetHash = CurrentPlanetHash();
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                var gid = wo?.GetGroup()?.GetId();
                var tier = Content.TierOf(gid);
                var scannerTier = Content.ScannerOf(gid);
                var relay = Content.IsRelay(gid);
                if (gid == Content.Display.id || gid == Content.Beacon.id)
                {
                    if (wo.GetIsPlaced() && (planetHash == 0 || wo.GetPlanetHash() == planetHash))
                    {
                        if (gid == Content.Beacon.id)
                        {
                            beacons.Add(wo);
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
                    LogModules(magnet);
                    if (magnet.inventory != null)
                    {
                        ApplyStorageSize(magnet);
                    }
                    magnets.Add(magnet);
                    Decorate(wo, tier.color, tier.level, magnet.Upgrade(RoverUpgradeType.Lights));
                }
            }
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
                    + ", barren meteors " + (sc.effects.Contains(ResonatorEffect.CaptureBarren) ? "seen" : "not seen"));
                lines.Add("  => filter: " + (sc.filter.Count == 0 ? "none, tracks every meteor" : "only meteors with " + string.Join("/", sc.filter)));
            }
            else if (b is Magnet m)
            {
                var stabilized = m.effects.Contains(ResonatorEffect.Stabilizer);
                lines.Add("  => holds " + m.CaptureSlots + " meteors, fail chance " + (FailChance(m, false) * 100f).ToString("0") + "% ("
                    + (FailChance(m, true) * 100f).ToString("0") + "% for rare)" + (stabilized ? ", halved by the field stabilizer" : ""));
                var loot = new List<string>();
                if (m.effects.Contains(ResonatorEffect.LootBonus))
                {
                    loot.Add("x" + Content.LootBonusFactor + " amount");
                }
                if (m.effects.Contains(ResonatorEffect.DuplicateChance))
                {
                    loot.Add((Content.DuplicateChance * 100f).ToString("0") + "% duplicate per item");
                }
                if (m.effects.Contains(ResonatorEffect.EveryResourceType))
                {
                    loot.Add("at least one of each resource type");
                }
                if (m.effects.Contains(ResonatorEffect.Vortex))
                {
                    loot.Add("vortex pulls loose resources within " + Content.VortexRadius.ToString("0") + " m");
                }
                lines.Add("  => loot: " + (loot.Count == 0 ? "as the meteor carries" : string.Join(", ", loot)));
                lines.Add("  => pull turn speed x" + (1f + RoverUpgrades.SpeedTurnRatePerTier * m.Upgrade(RoverUpgradeType.Speed))
                    + ", storage +" + RoverUpgrades.StorageSlots(m.Upgrade(RoverUpgradeType.Storage)) + " slots"
                    + (m.Upgrade(RoverUpgradeType.Logistics) > 0 ? ", overflow to chests within " + Content.ForwardRadius.ToString("0") + " m" : "")
                    + (m.Upgrade(RoverUpgradeType.Jetpack) > 0 ? ", loot delivered at capture" : "")
                    + (m.Upgrade(RoverUpgradeType.Lights) > 0 ? ", pile light" : ""));
            }
            lines.Add("  => power draw " + b.Draw.ToString("0.#") + " kW"
                + (b.effects.Contains(ResonatorEffect.PowerSaver) ? " (power saver x" + Content.PowerSaverFactor + ")" : ""));
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
            var wanted = baseSize + RoverUpgrades.StorageSlots(magnet.Upgrade(RoverUpgradeType.Storage));
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

        bool powerOk = true;

        void UpdatePower()
        {
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
                tracks.Clear();
                return;
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

                var failChance = FailChance(magnet, rare);
                var failRoll = UnityEngine.Random.value;
                Plugin.Log(Describe(scanner) + " hands " + Describe(asteroid) + " to " + Describe(magnet)
                    + " (" + HorizontalDistance(magnet.position, landing).ToString("0") + " m from the predicted landing, slots "
                    + magnet.busy + "/" + magnet.CaptureSlots + ", picked: " + pickedBecause + "): fail chance " + (failChance * 100f).ToString("0") + "%"
                    + (rare ? " incl. rare +" + (Content.RareFailBonus * 100f).ToString("0") + "%" : "")
                    + (magnet.effects.Contains(ResonatorEffect.Stabilizer) ? " x" + Content.StabilizerFailFactor + " stabilizer" : "")
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
                if (core != null && loot.Count != 0 && coreRoll < Plugin.CoreDropChance)
                {
                    loot.Add(core);
                    NotifyText("MeteorMagnet_CoreFound", DataConfig.UiInformationsType.InInventory);
                }
                Plugin.Log("Meteor core roll " + (coreRoll * 100f).ToString("0.0") + " vs " + (Plugin.CoreDropChance * 100f).ToString("0.0")
                    + "% -> " + (core == null ? "core item missing" : coreRoll < Plugin.CoreDropChance ? "CORE FOUND" : "no core"));
                if (loot.Count == 0)
                {
                    magnet.busy--;
                    Plugin.Log(Describe(asteroid) + ": loot roll gave nothing, meteor left alone");
                    continue;
                }

                // Neutralize the impact: no resources on the ground, no damage.
                var originalAcceleration = Plugin.LimitAcceleration(asteroid);
                asteroid.numberOfResourceInDebris = 0;
                asteroid.doDamage = false;
                asteroid.shakeOnImpact = Plugin.ShakeOnImpact;
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
                    target = GroundPoint(magnet.position + away.normalized * Plugin.ImpactOffset),
                    pile = magnet.position + away.normalized * 3f + Vector3.up * 1.5f,
                    loot = loot,
                    capturedAt = Time.time,
                    originalAcceleration = originalAcceleration,
                };
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
                if (!PassesFilter(s, asteroid))
                {
                    why = "filtered out (looks for " + string.Join("/", s.filter) + ")";
                }
                else if (barren && !s.effects.Contains(ResonatorEffect.CaptureBarren))
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
                else if (HorizontalDistance(s.position, landing) > s.ScanRadius)
                {
                    why = "out of range " + HorizontalDistance(s.position, landing).ToString("0") + " > " + s.ScanRadius.ToString("0") + " m";
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
                nextShower = Time.time + Plugin.BeaconIntervalSeconds;
                Plugin.Log(beacons.Count + " meteor beacon(s): next shower in " + (Plugin.BeaconIntervalSeconds / 60f).ToString("0.#") + " min");
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
            if (MeteorBeacon.TrySummon(out var result))
            {
                nextShower = Time.time + Plugin.BeaconIntervalSeconds;
                NotifyText("MeteorMagnet_BeaconShower", DataConfig.UiInformationsType.OutInventory);
                Plugin.Log("Meteor beacon: " + result + ", next in " + (Plugin.BeaconIntervalSeconds / 60f).ToString("0.#") + " min");
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
            };
            MeteorDisplay.UpdateAll(lines);
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
            if (!PassesFilter(s, asteroid))
            {
                return false;
            }
            if (barren && !s.effects.Contains(ResonatorEffect.CaptureBarren))
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
            return d.magnitude <= s.ScanRadius;
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
            var bestRank = int.MaxValue;
            var bestDist = float.MaxValue;
            foreach (var m in scanner.network)
            {
                if (m.busy >= m.CaptureSlots)
                {
                    continue;
                }
                var rank = Plugin.RouteByFilter ? FilterRank(m, asteroid) : 0;
                var dist = Vector3.Distance(m.position, landing);
                if (rank < bestRank || (rank == bestRank && dist < bestDist))
                {
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
            };
            return best;
        }

        /// <summary>
        /// 0: the magnet's filter wants a resource of the meteor; 1: no filter (or no storage, a Mk1 pile);
        /// 2: the filter wants none of it, the loot would be dropped next to the magnet.
        /// </summary>
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

        static float FailChance(Magnet m, bool rare)
        {
            var chance = m.tier.failChance + (rare ? Content.RareFailBonus : 0f);
            return m.effects.Contains(ResonatorEffect.Stabilizer) ? chance * Content.StabilizerFailFactor : chance;
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
            var originalAcceleration = Plugin.LimitAcceleration(asteroid);

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
                mult *= Content.LootBonusFactor;
            }
            var total = Mathf.Max(Plugin.MinLootPerMeteor, Mathf.RoundToInt(baseCount * mult));
            var extraTypes = 0;
            var extraDuplicates = 0;
            var duplicates = magnet.effects.Contains(ResonatorEffect.DuplicateChance);

            var picks = new List<Group>();
            if (magnet.effects.Contains(ResonatorEffect.EveryResourceType))
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
                if (duplicates && UnityEngine.Random.value < Content.DuplicateChance)
                {
                    result.Add(g);
                    extraDuplicates++;
                }
            }
            Plugin.Log("Loot roll for #" + magnet.wo.GetId() + ": base " + baseCount.ToString("0.#") + " x" + mult.ToString("0.##")
                + (mult != Plugin.LootMultiplier ? " (Humble x" + Content.LootBonusFactor + ")" : "") + " = " + total
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

                // Too fast for the game's own impact check: it went through the ground this frame.
                // Put it back on the surface once so the impact registers.
                if (prev.HasValue && !c.unTunneled && TryGroundBetween(prev.Value, pos, out var surface))
                {
                    c.unTunneled = true;
                    t.position = surface + (prev.Value - surface).normalized * 0.5f;
                    Plugin.Log("Meteor passed through the ground at " + surface.ToString() + " (speed " + speed.ToString("0")
                        + " m/s), put back on the surface");
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

            int stored = 0, dropped = 0, filteredOut = 0, full = 0;
            if (c.magnet.tier.hasStorage && inv == null)
            {
                Plugin.Log("Magnet #" + c.magnet.wo.GetId() + " storage inventory not found, everything goes to the pile");
            }
            foreach (var g in c.loot)
            {
                // The meteor core is always kept, whatever the filter says.
                var passes = c.magnet.filter.Count == 0 || c.magnet.filter.Contains(g.GetId()) || g.GetId() == Content.Core.id;
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
                else if (Overflow(g, forward, c.pile))
                {
                    stored++;
                }
                else
                {
                    dropped++;
                }
            }

            if (dropped != 0 && c.magnet.Upgrade(RoverUpgradeType.Lights) > 0)
            {
                MarkPile(c.pile, c.magnet.Upgrade(RoverUpgradeType.Lights));
            }

            MeteorStats.AddResources(c.loot);
            Plugin.Log("Delivered " + c.loot.Count + " items from magnet #" + c.magnet.wo.GetId() + ": " + stored + " stored/forwarded, "
                + dropped + " dropped at " + c.pile
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
