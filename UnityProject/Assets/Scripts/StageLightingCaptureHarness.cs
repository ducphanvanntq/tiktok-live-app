using System;
using System.Collections;
using System.IO;
using System.Linq;
using UnityEngine;

namespace TikTokLiveGame
{
    // Opt-in integration observation: records actual renderer state after web updates.
    public sealed class StageLightingCaptureHarness : MonoBehaviour
    {
        [Serializable]
        private class Observation
        {
            public LightingConfig lighting;
            public bool connected, floorVisible, backgroundVisible;
            public int activeLights, activeBeams, npcCount;
            public float floorBrightness, beamWidth, backgroundBrightness;
            public string shader, screenshot;
        }

        public static void InstallIfRequested(GameObject root)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-stageLightingCapturePath");
            if (index < 0 || index + 1 >= arguments.Length) return;
            var harness = root.AddComponent<StageLightingCaptureHarness>();
            harness.StartCoroutine(harness.Observe(Path.GetFullPath(arguments[index + 1])));
        }

        private IEnumerator Observe(string directory)
        {
            Directory.CreateDirectory(directory);
            yield return new WaitForSecondsRealtime(4f);
            Camera camera = Camera.main;
            camera.GetComponent<ClubCameraController>().enabled = false;
            camera.transform.position = new Vector3(0f, 9.2f, 17.2f);
            camera.transform.LookAt(new Vector3(0f, 1.25f, -1.2f));
            string previous = null;
            int capture = 0;
            while (true)
            {
                yield return new WaitForSecondsRealtime(0.2f);
                string current = JsonUtility.ToJson(StageLighting.Current);
                if (current == previous) continue;
                yield return new WaitForEndOfFrame();
                LedDanceFloor floor = FindFirstObjectByType<LedDanceFloor>();
                Renderer backdrop = GameObject.Find("AmPhuBackdrop_Center").GetComponent<Renderer>();
                MovingHeadFixture[] fixtures = FindObjectsByType<MovingHeadFixture>(FindObjectsSortMode.None);
                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                string name = $"stage-{++capture:00}.png";
                File.WriteAllBytes(Path.Combine(directory, name), shot.EncodeToPNG());
                Destroy(shot);
                var observation = new Observation {
                    lighting = StageLighting.Current,
                    connected = GetComponent<TikTokWebSocketClient>()?.IsConnected == true,
                    floorVisible = floor.GetComponent<Renderer>().enabled,
                    backgroundVisible = backdrop.enabled,
                    activeLights = FindObjectsByType<Light>(FindObjectsSortMode.None).Count(light => light.enabled),
                    activeBeams = fixtures.Count(f => f.GetComponent<LineRenderer>().enabled),
                    npcCount = GetComponentInChildren<PlayerManager>().NpcCount,
                    floorBrightness = floor.SurfaceMaterial.GetFloat("_Brightness"),
                    backgroundBrightness = backdrop.sharedMaterial.GetFloat("_Brightness"),
                    beamWidth = fixtures.Average(f => f.GetComponent<LineRenderer>().endWidth),
                    shader = floor.SurfaceMaterial.shader.name,
                    screenshot = name,
                };
                File.WriteAllText(Path.Combine(directory, "stage-state.json"), JsonUtility.ToJson(observation, true));
                Debug.Log("STAGE_LIGHTING_APPLIED " + current);
                previous = current;
            }
        }
    }
}
