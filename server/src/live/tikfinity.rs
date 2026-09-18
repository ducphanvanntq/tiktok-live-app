//! Nguồn sự kiện qua TikFinity Desktop — port của `src/tiktok/normalize-tikfinity-event.js`.
//!
//! Giữ lại làm đường lui: nếu TikTok siết endpoint mà `piratetok-live-rs` đang dùng,
//! TikFinity vẫn chạy được vì nó tự lo phần kết nối.

use super::Outcome;
use crate::domain::event::IncomingEvent;
use crate::session::pipeline::process_game_event;
use crate::session::state::{now_ms, SharedState};
use futures_util::StreamExt;
use serde_json::Value;
use std::sync::atomic::{AtomicU64, Ordering};
use tokio_tungstenite::tungstenite::Message;

/// Bộ đếm sinh eventId khi TikFinity không gửi id — tránh bị coi là trùng lặp.
static EVENT_SEQUENCE: AtomicU64 = AtomicU64::new(0);

pub async fn run(state: &SharedState, username: &str, failures: &mut u32) -> Outcome {
    let url = state.tikfinity_ws_url.clone();
    let (stream, _) = match tokio_tungstenite::connect_async(&url).await {
        Ok(pair) => pair,
        Err(error) => return Outcome::Lost(format!("không mở được {url}: {error}")),
    };

    *failures = 0;
    tracing::info!("đã kết nối TikFinity tại {url}");
    state
        .set_status(
            "connected",
            Some(username.to_string()),
            format!("Đã kết nối TikFinity cho @{username}"),
        )
        .await;
    state.broadcast_metrics().await;

    let (_, mut receiver) = stream.split();
    while let Some(message) = receiver.next().await {
        let payload = match message {
            Ok(Message::Text(text)) => text.to_string(),
            Ok(Message::Binary(bytes)) => match String::from_utf8(bytes.to_vec()) {
                Ok(text) => text,
                Err(_) => continue,
            },
            Ok(Message::Close(_)) => return Outcome::Lost("TikFinity đóng kết nối".to_string()),
            Ok(_) => continue,
            Err(error) => return Outcome::Lost(error.to_string()),
        };

        for event in normalize_message(&payload) {
            process_game_event(state, &event).await;
        }
    }

    Outcome::Lost("TikFinity ngắt kết nối".to_string())
}

/// Tách một payload TikFinity (object đơn hoặc mảng) thành danh sách sự kiện.
pub fn normalize_message(payload: &str) -> Vec<IncomingEvent> {
    let Ok(parsed) = serde_json::from_str::<Value>(payload) else {
        return Vec::new();
    };
    match parsed {
        Value::Array(items) => items.iter().filter_map(normalize_one).collect(),
        other => normalize_one(&other).into_iter().collect(),
    }
}

/// Giá trị chuỗi không rỗng đầu tiên; số cũng được chấp nhận và đổi sang chuỗi.
fn first_text(value: &Value, keys: &[&str]) -> String {
    for key in keys {
        match value.get(key) {
            Some(Value::String(text)) if !text.trim().is_empty() => return text.trim().to_string(),
            Some(Value::Number(number)) => return number.to_string(),
            _ => {}
        }
    }
    String::new()
}

fn first_number(value: &Value, keys: &[&str]) -> Option<f64> {
    for key in keys {
        match value.get(key) {
            Some(Value::Number(number)) => return number.as_f64(),
            Some(Value::String(text)) => {
                if let Ok(number) = text.trim().parse::<f64>() {
                    return Some(number);
                }
            }
            _ => {}
        }
    }
    None
}

/// Gom mọi ứng viên URL (chuỗi lẫn mảng chuỗi) từ danh sách khoá.
fn collect_urls(sources: &[Option<&Value>]) -> Vec<String> {
    let mut urls = Vec::new();
    for source in sources.iter().flatten() {
        match source {
            Value::String(text) => urls.push(text.clone()),
            Value::Array(items) => {
                for item in items {
                    if let Value::String(text) = item {
                        urls.push(text.clone());
                    }
                }
            }
            _ => {}
        }
    }
    urls.retain(|url| url.starts_with("http://") || url.starts_with("https://"));
    urls
}

/// Ảnh đại diện. Ưu tiên JPEG/PNG vì `UnityWebRequestTexture` không giải mã được
/// ảnh WebP mà TikTok hay trả về.
fn avatar_url(user: &Value, data: &Value) -> String {
    let details = user.get("userDetails").or_else(|| data.get("userDetails"));
    let urls = collect_urls(&[
        details.and_then(|d| d.get("profilePictureUrls")),
        user.get("profilePictureUrls"),
        data.get("profilePictureUrls"),
        user.get("profilePictureUrlHD"),
        user.get("profilePictureUrl"),
        user.get("avatarUrl"),
        user.get("avatar"),
        user.get("picture"),
        data.get("profilePictureUrl"),
        data.get("avatarUrl"),
        data.get("avatar"),
    ]);

    urls.iter()
        .find(|url| {
            let lowered = url.to_lowercase();
            let stem = lowered.split(['?', '~']).next().unwrap_or("");
            stem.ends_with(".jpg") || stem.ends_with(".jpeg") || stem.ends_with(".png")
        })
        .or_else(|| urls.first())
        .cloned()
        .unwrap_or_default()
}

fn gift_picture_url(data: &Value, gift: &Value) -> String {
    let extended = data.get("extendedGiftInfo");
    collect_urls(&[
        data.get("giftPictureUrl"),
        gift.get("giftPictureUrl"),
        gift.get("imageUrl"),
        gift.get("pictureUrl"),
        gift.get("giftImage").and_then(|v| v.get("url")),
        gift.get("image").and_then(|v| v.get("url")),
        extended.and_then(|v| v.get("image")).and_then(|v| v.get("url")),
        extended.and_then(|v| v.get("pictureUrl")),
    ])
    .into_iter()
    .next()
    .unwrap_or_default()
}

fn apply_user(event: &mut IncomingEvent, data: &Value) {
    let empty = Value::Null;
    let user = data
        .get("user")
        .or_else(|| data.get("userData"))
        .or_else(|| data.get("author"))
        .unwrap_or(data);

    let unique_id = {
        let from_user = first_text(user, &["uniqueId", "unique_id", "username"]);
        if from_user.is_empty() {
            first_text(data, &["uniqueId", "username"])
        } else {
            from_user
        }
    };
    let nickname = {
        let from_user = first_text(user, &["nickname", "displayName", "name"]);
        if !from_user.is_empty() {
            from_user
        } else {
            let from_data = first_text(data, &["nickname"]);
            if !from_data.is_empty() {
                from_data
            } else if !unique_id.is_empty() {
                unique_id.clone()
            } else {
                "TikTok user".to_string()
            }
        }
    };
    let user_id = {
        let from_user = first_text(user, &["userId", "user_id", "id"]);
        if !from_user.is_empty() {
            from_user
        } else {
            let from_data = first_text(data, &["userId", "user_id"]);
            if !from_data.is_empty() {
                from_data
            } else if !unique_id.is_empty() {
                unique_id.clone()
            } else {
                nickname.clone()
            }
        }
    };

    event.avatar = avatar_url(if user.is_null() { &empty } else { user }, data);
    event.unique_id = if unique_id.is_empty() { user_id.clone() } else { unique_id };
    event.nickname = nickname;
    event.user_id = user_id;
}

/// Tên loại sự kiện đã rút gọn về chữ cái thường.
fn event_type(value: &Value) -> String {
    first_text(value, &["event", "eventType", "type", "name"])
        .to_lowercase()
        .chars()
        .filter(|c| c.is_ascii_alphabetic())
        .collect()
}

fn normalize_one(raw: &Value) -> Option<IncomingEvent> {
    if !raw.is_object() {
        return None;
    }
    let data = match raw.get("data") {
        Some(value) if value.is_object() => value,
        _ => raw,
    };

    let type_key = {
        let from_raw = event_type(raw);
        if from_raw.is_empty() { event_type(data) } else { from_raw }
    };
    let kind = match type_key.as_str() {
        "chat" | "comment" | "message" => "chat",
        "member" | "join" | "viewerjoin" => "member",
        "gift" => "gift",
        "like" => "like",
        "follow" => "follow",
        "share" => "share",
        _ => return None,
    };

    let mut event = IncomingEvent::new(kind);
    event.event_id = {
        let id = {
            let from_raw = first_text(raw, &["eventId", "msgId", "id"]);
            if from_raw.is_empty() {
                first_text(data, &["eventId", "msgId", "id"])
            } else {
                from_raw
            }
        };
        if id.is_empty() {
            format!(
                "tikfinity-{}-{}",
                now_ms(),
                EVENT_SEQUENCE.fetch_add(1, Ordering::Relaxed) + 1
            )
        } else {
            id
        }
    };
    apply_user(&mut event, data);

    match kind {
        "chat" => {
            event.comment = first_text(data, &["comment", "message", "text"]);
        }
        "like" => {
            event.like_count = first_number(data, &["likeCount", "count"]).unwrap_or(1.0).max(1.0) as i64;
            event.total_like_count =
                first_number(data, &["totalLikeCount", "total"]).unwrap_or(0.0).max(0.0) as i64;
        }
        "gift" => {
            let empty = Value::Null;
            let gift = data
                .get("gift")
                .or_else(|| data.get("giftDetails"))
                .or_else(|| data.get("extendedGiftInfo"))
                .unwrap_or(&empty);

            let repeat_count = first_number(data, &["repeatCount", "repeat_count", "count"])
                .unwrap_or(1.0)
                .max(1.0) as i64;
            let unit_diamonds = first_number(gift, &["diamondCount", "diamond_count"])
                .or_else(|| first_number(data, &["diamondCount", "coins"]))
                .unwrap_or(0.0)
                .max(0.0) as i64;

            event.gift_id = {
                let from_data = first_text(data, &["giftId", "gift_id"]);
                if from_data.is_empty() { first_text(gift, &["id", "giftId"]) } else { from_data }
            };
            event.gift_name = {
                let from_data = first_text(data, &["giftName"]);
                let name = if from_data.is_empty() {
                    first_text(gift, &["giftName", "name"])
                } else {
                    from_data
                };
                if name.is_empty() { "Gift".to_string() } else { name }
            };
            event.gift_type = first_number(data, &["giftType"])
                .or_else(|| first_number(gift, &["giftType"]))
                .unwrap_or(0.0) as i64;
            // TikFinity đã gộp streak sẵn, nên thiếu `repeatEnd` nghĩa là đã chốt.
            event.repeat_end = data.get("repeatEnd").and_then(Value::as_bool).unwrap_or(true);
            event.repeat_count = repeat_count;
            event.unit_diamond_count = unit_diamonds;
            event.gift_picture_url = gift_picture_url(data, gift);
            event.diamond_count = first_number(data, &["totalDiamondCount", "giftValue", "totalCoins"])
                .filter(|total| *total >= 0.0)
                .map(|total| total as i64)
                .unwrap_or(unit_diamonds * repeat_count);
        }
        _ => {}
    }

    Some(event)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ignores_payloads_that_are_not_json_objects() {
        assert!(normalize_message("khong phai json").is_empty());
        assert!(normalize_message("\"chuoi\"").is_empty());
        assert!(normalize_message("{\"event\":\"khong-biet\"}").is_empty());
    }

    #[test]
    fn maps_chat_aliases_onto_the_chat_event() {
        let events = normalize_message(
            r#"{"event":"comment","data":{"uniqueId":"abc","nickname":"ABC","comment":"xin chao"}}"#,
        );
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].kind, "chat");
        assert_eq!(events[0].comment, "xin chao");
        assert_eq!(events[0].unique_id, "abc");
        assert_eq!(events[0].nickname, "ABC");
        // Không có userId thì rơi về uniqueId.
        assert_eq!(events[0].user_id, "abc");
    }

    #[test]
    fn accepts_a_batch_array() {
        let events = normalize_message(
            r#"[{"event":"like","data":{"userId":"1","likeCount":5}},{"event":"share","data":{"userId":"2"}}]"#,
        );
        assert_eq!(events.len(), 2);
        assert_eq!(events[0].kind, "like");
        assert_eq!(events[0].like_count, 5);
        assert_eq!(events[1].kind, "share");
    }

    #[test]
    fn gift_totals_prefer_an_explicit_total_over_unit_times_repeat() {
        let events = normalize_message(
            r#"{"event":"gift","data":{"userId":"1","giftId":"5655","giftName":"Rose",
                "repeatCount":3,"gift":{"diamondCount":1},"totalDiamondCount":10}}"#,
        );
        assert_eq!(events[0].diamond_count, 10);
        assert_eq!(events[0].unit_diamond_count, 1);
        assert_eq!(events[0].repeat_count, 3);
    }

    #[test]
    fn gift_totals_fall_back_to_unit_times_repeat() {
        let events = normalize_message(
            r#"{"event":"gift","data":{"userId":"1","giftId":"5655","repeatCount":3,
                "gift":{"diamondCount":2}}}"#,
        );
        assert_eq!(events[0].diamond_count, 6);
        assert_eq!(events[0].gift_name, "Gift");
    }

    #[test]
    fn gifts_without_repeat_end_are_treated_as_finished() {
        let events = normalize_message(r#"{"event":"gift","data":{"userId":"1","giftType":1}}"#);
        assert!(events[0].repeat_end);
        assert!(!events[0].is_pending_gift_streak());

        let events =
            normalize_message(r#"{"event":"gift","data":{"userId":"1","giftType":1,"repeatEnd":false}}"#);
        assert!(events[0].is_pending_gift_streak());
    }

    #[test]
    fn avatar_prefers_a_jpeg_or_png_over_webp() {
        let events = normalize_message(
            r#"{"event":"member","data":{"userId":"1","user":{"profilePictureUrls":
                ["https://cdn.example/a.webp","https://cdn.example/a.jpeg?x=1"]}}}"#,
        );
        assert_eq!(events[0].avatar, "https://cdn.example/a.jpeg?x=1");
    }

    #[test]
    fn avatar_falls_back_to_the_first_url_when_no_jpeg_exists() {
        let events = normalize_message(
            r#"{"event":"member","data":{"userId":"1","user":{"profilePictureUrls":
                ["https://cdn.example/a.webp"]}}}"#,
        );
        assert_eq!(events[0].avatar, "https://cdn.example/a.webp");
    }

    #[test]
    fn missing_event_ids_get_a_unique_generated_one() {
        let first = normalize_message(r#"{"event":"like","data":{"userId":"1"}}"#);
        let second = normalize_message(r#"{"event":"like","data":{"userId":"1"}}"#);
        assert!(first[0].event_id.starts_with("tikfinity-"));
        assert_ne!(first[0].event_id, second[0].event_id);
    }

    #[test]
    fn flat_payloads_without_a_data_wrapper_still_work() {
        let events = normalize_message(r#"{"eventType":"follow","userId":"9","nickname":"Ai Do"}"#);
        assert_eq!(events[0].kind, "follow");
        assert_eq!(events[0].user_id, "9");
        assert_eq!(events[0].nickname, "Ai Do");
    }
}
