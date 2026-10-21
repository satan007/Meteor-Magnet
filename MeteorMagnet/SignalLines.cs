using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Glowing arcs that show the meteor network: scanner -> relay -> relay -> magnet,
    /// or straight debug beams from buildings to meteors.
    /// Lines are rebuilt on every refresh; unchanged ones are reused.
    /// </summary>
    internal class SignalLines
    {
        const int Segments = 20;
        // Thin in the middle of the span, a bit thinner at the ends, like a beam rather than a cable.
        static readonly AnimationCurve taper = new(
            new Keyframe(0f, 0.5f), new Keyframe(0.15f, 1f), new Keyframe(0.85f, 1f), new Keyframe(1f, 0.5f));
        // Debug beams reach meteors hundreds of metres away: thin at the building, wide at the meteor,
        // so both ends stay visible.
        const float WideningFactor = 40f;
        static readonly AnimationCurve widening = new(new Keyframe(0f, 1f / WideningFactor), new Keyframe(1f, 1f));

        readonly string name;
        readonly bool arc;

        internal SignalLines(string name, bool arc)
        {
            this.name = name;
            this.arc = arc;
        }
        const float Lift = 3f;

        static readonly string[] shaderNames =
        [
            "Sprites/Default",
            "Universal Render Pipeline/Particles/Unlit",
            "Universal Render Pipeline/Unlit",
            "Legacy Shaders/Particles/Additive",
            "Unlit/Color",
        ];

        readonly Dictionary<string, LineRenderer> lines = [];
        readonly HashSet<string> used = [];
        Material material;
        bool noShader;
        GameObject root;

        internal void Begin()
        {
            used.Clear();
        }

        /// <summary>Draws (or keeps) a line between two points.</summary>
        internal void Edge(int fromId, Vector3 from, Color fromColor, int toId, Vector3 to, Color toColor)
        {
            if (!EnsureMaterial())
            {
                return;
            }
            var key = fromId + ">" + toId;
            // One line per pair of buildings: the meteor and storage networks share the link between them.
            if (used.Contains(key) || used.Contains(toId + ">" + fromId))
            {
                return;
            }
            used.Add(key);
            if (!lines.TryGetValue(key, out var lr) || lr == null)
            {
                var go = new GameObject("MeteorMagnet_Signal_" + key);
                go.transform.SetParent(root.transform, false);
                lr = go.AddComponent<LineRenderer>();
                lr.material = material;
                lr.positionCount = arc ? Segments + 1 : 2;
                lr.useWorldSpace = true;
                lr.widthCurve = arc ? taper : widening;
                lr.numCapVertices = 0;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                lines[key] = lr;
            }

            var a = from + Vector3.up * Lift;
            var b = to + Vector3.up * Lift;
            if (!arc)
            {
                lr.SetPosition(0, a);
                lr.SetPosition(1, to);
            }
            else
            {
                // Arc: a quadratic curve bulging upwards by a fifth of the distance.
                var control = (a + b) * 0.5f + Vector3.up * (Vector3.Distance(a, b) * 0.2f);
                for (int i = 0; i <= Segments; i++)
                {
                    var t = i / (float)Segments;
                    var p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * control + t * t * b;
                    lr.SetPosition(i, p);
                }
            }
            lr.widthMultiplier = Plugin.SignalLineWidth * (arc ? 1f : WideningFactor);
            var alpha = Plugin.SignalLineOpacity;
            fromColor.a *= alpha;
            toColor.a *= alpha;
            lr.startColor = fromColor;
            lr.endColor = toColor;
        }

        /// <summary>Removes arcs that are no longer part of the network.</summary>
        internal void End()
        {
            var stale = new List<string>();
            foreach (var kv in lines)
            {
                if (!used.Contains(kv.Key))
                {
                    stale.Add(kv.Key);
                }
            }
            foreach (var key in stale)
            {
                if (lines[key] != null)
                {
                    Object.Destroy(lines[key].gameObject);
                }
                lines.Remove(key);
            }
        }

        internal void Clear()
        {
            used.Clear();
            End();
        }

        bool EnsureMaterial()
        {
            if (material != null)
            {
                if (root == null)
                {
                    root = new GameObject(name);
                }
                return true;
            }
            if (noShader)
            {
                return false;
            }
            foreach (var name in shaderNames)
            {
                var shader = Shader.Find(name);
                if (shader != null)
                {
                    material = new Material(shader);
                    Plugin.Log("Signal lines use shader " + name);
                    root = new GameObject(name);
                    return true;
                }
            }
            noShader = true;
            Plugin.LogWarning("No shader found for signal lines; they will not be drawn.");
            return false;
        }
    }
}
