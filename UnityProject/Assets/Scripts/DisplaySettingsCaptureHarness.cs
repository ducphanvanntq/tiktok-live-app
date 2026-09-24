using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace TikTokLiveGame
{
    // Offline regression and screenshots; never connects to a live room.
    public sealed class DisplaySettingsCaptureHarness : MonoBehaviour
    {
        private int assertions;
        internal IEnumerator Capture(string directory)
        {
            Directory.CreateDirectory(directory);
            Application.logMessageReceived += FailOnException;
            Require(GetComponent<TikTokWebSocketClient>() == null, "Preview must stay offline");
            yield return new WaitForSecondsRealtime(3f);
            TikTokGameController game = GetComponent<TikTokGameController>();
            PlayerManager players = GetComponentInChildren<PlayerManager>();
            WelcomeToast welcome = GetComponent<WelcomeToast>();
            ViewerChatBubbles chat = GetComponent<ViewerChatBubbles>();
            TopPointsPanel top = game.PointsPanel;
            Camera camera = Camera.main;
            camera.GetComponent<ClubCameraController>().enabled = false;
            camera.transform.position = new Vector3(0f, 9.2f, 17.2f);
            camera.transform.LookAt(new Vector3(0f, 1.25f, -1.2f));
            camera.fieldOfView = 45f;
            top.ResetPosition();
            Action<TikTokEvent> emit = data => game.SendMessage("HandleEvent", data);
            Action<bool, bool, bool> configure = (showTop, showWelcome, showChat) =>
                emit(JsonUtility.FromJson<TikTokEvent>("{\"type\":\"display_config\",\"display\":{\"showTop\":" +
                    showTop.ToString().ToLowerInvariant() + ",\"showWelcome\":" + showWelcome.ToString().ToLowerInvariant() +
                    ",\"showChat\":" + showChat.ToString().ToLowerInvariant() + "}}"));
            emit(new TikTokEvent { type = "snapshot", pointsVersion = 1, pointsRevision = 1,
                pointScores = new[] { new PointScoreData { userId = "display-a", nickname = "Minh Anh", points = 120, reachedOrder = 1 } },
                players = new[] { new TikTokPlayerData { userId = "display-a", nickname = "Minh Anh" } } });
            emit(Chat("display-a", "Xin chào cả nhà!"));
            yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForEndOfFrame();
            Require(top.Opacity > 0f && chat.Visible.Count > 0 && welcome.DisplayEnabled, "All three overlays default to enabled");
            yield return Shot(directory, "01-default-top-chat.png");
            emit(Chat("display-b", "Mình mới vào sàn"));
            yield return new WaitForSecondsRealtime(0.35f);
            Require(welcome.ActiveUserId == "display-b", "Welcome appears for a new viewer");
            yield return Shot(directory, "02-welcome-on.png");
            top.TogglePositioning();
            configure(false, false, false);
            yield return new WaitForEndOfFrame();
            Require(top.Opacity == 0 && !top.IsPositioning && welcome.ActiveUserId == null && chat.Visible.Count == 0,
                "Disabling clears active UI immediately, including TOP placement");
            emit(Chat("display-c", "Chat while hidden"));
            emit(new TikTokEvent { type = "like", userId = "display-a", likeCount = 9, pointsVersion = 1, pointsRevision = 2,
                pointScores = new[] { new PointScoreData { userId = "display-a", nickname = "Minh Anh", points = 129, reachedOrder = 2 } } });
            yield return new WaitForSecondsRealtime(0.3f);
            Require(chat.ActiveCount == 0 && welcome.ActiveUserId == null && players.Find("display-c") != null,
                "Hidden messages do not queue, while users still join");
            Require(game.PointScores[0].points == 129, "Points continue accumulating while TOP is hidden");
            yield return Shot(directory, "03-all-off.png");
            configure(true, false, false);
            yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForEndOfFrame();
            Require(top.Opacity > 0f && !chat.DisplayEnabled && !welcome.DisplayEnabled, "TOP can be enabled independently");
            configure(false, true, false);
            emit(Chat("display-c", "Already joined while hidden"));
            yield return new WaitForSecondsRealtime(0.2f);
            Require(welcome.ActiveUserId == null, "Re-enabling does not greet an old hidden join");
            emit(Chat("display-d", "New join after enabling"));
            yield return new WaitForSecondsRealtime(0.2f);
            Require(welcome.ActiveUserId == "display-d" && chat.ActiveCount == 0 && top.Opacity == 0,
                "Welcome can be enabled independently");
            configure(false, false, true);
            Require(chat.ActiveCount == 0, "Re-enabling chat does not replay old messages");
            emit(Chat("display-a", "Chat đã bật lại"));
            yield return new WaitForSecondsRealtime(0.5f);
            yield return new WaitForEndOfFrame();
            Require(chat.Visible.Count > 0 && top.Opacity == 0 && welcome.ActiveUserId == null,
                "Chat can be enabled independently");
            configure(false, false, false);
            emit(new TikTokEvent { type = "reset" });
            emit(Chat("display-e", "After reset"));
            yield return new WaitForSecondsRealtime(0.2f);
            Require(!chat.DisplayEnabled && !welcome.DisplayEnabled && welcome.ActiveUserId == null && top.Opacity == 0,
                "Reset does not enable disabled overlays");
            configure(true, true, true);
            emit(Chat("display-f", "Hello again"));
            yield return new WaitForSecondsRealtime(0.3f);
            Require(welcome.ActiveUserId == "display-f" && chat.ActiveCount > 0, "New events render after re-enabling");
            yield return VerifyExtraSettings(game, players, camera.GetComponent<ClubCameraController>(), emit);
            File.WriteAllText(Path.Combine(directory, "verification.txt"), $"PASS: {assertions} assertions; seven default-enabled switches; immediate hiding; no stale replay; score continuity; reset/re-enable; NPC/chat cancellation and queues; gift focus retained; gift visuals/audio cleanup; independent event feed.\n");
            Debug.Log("DISPLAY_SETTINGS_PREVIEW_OK");
            Application.logMessageReceived -= FailOnException;
            Application.Quit();
        }

        private IEnumerator VerifyExtraSettings(TikTokGameController game, PlayerManager players, ClubCameraController director, Action<TikTokEvent> emit)
        {
            DisplayConfig legacy = JsonUtility.FromJson<TikTokEvent>("{\"type\":\"display_config\",\"display\":{\"showTop\":true,\"showChat\":true,\"showWelcome\":true}}").display;
            Require(legacy.focusNpc && legacy.focusChat && legacy.showFeed && legacy.showGiftEffects, "Older three-switch configs keep new switches enabled");
            DisplayConfig config = new();
            Action apply = () => emit(JsonUtility.FromJson<TikTokEvent>(JsonUtility.ToJson(new TikTokEvent { type = "display_config", display = config })));
            Write(game, "hudVisible", false);
            config.showFeed = false; apply();
            Require(!Read<bool>(game, "hudVisible"), "Changing another switch preserves TOP hidden locally by F3");
            config.showFeed = true; apply();
            Write(game, "hudVisible", true);
            object queue = Read<object>(director, "welcomeQueue");
            queue.GetType().GetMethod("Clear").Invoke(queue, null);
            Write(director, "welcomeOverflow", false);
            Write(director, "chatOverflow", false);
            Write(director, "focusUntil", -1f);
            Write(director, "nextNpcFocusAt", -1f);
            director.SendMessage("LateUpdate");
            Require(Focused(director)?.IsNpc == true, "NPC focus defaults to enabled");
            config.focusNpc = false; apply();
            Require(Focused(director) == null, "Turning off NPC focus cancels the current bot shot immediately");
            Write(director, "nextNpcFocusAt", -1f);
            director.SendMessage("LateUpdate");
            Require(Focused(director) == null, "Disabled NPC focus does not restart automatically");
            config.focusNpc = true; apply();
            Write(director, "nextNpcFocusAt", -1f);
            director.SendMessage("LateUpdate");
            Require(Focused(director)?.IsNpc == true, "NPC focus can be re-enabled");
            emit(Chat("display-e", "Chat gets camera priority"));
            Require(Focused(director) == players.Find("display-e"), "Chat focus still interrupts a bot");
            emit(Chat("display-f", "Queued chat"));
            Require(Read<ICollection>(director, "welcomeQueue").Count > 0, "A second chat queues normally");
            config.focusChat = false; apply();
            Require(Focused(director) == null && Read<ICollection>(director, "welcomeQueue").Count == 0,
                "Turning off chat focus cancels both active and queued chat turns");
            emit(Chat("display-e", "Chat without zoom"));
            Require(Focused(director) == null && game.GetComponent<ViewerChatBubbles>().ActiveCount > 0,
                "Disabling chat focus preserves chat bubbles");
            director.FocusFireworks(players.Find("display-e"));
            config.focusChat = true; apply();
            emit(Chat("display-f", "Wait for gift"));
            config.focusChat = false; apply();
            Require(Focused(director) == players.Find("display-e") && Read<bool>(director, "focusFireworks"),
                "Camera switches do not cancel gift focus");
            // A genuine welcome must remain queued when pending chat turns are removed.
            director.QueueWelcome(players.Find("display-f"), 2f);
            apply();
            Require(Read<ICollection>(director, "welcomeQueue").Count == 1, "Chat switch preserves welcome turns");
            config.showFeed = false; apply();
            emit(Chat("display-e", "Hidden feed, visible bubble"));
            Require(Read<ICollection>(game, "feed").Count == 0 && game.GetComponent<ViewerChatBubbles>().ActiveCount > 0,
                "The event feed can be disabled without hiding bubbles");
            config.showFeed = true; apply();
            Require(Read<ICollection>(game, "feed").Count == 0, "Re-enabling feed does not replay old events");
            emit(Chat("display-e", "New feed entry"));
            Require(Read<ICollection>(game, "feed").Count == 1, "New feed entries resume after enabling");
            GiftEffectManager effects = game.GetComponent<GiftEffectManager>();
            emit(new TikTokEvent { type = "gift", userId = "display-e", nickname = "Minh Anh", giftName = "Preview fireworks", action = "fireworks", diamondCount = 500, durationMs = 6000 });
            yield return null;
            Require(effects.BannerUntil > Time.unscaledTime && effects.GetComponentsInChildren<ParticleSystem>().Length > 0,
                "Enabled gifts start their banner and particles");
            config.showGiftEffects = false; apply();
            yield return null;
            Require(effects.BannerUntil == 0f && effects.GetComponentsInChildren<ParticleSystem>().Length == 0 &&
                effects.GetComponentsInChildren<Light>().Length == 0 && !Read<AudioSource>(effects, "fireworkAudio").isPlaying,
                "Disabling gift effects clears the banner, particles, spotlight and sound");
            long before = game.PointScores[0].points;
            emit(new TikTokEvent { type = "gift", userId = "display-e", nickname = "Minh Anh", giftName = "Hidden effect", action = "fireworks", diamondCount = 500, durationMs = 6000 });
            effects.PartyBurst();
            yield return new WaitForSecondsRealtime(1f);
            Require(effects.BannerUntil == 0f && effects.GetComponentsInChildren<ParticleSystem>().Length == 0,
                "Disabled gift and party effects cannot spawn or resume stale coroutines");
            Require(game.PointScores[0].points == before + 50000, "Gifts continue scoring while effects are disabled");
            config.showGiftEffects = true; apply();
            Require(effects.BannerUntil == 0f, "Re-enabling effects does not replay the previous gift");
            effects.PartyBurst();
            yield return null;
            Require(effects.BannerUntil > Time.unscaledTime && effects.GetComponentsInChildren<ParticleSystem>().Length > 0,
                "New effects resume after enabling");
            effects.SetDisplayEnabled(false);
        }

        private static T Read<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
        private static void Write(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
        private static PlayerActor Focused(ClubCameraController director) => Time.time < Read<float>(director, "focusUntil")
            ? Read<Transform>(director, "focusTarget")?.GetComponent<PlayerActor>() : null;

        private static TikTokEvent Chat(string id, string comment) => new() { type = "chat", userId = id, nickname = id == "display-a" ? "Minh Anh" : "Khách mới", comment = comment };
        private static IEnumerator Shot(string directory, string name)
        {
            yield return new WaitForEndOfFrame();
            ScreenCapture.CaptureScreenshot(Path.Combine(directory, name));
            yield return null;
        }
        private void Require(bool ok, string message)
        {
            assertions++;
            if (ok) return;
            Debug.LogError("DISPLAY_SETTINGS_PREVIEW_FAILED: " + message);
            Application.Quit(2);
            throw new InvalidOperationException(message);
        }
        private void FailOnException(string message, string stack, LogType type)
        {
            if (type is LogType.Exception or LogType.Assert) Application.Quit(2);
        }
    }
}
