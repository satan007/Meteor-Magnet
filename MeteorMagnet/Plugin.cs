using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using SpaceCraft;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Adds a buildable "Meteor Magnet": a storage device that pulls incoming meteors
    /// towards itself and stores all of their resources in its own inventory,
    /// so nothing needs to be picked up from the ground.
    /// </summary>
    [BepInPlugin("satan007.theplanetcrafter.meteormagnet", "Meteor Magnet", PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string MagnetGroupId = "MeteorMagnet";

        // The building is a clone of the big storage container so it gets an inventory for free.
        const string BaseGroupId = "Container2";

        static ManualLogSource logger;

        static ConfigEntry<bool> modEnabled;
        static ConfigEntry<float> captureRadius;
        static ConfigEntry<bool> captureEverywhere;
        static ConfigEntry<float> lootMultiplier;
        static ConfigEntry<int> minLootPerMeteor;
        static ConfigEntry<bool> pullMeteors;
        static ConfigEntry<float> pullTurnRate;
        static ConfigEntry<float> impactOffset;
        static ConfigEntry<bool> leaveAsteroidBody;
        static ConfigEntry<bool> shakeOnImpact;
        static ConfigEntry<string> recipe;
        static ConfigEntry<float> unlockTerraformation;
        static ConfigEntry<bool> showNotification;
        static ConfigEntry<bool> debugLog;

        static AccessTools.FieldRef<Asteroid, bool> fAsteroidHasCrashed;

        static string pluginDir;

        public void Awake()
        {
            logger = Logger;
            pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            modEnabled = Config.Bind("General", "Enabled", true, "Включён ли мод.");
            debugLog = Config.Bind("General", "DebugLog", false, "Подробный лог в BepInEx/LogOutput.log.");

            captureRadius = Config.Bind("Capture", "Radius", 400f, "Радиус захвата в метрах (считается по горизонтали от устройства до точки падения метеорита).");
            captureEverywhere = Config.Bind("Capture", "Everywhere", false, "Захватывать вообще все метеориты на планете, независимо от радиуса.");
            lootMultiplier = Config.Bind("Capture", "LootMultiplier", 1f, "Множитель количества ресурсов с одного метеорита.");
            minLootPerMeteor = Config.Bind("Capture", "MinLootPerMeteor", 1, "Минимум ресурсов с одного метеорита (если у метеорита вообще есть ресурсы).");
            showNotification = Config.Bind("Capture", "ShowNotification", true, "Показывать сообщение при захвате метеорита.");

            pullMeteors = Config.Bind("Visual", "PullMeteors", true, "Визуально притягивать метеорит к устройству (он упадёт рядом с ним). Если выключить, метеорит упадёт где падал, но пустым.");
            pullTurnRate = Config.Bind("Visual", "PullTurnRate", 90f, "Скорость поворота метеорита к устройству, градусов в секунду.");
            impactOffset = Config.Bind("Visual", "ImpactOffset", 25f, "На каком расстоянии от устройства падает притянутый метеорит (чтобы не бил прямо в базу).");
            leaveAsteroidBody = Config.Bind("Visual", "LeaveAsteroidBody", false, "Оставлять ли тело метеорита/кратер на месте падения.");
            shakeOnImpact = Config.Bind("Visual", "ShakeOnImpact", false, "Тряска камеры при падении захваченного метеорита.");

            recipe = Config.Bind("Building", "Recipe", "Alloy,Alloy,Titanium,Titanium,Iron,Iron,Rod-uranium",
                "Рецепт устройства: id предметов через запятую. Неизвестные id пропускаются.");
            unlockTerraformation = Config.Bind("Building", "UnlockAtTerraformation", 0f,
                "Индекс терраформации (Ti), при котором открывается устройство. 0 = доступно сразу.");

            fAsteroidHasCrashed = TryFieldRef<Asteroid, bool>("hasCrashed");

            var harmony = new Harmony("satan007.theplanetcrafter.meteormagnet");
            harmony.PatchAll(typeof(Plugin));

            var go = new GameObject("MeteorMagnet_Controller");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MagnetController>();

            Logger.LogInfo("Meteor Magnet loaded.");
        }

        internal static bool Enabled => modEnabled.Value;
        internal static float CaptureRadius => captureRadius.Value;
        internal static bool CaptureEverywhere => captureEverywhere.Value;
        internal static float LootMultiplier => lootMultiplier.Value;
        internal static int MinLootPerMeteor => minLootPerMeteor.Value;
        internal static bool PullMeteors => pullMeteors.Value;
        internal static float PullTurnRate => pullTurnRate.Value;
        internal static float ImpactOffset => impactOffset.Value;
        internal static bool LeaveAsteroidBody => leaveAsteroidBody.Value;
        internal static bool ShakeOnImpact => shakeOnImpact.Value;
        internal static bool ShowNotification => showNotification.Value;

        internal static bool HasCrashed(Asteroid a) => fAsteroidHasCrashed != null && fAsteroidHasCrashed(a);
        internal static bool CanDetectCrash => fAsteroidHasCrashed != null;

        internal static void Log(string s)
        {
            if (debugLog.Value)
            {
                logger.LogInfo(s);
            }
        }

        internal static void LogWarning(string s) => logger.LogWarning(s);

        static AccessTools.FieldRef<T, F> TryFieldRef<T, F>(string name)
        {
            if (AccessTools.Field(typeof(T), name) == null)
            {
                logger.LogWarning("Field " + typeof(T).Name + "." + name + " not found; falling back to timeout-based detection.");
                return null;
            }
            return AccessTools.FieldRefAccess<T, F>(name);
        }

        // ---------------------------------------------------------------
        // Building registration
        // ---------------------------------------------------------------

        [HarmonyPrefix]
        [HarmonyPatch(typeof(StaticDataHandler), "LoadStaticData")]
        static void StaticDataHandler_LoadStaticData(List<GroupData> ___groupsData)
        {
            if (___groupsData.Any(g => g != null && g.id == MagnetGroupId))
            {
                return;
            }

            if (___groupsData.Find(g => g != null && g.id == BaseGroupId) is not GroupDataConstructible baseGroup)
            {
                logger.LogError("Base building " + BaseGroupId + " not found, Meteor Magnet will not be available.");
                return;
            }

            var magnet = Instantiate(baseGroup);
            magnet.name = MagnetGroupId;
            magnet.id = MagnetGroupId;
            magnet.terraformStageUnlock = null;
            magnet.unlockingWorldUnit = DataConfig.WorldUnitType.Terraformation;
            magnet.unlockingValue = unlockTerraformation.Value;
            magnet.tradeValue = 0;

            magnet.recipeIngredients = [];
            foreach (var raw in recipe.Value.Split(','))
            {
                var id = raw.Trim();
                if (id.Length == 0)
                {
                    continue;
                }
                if (___groupsData.Find(g => g != null && g.id == id) is GroupDataItem ingredient)
                {
                    magnet.recipeIngredients.Add(ingredient);
                }
                else
                {
                    logger.LogWarning("Recipe ingredient '" + id + "' not found, skipping.");
                }
            }

            var icon = LoadIcon("MeteorMagnet.png");
            if (icon != null)
            {
                magnet.icon = icon;
            }

            ___groupsData.Add(magnet);
            Log("Registered building " + MagnetGroupId + " with " + magnet.recipeIngredients.Count + " ingredients.");
        }

        static Sprite LoadIcon(string file)
        {
            var path = Path.Combine(pluginDir, file);
            if (!File.Exists(path))
            {
                return null;
            }
            var tex = new Texture2D(1, 1);
            tex.LoadImage(File.ReadAllBytes(path));
            tex.name = "MeteorMagnetIcon";
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UnlockedGroupsHandler), "SetUnlockedGroups")]
        static void UnlockedGroupsHandler_SetUnlockedGroups(NetworkList<int> ____unlockedGroups)
        {
            if (unlockTerraformation.Value > 0f)
            {
                return;
            }
            var group = GroupsHandler.GetGroupViaId(MagnetGroupId);
            if (group != null && !____unlockedGroups.Contains(group.stableHashCode))
            {
                ____unlockedGroups.Add(group.stableHashCode);
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Localization), "LoadLocalization")]
        static void Localization_LoadLocalization(Dictionary<string, Dictionary<string, string>> ___localizationDictionary)
        {
            foreach (var kv in ___localizationDictionary)
            {
                if (kv.Key == "russian")
                {
                    kv.Value["GROUP_NAME_" + MagnetGroupId] = "Гравитационный захват";
                    kv.Value["GROUP_DESC_" + MagnetGroupId] = "Притягивает падающие метеориты и складывает все их ресурсы в своё хранилище.";
                    kv.Value["MeteorMagnet_Captured"] = "Метеорит захвачен";
                }
                else
                {
                    kv.Value["GROUP_NAME_" + MagnetGroupId] = "Meteor Magnet";
                    kv.Value["GROUP_DESC_" + MagnetGroupId] = "Pulls incoming meteors in and stores all of their resources in its own storage.";
                    kv.Value["MeteorMagnet_Captured"] = "Meteor captured";
                }
            }
        }
    }
}
