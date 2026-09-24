using System;
using System.Collections.Generic;
using UnityEngine;

namespace TikTokLiveGame
{
    [Serializable]
    public sealed class LightingConfig
    {
        public bool floorEnabled = true, backgroundEnabled = true, lightsEnabled = true;
        public float floorBrightness = 1.5f, backgroundBrightness = 1.25f, lightsBrightness = 1.5f, beamWidth = 2.5f;
        public int floorPalette = 3, backgroundPalette = 1, lightsPalette = 3, floorPattern;
    }

    public sealed class StageLighting : MonoBehaviour
    {
        public static LightingConfig Current { get; private set; } = new();
        private readonly List<Renderer> backdrops = new();
        private readonly List<Renderer> beams = new();
        private readonly List<Material> backdropMaterials = new();
        private readonly List<(Light light, bool enabled, float intensity)> lights = new();
        private LedDanceFloor floor;
        private bool chroma;
        private static readonly Color[] First = { new(0.7f,0.12f,1f), new(1f,0.12f,0.4f), new(0.12f,0.5f,1f), Color.white, new(1f,0.15f,0.65f), new(0.1f,1f,0.5f) };
        private static readonly Color[] Second = { new(0.04f,1f,1f), new(1f,0.75f,0.12f), new(0.65f,1f,1f), Color.white, new(0.6f,0.22f,1f), new(0.95f,1f,0.2f) };

        public static Color PaletteColor(int palette, float position)
        {
            palette = Mathf.Clamp(palette, 0, 5);
            return palette == 3 ? Color.HSVToRGB(Mathf.Repeat(position + ClubBeatClock.Beat * 0.035f, 1f), 0.82f, 1f)
                : Color.Lerp(First[palette], Second[palette], Mathf.PingPong(position, 1f));
        }

        private void Awake()
        {
            Current = new LightingConfig();
            floor = FindFirstObjectByType<LedDanceFloor>();
            Shader backdropShader = Resources.Load<Shader>("Shaders/StageBackdrop");
            foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (renderer.name.StartsWith("AmPhuBackdrop_", StringComparison.Ordinal))
                {
                    backdrops.Add(renderer);
                    // Clone so the backdrop resource owner continues to own its original materials.
                    Material material = new(renderer.sharedMaterial) { shader = backdropShader };
                    renderer.sharedMaterial = material;
                    backdropMaterials.Add(material);
                }
                if (renderer is LineRenderer && (renderer.GetComponent<MovingHeadFixture>() != null || renderer.GetComponent<ClubLaserBeam>() != null)) beams.Add(renderer);
            }
            foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                lights.Add((light, light.enabled, light.intensity));
            Configure(Current);
        }

        public void Configure(LightingConfig value)
        {
            Current = value ?? new LightingConfig();
            Current.floorBrightness = Mathf.Clamp(Current.floorBrightness, 0f, 2f);
            Current.backgroundBrightness = Mathf.Clamp(Current.backgroundBrightness, 0f, 2f);
            Current.lightsBrightness = Mathf.Clamp(Current.lightsBrightness, 0f, 2f);
            Current.beamWidth = Mathf.Clamp(Current.beamWidth, 0.5f, 4f);
            if (floor != null)
            {
                floor.Palette = Current.floorPalette;
                floor.Pattern = Current.floorPattern;
                floor.Brightness = Current.floorBrightness;
            }
        }

        public void SetChroma(bool value) => chroma = value;

        private void LateUpdate()
        {
            if (floor != null) floor.GetComponent<Renderer>().enabled = Current.floorEnabled && !chroma;
            foreach (Renderer backdrop in backdrops) if (backdrop != null) backdrop.enabled = Current.backgroundEnabled && !chroma;
            foreach (Renderer beam in beams) if (beam != null) beam.enabled = Current.lightsEnabled && !chroma;
            for (int i = 0; i < lights.Count; i++)
            {
                var item = lights[i];
                if (item.light == null) continue;
                item.light.enabled = item.enabled && Current.lightsEnabled && !chroma;
                if (item.light.GetComponent<MovingHeadFixture>() != null) continue;
                item.light.color = PaletteColor(Current.lightsPalette, i * 0.17f);
                if (item.light.GetComponent<ClubStrobeLight>() != null) item.light.intensity *= Current.lightsBrightness;
                else item.light.intensity = item.intensity * Current.lightsBrightness * (0.85f + 0.15f * Mathf.Cos(ClubBeatClock.Beat * Mathf.PI * 2f));
            }
            foreach (Material material in backdropMaterials)
            {
                int palette = Mathf.Clamp(Current.backgroundPalette, 0, 6);
                material.SetFloat("_Brightness", Current.backgroundBrightness);
                material.SetFloat("_Recolor", palette > 0 ? 1f : 0f);
                material.SetColor("_ColorA", PaletteColor(Mathf.Max(0, palette - 1), 0f));
                material.SetColor("_ColorB", PaletteColor(Mathf.Max(0, palette - 1), 0.5f));
                material.SetFloat("_Beat", ClubBeatClock.Beat);
            }
        }

        private void OnDestroy()
        {
            foreach (Material material in backdropMaterials) if (material != null) Destroy(material);
        }
    }
}
