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
    /// Adds the buildable "Meteor Magnet" in three tiers: storage devices that pull incoming meteors
    /// towards themselves and store all of their resources, so nothing needs to be picked up from the ground.
    /// Planet-exclusive resources can be crafted into resonators that give a magnet an extra ability.
    /// </summary>
    [BepInPlugin("satan007.theplanetcrafter.meteormagnet", "Meteor Magnet", PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        // Resonators are clones of this item (it is craftable and has a small model).
        const string ResonatorBaseGroupId = "Rod-uranium";

        static ManualLogSource logger;

        static ConfigEntry<bool> modEnabled;
        static ConfigEntry<float> lootMultiplier;
        static ConfigEntry<int> minLootPerMeteor;
        static ConfigEntry<bool> pullMeteors;
        static ConfigEntry<float> pullTurnRate;
        static ConfigEntry<float> impactOffset;
        static ConfigEntry<bool> leaveAsteroidBody;
        static ConfigEntry<bool> shakeOnImpact;
        static ConfigEntry<bool> showNotification;
        static ConfigEntry<bool> debugLog;
        static ConfigEntry<string> filterKey;
        static ConfigEntry<string> roverUpgradeItems;

        static readonly Dictionary<string, ConfigEntry<string>> recipes = [];
        static readonly Dictionary<string, ConfigEntry<float>> tierUnlocks = [];

        static AccessTools.FieldRef<Asteroid, bool> fAsteroidHasCrashed;

        static string pluginDir;

        public void Awake()
        {
            logger = Logger;
            pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            modEnabled = Config.Bind("General", "Enabled", true, "Включён ли мод.");
            debugLog = Config.Bind("General", "DebugLog", false, "Подробный лог в BepInEx/LogOutput.log.");

            lootMultiplier = Config.Bind("Capture", "LootMultiplier", 1f, "Множитель количества ресурсов с одного метеорита.");
            minLootPerMeteor = Config.Bind("Capture", "MinLootPerMeteor", 1, "Минимум ресурсов с одного метеорита (если у метеорита вообще есть ресурсы).");
            showNotification = Config.Bind("Capture", "ShowNotification", true, "Показывать сообщение при захвате метеорита.");
            filterKey = Config.Bind("Capture", "FilterKey", "<Keyboard>/n", "Клавиша окна фильтра: наведитесь на захват Mk2/Mk3 и нажмите.");

            pullMeteors = Config.Bind("Visual", "PullMeteors", true, "Визуально притягивать метеорит к устройству (он упадёт рядом с ним). Если выключить, метеорит упадёт где падал, но пустым.");
            pullTurnRate = Config.Bind("Visual", "PullTurnRate", 90f, "Скорость поворота метеорита к устройству, градусов в секунду.");
            impactOffset = Config.Bind("Visual", "ImpactOffset", 25f, "На каком расстоянии от устройства падает притянутый метеорит (чтобы не бил прямо в базу).");
            leaveAsteroidBody = Config.Bind("Visual", "LeaveAsteroidBody", false, "Оставлять ли тело метеорита/кратер на месте падения.");
            shakeOnImpact = Config.Bind("Visual", "ShakeOnImpact", false, "Тряска камеры при падении захваченного метеорита.");

            foreach (var tier in Content.Tiers)
            {
                var section = "Tier Mk" + tier.level;
                recipes[tier.id] = Config.Bind(section, "Recipe", tier.recipe, "Рецепт: id предметов через запятую. Неизвестные id пропускаются.");
                tierUnlocks[tier.id] = Config.Bind(section, "UnlockAtTerraformation", tier.unlockTi, "Индекс терраформации (Ti), при котором открывается этот тир. 0 = доступно сразу.");
            }
            roverUpgradeItems = Config.Bind("RoverUpgrades", "Items", "",
                "Улучшения ровера определяются автоматически по английскому названию. Если какое-то не определилось, впишите его id вручную: "
                + "Id=Тип:Тир через запятую, типы: Speed, Storage, Lights, Beacon, Oxygen, Equipment, Logistics. Пример: VehicleSpeed1=Speed:1,VehicleInventory2=Storage:2");

            foreach (var res in Content.Resonators)
            {
                recipes[res.id] = Config.Bind("Resonator " + res.planet, "Recipe", res.recipe, "Рецепт резонатора: id предметов через запятую. Неизвестные id пропускаются.");
            }

            fAsteroidHasCrashed = TryFieldRef<Asteroid, bool>("hasCrashed");

            var harmony = new Harmony("satan007.theplanetcrafter.meteormagnet");
            harmony.PatchAll(typeof(Plugin));

            var go = new GameObject("MeteorMagnet_Controller");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MagnetController>();
            go.AddComponent<FilterWindow>();

            Logger.LogInfo("Meteor Magnet loaded.");
        }

        internal static bool Enabled => modEnabled.Value;
        internal static float LootMultiplier => lootMultiplier.Value;
        internal static int MinLootPerMeteor => minLootPerMeteor.Value;
        internal static bool PullMeteors => pullMeteors.Value;
        internal static float PullTurnRate => pullTurnRate.Value;
        internal static float ImpactOffset => impactOffset.Value;
        internal static bool LeaveAsteroidBody => leaveAsteroidBody.Value;
        internal static bool ShakeOnImpact => shakeOnImpact.Value;
        internal static bool ShowNotification => showNotification.Value;
        internal static string FilterKey => filterKey.Value.Contains("<") ? filterKey.Value : "<Keyboard>/" + filterKey.Value;

        internal static bool HasCrashed(Asteroid a) => fAsteroidHasCrashed != null && fAsteroidHasCrashed(a);

        internal static void Log(string s)
        {
            if (debugLog.Value)
            {
                logger.LogInfo(s);
            }
        }

        internal static void LogWarning(string s) => logger.LogWarning(s);

        internal static void LogInfo(string s) => logger.LogInfo(s);

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
            RoverUpgrades.Detect(___groupsData, roverUpgradeItems.Value);

            var existing = ___groupsData.Where(g => g != null).Select(g => g.id).ToHashSet();

            foreach (var tier in Content.Tiers)
            {
                if (existing.Contains(tier.id))
                {
                    continue;
                }
                GroupDataConstructible baseGroup = null;
                foreach (var baseId in tier.baseGroupIds)
                {
                    baseGroup = ___groupsData.Find(g => g != null && g.id == baseId) as GroupDataConstructible;
                    if (baseGroup != null)
                    {
                        break;
                    }
                }
                if (baseGroup == null)
                {
                    logger.LogError("No base building for " + tier.id + ", it will not be available.");
                    continue;
                }

                tier.resolvedBaseId = baseGroup.id;
                var magnet = Instantiate(baseGroup);
                SetupClone(magnet, tier.id, ___groupsData);
                magnet.unlockingValue = tierUnlocks[tier.id].Value;
                ___groupsData.Add(magnet);
                Log("Registered building " + tier.id + " (base " + baseGroup.id + ")");
            }

            if (___groupsData.Find(g => g != null && g.id == ResonatorBaseGroupId) is not GroupDataItem resonatorBase)
            {
                logger.LogError("Base item " + ResonatorBaseGroupId + " not found, resonators will not be available.");
                return;
            }
            foreach (var res in Content.Resonators)
            {
                if (existing.Contains(res.id))
                {
                    continue;
                }
                var item = Instantiate(resonatorBase);
                SetupClone(item, res.id, ___groupsData);
                item.unlockingValue = 0f;
                ___groupsData.Add(item);
                Log("Registered resonator " + res.id);
            }
        }

        static void SetupClone(GroupData gd, string id, List<GroupData> groupsData)
        {
            gd.name = id;
            gd.id = id;
            gd.terraformStageUnlock = null;
            gd.unlockingWorldUnit = DataConfig.WorldUnitType.Terraformation;
            if (gd is GroupDataItem item)
            {
                item.tradeValue = 0;
            }

            gd.recipeIngredients = [];
            foreach (var raw in recipes[id].Value.Split(','))
            {
                var ingredientId = raw.Trim();
                if (ingredientId.Length == 0)
                {
                    continue;
                }
                if (groupsData.Find(g => g != null && g.id == ingredientId) is GroupDataItem ingredient)
                {
                    gd.recipeIngredients.Add(ingredient);
                }
                else
                {
                    logger.LogWarning(id + ": recipe ingredient '" + ingredientId + "' not found, skipping.");
                }
            }

            var icon = LoadIcon(id + ".png");
            if (icon != null)
            {
                gd.icon = icon;
            }
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
            tex.name = Path.GetFileNameWithoutExtension(file);
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(UnlockedGroupsHandler), "SetUnlockedGroups")]
        static void UnlockedGroupsHandler_SetUnlockedGroups(NetworkList<int> ____unlockedGroups)
        {
            var ids = Content.Tiers.Where(t => tierUnlocks[t.id].Value <= 0f).Select(t => t.id)
                .Concat(Content.Resonators.Select(r => r.id));
            foreach (var id in ids)
            {
                var group = GroupsHandler.GetGroupViaId(id);
                if (group != null && !____unlockedGroups.Contains(group.stableHashCode))
                {
                    ____unlockedGroups.Add(group.stableHashCode);
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Localization), "LoadLocalization")]
        static void Localization_LoadLocalization(Dictionary<string, Dictionary<string, string>> ___localizationDictionary)
        {
            if (___localizationDictionary.TryGetValue("english", out var english))
            {
                RoverUpgrades.DetectByName(english);
            }

            foreach (var kv in ___localizationDictionary)
            {
                var ru = kv.Key == "russian";
                var dict = kv.Value;
                foreach (var t in Content.Tiers)
                {
                    dict["GROUP_NAME_" + t.id] = ru ? t.nameRu : t.nameEn;
                    dict["GROUP_DESC_" + t.id] = ru ? t.descRu : t.descEn;
                }
                foreach (var r in Content.Resonators)
                {
                    dict["GROUP_NAME_" + r.id] = ru ? r.nameRu : r.nameEn;
                    dict["GROUP_DESC_" + r.id] = ru ? r.descRu : r.descEn;
                }
                dict["MeteorMagnet_Captured"] = ru ? "Метеорит захвачен" : "Meteor captured";
                dict["MeteorMagnet_Stored"] = ru ? "в хранилище" : "stored";
                dict["MeteorMagnet_Dropped"] = ru ? "рядом с устройством" : "dropped nearby";
                dict["MeteorMagnet_FilterTitle"] = ru ? "Фильтр гравитационного захвата" : "Meteor Magnet filter";
                dict["MeteorMagnet_FilterHint"] = ru
                    ? "Отмеченные ресурсы идут в хранилище, остальные падают рядом. Ничего не отмечено — в хранилище идёт всё."
                    : "Checked resources go to storage, the rest drop nearby. Nothing checked = everything goes to storage.";
                dict["MeteorMagnet_FilterAll"] = ru ? "Всё в хранилище" : "Store everything";
                dict["MeteorMagnet_FilterClose"] = ru ? "Закрыть" : "Close";
                dict["MeteorMagnet_FilterEmpty"] = ru ? "На этой планете метеориты с ресурсами не найдены." : "No resource meteors found on this planet.";
            }
        }
    }
}
