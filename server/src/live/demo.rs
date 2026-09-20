//! Chế độ demo: sinh người xem giả để thử sàn nhảy khi không có live thật.

use crate::domain::event::IncomingEvent;
use crate::session::pipeline::{process_game_event, process_operator_join};
use crate::session::state::SharedState;
use rand::Rng;
use std::time::Duration;

/// Nhịp sinh sự kiện ngẫu nhiên.
const TICK_MS: u64 = 700;
/// Giãn cách giữa các lượt người xem vào phòng lúc khởi động.
const JOIN_STAGGER_MS: u64 = 35;

/// Dừng demo đang chạy (nếu có) và đợi tác vụ kết thúc hẳn.
pub async fn stop(state: &SharedState) -> bool {
    let _event_guard = state.event_gate.lock().await;
    let handle = state.demo_task.lock().await.take();
    if let Some(handle) = handle {
        handle.abort();
        let _ = handle.await;
        return true;
    }
    false
}

/// Khởi động demo với `count` người xem giả.
pub async fn start(state: SharedState, count: usize) {
    stop(&state).await;

    let event_guard = state.event_gate.lock().await;
    {
        let mut session = state.session.write().await;
        session.reset("demo");
    }
    state
        .set_status("demo", None, format!("Chế độ demo: {count} người"))
        .await;
    state.broadcast_json(&serde_json::json!({ "type": "reset" }));
    state.broadcast_metrics().await;
    drop(event_guard);

    let task_state = state.clone();
    let handle = tokio::spawn(async move {
        // Cho người xem vào dần để overlay kịp dựng nhân vật.
        for index in 1..=count {
            tokio::time::sleep(Duration::from_millis(JOIN_STAGGER_MS)).await;
            emit_member(&task_state, index, None).await;
        }

        let mut ticker = tokio::time::interval(Duration::from_millis(TICK_MS));
        loop {
            ticker.tick().await;
            // Chọn hành động theo phân phối giống bản JS: chủ yếu nhảy, thỉnh thoảng quà.
            let (user_index, roll) = {
                let mut rng = rand::rng();
                (rng.random_range(1..=count), rng.random::<f64>())
            };

            if roll < 0.45 {
                emit_action(&task_state, "dance", user_index, 1, "", None).await;
            } else if roll < 0.65 {
                emit_action(&task_state, "walk", user_index, 1, "", None).await;
            } else if roll < 0.78 {
                emit_action(&task_state, "change", user_index, 1, "", None).await;
            } else if roll < 0.93 {
                emit_action(&task_state, "like", user_index, 10, "", None).await;
            } else {
                let value = if rand::rng().random::<f64>() < 0.2 { 100 } else { 10 };
                emit_action(&task_state, "gift", user_index, value, "", None).await;
            }
        }
    });

    *state.demo_task.lock().await = Some(handle);
}

/// Người xem giả. `manual_name` cho phép vận hành viên test bằng tên thật của mình.
fn mock_user(event: &mut IncomingEvent, index: usize, manual_name: Option<&str>) {
    match manual_name.map(str::trim).filter(|name| !name.is_empty()) {
        Some(name) => {
            event.user_id = name.to_string();
            event.unique_id = name.to_string();
            event.nickname = name.to_string();
        }
        None => {
            event.user_id = format!("demo-{index}");
            event.unique_id = format!("dancer_{index}");
            event.nickname = format!("Dancer {index}");
        }
    }
}

fn demo_event_id(action: &str) -> String {
    format!(
        "demo-{action}-{}-{}",
        crate::session::state::now_ms(),
        rand::rng().random::<u32>()
    )
}

async fn emit_member(state: &SharedState, index: usize, manual_name: Option<&str>) {
    let mut event = IncomingEvent::new("member");
    event.event_id = demo_event_id("join");
    mock_user(&mut event, index, manual_name);
    process_operator_join(state, &event).await;
}

/// Phát một hành động demo. Dùng cho cả nút bấm thủ công lẫn vòng lặp tự động.
pub async fn emit_action(
    state: &SharedState,
    action: &str,
    user_index: usize,
    value: i64,
    gift_name: &str,
    manual_name: Option<&str>,
) {
    if action == "member" {
        return emit_member(state, user_index, manual_name).await;
    }

    // Các lệnh dưới đây đi qua đúng đường chat thật, nên vẫn chịu luật Master.
    let chat_comment = match action {
        "dance" => Some("dance"),
        "jump" => Some("jump"),
        "walk" => Some("đi vòng"),
        "change" => Some("đổi nv"),
        _ => None,
    };

    let mut event = if let Some(comment) = chat_comment {
        let mut event = IncomingEvent::new("chat");
        event.comment = comment.to_string();
        event
    } else {
        match action {
            "like" => {
                let mut event = IncomingEvent::new("like");
                event.like_count = value;
                event
            }
            "follow" | "share" => IncomingEvent::new(action),
            "gift" => {
                let mut event = IncomingEvent::new("gift");
                event.gift_id = format!("demo-{value}");
                event.gift_name = if gift_name.trim().is_empty() {
                    demo_gift_name(value).to_string()
                } else {
                    gift_name.trim().to_string()
                };
                event.repeat_count = 1;
                event.diamond_count = value;
                event
            }
            _ => return,
        }
    };

    event.event_id = demo_event_id(action);
    mock_user(&mut event, user_index, manual_name);
    process_game_event(state, &event).await;
}

/// Tên quà giả theo mệnh giá, để bảng điều khiển hiển thị cho dễ nhìn.
fn demo_gift_name(value: i64) -> &'static str {
    match value {
        1 => "Rose",
        5 => "Dance Pop",
        20 => "Camera Star",
        50 => "Fire Crown",
        100 => "VIP Gift",
        200 => "Super VIP",
        500 => "Firework Rain",
        1000 => "Party Universe",
        v if v >= 100 => "Fireworks",
        v if v >= 10 => "VIP Gift",
        _ => "Rose",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn demo_gift_names_cover_the_control_panel_presets() {
        assert_eq!(demo_gift_name(1), "Rose");
        assert_eq!(demo_gift_name(1000), "Party Universe");
        // Giá trị ngoài preset rơi về nhóm theo mệnh giá.
        assert_eq!(demo_gift_name(300), "Fireworks");
        assert_eq!(demo_gift_name(15), "VIP Gift");
        assert_eq!(demo_gift_name(2), "Rose");
    }

    #[test]
    fn mock_user_prefers_a_manual_name_when_given() {
        let mut event = IncomingEvent::new("chat");
        mock_user(&mut event, 7, None);
        assert_eq!(event.user_id, "demo-7");
        assert_eq!(event.unique_id, "dancer_7");
        assert_eq!(event.nickname, "Dancer 7");

        let mut event = IncomingEvent::new("chat");
        mock_user(&mut event, 7, Some("  ong_chu  "));
        assert_eq!(event.user_id, "ong_chu");
        assert_eq!(event.nickname, "ong_chu");

        // Tên rỗng thì vẫn dùng người xem giả.
        let mut event = IncomingEvent::new("chat");
        mock_user(&mut event, 3, Some("   "));
        assert_eq!(event.user_id, "demo-3");
    }
}
