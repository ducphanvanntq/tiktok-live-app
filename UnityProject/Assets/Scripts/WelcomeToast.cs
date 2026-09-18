using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace TikTokLiveGame
{
    /// <summary>One compact viewer card with a fixed, reusable flock of butterflies.</summary>
    public sealed class WelcomeToast : MonoBehaviour
    {
        internal const float Duration = 2.7f;
        internal const int QueueLimit = 6;
        private const int RememberLimit = 4000;
        private const int FlockSize = 12;
        private const int FlightPoints = 12;
        private const int TrailSamples = 5;
        private const float TrailLifetime = 0.8f;
        private const float CardWidth = 320f;
        private const float CardHeight = 76f;
        private readonly Queue<Entry> pending = new();
        private readonly HashSet<string> greeted = new();
        private readonly Queue<string> greetedOrder = new();
        private readonly Butterfly[] butterflies = new Butterfly[FlockSize];
        private System.Random random = new();
        private readonly Queue<int> themeDeck = new();
        private static readonly Vector2[] Anchors =
        {
            new(0.05f, 0.76f), new(0.5f, 0.83f), new(0.95f, 0.89f),
            new(0.05f, 0.88f), new(0.95f, 0.77f), new(0.5f, 0.9f)
        };
        private Entry current;
        private float startedAt;
        private int themeIndex = -1;
        private int anchorIndex = -1;
        private Texture2D atlas;
        private Texture2D glow;
        private Texture2D sparkle;
        private Frame[] frames;
        private Theme[] themes;
        private GUIStyle nameStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle initialStyle;
        private Rect layout;
        private Vector2 viewport;
        private float frameRadius;
        private bool ready;
        private int flockDrawCalls;

        internal int PendingCount => pending.Count;
        internal string ActiveUserId => current?.UserId;
        internal int ThemeIndex => themeIndex;
        internal int AnchorIndex => anchorIndex;
        internal Rect CardRect => layout;
        internal bool HasActiveAvatar => current?.Avatar != null;
        internal bool AssetsReady => ready;
        internal int FlockDrawCalls => flockDrawCalls;
        internal float ActiveAge => Time.unscaledTime - startedAt;
        internal int ThemeCount => themes?.Length ?? 0;
        internal Color NameColor => themes[themeIndex].Text;
        internal Color BackgroundColor => themes[themeIndex].Background;
        internal int ButterflyCount => butterflies.Length;
        internal Vector2 FlightPosition(int index, float time) => Flight(layout, butterflies[index], time);
        internal float ButterflyRadius(int index) => butterflies[index].Size * frameRadius;
        internal void SetPreviewSeed(int seed) => random = new System.Random(seed);
        internal float FlightSpeedLimit(int index)
        {
            Butterfly b = butterflies[index];
            float speed = 0f;
            for (int i = 1; i < b.Path.Length; i++)
            {
                Vector2 delta = b.Path[i] - b.Path[i - 1];
                delta.x *= b.Side % 2 == 0 ? layout.width * 0.5f : layout.height * 0.85f;
                speed = Mathf.Max(speed, delta.magnitude / b.StepSeconds);
            }
            return speed;
        }
        internal Color PaletteNameColor(int index) => themes[index].Text;
        internal Color PaletteBackgroundColor(int index) => themes[index].Background;

        [Serializable] private sealed class Manifest { public Atlas atlas; public Palette[] proposedThemes; }
        [Serializable] private sealed class Atlas { public int width; public int height; public AtlasFrame[] frames; }
        [Serializable] private sealed class AtlasFrame
        {
            public int[] rectTopLeft;
            public int[] contentBoundsInCellTopLeft;
            public float[] suggestedPivotUnityNormalized;
        }
        [Serializable] private sealed class Palette { public string background; public string border; public string text; public string[] butterflies; }
        private struct Frame { public Rect UV; public Vector2 Size; public Vector2 Pivot; }
        private struct Theme { public Color Background; public Color Border; public Color Text; public Color[] Butterflies; }
        private sealed class Butterfly
        {
            public readonly Vector2[] Path = new Vector2[FlightPoints];
            public int Side;
            public float StepSeconds, Offset, Size, Flap, FlapRate, Wobble;
            public Color Color;
        }
        private sealed class Entry
        {
            public string UserId;
            public string Name;
            public string Initial;
            public readonly GUIContent DisplayName = new();
            public float TextWidth = -1f;
            public Sprite Avatar;
        }

        private void Awake()
        {
            atlas = Resources.Load<Texture2D>("Welcome/butterfly-flap-atlas");
            TextAsset json = Resources.Load<TextAsset>("Welcome/asset-manifest");
            if (atlas == null || json == null)
            {
                Debug.LogError("Welcome disabled: assets are missing from Resources/Welcome.");
                enabled = false;
                return;
            }
            if (!TryReadManifest(json.text, atlas.width, atlas.height, out Manifest manifest, out string error))
            {
                Debug.LogError("Welcome disabled: " + error);
                enabled = false;
                return;
            }
            frames = new Frame[manifest.atlas.frames.Length];
            for (int i = 0; i < frames.Length; i++)
            {
                AtlasFrame data = manifest.atlas.frames[i];
                int[] r = data.rectTopLeft;
                int[] b = data.contentBoundsInCellTopLeft;
                // Trim the large transparent cell margins when drawing, preserving
                // original pixel scale and the measured pivot in every wing pose.
                const float padding = 12f;
                float x = r[0] + b[0] - padding;
                float y = r[1] + b[1] - padding;
                float w = b[2] - b[0] + padding * 2f;
                float h = b[3] - b[1] + padding * 2f;
                frames[i] = new Frame
                {
                    UV = new Rect(x / manifest.atlas.width, 1f - (y + h) / manifest.atlas.height,
                        w / manifest.atlas.width, h / manifest.atlas.height),
                    Size = new Vector2(w, h),
                    Pivot = new Vector2(r[0] + data.suggestedPivotUnityNormalized[0] * r[2] - x,
                        r[1] + (1f - data.suggestedPivotUnityNormalized[1]) * r[3] - y)
                };
                Frame frame = frames[i];
                // Furthest corner from the measured pivot bounds every rotated
                // pose, including transparent padding, instead of guessing a radius.
                Vector2 extent = new(Mathf.Max(frame.Pivot.x, frame.Size.x - frame.Pivot.x),
                    Mathf.Max(frame.Pivot.y, frame.Size.y - frame.Pivot.y));
                frameRadius = Mathf.Max(frameRadius, extent.magnitude / 300f);
            }
            themes = new Theme[manifest.proposedThemes.Length];
            for (int i = 0; i < themes.Length; i++)
            {
                Palette p = manifest.proposedThemes[i];
                Color[] colors = Array.ConvertAll(p.butterflies, Hex);
                themes[i] = new Theme { Background = Hex(p.background), Border = Hex(p.border), Text = Hex(p.text), Butterflies = colors };
            }
            glow = CreateLightTexture(false);
            sparkle = CreateLightTexture(true);
            for (int i = 0; i < butterflies.Length; i++) butterflies[i] = new Butterfly();
            ready = true;
        }

        internal static bool ValidateManifest(string json, int width, int height, out string error) =>
            TryReadManifest(json, width, height, out _, out error);

        private static bool TryReadManifest(string json, int width, int height, out Manifest manifest, out string error)
        {
            manifest = null;
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(json)) throw new FormatException("Empty manifest.");
                Manifest parsed = JsonUtility.FromJson<Manifest>(json);
                if (parsed?.atlas == null || width <= 0 || height <= 0 || parsed.atlas.width != width || parsed.atlas.height != height ||
                    parsed.atlas.frames?.Length != 4) throw new FormatException("Atlas dimensions or wing frames are invalid.");
                foreach (AtlasFrame frame in parsed.atlas.frames)
                {
                    if (frame?.rectTopLeft?.Length != 4 || frame.contentBoundsInCellTopLeft?.Length != 4 ||
                        frame.suggestedPivotUnityNormalized?.Length != 2) throw new FormatException("Incomplete wing frame.");
                    int[] r = frame.rectTopLeft;
                    int[] b = frame.contentBoundsInCellTopLeft;
                    if (r[0] < 0 || r[1] < 0 || r[2] <= 0 || r[3] <= 0 || r[2] > width || r[3] > height ||
                        r[0] > width - r[2] || r[1] > height - r[3] || b[0] < 12 || b[1] < 12 ||
                        b[2] <= b[0] || b[3] <= b[1] || b[2] > r[2] - 12 || b[3] > r[3] - 12)
                        throw new FormatException("Wing bounds exceed the atlas cell.");
                    foreach (float pivot in frame.suggestedPivotUnityNormalized)
                        if (float.IsNaN(pivot) || float.IsInfinity(pivot) || pivot < 0f || pivot > 1f)
                            throw new FormatException("Invalid wing pivot.");
                }
                if (parsed.proposedThemes == null || parsed.proposedThemes.Length < 2)
                    throw new FormatException("At least two palettes are required.");
                foreach (Palette palette in parsed.proposedThemes)
                {
                    if (palette == null || !ValidColor(palette.background) || !ValidColor(palette.border) || !ValidColor(palette.text) ||
                        palette.butterflies == null || palette.butterflies.Length < 2)
                        throw new FormatException("Incomplete palette.");
                    foreach (string color in palette.butterflies)
                        if (!ValidColor(color)) throw new FormatException("Invalid butterfly color.");
                }
                manifest = parsed;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool ValidColor(string value) => !string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value, out _);

        public void Handle(TikTokEvent data, PlayerManager players)
        {
            if (!ready || data == null) return;
            if (data.type == "reset") { Clear(); return; }
            if (data.type == "snapshot")
            {
                // Reconnects refresh the roster, not the lifetime of queued UI.
                foreach (TikTokPlayerData player in data.players ?? Array.Empty<TikTokPlayerData>()) Remember(player?.userId);
                return;
            }
            if (data.type is not ("member" or "chat" or "gift" or "like" or "follow" or "share")) return;
            if (string.IsNullOrWhiteSpace(data.userId) || data.userId.StartsWith("npc-", StringComparison.Ordinal)) return;
            if (players == null || players.Find(data.userId) == null || greeted.Contains(data.userId)) return;
            if (pending.Count >= QueueLimit) return;
            string name = string.IsNullOrWhiteSpace(data.nickname) ? data.uniqueId : data.nickname;
            name = string.IsNullOrWhiteSpace(name) ? "Khách mới" : name.Replace('\n', ' ').Replace('\r', ' ').Trim();
            Entry entry = new() { UserId = data.userId, Name = name, Initial = StringInfo.GetNextTextElement(name).ToUpperInvariant() };
            pending.Enqueue(entry);
            Remember(data.userId);
            // A late download only updates this entry; it cannot replace another viewer.
            AvatarService.Instance?.Load(data.avatar, sprite => entry.Avatar = sprite);
        }

        private bool Remember(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !greeted.Add(id)) return false;
            greetedOrder.Enqueue(id);
            while (greetedOrder.Count > RememberLimit) greeted.Remove(greetedOrder.Dequeue());
            return true;
        }

        public void Clear()
        {
            pending.Clear();
            themeDeck.Clear();
            greeted.Clear();
            greetedOrder.Clear();
            current = null;
        }

        private void Update()
        {
            if (!ready) return;
            if (current != null && Time.unscaledTime - startedAt < Duration) return;
            current = pending.Count > 0 ? pending.Dequeue() : null;
            if (current == null) return;
            startedAt = Time.unscaledTime;
            themeIndex = NextTheme();
            anchorIndex = NextIndex(anchorIndex, Anchors.Length);
            viewport = Vector2.zero;
            Theme theme = themes[themeIndex];
            for (int i = 0; i < butterflies.Length; i++) PlanFlight(butterflies[i], i, theme);
        }

        private int NextTheme()
        {
            if (themeDeck.Count == 0)
            {
                int[] order = new int[themes.Length];
                for (int i = 0; i < order.Length; i++) order[i] = i;
                for (int i = order.Length - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (order[i], order[j]) = (order[j], order[i]);
                }
                if (order[0] == themeIndex) (order[0], order[1]) = (order[1], order[0]);
                foreach (int index in order) themeDeck.Enqueue(index);
            }
            return themeDeck.Dequeue();
        }

        private void PlanFlight(Butterfly butterfly, int index, Theme theme)
        {
            // Four above, four below, two on each side. Each gets its own curved
            // wandering path, depth and pace; there is no shared orbit or direction.
            butterfly.Side = index < 4 ? 0 : index < 8 ? 2 : index < 10 ? 1 : 3;
            bool horizontal = butterfly.Side % 2 == 0;
            float home = horizontal ? -0.84f + (index % 4) * 0.56f : (index % 2 == 0 ? -0.5f : 0.5f);
            float reach = horizontal ? Range(0.25f, 0.45f) : Range(0.55f, 0.9f);
            float phase = Range(0f, Mathf.PI * 2f);
            float bend = Range(1.05f, 2.15f) * (random.Next(2) == 0 ? -1f : 1f);
            butterfly.StepSeconds = Range(0.49f, 0.78f);
            butterfly.Offset = Range(0f, 0.5f);
            butterfly.Size = index % 4 == 0 ? Range(33f, 37f) : Range(23f, 31f);
            butterfly.Flap = Range(0f, 4f);
            butterfly.FlapRate = Range(9f, 16f);
            butterfly.Wobble = phase;
            butterfly.Color = theme.Butterflies[random.Next(theme.Butterflies.Length)];
            for (int point = 0; point < butterfly.Path.Length; point++)
            {
                float along = home + Mathf.Sin(phase + point * bend) * reach + Range(-0.09f, 0.09f);
                // Negative depth brings bodies over the card edge. The avatar
                // and text are painted afterwards, so the content stays legible.
                float depth = index % 3 == 0 ? Range(-13f, -4f) : Range(-10f, 18f);
                butterfly.Path[point] = new Vector2(Mathf.Clamp(along, -1.06f, 1.06f), depth);
            }
        }

        private int NextIndex(int previous, int count) => previous < 0 ? random.Next(count) : (previous + random.Next(1, count)) % count;
        private float Range(float min, float max) => Mathf.Lerp(min, max, (float)random.NextDouble());

        public void Draw(float width, float height, int feedCount)
        {
            if (!AssetsReady || current == null || Event.current.type != EventType.Repaint) return;
            EnsureStyles();
            float bottom = height - 76f - (feedCount > 0 ? 34f + feedCount * 27f : 20f) - CardHeight;
            if (viewport != new Vector2(width, height))
            {
                viewport = new Vector2(width, height);
                // Reserve room around the card for the flock, and below it for
                // the existing feed. The anchor stays fixed throughout this welcome.
                float w = Mathf.Min(CardWidth, Mathf.Max(140f, width - 160f));
                float x = Mathf.Lerp(80f, Mathf.Max(80f, width - w - 80f), Anchors[anchorIndex].x);
                float y = Mathf.Clamp(height * Anchors[anchorIndex].y, 80f, Mathf.Max(80f, bottom));
                layout = new Rect(x, y, w, CardHeight);
            }
            // A growing feed takes priority immediately. Do not drop the card
            // back down when old feed entries expire during the same welcome.
            layout.y = Mathf.Min(layout.y, Mathf.Max(80f, bottom));
            float elapsed = Time.unscaledTime - startedAt;
            float ease = 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / 0.3f), 3f);
            float outro = Mathf.Clamp01((Duration - elapsed) / 0.4f);
            float alpha = Mathf.Min(ease, outro);
            Rect card = layout;
            card.y += 18f * (1f - ease) - 10f * (1f - outro);
            Theme theme = themes[themeIndex];
            Matrix4x4 savedMatrix = GUI.matrix;
            Color savedColor = GUI.color;
            GUI.matrix = savedMatrix * Around(card.center, Matrix4x4.Scale(Vector3.one * (0.94f + 0.06f * ease)));
            GUI.color = new Color(1f, 1f, 1f, alpha);

            Round(Expand(card, 4f), WithAlpha(theme.Border, 0.07f), 22f);
            Round(card, theme.Border, 18f);
            Round(Expand(card, -1.3f), WithAlpha(theme.Background, 0.98f), 17f);
            DrawFlock(card, elapsed);
            Rect ring = new(card.x + 13f, card.y + 13f, 50f, 50f);
            Round(ring, WithAlpha(theme.Border, 0.9f), 25f);
            Rect portrait = Expand(ring, -2f);
            Round(portrait, Color.Lerp(theme.Background, theme.Border, 0.18f), 23f);
            if (current.Avatar != null)
                DrawRoundedTexture(portrait, current.Avatar.texture, ScaleMode.ScaleAndCrop, Color.white, 23f);
            else GUI.Label(portrait, current.Initial, initialStyle);
            Round(new Rect(ring.xMax - 10f, ring.yMax - 10f, 12f, 12f), theme.Background, 6f);
            Round(new Rect(ring.xMax - 8f, ring.yMax - 8f, 8f, 8f), theme.Butterflies[1], 4f);
            float textWidth = card.width - 99f;
            if (!Mathf.Approximately(current.TextWidth, textWidth))
            {
                current.TextWidth = textWidth;
                current.DisplayName.text = FitName(current.Name, textWidth);
            }
            nameStyle.normal.textColor = theme.Text;
            subtitleStyle.normal.textColor = Color.Lerp(theme.Border, theme.Text, 0.55f);
            GUI.Label(new Rect(card.x + 76f, card.y + 14f, textWidth, 26f), current.DisplayName, nameStyle);
            GUI.Label(new Rect(card.x + 76f, card.y + 41f, textWidth, 20f), "Vừa vào sàn", subtitleStyle);
            GUI.color = savedColor;
            GUI.matrix = savedMatrix;
        }

        private static Vector2 Flight(Rect card, Butterfly butterfly, float time)
        {
            // Quadratic B-splines stay within their control points' bounds and
            // join with continuous velocity, including when the butterfly turns.
            float clock = Mathf.Clamp((time + TrailLifetime) / butterfly.StepSeconds + butterfly.Offset,
                0f, FlightPoints - 2.001f);
            int segment = Mathf.FloorToInt(clock);
            float t = clock - segment;
            Vector2 p = 0.5f * ((1f - t) * (1f - t) * butterfly.Path[segment] +
                (-2f * t * t + 2f * t + 1f) * butterfly.Path[segment + 1] +
                t * t * butterfly.Path[segment + 2]);
            Vector2 offset = butterfly.Side switch
            {
                0 => new Vector2(p.x * card.width * 0.5f, -card.height * 0.5f - p.y),
                1 => new Vector2(card.width * 0.5f + p.y, p.x * card.height * 0.85f),
                2 => new Vector2(p.x * card.width * 0.5f, card.height * 0.5f + p.y),
                _ => new Vector2(-card.width * 0.5f - p.y, p.x * card.height * 0.85f)
            };
            return card.center + offset;
        }

        private void DrawFlock(Rect card, float elapsed)
        {
            Color savedColor = GUI.color;
            Matrix4x4 savedMatrix = GUI.matrix;
            flockDrawCalls = 0;
            // Complete trails first, then wings, so a neighbour's trail cannot
            // paint over another butterfly. Particles stay at their birth position.
            foreach (Butterfly butterfly in butterflies)
            {
                Vector2 previous = Flight(card, butterfly, elapsed);
                for (int trail = 1; trail <= TrailSamples; trail++)
                {
                    float age = trail * TrailLifetime / TrailSamples;
                    if (age > elapsed) break;
                    Vector2 point = Flight(card, butterfly, elapsed - age);
                    float fade = Mathf.Pow(1f - age / TrailLifetime, 1.5f);
                    DrawRibbon(previous, point, 7f * fade + 1f,
                        WithAlpha(Color.Lerp(butterfly.Color, Color.white, 0.4f), savedColor.a * 0.8f * fade));
                    previous = point;
                }
                const float emissionInterval = 0.27f;
                float emissionOffset = butterfly.Flap * 0.023f;
                int newest = Mathf.FloorToInt((elapsed - emissionOffset) / emissionInterval);
                for (int particle = newest; particle >= Mathf.Max(0, newest - 2); particle--)
                {
                    float born = particle * emissionInterval + emissionOffset;
                    float age = elapsed - born;
                    if (age < 0f || age >= TrailLifetime) continue;
                    float life = age / TrailLifetime;
                    float seed = butterfly.Wobble + particle * 2.39996f;
                    Vector2 point = Flight(card, butterfly, born) +
                        new Vector2(Mathf.Sin(seed) * (2f + life * 7f), life * 8f + Mathf.Cos(seed) * 3f);
                    float fade = Mathf.SmoothStep(0f, 1f, age / 0.06f) * Mathf.Pow(1f - life, 0.85f);
                    float twinkle = 0.82f + 0.18f * Mathf.Sin(age * 24f + seed);
                    DrawLight(point, (particle % 3 == 0 ? 13f : 8f) * (1f - life * 0.5f), sparkle,
                        WithAlpha(Color.Lerp(butterfly.Color, Color.white, 0.8f), savedColor.a * fade * twinkle));
                }
            }
            foreach (Butterfly butterfly in butterflies)
            {
                Vector2 position = Flight(card, butterfly, elapsed);
                Vector2 velocity = (Flight(card, butterfly, elapsed + 0.02f) - Flight(card, butterfly, elapsed - 0.02f)) / 0.04f;
                float tilt = Mathf.Clamp(velocity.x * 0.7f, -48f, 48f) + Mathf.Sin(elapsed * 3f + butterfly.Wobble) * 8f;
                DrawLight(position, butterfly.Size * 1.55f, glow, WithAlpha(butterfly.Color, savedColor.a * 0.32f));
                GUI.matrix = savedMatrix * Around(position,
                    Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, tilt)));
                float flap = elapsed * butterfly.FlapRate + butterfly.Flap;
                int frameIndex = Mathf.FloorToInt(flap) % frames.Length;
                float blend = Mathf.SmoothStep(0f, 1f, flap - Mathf.Floor(flap));
                DrawWing(position, butterfly, frames[frameIndex], savedColor.a * (1f - blend));
                DrawWing(position, butterfly, frames[(frameIndex + 1) % frames.Length], savedColor.a * blend);
                GUI.matrix = savedMatrix;
            }
            GUI.color = savedColor;
        }

        private void DrawWing(Vector2 position, Butterfly butterfly, Frame frame, float alpha)
        {
            float scale = butterfly.Size / 300f;
            Rect rect = new(position.x - frame.Pivot.x * scale, position.y - frame.Pivot.y * scale,
                frame.Size.x * scale, frame.Size.y * scale);
            GUI.color = WithAlpha(butterfly.Color, alpha);
            GUI.DrawTextureWithTexCoords(rect, atlas, frame.UV, true);
            flockDrawCalls++;
        }

        private void DrawLight(Vector2 position, float size, Texture2D texture, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(new Rect(position.x - size * 0.5f, position.y - size * 0.5f, size, size), texture);
            flockDrawCalls++;
        }

        private void DrawRibbon(Vector2 from, Vector2 to, float width, Color color)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length < 0.05f) return;
            Matrix4x4 matrix = GUI.matrix;
            GUI.matrix = matrix * Around(from, Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg)));
            GUI.color = color;
            GUI.DrawTexture(new Rect(from.x - width * 0.5f, from.y - width * 0.5f, length + width, width), glow);
            flockDrawCalls++;
            GUI.matrix = matrix;
        }

        private static Texture2D CreateLightTexture(bool star)
        {
            const int size = 64;
            Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
            {
                name = star ? "Welcome sparkle" : "Welcome soft glow",
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs((x + 0.5f) / size * 2f - 1f);
                    float dy = Mathf.Abs((y + 0.5f) / size * 2f - 1f);
                    float radius = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha = Mathf.Exp(-5f * radius * radius) * Mathf.Clamp01((1f - radius) * 4f);
                    if (star)
                    {
                        float rays = Mathf.Pow(Mathf.Clamp01(1f - Mathf.Min(dx, dy) * 9f), 2f) *
                            Mathf.Pow(Mathf.Clamp01(1f - Mathf.Max(dx, dy)), 1.4f);
                        alpha = Mathf.Max(alpha * 0.45f, rays);
                    }
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            texture.SetPixels(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void OnDestroy()
        {
            if (glow != null) Destroy(glow);
            if (sparkle != null) Destroy(sparkle);
        }

        private string FitName(string text, float width)
        {
            if (nameStyle.CalcSize(new GUIContent(text)).x <= width) return text;
            string result = string.Empty;
            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string next = result + elements.GetTextElement();
                if (nameStyle.CalcSize(new GUIContent(next + "…")).x > width) break;
                result = next;
            }
            return result + "…";
        }

        private void EnsureStyles()
        {
            if (nameStyle != null) return;
            nameStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 21, fontStyle = FontStyle.Bold, richText = false, wordWrap = false,
                clipping = TextClipping.Clip, padding = new RectOffset(), alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
            subtitleStyle = new GUIStyle(nameStyle) { fontSize = 15, fontStyle = FontStyle.Normal };
            initialStyle = new GUIStyle(nameStyle) { alignment = TextAnchor.MiddleCenter };
        }
        private static Color Hex(string text) => ColorUtility.TryParseHtmlString(text, out Color color) ? color : Color.white;
        private static Matrix4x4 Around(Vector2 pivot, Matrix4x4 transform) =>
            Matrix4x4.Translate(new Vector3(pivot.x, pivot.y, 0f)) * transform *
            Matrix4x4.Translate(new Vector3(-pivot.x, -pivot.y, 0f));
        private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);
        private static Rect Expand(Rect r, float amount) => new(r.x - amount, r.y - amount, r.width + 2f * amount, r.height + 2f * amount);
        private static void Round(Rect r, Color color, float radius) => DrawRoundedTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, color, radius);

        internal static void DrawRoundedTexture(Rect r, Texture texture, ScaleMode mode, Color color, float radius)
        {
            // This overload supplies a color instead of using GUI.color. Fold
            // the parent tint/opacity into it once, including on avatar textures.
            Color saved = GUI.color;
            GUI.color = Color.white;
            GUI.DrawTexture(r, texture, mode, true, 0f, color * saved, 0f, radius);
            GUI.color = saved;
        }
    }
}
