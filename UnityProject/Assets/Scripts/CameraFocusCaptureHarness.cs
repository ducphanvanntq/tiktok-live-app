using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TikTokLiveGame
{
    // Explicit offline verification; enabled only by -welcomePreviewPath -cameraFocusPreview.
    public sealed class CameraFocusCaptureHarness : MonoBehaviour
    {
        private ClubCameraController director;
        private int assertions;
        private static readonly FieldInfo TargetField = typeof(ClubCameraController).GetField("focusTarget", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo UntilField = typeof(ClubCameraController).GetField("focusUntil", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo QueueField = typeof(ClubCameraController).GetField("welcomeQueue", BindingFlags.Instance | BindingFlags.NonPublic);

        private PlayerActor FocusedActor
        {
            get
            {
                if (Time.time >= (float)UntilField.GetValue(director)) return null;
                Transform target = (Transform)TargetField.GetValue(director);
                return target != null ? target.GetComponent<PlayerActor>() : null;
            }
        }

        internal IEnumerator Capture(string directory)
        {
            Directory.CreateDirectory(directory);
            Application.logMessageReceived += FailOnException;
            Require(GetComponent<TikTokWebSocketClient>() == null, "Preview must not connect to the live room");
            director = Camera.main.GetComponent<ClubCameraController>();
            PlayerManager manager = GetComponentInChildren<PlayerManager>();
            TikTokGameController game = GetComponent<TikTokGameController>();
            ViewerChatBubbles bubbles = GetComponent<ViewerChatBubbles>();
            Action<TikTokEvent> emit = data => game.SendMessage("HandleEvent", data);

            yield return WaitFor(() => FocusedActor != null && FocusedActor.IsNpc, 15f, "An idle room automatically focuses an NPC");
            PlayerActor firstNpc = FocusedActor;
            yield return new WaitForSecondsRealtime(0.7f);
            Require(Camera.main.fieldOfView < 40f, "NPC focus moves the real camera into a close shot");
            yield return Shot(directory, "01-npc-focus.png");

            emit(Chat(firstNpc.UserId, "Bot chat"));
            emit(Chat("spectator-focus", "Not joined", true));
            emit(Chat("demo-focus", "Synthetic chat"));
            Require(FocusedActor == firstNpc && manager.Find("spectator-focus") == null, "Synthetic chat and unjoined spectators cannot interrupt");
            emit(Chat("camera-viewer-a", "Xin chào cả nhà!"));
            PlayerActor viewerA = manager.Find("camera-viewer-a");
            Require(FocusedActor == viewerA, "An ordinary chat interrupts an NPC synchronously");
            director.Focus(firstNpc, 3f, false);
            Require(FocusedActor == viewerA, "An NPC cannot steal the camera back from a viewer");

            emit(Chat("camera-viewer-b", "Nhạc hay quá!"));
            PlayerActor viewerB = manager.Find("camera-viewer-b");
            for (int i = 0; i < 100; i++)
            {
                emit(Chat(viewerA.UserId, "Chat tiếp " + i));
                emit(Chat(viewerB.UserId, "Đang chờ " + i));
            }
            Require(((ICollection)QueueField.GetValue(director)).Count == 1, "Repeated chat keeps one queued turn per viewer");
            yield return new WaitForSecondsRealtime(0.8f);
            yield return Shot(directory, "02-viewer-chat-focus.png");
            Require(FocusedActor == viewerA && bubbles.Visible.Any(item => item.UserId == viewerA.UserId), "The focused viewer's chat bubble is visible");
            yield return WaitFor(() => FocusedActor == viewerB, 4f, "The next viewer gets a turn before any NPC");

            emit(new TikTokEvent { type = "gift", userId = viewerA.UserId, nickname = "Minh Anh", action = "fireworks", diamondCount = 100, durationMs = 6000, giftName = "Preview" });
            Require(FocusedActor == viewerA, "Gift focus retains its existing priority");
            emit(Chat("camera-viewer-c", "Chờ mình với!"));
            PlayerActor viewerC = manager.Find("camera-viewer-c");
            director.Focus(firstNpc, 3f, false);
            Require(FocusedActor == viewerA, "Chat and NPC focus do not interrupt the active gift effect");
            yield return WaitFor(() => FocusedActor == viewerC, 7f, "The waiting chat receives focus after the gift");
            yield return WaitFor(() => FocusedActor != null && FocusedActor.IsNpc, 13f, "NPC focus resumes after viewer turns and the idle gap");
            Require(FocusedActor != firstNpc, "Idle close-ups rotate through different NPCs");
            yield return new WaitForSecondsRealtime(0.7f);
            yield return Shot(directory, "03-npc-resumed.png");

            emit(new TikTokEvent { type = "reset" });
            yield return null;
            yield return new WaitForEndOfFrame();
            Require(FocusedActor == null && manager.NpcCount == 20, "Reset removes the old camera target and restores the NPC crowd");
            File.WriteAllText(Path.Combine(directory, "verification.txt"), $"PASS: {assertions} assertions. Automatic rotating NPC close-ups, immediate ordinary-chat priority, NPC cannot steal real-user focus, duplicate suppression, queued viewers, visible chat bubble, gift priority, NPC resumption and reset.\n");
            Debug.Log("CAMERA_FOCUS_PREVIEW_OK");
            Application.logMessageReceived -= FailOnException;
            Application.Quit();
        }

        private IEnumerator WaitFor(Func<bool> condition, float timeout, string message)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Require(condition(), message);
        }

        private static IEnumerator Shot(string directory, string name)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(directory, name));
            yield return null;
        }

        private static TikTokEvent Chat(string id, string comment, bool spectatorOnly = false) => new()
        {
            type = "chat", userId = id, nickname = id == "camera-viewer-a" ? "Minh Anh" : "Khách xem", comment = comment, spectatorOnly = spectatorOnly
        };

        private void Require(bool condition, string message)
        {
            assertions++;
            if (condition) return;
            Debug.LogError("CAMERA_FOCUS_PREVIEW_FAILED: " + message);
            Application.Quit(2);
            throw new InvalidOperationException(message);
        }

        private void FailOnException(string message, string stack, LogType type)
        {
            if (type is LogType.Exception or LogType.Assert) Application.Quit(2);
        }
    }
}
