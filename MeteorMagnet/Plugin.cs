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
        static ConfigEntry<float> signalLineWidth;
        static ConfigEntry<float> signalLineOpacity;
        static ConfigEntry<bool> showMeteorLines;
        static ConfigEntry<bool> captureEffects;
        static ConfigEntry<bool> requirePower;
        static ConfigEntry<string> displayBaseScreen;
        static ConfigEntry<float> beaconInterval;
        static ConfigEntry<float> coreDropChance;
        static ConfigEntry<string> coreBaseItem;
        static ConfigEntry<int> coreTradeValue;
        static ConfigEntry<float> markerSeconds;
        static ConfigEntry<bool> routeByFilter;
        static ConfigEntry<bool> showNotification;
        static ConfigEntry<bool> debugLog;
        static ConfigEntry<string> filterKey;
        static ConfigEntry<string> moduleKey;
        static ConfigEntry<string> roverUpgradeItems;
        static ConfigEntry<string> playerGearItems;
        static ConfigEntry<string> baseRocketId;

        static readonly Dictionary<string, ConfigEntry<string>> recipes = [];
        static readonly Dictionary<string, ConfigEntry<float>> unlocks = [];
        static readonly Dictionary<string, ConfigEntry<float>> energyUse = [];

        static AccessTools.FieldRef<Asteroid, bool> fAsteroidHasCrashed;
        static FieldInfo fAsteroidMaxAcceleration;
        static FieldInfo fAsteroidInitialSpeed;

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
            filterKey = Config.Bind("Capture", "FilterKey", "<Keyboard>/n", "Клавиша окна фильтра: наведитесь на захват Mk2/Mk3 (что идёт в хранилище) или на сканер (какие метеориты он ведёт) и нажмите.");
            moduleKey = Config.Bind("Capture", "ModuleKey", "<Keyboard>/k", "Клавиша ячеек модулей: наведитесь на сканер или захват и нажмите.");
            routeByFilter = Config.Bind("Capture", "RouteByFilter", true, "Сканер отдаёт метеорит в первую очередь захвату, у которого в фильтре есть его ресурсы, потом захватам без фильтра, потом остальным.");
            requirePower = Config.Bind("Energy", "RequirePower", true, "Метеорная сеть работает, только пока на базе хватает энергии. Потребление каждой постройки задаётся в её разделе (EnergyUse).");

            pullMeteors = Config.Bind("Visual", "PullMeteors", true, "Визуально притягивать метеорит к устройству (он упадёт рядом с ним). Если выключить, метеорит упадёт где падал, но пустым.");
            pullTurnRate = Config.Bind("Visual", "PullTurnRate", 90f, "Скорость поворота метеорита к устройству, градусов в секунду.");
            impactOffset = Config.Bind("Visual", "ImpactOffset", 25f, "На каком расстоянии от устройства падает притянутый метеорит (чтобы не бил прямо в базу).");
            leaveAsteroidBody = Config.Bind("Visual", "LeaveAsteroidBody", false, "Оставлять ли тело метеорита/кратер на месте падения.");
            shakeOnImpact = Config.Bind("Visual", "ShakeOnImpact", false, "Тряска камеры при падении захваченного метеорита.");
            showSignalLines = Config.Bind("Visual", "ShowSignalLines", true, "Показывать линии сигнала: сканер → реле → захват.");
            signalLineWidth = Config.Bind("Visual", "SignalLineWidth", 0.03f, "Толщина линии сигнала в метрах (к концам линия сужается вдвое).");
            signalLineOpacity = Config.Bind("Visual", "SignalLineOpacity", 0.4f, "Непрозрачность линии сигнала: 0 — невидима, 1 — сплошная.");
            markerSeconds = Config.Bind("Visual", "MarkerSeconds", Content.MarkerLifetime, "Сколько секунд над упавшим непойманным (сорвавшимся или отброшенным) метеоритом висит метка: луч и значок вверху экрана. 0 = не показывать.");
            captureEffects = Config.Bind("Visual", "CaptureEffects", true, "Вспышка и след при захвате метеорита, вспышка при его падении.");
            showMeteorLines = Config.Bind("Visual", "ShowMeteorLines", true, "Отладка: лучи от сканера к метеориту, который он ведёт (бледный — идёт захват цели, яркий — цель захвачена), и от захвата к метеориту, который он тянет (красный — метеорит отбрасывается).");

            foreach (var def in Content.Buildings)
            {
                recipes[def.id] = Config.Bind(def.ConfigSection, "Recipe", def.recipe, "Рецепт: id предметов через запятую. Неизвестные id пропускаются.");
                unlocks[def.id] = Config.Bind(def.ConfigSection, "UnlockAtTerraformation", def.unlockTi, "Индекс терраформации (Ti), при котором открывается эта постройка. 0 = доступно сразу.");
                energyUse[def.id] = Config.Bind(def.ConfigSection, "EnergyUse", def.energy, "Потребление энергии постройкой (в единицах игры, кВт). Применяется после перезапуска игры.");
            }
            beaconInterval = Config.Bind(Content.Beacon.ConfigSection, "IntervalMinutes", Content.Beacon.intervalMinutes, "Как часто маяк вызывает метеоритный дождь, минут.");
            coreDropChance = Config.Bind("Meteor Core", "DropChance", Content.Core.dropChance, "Шанс, что пойманный метеорит оставит ядро (0.03 = 3%).");
            coreTradeValue = Config.Bind("Meteor Core", "TradeValue", -1, "Цена ядра в терратокенах для торговых ракет. -1 = в 20 раз дороже предмета, чью модель берёт ядро (иридий). 0 = не продаётся.");
            coreBaseItem = Config.Bind("Meteor Core", "BaseItem", "", "Id предмета игры, модель которого берёт ядро. Пусто = иридий (если нет — осмий, урановый стержень).");
            displayBaseScreen = Config.Bind("Display", "BaseScreen", "",
                "Id игрового экрана, который копирует дисплей. Пусто = выбрать автоматически. Найденные экраны пишутся в лог.");
            roverUpgradeItems = Config.Bind("RoverUpgrades", "Items", "",
                "Улучшения ровера определяются автоматически по английскому названию. Если какое-то не определилось, впишите его id вручную: "
                + "Id=Тип:Тир через запятую, типы: Speed, Storage, Lights, Beacon, Oxygen, Equipment, Logistics, Jetpack. Пример: VehicleSpeed1=Speed:1,VehicleInventory2=Storage:2");

            playerGearItems = Config.Bind("PlayerGear", "Items", "",
                "Экипировка игрока определяется автоматически по id игры (Backpack3, OxygenTank2, MultiToolMineSpeed4...). Если что-то не определилось, впишите вручную: "
                + "Id=Тип:Тир через запятую, типы: Backpack, Exoskeleton, Jetpack, Boots, OxygenTank, MiningChip, TorchChip, DeconstructChip, Compass. Пример: EquipmentIncrease2=Exoskeleton:2");

            foreach (var res in Content.Resonators)
            {
                var entry = Config.Bind(res.ConfigSection, "Recipe", res.recipe, "Рецепт модуля: id предметов через запятую. Неизвестные id пропускаются.");
                if (Array.IndexOf(res.previousRecipes, entry.Value) >= 0)
                {
                    Logger.LogInfo(res.id + ": recipe updated from the old default '" + entry.Value + "' to '" + res.recipe + "'");
                    entry.Value = res.recipe;
                }
                recipes[res.id] = entry;
            }

            var rocket = Content.SatelliteRocket;
            recipes[rocket.id] = Config.Bind("Satellite Rocket", "Recipe", rocket.recipe, "Рецепт спутниковой ракеты: id предметов через запятую. Неизвестные id пропускаются.");
            unlocks[rocket.id] = Config.Bind("Satellite Rocket", "UnlockAtTerraformation", rocket.unlockTi, "Индекс терраформации (Ti), при котором открывается ракета. 0 = доступно сразу.");
            baseRocketId = Config.Bind("Satellite Rocket", "BaseRocket", "",
                "Id игровой ракеты, которую копирует спутниковая. Пусто = найти автоматически (сначала GPS/навигационный спутник). Выбранная основа пишется в лог.");

            fAsteroidHasCrashed = TryFieldRef<Asteroid, bool>("hasCrashed");
            fAsteroidMaxAcceleration = AccessTools.Field(typeof(Asteroid), "maxAcceleration");
            fAsteroidInitialSpeed = AccessTools.Field(typeof(Asteroid), "initialSpeed");
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
            go.AddComponent<MeteorMarkers>();

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
        internal static float SignalLineWidth => Mathf.Clamp(signalLineWidth.Value, 0.005f, 1f);
        internal static float SignalLineOpacity => Mathf.Clamp01(signalLineOpacity.Value);
        internal static bool ShowMeteorLines => showMeteorLines.Value;
        internal static bool CaptureEffects => captureEffects.Value;
        internal static float BeaconIntervalSeconds => Mathf.Max(1f, beaconInterval.Value) * 60f;
        internal static float CoreDropChance => Mathf.Clamp01(coreDropChance.Value);
        internal static float MarkerSeconds => markerSeconds.Value;
        internal static bool RouteByFilter => routeByFilter.Value;
        internal static float EnergyUse(string id) => energyUse.TryGetValue(id, out var e) ? Mathf.Max(0f, e.Value) : 0f;
        internal static bool ShowNotification => showNotification.Value;
        internal static string FilterKey => filterKey.Value.Contains("<") ? filterKey.Value : "<Keyboard>/" + filterKey.Value;
        internal static string ModuleKey => moduleKey.Value.Contains("<") ? moduleKey.Value : "<Keyboard>/" + moduleKey.Value;

        static bool lastPowerOk = true;

        /// <summary>Free power of the base: production minus the game's own demand, before our network.</summary>
        internal static float FreePower()
        {
            var unit = Managers.GetManager<WorldUnitsHandler>()?.GetUnit(DataConfig.WorldUnitType.Energy);
            return unit == null ? float.PositiveInfinity : (float)(unit.GetIncreaseValuePersSec() + unit.GetDecreaseValuePersSec());
        }

        /// <summary>
        /// Whether the base has enough power for the meteor network. The game's energy units count only vanilla
        /// buildings (ours draw nothing there, so modules can lower the draw per building); the network's own
        /// demand is added here. Changes are logged.
        /// </summary>
        internal static bool PowerOk(float networkDemand, out string details)
        {
            details = "";
            if (!requirePower.Value)
            {
                return true;
            }
            var free = FreePower();
            if (float.IsInfinity(free))
            {
                return true;
            }
            var ok = free - networkDemand >= 0f;
            details = "free power of the base " + free.ToString("0.##") + ", network demand " + networkDemand.ToString("0.##");
            if (ok != lastPowerOk)
            {
                lastPowerOk = ok;
                LogInfo("Meteor network power " + (ok ? "restored" : "LOST") + ": " + details);
            }
            return ok;
        }

        internal static bool HasCrashed(Asteroid a) => fAsteroidHasCrashed != null && fAsteroidHasCrashed(a);

        /// <summary>
        /// A pulled meteor flies a longer way than a vanilla one; at full acceleration it builds up so much speed
        /// that it passes through the ground in a single frame. Zero acceleration freezes it in the air,
        /// so it is only reduced. Returns the original value (NaN if the field is unknown).
        /// </summary>
        internal static float LimitAcceleration(Asteroid a, float factor)
        {
            if (fAsteroidMaxAcceleration == null)
            {
                return float.NaN;
            }
            var original = (float)fAsteroidMaxAcceleration.GetValue(a);
            var limited = original * factor;
            fAsteroidMaxAcceleration.SetValue(a, limited);
            Log("Meteor maxAcceleration " + original + " -> " + limited
                + (fAsteroidInitialSpeed != null ? ", initialSpeed " + fAsteroidInitialSpeed.GetValue(a) : ""));
            return original;
        }

        internal static void SetAcceleration(Asteroid a, float value)
        {
            if (fAsteroidMaxAcceleration != null && !float.IsNaN(value))
            {
                fAsteroidMaxAcceleration.SetValue(a, value);
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
            PlayerGear.Detect(___groupsData, playerGearItems.Value);

            var existing = ___groupsData.Where(g => g != null).Select(g => g.id).ToHashSet();

            // First: recipes of the beacon and the advanced modules use it.
            RegisterCore(___groupsData, existing);

            foreach (var def in Content.Buildings)
            {
                if (existing.Contains(def.id))
                {
                    continue;
                }
                if (def == Content.Display)
                {
                    def.baseGroupIds = DisplayBases(___groupsData);
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
                ClearEnergy(building);
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

        /// <summary>The meteor core: a clone of a raw resource, not craftable, found only in captured meteors.</summary>
        static void RegisterCore(List<GroupData> groupsData, HashSet<string> existing)
        {
            var core = Content.Core;
            if (existing.Contains(core.id))
            {
                return;
            }
            var ids = new List<string>();
            if (coreBaseItem.Value.Trim().Length != 0)
            {
                ids.Add(coreBaseItem.Value.Trim());
            }
            ids.AddRange(core.baseItemIds);
            GroupDataItem baseItem = null;
            foreach (var id in ids)
            {
                baseItem = groupsData.Find(g => g != null && g.id == id) as GroupDataItem;
                if (baseItem != null)
                {
                    break;
                }
            }
            if (baseItem == null)
            {
                logger.LogError("No base item for " + core.id + ", meteor cores will not drop and their recipes lose them.");
                return;
            }
            var item = Instantiate(baseItem);
            SetupClone(item, core.id, groupsData);
            item.unlockingValue = 0f;
            item.craftableInList = [];
            // Sold by trade rockets like any resource: a sink for the cores players do not need.
            item.tradeValue = coreTradeValue.Value >= 0 ? coreTradeValue.Value : baseItem.tradeValue * 20;
            groupsData.Add(item);
            LogInfo("Registered " + core.id + " (model of " + baseItem.id + ", trade value " + item.tradeValue
                + ", base item " + baseItem.tradeValue + ")");
        }

        static FieldInfo energyField;
        static bool energyFieldLooked;

        /// <summary>
        /// The game's own energy accounting must not count our buildings (the base building may draw or make power):
        /// the network's draw is computed by the mod, so modules can lower it per building.
        /// The field is found by name so a renamed field only disables this, with a warning.
        /// </summary>
        static void ClearEnergy(GroupDataConstructible building)
        {
            if (!energyFieldLooked)
            {
                energyFieldLooked = true;
                var candidates = new List<string>();
                foreach (var f in AccessTools.GetDeclaredFields(typeof(GroupDataConstructible)))
                {
                    if (f.FieldType == typeof(float) && f.Name.IndexOf("unitGeneration", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidates.Add(f.Name);
                        if (f.Name.IndexOf("Energy", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            energyField = f;
                        }
                    }
                }
                LogInfo("Energy field: " + (energyField?.Name ?? "NOT FOUND") + " (unit generation fields: " + string.Join(", ", candidates) + ")");
            }
            if (energyField == null)
            {
                return;
            }
            var before = (float)energyField.GetValue(building);
            energyField.SetValue(building, 0f);
            LogInfo(building.id + " game energy " + before + " -> 0, network draw " + EnergyUse(building.id) + " (counted by the mod)");
        }

        /// <summary>
        /// The game's wall screens that can carry our statistics: the configured one first, then every
        /// constructible whose id starts with "Screen" and whose model has text on it. All of them are logged.
        /// </summary>
        static string[] DisplayBases(List<GroupData> groupsData)
        {
            var result = new List<string>();
            var configured = displayBaseScreen.Value.Trim();
            if (configured.Length != 0)
            {
                result.Add(configured);
            }
            var report = new List<string>();
            foreach (var gd in groupsData)
            {
                if (gd is not GroupDataConstructible gc || gc.id == null || !gc.id.StartsWith("Screen", StringComparison.Ordinal))
                {
                    continue;
                }
                var texts = gc.associatedGameObject != null ? MeteorDisplay.TextComponents(gc.associatedGameObject).Count : 0;
                report.Add(gc.id + " (" + texts + " texts)");
                if (texts > 0)
                {
                    result.Add(gc.id);
                }
            }
            LogInfo("Screens found for the meteor display: " + (report.Count == 0 ? "none" : string.Join(", ", report)));
            return result.ToArray();
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
            foreach (var raw in (recipes.TryGetValue(id, out var recipe) ? recipe.Value : "").Split(','))
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
                dict["GROUP_NAME_" + Content.Core.id] = ru ? Content.Core.nameRu : Content.Core.nameEn;
                dict["GROUP_DESC_" + Content.Core.id] = ru ? Content.Core.descRu : Content.Core.descEn;
                dict["MeteorMagnet_BeaconShower"] = ru ? "Метеорный маяк вызвал метеоритный дождь" : "The meteor beacon called a meteor shower";
                dict["MeteorMagnet_CoreFound"] = ru ? "Найдено ядро метеорита!" : "Meteor core found!";
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
                dict["MeteorMagnet_ScannerFilterTitle"] = ru ? "Фильтр метеорного сканера" : "Meteor Scanner filter";
                dict["MeteorMagnet_ScannerFilterHint"] = ru
                    ? "Сканер ведёт только метеориты, в которых есть хотя бы один отмеченный ресурс. Ничего не отмечено — ведёт все."
                    : "The scanner tracks only meteors carrying at least one checked resource. Nothing checked = tracks all.";
                dict["MeteorMagnet_ScannerFilterAll"] = ru ? "Вести все" : "Track all";
                dict["MeteorMagnet_NoPower"] = ru ? "Метеорной сети не хватает энергии — сканеры отключены" : "The meteor network lacks power - scanners are off";
                dict["MeteorMagnet_DisplayTitle"] = ru ? "Метеорная сеть" : "Meteor network";
                dict["MeteorMagnet_FilterEmpty"] = ru ? "На этой планете метеориты с ресурсами не найдены." : "No resource meteors found on this planet.";
            }
        }
    }
}
