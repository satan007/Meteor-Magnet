using SpaceCraft;
using System.Collections.Generic;
using System.Linq;

namespace MeteorMagnet
{
    /// <summary>
    /// Meteor network statistics for the wall display. Kept in the text of a hidden world object,
    /// so they are saved with the world: "captured=12;slipped=3;deflected=1;Iron=40;Silicon=22".
    /// </summary>
    internal static class MeteorStats
    {
        const int StoreWorldObjectId = 905_150_003;

        internal static int Captured;
        internal static int Slipped;
        internal static int Deflected;
        /// <summary>Achievement hunter meteors that came down next to the player.</summary>
        internal static int CloseCalls;
        internal static readonly Dictionary<string, int> Resources = [];

        static WorldObjectsHandler loadedFor;
        static bool dirty;

        internal static int ResourceTotal => Resources.Values.Sum();

        /// <summary>Reads the saved numbers once per loaded world.</summary>
        internal static void EnsureLoaded()
        {
            var handler = WorldObjectsHandler.Instance;
            if (handler == null || handler == loadedFor)
            {
                return;
            }
            loadedFor = handler;
            Captured = Slipped = Deflected = CloseCalls = 0;
            Resources.Clear();
            var text = handler.GetWorldObjectViaId(StoreWorldObjectId)?.GetText() ?? "";
            foreach (var part in text.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0 || !int.TryParse(part.Substring(eq + 1), out var n))
                {
                    continue;
                }
                var key = part.Substring(0, eq);
                switch (key)
                {
                    case "captured": Captured = n; break;
                    case "slipped": Slipped = n; break;
                    case "deflected": Deflected = n; break;
                    case "closecalls": CloseCalls = n; break;
                    default: Resources[key] = n; break;
                }
            }
            Plugin.Log("Meteor stats loaded: " + (text.Length == 0 ? "none yet" : text));
        }

        internal static void AddCaptured() { EnsureLoaded(); Captured++; dirty = true; }
        internal static void AddSlipped() { EnsureLoaded(); Slipped++; dirty = true; }
        internal static void AddDeflected() { EnsureLoaded(); Deflected++; dirty = true; }
        internal static void AddCloseCall() { EnsureLoaded(); CloseCalls++; dirty = true; }

        internal static void AddResources(List<Group> loot)
        {
            EnsureLoaded();
            foreach (var g in loot)
            {
                var id = g.GetId();
                Resources[id] = Resources.TryGetValue(id, out var n) ? n + 1 : 1;
            }
            dirty = loot.Count != 0 || dirty;
        }

        /// <summary>Writes changed numbers into the hidden world object.</summary>
        internal static void Save()
        {
            if (!dirty || WorldObjectsHandler.Instance == null)
            {
                return;
            }
            dirty = false;
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
            var parts = new List<string> { "captured=" + Captured, "slipped=" + Slipped, "deflected=" + Deflected, "closecalls=" + CloseCalls };
            parts.AddRange(Resources.Select(kv => kv.Key + "=" + kv.Value));
            wo.SetText(string.Join(";", parts));
        }

        /// <summary>The most collected resources, readable names: "Iron 40, Silicon 22, Iridium 5".</summary>
        internal static string Top(int count)
        {
            return string.Join(", ", Resources.OrderByDescending(kv => kv.Value).Take(count)
                .Select(kv =>
                {
                    var g = GroupsHandler.GetGroupViaId(kv.Key);
                    return (g != null ? Readable.GetGroupName(g) : kv.Key) + " " + kv.Value;
                }));
        }
    }
}
