using SpaceCraft;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// What is stored on each storage disk: every disk item has its own hidden inventory, so the items travel
    /// with the disk. The disk -> inventory mapping lives in the text of a hidden world object.
    /// </summary>
    internal static class DiskStorage
    {
        const int StoreWorldObjectId = 905_150_005;
        static readonly HashSet<int> pending = [];

        static bool IsHost => NetworkManager.Singleton == null || NetworkManager.Singleton.IsServer;

        /// <summary>The disk's inventory, or null while it is being created.</summary>
        internal static Inventory Get(WorldObject disk, int capacity)
        {
            if (!IsHost)
            {
                return null;
            }
            var id = disk.GetId();
            var mapping = Load();
            if (mapping.TryGetValue(id, out var invId))
            {
                var inv = InventoriesHandler.Instance.GetInventoryById(invId);
                if (inv != null)
                {
                    if (inv.GetSize() < capacity)
                    {
                        inv.SetSize(capacity);
                    }
                    return inv;
                }
                Plugin.Log("Disk inventory " + invId + " of disk " + id + " not found, creating a new one.");
            }
            if (pending.Add(id))
            {
                InventoriesHandler.Instance.CreateNewInventory(capacity, 0, 0, null, null, inv =>
                {
                    pending.Remove(id);
                    if (inv == null)
                    {
                        return;
                    }
                    var m = Load();
                    m[id] = inv.GetId();
                    Save(m);
                    Plugin.Log("Disk " + id + " got inventory " + inv.GetId() + " (" + capacity + " items)");
                });
            }
            return null;
        }

        internal static void Reset()
        {
            pending.Clear();
        }

        static Dictionary<int, int> Load()
        {
            var result = new Dictionary<int, int>();
            var wo = WorldObjectsHandler.Instance.GetWorldObjectViaId(StoreWorldObjectId);
            foreach (var pair in (wo?.GetText() ?? "").Split(';'))
            {
                var kv = pair.Split(',');
                if (kv.Length == 2 && int.TryParse(kv[0], out var disk) && int.TryParse(kv[1], out var inv))
                {
                    result[disk] = inv;
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

    /// <summary>A storage disk inserted into a drive of a working network.</summary>
    internal class NetworkDisk
    {
        internal WorldObject disk;
        internal StorageItemDef def;
        internal Inventory inventory;

        internal int Count => inventory.GetInsideWorldObjects().Count;

        internal HashSet<string> Kinds()
        {
            var kinds = new HashSet<string>();
            foreach (var wo in inventory.GetInsideWorldObjects())
            {
                var id = wo?.GetGroup()?.GetId();
                if (id != null)
                {
                    kinds.Add(id);
                }
            }
            return kinds;
        }
    }

    /// <summary>One controller with the drives and terminals it serves.</summary>
    internal class StorageNet
    {
        internal WorldObject controller;
        internal string coreId;
        internal int channels;
        /// <summary>Multiplier of every link radius, from the core's tier.</summary>
        internal float radiusFactor = 1f;
        internal readonly List<WorldObject> drives = [];
        internal readonly List<WorldObject> terminals = [];
        /// <summary>Magnets and scanners with the pulsar transmitter served by this network.</summary>
        internal readonly List<WorldObject> magnets = [];
        /// <summary>Module distributors of this network.</summary>
        internal readonly List<WorldObject> hubs = [];
        /// <summary>Devices in reach that got no channel (farthest first lose out).</summary>
        internal readonly List<WorldObject> unserved = [];
        internal readonly List<NetworkDisk> disks = [];
        /// <summary>Links of the network (controller -> relay -> device) with the channels running through each.</summary>
        internal readonly List<(int fromId, Vector3 from, int toId, Vector3 to, int channels)> links = [];

        internal IEnumerable<WorldObject> Served()
        {
            foreach (var l in new[] { drives, terminals, magnets, hubs })
            {
                foreach (var wo in l)
                {
                    yield return wo;
                }
            }
        }

        /// <summary>Channels taken: every device but the controller.</summary>
        internal int Used => drives.Count + terminals.Count + magnets.Count + hubs.Count;
        internal int Capacity { get { var c = 0; foreach (var d in disks) { c += d.def.capacity; } return c; } }
        internal int Stored { get { var c = 0; foreach (var d in disks) { c += d.Count; } return c; } }
        internal int KindSlots { get { var c = 0; foreach (var d in disks) { c += d.def.types; } return c; } }

        /// <summary>The infinity core powers the whole network: nothing on it draws power.</summary>
        internal bool SelfPowered => Content.SelfPowered(Content.CoreTierOf(coreId));

        internal float Draw()
        {
            if (SelfPowered)
            {
                return 0f;
            }
            // The controller's own draw grows x1.5 per core tier (channels double).
            var draw = Plugin.EnergyUse(Content.StorageController.id) * Content.ControllerDrawFactor(Content.CoreTierOf(coreId))
                + drives.Count * Plugin.EnergyUse(Content.StorageDrive.id)
                + terminals.Count * Plugin.EnergyUse(Content.StorageTerminal.id)
                + hubs.Count * Plugin.EnergyUse(Content.ModuleHub.id);
            foreach (var d in disks)
            {
                draw += d.def.energy;
            }
            return draw;
        }
    }

    /// <summary>
    /// The item storage networks (AE2-style): built from the constructed controllers, drives, terminals
    /// and the meteor relays on every network refresh.
    /// </summary>
    internal static class StorageNetwork
    {
        internal static readonly List<StorageNet> Nets = [];
        // Last logged state of each controller, so changes are logged once.
        static readonly Dictionary<int, string> logged = [];

        internal static void Reset()
        {
            Nets.Clear();
            logged.Clear();
            DiskStorage.Reset();
        }

        internal static float Demand()
        {
            var d = 0f;
            foreach (var n in Nets)
            {
                d += n.Draw();
            }
            return d;
        }

        internal static StorageNet NetOf(WorldObject device)
        {
            foreach (var n in Nets)
            {
                if (n.terminals.Contains(device) || n.drives.Contains(device) || n.magnets.Contains(device) || n.hubs.Contains(device) || n.controller == device)
                {
                    return n;
                }
            }
            return null;
        }

        /// <summary>The nearest node whose circle covers the point, or -1.</summary>
        static int Nearest(List<(int id, Vector3 pos, float radius, int parent, WorldObject device)> nodes, Vector3 p)
        {
            var best = -1;
            var bestDist = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                var d = Flat(nodes[i].pos, p);
                if (d <= nodes[i].radius && d < bestDist)
                {
                    best = i;
                    bestDist = d;
                }
            }
            return best;
        }

        static float Flat(Vector3 a, Vector3 b)
        {
            var d = a - b;
            d.y = 0;
            return d.magnitude;
        }

        internal static void Build(List<WorldObject> controllers, List<WorldObject> drives, List<WorldObject> terminals, List<WorldObject> magnets,
            List<WorldObject> hubs, List<(WorldObject wo, Vector3 position)> relays)
        {
            Nets.Clear();
            var taken = new HashSet<WorldObject>();
            foreach (var c in controllers)
            {
                var net = new StorageNet { controller = c };
                var slot = ModuleSlots.Get(c, Content.StorageController.slots);
                foreach (var item in slot?.GetInsideWorldObjects() ?? (IEnumerable<WorldObject>)[])
                {
                    var id = item?.GetGroup()?.GetId();
                    var ch = Content.ChannelsOf(id);
                    if (ch > net.channels)
                    {
                        net.channels = ch;
                        net.coreId = id;
                        net.radiusFactor = Content.RadiusFactorOf(id);
                    }
                }

                // Reach: a tree grown from the controller. Relays carry the link far (60 m), every other device of the
                // network passes it on to neighbours close by (12 m), so a compact base needs no relays at all.
                // Each node remembers the node it was reached from, for the channel spirals.
                var nodes = new List<(int id, Vector3 pos, float radius, int parent, WorldObject device)>
                {
                    (c.GetId(), c.GetPosition(), Content.LinkRadius * net.radiusFactor, -1, null),
                };
                var candidates = new List<(WorldObject wo, Vector3 pos, float radius, int kind)>();
                foreach (var r in relays)
                {
                    candidates.Add((r.wo, r.position, Content.LinkRadius * net.radiusFactor, -1));
                }
                void AddDevices(List<WorldObject> list, int kind)
                {
                    foreach (var d in list)
                    {
                        if (!taken.Contains(d))
                        {
                            candidates.Add((d, d.GetPosition(), Content.DeviceLinkRadius * net.radiusFactor, kind));
                        }
                    }
                }
                // 0 drive, 1 terminal, 2 linked magnet or scanner, 3 module distributor.
                AddDevices(drives, 0);
                AddDevices(terminals, 1);
                AddDevices(magnets, 2);
                AddDevices(hubs, 3);
                var joined = new bool[candidates.Count];
                bool grew;
                do
                {
                    grew = false;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        var parent = joined[i] ? -1 : Nearest(nodes, candidates[i].pos);
                        if (parent >= 0)
                        {
                            joined[i] = true;
                            var cand = candidates[i];
                            nodes.Add((cand.wo.GetId(), cand.pos, cand.radius, parent, cand.kind >= 0 ? cand.wo : null));
                            grew = true;
                        }
                    }
                } while (grew);

                // Channels go to the devices reached first (closest to the controller along the tree).
                for (int i = 1; i < nodes.Count; i++)
                {
                    var wo = nodes[i].device;
                    if (wo == null)
                    {
                        continue;
                    }
                    var kind = candidates.Find(x => x.wo == wo).kind;
                    taken.Add(wo);
                    if (net.Used >= net.channels)
                    {
                        net.unserved.Add(wo);
                    }
                    else
                    {
                        (kind == 0 ? net.drives : kind == 1 ? net.terminals : kind == 2 ? net.magnets : net.hubs).Add(wo);
                    }
                }

                foreach (var d in net.drives)
                {
                    var bay = ModuleSlots.Get(d, Content.StorageDrive.slots);
                    foreach (var item in bay?.GetInsideWorldObjects() ?? (IEnumerable<WorldObject>)[])
                    {
                        var def = Content.StorageItemOf(item?.GetGroup()?.GetId());
                        if (def == null || !def.IsDisk)
                        {
                            continue;
                        }
                        var inv = DiskStorage.Get(item, def.capacity);
                        if (inv != null)
                        {
                            net.disks.Add(new NetworkDisk { disk = item, def = def, inventory = inv });
                        }
                    }
                }
                // Channels through every link: each served device adds one to its link and to every link up to the controller.
                var through = new Dictionary<int, int>();
                foreach (var device in net.Served())
                {
                    var at = nodes.FindIndex(x => x.device == device);
                    for (var n = at; n > 0; n = nodes[n].parent)
                    {
                        through[n] = through.TryGetValue(n, out var k) ? k + 1 : 1;
                    }
                }
                foreach (var kv in through)
                {
                    var child = nodes[kv.Key];
                    var parent = nodes[child.parent];
                    net.links.Add((parent.id, parent.pos, child.id, child.pos, kv.Value));
                }
                Nets.Add(net);

                var state = "core " + (net.coreId ?? "none") + " (" + Content.ChannelsText(net.channels) + " channels, radii x" + net.radiusFactor + "), drives " + net.drives.Count
                    + ", terminals " + net.terminals.Count + ", linked magnets/scanners " + net.magnets.Count + ", distributors " + net.hubs.Count + ", without a channel " + net.unserved.Count
                    + ", disks " + net.disks.Count + ", stored " + net.Stored + "/" + net.Capacity + ", draw " + net.Draw().ToString("0.#") + " kW";
                if (!logged.TryGetValue(c.GetId(), out var last) || last != state)
                {
                    logged[c.GetId()] = state;
                    Plugin.Log("Storage controller #" + c.GetId() + ": " + state);
                }
            }
        }

        /// <summary>Items of the network grouped by kind: every item with the disk it is on.</summary>
        internal static Dictionary<string, List<(NetworkDisk disk, WorldObject item)>> Contents(StorageNet net)
        {
            var result = new Dictionary<string, List<(NetworkDisk, WorldObject)>>();
            foreach (var d in net.disks)
            {
                foreach (var wo in d.inventory.GetInsideWorldObjects())
                {
                    var id = wo?.GetGroup()?.GetId();
                    if (id == null)
                    {
                        continue;
                    }
                    if (!result.TryGetValue(id, out var list))
                    {
                        result[id] = list = [];
                    }
                    list.Add((d, wo));
                }
            }
            return result;
        }

        /// <summary>
        /// A disk that can take one more item of this kind: one that already holds the kind first,
        /// then one with a free kind slot. <paramref name="planned"/> counts items on their way to each disk.
        /// </summary>
        internal static NetworkDisk DiskFor(StorageNet net, string kind, Dictionary<NetworkDisk, (int count, HashSet<string> kinds)> planned)
        {
            NetworkDisk fallback = null;
            foreach (var d in net.disks)
            {
                if (!planned.TryGetValue(d, out var p))
                {
                    p = (d.Count, d.Kinds());
                    planned[d] = p;
                }
                if (p.count >= d.def.capacity)
                {
                    continue;
                }
                if (p.kinds.Contains(kind))
                {
                    return d;
                }
                if (fallback == null && p.kinds.Count < d.def.types)
                {
                    fallback = d;
                }
            }
            return fallback;
        }
    }
}
