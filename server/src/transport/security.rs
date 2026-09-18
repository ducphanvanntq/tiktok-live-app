//! Lọc dữ liệu không tin cậy và kiểm soát truy cập — port của `src/security.js`.
//!
//! Mọi thứ đến từ TikTok, TikFinity hay WebSocket client đều đi qua đây trước khi
//! chạm vào state hoặc được broadcast.

use crate::domain::event::{GameEvent, IncomingEvent, EVENT_TYPES};
use url::{Host, Url};

/// Giới hạn mặc định của rate limiter WebSocket: 40 lệnh mỗi 5 giây.
pub const RATE_LIMIT_MAX: u32 = 40;
pub const RATE_LIMIT_WINDOW_MS: i64 = 5000;

/// Thay ký tự điều khiển bằng khoảng trắng, gộp khoảng trắng liên tiếp, trim, rồi cắt độ dài.
///
/// Khác biệt nhỏ so với JS: bản JS cắt theo đơn vị mã UTF-16, bản này cắt theo ký tự Unicode
/// nên emoji không bị xé đôi. Với nickname/comment thực tế kết quả như nhau.
pub fn clean_text(value: &str, maximum_length: usize) -> String {
    let replaced: String = value
        .chars()
        .map(|c| if is_control_char(c) { ' ' } else { c })
        .collect();

    let mut out = String::with_capacity(replaced.len());
    let mut pending_space = false;
    for c in replaced.chars() {
        if c.is_whitespace() {
            pending_space = true;
            continue;
        }
        if pending_space && !out.is_empty() {
            out.push(' ');
        }
        pending_space = false;
        out.push(c);
    }

    out.chars().take(maximum_length).collect()
}

fn is_control_char(c: char) -> bool {
    matches!(c, '\u{0}'..='\u{1f}' | '\u{7f}')
}

/// Kẹp số vào khoảng cho phép; giá trị không hữu hạn (NaN/Infinity) rơi về `fallback`.
pub fn bounded_number(value: f64, minimum: f64, maximum: f64, fallback: f64) -> f64 {
    if !value.is_finite() {
        return fallback;
    }
    value.clamp(minimum, maximum)
}

/// Chỉ chấp nhận URL `https:`. Mọi thứ khác (kể cả `javascript:` và `http:`) thành chuỗi rỗng.
pub fn safe_https_url(value: &str) -> String {
    let text = clean_text(value, 2048);
    if text.is_empty() {
        return String::new();
    }
    match Url::parse(&text) {
        Ok(url) if url.scheme() == "https" => url.to_string(),
        _ => String::new(),
    }
}

/// Lọc sự kiện thô thành `GameEvent` an toàn. Trả `None` nếu loại sự kiện không hợp lệ.
pub fn sanitize_game_event(input: &IncomingEvent) -> Option<GameEvent> {
    if !EVENT_TYPES.contains(&input.kind.as_str()) {
        return None;
    }

    let nickname = clean_text(&input.nickname, 80);
    Some(GameEvent {
        kind: input.kind.clone(),
        event_id: clean_text(&input.event_id, 180),
        user_id: clean_text(&input.user_id, 128),
        unique_id: clean_text(&input.unique_id, 64),
        nickname: if nickname.is_empty() { "TikTok user".to_string() } else { nickname },
        avatar: safe_https_url(&input.avatar),
        comment: clean_text(&input.comment, 300),
        gift_id: clean_text(&input.gift_id, 80),
        gift_name: clean_text(&input.gift_name, 100),
        gift_picture_url: safe_https_url(&input.gift_picture_url),
        repeat_count: bounded_number(input.repeat_count as f64, 0.0, 100_000.0, 0.0) as i64,
        unit_diamond_count: bounded_number(input.unit_diamond_count as f64, 0.0, 100_000_000.0, 0.0) as i64,
        diamond_count: bounded_number(input.diamond_count as f64, 0.0, 1_000_000_000.0, 0.0) as i64,
        like_count: bounded_number(input.like_count as f64, 0.0, 1_000_000.0, 0.0) as i64,
        spectator_only: input.spectator_only,
        ..Default::default()
    })
}

/// Địa chỉ loopback, kể cả dạng IPv4-mapped `::ffff:127.0.0.1`.
pub fn is_loopback_address(value: &str) -> bool {
    let lowered = value.trim().to_lowercase();
    let address = lowered.strip_prefix("::ffff:").unwrap_or(&lowered);
    address == "127.0.0.1" || address == "::1"
}

/// Header `Host` phải trỏ về chính máy này, trừ khi vận hành viên bật `ALLOW_LAN=1`.
pub fn is_allowed_host(value: &str, port: u16, allow_lan: bool) -> bool {
    let host = value.trim().to_lowercase();
    if host.is_empty() || host.len() > 255 {
        return false;
    }
    if host.chars().any(|c| c.is_whitespace() || c == '/' || c == '\\') {
        return false;
    }
    if allow_lan {
        return true;
    }
    host == format!("127.0.0.1:{port}")
        || host == format!("localhost:{port}")
        || host == format!("[::1]:{port}")
}

/// Origin rỗng nghĩa là client gốc (Unity dùng `ClientWebSocket`, không gửi header `Origin`)
/// — được phép. Origin có giá trị thì bắt buộc là `http://` loopback đúng cổng.
pub fn is_allowed_origin(value: &str, port: u16) -> bool {
    if value.is_empty() {
        return true;
    }
    let Ok(origin) = Url::parse(value) else {
        return false;
    };
    if origin.scheme() != "http" || origin.port() != Some(port) {
        return false;
    }
    match origin.host() {
        Some(Host::Domain(name)) => name.eq_ignore_ascii_case("localhost"),
        Some(Host::Ipv4(address)) => address.is_loopback(),
        Some(Host::Ipv6(address)) => address.is_loopback(),
        None => false,
    }
}

/// Cửa sổ trượt đơn giản cho tốc độ lệnh WebSocket.
#[derive(Debug, Clone, Default)]
pub struct RateLimit {
    window_started_at: i64,
    count: u32,
}

impl RateLimit {
    /// Ghi nhận một lệnh; trả `false` khi đã vượt hạn mức trong cửa sổ hiện tại.
    pub fn consume(&mut self, now: i64, maximum: u32, window_ms: i64) -> bool {
        if self.window_started_at == 0 || now - self.window_started_at >= window_ms {
            self.window_started_at = now;
            self.count = 0;
        }
        self.count += 1;
        self.count <= maximum
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_only_local_http_hosts_and_origins_by_default() {
        assert!(is_allowed_host("127.0.0.1:3000", 3000, false));
        assert!(!is_allowed_host("evil.example:3000", 3000, false));
        assert!(is_allowed_origin("http://localhost:3000", 3000));
        assert!(!is_allowed_origin("https://evil.example", 3000));
        assert!(is_loopback_address("::ffff:127.0.0.1"));
    }

    #[test]
    fn host_check_rejects_separators_and_overlong_values() {
        assert!(!is_allowed_host("127.0.0.1:3000/evil", 3000, false));
        assert!(!is_allowed_host("127.0.0.1:3000\\evil", 3000, false));
        assert!(!is_allowed_host("", 3000, false));
        assert!(!is_allowed_host(&"a".repeat(256), 3000, false));
        // ALLOW_LAN bỏ qua danh sách trắng nhưng vẫn giữ các kiểm tra hình thức.
        assert!(is_allowed_host("192.168.1.10:3000", 3000, true));
        assert!(!is_allowed_host("bad host", 3000, true));
    }

    #[test]
    fn origin_check_requires_matching_port_and_plain_http() {
        assert!(is_allowed_origin("http://127.0.0.1:3000", 3000));
        assert!(is_allowed_origin("http://[::1]:3000", 3000));
        assert!(!is_allowed_origin("http://localhost:3001", 3000));
        assert!(!is_allowed_origin("http://localhost", 3000));
        assert!(!is_allowed_origin("not a url", 3000));
        // Client gốc (Unity) không gửi Origin.
        assert!(is_allowed_origin("", 3000));
    }

    #[test]
    fn sanitizes_untrusted_tiktok_event_fields_and_urls() {
        let event = sanitize_game_event(&IncomingEvent {
            kind: "gift".into(),
            user_id: "user\u{0}id".into(),
            nickname: "A".repeat(200),
            avatar: "javascript:alert(1)".into(),
            gift_picture_url: "https://cdn.example/gift.png".into(),
            diamond_count: i64::MAX,
            comment: "hello\nworld".into(),
            ..Default::default()
        })
        .unwrap();

        assert_eq!(event.user_id, "user id");
        assert_eq!(event.nickname.chars().count(), 80);
        assert_eq!(event.avatar, "");
        assert_eq!(event.gift_picture_url, "https://cdn.example/gift.png");
        assert_eq!(event.comment, "hello world");
        assert_eq!(event.diamond_count, 1_000_000_000);
    }

    #[test]
    fn rejects_unknown_event_types() {
        let mut event = IncomingEvent::new("admin");
        assert!(sanitize_game_event(&event).is_none());
        event.kind = "chat".into();
        assert!(sanitize_game_event(&event).is_some());
    }

    #[test]
    fn empty_nickname_falls_back_to_a_placeholder() {
        let event = sanitize_game_event(&IncomingEvent::new("chat")).unwrap();
        assert_eq!(event.nickname, "TikTok user");
    }

    #[test]
    fn non_finite_numbers_fall_back_to_zero() {
        assert_eq!(bounded_number(f64::INFINITY, 0.0, 100.0, 0.0), 0.0);
        assert_eq!(bounded_number(f64::NAN, 0.0, 100.0, 0.0), 0.0);
        assert_eq!(bounded_number(500.0, 0.0, 100.0, 0.0), 100.0);
        assert_eq!(bounded_number(-5.0, 0.0, 100.0, 0.0), 0.0);
    }

    #[test]
    fn only_https_urls_survive() {
        assert_eq!(safe_https_url("https://cdn.example/a.png"), "https://cdn.example/a.png");
        assert_eq!(safe_https_url("http://cdn.example/a.png"), "");
        assert_eq!(safe_https_url("javascript:alert(1)"), "");
        assert_eq!(safe_https_url(""), "");
    }

    #[test]
    fn rate_limiter_closes_the_window_after_its_configured_capacity() {
        let mut state = RateLimit::default();
        assert!(state.consume(1000, 2, 5000));
        assert!(state.consume(1001, 2, 5000));
        assert!(!state.consume(1002, 2, 5000));
        // Sang cửa sổ mới thì mở lại.
        assert!(state.consume(7000, 2, 5000));
    }
}
