using System.Collections.Generic;
using System.Globalization;
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
        private readonly Dictionary<string, Donor> donors = new();
        private readonly List<FeedEntry> feed = new();

        // ── Toast chào người mới vào sàn ────────────────────────────────
        // Mỗi người chỉ được chào một lần mỗi phiên, nên cần nhớ ai đã chào.
        private readonly HashSet<string> greeted = new();
        private readonly Queue<string> welcomeQueue = new();
        private string welcomeName = string.Empty;
        private float welcomeUntil;
        private const float WelcomeSeconds = 2.5f;
        private const float WelcomeFadeSeconds = 0.5f;
        // Chặn dồn toast khi có cả chục người vào một lúc: giữ tối đa 6 lượt,
        // quá thì bỏ người đến sau thay vì kéo dài hàng đợi vô hạn.
        private const int WelcomeQueueLimit = 6;
        // Live dài có thể qua hàng nghìn người xem; xả bộ nhớ khi tập quá lớn.
        private const int GreetedLimit = 4000;
        private string username = "";
        private string connectionStatus = "Đang chờ Node server...";
        private int events;
        private int diamonds;
        private float partyEnergy;
        private bool controlsVisible;
        private bool hudVisible;
        private bool chromaMode;
        private int cinematicVersion;
        private bool restoreControlsAfterCinematic;
        private readonly List<Renderer> environmentRenderers = new();
        private readonly List<Light> environmentLights = new();
        private bool stylesReady;
        private GUIStyle panelStyle;
        private GUIStyle titleStyle;
        private GUIStyle labelStyle;
        private GUIStyle smallStyle;
        private GUIStyle buttonStyle;
        private GUIStyle inputStyle;
        private GUIStyle rankStyle;
        private GUIStyle bannerStyle;
        private GUIStyle welcomeNameFill;
        private GUIStyle welcomeNameOutline;
        private GUIStyle welcomeSubFill;
        private GUIStyle welcomeSubOutline;
        // Chữ nổi trơn không có nền, nên viền phải vẽ đủ 8 hướng mới đọc được
        // trên mọi màu cảnh phía sau.
        private static readonly Vector2[] OutlineOffsets =
        {
            new(-1f, -1f), new(1f, -1f), new(-1f, 1f), new(1f, 1f),
            new(-1f, 0f), new(1f, 0f), new(0f, -1f), new(0f, 1f)
        };
        // Toast không đứng một chỗ: mỗi lượt chọn một điểm neo khác, tránh trùng
        // ngay với lượt trước. Toạ độ chuẩn hoá 0..1 theo khung GUI đã scale.
        private static readonly Vector2[] WelcomeAnchors =
        {
            new(0.50f, 0.185f), new(0.28f, 0.245f), new(0.72f, 0.245f),
            new(0.26f, 0.370f), new(0.74f, 0.370f), new(0.50f, 0.450f),
            new(0.30f, 0.650f), new(0.70f, 0.650f), new(0.50f, 0.720f)
        };
        private Vector2 welcomeAnchor = WelcomeAnchors[0];
        private int welcomeAnchorIndex = -1;
        private Texture2D energyBack;
        private Texture2D energyFill;
        // DrawControls() dùng texture này cho tab đang chọn, nên phải là field
        // chứ không phải biến cục bộ trong EnsureStyles().
        private Texture2D buttonHover;

        private int controlTab = 0;
        private string manualUsername = "Khách VIP";

        public void Initialize(TikTokWebSocketClient socketClient, PlayerManager manager, GiftEffectManager effects)
        {
            client = socketClient;
            playerManager = manager;
            giftEffects = effects;
            clubCamera = Camera.main?.GetComponent<ClubCameraController>();
            client.EventReceived += HandleEvent;
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

            // Toast chào hiện lần lượt: chỉ lấy người kế tiếp khi người trước đã hết giờ.
            if (Time.unscaledTime >= welcomeUntil && welcomeQueue.Count > 0)
            {
                welcomeName = welcomeQueue.Dequeue();
                welcomeUntil = Time.unscaledTime + WelcomeSeconds;
                welcomeAnchor = NextWelcomeAnchor();
            }
        }

        private void HandleEvent(TikTokEvent liveEvent)
        {
            if (liveEvent.type == "status") connectionStatus = liveEvent.message;
            if (liveEvent.type is "member" or "chat" or "gift" or "like" or "follow" or "share") events++;

            if (liveEvent.type == "snapshot")
            {
                donors.Clear();
                diamonds = 0;
                foreach (TikTokPlayerData donor in liveEvent.vipScores ?? System.Array.Empty<TikTokPlayerData>())
                {
                    donors[donor.userId] = new Donor(donor.userId, donor.nickname, donor.score);
                    diamonds += donor.score;
                }
                // Snapshot là phiên đang có sẵn (lúc nối lại Node). Đánh dấu đã chào
                // hết để không bắn một loạt toast cho người vào từ trước.
                foreach (TikTokPlayerData player in liveEvent.players ?? System.Array.Empty<TikTokPlayerData>())
                    if (!string.IsNullOrWhiteSpace(player.userId)) greeted.Add(player.userId);
            }
            else if (liveEvent.type == "gift")
            {
                diamonds += liveEvent.diamondCount;
                donors.TryGetValue(liveEvent.userId, out Donor current);
                donors[liveEvent.userId] = new Donor(liveEvent.userId, liveEvent.nickname, current.Score + liveEvent.diamondCount);
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
                donors.Clear();
                feed.Clear();
                greeted.Clear();
                welcomeQueue.Clear();
                welcomeName = string.Empty;
                welcomeUntil = 0f;
            }

            playerManager.Handle(liveEvent);
            TryWelcome(liveEvent);
            if (liveEvent.type is "gift" or "snapshot") UpdateTopPlayers();
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
                if (actor == null) return;
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
            playerManager.UpdateTopRanks(donors.Values.OrderByDescending(donor => donor.Score).Take(3).Select(donor => donor.UserId));
        }

        private void AddEnergy(float amount)
        {
            partyEnergy += Mathf.Max(0f, amount);
            if (partyEnergy < 1000f) return;
            partyEnergy %= 1000f;
            playerManager.DanceAll(7f);
            giftEffects.PartyBurst();
        }

        /// <summary>
        /// Xếp một lượt chào cho người vừa xuất hiện trên sàn lần đầu.
        /// Không dùng cờ joinedNow của bridge vì cờ đó chỉ bật cho lượt vào bằng
        /// từ khoá/follow/share — người vào bằng gift, hoặc mọi người khi joinMode
        /// là all_interactions, sẽ không được tính. Điều kiện ở đây là "đã có mặt
        /// trên sàn và chưa từng được chào", nên đúng với cả hai joinMode.
        /// </summary>
        private void TryWelcome(TikTokEvent liveEvent)
        {
            if (playerManager == null || string.IsNullOrWhiteSpace(liveEvent.userId)) return;
            if (liveEvent.type is not ("member" or "chat" or "gift" or "like" or "follow" or "share")) return;
            // NPC lấp sàn không phải người xem thật.
            if (liveEvent.userId.StartsWith("npc-")) return;
            // Find() trả null nếu người này bị chặn vì spectatorOnly, tức chưa lên sàn.
            if (playerManager.Find(liveEvent.userId) == null) return;
            if (greeted.Count > GreetedLimit) greeted.Clear();
            if (!greeted.Add(liveEvent.userId)) return;

            if (welcomeQueue.Count >= WelcomeQueueLimit) return;
            string name = string.IsNullOrWhiteSpace(liveEvent.nickname)
                ? (string.IsNullOrWhiteSpace(liveEvent.uniqueId) ? "Khách mới" : liveEvent.uniqueId)
                : liveEvent.nickname;
            int[] elements = StringInfo.ParseCombiningCharacters(name);
            if (elements.Length > 22) name = name[..elements[22]].TrimEnd() + "…";
            welcomeQueue.Enqueue(name);
        }

        /// <summary>Chọn điểm neo khác lượt trước để toast không luôn hiện một chỗ.</summary>
        private Vector2 NextWelcomeAnchor()
        {
            if (WelcomeAnchors.Length < 2) return WelcomeAnchors[0];
            int index = welcomeAnchorIndex;
            while (index == welcomeAnchorIndex) index = Random.Range(0, WelcomeAnchors.Length);
            welcomeAnchorIndex = index;
            return WelcomeAnchors[index];
        }

        /// <summary>
        /// Vẽ chữ cách điệu: từng ký tự một, giãn cách, nhấp nhô dạng sóng, và mỗi
        /// ký tự có viền tối vẽ lệch 8 hướng. Một GUI.Label không làm được vì cần
        /// dịch riêng từng ký tự và cần hai lớp màu khác nhau.
        /// </summary>
        private static void DrawWaveText(float x, float y, string text, GUIStyle fill, GUIStyle outline,
                                         float spacing, float amplitude, float phase, float outlineSize)
        {
            float cursor = x;
            int index = 0;
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string glyph = elements.GetTextElement();
                float glyphWidth = fill.CalcSize(new GUIContent(glyph)).x;
                // Lệch pha theo vị trí ký tự để sóng chạy dọc chuỗi.
                float bob = Mathf.Sin(Time.unscaledTime * 4.2f + index * 0.62f + phase) * amplitude;
                Rect slot = new(cursor, y + bob, glyphWidth + 4f, fill.fontSize + 12f);
                if (glyph != " ")
                {
                    foreach (Vector2 offset in OutlineOffsets)
                        GUI.Label(new Rect(slot.x + offset.x * outlineSize, slot.y + offset.y * outlineSize,
                            slot.width, slot.height), glyph, outline);
                    GUI.Label(slot, glyph, fill);
                }
                cursor += glyphWidth + spacing;
                index++;
            }
        }

        /// <summary>Đo bề rộng chuỗi đúng như DrawWaveText sẽ vẽ, không vẽ gì.</summary>
        private static float MeasureWaveText(string text, GUIStyle style, float spacing)
        {
            float total = 0f;
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
                total += style.CalcSize(new GUIContent(elements.GetTextElement())).x + spacing;
            return Mathf.Max(0f, total - spacing);
        }

        private void DrawWelcome(float width, float height)
        {
            if (string.IsNullOrEmpty(welcomeName) || Time.unscaledTime >= welcomeUntil) return;

            const string subtitle = "VỪA VÀO SÀN";
            const float blockHeight = 62f;
            const float nameSpacing = 2.5f;
            const float subSpacing = 5f;

            float nameWidth = MeasureWaveText(welcomeName, welcomeNameFill, nameSpacing);
            float subWidth = MeasureWaveText(subtitle, welcomeSubFill, subSpacing);
            float blockWidth = Mathf.Max(nameWidth, subWidth);

            float remaining = welcomeUntil - Time.unscaledTime;
            float elapsed = WelcomeSeconds - remaining;
            float intro = Mathf.Clamp01(elapsed / 0.28f);
            float outro = Mathf.Clamp01(remaining / WelcomeFadeSeconds);
            float ease = 1f - Mathf.Pow(1f - intro, 3f);          // ease-out cubic
            float alpha = Mathf.Min(ease, outro);

            // Neo ngẫu nhiên, nhưng kẹp lại để chữ luôn nằm trọn trong khung và
            // không đè lên header phía trên hay vùng feed phía dưới.
            float centerX = Mathf.Clamp(width * welcomeAnchor.x,
                blockWidth * 0.5f + 24f, Mathf.Max(blockWidth * 0.5f + 24f, width - blockWidth * 0.5f - 24f));
            float y = Mathf.Clamp(height * welcomeAnchor.y, 88f, Mathf.Max(88f, height - blockHeight - 210f));
            // Trượt xuống khi vào, nhấc lên nhẹ khi tan.
            y += -24f * (1f - ease) - 12f * (1f - outro);

            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;
            // Pop nhẹ từ 90% lên 100% cho cảm giác nảy vào.
            GUIUtility.ScaleAroundPivot(
                Vector2.one * (0.90f + 0.10f * ease),
                new Vector2(centerX, y + blockHeight * 0.5f));

            GUI.color = new Color(1f, 1f, 1f, alpha);
            // Hai dòng căn giữa độc lập nên tên dài hay ngắn vẫn cân so với nhau.
            DrawWaveText(centerX - nameWidth * 0.5f, y, welcomeName,
                welcomeNameFill, welcomeNameOutline, nameSpacing, 3f, 0f, 2f);
            DrawWaveText(centerX - subWidth * 0.5f, y + 42f, subtitle,
                welcomeSubFill, welcomeSubOutline, subSpacing, 1.5f, 1.9f, 1.5f);

            GUI.color = savedColor;
            GUI.matrix = savedMatrix;
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

            if (hudVisible)
            {
                DrawHeader(width);
                DrawEnergy(width);
                DrawTopDonors(width);
            }
            DrawFeed(height);
            if (controlsVisible) DrawControls();

            if (giftEffects != null && Time.unscaledTime < giftEffects.BannerUntil)
                GUI.Box(new Rect(width * 0.5f - 310f, 142f, 620f, 68f), giftEffects.BannerText, bannerStyle);

            DrawWelcome(width, height);

            GUI.matrix = originalMatrix;
        }

        private void DrawHeader(float width)
        {
            GUI.Box(new Rect(18, 16, width - 36, 58), GUIContent.none, panelStyle);
            GUI.Label(new Rect(34, 23, 245, 28), "ÔNG CHÚ MMO", titleStyle);
            string node = client != null && client.IsConnected ? "NODE ONLINE" : "NODE OFFLINE";
            GUI.Label(new Rect(275, 29, 95, 22), node, smallStyle);
            if (width > 900f) GUI.Label(new Rect(width - 250, 24, 230, 24), "F1 CONTROL   F2 CHROMA", smallStyle);
        }

        private void DrawEnergy(float width)
        {
            float barWidth = width > 900f ? Mathf.Min(380f, width * 0.34f) : Mathf.Max(160f, width - 410f);
            float x = width > 900f ? width * 0.5f - barWidth * 0.5f : 390f;
            GUI.Label(new Rect(x, 20, barWidth, 22), "PARTY ENERGY", smallStyle);
            GUI.DrawTexture(new Rect(x, 48, barWidth, 13), energyBack);
            GUI.DrawTexture(new Rect(x, 48, barWidth * Mathf.Clamp01(partyEnergy / 1000f), 13), energyFill);
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
                    client.ConnectTikTok(username);
                    controlsVisible = false;
                    restoreControlsAfterCinematic = false;
                }
                if (GUI.Button(new Rect(34, 246, 100, 40), "DEMO 30", buttonStyle)) client.StartDemo(30);
                if (GUI.Button(new Rect(142, 246, 100, 40), "DEMO 400", buttonStyle)) client.StartDemo(400);
                if (GUI.Button(new Rect(250, 246, 115, 40), "GIFT 100", buttonStyle)) client.DemoGift(100);
                
                if (GUI.Button(new Rect(34, 296, 100, 36), "RESET", buttonStyle)) client.ResetGame();
                if (GUI.Button(new Rect(142, 296, 100, 36), "ẨN (F1)", buttonStyle)) controlsVisible = false;
                if (GUI.Button(new Rect(250, 296, 115, 36), "GIFT 1000", buttonStyle)) client.DemoGift(1000);
                
                if (GUI.Button(new Rect(34, 344, 100, 36), "NGẮT LIVE", buttonStyle)) client.DisconnectTikTok();
                if (GUI.Button(new Rect(142, 344, 100, 36), "DỪNG DEMO", buttonStyle)) client.StopDemo();
                if (GUI.Button(new Rect(250, 344, 115, 36), chromaMode ? "HIỆN SÂN" : "CHROMA", buttonStyle)) ToggleChroma();
            }
            else
            {
                GUI.Label(new Rect(34, 140, 330, 28), "1. Tên nhân vật muốn thêm:", labelStyle);
                manualUsername = GUI.TextField(new Rect(34, 172, 205, 38), manualUsername, inputStyle);
                if (GUI.Button(new Rect(247, 172, 118, 38), "THÊM VÀO", buttonStyle))
                {
                    HandleEvent(new TikTokEvent { type = "member", userId = manualUsername, nickname = manualUsername, action = "join", joinedNow = true, durationMs = 3000 });
                }
                
                GUI.Label(new Rect(34, 226, 330, 28), "2. Tặng quà cho nhân vật này:", labelStyle);
                if (GUI.Button(new Rect(34, 260, 100, 40), "1 Hoa", buttonStyle))
                {
                    HandleEvent(new TikTokEvent { type = "gift", userId = manualUsername, nickname = manualUsername, action = "gift", giftName = "Hoa hồng", diamondCount = 1, durationMs = 3000 });
                }
                if (GUI.Button(new Rect(142, 260, 100, 40), "100 Xu", buttonStyle))
                {
                    HandleEvent(new TikTokEvent { type = "gift", userId = manualUsername, nickname = manualUsername, action = "gift", giftName = "Mũ", diamondCount = 100, durationMs = 5000 });
                }
                if (GUI.Button(new Rect(250, 260, 115, 40), "1000 Xu", buttonStyle))
                {
                    HandleEvent(new TikTokEvent { type = "gift", userId = manualUsername, nickname = manualUsername, action = "gift", giftName = "Siêu xe", diamondCount = 1000, durationMs = 8000 });
                }
                
                if (GUI.Button(new Rect(34, 320, 100, 36), "ẨN (F1)", buttonStyle)) controlsVisible = false;
                if (GUI.Button(new Rect(142, 320, 223, 36), chromaMode ? "HIỆN SÂN" : "CHROMA", buttonStyle)) ToggleChroma();
            }
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

        private void DrawTopDonors(float width)
        {
            Donor[] top = donors.Values.OrderByDescending(donor => donor.Score).Take(3).ToArray();
            float x = width - 358f;
            GUI.Box(new Rect(x, 84, 340, 150), GUIContent.none, panelStyle);
            GUI.Label(new Rect(x + 18, 94, 304, 28), "TOP 3 TẶNG QUÀ", titleStyle);
            for (int index = 0; index < top.Length; index++)
            {
                Color old = GUI.color;
                GUI.color = index == 0 ? new Color(1f, 0.82f, 0.25f) : index == 1 ? new Color(0.72f, 0.9f, 1f) : new Color(1f, 0.55f, 0.32f);
                GUI.Label(new Rect(x + 18, 126 + index * 32, 304, 28), $"#{index + 1}  {top[index].Name}    {top[index].Score}", rankStyle);
                GUI.color = old;
            }
        }

        private void DrawFeed(float height)
        {
            float y = height - 34f - feed.Count * 27f;
            foreach (FeedEntry item in feed)
            {
                Color old = GUI.color;
                GUI.color = item.Color;
                GUI.Label(new Rect(24, y, 520, 25), item.Text, smallStyle);
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
            energyBack = Solid(new Color(0.05f, 0.05f, 0.12f, 0.95f));
            energyFill = Solid(new Color(0.1f, 0.9f, 1f, 1f));
            panelStyle = new GUIStyle(GUI.skin.box) { normal = { background = panel }, border = new RectOffset(8, 8, 8, 8) };
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
            labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, normal = { textColor = new Color(0.75f, 0.93f, 1f) }, clipping = TextClipping.Clip };
            smallStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, normal = { textColor = new Color(0.82f, 0.86f, 0.94f) } };
            rankStyle = new GUIStyle(smallStyle) { fontSize = 18, fontStyle = FontStyle.Bold };
            buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, normal = { background = button, textColor = Color.white }, hover = { background = buttonHover, textColor = Color.white }, active = { background = buttonHover, textColor = Color.white } };
            inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = 18, normal = { background = input, textColor = Color.white }, padding = new RectOffset(12, 8, 8, 6) };
            bannerStyle = new GUIStyle(panelStyle) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, normal = { background = Solid(new Color(0.28f, 0.025f, 0.24f, 0.95f)), textColor = Color.white } };
            // ── Chữ chào người mới ──────────────────────────────────────
            // Không có nền nên chữ vẽ hai lớp: lớp viền tối lệch 8 hướng, rồi
            // lớp màu đè lên. GUI.color chỉ điều khiển alpha nên phải tách
            // style riêng cho từng màu.
            Color outlineColor = new(0.01f, 0.01f, 0.04f, 1f);
            welcomeNameFill = new GUIStyle(GUI.skin.label) { fontSize = 32, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, normal = { textColor = Color.white } };
            welcomeNameOutline = new GUIStyle(welcomeNameFill) { normal = { textColor = outlineColor } };
            welcomeSubFill = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft, normal = { textColor = new Color(0.35f, 0.98f, 1f, 1f) } };
            welcomeSubOutline = new GUIStyle(welcomeSubFill) { normal = { textColor = outlineColor } };
        }

        private static Texture2D Solid(Color color)
        {
            Texture2D texture = new(2, 2, TextureFormat.RGBA32, false);
            texture.SetPixels(new[] { color, color, color, color });
            texture.Apply();
            return texture;
        }

        private readonly struct Donor
        {
            public readonly string UserId;
            public readonly string Name;
            public readonly int Score;
            public Donor(string userId, string name, int score)
            {
                UserId = userId;
                Name = string.IsNullOrWhiteSpace(name) ? "TikTok user" : name;
                Score = score;
            }
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
