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

            internal int Upgrade(RoverUpgradeType t) => upgrades.TryGetValue(t, out var v) ? v : 0;

            /// <summary>The building's separate module slots (see <see cref="ModuleSlots"/>).</summary>
            internal Inventory moduleInventory;

            internal void ReadModules()
            {
                if (moduleInventory == null)
                {
                    return;
                }
                foreach (var item in moduleInventory.GetInsideWorldObjects())
                {
                    var itemId = item?.GetGroup()?.GetId();
                    var res = Content.ResonatorOf(itemId);
                    // A module only works in the building it is made for.
                    if (res != null && res.forScanner == (this is Scanner))
                    {
                        effects.Add(res.effect);
                    }
                    if (RoverUpgrades.TryGet(itemId, out var type, out var upTier) && upTier > Upgrade(type))
                    {
                        upgrades[type] = upTier;
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

            /// <summary>Mk3 with a Space Link module over a planet that has a satellite in orbit.</summary>
            internal bool planetWide;

            internal float ScanRadius => planetWide
                ? float.PositiveInfinity
                : tier.scanRadius * (effects.Contains(ResonatorEffect.RadiusBoost) ? Content.RadiusBoostFactor : 1f);

            internal float LinkFactor => Upgrade(RoverUpgradeType.Beacon) > 0 ? RoverUpgrades.BeaconLinkFactor : 1f;

            internal float LockTime => tier.lockTime
                * (planetWide && effects.Contains(ResonatorEffect.RadiusBoost) ? Content.LensLockFactor : 1f);

            internal int TrackSlots => tier.trackSlots + RoverUpgrades.EquipmentTargets(Upgrade(RoverUpgradeType.Equipment));
        }

        class Capture
        {
            internal Magnet magnet;
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
        }

        /// <summary>A meteor a scanner is tracking: locking on, then waiting for a free magnet.</summary>
        class Track
        {
            internal WorldObject scanner;
            internal float since;
        }

        readonly Dictionary<Asteroid, Capture> captures = [];
        readonly Dictionary<Asteroid, Track> tracks = [];
        // Meteors already decided (captured, failed or not worth it), so each one is decided only once.
        readonly HashSet<int> seen = [];
        readonly List<Magnet> magnets = [];
        readonly List<Scanner> scanners = [];
        readonly List<(WorldObject wo, Vector3 position)> relays = [];
        readonly Dictionary<int, Light> glows = [];
        // Vanilla container sizes by group id, used as the base size of Mk2/Mk3 storage.
        readonly Dictionary<string, int> baseSizes = [];
        static readonly HashSet<string> warnedNoBaseSize = [];
        // Magnets already reported as outside every link area.
        readonly HashSet<int> warnedUnlinked = [];
        readonly List<Asteroid> done = [];

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
                ScanForMeteors();
            }

            if (Plugin.PullMeteors)
            {
                SteerCaptured();
            }
            CheckLanded();

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
            var planetHash = CurrentPlanetHash();
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                var gid = wo?.GetGroup()?.GetId();
                var tier = Content.TierOf(gid);
                var scannerTier = Content.ScannerOf(gid);
                var relay = Content.IsRelay(gid);
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
            foreach (var s in scanners)
            {
                var factor = s.LinkFactor;
                s.link.Add((s.position, s.tier.linkRadius * factor));

                // Relays join when they stand inside the area reached so far; repeat until nothing new joins.
                var joined = new bool[relays.Count];
                bool grew;
                do
                {
                    grew = false;
                    for (int i = 0; i < relays.Count; i++)
                    {
                        if (!joined[i] && Covers(s.link, relays[i].position))
                        {
                            joined[i] = true;
                            s.link.Add((relays[i].position, Content.Relay.linkRadius * factor));
                            grew = true;
                        }
                    }
                }
                while (grew);

                foreach (var m in magnets)
                {
                    if (Covers(s.link, m.position))
                    {
                        s.network.Add(m);
                        linked.Add(m);
                    }
                }
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

        static bool Covers(List<(Vector3 center, float radius)> area, Vector3 p)
        {
            foreach (var (center, radius) in area)
            {
                var d = center - p;
                d.y = 0;
                if (d.sqrMagnitude <= radius * radius)
                {
                    return true;
                }
            }
            return false;
        }

        // What each building's modules were last time, so changes are logged once.
        readonly Dictionary<int, string> loggedModules = [];

        void LogModules(Building b)
        {
            var parts = new List<string>();
            foreach (var e in b.effects)
            {
                parts.Add(e.ToString());
            }
            foreach (var kv in b.upgrades)
            {
                parts.Add(kv.Key + " T" + kv.Value);
            }
            parts.Sort(StringComparer.Ordinal);
            var text = (b.moduleInventory == null ? "slots not ready; " : "slots " + b.moduleInventory.GetInsideWorldObjects().Count
                + "/" + b.moduleInventory.GetSize() + "; ") + (parts.Count == 0 ? "no modules" : string.Join(", ", parts));
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

        void ScanForMeteors()
        {
            if (scanners.Count == 0 || magnets.Count == 0)
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
                        Plugin.Log("Meteor crashed while tracked: " + (tracked < lockNeeded ? "lock not done" : "no free magnet")
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
                if (scanner == null || !CanSee(scanner, landing, barren, rare))
                {
                    if (track != null)
                    {
                        tracks.Remove(asteroid);
                    }
                    // Pick up a new target: the nearest scanner that sees it and has a free tracking slot.
                    scanner = PickScanner(landing, barren, rare);
                    if (scanner != null)
                    {
                        tracks[asteroid] = new Track { scanner = scanner.wo, since = Time.time };
                        scanner.tracking++;
                    }
                    continue;
                }

                // Lock on first, then wait for a magnet with a free slot.
                var lockTime = scanner.LockTime * (rare ? Content.RareLockFactor : 1f);
                if (Time.time - track.since < lockTime)
                {
                    continue;
                }
                var magnet = PickMagnet(scanner, landing);
                if (magnet == null)
                {
                    continue;
                }
                tracks.Remove(asteroid);
                scanner.tracking--;
                seen.Add(asteroid.GetInstanceID());
                magnet.busy++;

                var failChance = magnet.tier.failChance + (rare ? Content.RareFailBonus : 0f);
                if (UnityEngine.Random.value < failChance)
                {
                    if (UnityEngine.Random.value < Content.BadFailShare)
                    {
                        Deflect(asteroid, magnet);
                    }
                    else
                    {
                        magnet.busy--;
                        Plugin.Log("Capture slipped, meteor keeps its course.");
                        NotifyText("MeteorMagnet_Slipped", DataConfig.UiInformationsType.OutInventory);
                    }
                    continue;
                }

                var loot = barren ? RollBarrenLoot(asteroid, magnet) : RollLoot(asteroid, magnet);
                if (loot.Count == 0)
                {
                    magnet.busy--;
                    continue;
                }

                // Neutralize the impact: no resources on the ground, no damage.
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
                    target = GroundPoint(magnet.position + away.normalized * Plugin.ImpactOffset),
                    pile = magnet.position + away.normalized * 3f + Vector3.up * 1.5f,
                    loot = loot,
                    capturedAt = Time.time,
                };
                if (magnet.Upgrade(RoverUpgradeType.Jetpack) > 0)
                {
                    Deliver(capture);
                    capture.delivered = true;
                }
                captures[asteroid] = capture;
                Plugin.Log("Captured meteor with " + loot.Count + " resources for " + magnet.tier.id + " #" + magnet.wo.GetId()
                    + ": meteor at " + asteroid.transform.position + ", would land " + landing + ", pulled to " + capture.target
                    + " (magnet at " + magnet.position + ")");
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

        static bool CanSee(Scanner s, Vector3 landing, bool barren, bool rare)
        {
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

        Scanner PickScanner(Vector3 landing, bool barren, bool rare)
        {
            Scanner best = null;
            var bestDist = float.MaxValue;
            foreach (var s in scanners)
            {
                if (s.tracking >= s.TrackSlots || !CanSee(s, landing, barren, rare))
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
        static Magnet PickMagnet(Scanner scanner, Vector3 landing)
        {
            Magnet best = null;
            var bestDist = float.MaxValue;
            foreach (var m in scanner.network)
            {
                if (m.busy >= m.tier.captureSlots)
                {
                    continue;
                }
                var dist = Vector3.Distance(m.position, landing);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                }
            }
            return best;
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

        static bool IsRare(Asteroid asteroid)
        {
            foreach (var gd in asteroid.groupsSelected)
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
        void Deflect(Asteroid asteroid, Magnet magnet)
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

            captures[asteroid] = new Capture
            {
                magnet = magnet,
                target = GroundPoint(magnet.position + dir * dist),
                loot = [],
                capturedAt = Time.time,
                delivered = true,
                deflected = true,
            };
            Plugin.Log("Capture failed badly, meteor thrown " + dist.ToString("0") + " m away.");
            NotifyText("MeteorMagnet_Deflected", DataConfig.UiInformationsType.OutInventory);
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
                }
            }
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
                var speed = c.lastPosition.HasValue ? (pos - c.lastPosition.Value).magnitude / dt : 0f;
                c.lastPosition = pos;
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
                    Plugin.Log("Captured meteor " + (crashed ? "crashed" : gone ? "disappeared" : "timed out (never hit the ground)")
                        + " after " + (Time.time - kv.Value.capturedAt).ToString("0.0") + " s"
                        + (gone ? "" : " at " + asteroid.transform.position) + (kv.Value.deflected ? ", thrown away" : ""));
                    done.Add(asteroid);
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

            int stored = 0, dropped = 0;
            foreach (var g in c.loot)
            {
                var toStorage = inv != null
                    && (c.magnet.filter.Count == 0 || c.magnet.filter.Contains(g.GetId()))
                    && !inv.IsFull();
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

            Plugin.Log("Delivered " + c.loot.Count + " items from magnet #" + c.magnet.wo.GetId() + ": " + stored + " stored/forwarded, "
                + dropped + " dropped at " + c.pile);
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
