using HarmonyLib;
using SpaceCraft;
using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// What meteors can bring on the current planet, read from the planet's meteor events.
    /// </summary>
    internal static class PlanetMeteors
    {
        static readonly AccessTools.FieldRef<MeteoHandler, List<MeteoEventData>> fMeteoHandlerMeteoEvents =
            AccessTools.FieldRefAccess<MeteoHandler, List<MeteoEventData>>("_meteoEvents");

        /// <summary>
        /// Resource group ids of the current planet's meteors, plus the barren-meteor ores.
        /// </summary>
        internal static HashSet<string> ResourceIds()
        {
            var ids = new HashSet<string>();
            var mh = Managers.GetManager<MeteoHandler>();
            var events = mh != null ? fMeteoHandlerMeteoEvents(mh) : null;
            foreach (var me in events ?? [])
            {
                var groups = me?.asteroidEventData?.asteroidGameObject?.GetComponent<Asteroid>()?.groupsSelected;
                foreach (var gd in groups ?? [])
                {
                    if (gd != null)
                    {
                        ids.Add(gd.id);
                    }
                }
            }
            foreach (var id in Content.BarrenLoot)
            {
                ids.Add(id);
            }
            return ids;
        }
    }
}
