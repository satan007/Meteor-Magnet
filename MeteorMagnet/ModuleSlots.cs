using SpaceCraft;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Separate module slots for scanners and magnets: each building gets its own small inventory that only
    /// accepts modules (resonators and rover upgrades), apart from the storage the loot goes into.
    /// Which inventory belongs to which building is kept in the text of a hidden world object, so it is saved.
    /// </summary>
    internal static class ModuleSlots
    {
        // Fixed id next to the satellite store, far away from the game's own ids.
        const int StoreWorldObjectId = 905_150_002;

        // Buildings whose module inventory is being created right now.
        static readonly HashSet<int> pending = [];
        // Inventories that already got their module-only restriction this session.
        static readonly HashSet<int> authorized = [];
        // Buildings seen placed this session: only these can be "gone" (deconstructed), not merely not loaded yet.
        static readonly Dictionary<int, Vector3> seenBuildings = [];

        static bool IsHost => NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

        /// <summary>
        /// The module inventory of a building, created on first use. Null while it is being created.
        /// </summary>
        // Extra slots from an exoskeleton inside the building, by building id (see SetExtraSlots).
        static readonly Dictionary<int, int> extraSlots = [];

        /// <summary>Exoskeleton: the building gets this many slots on top of its own from the next refresh on.</summary>
        internal static void SetExtraSlots(int buildingId, int extra)
        {
            if (extra > 0)
            {
                extraSlots[buildingId] = extra;
            }
            else
            {
                extraSlots.Remove(buildingId);
            }
        }

        internal static Inventory Get(WorldObject building, int slots)
        {
            if (slots <= 0 || !IsHost)
            {
                return null;
            }
            var id = building.GetId();
            slots += extraSlots.TryGetValue(id, out var extra) ? extra : 0;
            seenBuildings[id] = building.GetPosition();

            var mapping = Load();
            if (mapping.TryGetValue(id, out var invId))
            {
                var inv = InventoriesHandler.Instance.GetInventoryById(invId);
                if (inv != null)
                {
                    Prepare(inv, slots);
                    return inv;
                }
                // The saved inventory is gone: give the building a fresh one.
                Plugin.Log("Module inventory " + invId + " of building " + id + " not found, creating a new one.");
            }

            if (pending.Add(id))
            {
                InventoriesHandler.Instance.CreateNewInventory(slots, 0, 0, null, null, inv =>
                {
                    pending.Remove(id);
                    if (inv == null)
                    {
                        return;
                    }
                    var m = Load();
                    m[id] = inv.GetId();
                    Save(m);
                    Prepare(inv, slots);
                    Plugin.Log("Module slots " + inv.GetId() + " created for building " + id);
                });
            }
            return null;
        }

        static void Prepare(Inventory inv, int slots)
        {
            // Never shrink below what is inside.
            var size = Mathf.Max(slots, inv.GetInsideWorldObjects().Count);
            if (inv.GetSize() != size)
            {
                inv.SetSize(size);
            }
            if (authorized.Add(inv.GetId()))
            {
                foreach (var id in ModuleGroupIds())
                {
                    var g = GroupsHandler.GetGroupViaId(id);
                    if (g != null)
                    {
                        inv.AddAuthorizedGroup(g);
                    }
                }
            }
        }

        static IEnumerable<string> ModuleGroupIds()
        {
            foreach (var r in Content.Resonators)
            {
                yield return r.id;
            }
            yield return Content.Blueprint.id;
            foreach (var id in RoverUpgrades.AllIds())
            {
                yield return id;
            }
            foreach (var id in PlayerGear.AllIds())
            {
                yield return id;
            }
        }

        /// <summary>
        /// Modules of deconstructed buildings are not lost: they drop where the building stood.
        /// </summary>
        internal static void DropOrphans()
        {
            if (!IsHost || seenBuildings.Count == 0)
            {
                return;
            }
            var mapping = Load();
            var changed = false;
            foreach (var kv in new List<KeyValuePair<int, int>>(mapping))
            {
                if (!seenBuildings.TryGetValue(kv.Key, out var pos)
                    || WorldObjectsHandler.Instance.GetWorldObjectViaId(kv.Key) != null)
                {
                    continue;
                }
                var inv = InventoriesHandler.Instance.GetInventoryById(kv.Value);
                if (inv != null)
                {
                    var items = new List<WorldObject>(inv.GetInsideWorldObjects());
                    InventoriesHandler.Instance.DestroyInventory(kv.Value, false);
                    foreach (var item in items)
                    {
                        var g = item?.GetGroup();
                        if (g == null)
                        {
                            continue;
                        }
                        // Recreate the module on the ground and remove the copy that was in the destroyed inventory.
                        WorldObjectsHandler.Instance.CreateAndDropOnFloor(g, pos + Vector3.up);
                        WorldObjectsHandler.Instance.DestroyWorldObject(item.GetId());
                        Plugin.Log("  dropped module " + g.GetId());
                    }
                }
                mapping.Remove(kv.Key);
                seenBuildings.Remove(kv.Key);
                changed = true;
                Plugin.Log("Building " + kv.Key + " is gone, its modules were dropped.");
            }
            if (changed)
            {
                Save(mapping);
            }
        }

        /// <summary>A new world was loaded: forget what was seen in the previous one.</summary>
        internal static void Reset()
        {
            pending.Clear();
            authorized.Clear();
            extraSlots.Clear();
            seenBuildings.Clear();
        }

        static Dictionary<int, int> Load()
        {
            var result = new Dictionary<int, int>();
            var wo = WorldObjectsHandler.Instance.GetWorldObjectViaId(StoreWorldObjectId);
            foreach (var pair in (wo?.GetText() ?? "").Split(';'))
            {
                var kv = pair.Split(',');
                if (kv.Length == 2 && int.TryParse(kv[0], out var building) && int.TryParse(kv[1], out var inv))
                {
                    result[building] = inv;
                }
            }
            return result;
        }

        static void Save(Dictionary<int, int> mapping)
        {
            var wo = WorldObjectsHandler.Instance.GetWorldObjectViaId(StoreWorldObjectId);
            if (wo == null)
            {
                var gr = GroupsHandler.GetGroupViaId("Iron");
                if (gr == null)
                {
                    return;
                }
                wo = WorldObjectsHandler.Instance.CreateNewWorldObject(gr, StoreWorldObjectId);
                wo.SetDontSaveMe(false);
            }
            var parts = new List<string>();
            foreach (var kv in mapping)
            {
                parts.Add(kv.Key + "," + kv.Value);
            }
            wo.SetText(string.Join(";", parts));
        }
    }
}
