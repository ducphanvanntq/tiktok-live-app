using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TikTokLiveGame
{
    public sealed class TikTokGameController : MonoBehaviour
    {
        private TikTokWebSocketClient client;
        private PlayerManager playerManager;
        private GiftEffectManager giftEffects;
        private ClubCameraController clubCamera;
        private readonly List<FeedEntry> feed = new();

        private WelcomeToast welcomeToast;
        private TopPointsPanel topPoints;
        private ViewerChatBubbles chatBubbles;
        private readonly PointsLeaderboard points = new();
        internal TopPointsPanel PointsPanel => topPoints;
        internal PointScoreData[] PointScores => points.Top;
        private string username = "";
        private string connectionStatus = "Đang chờ Node server...";
        private int events;
        private int diamonds;
        private float partyEnergy;
        private bool controlsVisible;
        private bool hudVisible = true;
        private bool chromaMode;
        private int cinematicVersion;
        private bool restoreControlsAfterCinematic;
        private readonly List<Renderer> environmentRenderers = new();
        private readonly List<Light> environmentLights = new();
        private bool stylesReady;
        private GUIStyle panelStyle;
        private GUIStyle labelStyle;
        private GUIStyle smallStyle;
        private GUIStyle buttonStyle;
        private GUIStyle inputStyle;
        private GUIStyle bannerStyle;
        // DrawControls() dùng texture này cho tab đang chọn, nên phải là field
        // chứ không phải biến cục bộ trong EnsureStyles().
        private Texture2D buttonHover;

        private int controlTab = 0;
        private string manualUsername = "Khách VIP";

        public void Initialize(TikTokWebSocketClient socketClient, PlayerManager manager, GiftEffectManager effects)
        {
            welcomeToast = gameObject.AddComponent<WelcomeToast>();
            topPoints = gameObject.AddComponent<TopPointsPanel>();
            chatBubbles = gameObject.AddComponent<ViewerChatBubbles>();
            client = socketClient;
            playerManager = manager;
            giftEffects = effects;
            clubCamera = Camera.main?.GetComponent<ClubCameraController>();
            if (client != null) client.EventReceived += HandleEvent;
            username = PlayerPrefs.GetString("TikTokUsername", string.Empty);
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F1)) controlsVisible = !controlsVisible;
            if (Input.GetKeyDown(KeyCode.F2)) ToggleChroma();
            if (Input.GetKeyDown(KeyCode.F3)) hudVisible = !hudVisible;
            if (Input.GetKeyDown(KeyCode.F11)) Screen.fullScreen = !Screen.fullScreen;
            for (int index = feed.Count - 1; index >= 0; index--)
                if (Time.unscaledTime - feed[index].Time > 9f) feed.RemoveAt(index);
        }

        private void HandleEvent(TikTokEvent liveEvent)
        {
            if (liveEvent.type == "status") connectionStatus = liveEvent.message;
            if (liveEvent.type is "member" or "chat" or "gift" or "like" or "follow" or "share") events++;

            if (liveEvent.type == "snapshot")
            {
                diamonds = 0;
                foreach (TikTokPlayerData donor in liveEvent.vipScores ?? System.Array.Empty<TikTokPlayerData>())
                    diamonds += donor.score;
            }
            else if (liveEvent.type == "gift")
            {
                diamonds += liveEvent.diamondCount;
                AddEnergy(liveEvent.diamondCount * 2f);
                AddFeed($"{liveEvent.nickname} tặng {liveEvent.giftName} (+{liveEvent.diamondCount})", new Color(1f, 0.55f, 0.84f));
            }
            else if (liveEvent.type == "chat")
            {
                AddEnergy(2f);
                AddFeed($"{liveEvent.nickname}: {liveEvent.comment}", Color.white);
            }
            else if (liveEvent.type == "like")
            {
                AddEnergy(liveEvent.likeCount * 0.08f);
            }
            else if (liveEvent.type == "member")
            {
                AddEnergy(1f);
                AddFeed($"{liveEvent.nickname} vào sàn", new Color(0.35f, 0.95f, 1f));
            }
            else if (liveEvent.type is "follow" or "share")
            {
                AddFeed($"{liveEvent.nickname} đã {liveEvent.type}", new Color(1f, 0.85f, 0.3f));
            }
            else if (liveEvent.type == "reset")
            {
                events = 0;
                diamonds = 0;
                partyEnergy = 0f;
                feed.Clear();
            }

            playerManager.Handle(liveEvent);
            chatBubbles.Handle(liveEvent, playerManager, Time.unscaledTime);
            welcomeToast.Handle(liveEvent, playerManager);
            if (points.Apply(liveEvent)) topPoints.SetScores(points.Top, liveEvent.type == "reset");
            if (liveEvent.type is "gift" or "like" or "snapshot" or "member" or "chat" or "follow" or "share" or "reset") UpdateTopPlayers();
            if (liveEvent.type == "gift")
            {
                PlayerActor actor = playerManager.Find(liveEvent.userId);
                giftEffects.Play(liveEvent, actor);
            }

            bool joinFocus = liveEvent.action == "join" && liveEvent.joinedNow;
            bool socialFocus = liveEvent.type is "follow" or "share";
            bool requestsFocus = joinFocus || socialFocus || liveEvent.action is "camera" or "walk" or "vip" or "topdj" or "fireworks" or "medal";
            if (requestsFocus && (liveEvent.type is "gift" or "chat" or "follow" or "share"))
            {
                PlayerActor actor = playerManager.Find(liveEvent.userId);
                if (actor == null || actor.IsNpc) return;
                float focusSeconds = liveEvent.durationMs > 0 ? liveEvent.durationMs / 1000f : (socialFocus ? 2f : joinFocus ? 2.5f : 3f);
                bool wideWalkFocus = liveEvent.action == "walk";
                if (joinFocus || socialFocus)
                {
                    clubCamera?.QueueWelcome(actor, focusSeconds);
                }
                else if (liveEvent.action == "fireworks")
                    clubCamera?.FocusFireworks(actor, Mathf.Max(6f, focusSeconds));
                else
                    clubCamera?.Focus(
                        actor,
                        focusSeconds,
                        liveEvent.action is "vip" or "topdj" || liveEvent.diamondCount >= 100,
                        wideWalkFocus);
                // A first-time "hey" is a clean welcome shot. Gift focuses keep
                // the crowd dimming and VIP decoration handled by PlayerManager.
                if (!joinFocus && !socialFocus)
                {
                    playerManager.FocusPlayer(liveEvent.userId, focusSeconds);
                    StartCoroutine(CinematicGift(focusSeconds));
                }
            }
        }

        private System.Collections.IEnumerator CinematicGift(float seconds)
        {
            int version = ++cinematicVersion;
            restoreControlsAfterCinematic |= controlsVisible;
            controlsVisible = false;
            yield return new WaitForSeconds(Mathf.Clamp(seconds, 2.5f, 12f));
            if (version == cinematicVersion)
            {
                controlsVisible = restoreControlsAfterCinematic;
                restoreControlsAfterCinematic = false;
            }
        }

        private void UpdateTopPlayers()
        {
            playerManager.UpdateTopRanks(points.Top.Select(player => player.userId));
        }

        private void AddEnergy(float amount)
        {
            partyEnergy += Mathf.Max(0f, amount);
            if (partyEnergy < 1000f) return;
            partyEnergy %= 1000f;
            playerManager.DanceAll(7f);
            giftEffects.PartyBurst();
        }

        private void AddFeed(string text, Color color)
        {
            feed.Add(new FeedEntry(text, color, Time.unscaledTime));
            if (feed.Count > 7) feed.RemoveAt(0);
        }

        private void OnGUI()
        {
            EnsureStyles();
            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 1080f, Screen.height / 1080f), 0.72f, 1.25f);
            Matrix4x4 originalMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float width = Screen.width / scale;
            float height = Screen.height / scale;

            DrawFeed(height);
            if (controlsVisible) DrawControls();

            if (giftEffects != null && Time.unscaledTime < giftEffects.BannerUntil)
                GUI.Box(new Rect(width * 0.5f - 310f, 142f, 620f, 68f), giftEffects.BannerText, bannerStyle);

            welcomeToast?.Draw(width, height, feed.Count);

            // The TOP card uses output pixels and restores the GUI transform.
            // Welcomes have moving wings outside their card; conservatively
            // reserve the whole animation rather than just its background.
            bool bannerActive = giftEffects != null && Time.unscaledTime < giftEffects.BannerUntil;
            topPoints.Draw(playerManager, hudVisible,
                controlsVisible || welcomeToast.ActiveUserId != null || bannerActive);

            chatBubbles.Draw(Camera.main, topPoints.Opacity > 0f ? topPoints.GetPixelRect() : default, controlsVisible);

            GUI.matrix = originalMatrix;
        }

        private void DrawControls()
        {
            GUI.Box(new Rect(18, 84, 365, 320), GUIContent.none, panelStyle);
            
            GUIStyle activeTab = new GUIStyle(buttonStyle) { normal = { background = buttonHover, textColor = Color.yellow } };
            if (GUI.Button(new Rect(34, 96, 155, 32), "KẾT NỐI & DEMO", controlTab == 0 ? activeTab : buttonStyle)) controlTab = 0;
            if (GUI.Button(new Rect(195, 96, 169, 32), "THÊM KHÁCH VIP", controlTab == 1 ? activeTab : buttonStyle)) controlTab = 1;

            if (controlTab == 0)
            {
                GUI.Label(new Rect(34, 136, 330, 28), connectionStatus, labelStyle);
                GUI.Label(new Rect(34, 166, 330, 24), $"Người chơi {playerManager?.Count ?? 0}   Event {events}   Diamond {diamonds}", smallStyle);
                username = GUI.TextField(new Rect(34, 196, 205, 38), username, inputStyle);
                if (GUI.Button(new Rect(247, 196, 118, 38), "KẾT NỐI", buttonStyle))
                {
                    PlayerPrefs.SetString("TikTokUsername", username);
                    client?.ConnectTikTok(username);
                    controlsVisible = false;
                    restoreControlsAfterCinematic = false;
                }
                if (GUI.Button(new Rect(34, 246, 100, 40), "DEMO 30", buttonStyle)) client?.StartDemo(30);
                if (GUI.Button(new Rect(142, 246, 100, 40), "DEMO 400", buttonStyle)) client?.StartDemo(400);
                if (GUI.Button(new Rect(250, 246, 115, 40), "GIFT 100", buttonStyle)) client?.DemoGift(100);
                
                if (GUI.Button(new Rect(34, 296, 100, 36), "RESET", buttonStyle)) client?.ResetGame();
                if (GUI.Button(new Rect(142, 296, 100, 36), "ẨN (F1)", buttonStyle)) controlsVisible = false;
                if (GUI.Button(new Rect(250, 296, 115, 36), "GIFT 1000", buttonStyle)) client?.DemoGift(1000);
                
                if (GUI.Button(new Rect(34, 344, 100, 36), "NGẮT LIVE", buttonStyle)) client?.DisconnectTikTok();
                if (GUI.Button(new Rect(142, 344, 100, 36), "DỪNG DEMO", buttonStyle)) client?.StopDemo();
                if (GUI.Button(new Rect(250, 344, 115, 36), chromaMode ? "HIỆN SÂN" : "CHROMA", buttonStyle)) ToggleChroma();
            }
            else
            {
                GUI.Label(new Rect(34, 140, 330, 28), "1. Tên nhân vật muốn thêm:", labelStyle);
                manualUsername = GUI.TextField(new Rect(34, 172, 205, 38), manualUsername, inputStyle);
                if (GUI.Button(new Rect(247, 172, 118, 38), "THÊM VÀO", buttonStyle))
                {
                    SendManualEvent(new TikTokEvent { type = "member", userId = manualUsername, nickname = manualUsername, action = "join", joinedNow = true, durationMs = 3000 });
                }
                
                GUI.Label(new Rect(34, 226, 330, 28), "2. Tặng quà cho nhân vật này:", labelStyle);
                if (GUI.Button(new Rect(34, 260, 100, 40), "1 Hoa", buttonStyle))
                {
                    SendManualEvent(new TikTokEvent { type = "gift", userId = manualUsername, nickname = manualUsername, action = "gift", giftName = "Hoa hồng", diamondCount = 1, durationMs = 3000 });
                }
                if (GUI.Button(new Rect(142, 260, 100, 40), "100 Xu", buttonStyle))
                {
                    SendManualEvent(new TikTokEvent { type = "gift", userId = manualUsername, nickname = manualUsername, action = "gift", giftName = "Mũ", diamondCount = 100, durationMs = 5000 });
                }
                if (GUI.Button(new Rect(250, 260, 115, 40), "1000 Xu", buttonStyle))
                {
                    SendManualEvent(new TikTokEvent { type = "gift", userId = manualUsername, nickname = manualUsername, action = "gift", giftName = "Siêu xe", diamondCount = 1000, durationMs = 8000 });
                }
                
                if (GUI.Button(new Rect(34, 320, 100, 36), "ẨN (F1)", buttonStyle)) controlsVisible = false;
                if (GUI.Button(new Rect(142, 320, 223, 36), chromaMode ? "HIỆN SÂN" : "CHROMA", buttonStyle)) ToggleChroma();
            }
        }

        private void SendManualEvent(TikTokEvent data)
        {
            // Connected tests must pass through the session's score owner so
            // every overlay and reconnect receives the same points.
            if (client != null && client.IsConnected)
                client.DemoManualEvent(data.type, data.nickname, data.diamondCount, data.giftName);
            else
                HandleEvent(data);
        }

        private void ToggleChroma()
        {
            chromaMode = !chromaMode;
            if (environmentRenderers.Count == 0)
            {
                foreach (Renderer renderer in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                    if (!renderer.transform.IsChildOf(playerManager.transform)) environmentRenderers.Add(renderer);
                environmentLights.AddRange(FindObjectsByType<Light>(FindObjectsSortMode.None));
            }
            foreach (Renderer renderer in environmentRenderers) if (renderer != null) renderer.enabled = !chromaMode;
            foreach (Light light in environmentLights) if (light != null) light.enabled = !chromaMode;
            RenderSettings.fog = !chromaMode;
            if (Camera.main != null) Camera.main.backgroundColor = chromaMode ? new Color(0f, 1f, 0f) : new Color(0.01f, 0.005f, 0.025f);
        }

        private void DrawFeed(float height)
        {
            float y = height - 34f - feed.Count * 27f;
            float scale = Mathf.Clamp(Mathf.Min(Screen.width / 1080f, Screen.height / 1080f), 0.72f, 1.25f);
            float feedWidth = points.Top.Length > 0 && hudVisible
                ? Mathf.Max(40f,topPoints.GetPixelRect().x/scale-38f) : 520f;
            foreach (FeedEntry item in feed)
            {
                Color old = GUI.color;
                GUI.color = item.Color;
                GUI.Label(new Rect(24, y, Mathf.Min(520f,feedWidth), 25), item.Text, smallStyle);
                GUI.color = old;
                y += 27f;
            }
        }

        private void EnsureStyles()
        {
            if (stylesReady) return;
            stylesReady = true;
            Texture2D panel = Solid(new Color(0.018f, 0.018f, 0.055f, 0.91f));
            Texture2D button = Solid(new Color(0.14f, 0.08f, 0.28f, 0.98f));
            buttonHover = Solid(new Color(0.08f, 0.45f, 0.62f, 1f));
            Texture2D input = Solid(new Color(0.01f, 0.01f, 0.025f, 0.98f));
            panelStyle = new GUIStyle(GUI.skin.box) { normal = { background = panel }, border = new RectOffset(8, 8, 8, 8) };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, normal = { textColor = new Color(0.75f, 0.93f, 1f) }, clipping = TextClipping.Clip };
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(0.82f, 0.86f, 0.94f) } };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { background = button, textColor = Color.white }, hover = { background = buttonHover, textColor = Color.white }, active = { background = buttonHover, textColor = Color.white } };
            inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 18, normal = { background = input, textColor = Color.white }, padding = new RectOffset(12, 8, 8, 6) };
            bannerStyle = new GUIStyle(panelStyle) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { background = Solid(new Color(0.28f, 0.025f, 0.24f, 0.95f)), textColor = Color.white } };
        }

        private static Texture2D Solid(Color color)
        {
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels(new[] { color, color, color, color });
            texture.Apply();
            return texture;
        }

        private readonly struct FeedEntry
        {
            public readonly string Text;
            public readonly Color Color;
            public readonly float Time;
            public FeedEntry(string text, Color color, float time) { Text = text; Color = color; Time = time; }
        }

        private void OnDestroy()
        {
            if (client != null) client.EventReceived -= HandleEvent;
        }
    }
}
