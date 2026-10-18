using SpaceCraft;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Scans for incoming meteors, assigns them to the nearest Meteor Magnet,
    /// steers them towards it and, once they land, puts their resources into the magnet's inventory.
    /// </summary>
    internal class MagnetController : MonoBehaviour
    {
        const float ScanInterval = 0.25f;

        // If a captured meteor neither crashes nor disappears in this time, deliver the loot anyway.
        const float CaptureTimeout = 90f;

        class Capture
        {
            internal WorldObject magnet;
            internal Vector3 target;
            internal List<Group> loot;
            internal float capturedAt;
        }

        readonly Dictionary<Asteroid, Capture> captures = [];
        // Meteors already looked at (captured or rejected), so each one is decided only once.
        readonly HashSet<int> seen = [];
        readonly List<WorldObject> magnets = [];
        readonly HashSet<int> decorated = [];

        float nextScan;

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
        }

        void RefreshMagnets()
        {
            magnets.Clear();
            var planetHash = CurrentPlanetHash();
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                if (wo == null || wo.GetGroup()?.GetId() != Plugin.MagnetGroupId)
                {
                    continue;
                }
                if (planetHash != 0 && wo.GetPlanetHash() != planetHash)
                {
                    continue;
                }
                if (!wo.GetIsPlaced())
                {
                    continue;
                }
                magnets.Add(wo);
                Decorate(wo);
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
                if (asteroid.groupsSelected == null || asteroid.groupsSelected.Count == 0)
                {
                    // Plain rock / rain / lightning: nothing to collect.
                    continue;
                }

                var landing = PredictLanding(asteroid.transform);
                var magnet = FindMagnetFor(landing);
                if (magnet == null)
                {
                    Plugin.Log("Meteor at " + asteroid.transform.position + " (landing ~" + landing + ") is out of range.");
                    continue;
                }

                var loot = RollLoot(asteroid);
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

                var magnetPos = magnet.GetPosition();
                var away = landing - magnetPos;
                away.y = 0;
                if (away.sqrMagnitude < 0.01f)
                {
                    away = Vector3.forward;
                }
                var target = magnetPos + away.normalized * Plugin.ImpactOffset;

                captures[asteroid] = new Capture
                {
                    magnet = magnet,
                    target = target,
                    loot = loot,
                    capturedAt = Time.time,
                };
                Plugin.Log("Captured meteor with " + loot.Count + " resources for magnet " + magnet.GetId());
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
                groundY += m.GetPosition().y;
            }
            groundY /= magnets.Count;
            if (f.y < -0.01f && p.y > groundY)
            {
                return p + f * ((p.y - groundY) / -f.y);
            }
            return p;
        }

        WorldObject FindMagnetFor(Vector3 landing)
        {
            WorldObject best = null;
            var bestDist = float.MaxValue;
            var radius = Plugin.CaptureRadius;
            foreach (var m in magnets)
            {
                var d = m.GetPosition() - landing;
                d.y = 0;
                var dist = d.magnitude;
                if (!Plugin.CaptureEverywhere && dist > radius)
                {
                    continue;
                }
                var inv = InventoriesHandler.Instance.GetInventoryById(m.GetLinkedInventoryId());
                if (inv == null || inv.IsFull())
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

        static List<Group> RollLoot(Asteroid asteroid)
        {
            // numberOfResourceInDebris is a fraction of the debris pieces in vanilla data;
            // treat values above 1 as an absolute count to be safe.
            var n = asteroid.numberOfResourceInDebris;
            var count = n <= 1f ? asteroid.debrisNumber * n : n;
            var total = Mathf.Max(Plugin.MinLootPerMeteor, Mathf.RoundToInt(count * Plugin.LootMultiplier));
            Plugin.Log("Meteor debris=" + asteroid.debrisNumber + ", resourceInDebris=" + n + " -> " + total + " items");

            var candidates = new List<Group>();
            foreach (var gd in asteroid.groupsSelected)
            {
                if (gd == null)
                {
                    continue;
                }
                var g = GroupsHandler.GetGroupViaId(gd.id);
                if (g != null)
                {
                    candidates.Add(g);
                }
            }

            var result = new List<Group>();
            if (candidates.Count == 0)
            {
                return result;
            }
            for (int i = 0; i < total; i++)
            {
                result.Add(candidates[UnityEngine.Random.Range(0, candidates.Count)]);
            }
            return result;
        }

        void SteerCaptured()
        {
            var maxDeg = Plugin.PullTurnRate * Time.deltaTime;
            foreach (var kv in captures)
            {
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

        readonly List<Asteroid> done = [];

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
                    Deliver(kv.Value);
                }
            }
            foreach (var a in done)
            {
                captures.Remove(a);
            }
        }

        static void Deliver(Capture c)
        {
            var inv = InventoriesHandler.Instance.GetInventoryById(c.magnet.GetLinkedInventoryId());
            if (inv == null)
            {
                Plugin.LogWarning("Magnet " + c.magnet.GetId() + " has no inventory; loot lost.");
                return;
            }
            foreach (var g in c.loot)
            {
                InventoriesHandler.Instance.AddItemToInventory(g, inv, (success, id) =>
                {
                    if (!success && id != 0)
                    {
                        WorldObjectsHandler.Instance.DestroyWorldObject(id);
                    }
                });
            }

            if (Plugin.ShowNotification && c.loot.Count != 0)
            {
                Notify(c.loot);
            }
        }

        static void Notify(List<Group> loot)
        {
            try
            {
                var displayer = Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer();
                if (displayer == null)
                {
                    return;
                }
                var text = Localization.GetLocalizedString("MeteorMagnet_Captured") + ": +" + loot.Count;
                displayer.AddInformation(3f, text, DataConfig.UiInformationsType.InInventory, loot[0].GetImage());
            }
            catch (Exception ex)
            {
                Plugin.Log("Notification failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Gives the placed magnet a purple glow so it does not look like a plain container.
        /// </summary>
        void Decorate(WorldObject wo)
        {
            var go = wo.GetGameObject();
            if (go == null || !decorated.Add(go.GetInstanceID()))
            {
                return;
            }
            var color = new Color(0.55f, 0.25f, 1f);
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
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = 8f;
            light.intensity = 3f;
        }
    }
}
