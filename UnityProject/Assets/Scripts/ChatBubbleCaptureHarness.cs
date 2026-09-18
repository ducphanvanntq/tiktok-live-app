using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace TikTokLiveGame
{
    // Explicit offline player verification. No transport or messages reach the live room.
    public sealed class ChatBubbleCaptureHarness : MonoBehaviour
    {
        private int assertions;
        private int placements;
        private string directory;

        internal IEnumerator Capture(string output)
        {
            directory = output;
            Directory.CreateDirectory(output);
            Application.logMessageReceived += FailOnException;
            // Allow the standalone window and initial GIF upload to finish before
            // validating pixels. Startup time is not part of chat latency.
            yield return new WaitForSecondsRealtime(10f);
            Require(GetComponent<TikTokWebSocketClient>() == null, "Preview has no live transport");
            VerifyText();
            TikTokGameController game = GetComponent<TikTokGameController>();
            PlayerManager manager = GetComponentInChildren<PlayerManager>();
            ViewerChatBubbles bubbles = GetComponent<ViewerChatBubbles>();
            Require(bubbles.AssetsReady, "Both generated assets load in the built player");
            Action<TikTokEvent> emit = data => game.SendMessage("HandleEvent", data);
            Camera camera = Camera.main;
            ClubCameraController director = camera.GetComponent<ClubCameraController>();
            director.enabled = false;
            Wide(camera);
            var people = new[] { Person("viewer-a", "Minh Anh"), Person("viewer-b", "Ngọc Hà"), Person("viewer-c", "Gia Huy") };
            emit(new TikTokEvent { type = "snapshot", players = people });
            yield return new WaitForSecondsRealtime(2f);

            emit(Chat("npc-000", "Bot must stay silent"));
            emit(Chat("demo-1", "Demo must stay silent"));
            emit(Chat("master-test", "Synthetic must stay silent"));
            var spectator = Chat("spectator", "Not joined"); spectator.spectatorOnly = true; emit(spectator);
            Require(bubbles.ActiveCount == 0 && manager.Find("spectator") == null, "Only joined real viewers get a bubble");
            // Creating synthetic fixtures reflows the floor. Place the real
            // fixtures afterwards, staggered in depth as well as horizontally.
            Place(manager.Find("viewer-a"), new Vector3(0f, 0f, 1f));
            Place(manager.Find("viewer-b"), new Vector3(-2.8f, 0f, -4f));
            Place(manager.Find("viewer-c"), new Vector3(2.8f, 0f, 4f));
            float burstTime = Time.unscaledTime;
            bubbles.Handle(Chat("viewer-a", "Tin đầu"), manager, burstTime);
            for (int i = 0; i < 100; i++) bubbles.Handle(Chat("viewer-a", "Đang gõ " + i), manager, burstTime);
            Require(bubbles.MessageFor("viewer-a") == "Tin đầu", "A burst does not flicker through intermediate text");
            bubbles.Tick(burstTime + 0.7f);
            Require(bubbles.MessageFor("viewer-a") == "Đang gõ 99", "Pending slot retains only latest text");
            bubbles.Handle(Chat("viewer-a", "Tin chờ cũ"), manager, burstTime + 0.8f);
            bubbles.Handle(Chat("viewer-a", "Tin mới đúng hạn"), manager, burstTime + 1.4f);
            Require(bubbles.MessageFor("viewer-a") == "Tin mới đúng hạn", "New arrival replaces an overdue pending message before it can render");
            bubbles.Handle(Chat("viewer-a", "Tin đang chờ khác"), manager, burstTime + 1.5f);
            bubbles.Handle(Chat("viewer-a", "Tin mới đúng hạn"), manager, burstTime + 1.6f);
            bubbles.Tick(burstTime + 2.1f);
            Require(bubbles.MessageFor("viewer-a") == "Tin mới đúng hạn", "Repeating the current text cancels an obsolete pending message");
            bubbles.Handle(Chat("viewer-a", "Tin sau khi hết hạn"), manager, burstTime + 10f);
            Require(bubbles.ActiveCount == 1 && bubbles.MessageFor("viewer-a") == "Tin sau khi hết hạn", "A chat after expiry replaces the old entry immediately");
            bubbles.Handle(new TikTokEvent { type = "reset" }, manager, Time.unscaledTime);
            emit(Chat("viewer-a", " \n\t\r "));
            Require(bubbles.ActiveCount == 0, "Blank chat is ignored");
            emit(Chat("viewer-a", "Chào cả nhà!"));
            yield return new WaitForSecondsRealtime(0.25f);
            yield return Shot("01-white-sparkles.png");
            Require(bubbles.Visible.Any(p => p.UserId == "viewer-a"), "Real viewer bubble actually renders");
            VerifyPlacements(bubbles, manager, camera);
            // Record real rendered effect frames, including a steady text plateau.
            for (int frame = 0; frame < 12; frame++)
            {
                yield return Shot($"animation-{frame:00}.png");
                yield return new WaitForSecondsRealtime(0.06f);
            }

            for (int i = 0; i < 1000; i++) emit(Chat("viewer-a", "Tin mới " + i));
            Require(bubbles.ActiveCount == 1, "1000 messages from one person allocate only one entry");
            yield return new WaitForSecondsRealtime(ViewerChatBubbles.UpdateInterval + 0.1f);
            Require(bubbles.MessageFor("viewer-a") == "Tin mới 999", "Flood converges to latest text without a queue");
            string longText = "Mọi người ơi hôm nay nhạc hay quá, chúc cả nhà có một buổi tối thật vui vẻ nhé!";
            emit(Chat("viewer-a", longText));
            yield return new WaitForSecondsRealtime(ViewerChatBubbles.UpdateInterval + 0.1f);
            yield return Shot("02-long-chat-ellipsis.png");
            Require(bubbles.Visible.Any(p => p.UserId == "viewer-a" && p.Text.EndsWith("…")), "Long text renders with ellipsis");
            emit(Chat("viewer-b", "Nhạc hay quá!"));
            emit(Chat("viewer-c", "Cùng nhảy nào!"));
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Shot("03-multiple-viewers.png");
            VerifyPlacements(bubbles, manager, camera);
            Require(bubbles.Visible.Count >= 2, "Separated viewers display simultaneous bubbles");

            // Make two actors occupy the same screen region. Both stay in state,
            // but only the newest bubble can occupy the shared visible rectangle.
            Place(manager.Find("viewer-b"), manager.Find("viewer-a").transform.position);
            emit(Chat("viewer-b", "Ưu tiên tin mới"));
            yield return new WaitForSecondsRealtime(0.1f);
            yield return Shot("04-crowded-overlap.png");
            VerifyPlacements(bubbles, manager, camera);
            Require(!(bubbles.Visible.Any(p => p.UserId == "viewer-a") && bubbles.Visible.Any(p => p.UserId == "viewer-b")), "Overlapping bubbles do not stack");
            Place(manager.Find("viewer-b"), new Vector3(-2.8f, 0f, -4f));

            // Project at the final render camera each frame, across all director shots.
            MethodInfo configure = typeof(ClubCameraController).GetMethod("ConfigureDirectorShot", BindingFlags.NonPublic | BindingFlags.Static);
            manager.TryGetViewerBounds(out Bounds crowd);
            int visibleCameraFrames = 0;
            for (int shot = 0; shot < 8; shot++) for (int step = 0; step <= 4; step++)
            {
                foreach (var person in people) emit(Chat(person.userId, "Lia cam vẫn bám đúng người"));
                object[] args = { shot, step % 2, step * 0.25f, crowd, Vector3.zero, Vector3.zero, 45f };
                configure.Invoke(null, args);
                camera.transform.position = (Vector3)args[4]; camera.transform.LookAt((Vector3)args[5]); camera.fieldOfView = (float)args[6];
                yield return new WaitForEndOfFrame();
                VerifyPlacements(bubbles, manager, camera);
                if (bubbles.Visible.Count > 0) visibleCameraFrames++;
                if (step == 2) yield return Shot($"camera-{shot:00}.png");
            }
            Require(visibleCameraFrames >= 20, "Camera test exercises visible bubbles, not just culling");
            Wide(camera);
            PlayerActor first = manager.Find("viewer-a");
            first.enabled = true;
            first.Grow(3f); first.Jump(1.4f);
            for (int frame = 0; frame < 20; frame++)
            {
                emit(Chat(first.UserId, "Nhảy lên nào!"));
                yield return new WaitForSecondsRealtime(0.08f);
                yield return new WaitForEndOfFrame();
                VerifyPlacements(bubbles, manager, camera);
            }
            first.enabled = false;
            Place(first, camera.transform.position - camera.transform.forward * 5f);
            yield return Shot("05-behind-camera.png");
            Require(!bubbles.Visible.Any(p => p.UserId == first.UserId), "Behind-camera bubble is hidden");
            Place(first, new Vector3(0f, 0f, 1f));
            yield return new WaitForEndOfFrame();
            first.gameObject.SetActive(false);
            yield return new WaitForEndOfFrame();
            Require(bubbles.MessageFor(first.UserId) == null, "Removed/disabled actors leave no orphan bubble");
            Require(!bubbles.Visible.Any(p => p.UserId == first.UserId), "Disabled actor chat is absent from the rendered frame");
            first.gameObject.SetActive(true);

            emit(new TikTokEvent { type = "snapshot", players = people });
            Require(bubbles.ActiveCount == 0, "Reconnect does not replay old comments");
            yield return new WaitForSecondsRealtime(2f);
            Place(manager.Find("viewer-a"), new Vector3(0f, 0f, 1f));
            emit(Chat("viewer-a", "Tự ẩn sau vài giây"));
            yield return new WaitForSecondsRealtime(ViewerChatBubbles.Lifetime - 0.15f);
            yield return Shot("06-fade-out.png");
            Require(bubbles.Visible.Count > 0 && bubbles.Visible[0].Opacity < 0.6f, "Bubble actually fades before expiry");
            yield return new WaitForSecondsRealtime(0.3f);
            Require(bubbles.ActiveCount == 0, "Timer removes expired bubbles");

            // Room-scale flood with actual actors. State and drawing remain bounded.
            var crowdRoster = Enumerable.Range(0, 400).Select(i => Person("load-" + i, "Khách " + i)).ToArray();
            emit(new TikTokEvent { type = "snapshot", players = crowdRoster });
            yield return new WaitForSecondsRealtime(2f);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 400; i++) bubbles.Handle(Chat("load-" + i, "Xin chào " + i), manager, Time.unscaledTime);
            for (int i = 0; i < 5000; i++) bubbles.Handle(Chat("load-" + (i % 400), "Chat liên tục " + i), manager, Time.unscaledTime);
            timer.Stop();
            Require(bubbles.ActiveCount == 400, "400 users and 5000 updates keep exactly 400 entries");
            yield return new WaitForSecondsRealtime(0.3f);
            yield return Shot("07-room-flood.png");
            VerifyPlacements(bubbles, manager, camera);
            Require(bubbles.Visible.Any(p => p.Opacity > 0.9f), "Crowd test renders opaque messages, not just pending placements");
            emit(new TikTokEvent { type = "reset" });
            yield return new WaitForEndOfFrame();
            Require(bubbles.ActiveCount == 0 && bubbles.Visible.Count == 0, "Reset clears every bubble and pending update");
            File.WriteAllText(Path.Combine(output, "verification.txt"), $"PASS: {assertions} assertions; {placements} rendered placements; 40 camera samples; 1000 same-user messages; 400 viewers + 5000 updates in {timer.ElapsedMilliseconds} ms. Unicode/ellipsis, real-viewer gating, join policy, overlaps, animation, camera, grow/jump, off-screen, expiry, reconnect, reset.\n");
            Debug.Log("CHAT_BUBBLES_PREVIEW_OK");
            Application.logMessageReceived -= FailOnException;
            Application.Quit();
        }

        private void VerifyText()
        {
            Require(ChatBubbleText.Clean("  Chào\n cả\t nhà!  ") == "Chào cả nhà!", "Whitespace is one line");
            Require(ChatBubbleText.Clean("e\u0301") == "é", "Vietnamese combining accents normalize");
            Require(ChatBubbleText.Clean("\ud800abc\udc00") == "abc", "Malformed surrogates are removed");
            Require(ChatBubbleText.Clean("\u202eabc") == "abc", "Bidi control cannot reorder the overlay");
            foreach (string symbol in new[] { "👨‍👩‍👧‍👦", "🇻🇳", "👍🏽", "a\u0301", "1️⃣" })
            {
                Require(ChatBubbleText.ElementEnds(symbol).Count == 1, "Unicode cluster stays whole: " + symbol);
                Require(ChatBubbleText.Fit(symbol + "abcdef", symbol.Length + 1, s => s.Length) == symbol + "…", "Truncation preserves cluster: " + symbol);
            }
            Require(ChatBubbleText.Clean(new string('x', 100000)).Length <= 121, "Huge input is bounded");
            Require(ChatBubbleText.Clean(new string(' ', 100000)).Length == 0, "Huge blank input is bounded and ignored");
            Require(ChatBubbleText.Fit("short", 100, s => s.Length) == "short", "Short chat has no ellipsis");
        }

        private void VerifyPlacements(ViewerChatBubbles bubbles, PlayerManager manager, Camera camera)
        {
            Require(bubbles.Visible.Count <= ViewerChatBubbles.VisibleLimit, "At most six visible bubbles");
            for (int i = 0; i < bubbles.Visible.Count; i++)
            {
                var p = bubbles.Visible[i]; placements++;
                Require(p.Occupied.xMin >= 0 && p.Occupied.yMin >= 0 && p.Occupied.xMax <= Screen.width && p.Occupied.yMax <= Screen.height, "All art fits viewport");
                PlayerActor actor = manager.Find(p.UserId);
                Require(ViewerChatBubbles.IsRealViewer(actor), "Visible bubble belongs to a real viewer");
                Require(actor.TryGetChatAnchor(camera, out Vector2 anchor, out _) && Vector2.Distance(anchor, p.Anchor) < 1f, "Bubble uses this frame's camera and actor position");
                Require(Mathf.Abs(p.Rect.center.x - anchor.x) < 1f, "Tail points to its owner horizontally");
                for (int j = i + 1; j < bubbles.Visible.Count; j++) Require(!p.Occupied.Overlaps(bubbles.Visible[j].Occupied), "Visible bubbles never overlap");
            }
        }
        private static void Wide(Camera camera)
        { camera.transform.position = new Vector3(0f, 9.2f, 17.2f); camera.transform.LookAt(new Vector3(0f, 1.25f, -1.2f)); camera.fieldOfView = 45f; }
        private static void Place(PlayerActor actor, Vector3 position) { actor.enabled = false; actor.transform.position = position; }
        private static TikTokPlayerData Person(string id, string name) => new() { userId = id, nickname = name };
        private static TikTokEvent Chat(string id, string text) => new()
        {
            type = "chat", userId = id, comment = text,
            nickname = id == "viewer-a" ? "Minh Anh" : id == "viewer-b" ? "Ngọc Hà" : id == "viewer-c" ? "Gia Huy" : id
        };
        private IEnumerator Shot(string filename)
        { yield return new WaitForEndOfFrame(); ScreenCapture.CaptureScreenshot(Path.Combine(directory, filename)); yield return null; }
        private void FailOnException(string message, string trace, LogType type)
        { if (type == LogType.Exception) { File.WriteAllText(Path.Combine(directory, "failure.txt"), message + "\n" + trace); Application.Quit(2); } }
        private void Require(bool condition, string message)
        {
            assertions++;
            if (condition) return;
            File.WriteAllText(Path.Combine(directory, "failure.txt"), message);
            Debug.LogError("CHAT_BUBBLES_PREVIEW_FAILED: " + message);
            Application.Quit(2);
            throw new InvalidOperationException(message);
        }
    }
}
