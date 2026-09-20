//! Kết nối tới nguồn sự kiện live và tự kết nối lại khi rớt.

pub mod demo;
pub mod tikfinity;
pub mod tiktok;

use crate::session::state::SharedState;
use std::time::Duration;

/// Kết quả một lượt kết nối.
pub enum Outcome {
    /// Chủ phòng đã tắt live — không thử lại nữa.
    Ended,
    /// Mất kết nối vì lý do khác — thử lại.
    Lost(String),
}

/// Username TikTok hợp lệ: 2–24 ký tự chữ, số, dấu chấm hoặc gạch dưới.
pub fn normalize_username(value: &str) -> Option<String> {
    let username = value.trim().trim_start_matches('@');
    let length = username.chars().count();
    if !(2..=24).contains(&length) {
        return None;
    }
    if !username
        .chars()
        .all(|c| c.is_ascii_alphanumeric() || c == '.' || c == '_')
    {
        return None;
    }
    Some(username.to_string())
}

/// Ngắt kết nối live hiện tại và đợi tác vụ dừng hẳn.
pub async fn disconnect(state: &SharedState) {
    // Finish any event/config write before cancelling the provider task.
    let _event_guard = state.event_gate.lock().await;
    let handle = state.live_task.lock().await.take();
    if let Some(handle) = handle {
        handle.abort();
        let _ = handle.await;
    }
}

/// Bắt đầu theo dõi một tài khoản. Tự dừng demo và kết nối cũ trước.
pub async fn connect(state: SharedState, username: String) {
    disconnect(&state).await;
    demo::stop(&state).await;

    let event_guard = state.event_gate.lock().await;
    {
        let mut session = state.session.write().await;
        session.reset("tiktok");
    }
    state.broadcast_json(&serde_json::json!({ "type": "reset" }));
    state.broadcast_metrics().await;
    drop(event_guard);

    let task_state = state.clone();
    let handle = tokio::spawn(async move { run_with_reconnect(task_state, username).await });
    *state.live_task.lock().await = Some(handle);
}

/// Vòng kết nối lại với backoff luỹ thừa, tối đa 30 giây — giống bản JS.
async fn run_with_reconnect(state: SharedState, username: String) {
    let tikfinity = state.live_provider == "tikfinity";
    let mut failures: u32 = 0;

    loop {
        let reconnecting = failures > 0;
        let message = match (tikfinity, reconnecting) {
            (true, false) => "Đang kết nối TikFinity Desktop...".to_string(),
            (true, true) => "Đang kết nối lại TikFinity Desktop...".to_string(),
            (false, false) => format!("Đang kết nối @{username}..."),
            (false, true) => format!("Đang kết nối lại @{username}..."),
        };
        state
            .set_status(
                if reconnecting { "reconnecting" } else { "connecting" },
                Some(username.clone()),
                message,
            )
            .await;

        let outcome = if tikfinity {
            tikfinity::run(&state, &username, &mut failures).await
        } else {
            tiktok::run(&state, &username, &mut failures).await
        };

        match outcome {
            Outcome::Ended => {
                state
                    .set_status("ended", Some(username.clone()), format!("Live @{username} đã kết thúc"))
                    .await;
                return;
            }
            Outcome::Lost(reason) => {
                tracing::warn!("mất kết nối live: {reason}");
            }
        }

        failures += 1;
        let delay_ms = (2000_u64 << failures.min(4)).min(30_000);
        let seconds = delay_ms.div_ceil(1000);
        let message = if tikfinity {
            format!("Chưa thấy TikFinity Desktop. Tự thử lại sau {seconds} giây...")
        } else {
            format!("Mất kết nối TikTok. Tự kết nối lại sau {seconds} giây...")
        };
        state
            .set_status("reconnecting", Some(username.clone()), message)
            .await;
        tokio::time::sleep(Duration::from_millis(delay_ms)).await;
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_plain_usernames_with_or_without_an_at_sign() {
        assert_eq!(normalize_username("wangnguen").as_deref(), Some("wangnguen"));
        assert_eq!(normalize_username("@wangnguen").as_deref(), Some("wangnguen"));
        assert_eq!(normalize_username("  @wang.nguen_79 ").as_deref(), Some("wang.nguen_79"));
        assert_eq!(normalize_username("ab").as_deref(), Some("ab"));
    }

    #[test]
    fn rejects_names_that_are_too_short_too_long_or_have_odd_characters() {
        assert!(normalize_username("a").is_none());
        assert!(normalize_username(&"a".repeat(25)).is_none());
        assert!(normalize_username("ten co dau").is_none());
        assert!(normalize_username("user/name").is_none());
        assert!(normalize_username("nguyễn").is_none());
        assert!(normalize_username("").is_none());
    }

    #[test]
    fn backoff_matches_the_js_schedule_and_caps_at_thirty_seconds() {
        let delay = |failures: u32| (2000_u64 << failures.min(4)).min(30_000);
        assert_eq!(delay(1), 4_000);
        assert_eq!(delay(2), 8_000);
        assert_eq!(delay(3), 16_000);
        assert_eq!(delay(4), 30_000);
        assert_eq!(delay(9), 30_000);
    }
}
