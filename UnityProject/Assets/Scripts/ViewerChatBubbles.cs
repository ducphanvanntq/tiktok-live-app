using System;
using System.Collections.Generic;
using UnityEngine;

namespace TikTokLiveGame
{
    /// <summary>Bounded, latest-message-wins chat. Drawn after the camera's LateUpdate in output pixels.</summary>
    public sealed class ViewerChatBubbles : MonoBehaviour
    {
        internal const float Lifetime = 4.5f;
        internal const float UpdateInterval = 0.65f;
        internal const float FadeTime = 0.35f;
        internal const int Capacity = 400;
        internal const int VisibleLimit = 6;
        private readonly List<Entry> entries = new();
        private readonly List<Entry> candidates = new();
        private readonly List<Placement> visible = new();
        private Texture2D bubble;
        private Texture2D sparkle;
        private GUIStyle textStyle;
        private readonly GUIContent content = new();
        private long sequence;
        internal int ActiveCount => entries.Count;
        internal IReadOnlyList<Placement> Visible => visible;
        internal bool AssetsReady => bubble != null && sparkle != null;

        private void Awake()
        {
            bubble = Resources.Load<Texture2D>("ChatBubbles/white-bubble");
            sparkle = Resources.Load<Texture2D>("ChatBubbles/sparkle");
        }

        internal static bool IsRealViewer(PlayerActor actor) => actor != null && !string.IsNullOrWhiteSpace(actor.UserId) &&
            !actor.IsNpc && !actor.UserId.StartsWith("demo-", StringComparison.Ordinal) && actor.UserId != "master-test";

        internal void Handle(TikTokEvent data, PlayerManager players, float now)
        {
            if (data == null) return;
            if (data.type is "reset" or "snapshot") { entries.Clear(); visible.Clear(); return; }
            if (data.type != "chat") return;
            PlayerActor actor = players.Find(data.userId);
            // A spectator's comment must not bypass the existing join policy.
            if (!IsRealViewer(actor)) return;
            string message = ChatBubbleText.Clean(data.comment);
            if (message.Length == 0) return;
            Entry entry = entries.Find(item => item.Actor == actor);
            if (entry != null && now >= entry.ExpiresAt)
            {
                entries.Remove(entry);
                entry = null;
            }
            if (entry == null)
            {
                if (entries.Count >= Capacity)
                {
                    Entry oldest = entries[0];
                    foreach (Entry item in entries) if (item.Sequence < oldest.Sequence) oldest = item;
                    entries.Remove(oldest);
                }
                entry = new Entry { Actor = actor, Message = message, StartedAt = now, ChangedAt = now };
                entries.Add(entry);
            }
            else
            {
                // One pending slot per person, never a backlog of obsolete chats.
                entry.Pending = message == entry.Message ? null : message;
                if (now - entry.ChangedAt >= UpdateInterval) ApplyPending(entry, now);
                if (now > entry.ExpiresAt - FadeTime) entry.StartedAt = now;
            }
            entry.ExpiresAt = now + Lifetime;
            entry.Sequence = ++sequence;
        }

        // Process once after the transport's Update has delivered this frame's
        // batch. Incoming messages replace pending text before it is displayed.
        private void LateUpdate() => Tick(Time.unscaledTime);

        internal void Tick(float now)
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry entry = entries[i];
                if (!IsRealViewer(entry.Actor) || !entry.Actor.gameObject.activeInHierarchy || now >= entry.ExpiresAt)
                { entries.RemoveAt(i); continue; }
                if (now - entry.ChangedAt >= UpdateInterval) ApplyPending(entry, now);
            }
        }

        private static void ApplyPending(Entry entry, float now)
        {
            if (entry.Pending == null) return;
            entry.Message = entry.Pending;
            entry.Pending = null;
            entry.ChangedAt = now;
        }

        internal string MessageFor(string id) => entries.Find(entry => entry.Actor != null && entry.Actor.UserId == id)?.Message;

        internal void Draw(Camera camera, Rect reserved, bool controlsOpen)
        {
            if (Event.current.type != EventType.Repaint) return;
            visible.Clear();
            if (camera == null || controlsOpen || !AssetsReady) return;
            EnsureStyle();
            Matrix4x4 oldMatrix = GUI.matrix;
            Color oldColor = GUI.color;
            GUI.matrix = Matrix4x4.identity;
            try
            {
                float now = Time.unscaledTime;
                float scale = Mathf.Clamp(Mathf.Min(Screen.width / 540f, Screen.height / 960f), 0.6f, 2f);
                int fontSize = Mathf.Max(11, Mathf.RoundToInt(14f * scale));
                textStyle.fontSize = fontSize;
                Rect safe = Screen.safeArea;
                if (safe.width <= 0f || safe.height <= 0f) safe = new Rect(0, 0, Screen.width, Screen.height);
                safe = new Rect(safe.xMin + 10f * scale, Screen.height - safe.yMax + 10f * scale,
                    safe.width - 20f * scale, safe.height - 20f * scale);
                candidates.Clear();
                candidates.AddRange(entries);
                candidates.Sort((a, b) => b.Sequence.CompareTo(a.Sequence));
                foreach (Entry entry in candidates)
                {
                    if (visible.Count >= VisibleLimit) break;
                    if (entry.Actor == null || !entry.Actor.TryGetChatAnchor(camera, out Vector2 anchor, out float actorAlpha)) continue;
                    if (!safe.Contains(anchor)) continue;
                    float maxWidth = 174f * scale;
                    if (entry.CachedMessage != entry.Message || entry.CachedFontSize != fontSize || entry.CachedWidth != maxWidth)
                    {
                        entry.DisplayText = ChatBubbleText.Fit(entry.Message, maxWidth, Measure);
                        entry.TextWidth = Measure(entry.DisplayText);
                        entry.CachedMessage = entry.Message;
                        entry.CachedFontSize = fontSize;
                        entry.CachedWidth = maxWidth;
                    }
                    float width = Mathf.Clamp(entry.TextWidth + 52f * scale, 142f * scale, 226f * scale);
                    float height = 68f * scale;
                    // Art has transparent margins: the actual pointer ends at 86% height.
                    Rect rect = new(anchor.x - width * 0.5f, anchor.y - height * 0.86f - 5f * scale, width, height);
                    Rect occupied = new(rect.xMin - 5f * scale, rect.yMin - 5f * scale, rect.width + 10f * scale, rect.height);
                    // Never pin an off-screen person's bubble to the edge or detach its tail.
                    if (!Contains(safe, occupied) || (reserved.width > 0f && occupied.Overlaps(reserved))) continue;
                    bool blocked = false;
                    foreach (Placement placed in visible) if (occupied.Overlaps(placed.Occupied)) { blocked = true; break; }
                    if (blocked) continue;
                    float age = now - entry.StartedAt;
                    float opacity = Mathf.Min(Mathf.Clamp01(age / 0.18f), Mathf.Clamp01((entry.ExpiresAt - now) / FadeTime)) * actorAlpha;
                    visible.Add(new Placement(entry.Actor.UserId, rect, occupied, anchor, entry.DisplayText, opacity));
                    DrawBubble(entry, rect, opacity, age, scale);
                }
            }
            finally { GUI.matrix = oldMatrix; GUI.color = oldColor; }
        }

        private void DrawBubble(Entry entry, Rect rect, float opacity, float age, float scale)
        {
            GUI.color = new Color(1f, 1f, 1f, opacity);
            GUI.DrawTexture(rect, bubble, ScaleMode.StretchToFill, true);
            Rect textRect = new(rect.x + 23f * scale, rect.y + rect.height * 0.21f, rect.width - 46f * scale, rect.height * 0.47f);
            GUI.Label(textRect, entry.DisplayText, textStyle);
            // Three small independent glints stay on/outside the rim, never on the words.
            for (int i = 0; i < 3; i++)
            {
                float pulse = 0.5f + 0.5f * Mathf.Sin(age * 5f + i * 2.3f);
                float size = (i == 0 ? 38f : 30f) * scale * (0.8f + pulse * 0.2f);
                float x = i == 0 ? rect.x + rect.width * 0.08f : i == 1 ? rect.x + rect.width * 0.9f : Mathf.Lerp(rect.x + rect.width * 0.16f, rect.x + rect.width * 0.83f, Mathf.Repeat(age * 0.28f, 1f));
                float y = rect.y + rect.height * (i == 1 ? 0.3f : 0.16f);
                GUI.color = new Color(1f, 1f, 1f, opacity * (0.35f + pulse * 0.65f));
                // The generated sprite includes generous transparent margins.
                // Sample its central star so a small UI glint stays visible.
                GUI.DrawTextureWithTexCoords(new Rect(x - size * 0.5f, y - size * 0.5f, size, size),
                    sparkle, new Rect(0.23f, 0.13f, 0.54f, 0.74f), true);
            }
        }

        private float Measure(string text) { content.text = text; return textStyle.CalcSize(content).x; }
        private void EnsureStyle()
        {
            textStyle ??= new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                richText = false, wordWrap = false, clipping = TextClipping.Clip,
                padding = new RectOffset(), margin = new RectOffset(),
                normal = { textColor = new Color(0.055f, 0.065f, 0.075f, 1f) }
            };
        }
        private static bool Contains(Rect outer, Rect inner) => inner.xMin >= outer.xMin && inner.yMin >= outer.yMin && inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;

        private sealed class Entry
        {
            internal PlayerActor Actor;
            internal string Message, Pending, DisplayText, CachedMessage;
            internal float StartedAt, ChangedAt, ExpiresAt, TextWidth, CachedWidth;
            internal int CachedFontSize;
            internal long Sequence;
        }

        internal readonly struct Placement
        {
            internal readonly string UserId, Text;
            internal readonly Rect Rect, Occupied;
            internal readonly Vector2 Anchor;
            internal readonly float Opacity;
            internal Placement(string id, Rect rect, Rect occupied, Vector2 anchor, string text, float opacity)
            { UserId = id; Rect = rect; Occupied = occupied; Anchor = anchor; Text = text; Opacity = opacity; }
        }
    }
}
