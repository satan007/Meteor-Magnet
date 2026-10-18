using SpaceCraft;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// The meteor network display: a copy of one of the game's wall screens whose texts are replaced
    /// with our statistics. The screen's own updater is switched off. Texts are reached by reflection
    /// (TextMeshPro), so the mod needs no extra references.
    /// </summary>
    internal static class MeteorDisplay
    {
        // Screens already prepared (updater off, texts listed in the log), by world object id.
        static readonly Dictionary<int, List<Component>> prepared = [];

        /// <summary>All TextMeshPro texts of a screen model, top to bottom.</summary>
        internal static List<Component> TextComponents(GameObject go)
        {
            var result = new List<Component>();
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c != null && TextProperty(c) != null)
                {
                    result.Add(c);
                }
            }
            result.Sort((a, b) => b.transform.position.y.CompareTo(a.transform.position.y));
            return result;
        }

        static PropertyInfo TextProperty(Component c)
        {
            for (var t = c.GetType(); t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                if (t.Name == "TMP_Text")
                {
                    return t.GetProperty("text");
                }
            }
            return null;
        }

        internal static void Reset()
        {
            prepared.Clear();
        }

        internal static void UpdateAll(List<string> lines)
        {
            foreach (var wo in WorldObjectsHandler.Instance.GetConstructedWorldObjects())
            {
                if (wo?.GetGroup()?.GetId() != Content.Display.id || !wo.GetIsPlaced())
                {
                    continue;
                }
                var go = wo.GetGameObject();
                if (go == null)
                {
                    continue;
                }
                if (!prepared.TryGetValue(wo.GetId(), out var texts) || texts.Count == 0 || texts[0] == null)
                {
                    texts = Prepare(wo, go);
                    prepared[wo.GetId()] = texts;
                }
                StopUpdaters(wo, go);
                Write(texts, lines);
            }
        }

        /// <summary>
        /// Stops the screen's own updater, which would overwrite our texts with world unit values.
        /// Checked on every update, since the game may start it again when the screen is re-enabled.
        /// </summary>
        static void StopUpdaters(WorldObject wo, GameObject go)
        {
            foreach (var mb in go.GetComponentsInChildren<WorldUnitsGenerationDisplayer>(true))
            {
                if (mb.enabled)
                {
                    mb.StopAllCoroutines();
                    mb.enabled = false;
                    Plugin.Log("Meteor display #" + wo.GetId() + ": stopped " + mb.GetType().Name + " on " + mb.gameObject.name);
                }
            }
        }

        static List<Component> Prepare(WorldObject wo, GameObject go)
        {
            StopUpdaters(wo, go);
            var texts = TextComponents(go);
            var report = new List<string>();
            foreach (var t in texts)
            {
                report.Add(t.gameObject.name + " y=" + t.transform.position.y.ToString("0.00") + " '" + TextProperty(t).GetValue(t) + "'");
                // Long lines shrink to fit instead of running off the screen.
                t.GetType().GetProperty("enableAutoSizing")?.SetValue(t, true);
                t.GetType().GetProperty("fontSizeMin")?.SetValue(t, 1f);
                t.GetType().GetProperty("enableWordWrapping")?.SetValue(t, false);
            }
            Plugin.Log("Meteor display #" + wo.GetId() + " texts (" + texts.Count + "): " + string.Join(" | ", report));
            return texts;
        }

        /// <summary>
        /// One line per text field from the top; if there are fewer fields than lines,
        /// the last field gets the rest, one per row.
        /// </summary>
        static void Write(List<Component> texts, List<string> lines)
        {
            for (int i = 0; i < texts.Count; i++)
            {
                string value;
                if (i >= lines.Count)
                {
                    value = "";
                }
                else if (i == texts.Count - 1)
                {
                    value = string.Join("\n", lines.GetRange(i, lines.Count - i));
                }
                else
                {
                    value = lines[i];
                }
                try
                {
                    TextProperty(texts[i])?.SetValue(texts[i], value);
                }
                catch (Exception ex)
                {
                    Plugin.LogWarning("Meteor display: cannot write text: " + ex.Message);
                }
            }
        }
    }
}
