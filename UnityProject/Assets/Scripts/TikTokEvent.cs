using System;

namespace TikTokLiveGame
{
    [Serializable]
    public class TikTokEvent
    {
        public string type;
        public string state;
        public string message;
        public string userId;
        public string uniqueId;
        public string nickname;
        public string avatar;
        public string comment;
        public string giftId;
        public string giftName;
        public string action;
        public string masterRuleId;
        public string label;
        public string variant;
        public string titleLabel;
        public string titleVariant;
        public int repeatCount;
        public int diamondCount;
        public int likeCount;
        public int score;
        public int giftPower;
        public int durationMs;
        public int fireworkBursts;
        public bool spectatorOnly;
        public bool joinedNow;
        public long titleExpiresAt;
        public TikTokPlayerData[] players;
        public TikTokPlayerData[] vipScores;
        public int pointsVersion;
        public long pointsRevision;
        public PointScoreData[] pointScores;
        public DisplayConfig display;
    }

    [Serializable]
    public class DisplayConfig
    {
        public LightingConfig lighting = new();
        public bool showTop = true;
        public bool showWelcome = true;
        public bool showChat = true;
        public bool showFeed = true;
        public bool showGiftEffects = true;
        public bool focusNpc = true;
        public bool focusChat = true;
    }

    [Serializable]
    public class PointScoreData
    {
        public string userId;
        public string nickname;
        public string avatar;
        public long points;
        public long reachedOrder;
    }

    [Serializable]
    public class TikTokPlayerData
    {
        public string userId;
        public string uniqueId;
        public string nickname;
        public string avatar;
        public string titleLabel;
        public string titleVariant;
        public int score;
        public int giftPower;
        public long titleExpiresAt;
    }

    [Serializable]
    internal class ClientMessage
    {
        public string type;
        public string role;
        public string username;
        public string manualUsername;
        public string action;
        public string giftName;
        public int count;
        public int userIndex;
        public int value;
    }
}
