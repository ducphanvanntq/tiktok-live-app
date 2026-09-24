//! Kiểu sự kiện trên đường truyền tới overlay Unity và bảng điều khiển web.
//!
//! `GameEvent` là **hợp đồng wire-format**: Unity deserialize bằng `JsonUtility`
//! (xem `UnityProject/Assets/Scripts/TikTokEvent.cs`) nên tên field camelCase phải giữ
//! nguyên tuyệt đối, và mọi field số phải luôn có mặt dưới dạng số — `JsonUtility`
//! không xử lý được `null` cho kiểu giá trị.

use serde::{Deserialize, Serialize};

/// Các loại sự kiện game được chấp nhận. Mọi giá trị khác bị loại ở khâu sanitize.
pub const EVENT_TYPES: [&str; 6] = ["member", "chat", "gift", "like", "follow", "share"];

/// Sự kiện thô từ provider (TikTok / TikFinity / demo), trước khi sanitize.
///
/// Giữ thêm `gift_type` và `repeat_end` để lọc gift streak — hai field này bị loại
/// khi sanitize nên không bao giờ ra tới client, giống hệt bản JS.
#[derive(Debug, Clone, Default)]
pub struct IncomingEvent {
    pub kind: String,
    pub event_id: String,
    pub user_id: String,
    pub unique_id: String,
    pub nickname: String,
    pub avatar: String,
    pub comment: String,
    pub gift_id: String,
    pub gift_name: String,
    pub gift_picture_url: String,
    pub gift_type: i64,
    pub repeat_end: bool,
    pub repeat_count: i64,
    pub unit_diamond_count: i64,
    pub diamond_count: i64,
    pub like_count: i64,
    pub total_like_count: i64,
    pub spectator_only: bool,
}

impl IncomingEvent {
    pub fn new(kind: &str) -> Self {
        Self {
            kind: kind.to_string(),
            repeat_end: true,
            ..Default::default()
        }
    }

    /// Gift dạng streak chưa kết thúc: TikTok bắn liên tục trong lúc người xem giữ nút,
    /// chỉ frame cuối (`repeat_end`) mới là số lượng chốt. Bỏ qua các frame giữa chừng,
    /// nếu không tổng kim cương sẽ bị cộng dồn sai.
    pub fn is_pending_gift_streak(&self) -> bool {
        self.gift_type == 1 && !self.repeat_end
    }
}

/// Sự kiện đã sanitize — đây chính là JSON gửi cho client.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct GameEvent {
    #[serde(rename = "type")]
    pub kind: String,
    pub event_id: String,
    pub user_id: String,
    pub unique_id: String,
    pub nickname: String,
    pub avatar: String,
    pub comment: String,
    pub gift_id: String,
    pub gift_name: String,
    pub gift_picture_url: String,
    pub repeat_count: i64,
    pub unit_diamond_count: i64,
    pub diamond_count: i64,
    pub like_count: i64,
    pub spectator_only: bool,

    // Được gắn thêm bởi luật Master / bảng gift, sau khi sanitize.
    pub action: String,
    pub duration_ms: i64,
    pub label: String,
    pub variant: String,
    pub firework_bursts: i64,
    pub master_rule_id: String,
    pub joined_now: bool,
    #[serde(flatten, default, skip_serializing_if = "Option::is_none")]
    pub points: Option<super::points::PointsSnapshot>,
}

impl GameEvent {
    pub fn is(&self, kind: &str) -> bool {
        self.kind == kind
    }
}

/// Trạng thái người chơi trong phiên, gửi kèm `snapshot`.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct PlayerState {
    pub user_id: String,
    pub unique_id: String,
    pub nickname: String,
    pub avatar: String,
    pub gift_power: i64,
    pub title_label: String,
    pub title_variant: String,
    pub title_expires_at: i64,
    #[serde(skip)]
    pub last_active: i64,
}

/// Điểm VIP tích luỹ theo kim cương trong phiên.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct VipScore {
    pub user_id: String,
    pub nickname: String,
    pub avatar: String,
    pub score: i64,
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn pending_gift_streak_only_applies_to_repeatable_gifts() {
        let mut event = IncomingEvent::new("gift");
        event.gift_type = 1;
        event.repeat_end = false;
        assert!(event.is_pending_gift_streak());

        event.repeat_end = true;
        assert!(!event.is_pending_gift_streak());

        // Gift không lặp (gift_type != 1) luôn được phát ngay.
        event.gift_type = 0;
        event.repeat_end = false;
        assert!(!event.is_pending_gift_streak());
    }

    #[test]
    fn wire_format_uses_the_exact_field_names_unity_expects() {
        let json = serde_json::to_value(GameEvent {
            kind: "gift".into(),
            user_id: "42".into(),
            gift_name: "Rose".into(),
            diamond_count: 5,
            ..Default::default()
        })
        .unwrap();

        // Tên phải khớp UnityProject/Assets/Scripts/TikTokEvent.cs.
        for key in [
            "type", "eventId", "userId", "uniqueId", "nickname", "avatar", "comment",
            "giftId", "giftName", "giftPictureUrl", "repeatCount", "unitDiamondCount",
            "diamondCount", "likeCount", "spectatorOnly", "action", "durationMs",
            "label", "variant", "fireworkBursts", "masterRuleId", "joinedNow",
        ] {
            assert!(json.get(key).is_some(), "thiếu field wire-format: {key}");
        }
        assert_eq!(json["type"], "gift");
        assert_eq!(json["diamondCount"], 5);
    }

    #[test]
    fn numeric_fields_never_serialize_as_null() {
        // JsonUtility của Unity không parse được `null` cho int/bool.
        let json = serde_json::to_value(GameEvent::default()).unwrap();
        for (key, value) in json.as_object().unwrap() {
            assert!(!value.is_null(), "field {key} không được là null");
        }
    }

    #[test]
    fn player_state_hides_internal_bookkeeping_from_the_wire() {
        let json = serde_json::to_value(PlayerState {
            last_active: 1_700_000_000_000,
            ..Default::default()
        })
        .unwrap();
        assert!(json.get("lastActive").is_none());
        assert!(json.get("giftPower").is_some());
    }
}
