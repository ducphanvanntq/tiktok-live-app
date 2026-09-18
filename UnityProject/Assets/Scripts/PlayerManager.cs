using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace TikTokLiveGame
{
    public sealed class PlayerManager : MonoBehaviour
    {
        [SerializeField] private int maxPlayers = 400;
        [SerializeField] private float playerTtlSeconds = 600f;
        [SerializeField] private int npcCrowdTarget = 20;
        private readonly Dictionary<string, PlayerActor> players = new();
        private readonly List<string> playerOrder = new();
        private readonly Dictionary<string, int> crowdSlots = new();
        private int focusVersion;
        private string focusedUserId;
        private readonly NpcNamePool npcNames = new();
        private const float NpcAmbientAlpha = 1f;
        private const float FocusedCrowdAlpha = 0.22f;
        private const float FocusedNpcAlpha = 0.14f;
        private const float FocusedTopAlpha = 0.4f;
        private const int CrowdColumns = 24;
        private const int CrowdRows = 18;
        private const int CrowdCapacity = CrowdColumns * CrowdRows;


        public int Count => players.Count;
        public int NpcCount => players.Values.Count(actor => actor.IsNpc);
        public int ViewerCount => Count - NpcCount;
        internal int ViewerCapacity => Mathf.Clamp(maxPlayers, 1, 400);
        internal int NpcTarget => Mathf.Clamp(npcCrowdTarget, 0, 20);
        internal int UniqueSlotCount => crowdSlots.Values.Distinct().Count();
        public PlayerActor Find(string userId) => !string.IsNullOrWhiteSpace(userId) && players.TryGetValue(userId, out PlayerActor actor) ? actor : null;

        public bool TryGetViewerBounds(out Bounds bounds)
        {
            PlayerActor first = players.Values.FirstOrDefault(actor => actor != null && !actor.IsNpc);
            if (first == null)
            {
                bounds = new Bounds(Vector3.zero, Vector3.zero);
                return false;
            }

            bounds = new Bounds(first.transform.position, Vector3.zero);
            foreach (PlayerActor actor in players.Values)
                if (actor != null && !actor.IsNpc) bounds.Encapsulate(actor.transform.position);
            return true;
        }

        public void DanceAll(float seconds = 6f)
        {
            foreach (PlayerActor actor in players.Values) actor.Dance(seconds);
        }

        private void Start() => EnsureNpcCrowd();

        public void FocusPlayer(string userId, float seconds)
        {
            PlayerActor target = Find(userId);
            if (target == null || target.IsNpc) return;
            int version = ++focusVersion;
            focusedUserId = userId;
            foreach (KeyValuePair<string, PlayerActor> pair in players)
            {
                bool focused = pair.Key == userId;
                if (focused) pair.Value.ReturnToAssignedSlot();
                pair.Value.SetVisibility(focused ? 1f : FocusBackgroundAlpha(pair.Value));
                pair.Value.SetGiftFocus(focused);
            }
            StartCoroutine(RestoreFocus(version, Mathf.Clamp(seconds, 2.5f, 12f)));
        }

        private System.Collections.IEnumerator RestoreFocus(int version, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (version != focusVersion) yield break;
            focusedUserId = null;
            foreach (PlayerActor actor in players.Values)
            {
                actor.SetGiftFocus(false);
                actor.ReturnToAssignedSlot();
            }
            ApplyAmbientVisibility();
        }

        public void UpdateTopRanks(IEnumerable<string> rankedUserIds)
        {
            Dictionary<string, int> ranks = rankedUserIds
                .Take(3)
                .Select((id, index) => new { id, rank = index + 1 })
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.id) &&
                    players.TryGetValue(item.id, out PlayerActor actor) &&
                    !actor.IsNpc)
                .ToDictionary(item => item.id, item => item.rank);
            bool changed = false;
            foreach (KeyValuePair<string, PlayerActor> pair in players)
            {
                int rank = ranks.TryGetValue(pair.Key, out int value) ? value : 0;
                if (pair.Value.TopRank == rank) continue;
                pair.Value.SetTopRank(rank);
                changed = true;
            }
            if (changed) ApplyVisibilityState();
        }

        internal bool OverlapsScreenRect(Camera camera, Rect rect)
        {
            if (camera == null) return true;
            foreach (PlayerActor actor in players.Values)
                if (actor != null && actor.OverlapsScreenRect(camera,rect)) return true;
            return false;
        }

        public PlayerActor GetOrCreate(TikTokEvent data)
        {
            // NPC identities belong to this manager; incoming viewer events must
            // not rename them or create additional synthetic crowd members.
            if (data == null || string.IsNullOrWhiteSpace(data.userId) || data.userId.StartsWith("npc-", System.StringComparison.Ordinal)) return null;
            if (players.TryGetValue(data.userId, out PlayerActor existing))
            {
                existing.UpdateIdentity(data);
                existing.Touch();
                return existing;
            }

            TikTokPlayerData playerData = new()
            {
                userId = data.userId,
                uniqueId = data.uniqueId,
                nickname = data.nickname,
                avatar = data.avatar,
                giftPower = data.giftPower
            };
            return Create(playerData);
        }

        public void Handle(TikTokEvent data)
        {
            if (data.type == "reset")
            {
                Clear();
                return;
            }
            if (data.type == "snapshot")
            {
                // A reconnect replaces the server roster, not the local crowd.
                // Preserve NPC instances, names, animation clocks and floor slots.
                focusVersion++;
                focusedUserId = null;
                foreach (string id in playerOrder.Where(id => !players[id].IsNpc).ToArray()) Remove(id);
                foreach (PlayerActor npc in players.Values)
                {
                    npc.SetGiftFocus(false);
                    npc.SetVisibility(NpcAmbientAlpha);
                }
                foreach (TikTokPlayerData player in data.players ?? System.Array.Empty<TikTokPlayerData>())
                    if (player != null && !string.IsNullOrWhiteSpace(player.userId) && !player.userId.StartsWith("npc-", System.StringComparison.Ordinal))
                        Create(player);
                EnsureNpcCrowd();
                return;
            }
            if (data.type is not ("member" or "chat" or "gift" or "like" or "follow" or "share")) return;

            if (data.spectatorOnly && Find(data.userId) == null) return;

            PlayerActor actor = GetOrCreate(data);
            if (actor == null) return;

            if (data.type == "chat")
            {
                if (!string.IsNullOrWhiteSpace(data.action))
                {
                    ApplyAction(actor, data, data.durationMs > 0 ? data.durationMs / 1000f : 5f);
                }
                else
                {
                    string command = Normalize(data.comment);
                    if (command.Contains("doi nv")) actor.ChangeCharacter();
                    else if (command.Contains("di vong") || command.Contains("walk")) actor.Walk();
                    else if (command is "jump" or "nhay") actor.Jump();
                    else if (command.Contains("dance")) actor.Dance();
                }
            }
            else if (data.type == "gift")
            {
                actor.AddGiftPower(data.diamondCount);
                float duration = data.durationMs > 0 ? data.durationMs / 1000f : Mathf.Clamp(data.diamondCount, 3f, 7f);
                ApplyAction(actor, data, duration);
            }
            else if (data.type is "follow" or "share") actor.Celebrate();
        }

        private static void ApplyAction(PlayerActor actor, TikTokEvent data, float duration)
        {
            if (data.action == "join") actor.ReturnToAssignedSlot();
            else if (data.action == "change") actor.ChangeCharacter();
            else if (data.action == "walk") actor.Walk(duration);
            else if (data.action == "jump") actor.Jump(duration);
            else if (data.action == "grow") actor.Grow(duration);
            else if (data.action is "camera" or "vip" or "topdj" or "fireworks" or "medal")
                actor.Celebrate(Mathf.Clamp(duration, 2.5f, 15f));
            else actor.Dance(Mathf.Clamp(duration, 2f, 10f));

            if (!string.IsNullOrWhiteSpace(data.label) && (data.action is "medal" or "vip" or "topdj"))
                actor.ShowTitle(data.label, Mathf.Max(3f, duration));
        }

        private PlayerActor Create(TikTokPlayerData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.userId)) return null;
            if (players.TryGetValue(data.userId, out PlayerActor existing)) return existing;
            bool isNpc = data.userId.StartsWith("npc-", System.StringComparison.Ordinal);
            if (!isNpc && ViewerCount >= ViewerCapacity) RemoveOldestViewer();
            int index = players.Count;

            GameObject playerObject = new($"Player_{data.userId}");
            playerObject.transform.SetParent(transform);
            PlayerActor actor = playerObject.AddComponent<PlayerActor>();
            Color color = Color.HSVToRGB(Mathf.Repeat(index * 0.173f, 1f), 0.72f, 1f);
            actor.Initialize(data, Vector3.zero, color);
            if (!string.IsNullOrWhiteSpace(data.titleLabel)) actor.ShowTitle(data.titleLabel, 10f);
            players[data.userId] = actor;
            playerOrder.Add(data.userId);
            crowdSlots[data.userId] = AllocateCrowdSlot();
            ReflowPlayers();
            ApplyVisibilityState();
            return actor;
        }

        private void ApplyVisibilityState()
        {
            if (string.IsNullOrEmpty(focusedUserId))
            {
                ApplyAmbientVisibility();
                return;
            }
            foreach (KeyValuePair<string, PlayerActor> pair in players)
                pair.Value.SetVisibility(pair.Key == focusedUserId ? 1f : FocusBackgroundAlpha(pair.Value));
        }

        private void ApplyAmbientVisibility()
        {
            foreach (PlayerActor actor in players.Values)
                actor.SetVisibility(actor.IsNpc ? NpcAmbientAlpha : 1f);
        }

        private static float FocusBackgroundAlpha(PlayerActor actor)
        {
            if (actor.IsTopRanked) return FocusedTopAlpha;
            return actor.IsNpc ? FocusedNpcAlpha : FocusedCrowdAlpha;
        }

        private void ReflowPlayers()
        {
            int count = playerOrder.Count;
            float scale = count <= 30 ? 0.65f : count <= 80 ? 0.56f : count <= 140 ? 0.46f : count <= 200 ? 0.38f : count <= 280 ? 0.32f : 0.27f;
            const float xSpacing = 0.5f;
            const float frontEdge = 3.75f;
            const float backEdge = -3.65f;
            float zSpacing = (frontEdge - backEdge) / (CrowdRows - 1);
            foreach (string userId in playerOrder)
            {
                if (!players.TryGetValue(userId, out PlayerActor actor)) continue;
                if (!crowdSlots.TryGetValue(userId, out int slot))
                {
                    slot = AllocateCrowdSlot();
                    crowdSlots[userId] = slot;
                }
                int row = slot / CrowdColumns;
                int column = slot % CrowdColumns;
                float stagger = row % 2 == 0 ? 0f : xSpacing * 0.5f;
                float x = (column - (CrowdColumns - 1) * 0.5f) * xSpacing + stagger;
                float z = frontEdge - row * zSpacing;
                float depthRatio = row / (float)(CrowdRows - 1);
                float perspectiveScale = scale * Mathf.Lerp(1.06f, 0.86f, depthRatio);
                actor.SetCrowdSlot(new Vector3(x, 0f, z), perspectiveScale);
            }
        }

        private int AllocateCrowdSlot()
        {
            HashSet<int> occupied = crowdSlots.Values.ToHashSet();
            List<int> available = Enumerable.Range(0, CrowdCapacity).Where(slot => !occupied.Contains(slot)).ToList();
            if (available.Count == 0) return Random.Range(0, CrowdCapacity);
            int minimumCellDistanceSquared = occupied.Count < 30 ? 9 : occupied.Count < 80 ? 4 : occupied.Count < 140 ? 2 : 1;
            List<int> separated = available.Where(candidate => occupied.All(other =>
            {
                int rowDelta = candidate / CrowdColumns - other / CrowdColumns;
                int columnDelta = candidate % CrowdColumns - other % CrowdColumns;
                return rowDelta * rowDelta + columnDelta * columnDelta >= minimumCellDistanceSquared;
            })).ToList();
            List<int> choices = separated.Count > 0 ? separated : available;
            return choices[Random.Range(0, choices.Count)];
        }

        private void Update()
        {
            RemoveInactiveViewers(Time.unscaledTime);
        }

        internal void RemoveInactiveViewers(float now)
        {
            float cutoff = now - playerTtlSeconds;
            foreach (string id in players.Where(pair => !pair.Value.IsNpc && pair.Value.LastActiveTime < cutoff).Select(pair => pair.Key).ToArray())
                Remove(id);
        }

        public void Clear()
        {
            foreach (PlayerActor actor in players.Values) Destroy(actor.gameObject);
            focusVersion++;
            focusedUserId = null;
            players.Clear();
            playerOrder.Clear();
            crowdSlots.Clear();
            EnsureNpcCrowd();
        }

        private void RemoveOldestViewer()
        {
            KeyValuePair<string, PlayerActor> oldest = players.Where(pair => !pair.Value.IsNpc)
                .OrderBy(pair => pair.Value.LastActiveTime).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key)) Remove(oldest.Key);
        }

        private void Remove(string id)
        {
            if (!players.Remove(id, out PlayerActor actor)) return;
            playerOrder.Remove(id);
            crowdSlots.Remove(id);
            Destroy(actor.gameObject);
            ReflowPlayers();
        }

        private void EnsureNpcCrowd()
        {
            HashSet<string> occupiedNames = players.Values.Where(actor => actor.IsNpc).Select(actor => actor.Nickname).ToHashSet();
            for (int index = 0; index < NpcTarget; index++)
            {
                string id = $"npc-{index:000}";
                if (players.ContainsKey(id)) continue;
                string name = npcNames.Next(occupiedNames);
                occupiedNames.Add(name);
                Create(new TikTokPlayerData
                {
                    userId = id,
                    uniqueId = id,
                    nickname = name,
                    avatar = string.Empty,
                    giftPower = 0
                });
            }
        }

        private static string Normalize(string value)
        {
            string decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
            StringBuilder builder = new();
            foreach (char character in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    builder.Append(character == 'đ' ? 'd' : character == 'Đ' ? 'D' : character);
            return builder.ToString().Normalize(NormalizationForm.FormC).Trim().ToLowerInvariant();
        }
    }
}
