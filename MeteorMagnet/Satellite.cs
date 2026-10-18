using HarmonyLib;
using SpaceCraft;
using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Remembers over which planets a meteor satellite rocket was launched.
    /// The list lives in the text of a hidden world object, so it is saved with the world.
    /// </summary>
    internal static class Satellite
    {
        // A fixed id far away from the game's own ids, like other mods do for hidden storage objects.
        const int StoreWorldObjectId = 905_150_001;
        const float LaunchButtonRange = 30f;

        internal static bool IsLaunched(int planetHash)
        {
            var wo = WorldObjectsHandler.Instance?.GetWorldObjectViaId(StoreWorldObjectId);
            return wo != null && Parse(wo.GetText()).Contains(planetHash);
        }

        static void MarkLaunched(int planetHash)
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
            var planets = Parse(wo.GetText());
            planets.Add(planetHash);
            wo.SetText(string.Join(",", planets));
        }

        static HashSet<int> Parse(string text)
        {
            var result = new HashSet<int>();
            foreach (var part in (text ?? "").Split(','))
            {
                if (int.TryParse(part.Trim(), out var h))
                {
                    result.Add(h);
                }
            }
            return result;
        }

        /// <summary>
        /// The launch button was pressed: if one of our satellite rockets stands ready next to it,
        /// it is the one going up, so the planet gets its satellite.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ActionSendInSpace), nameof(ActionSendInSpace.OnAction))]
        static void ActionSendInSpace_OnAction(ActionSendInSpace __instance)
        {
            if (!Plugin.Enabled || (NetworkManager.Singleton != null && !NetworkManager.Singleton.IsServer))
            {
                return;
            }
            try
            {
                var buttonPos = __instance.transform.position;
                foreach (var wo in WorldObjectsHandler.Instance.GetAllWorldObjects().Values)
                {
                    if (wo?.GetGroup()?.GetId() != Content.SatelliteRocket.id || !wo.GetIsPlaced())
                    {
                        continue;
                    }
                    var go = wo.GetGameObject();
                    // Not built yet (still a ghost) or far from this button: not the rocket being launched.
                    if (go == null || !go.activeSelf || go.GetComponent<GhostFx>() != null
                        || Vector3.Distance(go.transform.position, buttonPos) > LaunchButtonRange)
                    {
                        continue;
                    }
                    var planetHash = wo.GetPlanetHash();
                    if (planetHash == 0)
                    {
                        planetHash = Managers.GetManager<PlanetLoader>()?.GetCurrentPlanetData()?.GetPlanetHash() ?? 0;
                    }
                    MarkLaunched(planetHash);
                    Plugin.Log("Meteor satellite launched over planet " + planetHash);
                    Managers.GetManager<DisplayersHandler>()?.GetInformationsDisplayer()
                        ?.AddInformation(4f, Localization.GetLocalizedString("MeteorMagnet_SatelliteLaunched"),
                            DataConfig.UiInformationsType.OutInventory, wo.GetGroup().GetImage());
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogWarning("Satellite launch check failed: " + ex.Message);
            }
        }
    }
}
