using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Links of the storage network drawn as thin spirals winding around the signal arc, one spiral per channel
    /// running through the link. Spirals of one link differ in pitch and phase, so they do not lie on each other.
    /// </summary>
    internal class ChannelLines
    {
        const float Lift = 3f;
        const float Radius = 0.3f;
        const float RadiusStep = 0.08f;
        // Turns per metre of the first spiral; each next one winds a little faster.
        const float Pitch = 0.35f;
        const float PitchStep = 0.15f;
        const int PointsPerMetre = 8;
        const int MaxPoints = 600;

        readonly Dictionary<string, LineRenderer> lines = [];
        readonly HashSet<string> used = [];
        Material material;
        GameObject root;

        internal void Begin()
        {
            used.Clear();
        }

        internal void Link(int fromId, Vector3 from, int toId, Vector3 to, int channels, Color color)
        {
            if (!EnsureMaterial())
            {
                return;
            }
            var a = from + Vector3.up * Lift;
            var b = to + Vector3.up * Lift;
            var length = Vector3.Distance(a, b);
            var control = (a + b) * 0.5f + Vector3.up * (length * 0.2f);
            var points = Mathf.Clamp((int)(length * PointsPerMetre), 16, MaxPoints);
            for (int k = 0; k < channels; k++)
            {
                var key = fromId + ">" + toId + "#" + k;
                used.Add(key);
                if (!lines.TryGetValue(key, out var lr) || lr == null)
                {
                    var go = new GameObject("MeteorMagnet_Channel_" + key);
                    go.transform.SetParent(root.transform, false);
                    lr = go.AddComponent<LineRenderer>();
                    lr.material = material;
                    lr.useWorldSpace = true;
                    lr.numCapVertices = 0;
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    lr.receiveShadows = false;
                    lines[key] = lr;
                }
                lr.positionCount = points + 1;
                lr.widthMultiplier = Plugin.SignalLineWidth;
                var c = color;
                c.a *= Plugin.SignalLineOpacity;
                lr.startColor = c;
                lr.endColor = c;

                var radius = Radius + RadiusStep * k;
                var turnsPerMetre = Pitch + PitchStep * k;
                var phase = 2f * Mathf.PI * k / Mathf.Max(1, channels);
                for (int i = 0; i <= points; i++)
                {
                    var t = i / (float)points;
                    var p = (1 - t) * (1 - t) * a + 2 * (1 - t) * t * control + t * t * b;
                    var tangent = (2 * (1 - t) * (control - a) + 2 * t * (b - control)).normalized;
                    var n1 = Vector3.Cross(tangent, Vector3.up);
                    if (n1.sqrMagnitude < 1e-4f)
                    {
                        n1 = Vector3.Cross(tangent, Vector3.right);
                    }
                    n1.Normalize();
                    var n2 = Vector3.Cross(tangent, n1);
                    // The spiral fades in and out at the ends so it meets the buildings in one point.
                    var r = radius * Mathf.Clamp01(Mathf.Min(t, 1 - t) * 10f);
                    var angle = phase + 2f * Mathf.PI * turnsPerMetre * length * t;
                    lr.SetPosition(i, p + (n1 * Mathf.Cos(angle) + n2 * Mathf.Sin(angle)) * r);
                }
            }
        }

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
            if (material == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    return false;
                }
                material = new Material(shader);
            }
            if (root == null)
            {
                root = new GameObject("MeteorMagnet_ChannelLines");
            }
            return true;
        }
    }
}
