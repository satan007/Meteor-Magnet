using UnityEngine;

namespace MeteorMagnet
{
    /// <summary>
    /// Capture visuals: a flash where the field grabs a meteor, a glowing trail behind the pulled
    /// meteor, and a flash where it lands. Plain Unity lights and a trail, no extra assets.
    /// </summary>
    internal static class CaptureEffects
    {
        const float FlashTime = 0.8f;
        static Material trailMaterial;

        internal static void Flash(Vector3 position, Color color, float intensity, float range)
        {
            if (!Plugin.CaptureEffects)
            {
                return;
            }
            var go = new GameObject("MeteorMagnet_Flash");
            go.transform.position = position;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            go.AddComponent<FadeLight>().duration = FlashTime;
            Object.Destroy(go, FlashTime + 0.1f);
        }

        internal static void Trail(GameObject meteor, Color color)
        {
            if (!Plugin.CaptureEffects || meteor == null || meteor.GetComponent<TrailRenderer>() != null)
            {
                return;
            }
            if (trailMaterial == null)
            {
                var shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    return;
                }
                trailMaterial = new Material(shader);
            }
            var trail = meteor.AddComponent<TrailRenderer>();
            trail.material = trailMaterial;
            trail.time = 1.5f;
            trail.minVertexDistance = 2f;
            trail.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0f));
            trail.widthMultiplier = 3f;
            var end = color;
            end.a = 0f;
            color.a = 0.6f;
            trail.startColor = color;
            trail.endColor = end;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
        }

        /// <summary>Fades a flash light out over its lifetime.</summary>
        internal class FadeLight : MonoBehaviour
        {
            internal float duration = 1f;
            float start;
            float startIntensity;
            Light lightComponent;

            void Start()
            {
                start = Time.time;
                lightComponent = GetComponent<Light>();
                startIntensity = lightComponent.intensity;
            }

            void Update()
            {
                var t = (Time.time - start) / duration;
                lightComponent.intensity = startIntensity * Mathf.Clamp01(1f - t);
            }
        }
    }
}
