using System.Collections.Generic;
using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Marks where uncaught meteors (slipped or thrown away) came down: a tall beam of light over the spot
    /// and a tag with the distance along the top of the screen, like a compass mark. Marks fade out after a while.
    /// </summary>
    internal class MeteorMarkers : MonoBehaviour
    {
        const float BeamHeight = 150f;
        const float BeamWidth = 1.2f;

        class Mark
        {
            internal Vector3 position;
            internal Color color;
            internal float until;
            internal LineRenderer beam;
        }

        static readonly List<Mark> marks = [];
        static Material material;

        internal static void Add(Vector3 position, Color color, string why)
        {
            var seconds = Plugin.MarkerSeconds;
            if (seconds <= 0f)
            {
                return;
            }
            var mark = new Mark { position = position, color = color, until = Time.time + seconds };
            if (material == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    material = new Material(shader);
                }
            }
            if (material != null)
            {
                var go = new GameObject("MeteorMagnet_Marker");
                var lr = go.AddComponent<LineRenderer>();
                lr.material = material;
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.SetPosition(0, position);
                lr.SetPosition(1, position + Vector3.up * BeamHeight);
                lr.widthMultiplier = BeamWidth;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                mark.beam = lr;
            }
            marks.Add(mark);
            Plugin.Log("Marker at " + position + " for " + seconds.ToString("0") + " s: " + why);
        }

        internal static void Clear()
        {
            foreach (var m in marks)
            {
                if (m.beam != null)
                {
                    Destroy(m.beam.gameObject);
                }
            }
            marks.Clear();
        }

        void Update()
        {
            for (int i = marks.Count - 1; i >= 0; i--)
            {
                var m = marks[i];
                var left = m.until - Time.time;
                if (left <= 0f)
                {
                    if (m.beam != null)
                    {
                        Destroy(m.beam.gameObject);
                    }
                    marks.RemoveAt(i);
                    continue;
                }
                if (m.beam != null)
                {
                    // Fades out over the last 20 seconds, the top end is always faint.
                    var bottom = m.color;
                    bottom.a = 0.7f * Mathf.Clamp01(left / 20f);
                    var top = m.color;
                    top.a = 0f;
                    m.beam.startColor = bottom;
                    m.beam.endColor = top;
                }
            }
        }

        void OnGUI()
        {
            if (marks.Count == 0)
            {
                return;
            }
            var cam = Camera.main;
            if (cam == null)
            {
                return;
            }
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(16 * Screen.height / 1080f),
            };
            var w = 110f * Screen.height / 1080f;
            var h = 26f * Screen.height / 1080f;
            // Just below the game's compass.
            var y = 100f * Screen.height / 1080f;
            foreach (var m in marks)
            {
                var sp = cam.WorldToScreenPoint(m.position);
                var x = sp.x;
                if (sp.z < 0f)
                {
                    // Behind the player: pin to the nearer screen edge.
                    x = sp.x < Screen.width / 2f ? Screen.width - w / 2f : w / 2f;
                }
                x = Mathf.Clamp(x, w / 2f, Screen.width - w / 2f);
                var dist = Vector3.Distance(cam.transform.position, m.position);
                var c = m.color;
                c.a = 1f;
                style.normal.textColor = c;
                GUI.Label(new Rect(x - w / 2f, y, w, h), "▼ " + dist.ToString("0") + " m", style);
            }
        }
    }
}
