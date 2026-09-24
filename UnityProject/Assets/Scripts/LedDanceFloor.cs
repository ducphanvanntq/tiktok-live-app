using UnityEngine;
using UnityEngine.Rendering;

namespace TikTokLiveGame
{
    // One quad / one material; the GPU draws and animates all 192 LED panels.
    public sealed class LedDanceFloor : MonoBehaviour
    {
        [SerializeField, Range(0f, 2f)] private float brightness = 1.5f;
        [SerializeField, Range(0, 5)] private int palette = 3;
        [SerializeField, Range(0, 2)] private int pattern;
        private Material material;
        private static readonly int BeatId = Shader.PropertyToID("_Beat");
        private static readonly int BrightnessId = Shader.PropertyToID("_Brightness");
        private static readonly int PatternId = Shader.PropertyToID("_Pattern");
        private static readonly int ColorAId = Shader.PropertyToID("_ColorA");
        private static readonly int ColorBId = Shader.PropertyToID("_ColorB");

        internal Material SurfaceMaterial => material;
        public float Brightness { get => brightness; set => brightness = Mathf.Clamp(value, 0f, 2f); }
        public int Palette { get => palette; set => palette = Mathf.Clamp(value, 0, 5); }
        public int Pattern { get => pattern; set => pattern = Mathf.Clamp(value, 0, 2); }

        public static LedDanceFloor Create()
        {
            GameObject surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
            surface.name = "Dance Floor LED Surface";
            surface.layer = 8;
            // Crowd occupies x=-5.75..6, z=-3.65..3.75, with feet at y=0.
            surface.transform.position = new Vector3(0f, -0.035f, 0.8f);
            surface.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            surface.transform.localScale = new Vector3(14.4f, 10.8f, 1f);
            Destroy(surface.GetComponent<Collider>());
            return surface.AddComponent<LedDanceFloor>();
        }

        private void Awake()
        {
            Shader shader = Resources.Load<Shader>("Shaders/LedDanceFloor");
            if (shader == null || !shader.isSupported)
            {
                Debug.LogError("LED floor shader is missing or unsupported.");
                GetComponent<Renderer>().enabled = false;
                enabled = false;
                return;
            }
            material = new Material(shader) { name = "Dance Floor LED (runtime)" };
            Renderer surface = GetComponent<Renderer>();
            surface.sharedMaterial = material;
            surface.shadowCastingMode = ShadowCastingMode.Off;
            surface.receiveShadows = false;
            Apply(ClubBeatClock.Beat);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F6)) Palette = (palette + 1) % 6;
            if (Input.GetKeyDown(KeyCode.F7)) Pattern = (pattern + 1) % 3;
            if (Input.GetKeyDown(KeyCode.F8)) Brightness = brightness > 0f ? 0f : 1.5f;
            Apply(ClubBeatClock.Beat);
        }

        internal void Apply(float beat)
        {
            if (material == null) return;
            material.SetFloat(BeatId, beat);
            material.SetFloat(BrightnessId, Mathf.Clamp(brightness, 0f, 2f));
            material.SetFloat(PatternId, Mathf.Clamp(pattern, 0, 2));
            material.SetColor(ColorAId, StageLighting.PaletteColor(palette, 0f));
            material.SetColor(ColorBId, StageLighting.PaletteColor(palette, 0.5f));
            material.SetFloat("_Rainbow", palette == 3 ? 1f : 0f);
        }

        private void OnDestroy()
        {
            if (material != null) Destroy(material);
        }
    }
}
