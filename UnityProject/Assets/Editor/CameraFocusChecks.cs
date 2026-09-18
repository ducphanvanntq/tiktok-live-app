using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace TikTokLiveGame.Editor
{
    public static class CameraFocusChecks
    {
        // Run in Edit Mode: no live transport, real viewer events or game window.
        [MenuItem("TikTok Live Game/Check Camera Focus")]
        public static void Run()
        {
            GameObject root = new("Camera focus checks");
            try
            {
                PlayerManager manager = root.AddComponent<PlayerManager>();
                manager.enabled = false;
                var players = Read<Dictionary<string, PlayerActor>>(manager, "players");
                PlayerActor bot = Actor(root, "npc-000", new Vector3(-1000f, 0f, 1000f));
                players.Add(bot.UserId, bot);
                Require(!manager.TryGetViewerBounds(out _), "A bot-only room has no camera subjects");

                GameObject cameraObject = new("Test camera");
                cameraObject.transform.SetParent(root.transform);
                cameraObject.transform.position = new Vector3(0f, 9.2f, 17.2f);
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                VerifySparseAudienceFraming(camera);
                cameraObject.transform.position = new Vector3(0f, 9.2f, 17.2f);
                ClubCameraController director = cameraObject.AddComponent<ClubCameraController>();
                director.enabled = false;
                Write(director, "playerManager", manager);
                Write(director, "currentLookAt", new Vector3(0f, 1.25f, -1.2f));

                RejectBotRequests(director, bot);
                Require(Read<Transform>(director, "focusTarget") == null, "NPCs cannot start a close-up");
                Require(Read<ICollection>(director, "welcomeQueue").Count == 0, "NPCs cannot fill the welcome queue");
                Require(!Read<bool>(director, "welcomeOverflow"), "NPCs cannot trigger a crowd welcome");
                manager.FocusPlayer(bot.UserId, 5f);
                manager.FocusPlayer("missing-viewer", 5f);
                Require(Read<string>(manager, "focusedUserId") == null, "Invalid focus cannot dim the crowd around a bot");
                Write(director, "directorShot", 5);
                Write(director, "directorCycle", 2);
                Write(director, "directorShotStartBeat", -100f);
                Tick(director);
                Require(Read<int>(director, "directorShot") == 0 && Read<int>(director, "directorCycle") == 2,
                    "Without viewers, the camera stops cycling through NPC rows");

                PlayerActor first = Actor(root, "viewer-a", new Vector3(8f, 0f, -1f));
                PlayerActor second = Actor(root, "viewer-b", new Vector3(11f, 0f, 3f));
                players.Add(first.UserId, first);
                players.Add(second.UserId, second);
                Require(manager.TryGetViewerBounds(out Bounds bounds) && bounds.min == first.transform.position && bounds.max == second.transform.position,
                    "Mixed-room framing includes only real viewers");
                bot.transform.position = new Vector3(2000f, 500f, -2000f);
                Require(manager.TryGetViewerBounds(out Bounds movedBounds) && movedBounds == bounds,
                    "A moving or jumping bot cannot move the camera's framing bounds");

                director.Focus(first, 5f, true);
                float until = Read<float>(director, "focusUntil");
                RejectBotRequests(director, bot);
                Require(Read<Transform>(director, "focusTarget") == first.transform && Read<float>(director, "focusUntil") == until,
                    "NPC requests cannot interrupt a real viewer's VIP focus");
                director.FocusFireworks(second);
                Require(Read<Transform>(director, "focusTarget") == second.transform && Read<bool>(director, "focusFireworks"),
                    "Real viewers still receive fireworks focus");
                RejectBotRequests(director, bot);
                Require(Read<Transform>(director, "focusTarget") == second.transform && Read<bool>(director, "focusFireworks"),
                    "NPC requests cannot replace a real fireworks shot");
                director.QueueWelcome(first);
                RejectBotRequests(director, bot);
                Require(Read<ICollection>(director, "welcomeQueue").Count == 1 && !Read<bool>(director, "welcomeOverflow"),
                    "Only the real welcome remains queued after a burst of NPC requests");
                ExpireAndTick(director);
                Require(Read<Transform>(director, "focusTarget") == first.transform && !Read<bool>(director, "focusFireworks"),
                    "The next real viewer's welcome still plays");

                for (int i = 0; i < 5; i++) director.QueueWelcome(second);
                Require(Read<bool>(director, "welcomeOverflow"), "Real viewer bursts still request the group shot");
                for (int i = 0; i < 4; i++) ExpireAndTick(director);
                Require(Read<bool>(director, "focusCrowdWelcome"), "A real viewer burst receives its group welcome");
                players.Remove(first.UserId);
                players.Remove(second.UserId);
                Tick(director);
                Require(!Read<bool>(director, "focusCrowdWelcome"), "A group welcome stops if only bots remain");
                Tick(director);
                Require(Read<int>(director, "directorShot") == 0 && !manager.TryGetViewerBounds(out _),
                    "After viewers leave, the camera returns to the wide room view");

                Debug.Log("CAMERA_FOCUS_CHECKS_OK: NPC focus/queue/overflow rejected; viewer-only framing; real VIP/fireworks/welcome preserved; NPC-only room stays wide; sparse audience visible across all eight portrait camera shots.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static PlayerActor Actor(GameObject root, string id, Vector3 position)
        {
            GameObject obj = new(id);
            obj.transform.SetParent(root.transform);
            obj.transform.position = position;
            PlayerActor actor = obj.AddComponent<PlayerActor>();
            actor.enabled = false;
            // Identity-only actors avoid loading art or downloading avatars in Edit Mode.
            typeof(PlayerActor).GetProperty(nameof(PlayerActor.UserId)).SetValue(actor, id);
            return actor;
        }

        private static void RejectBotRequests(ClubCameraController director, PlayerActor bot)
        {
            director.Focus(bot, 12f, false);
            director.Focus(bot, 12f, true, true);
            director.FocusFireworks(bot);
            for (int i = 0; i < 8; i++) director.QueueWelcome(bot);
        }

        private static void VerifySparseAudienceFraming(Camera camera)
        {
            camera.aspect = 540f / 960f;
            MethodInfo configure = typeof(ClubCameraController).GetMethod("ConfigureDirectorShot", BindingFlags.NonPublic | BindingFlags.Static);
            Vector3[] viewers =
            {
                new(-5.75f, 0f, -3.65f), new(5.75f, 0f, -3.65f),
                new(-5.75f, 0f, 3.75f), new(5.75f, 0f, 3.75f),
                new(0f, 0.76f, -6.15f), new(-2.05f, 0.76f, -6.05f), new(2.05f, 0.76f, -6.05f)
            };
            foreach (Vector3 viewer in viewers)
                for (int shot = 0; shot < 8; shot++) for (int cycle = 0; cycle < 2; cycle++) for (int step = 0; step <= 4; step++)
                {
                    object[] args = { shot, cycle, step * 0.25f, new Bounds(viewer, Vector3.zero), Vector3.zero, Vector3.zero, 45f };
                    configure.Invoke(null, args);
                    camera.transform.position = (Vector3)args[4];
                    camera.transform.LookAt((Vector3)args[5]);
                    camera.fieldOfView = (float)args[6];
                    foreach (Vector3 offset in new[] { Vector3.zero, new Vector3(-0.7f, 1.1f, 0f), new Vector3(0.7f, 1.1f, 0f), new Vector3(0f, 3.1f, 0f) })
                    {
                        Vector3 p = camera.WorldToViewportPoint(viewer + offset);
                        Require(p.z > 0f && p.x >= 0f && p.x <= 1f && p.y >= 0f && p.y <= 1f,
                            $"Sparse audience clipped: viewer={viewer}, shot={shot}, cycle={cycle}, step={step}, offset={offset}, viewport={p}");
                    }
                }
            // A viewer on one side of the floor must not move the podium shot
            // away from TOP players on the opposite side.
            foreach (Vector3 podium in new[] { viewers[4], viewers[5], viewers[6] })
                foreach (Vector3 floor in new[] { viewers[0], viewers[1], viewers[2], viewers[3] })
                    for (int cycle = 0; cycle < 2; cycle++) for (int step = 0; step <= 4; step++)
                    {
                        Bounds mixed = new(podium, Vector3.zero);
                        mixed.Encapsulate(floor);
                        object[] args = { 4, cycle, step * 0.25f, mixed, Vector3.zero, Vector3.zero, 45f };
                        configure.Invoke(null, args);
                        camera.transform.position = (Vector3)args[4];
                        camera.transform.LookAt((Vector3)args[5]);
                        camera.fieldOfView = (float)args[6];
                        foreach (Vector3 offset in new[] { Vector3.zero, new Vector3(-0.7f, 1.1f, 0f), new Vector3(0.7f, 1.1f, 0f), new Vector3(0f, 3.1f, 0f) })
                        {
                            Vector3 p = camera.WorldToViewportPoint(podium + offset);
                            Require(p.z > 0f && p.x >= 0f && p.x <= 1f && p.y >= 0f && p.y <= 1f,
                                $"Mixed audience podium clipped: podium={podium}, floor={floor}, cycle={cycle}, step={step}, offset={offset}, viewport={p}");
                        }
                    }
        }

        private static T Read<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        private static void Write(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void Tick(ClubCameraController director) => typeof(ClubCameraController).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(director, null);
        private static void ExpireAndTick(ClubCameraController director) { Write(director, "focusUntil", -1f); Tick(director); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
