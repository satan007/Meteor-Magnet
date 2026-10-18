using HarmonyLib;
using SpaceCraft;
using System;
using System.Collections.Generic;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// The meteor beacon calls one of the planet's meteor showers that carry resources.
    /// It uses the game's own event queue, so the shower looks and behaves like a natural one.
    /// </summary>
    internal static class MeteorBeacon
    {
        static readonly AccessTools.FieldRef<MeteoHandler, List<MeteoEventData>> fMeteoEvents =
            AccessTools.FieldRefAccess<MeteoHandler, List<MeteoEventData>>("_meteoEvents");
        static readonly FieldInfo fQueue = AccessTools.Field(typeof(MeteoHandler), "_meteoEventQueue");
        static readonly FieldInfo fSelected = AccessTools.Field(typeof(MeteoHandler), "_selectedDataMeteoEventIndex");

        /// <summary>Whether a meteo event is running or waiting in the game's queue.</summary>
        static bool Busy(MeteoHandler mh, out string what)
        {
            what = "";
            if (fSelected?.GetValue(mh) is NetworkVariable<int> selected && selected.Value != -1)
            {
                what = "an event is running (index " + selected.Value + ")";
                return true;
            }
            if (fQueue?.GetValue(mh) is System.Collections.ICollection queue && queue.Count != 0)
            {
                what = queue.Count + " events queued";
                return true;
            }
            return false;
        }

        /// <summary>
        /// Queues a random resource shower of this planet. Returns false (with the reason) if it cannot right now.
        /// </summary>
        internal static bool TrySummon(bool commonOnly, out string result)
        {
            var mh = Managers.GetManager<MeteoHandler>();
            if (mh == null)
            {
                result = "no MeteoHandler";
                return false;
            }
            if (Busy(mh, out var what))
            {
                result = "waiting: " + what;
                return false;
            }
            var showers = new List<MeteoEventData>();
            foreach (var me in fMeteoEvents(mh) ?? [])
            {
                var groups = me?.asteroidEventData?.asteroidGameObject?.GetComponent<Asteroid>()?.groupsSelected;
                if (groups != null && groups.Count != 0
                    && (!commonOnly || !groups.Exists(g => g != null && Content.RareResources.Contains(g.id))))
                {
                    showers.Add(me);
                }
            }
            if (showers.Count == 0)
            {
                result = "this planet has no meteor showers with " + (commonOnly ? "common " : "") + "resources";
                return false;
            }
            var pick = showers[UnityEngine.Random.Range(0, showers.Count)];
            try
            {
                mh.QueueMeteoEvent(pick);
            }
            catch (Exception ex)
            {
                result = "QueueMeteoEvent failed: " + ex.Message;
                return false;
            }
            var names = showers.ConvertAll(e => e.name);
            result = "queued " + pick.name + " (out of " + string.Join(", ", names) + ")";
            return true;
        }
    }
}
