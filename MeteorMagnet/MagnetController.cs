using SpaceCraft;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Scans for incoming meteors, assigns them to the nearest Meteor Magnet that can reach them,
    /// steers them towards it and, once they land, puts their resources into the magnet's storage
    /// (Mk2/Mk3, respecting the filter) or drops them in a pile next to it (Mk1, filtered out, storage full).
    /// </summary>
    internal class MagnetController : MonoBehaviour
    {
        const float ScanInterval = 0.25f;

        // If a captured meteor neither crashes nor disappears in this time, deliver the loot anyway.
        const float CaptureTimeout = 90f;

        class Magnet
        {
            internal WorldObject wo;
            internal MagnetTier tier;
            internal Vector3 position;
            internal Inventory inventory;
            internal readonly HashSet<ResonatorEffect> effects = [];
            /// <summary>Group ids that go to storage; empty = everything.</summary>
            internal readonly HashSet<string> filter = [];
            /// <summary>Highest tier of each rover upgrade inside the storage.</summary>
            internal readonly Dictionary<RoverUpgradeType, int> upgrades = [];

            internal int Upgrade(RoverUpgradeType t) => upgrades.TryGetValue(t, out var v) ? v : 0;

            internal float Radius => tier.WholePlanet
                ? float.PositiveInfinity
                : tier.radius * (effects.Contains(ResonatorEffect.RadiusBoost) ? Content.RadiusBoostFactor : 1f);
        }

        class Capture
        {
            internal Magnet magnet;
            internal Vector3 target;
            internal Vector3 pile;
            internal List<Group> loot;
            internal float capturedAt;
            internal bool delivered;
        }

        readonly Dictionary<Asteroid, Capture> captures = [];
        // Meteors already looked at (captured or rejected), so each one is decided only once.
        readonly HashSet<int> seen = [];
        readonly List<Magnet> magnets = [];
        readonly Dictionary<int, Light> glows = [];
        // Vanilla container sizes by group id, used as the base size of Mk2/Mk3 storage.
        readonly Dictionary<string, int> baseSizes = [];
        static readonly HashSet<string> warnedNoBaseSize = [];
        readonly List<Asteroid> done = [];

        float nextScan;
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

            if (Time.time >= nextScan)
            {
                nextScan = Time.time + ScanInterval;
                RefreshMagnets();
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

        void RefreshMagnets()
        {
            magnets.Clear();
            var planetHash = CurrentPlanetHash();
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                var gid = wo?.GetGroup()?.GetId();
                var tier = Content.TierOf(gid);
                if (tier == null)
                {
                    RememberBaseSize(wo, gid);
                    continue;
                }
                if (!wo.GetIsPlaced())
                {
                    continue;
                }
                if (planetHash != 0 && wo.GetPlanetHash() != planetHash)
                {
                    continue;
                }
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
                if (magnet.inventory != null)
                {
                    foreach (var item in magnet.inventory.GetInsideWorldObjects())
                    {
                        var itemId = item?.GetGroup()?.GetId();
                        var res = Content.ResonatorOf(itemId);
                        if (res != null)
                        {
                            magnet.effects.Add(res.effect);
                        }
                        if (RoverUpgrades.TryGet(itemId, out var type, out var upTier) && upTier > magnet.Upgrade(type))
                        {
                            magnet.upgrades[type] = upTier;
                        }
                    }
                    ApplyStorageSize(magnet);
                }
                magnets.Add(magnet);
                Decorate(magnet);
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
                var wantsMore = magnet.Upgrade(RoverUpgradeType.Storage) > 0 || magnet.Upgrade(RoverUpgradeType.Equipment) > 0;
                if (wantsMore && baseId != null && warnedNoBaseSize.Add(baseId))
                {
                    Plugin.LogWarning("Storage upgrade needs at least one ordinary " + baseId + " built on this planet to know the base size.");
                }
                return;
            }
            var wanted = baseSize
                + RoverUpgrades.StorageSlots(magnet.Upgrade(RoverUpgradeType.Storage))
                + RoverUpgrades.EquipmentSlots(magnet.Upgrade(RoverUpgradeType.Equipment));
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
            if (magnets.Count == 0)
            {
                return;
            }
            foreach (var asteroid in FindObjectsByType<Asteroid>(FindObjectsSortMode.None))
            {
                if (asteroid == null || !seen.Add(asteroid.GetInstanceID()))
                {
                    continue;
                }
                if (Plugin.HasCrashed(asteroid))
                {
                    continue;
                }
                // Barren meteors (plain rocks) can only be caught with the Prime resonator.
                var barren = asteroid.groupsSelected == null || asteroid.groupsSelected.Count == 0;

                var landing = PredictLanding(asteroid.transform);
                var magnet = FindMagnetFor(landing, barren);
                if (magnet == null)
                {
                    Plugin.Log("Meteor at " + asteroid.transform.position + " (landing ~" + landing + ", barren=" + barren + ") not captured.");
                    continue;
                }

                var loot = barren ? RollBarrenLoot(asteroid, magnet) : RollLoot(asteroid, magnet);
                if (loot.Count == 0)
                {
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
                    target = magnet.position + away.normalized * Plugin.ImpactOffset,
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
                Plugin.Log("Captured meteor with " + loot.Count + " resources for " + magnet.tier.id + " #" + magnet.wo.GetId());
            }

            if (seen.Count > 4096)
            {
                seen.Clear();
            }
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

        /// <summary>
        /// Picks the closest magnet whose radius covers the landing point.
        /// A full magnet still catches meteors; the overflow is dropped next to it.
        /// </summary>
        Magnet FindMagnetFor(Vector3 landing, bool barren)
        {
            Magnet best = null;
            var bestDist = float.MaxValue;
            foreach (var m in magnets)
            {
                if (barren && !m.effects.Contains(ResonatorEffect.CaptureBarren))
                {
                    continue;
                }
                var d = m.position - landing;
                d.y = 0;
                var dist = d.magnitude;
                if (dist > m.Radius)
                {
                    continue;
                }
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = m;
                }
            }
            return best;
        }

        static List<Group> RollLoot(Asteroid asteroid, Magnet magnet)
        {
            // numberOfResourceInDebris is a fraction of the debris pieces in vanilla data;
            // treat values above 1 as an absolute count to be safe.
            var n = asteroid.numberOfResourceInDebris;
            var count = n <= 1f ? asteroid.debrisNumber * n : n;
            Plugin.Log("Meteor debris=" + asteroid.debrisNumber + ", resourceInDebris=" + n);

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
            return Roll(candidates, asteroid.debrisNumber * 0.25f, magnet);
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
            foreach (var kv in captures)
            {
                var speed = 1f + RoverUpgrades.SpeedTurnRatePerTier * kv.Value.magnet.Upgrade(RoverUpgradeType.Speed);
                var maxDeg = Plugin.PullTurnRate * speed * Time.deltaTime;
                var asteroid = kv.Key;
                if (asteroid == null || Plugin.HasCrashed(asteroid))
                {
                    continue;
                }
                var t = asteroid.transform;
                var dir = kv.Value.target - t.position;
                if (dir.sqrMagnitude < 1f)
                {
                    continue;
                }
                var wanted = Quaternion.LookRotation(dir.normalized);
                t.rotation = Quaternion.RotateTowards(t.rotation, wanted, maxDeg);

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
                var landed = asteroid == null
                    || Plugin.HasCrashed(asteroid)
                    || Time.time - kv.Value.capturedAt > CaptureTimeout;
                if (landed)
                {
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

            if (dropped != 0 && c.magnet.Upgrade(RoverUpgradeType.Beacon) > 0)
            {
                MarkPile(c.pile, c.magnet.Upgrade(RoverUpgradeType.Beacon));
            }

            if (Plugin.ShowNotification && c.loot.Count != 0)
            {
                Notify(c.loot[0], stored, dropped);
            }
        }

        /// <summary>
        /// Beacon upgrade: a tall light over the loot pile for a while, so it is easy to spot.
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
        /// Tints the placed magnet in its tier color and adds a glow so it does not look like a plain container.
        /// The Lights upgrade makes the glow brighter and wider.
        /// </summary>
        void Decorate(Magnet magnet)
        {
            var go = magnet.wo.GetGameObject();
            if (go == null)
            {
                return;
            }
            var key = go.GetInstanceID();
            if (!glows.TryGetValue(key, out var light) || light == null)
            {
                var color = magnet.tier.color;
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
            var level = magnet.tier.level;
            var lights = magnet.Upgrade(RoverUpgradeType.Lights);
            light.range = 6f + 3f * level + 8f * lights;
            light.intensity = 2f + level + 3f * lights;
        }
    }
}
