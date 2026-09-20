//! Trạng thái phiên live dùng chung cho HTTP và WebSocket.

use crate::config::{Paths, ServerSettings};
use crate::domain::event::{GameEvent, PlayerState, VipScore};
use crate::domain::gifts::{GiftCatalog, ObservedGift};
use crate::domain::rules::MasterConfig;
use crate::domain::display::DisplayConfig;
use serde::Serialize;
use std::collections::BTreeMap;
use std::sync::Arc;
use tokio::sync::{broadcast, Mutex, Notify, RwLock};
use tokio::task::JoinHandle;

/// Số gift tối đa giữ trong thư viện học được, cắt bớt theo thời điểm thấy gần nhất.
const MAX_OBSERVED_GIFTS: usize = 500;
/// Số khoá chống trùng giữ lại trước khi dọn.
const MAX_DEDUPE_KEYS: usize = 2000;
/// Sự kiện có `eventId` được nhớ 10 phút; sự kiện không có chỉ nhớ 2,5 giây.
const DEDUPE_RETENTION_WITH_ID_MS: i64 = 10 * 60 * 1000;
const DEDUPE_RETENTION_FALLBACK_MS: i64 = 2500;

pub fn now_ms() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConnectionStatus {
    pub state: String,
    pub username: Option<String>,
    pub message: String,
}

impl Default for ConnectionStatus {
    fn default() -> Self {
        Self {
            state: "idle".to_string(),
            username: None,
            message: "Chưa kết nối".to_string(),
        }
    }
}

#[derive(Debug, Clone, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Metrics {
    pub source: String,
    pub events: i64,
    pub members: i64,
    pub chats: i64,
    pub gifts: i64,
    pub diamonds: i64,
    pub likes: i64,
    pub started_at: Option<i64>,
}

impl Metrics {
    pub fn fresh(source: &str) -> Self {
        Self {
            source: source.to_string(),
            started_at: if source == "idle" { None } else { Some(now_ms()) },
            ..Default::default()
        }
    }

    pub fn idle() -> Self {
        Self::fresh("idle")
    }
}

/// Phần state thay đổi theo phiên live.
#[derive(Debug, Default)]
pub struct Session {
    pub metrics: Metrics,
    pub players: BTreeMap<String, PlayerState>,
    pub vip_scores: BTreeMap<String, VipScore>,
    pub points: crate::domain::points::PointsLeaderboard,
    dedupe: BTreeMap<String, i64>,
}

impl Session {
    pub fn new() -> Self {
        Self {
            metrics: Metrics::idle(),
            ..Default::default()
        }
    }

    /// Xoá sạch người chơi, điểm VIP và bộ nhớ chống trùng, giữ lại nguồn sự kiện.
    pub fn reset(&mut self, source: &str) {
        self.metrics = Metrics::fresh(source);
        self.players.clear();
        self.vip_scores.clear();
        self.points = Default::default();
        self.dedupe.clear();
    }

    /// Sự kiện đã thấy gần đây chưa? Ghi nhớ luôn nếu chưa.
    pub fn is_duplicate(&mut self, event: &GameEvent, now: i64) -> bool {
        let key = dedupe_key(event);
        let retention = if event.event_id.is_empty() {
            DEDUPE_RETENTION_FALLBACK_MS
        } else {
            DEDUPE_RETENTION_WITH_ID_MS
        };

        if let Some(previous) = self.dedupe.get(&key) {
            if now - previous < retention {
                return true;
            }
        }
        self.dedupe.insert(key, now);

        if self.dedupe.len() > MAX_DEDUPE_KEYS {
            self.dedupe
                .retain(|_, seen| now - *seen <= DEDUPE_RETENTION_WITH_ID_MS);
            while self.dedupe.len() > MAX_DEDUPE_KEYS {
                let Some(oldest) = self
                    .dedupe
                    .iter()
                    .min_by_key(|(_, seen)| **seen)
                    .map(|(key, _)| key.clone())
                else {
                    break;
                };
                self.dedupe.remove(&oldest);
            }
        }
        false
    }

    /// Bỏ người chơi đã lâu không tương tác, rồi cắt bớt nếu vẫn vượt trần.
    pub fn prune_players(&mut self, now: i64, ttl_ms: i64, maximum: usize) {
        let cutoff = now - ttl_ms.max(1000);
        self.players.retain(|_, player| player.last_active >= cutoff);

        let maximum = maximum.max(1);
        if self.players.len() <= maximum {
            return;
        }
        let mut by_age: Vec<_> = self
            .players
            .values()
            .map(|p| (p.last_active, p.user_id.clone()))
            .collect();
        by_age.sort();
        let excess = self.players.len() - maximum;
        for (_, user_id) in by_age.into_iter().take(excess) {
            self.players.remove(&user_id);
        }
    }
}

/// Khoá chống trùng: ưu tiên `eventId` của TikTok, nếu không có thì ghép các field đặc trưng.
fn dedupe_key(event: &GameEvent) -> String {
    if !event.event_id.is_empty() {
        return format!("id:{}", event.event_id);
    }
    format!(
        "fallback:{}|{}|{}|{}|{}|{}|{}|{}",
        event.kind,
        event.user_id,
        event.gift_id,
        event.gift_name,
        event.repeat_count,
        event.diamond_count,
        event.like_count,
        event.comment
    )
}

pub struct AppState {
    pub settings: ServerSettings,
    pub paths: Paths,
    /// `config/game.json` giữ nguyên dạng JSON để `/api/config` trả về đầy đủ.
    pub game_config: serde_json::Value,
    pub gift_catalog: GiftCatalog,
    pub max_players: usize,
    pub player_ttl_ms: i64,
    pub live_provider: String,
    pub tikfinity_ws_url: String,
    pub log_tiktok_events: bool,

    pub master: RwLock<MasterConfig>,
    pub display: RwLock<DisplayConfig>,
    pub observed_gifts: RwLock<BTreeMap<String, ObservedGift>>,
    pub session: RwLock<Session>,
    pub status: RwLock<ConnectionStatus>,

    /// Kênh phát tới mọi WebSocket client. Payload đã serialize sẵn để chỉ encode một lần.
    pub broadcast: broadcast::Sender<String>,
    /// Báo cho tác vụ nền biết `observed-gifts.json` cần ghi lại.
    pub observed_dirty: Notify,

    /// Tác vụ đang chạy demo, huỷ khi chuyển sang live thật.
    pub demo_task: Mutex<Option<JoinHandle<()>>>,
    /// Tác vụ đang giữ kết nối live (TikTok hoặc TikFinity).
    pub live_task: Mutex<Option<JoinHandle<()>>>,
    /// Serialize source changes from multiple control clients.
    pub operator_gate: Mutex<()>,
    /// Keep each event and reset ordered through state updates and broadcasts.
    pub event_gate: Mutex<()>,
    pub shutdown: tokio::sync::watch::Sender<bool>,
}

pub type SharedState = Arc<AppState>;

impl AppState {
    /// Phát một message JSON tới mọi client đang mở.
    pub fn broadcast_json<T: Serialize>(&self, message: &T) {
        match serde_json::to_string(message) {
            Ok(payload) => {
                // Lỗi duy nhất có thể xảy ra là "không có receiver" — không sao.
                let _ = self.broadcast.send(payload);
            }
            Err(error) => tracing::error!("không serialize được message broadcast: {error}"),
        }
    }

    pub async fn set_status(&self, state: &str, username: Option<String>, message: impl Into<String>) {
        let status = ConnectionStatus {
            state: state.to_string(),
            username,
            message: message.into(),
        };
        *self.status.write().await = status.clone();
        self.broadcast_json(&StatusMessage {
            kind: "status",
            status,
        });
    }

    pub async fn broadcast_metrics(&self) {
        let metrics = self.session.read().await.metrics.clone();
        self.broadcast_json(&MetricsMessage {
            kind: "metrics",
            metrics,
        });
    }

    /// Thư viện gift sắp xếp theo giá rồi tên, dùng cho bảng điều khiển.
    pub async fn gift_catalog_message(&self) -> GiftCatalogMessage {
        let mut gifts: Vec<ObservedGift> = self.observed_gifts.read().await.values().cloned().collect();
        gifts.sort_by(|a, b| {
            a.diamond_count
                .cmp(&b.diamond_count)
                .then_with(|| a.gift_name.cmp(&b.gift_name))
        });
        GiftCatalogMessage {
            kind: "gift_catalog",
            gifts,
        }
    }

    /// Đánh dấu thư viện gift cần ghi xuống đĩa. Việc ghi được gom nhóm ở tác vụ nền
    /// (xem `spawn_observed_gift_saver`) để một phiên live đông không ghi file liên tục.
    pub fn mark_observed_gifts_dirty(&self) {
        self.observed_dirty.notify_one();
    }

    /// Cắt bớt thư viện gift khi vượt trần, bỏ mục thấy lâu nhất.
    pub async fn trim_observed_gifts(&self) {
        let mut gifts = self.observed_gifts.write().await;
        if gifts.len() <= MAX_OBSERVED_GIFTS {
            return;
        }
        let mut by_age: Vec<_> = gifts
            .iter()
            .map(|(key, gift)| (gift.last_seen_at, key.clone()))
            .collect();
        by_age.sort();
        let excess = gifts.len() - MAX_OBSERVED_GIFTS;
        for (_, key) in by_age.into_iter().take(excess) {
            gifts.remove(&key);
        }
    }
}

// ─── Message gửi cho client ────────────────────────────────────────────────

#[derive(Serialize)]
pub struct StatusMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    #[serde(flatten)]
    pub status: ConnectionStatus,
}

#[derive(Serialize)]
pub struct MetricsMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    #[serde(flatten)]
    pub metrics: Metrics,
}

#[derive(Serialize)]
pub struct GiftCatalogMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub gifts: Vec<ObservedGift>,
}

#[cfg(test)]
mod tests {
    use super::*;

    fn event(kind: &str, event_id: &str) -> GameEvent {
        GameEvent {
            kind: kind.into(),
            event_id: event_id.into(),
            ..Default::default()
        }
    }

    #[test]
    fn events_with_an_id_are_deduped_for_ten_minutes() {
        let mut session = Session::new();
        let event = event("chat", "msg-1");
        assert!(!session.is_duplicate(&event, 0));
        assert!(session.is_duplicate(&event, 60_000));
        // Quá hạn lưu thì coi là sự kiện mới.
        assert!(!session.is_duplicate(&event, 10 * 60 * 1000 + 1));
    }

    #[test]
    fn events_without_an_id_use_a_short_fallback_window() {
        let mut session = Session::new();
        let event = event("like", "");
        assert!(!session.is_duplicate(&event, 0));
        assert!(session.is_duplicate(&event, 2000));
        assert!(!session.is_duplicate(&event, 2500));
    }

    #[test]
    fn different_events_do_not_collide_on_the_fallback_key() {
        let mut session = Session::new();
        let mut first = event("gift", "");
        first.gift_name = "Rose".into();
        let mut second = event("gift", "");
        second.gift_name = "Galaxy".into();

        assert!(!session.is_duplicate(&first, 0));
        assert!(!session.is_duplicate(&second, 0));
        assert!(session.is_duplicate(&first, 0));
    }

    #[test]
    fn pruning_drops_stale_players_then_caps_the_rest() {
        let mut session = Session::new();
        for i in 0..10 {
            session.players.insert(
                format!("u{i}"),
                PlayerState {
                    user_id: format!("u{i}"),
                    last_active: i as i64 * 1000,
                    ..Default::default()
                },
            );
        }

        // TTL 5s tính từ mốc 10000 → cutoff 5000, giữ u5..u9.
        session.prune_players(10_000, 5_000, 100);
        assert_eq!(session.players.len(), 5);
        assert!(!session.players.contains_key("u4"));
        assert!(session.players.contains_key("u5"));

        // Trần 2 người → giữ lại hai người mới nhất.
        session.prune_players(10_000, 5_000, 2);
        assert_eq!(session.players.len(), 2);
        assert!(session.players.contains_key("u9"));
        assert!(session.players.contains_key("u8"));
    }

    #[test]
    fn reset_clears_players_and_restamps_metrics() {
        let mut session = Session::new();
        session.players.insert("u1".into(), PlayerState::default());
        session.vip_scores.insert("u1".into(), VipScore::default());
        session.metrics.events = 5;

        session.reset("demo");
        assert!(session.players.is_empty());
        assert!(session.vip_scores.is_empty());
        assert_eq!(session.metrics.events, 0);
        assert_eq!(session.metrics.source, "demo");
        assert!(session.metrics.started_at.is_some());

        session.reset("idle");
        assert!(session.metrics.started_at.is_none());
    }

    #[test]
    fn status_message_flattens_onto_the_type_field() {
        let json = serde_json::to_value(StatusMessage {
            kind: "status",
            status: ConnectionStatus {
                state: "connected".into(),
                username: Some("abc".into()),
                message: "Đã kết nối".into(),
            },
        })
        .unwrap();
        assert_eq!(json["type"], "status");
        assert_eq!(json["state"], "connected");
        assert_eq!(json["username"], "abc");
        assert_eq!(json["message"], "Đã kết nối");
    }

    #[test]
    fn metrics_message_exposes_every_counter_control_panel_reads() {
        let json = serde_json::to_value(MetricsMessage {
            kind: "metrics",
            metrics: Metrics::fresh("tiktok"),
        })
        .unwrap();
        assert_eq!(json["type"], "metrics");
        for key in ["events", "members", "chats", "gifts", "diamonds", "likes"] {
            assert!(json.get(key).is_some(), "thiếu metric {key}");
        }
        assert_eq!(json["source"], "tiktok");
    }
}
