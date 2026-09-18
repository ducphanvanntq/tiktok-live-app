//! Đường đi của một sự kiện: sanitize → áp luật → khử trùng lặp → cập nhật state → broadcast.
//!
//! Port của `processGameEvent` / `emitGameEvent` / `learnObservedGift` trong `server.js`.

use crate::domain::event::{GameEvent, IncomingEvent, PlayerState, VipScore};
use crate::domain::gifts::{is_real_observed_gift, merge_observed_gift, ObservedGift};
use crate::domain::rules::{apply_built_in_chat_command, apply_rule, resolve_master_rule};
use crate::transport::security::sanitize_game_event;
use crate::session::state::{now_ms, SharedState};
use crate::domain::text::{normalize_text, split_triggers};
use serde::Serialize;

/// Thứ tự hiển thị các hành động trong bảng hướng dẫn người xem.
const GUIDE_ACTION_ORDER: [&str; 5] = ["walk", "medal", "fireworks", "grow", "topdj"];

/// Nhận một sự kiện thô, áp toàn bộ luật rồi phát đi.
pub async fn process_game_event(state: &SharedState, input: &IncomingEvent) {
    let Some(mut event) = sanitize_game_event(input) else {
        return;
    };

    {
        let master = state.master.read().await;
        let rule = resolve_master_rule(&master, &event).cloned();
        apply_rule(&mut event, rule.as_ref());
    }
    apply_built_in_chat_command(&mut event);

    let is_known_player = {
        let session = state.session.read().await;
        !event.user_id.is_empty() && session.players.contains_key(&event.user_id)
    };

    let joins_by_keyword = event.is("chat") && event.action == "join";
    let joins_by_social = event.is("follow") || event.is("share");
    event.joined_now = (joins_by_keyword || joins_by_social) && !is_known_player;

    {
        let master = state.master.read().await;
        let source = state.session.read().await.metrics.source.clone();
        // Ở chế độ "chỉ từ khoá", ai chưa gõ lệnh tham gia thì chỉ đứng xem.
        // Demo bỏ qua luật này để sàn luôn có người nhảy.
        if source != "demo" && master.join_mode == "keyword_only" && !is_known_player {
            let joins_by_gift = event.is("gift") && master.gift_always_joins;
            event.spectator_only = !(joins_by_keyword || joins_by_gift || joins_by_social);
        }
    }

    if event.is("gift") && is_real_observed_gift(&event) {
        learn_observed_gift(state, &event).await;
        let unit = if event.unit_diamond_count > 0 {
            event.unit_diamond_count
        } else {
            (event.diamond_count as f64 / event.repeat_count.max(1) as f64).round() as i64
        };
        state.broadcast_json(&GiftObservedMessage {
            kind: "gift_observed",
            gift_id: event.gift_id.clone(),
            gift_name: event.gift_name.clone(),
            diamond_count: unit,
            gift_picture_url: event.gift_picture_url.clone(),
        });
    }

    emit_game_event(state, event).await;
}

/// Khử trùng lặp, gắn hiệu ứng theo bảng quà, cập nhật người chơi và chỉ số, rồi phát.
async fn emit_game_event(state: &SharedState, mut event: GameEvent) {
    let now = now_ms();

    {
        let mut session = state.session.write().await;
        if session.is_duplicate(&event, now) {
            return;
        }
    }

    // Luật Master thắng bảng quà: nếu đã có luật khớp thì không tra bảng nữa.
    if event.is("gift") && event.master_rule_id.is_empty() {
        if let Some(rule) = state.gift_catalog.resolve(&event) {
            event.action = rule.action.clone();
            event.duration_ms = rule.duration_ms;
            event.label = rule.label.clone();
            event.variant = rule.variant.clone();
            event.firework_bursts = rule.firework_bursts;
        }
    }

    {
        let mut session = state.session.write().await;

        if !event.user_id.is_empty() && !event.spectator_only {
            let previous = session.players.get(&event.user_id).cloned().unwrap_or_default();
            let mut player = PlayerState {
                user_id: event.user_id.clone(),
                unique_id: non_empty(&event.unique_id, &previous.unique_id, &event.user_id),
                nickname: non_empty(&event.nickname, &previous.nickname, "TikTok user"),
                avatar: non_empty(&event.avatar, &previous.avatar, ""),
                last_active: now,
                ..previous.clone()
            };

            if event.is("gift") {
                player.gift_power = previous.gift_power.max(0) + event.diamond_count.max(0);
                if event.action == "medal" {
                    player.title_label = event.label.clone();
                    player.title_variant = if event.variant.is_empty() {
                        "fire".to_string()
                    } else {
                        event.variant.clone()
                    };
                    player.title_expires_at = now + event.duration_ms.max(3000);
                }
            }

            session.players.insert(event.user_id.clone(), player);
            session.prune_players(now, state.player_ttl_ms, state.max_players);
        }

        session.metrics.events += 1;
        match event.kind.as_str() {
            "member" => session.metrics.members += 1,
            "chat" => session.metrics.chats += 1,
            "like" => session.metrics.likes += event.like_count,
            "gift" => {
                session.metrics.gifts += 1;
                session.metrics.diamonds += event.diamond_count;

                let vip = session
                    .vip_scores
                    .entry(event.user_id.clone())
                    .or_insert_with(|| VipScore {
                        user_id: event.user_id.clone(),
                        ..Default::default()
                    });
                vip.nickname = non_empty(&event.nickname, &vip.nickname, "TikTok user");
                vip.avatar = non_empty(&event.avatar, &vip.avatar, "");
                vip.score += event.diamond_count;
            }
            _ => {}
        }
    }

    state.broadcast_json(&event);
    state.broadcast_metrics().await;
}

/// Giá trị đầu tiên không rỗng, theo thứ tự ưu tiên.
fn non_empty(value: &str, previous: &str, fallback: &str) -> String {
    for candidate in [value, previous, fallback] {
        let trimmed = candidate.trim();
        if !trimmed.is_empty() {
            return trimmed.to_string();
        }
    }
    String::new()
}

/// Ghi nhớ một quà vừa thấy, và tự gắn Gift ID vào luật Master khớp theo tên.
async fn learn_observed_gift(state: &SharedState, event: &GameEvent) {
    let gift_id = event.gift_id.trim().to_string();
    let incoming_name = event.gift_name.trim();
    let key = if !gift_id.is_empty() {
        gift_id.clone()
    } else {
        normalize_text(incoming_name)
    };
    if key.is_empty() {
        return;
    }

    let learned = {
        let mut gifts = state.observed_gifts.write().await;
        let previous = gifts.get(&key).cloned().unwrap_or_default();
        let learned = merge_observed_gift(&previous, event, now_ms());
        gifts.insert(key, learned.clone());
        learned
    };
    state.trim_observed_gifts().await;

    // Luật Master được viết theo tên quà; khi biết Gift ID thì gắn luôn để lần sau
    // khớp được kể cả khi TikTok đổi tên hiển thị theo ngôn ngữ người xem.
    let mut master_changed = false;
    if !gift_id.is_empty() {
        let mut master = state.master.write().await;
        let learned_name = normalize_text(&learned.gift_name);
        for rule in master.rules.iter_mut() {
            if rule.source != "gift" || !rule.gift_id.is_empty() {
                continue;
            }
            if !split_triggers(&rule.trigger).contains(&learned_name) {
                continue;
            }
            rule.gift_id = gift_id.clone();
            if learned.diamond_count > 0 {
                rule.display_diamonds = learned.diamond_count;
            }
            master_changed = true;
            break;
        }
    }

    if master_changed {
        let master = state.master.read().await.clone();
        if let Err(error) = save_master_config(state, &master).await {
            tracing::error!("không lưu được Gift ID vào Master: {error}");
        }
        state.broadcast_json(&MasterConfigMessage {
            kind: "master_config",
            master,
        });
    }

    state.mark_observed_gifts_dirty();
    let catalog = state.gift_catalog_message().await;
    state.broadcast_json(&catalog);
    let guide = viewer_guide_message(state).await;
    state.broadcast_json(&guide);
}

/// Ghi `config/master.json` (pretty-print, xuống dòng cuối file — giống bản JS).
pub async fn save_master_config(
    state: &SharedState,
    master: &crate::domain::rules::MasterConfig,
) -> std::io::Result<()> {
    let mut json = serde_json::to_string_pretty(master)?;
    json.push('\n');
    tokio::fs::write(state.paths.master_config(), json).await
}

/// Ghi `config/observed-gifts.json`.
pub async fn save_observed_gifts(state: &SharedState) -> std::io::Result<()> {
    let gifts: Vec<ObservedGift> = state.observed_gifts.read().await.values().cloned().collect();
    let mut json = serde_json::to_string_pretty(&gifts)?;
    json.push('\n');
    tokio::fs::write(state.paths.observed_gifts(), json).await
}

/// Bảng hướng dẫn hiển thị trên overlay: lệnh tham gia và các quà đáng chú ý.
pub async fn viewer_guide_message(state: &SharedState) -> ViewerGuideMessage {
    let master = state.master.read().await;
    let observed = state.observed_gifts.read().await;

    let join_command = master
        .rules
        .iter()
        .find(|rule| rule.enabled && rule.source == "chat" && rule.action == "join")
        .map(|rule| first_trigger(&rule.trigger))
        .unwrap_or_default();

    let gift_rules: Vec<_> = master
        .rules
        .iter()
        .filter(|rule| {
            rule.enabled
                && rule.source == "gift"
                && (rule.display_diamonds > 0
                    || (!rule.gift_id.is_empty() && observed.contains_key(&rule.gift_id)))
        })
        .collect();

    let mut items: Vec<_> = gift_rules
        .iter()
        .filter_map(|rule| {
            let order = GUIDE_ACTION_ORDER.iter().position(|a| *a == rule.action)?;
            let learned = observed.get(&rule.gift_id);
            Some((
                order,
                GuideItem {
                    gift_id: if rule.gift_id.is_empty() {
                        learned.map(|g| g.gift_id.clone()).unwrap_or_default()
                    } else {
                        rule.gift_id.clone()
                    },
                    gift_name: learned
                        .map(|g| g.gift_name.clone())
                        .filter(|name| !name.is_empty())
                        .unwrap_or_else(|| {
                            let trigger = first_trigger(&rule.trigger);
                            if trigger.is_empty() { "Gift".to_string() } else { trigger }
                        }),
                    diamond_count: learned
                        .map(|g| g.diamond_count)
                        .filter(|d| *d > 0)
                        .unwrap_or(rule.display_diamonds),
                    gift_picture_url: learned.map(|g| g.gift_picture_url.clone()).unwrap_or_default(),
                    label: if rule.label.is_empty() {
                        rule.action.to_uppercase()
                    } else {
                        rule.label.clone()
                    },
                },
            ))
        })
        .collect();
    items.sort_by_key(|(order, _)| *order);
    let guide_items: Vec<GuideItem> = items.into_iter().take(5).map(|(_, item)| item).collect();

    let zoom_rule = gift_rules.iter().find(|rule| rule.action == "camera");
    let zoom_gift_name = zoom_rule
        .map(|rule| {
            observed
                .get(&rule.gift_id)
                .map(|g| g.gift_name.clone())
                .filter(|name| !name.is_empty())
                .unwrap_or_else(|| first_trigger(&rule.trigger))
        })
        .unwrap_or_default();

    ViewerGuideMessage {
        kind: "viewer_guide",
        join_command,
        zoom_gift_name,
        guide_items,
    }
}

/// Alias đầu tiên trong danh sách trigger, giữ nguyên dấu để hiển thị cho người xem.
fn first_trigger(trigger: &str) -> String {
    trigger.split(',').next().unwrap_or("").trim().to_string()
}

/// Ảnh chụp phiên hiện tại, gửi cho overlay ngay khi nó kết nối.
pub async fn snapshot_message(state: &SharedState) -> SnapshotMessage {
    let mut session = state.session.write().await;
    session.prune_players(now_ms(), state.player_ttl_ms, state.max_players);
    SnapshotMessage {
        kind: "snapshot",
        players: session.players.values().cloned().collect(),
        vip_scores: session.vip_scores.values().cloned().collect(),
    }
}

// ─── Message gửi cho client ────────────────────────────────────────────────

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GiftObservedMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub gift_id: String,
    pub gift_name: String,
    pub diamond_count: i64,
    pub gift_picture_url: String,
}

#[derive(Serialize)]
pub struct MasterConfigMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub master: crate::domain::rules::MasterConfig,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct GuideItem {
    pub gift_id: String,
    pub gift_name: String,
    pub diamond_count: i64,
    pub gift_picture_url: String,
    pub label: String,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ViewerGuideMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub join_command: String,
    pub zoom_gift_name: String,
    pub guide_items: Vec<GuideItem>,
}

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub players: Vec<PlayerState>,
    pub vip_scores: Vec<VipScore>,
}
