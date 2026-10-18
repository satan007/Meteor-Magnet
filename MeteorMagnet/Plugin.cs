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
        static ConfigEntry<bool> showSignalLines;
        static ConfigEntry<bool> showNotification;
        static ConfigEntry<bool> debugLog;
        static ConfigEntry<string> filterKey;
        static ConfigEntry<string> moduleKey;
        static ConfigEntry<string> roverUpgradeItems;
        static ConfigEntry<string> baseRocketId;

        static readonly Dictionary<string, ConfigEntry<string>> recipes = [];
        static readonly Dictionary<string, ConfigEntry<float>> unlocks = [];

        static AccessTools.FieldRef<Asteroid, bool> fAsteroidHasCrashed;
        static FieldInfo fAsteroidMaxAcceleration;

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
            moduleKey = Config.Bind("Capture", "ModuleKey", "<Keyboard>/k", "Клавиша ячеек модулей: наведитесь на сканер или захват и нажмите.");

            pullMeteors = Config.Bind("Visual", "PullMeteors", true, "Визуально притягивать метеорит к устройству (он упадёт рядом с ним). Если выключить, метеорит упадёт где падал, но пустым.");
            pullTurnRate = Config.Bind("Visual", "PullTurnRate", 90f, "Скорость поворота метеорита к устройству, градусов в секунду.");
            impactOffset = Config.Bind("Visual", "ImpactOffset", 25f, "На каком расстоянии от устройства падает притянутый метеорит (чтобы не бил прямо в базу).");
            leaveAsteroidBody = Config.Bind("Visual", "LeaveAsteroidBody", false, "Оставлять ли тело метеорита/кратер на месте падения.");
            shakeOnImpact = Config.Bind("Visual", "ShakeOnImpact", false, "Тряска камеры при падении захваченного метеорита.");
            showSignalLines = Config.Bind("Visual", "ShowSignalLines", true, "Показывать линии сигнала: сканер → реле → захват.");

            foreach (var def in Content.Buildings)
            {
                recipes[def.id] = Config.Bind(def.ConfigSection, "Recipe", def.recipe, "Рецепт: id предметов через запятую. Неизвестные id пропускаются.");
                unlocks[def.id] = Config.Bind(def.ConfigSection, "UnlockAtTerraformation", def.unlockTi, "Индекс терраформации (Ti), при котором открывается эта постройка. 0 = доступно сразу.");
            }
            roverUpgradeItems = Config.Bind("RoverUpgrades", "Items", "",
                "Улучшения ровера определяются автоматически по английскому названию. Если какое-то не определилось, впишите его id вручную: "
                + "Id=Тип:Тир через запятую, типы: Speed, Storage, Lights, Beacon, Oxygen, Equipment, Logistics, Jetpack. Пример: VehicleSpeed1=Speed:1,VehicleInventory2=Storage:2");

            foreach (var res in Content.Resonators)
            {
                recipes[res.id] = Config.Bind(res.ConfigSection, "Recipe", res.recipe, "Рецепт модуля: id предметов через запятую. Неизвестные id пропускаются.");
            }

            var rocket = Content.SatelliteRocket;
            recipes[rocket.id] = Config.Bind("Satellite Rocket", "Recipe", rocket.recipe, "Рецепт спутниковой ракеты: id предметов через запятую. Неизвестные id пропускаются.");
            unlocks[rocket.id] = Config.Bind("Satellite Rocket", "UnlockAtTerraformation", rocket.unlockTi, "Индекс терраформации (Ti), при котором открывается ракета. 0 = доступно сразу.");
            baseRocketId = Config.Bind("Satellite Rocket", "BaseRocket", "",
                "Id игровой ракеты, которую копирует спутниковая. Пусто = найти автоматически (сначала GPS/навигационный спутник). Выбранная основа пишется в лог.");

            fAsteroidHasCrashed = TryFieldRef<Asteroid, bool>("hasCrashed");
            fAsteroidMaxAcceleration = AccessTools.Field(typeof(Asteroid), "maxAcceleration");
            if (fAsteroidMaxAcceleration == null || fAsteroidMaxAcceleration.FieldType != typeof(float))
            {
                Logger.LogWarning("Asteroid.maxAcceleration not found; pulled meteors will keep accelerating.");
                fAsteroidMaxAcceleration = null;
            }

            var harmony = new Harmony("satan007.theplanetcrafter.meteormagnet");
            harmony.PatchAll(typeof(Plugin));
            harmony.PatchAll(typeof(Satellite));

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
        internal static bool ShowSignalLines => showSignalLines.Value;
        internal static bool ShowNotification => showNotification.Value;
        internal static string FilterKey => filterKey.Value.Contains("<") ? filterKey.Value : "<Keyboard>/" + filterKey.Value;
        internal static string ModuleKey => moduleKey.Value.Contains("<") ? moduleKey.Value : "<Keyboard>/" + moduleKey.Value;

        internal static bool HasCrashed(Asteroid a) => fAsteroidHasCrashed != null && fAsteroidHasCrashed(a);

        /// <summary>
        /// A pulled meteor flies a longer way than a vanilla one; without this it builds up so much speed
        /// that it passes through the ground in a single frame.
        /// </summary>
        internal static void StopAcceleration(Asteroid a)
        {
            if (fAsteroidMaxAcceleration != null)
            {
                Log("Meteor maxAcceleration " + fAsteroidMaxAcceleration.GetValue(a) + " -> 0");
                fAsteroidMaxAcceleration.SetValue(a, 0f);
            }
        }

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

            foreach (var def in Content.Buildings)
            {
                if (existing.Contains(def.id))
                {
                    continue;
                }
                GroupDataConstructible baseGroup = null;
                foreach (var baseId in def.baseGroupIds)
                {
                    baseGroup = ___groupsData.Find(g => g != null && g.id == baseId) as GroupDataConstructible;
                    if (baseGroup != null)
                    {
                        break;
                    }
                }
                if (baseGroup == null)
                {
                    logger.LogError("No base building for " + def.id + ", it will not be available.");
                    continue;
                }

                def.resolvedBaseId = baseGroup.id;
                var building = Instantiate(baseGroup);
                SetupClone(building, def.id, ___groupsData);
                building.unlockingValue = unlocks[def.id].Value;
                ___groupsData.Add(building);
                Log("Registered building " + def.id + " (base " + baseGroup.id + ")");
            }

            RegisterSatelliteRocket(___groupsData, existing);

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
                item.craftableInList = [res.craftStation];
                ___groupsData.Add(item);
                Log("Registered resonator " + res.id);
            }
        }

        /// <summary>
        /// The satellite rocket is a clone of one of the game's rockets, so it is crafted and launched the same way.
        /// Its terraformation multipliers are zeroed: launching it only puts the meteor satellite into orbit.
        /// </summary>
        static void RegisterSatelliteRocket(List<GroupData> groupsData, HashSet<string> existing)
        {
            var id = Content.SatelliteRocket.id;
            if (existing.Contains(id))
            {
                return;
            }
            var baseRocket = PickBaseRocket(groupsData);
            if (baseRocket == null)
            {
                logger.LogError("No rocket found to base the satellite rocket on; it will not be available.");
                return;
            }
            var rocket = Instantiate(baseRocket);
            SetupClone(rocket, id, groupsData);
            rocket.unlockingValue = unlocks[id].Value;

            foreach (var f in MultiplierFields(rocket))
            {
                f.SetValue(rocket, 0f);
            }
            groupsData.Add(rocket);
            logger.LogInfo("Satellite rocket is based on " + baseRocket.id);
        }

        /// <summary>
        /// The game's GPS/navigation satellite rocket is the natural base: it changes no world units, it only
        /// unlocks things. Its id is not known in advance, so prefer a rocket without terraforming multipliers
        /// whose id hints at a map or satellite, then any rocket without multipliers, then any rocket.
        /// </summary>
        static GroupData PickBaseRocket(List<GroupData> groupsData)
        {
            var configured = baseRocketId.Value.Trim();
            if (configured.Length != 0)
            {
                var found = groupsData.Find(g => g != null && g.id == configured);
                if (found != null)
                {
                    return found;
                }
                logger.LogWarning("BaseRocket '" + configured + "' not found, picking automatically.");
            }

            var rockets = groupsData.Where(g => g != null && g.id != null
                && g.id.StartsWith("Rocket", StringComparison.Ordinal)
                && !g.id.StartsWith("RocketReactor", StringComparison.Ordinal)).ToList();
            string[] hints = ["gps", "map", "satellite", "nav"];
            var plain = rockets.Where(r => MultiplierFields(r).All(f => (float)f.GetValue(r) == 0f)).ToList();
            return plain.FirstOrDefault(r => hints.Any(h => r.id.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0))
                ?? plain.FirstOrDefault()
                ?? rockets.FirstOrDefault();
        }

        static IEnumerable<FieldInfo> MultiplierFields(GroupData gd)
        {
            for (var t = gd.GetType(); t != null && t != typeof(ScriptableObject); t = t.BaseType)
            {
                foreach (var f in AccessTools.GetDeclaredFields(t))
                {
                    if (f.FieldType == typeof(float) && f.Name.IndexOf("multiplier", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        yield return f;
                    }
                }
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
            var ids = Content.Buildings.Where(b => unlocks[b.id].Value <= 0f).Select(b => b.id)
                .Concat(unlocks[Content.SatelliteRocket.id].Value <= 0f ? new[] { Content.SatelliteRocket.id } : Array.Empty<string>())
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
                foreach (var b in Content.Buildings)
                {
                    dict["GROUP_NAME_" + b.id] = ru ? b.nameRu : b.nameEn;
                    dict["GROUP_DESC_" + b.id] = ru ? b.descRu : b.descEn;
                }
                foreach (var r in Content.Resonators)
                {
                    dict["GROUP_NAME_" + r.id] = ru ? r.nameRu : r.nameEn;
                    dict["GROUP_DESC_" + r.id] = ru ? r.descRu : r.descEn;
                }
                var sat = Content.SatelliteRocket;
                dict["GROUP_NAME_" + sat.id] = ru ? sat.nameRu : sat.nameEn;
                dict["GROUP_DESC_" + sat.id] = ru ? sat.descRu : sat.descEn;
                dict["MeteorMagnet_SatelliteLaunched"] = ru ? "Метеорный спутник выведен на орбиту" : "Meteor satellite is in orbit";
                dict["MeteorMagnet_Captured"] = ru ? "Метеорит захвачен" : "Meteor captured";
                dict["MeteorMagnet_Stored"] = ru ? "в хранилище" : "stored";
                dict["MeteorMagnet_Slipped"] = ru ? "Захват сорвался, метеорит падает как обычно" : "Capture slipped, the meteor falls as usual";
                dict["MeteorMagnet_Deflected"] = ru ? "Сбой поля! Метеорит отброшен в сторону" : "Field failure! The meteor was thrown away";
                dict["MeteorMagnet_Dropped"] = ru ? "рядом с устройством" : "dropped nearby";
                dict["MeteorMagnet_ModulesNotReady"] = ru ? "Ячейки модулей создаются, попробуйте ещё раз" : "Module slots are being created, try again";
                dict["MeteorMagnet_NoScanner"] = ru ? "Гравитационный захват не в зоне связи сканера или реле — он не будет работать" : "Meteor Magnet is outside any scanner or relay link area - it will not work";
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
