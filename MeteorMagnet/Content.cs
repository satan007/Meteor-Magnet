using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// A tier of the Meteor Magnet building. The tier decides the capture radius.
    /// </summary>
    internal class MagnetTier
    {
        internal string id;
        internal int level;
        /// <summary>Buildings to clone, first existing one wins.</summary>
        internal string[] baseGroupIds;
        /// <summary>The base building actually used, set at registration.</summary>
        internal string resolvedBaseId;
        /// <summary>Mk1 only redirects meteors and drops the loot next to itself; higher tiers store it.</summary>
        internal bool hasStorage;
        /// <summary>Capture radius in meters; PositiveInfinity = whole planet.</summary>
        internal float radius;
        internal string recipe;
        internal float unlockTi;
        internal Color color;
        internal string nameRu, nameEn, descRu, descEn;

        internal bool WholePlanet => float.IsPositiveInfinity(radius);
    }

    internal enum ResonatorEffect
    {
        /// <summary>Also captures meteors that carry no resources and turns them into common ore.</summary>
        CaptureBarren,
        /// <summary>+50% loot.</summary>
        LootBonus,
        /// <summary>Doubles the capture radius (no effect on a whole-planet magnet).</summary>
        RadiusBoost,
        /// <summary>Every captured item has a chance to be duplicated.</summary>
        DuplicateChance,
        /// <summary>Filtered-out and overflow items go to nearby containers instead of a pile.</summary>
        ForwardToContainers,
        /// <summary>Every meteor gives at least one of each resource type it carries.</summary>
        EveryResourceType,
    }

    /// <summary>
    /// A module crafted from resources that exist on a single planet. Put it into a Mk2/Mk3 magnet's
    /// storage to give that magnet an extra ability. Each kind counts once per magnet.
    /// </summary>
    internal class Resonator
    {
        internal string id;
        internal string planet;
        internal ResonatorEffect effect;
        internal string recipe;
        internal Color color;
        internal string nameRu, nameEn, descRu, descEn;
    }

    internal static class Content
    {
        internal const float LootBonusFactor = 1.5f;
        internal const float RadiusBoostFactor = 2f;
        internal const float DuplicateChance = 0.25f;
        internal const float ForwardRadius = 40f;

        /// <summary>What a barren meteor turns into with the Prime resonator.</summary>
        internal static readonly string[] BarrenLoot = ["Iron", "Titanium", "Magnesium", "Silicon", "Cobalt", "Aluminium"];

        internal static readonly MagnetTier[] Tiers =
        [
            new MagnetTier
            {
                id = "MeteorMagnet1", level = 1,
                baseGroupIds = ["Beacon", "Container1"],
                hasStorage = false,
                radius = 150f,
                recipe = "Iron,Iron,Iron,Titanium,Titanium,Magnesium,Magnesium,Silicon",
                unlockTi = 0f,
                color = new Color(0.35f, 0.55f, 1f),
                nameRu = "Гравитационный захват Mk1",
                nameEn = "Meteor Magnet Mk1",
                descRu = "Перенаправляет метеориты в радиусе 150 м к себе. Ресурсы ссыпаются аккуратной кучкой рядом с устройством.",
                descEn = "Redirects meteors within 150 m towards itself. Their resources drop in a neat pile next to it.",
            },
            new MagnetTier
            {
                id = "MeteorMagnet2", level = 2,
                baseGroupIds = ["Container1", "Container2"],
                hasStorage = true,
                radius = 500f,
                recipe = "Alloy,Alloy,Aluminium,Aluminium,Cobalt,Cobalt,Titanium,Titanium",
                unlockTi = 1000000f,
                color = new Color(0.55f, 0.25f, 1f),
                nameRu = "Гравитационный захват Mk2",
                nameEn = "Meteor Magnet Mk2",
                descRu = "Притягивает метеориты в радиусе 500 м и складывает их ресурсы в хранилище размером с малый сундук. Наведитесь и нажмите клавишу фильтра, чтобы выбрать ресурсы. Резонатор внутри даёт дополнительный эффект.",
                descEn = "Pulls in meteors within 500 m and stores their resources (small chest storage). Aim at it and press the filter key to choose resources. A resonator inside gives an extra effect.",
            },
            new MagnetTier
            {
                id = "MeteorMagnet3", level = 3,
                baseGroupIds = ["Container2", "Container1"],
                hasStorage = true,
                radius = float.PositiveInfinity,
                recipe = "Alloy,Alloy,Alloy,Alloy,Rod-uranium,Rod-uranium,Iridium,Iridium,Zeolite,Zeolite",
                unlockTi = 100000000f,
                color = new Color(1f, 0.3f, 0.75f),
                nameRu = "Гравитационный захват Mk3",
                nameEn = "Meteor Magnet Mk3",
                descRu = "Притягивает все метеориты на планете и складывает их ресурсы в хранилище размером с большой сундук. Наведитесь и нажмите клавишу фильтра, чтобы выбрать ресурсы. Резонатор внутри даёт дополнительный эффект.",
                descEn = "Pulls in every meteor on the planet and stores their resources (big chest storage). Aim at it and press the filter key to choose resources. A resonator inside gives an extra effect.",
            },
        ];

        internal static readonly Resonator[] Resonators =
        [
            new Resonator
            {
                id = "MeteorResonatorPrime", planet = "Prime",
                effect = ResonatorEffect.CaptureBarren,
                recipe = "PulsarQuartz,PulsarQuartz,Alloy",
                color = new Color(0.3f, 0.8f, 1f),
                nameRu = "Пульсарный резонатор",
                nameEn = "Pulsar Resonator",
                descRu = "Модуль для гравитационного захвата Mk2/Mk3. Захват ловит и пустые метеориты, превращая их в обычную руду.",
                descEn = "Meteor Magnet Mk2/Mk3 module. The magnet also captures barren meteors and turns them into common ore.",
            },
            new Resonator
            {
                id = "MeteorResonatorHumble", planet = "Humble",
                effect = ResonatorEffect.LootBonus,
                recipe = "Dolomite,Dolomite,Bauxite,Bauxite,Uraninite",
                color = new Color(1f, 0.6f, 0.2f),
                nameRu = "Доломитовый концентратор",
                nameEn = "Dolomite Concentrator",
                descRu = "Модуль для гравитационного захвата Mk2/Mk3. +50% ресурсов с каждого метеорита.",
                descEn = "Meteor Magnet Mk2/Mk3 module. +50% resources from every meteor.",
            },
            new Resonator
            {
                id = "MeteorResonatorSelenea", planet = "Selenea",
                effect = ResonatorEffect.RadiusBoost,
                recipe = "Selenium,Selenium,BalzarQuartz",
                color = new Color(0.3f, 1f, 0.6f),
                nameRu = "Селеновая линза",
                nameEn = "Selenium Lens",
                descRu = "Модуль для гравитационного захвата Mk2/Mk3. Удваивает радиус захвата.",
                descEn = "Meteor Magnet Mk2/Mk3 module. Doubles the capture radius.",
            },
            new Resonator
            {
                id = "MeteorResonatorToxicity", planet = "Toxicity",
                effect = ResonatorEffect.DuplicateChance,
                recipe = "ToxicGoo,ToxicGoo,ToxicGoo,Alloy",
                color = new Color(0.7f, 1f, 0.1f),
                nameRu = "Токсичный катализатор",
                nameEn = "Toxic Catalyst",
                descRu = "Модуль для гравитационного захвата Mk2/Mk3. Каждый пойманный ресурс с шансом 25% удваивается.",
                descEn = "Meteor Magnet Mk2/Mk3 module. Every captured resource has a 25% chance to be doubled.",
            },
            new Resonator
            {
                id = "MeteorResonatorAqualis", planet = "Aqualis",
                effect = ResonatorEffect.ForwardToContainers,
                recipe = "Phosphorus,Phosphorus,Phosphorus,Alloy",
                color = new Color(0.2f, 0.5f, 1f),
                nameRu = "Фосфорный ретранслятор",
                nameEn = "Phosphorus Relay",
                descRu = "Модуль для гравитационного захвата Mk2/Mk3. То, что не прошло фильтр или не влезло, отправляется в ближайшие сундуки в радиусе 40 м, а не падает кучкой.",
                descEn = "Meteor Magnet Mk2/Mk3 module. Filtered-out and overflow resources go to the nearest containers within 40 m instead of a pile.",
            },
            new Resonator
            {
                id = "MeteorResonatorSkeo", planet = "Skeo",
                effect = ResonatorEffect.EveryResourceType,
                recipe = "Amber,Amber,Amber,Alloy",
                color = new Color(1f, 0.75f, 0f),
                nameRu = "Янтарный сепаратор",
                nameEn = "Amber Separator",
                descRu = "Модуль для гравитационного захвата Mk2/Mk3. С каждого метеорита гарантированно приходит хотя бы по одному ресурсу каждого вида, который он несёт.",
                descEn = "Meteor Magnet Mk2/Mk3 module. Every meteor gives at least one of each resource type it carries.",
            },
        ];

        static Dictionary<string, MagnetTier> tierById;
        static Dictionary<string, Resonator> resonatorById;

        internal static MagnetTier TierOf(string groupId)
        {
            tierById ??= BuildIndex(Tiers, t => t.id);
            return groupId != null && tierById.TryGetValue(groupId, out var t) ? t : null;
        }

        internal static Resonator ResonatorOf(string groupId)
        {
            resonatorById ??= BuildIndex(Resonators, r => r.id);
            return groupId != null && resonatorById.TryGetValue(groupId, out var r) ? r : null;
        }

        static Dictionary<string, T> BuildIndex<T>(T[] items, System.Func<T, string> key)
        {
            var d = new Dictionary<string, T>();
            foreach (var item in items)
            {
                d[key(item)] = item;
            }
            return d;
        }
    }
}
