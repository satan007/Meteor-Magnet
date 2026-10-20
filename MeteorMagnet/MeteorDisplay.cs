using HarmonyLib;
using SpaceCraft;
using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// The meteor network display: a copy of one of the game's screens that open their own interface
    /// (like the orbital information screen). Using it opens our statistics window instead of the game's one;
    /// nothing is written on the screen itself.
    /// </summary>
    internal static class MeteorDisplay
    {
        /// <summary>The statistics, refreshed with the network (about every second).</summary>
        internal static readonly List<string> Lines = [];

        internal static void Reset()
        {
            Lines.Clear();
        }

        internal static void Update(List<string> lines)
        {
            Lines.Clear();
            Lines.AddRange(lines);
        }

        internal static bool IsDisplay(WorldObject wo) => wo?.GetGroup()?.GetId() == Content.Display.id;

        /// <summary>Whether a screen model opens an interface when used: those make good displays.</summary>
        internal static bool OpensUi(GameObject prefab) =>
            prefab != null && prefab.GetComponentInChildren<ActionOpenUi>(true) != null;

        /// <summary>Using our display opens the statistics window, not the interface of the screen it was copied from.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ActionOpenUi), nameof(ActionOpenUi.OnAction))]
        static bool ActionOpenUi_OnAction(ActionOpenUi __instance)
        {
            var wo = __instance.GetComponentInParent<WorldObjectAssociated>()?.GetWorldObject();
            if (Plugin.Enabled && wo?.GetGroup()?.GetId() == Content.StorageTerminal.id)
            {
                StorageTerminal.Open(wo);
                return false;
            }
            if (!Plugin.Enabled || !IsDisplay(wo))
            {
                return true;
            }
            Plugin.Log("Meteor display #" + wo.GetId() + " used: opening the statistics window");
            FilterWindow.OpenStats();
            return false;
        }
    }
}
