using System;
using System.Collections.Generic;
using System.Linq;

namespace TikTokLiveGame
{
    // Absolute server snapshots prevent double-counting after reconnects. The
    // local path is for older bridges and isolated visual captures.
    internal sealed class PointsLeaderboard
    {
        private const long MaxPoints = 9007199254740991L;
        private readonly Dictionary<string, PointScoreData> players = new();
        private long revision = -1;
        private long sequence;
        private bool authoritative;
        public PointScoreData[] Top { get; private set; } = Array.Empty<PointScoreData>();

        public bool Apply(TikTokEvent data)
        {
            if (data.type == "reset")
            {
                players.Clear(); Top = Array.Empty<PointScoreData>(); revision = -1;
                sequence = 0; authoritative = false;
                return true;
            }
            if (data.pointsVersion >= 1)
            {
                if (data.type != "snapshot" && data.pointsRevision <= revision) return false;
                authoritative = true;
                revision = data.pointsRevision;
                players.Clear();
                foreach (PointScoreData item in data.pointScores ?? Array.Empty<PointScoreData>())
                    if (Valid(item?.userId) && item.points > 0)
                        players[item.userId] = new PointScoreData { userId = item.userId, nickname = item.nickname,
                            avatar = item.avatar, points = Math.Min(MaxPoints, item.points), reachedOrder = item.reachedOrder };
                Refresh();
                return true;
            }
            if (authoritative) return false;
            if (data.type == "snapshot")
            {
                players.Clear(); sequence = 0;
                foreach (TikTokPlayerData donor in data.vipScores ?? Array.Empty<TikTokPlayerData>())
                    if (donor != null && Valid(donor.userId) && donor.score > 0)
                        players[donor.userId] = new PointScoreData { userId = donor.userId, nickname = donor.nickname,
                            avatar = donor.avatar, points = (long)donor.score * 100L, reachedOrder = ++sequence };
                Refresh();
                return true;
            }
            if (!Valid(data.userId)) return false;
            long delta = data.type == "like" ? Math.Max(0, data.likeCount)
                : data.type == "gift" ? (long)Math.Max(0, data.diamondCount) * 100L : 0;
            if (!players.TryGetValue(data.userId, out PointScoreData player))
            {
                if (delta == 0) return false;
                players[data.userId] = player = new PointScoreData { userId = data.userId };
            }
            if (!string.IsNullOrWhiteSpace(data.nickname)) player.nickname = data.nickname;
            if (!string.IsNullOrWhiteSpace(data.avatar)) player.avatar = data.avatar;
            if (delta > 0)
            {
                player.points = Math.Min(MaxPoints, player.points + delta);
                player.reachedOrder = ++sequence;
            }
            Refresh();
            return true;
        }

        private static bool Valid(string id) => !string.IsNullOrWhiteSpace(id) && !id.StartsWith("npc-", StringComparison.Ordinal);
        private void Refresh() => Top = players.Values.Where(p => p.points > 0)
            .OrderByDescending(p => p.points).ThenBy(p => p.reachedOrder).ThenBy(p => p.userId, StringComparer.Ordinal).Take(3).ToArray();
    }
}
