//! Kết nối trực tiếp TikTok Live qua `piratetok-live-rs`.
//!
//! Thay cho `tiktok-live-connector` (npm): không cần sign server, không cần Euler API key —
//! crate tự lấy cookie `ttwid` rồi mở WebSocket Webcast và giải mã protobuf.

use super::Outcome;
use crate::domain::event::IncomingEvent;
use crate::session::pipeline::process_game_event;
use crate::session::state::SharedState;
use piratetok_live_rs::structs::proto::{Image, UserIdentity};
use piratetok_live_rs::structs::TikTokLiveEvent;
use piratetok_live_rs::TikTokLive;

/// Chạy một phiên kết nối tới khi mất kết nối hoặc live kết thúc.
pub async fn run(state: &SharedState, username: &str, failures: &mut u32) -> Outcome {
    tracing::info!("[tiktok] kết nối trực tiếp @{username}");

    let mut stream = match TikTokLive::builder(username)
        // Bản thân crate cũng có vòng thử lại; để nó lo các lần rớt ngắn, còn
        // vòng ngoài của bridge lo trường hợp hỏng lâu.
        .max_retries(3)
        .connect()
        .await
    {
        Ok(stream) => stream,
        Err(error) => {
            let hint = error_hint(&error);
            tracing::error!("[tiktok] không kết nối được @{username}: {error}");
            if !hint.is_empty() {
                tracing::error!("[tiktok]   → {hint}");
            }
            // Chủ phòng không live là trạng thái bình thường, vẫn chờ họ lên sóng.
            state
                .set_status(
                    "error",
                    Some(username.to_string()),
                    if hint.is_empty() { error.to_string() } else { hint },
                )
                .await;
            return Outcome::Lost(error.to_string());
        }
    };

    while let Some(event) = stream.next_event().await {
        match event {
            TikTokLiveEvent::Connected { room_id } => {
                *failures = 0;
                tracing::info!("[tiktok] đã vào phòng live, room {room_id}");
                state
                    .set_status(
                        "connected",
                        Some(username.to_string()),
                        format!("Đã kết nối @{username}"),
                    )
                    .await;
                state.broadcast_metrics().await;
            }

            TikTokLiveEvent::Reconnecting { attempt, max_retries, delay_secs } => {
                tracing::warn!("[tiktok] thử lại {attempt}/{max_retries} sau {delay_secs}s");
                state
                    .set_status(
                        "reconnecting",
                        Some(username.to_string()),
                        format!("Đang kết nối lại @{username} ({attempt}/{max_retries})..."),
                    )
                    .await;
            }

            TikTokLiveEvent::Chat(msg) => {
                let mut event = IncomingEvent::new("chat");
                event.event_id = msg_id(msg.common.as_ref());
                apply_user(&mut event, msg.user.as_ref());
                event.comment = msg.comment;
                forward(state, event).await;
            }

            TikTokLiveEvent::Member(msg) | TikTokLiveEvent::Join(msg) => {
                let mut event = IncomingEvent::new("member");
                event.event_id = msg_id(msg.common.as_ref());
                apply_user(&mut event, msg.user.as_ref());
                forward(state, event).await;
            }

            TikTokLiveEvent::Like(msg) => {
                let mut event = IncomingEvent::new("like");
                event.event_id = msg_id(msg.common.as_ref());
                apply_user(&mut event, msg.user.as_ref());
                event.like_count = i64::from(msg.like_count).max(1);
                event.total_like_count = msg.total_like_count.max(0);
                forward(state, event).await;
            }

            TikTokLiveEvent::Follow(msg) => {
                let mut event = IncomingEvent::new("follow");
                event.event_id = msg_id(msg.common.as_ref());
                apply_user(&mut event, msg.user.as_ref());
                forward(state, event).await;
            }

            TikTokLiveEvent::Share(msg) => {
                let mut event = IncomingEvent::new("share");
                event.event_id = msg_id(msg.common.as_ref());
                apply_user(&mut event, msg.user.as_ref());
                forward(state, event).await;
            }

            TikTokLiveEvent::Gift(msg) => {
                let details = msg.gift_details.unwrap_or_default();
                let repeat_count = i64::from(msg.repeat_count).max(1);
                let unit_diamonds = i64::from(details.diamond_count).max(0);

                let mut event = IncomingEvent::new("gift");
                event.event_id = msg_id(msg.common.as_ref());
                apply_user(&mut event, msg.user.as_ref());
                event.gift_id = if msg.gift_id != 0 {
                    msg.gift_id.to_string()
                } else if details.id != 0 {
                    details.id.to_string()
                } else {
                    String::new()
                };
                event.gift_name = if details.gift_name.is_empty() {
                    "Gift".to_string()
                } else {
                    details.gift_name
                };
                event.gift_picture_url = first_image_url(details.gift_image.as_ref());
                event.gift_type = i64::from(details.gift_type);
                event.repeat_end = msg.repeat_end != 0;
                event.repeat_count = repeat_count;
                event.unit_diamond_count = unit_diamonds;
                event.diamond_count = unit_diamonds * repeat_count;

                // Gift lặp chỉ tính một lần, lúc người xem thả tay.
                if event.is_pending_gift_streak() {
                    if state.log_tiktok_events {
                        tracing::debug!(
                            "[tiktok] chờ kết thúc streak: {} x{}",
                            event.gift_name,
                            event.repeat_count
                        );
                    }
                    continue;
                }
                forward(state, event).await;
            }

            TikTokLiveEvent::LiveEnded(_) => {
                tracing::info!("[tiktok] @{username} đã tắt live");
                return Outcome::Ended;
            }

            TikTokLiveEvent::Disconnected => {
                return Outcome::Lost("stream đã đóng".to_string());
            }

            _ => {}
        }
    }

    Outcome::Lost("stream kết thúc".to_string())
}

async fn forward(state: &SharedState, event: IncomingEvent) {
    if state.log_tiktok_events {
        let who = [&event.nickname, &event.unique_id, &event.user_id]
            .into_iter()
            .find(|value| !value.is_empty())
            .cloned()
            .unwrap_or_else(|| "?".to_string());
        tracing::info!("[tiktok] {who} — {}", describe(&event));
    }
    process_game_event(state, &event).await;
}

fn describe(event: &IncomingEvent) -> String {
    match event.kind.as_str() {
        "gift" => format!(
            "gift \"{}\" (id {}) x{} = {} diamond",
            event.gift_name,
            if event.gift_id.is_empty() { "?" } else { &event.gift_id },
            event.repeat_count,
            event.diamond_count
        ),
        "chat" => format!("chat: {}", event.comment),
        "like" => format!("like x{}", event.like_count),
        other => other.to_string(),
    }
}

fn msg_id(common: Option<&piratetok_live_rs::structs::proto::CommonMessageData>) -> String {
    match common {
        Some(common) if common.msg_id != 0 => common.msg_id.to_string(),
        _ => String::new(),
    }
}

/// Điền thông tin người dùng, dùng cùng thứ tự dự phòng như bản JS.
fn apply_user(event: &mut IncomingEvent, user: Option<&UserIdentity>) {
    let Some(user) = user else { return };

    let unique_id = user.unique_id.trim();
    let nickname = user.nickname.trim();
    let fallback = if !unique_id.is_empty() { unique_id } else { nickname };

    event.user_id = if user.user_id != 0 {
        user.user_id.to_string()
    } else {
        fallback.to_string()
    };
    event.unique_id = if unique_id.is_empty() {
        event.user_id.clone()
    } else {
        unique_id.to_string()
    };
    event.nickname = if nickname.is_empty() {
        if unique_id.is_empty() { "TikTok user".to_string() } else { unique_id.to_string() }
    } else {
        nickname.to_string()
    };
    event.avatar = [&user.avatar_thumb, &user.avatar_medium, &user.avatar_large]
        .into_iter()
        .find_map(|image| {
            let url = first_image_url(image.as_ref());
            (!url.is_empty()).then_some(url)
        })
        .unwrap_or_default();
}

/// URL `https` đầu tiên trong danh sách ảnh.
fn first_image_url(image: Option<&Image>) -> String {
    image
        .map(|image| {
            image
                .url_list
                .iter()
                .find(|url| url.starts_with("https://"))
                .cloned()
                .unwrap_or_default()
        })
        .unwrap_or_default()
}

/// Gợi ý tiếng Việt cho các lỗi kết nối hay gặp.
fn error_hint(error: &piratetok_live_rs::errors::TikTokLiveError) -> String {
    use piratetok_live_rs::errors::TikTokLiveError as E;
    match error {
        E::HostNotOnline(_) => "Tài khoản này hiện KHÔNG live. Chỉ kết nối được khi họ đang phát trực tiếp.",
        E::UserNotFound(_) | E::ProfileNotFound(_) => {
            "Không tìm thấy tài khoản. Lấy đúng phần sau @ trong link tiktok.com/@username."
        }
        E::ProfilePrivate(_) => "Tài khoản đang để riêng tư nên không đọc được phòng live.",
        E::AgeRestricted(_) | E::SessionRequired(_) => {
            "Phòng live giới hạn độ tuổi, cần cookie đăng nhập mới xem được."
        }
        E::DeviceBlocked => "TikTok tạm chặn thiết bị. Chờ một lát rồi thử lại, hoặc đổi mạng.",
        E::RoomIdMissing => "TikTok không trả về room id. Thử lại sau ít phút.",
        _ => "",
    }
    .to_string()
}
