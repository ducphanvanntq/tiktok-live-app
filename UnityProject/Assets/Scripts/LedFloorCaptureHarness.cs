using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace TikTokLiveGame
{
    // Runs the real player offline and verifies pixels rendered by the bundled shader.
    public sealed class LedFloorCaptureHarness : MonoBehaviour
    {
        private int assertions;
        private void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("LED_FLOOR_FAIL: " + message);
            assertions++;
        }

        private void OnEnable() => Application.logMessageReceived += OnLog;
        private void OnDisable() => Application.logMessageReceived -= OnLog;
        private void OnLog(string message, string trace, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert)
                Application.Quit(2);
        }

        internal IEnumerator Capture(string directory)
        {
            Directory.CreateDirectory(directory);
            yield return new WaitForSecondsRealtime(3f);
            Require(GetComponent<TikTokWebSocketClient>() == null, "Capture must stay offline.");
            LedDanceFloor floor = FindFirstObjectByType<LedDanceFloor>();
            Require(floor != null && floor.enabled && floor.SurfaceMaterial != null, "Runtime floor and material exist.");
            Require(floor.SurfaceMaterial.shader.isSupported, "Bundled shader is supported on this GPU.");
            Require(floor.transform.parent == null && Mathf.Abs(floor.transform.position.y + 0.035f) < 0.001f,
                "Floor stays below feet and outside the rear rig offset.");
            Require(floor.GetComponent<Collider>() == null, "Existing physics floor remains the only floor collider.");
            Require(GetComponentInChildren<PlayerManager>().NpcCount == 20, "NPC crowd still spawns.");
            float beat = floor.SurfaceMaterial.GetFloat("_Beat");
            yield return new WaitForSecondsRealtime(0.2f);
            Require(floor.SurfaceMaterial.GetFloat("_Beat") > beat, "Live Update advances shader beat.");
            Require(Mathf.Abs(floor.SurfaceMaterial.GetFloat("_Beat") - ClubBeatClock.Beat) < 0.2f, "Floor uses the shared lighting clock.");

            floor.enabled = false;
            GameObject probeObject = new("LED Floor Pixel Probe");
            Camera probe = probeObject.AddComponent<Camera>();
            probe.enabled = false;
            probe.transform.position = new Vector3(0f, 15f, 0.8f);
            probe.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            probe.orthographic = true;
            probe.orthographicSize = 5.4f;
            probe.aspect = 14.4f / 10.8f;
            probe.cullingMask = 1 << 8;
            probe.clearFlags = CameraClearFlags.SolidColor;
            probe.backgroundColor = Color.black;
            RenderTexture target = new(320, 240, 24);
            target.Create();
            probe.targetTexture = target;
            try
            {
                Color32[] dark = Render(probe, floor, 0f, 0, 0, 0f);
                Color32[] on = Render(probe, floor, 0f, 0, 0, 0.8f);
                Require(Difference(dark, on) > 10f, "Enabling LED brightness changes actual rendered floor pixels.");
                for (int pattern = 0; pattern < 3; pattern++)
                {
                    Color32[] first = Render(probe, floor, 0f, pattern, 0, 0.8f);
                    Color32[] second = Render(probe, floor, 2.5f, pattern, 0, 0.8f);
                    Require(Difference(first, second) > 3f, "Pattern " + pattern + " animates on GPU.");
                }
                for (int palette = 1; palette < 3; palette++)
                    Require(Difference(on, Render(probe, floor, 0f, 0, palette, 0.8f)) > 3f, "Palette " + palette + " changes rendered colors.");
            }
            finally
            {
                probe.targetTexture = null;
                target.Release(); Destroy(target); Destroy(probeObject);
                floor.Palette = 0; floor.Pattern = 0; floor.Brightness = 0.8f; floor.enabled = true;
            }

            Camera camera = Camera.main;
            camera.GetComponent<ClubCameraController>().enabled = false;
            camera.transform.position = new Vector3(0f, 9.2f, 17.2f);
            camera.transform.LookAt(new Vector3(0f, 1.25f, -1.2f));
            for (int i = 0; i < 3; i++)
            {
                yield return new WaitForSecondsRealtime(0.27f);
                yield return new WaitForEndOfFrame();
                Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(directory, $"unity-led-{i + 1}.png"), shot.EncodeToPNG());
                Destroy(shot);
            }
            floor.Brightness = 0f;
            yield return null;
            yield return new WaitForEndOfFrame();
            Texture2D off = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(directory, "unity-led-off.png"), off.EncodeToPNG());
            Destroy(off);
            string result = $"LED_FLOOR_PASS: {assertions} assertions; GPU={SystemInfo.graphicsDeviceName}; API={SystemInfo.graphicsDeviceType}; BPM={ClubBeatClock.Bpm}; screen={Screen.width}x{Screen.height}";
            File.WriteAllText(Path.Combine(directory, "unity-led-verification.txt"), result);
            Debug.Log(result);
            Application.Quit(0);
        }

        private static Color32[] Render(Camera camera, LedDanceFloor floor, float beat, int pattern, int palette, float brightness)
        {
            floor.Pattern = pattern; floor.Palette = palette; floor.Brightness = brightness; floor.Apply(beat);
            camera.Render();
            RenderTexture previous = RenderTexture.active;
            Texture2D texture = new(320, 240, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = camera.targetTexture;
                texture.ReadPixels(new Rect(0, 0, 320, 240), 0, 0);
                texture.Apply();
                return texture.GetPixels32();
            }
            finally { RenderTexture.active = previous; Destroy(texture); }
        }

        private static float Difference(Color32[] a, Color32[] b)
        {
            long sum = 0;
            for (int i = 0; i < a.Length; i++)
                sum += Math.Abs(a[i].r - b[i].r) + Math.Abs(a[i].g - b[i].g) + Math.Abs(a[i].b - b[i].b);
            return (float)sum / (a.Length * 3);
        }
    }
}
