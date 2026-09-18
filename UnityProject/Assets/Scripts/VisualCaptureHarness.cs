using System;
using System.Collections;
using UnityEngine;

namespace TikTokLiveGame
{
    public sealed class VisualCaptureHarness : MonoBehaviour
    {
        private bool fadeProbe;
        private static readonly float[] ProbeAlpha = { 0f, 0.25f, 0.5f, 1f };

        internal static string ParseWelcomeDirectory(string[] arguments, out string error)
        {
            error = null;
            int index = Array.IndexOf(arguments, "-welcomePreviewPath");
            if (index < 0) return null;
            if (index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]) || arguments[index + 1].StartsWith("-", StringComparison.Ordinal))
            {
                error = "WELCOME_PREVIEW_ARGUMENT_ERROR: -welcomePreviewPath requires an output directory.";
                return null;
            }
            if (Array.IndexOf(arguments, "-welcomePreviewPath", index + 1) >= 0)
            {
                error = "WELCOME_PREVIEW_ARGUMENT_ERROR: duplicate -welcomePreviewPath.";
                return null;
            }
            try { return System.IO.Path.GetFullPath(arguments[index + 1]); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
            {
                error = "WELCOME_PREVIEW_ARGUMENT_ERROR: " + exception.Message;
                return null;
            }
        }

        public static void InstallIfRequested(GameObject root, string welcomeDirectory)
        {
            if (welcomeDirectory != null)
            {
                VisualCaptureHarness preview = root.AddComponent<VisualCaptureHarness>();
                preview.StartCoroutine(preview.CaptureWelcome(welcomeDirectory));
                return;
            }
            string[] arguments = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(arguments, "-capturePath");
            if (index < 0 || index + 1 >= arguments.Length) return;
            VisualCaptureHarness harness = root.AddComponent<VisualCaptureHarness>();
            harness.StartCoroutine(harness.Capture(arguments[index + 1]));
        }

        private IEnumerator CaptureWelcome(string directory)
        {
            System.IO.Directory.CreateDirectory(directory);
            yield return new WaitForSecondsRealtime(2f);
            TikTokGameController game = GetComponent<TikTokGameController>();
            WelcomeToast toast = GetComponent<WelcomeToast>();
            toast.SetPreviewSeed(20260918);
            Require(GetComponent<TikTokWebSocketClient>() == null, "Welcome preview must not construct a live transport.");
            Require(toast.AssetsReady && toast.ThemeCount >= 12, "Four wing poses, glow textures and at least twelve palettes must load.");
            VerifyArgumentsAndAssets();
            fadeProbe = true;
            yield return new WaitForEndOfFrame();
            VerifyFadePixels();
            fadeProbe = false;
            for (int i = 0; i < toast.ThemeCount; i++)
                Require(Contrast(toast.PaletteNameColor(i), toast.PaletteBackgroundColor(i)) >= 4.5f,
                    "Every name/background combination must remain readable.");
            Action<TikTokEvent> emit = data => game.SendMessage("HandleEvent", data);
            emit(new TikTokEvent { type = "reset" });
            emit(new TikTokEvent { type = "like", userId = "preview-spectator", nickname = "Spectator", spectatorOnly = true });
            Require(toast.PendingCount == 0, "Spectators must not be greeted.");
            emit(new TikTokEvent { type = "like", userId = "npc-000", nickname = "NPC" });
            Require(toast.PendingCount == 0, "NPCs must not be greeted.");
            TikTokEvent first = new() { type = "like", userId = "preview-first", nickname = "Minh Anh", likeCount = 1 };
            emit(first);
            emit(first);
            Require(toast.PendingCount == 1, "Repeated interactions must produce one welcome.");
            yield return null;
            Require(toast.ActiveUserId == first.userId, "The queued welcome must become visible.");
            emit(new TikTokEvent { type = "like", userId = "preview-pending", nickname = "Pending" });
            float ageBeforeSnapshot = toast.ActiveAge;
            emit(new TikTokEvent { type = "snapshot", players = new[]
            {
                new TikTokPlayerData { userId = first.userId, nickname = first.nickname },
                new TikTokPlayerData { userId = "preview-pending", nickname = "Pending" },
                new TikTokPlayerData { userId = "preview-existing", nickname = "Existing" }
            } });
            Require(toast.ActiveUserId == first.userId && toast.PendingCount == 1 && toast.ActiveAge >= ageBeforeSnapshot,
                "Reconnect must preserve the active animation clock and queued welcomes.");
            yield return new WaitForSecondsRealtime(WelcomeToast.Duration + 0.05f);
            Require(toast.ActiveUserId == "preview-pending", "The welcome queued before reconnect must still play.");
            emit(new TikTokEvent { type = "reset" });
            Require(toast.ActiveUserId == null && toast.PendingCount == 0, "Reset must clear active and queued welcomes.");
            emit(new TikTokEvent { type = "snapshot", players = new[] { new TikTokPlayerData { userId = "preview-existing", nickname = "Existing" } } });
            emit(new TikTokEvent { type = "like", userId = "preview-existing", nickname = "Existing" });
            Require(toast.PendingCount == 0, "Reconnected players must not get another welcome.");
            for (int i = 0; i < 20; i++)
                emit(new TikTokEvent { type = "like", userId = "preview-burst-" + i, nickname = "Burst " + i });
            Require(toast.PendingCount == WelcomeToast.QueueLimit, "Burst queue must remain bounded.");
            yield return null;
            Require(toast.PendingCount == WelcomeToast.QueueLimit - 1, "The active welcome must free a queue slot.");
            emit(new TikTokEvent { type = "like", userId = "preview-burst-19", nickname = "Retry after overflow" });
            Require(toast.PendingCount == WelcomeToast.QueueLimit, "A viewer dropped by the cap must be eligible on a later interaction.");
            emit(new TikTokEvent { type = "like", userId = "preview-burst-19", nickname = "Duplicate retry" });
            Require(toast.PendingCount == WelcomeToast.QueueLimit, "A successfully queued retry must still be deduplicated.");
            emit(new TikTokEvent { type = "reset" });

            string[] names = { "Minh Anh", "Ngọc Hà", "Người bạn tên rất dài 🌸 OlaChat", "Mai Linh", "Gia Huy", "Khách mới",
                "Bảo Ngọc", "Tuấn Anh", "Thanh Trúc", "Hoàng Yến", "Hải Đăng", "Khánh Linh" };
            string[] arguments = Environment.GetCommandLineArgs();
            int avatarIndex = Array.IndexOf(arguments, "-welcomeAvatarUrl");
            string avatarUrl = avatarIndex >= 0 && avatarIndex + 1 < arguments.Length ? arguments[avatarIndex + 1] : "";
            if (!string.IsNullOrEmpty(avatarUrl))
            {
                // Wait for the service's actual completion (HTTP timeout is 10 s),
                // then begin captures from its cache. Never use an animation frame as a network deadline.
                bool completed = false;
                Sprite downloadedAvatar = null;
                AvatarService.Instance.Load(avatarUrl, sprite => { downloadedAvatar = sprite; completed = true; });
                float deadline = Time.realtimeSinceStartup + 12f;
                while (!completed && Time.realtimeSinceStartup < deadline) yield return null;
                Require(completed && downloadedAvatar != null, "The preview avatar fixture must finish downloading before capture (12 s deadline).");
            }
            int previousTheme = -1;
            int previousAnchor = -1;
            var seenThemes = new System.Collections.Generic.HashSet<int>();
            var captureTimes = new System.Text.StringBuilder();
            for (int sample = 0; sample < names.Length; sample++)
            {
                emit(new TikTokEvent { type = sample == 1 ? "member" : "like", userId = "preview-card-" + sample,
                    nickname = names[sample], likeCount = 1, avatar = sample == 2 ? "" : avatarUrl });
                yield return null;
                Require(toast.ThemeIndex != previousTheme && toast.AnchorIndex != previousAnchor,
                    "Adjacent welcomes must change theme and anchor.");
                previousTheme = toast.ThemeIndex;
                previousAnchor = toast.AnchorIndex;
                Require(seenThemes.Add(toast.ThemeIndex), "All twelve palettes must appear before the deck repeats.");
                // Several frames per welcome show entrance, wing motion and exit.
                float sampleStart = Time.unscaledTime;
                int renderedFrames = 0;
                int maximumFlockDraws = 0;
                bool feedGrew = false;
                float previousCardY = 0f;
                for (int frame = 0; Time.unscaledTime - sampleStart < WelcomeToast.Duration; frame++)
                {
                    yield return new WaitForEndOfFrame();
                    renderedFrames++;
                    maximumFlockDraws = Mathf.Max(maximumFlockDraws, toast.FlockDrawCalls);
                    Require(toast.FlockDrawCalls <= 132, "The flock must stay within its reduced 132-draw budget.");
                    if (feedGrew)
                    {
                        Require(toast.CardRect.y <= previousCardY, "A growing feed must move the welcome out of its way.");
                        VerifyFlights(toast, 7);
                        feedGrew = false;
                    }
                    if (frame == 10)
                    {
                        Require(toast.CardRect.width > 0f, "The UI must actually render, not just update while hidden.");
                        VerifyFlights(toast);
                        if (sample != 2 && !string.IsNullOrEmpty(avatarUrl))
                            Require(toast.HasActiveAvatar, "The viewer avatar must load and appear on its card.");
                    }
                    string fileName = $"welcome-{sample:00}-{frame:000}.png";
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(directory, fileName));
                    captureTimes.Append(fileName).Append('\t').Append(Time.unscaledTime.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                    if (sample == 3 && frame == 14)
                    {
                        previousCardY = toast.CardRect.y;
                        for (int i = 0; i < 7; i++)
                            emit(new TikTokEvent { type = "chat", userId = "preview-card-" + sample, nickname = names[sample], comment = "Xin chào " + i });
                        feedGrew = true;
                    }
                    yield return new WaitForSecondsRealtime(0.025f);
                }
                Require(renderedFrames > 10, "The preview must render enough frames to run flight checks.");
                Debug.Log($"WELCOME_SAMPLE {sample}: theme={toast.ThemeIndex}, name=#{ColorUtility.ToHtmlStringRGB(toast.NameColor)}, background=#{ColorUtility.ToHtmlStringRGB(toast.BackgroundColor)}, anchor={toast.AnchorIndex}, card={toast.CardRect}, flockDraws={maximumFlockDraws}");
                yield return new WaitForSecondsRealtime(0.3f);
            }
            emit(new TikTokEvent { type = "reset" });
            Require(toast.PendingCount == 0 && toast.ActiveUserId == null, "Final reset must clear the effect.");
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "verification.txt"),
                "PASS: invalid arguments/manifests, texture ownership, card/avatar alpha pixels, spectator/NPC filtering, once-per-viewer, reconnect preserves active/queue, overflow retry, reset, 12 palettes, text contrast, seeded continuous flights, measured wing bounds, flights on card, growing-feed clearance, <=132 flock draw calls.\n");
            System.IO.File.WriteAllText(System.IO.Path.Combine(directory, "frame-times.tsv"), captureTimes.ToString());
            Debug.Log("WELCOME_PREVIEW_OK");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        private static void Require(bool condition, string message)
        {
            if (condition) return;
            Debug.LogError("WELCOME_PREVIEW_FAILED: " + message);
            Application.Quit(2);
            throw new InvalidOperationException(message);
        }

        private static void VerifyFlights(WelcomeToast toast, int feedCount = 0)
        {
            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 1080f, Screen.height / 1080f), 0.72f, 1.25f);
            Rect viewport = new(0f, 0f, Screen.width / scale, Screen.height / scale);
            Rect card = toast.CardRect;
            int turns = 0;
            int onCard = 0;
            for (int i = 0; i < toast.ButterflyCount; i++)
            {
                Vector2 previous = toast.FlightPosition(i, 0f);
                Vector2 previousVelocity = Vector2.zero;
                float travel = 0f;
                for (float t = 0.025f; t <= WelcomeToast.Duration; t += 0.025f)
                {
                    Vector2 position = toast.FlightPosition(i, t);
                    float radius = toast.ButterflyRadius(i);
                    Require(viewport.Contains(position - Vector2.one * radius) && viewport.Contains(position + Vector2.one * radius),
                        "Wings must stay inside the viewport for the full flight.");
                    if (card.Contains(position)) onCard++;
                    if (feedCount > 0)
                        Require(position.y + radius + 18f < viewport.height - 34f - feedCount * 27f,
                            "Wings and entrance motion must stay above the growing feed.");
                    Vector2 velocity = position - previous;
                    // A quadratic B-spline's derivative is a convex combination
                    // of adjacent control-point differences. Scale the bound by dt.
                    Require(velocity.magnitude <= toast.FlightSpeedLimit(i) * 0.025f + 0.02f,
                        "Flight speed must remain inside its control-point derivative bound.");
                    if (Mathf.Abs(previousVelocity.x) > 0.05f && Mathf.Abs(velocity.x) > 0.05f && Mathf.Sign(previousVelocity.x) != Mathf.Sign(velocity.x)) turns++;
                    travel += velocity.magnitude;
                    previous = position;
                    previousVelocity = velocity;
                }
                Require(travel > 12f, "Every butterfly must move along its own path.");
                for (int j = 0; j < i; j++)
                {
                    float separation = Vector2.Distance(toast.FlightPosition(i, 0.5f), toast.FlightPosition(j, 0.5f)) +
                        Vector2.Distance(toast.FlightPosition(i, 1.5f), toast.FlightPosition(j, 1.5f));
                    Require(separation > 2f, "Butterflies must not share an identical path.");
                }
            }
            Require(turns >= 2, "The flock must include independent changes of direction.");
            Require(onCard > 0, "Some butterflies must visibly fly over the card background.");
        }

        private static void VerifyArgumentsAndAssets()
        {
            Require(ParseWelcomeDirectory(Array.Empty<string>(), out string absentError) == null && absentError == null, "Normal launch must retain the bridge.");
            foreach (string[] invalid in new[] { new[] { "-welcomePreviewPath" }, new[] { "-welcomePreviewPath", "" },
                new[] { "-welcomePreviewPath", "-logFile", "player.log" }, new[] { "-welcomePreviewPath", "a", "-welcomePreviewPath", "b" } })
                Require(ParseWelcomeDirectory(invalid, out string error) == null && error != null, "Invalid preview arguments must fail explicitly.");
            Require(ParseWelcomeDirectory(new[] { "-welcomePreviewPath", "folder with spaces" }, out string validError) != null && validError == null,
                "A quoted directory with spaces must be valid.");
            Texture2D atlas = Resources.Load<Texture2D>("Welcome/butterfly-flap-atlas");
            string json = Resources.Load<TextAsset>("Welcome/asset-manifest").text;
            foreach (string invalid in new[] { "", "null", "{}", json.Substring(0, 50), json.Replace("proposedThemes", "missingThemes"),
                json.Replace("butterflies", "missingColors"), json.Replace("rectTopLeft", "missingBounds"), json.Replace("#0D2114", "invalid-color") })
                Require(!WelcomeToast.ValidateManifest(invalid, atlas.width, atlas.height, out string error) && error != null,
                    "Malformed manifests must be rejected without an exception.");
            int paletteStart = json.IndexOf("\"proposedThemes\"", StringComparison.Ordinal);
            string prefix = json.Substring(0, paletteStart);
            Require(!WelcomeToast.ValidateManifest(prefix + "\"proposedThemes\":[]}", atlas.width, atlas.height, out _), "An empty palette deck must be rejected.");
            string onePalette = "{\"background\":\"#102030\",\"border\":\"#FFFFFF\",\"text\":\"#FFFFFF\",\"butterflies\":[\"#FFFFFF\",\"#FFFFFF\"]}";
            Require(!WelcomeToast.ValidateManifest(prefix + "\"proposedThemes\":[" + onePalette + "]}", atlas.width, atlas.height, out _), "One palette must be rejected.");
            Require(WelcomeToast.ValidateManifest(prefix + "\"proposedThemes\":[" + onePalette + "," + onePalette + "]}", atlas.width, atlas.height, out _), "Two valid palettes must be safe to use.");
            foreach (Texture2D source in new[] { Texture2D.blackTexture, Resources.Load<Texture2D>("Backgrounds/olachat-background-v3") })
            {
                TextureWrapMode wrap = source.wrapMode;
                FilterMode filter = source.filterMode;
                int aniso = source.anisoLevel;
                Texture2D copy = ClubSceneBuilder.CreateBackgroundTexture(source);
                Require(copy != source && source.wrapMode == wrap && source.filterMode == filter && source.anisoLevel == aniso,
                    "Backdrop sampling must not mutate built-in or Resources textures.");
                Destroy(copy);
            }
            Texture2D fallback = ClubSceneBuilder.CreateBackgroundTexture(null);
            Require(fallback != Texture2D.blackTexture, "The missing-background fallback must also be owned.");
            Destroy(fallback);
        }

        private void OnGUI()
        {
            if (!fadeProbe || Event.current.type != EventType.Repaint) return;
            Matrix4x4 matrix = GUI.matrix;
            Color color = GUI.color;
            int depth = GUI.depth;
            GUI.depth = -1000;
            GUI.matrix = Matrix4x4.identity;
            GUI.color = Color.black;
            GUI.DrawTexture(new Rect(0f, 0f, 260f, 64f), Texture2D.whiteTexture);
            for (int i = 0; i < ProbeAlpha.Length; i++)
            {
                GUI.color = new Color(1f, 1f, 1f, ProbeAlpha[i]);
                WelcomeToast.DrawRoundedTexture(new Rect(8f + i * 64f, 4f, 40f, 24f), Texture2D.whiteTexture, ScaleMode.StretchToFill, Color.red, 6f);
                WelcomeToast.DrawRoundedTexture(new Rect(8f + i * 64f, 36f, 40f, 24f), Texture2D.whiteTexture, ScaleMode.ScaleAndCrop, Color.white, 12f);
            }
            GUI.matrix = matrix;
            GUI.color = color;
            GUI.depth = depth;
        }

        private static void VerifyFadePixels()
        {
            Texture2D shot = ScreenCapture.CaptureScreenshotAsTexture();
            try
            {
                for (int i = 0; i < ProbeAlpha.Length; i++)
                {
                    Color card = shot.GetPixel(28 + i * 64, shot.height - 16);
                    Color avatar = shot.GetPixel(28 + i * 64, shot.height - 48);
                    Require(Mathf.Abs(card.r - ProbeAlpha[i]) < 0.06f && Mathf.Abs(avatar.r - ProbeAlpha[i]) < 0.06f,
                        $"Card/avatar fade must reach the framebuffer once: alpha={ProbeAlpha[i]}, card={card.r}, avatar={avatar.r}.");
                }
            }
            finally { Destroy(shot); }
        }

        private static float Contrast(Color text, Color background) => (Luminance(text) + 0.05f) / (Luminance(background) + 0.05f);
        private static float Luminance(Color color) => 0.2126f * LinearChannel(color.r) + 0.7152f * LinearChannel(color.g) + 0.0722f * LinearChannel(color.b);
        private static float LinearChannel(float value) => value <= 0.04045f ? value / 12.92f : Mathf.Pow((value + 0.055f) / 1.055f, 2.4f);

        private IEnumerator Capture(string path)
        {
            yield return new WaitForSecondsRealtime(1.5f);
            TikTokWebSocketClient client = FindFirstObjectByType<TikTokWebSocketClient>();
            client?.StartDemo(40);
            yield return new WaitForSecondsRealtime(5f);
            client?.StopDemo();
            client?.DemoGift(300, 7);
            client?.DemoGift(200, 8);
            client?.DemoGift(100, 9);
            yield return new WaitForSecondsRealtime(9f);
            FindFirstObjectByType<GiftEffectManager>()?.PartyBurst();
            yield return new WaitForSecondsRealtime(0.8f);
            ScreenCapture.CaptureScreenshot(path + ".stage.png");
            yield return new WaitForSecondsRealtime(0.5f);
            client?.DemoGift(100, 7);
            yield return new WaitForSecondsRealtime(4f);
            ScreenCapture.CaptureScreenshot(path);
            Debug.Log($"Visual capture saved to {path}");
            yield return new WaitForSecondsRealtime(2f);
            Application.Quit();
        }
    }
}
