//! Luật Master do vận hành viên cấu hình — port của `src/master/rules.js`.
//!
//! File `config/master.json` được server ghi lại (khi lưu từ bảng điều khiển, hoặc khi
//! tự gắn Gift ID học được), nên kiểu dữ liệu ở đây phải round-trip đúng hình dạng JSON cũ.

use crate::domain::event::GameEvent;
use crate::domain::text::{normalize_text, split_triggers};
use serde::{Deserialize, Serialize};

const SOURCES: [&str; 2] = ["chat", "gift"];
const ACTIONS: [&str; 10] = [
    "join", "dance", "camera", "change", "walk", "grow", "medal", "vip", "topdj", "fireworks",
];
const VARIANTS: [&str; 4] = ["aura", "neon", "fire", "royal"];

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MasterRule {
    pub id: String,
    pub enabled: bool,
    pub source: String,
    pub trigger: String,
    pub gift_id: String,
    #[serde(rename = "match")]
    pub match_mode: String,
    pub action: String,
    pub display_diamonds: i64,
    pub duration_ms: i64,
    pub label: String,
    pub variant: String,
    pub firework_bursts: i64,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct MasterConfig {
    pub join_mode: String,
    pub gift_always_joins: bool,
    pub rules: Vec<MasterRule>,
}

impl Default for MasterConfig {
    fn default() -> Self {
        Self {
            join_mode: "keyword_only".to_string(),
            gift_always_joins: true,
            rules: Vec::new(),
        }
    }
}

/// Dạng "lỏng" để nhận JSON không tin cậy từ bảng điều khiển hoặc file cấu hình.
/// Mọi field đều tuỳ chọn và kiểu số nhận `serde_json::Value` để không vỡ khi gặp rác.
#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct RawMasterRule {
    pub id: Option<String>,
    pub enabled: Option<bool>,
    pub source: Option<String>,
    pub trigger: Option<String>,
    pub gift_id: Option<String>,
    #[serde(rename = "match")]
    pub match_mode: Option<String>,
    pub action: Option<String>,
    pub display_diamonds: Option<serde_json::Value>,
    pub duration_ms: Option<serde_json::Value>,
    pub label: Option<String>,
    pub variant: Option<String>,
    pub firework_bursts: Option<serde_json::Value>,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub struct RawMasterConfig {
    pub join_mode: Option<String>,
    pub gift_always_joins: Option<bool>,
    pub rules: Option<Vec<RawMasterRule>>,
}

/// Đọc số từ JSON lỏng lẻo rồi kẹp vào khoảng cho phép, giống `Number(x) || 0` bên JS.
fn bounded_int(value: &Option<serde_json::Value>, minimum: i64, maximum: i64) -> i64 {
    let number = match value {
        Some(serde_json::Value::Number(n)) => n.as_f64().unwrap_or(0.0),
        Some(serde_json::Value::String(s)) => s.trim().parse::<f64>().unwrap_or(0.0),
        _ => 0.0,
    };
    if !number.is_finite() {
        return minimum.max(0).min(maximum);
    }
    (number as i64).clamp(minimum, maximum)
}

fn cap_chars(value: &str, maximum: usize) -> String {
    value.chars().take(maximum).collect()
}

/// Chuẩn hoá một luật về dạng hợp lệ. Giá trị sai kiểu rơi về mặc định an toàn
/// thay vì bị từ chối, để bảng điều khiển không bao giờ lưu được cấu hình hỏng.
pub fn sanitize_rule(input: &RawMasterRule) -> MasterRule {
    let generated_id = uuid::Uuid::new_v4().to_string();
    let id: String = input
        .id
        .as_deref()
        .unwrap_or(&generated_id)
        .chars()
        .filter(|c| c.is_ascii_alphanumeric() || *c == '_' || *c == '-')
        .take(64)
        .collect();

    let source = input
        .source
        .as_deref()
        .filter(|s| SOURCES.contains(s))
        .unwrap_or("gift")
        .to_string();

    let action = input
        .action
        .as_deref()
        .filter(|a| ACTIONS.contains(a))
        .unwrap_or("dance")
        .to_string();

    let variant = input
        .variant
        .as_deref()
        .filter(|v| VARIANTS.contains(v))
        .unwrap_or("")
        .to_string();

    MasterRule {
        id: if id.is_empty() { generated_id } else { id },
        enabled: input.enabled.unwrap_or(true),
        source,
        trigger: cap_chars(input.trigger.as_deref().unwrap_or("").trim(), 300),
        gift_id: cap_chars(input.gift_id.as_deref().unwrap_or("").trim(), 80),
        match_mode: if input.match_mode.as_deref() == Some("contains") {
            "contains".to_string()
        } else {
            "exact".to_string()
        },
        action,
        display_diamonds: bounded_int(&input.display_diamonds, 0, 1_000_000),
        duration_ms: bounded_int(&input.duration_ms, 0, 300_000),
        label: cap_chars(input.label.as_deref().unwrap_or("").trim(), 80),
        variant,
        firework_bursts: bounded_int(&input.firework_bursts, 0, 12),
    }
}

/// Chuẩn hoá toàn bộ cấu hình Master, tối đa 100 luật.
pub fn sanitize_master_config(input: &RawMasterConfig) -> MasterConfig {
    MasterConfig {
        join_mode: if input.join_mode.as_deref() == Some("all_interactions") {
            "all_interactions".to_string()
        } else {
            "keyword_only".to_string()
        },
        gift_always_joins: input.gift_always_joins.unwrap_or(true),
        rules: input
            .rules
            .as_deref()
            .unwrap_or_default()
            .iter()
            .take(100)
            .map(sanitize_rule)
            .collect(),
    }
}

/// Luật có khớp sự kiện không? Gift ID khớp thì thắng ngay, không cần xét tên.
fn matches_rule(rule: &MasterRule, event: &GameEvent) -> bool {
    if !rule.enabled || rule.source != event.kind {
        return false;
    }
    if rule.source == "gift" && !rule.gift_id.is_empty() && event.gift_id == rule.gift_id {
        return true;
    }
    let value = normalize_text(if rule.source == "chat" {
        &event.comment
    } else {
        &event.gift_name
    });
    if value.is_empty() {
        return false;
    }
    split_triggers(&rule.trigger).iter().any(|trigger| {
        if rule.match_mode == "contains" {
            value.contains(trigger.as_str())
        } else {
            value == *trigger
        }
    })
}

/// Tìm luật áp dụng cho sự kiện.
///
/// Luật gắn Gift ID luôn được ưu tiên quét trước: tên quà bị TikTok đổi theo ngôn ngữ
/// người xem, còn Gift ID thì không.
pub fn resolve_master_rule<'a>(config: &'a MasterConfig, event: &GameEvent) -> Option<&'a MasterRule> {
    if event.is("gift") && !event.gift_id.is_empty() {
        let by_id = config
            .rules
            .iter()
            .find(|rule| rule.enabled && rule.source == "gift" && rule.gift_id == event.gift_id);
        if by_id.is_some() {
            return by_id;
        }
    }
    config.rules.iter().find(|rule| matches_rule(rule, event))
}

/// Gắn hiệu ứng của luật vào sự kiện.
pub fn apply_rule(event: &mut GameEvent, rule: Option<&MasterRule>) {
    let Some(rule) = rule else { return };
    event.action = rule.action.clone();
    event.duration_ms = rule.duration_ms;
    event.label = rule.label.clone();
    event.variant = rule.variant.clone();
    event.firework_bursts = rule.firework_bursts;
    event.master_rule_id = rule.id.clone();
}

/// Lệnh chat dựng sẵn: `jump` / `nhảy` luôn nhảy một nhịp ngắn, không cần luật Master.
pub fn apply_built_in_chat_command(event: &mut GameEvent) {
    if !event.is("chat") {
        return;
    }
    let command = normalize_text(&event.comment);
    if command != "jump" && command != "nhay" {
        return;
    }
    event.action = "jump".to_string();
    event.duration_ms = 950;
    event.master_rule_id = String::new();
}

#[cfg(test)]
mod tests {
    use super::*;

    fn raw(json: serde_json::Value) -> RawMasterConfig {
        serde_json::from_value(json).unwrap()
    }

    fn master() -> MasterConfig {
        sanitize_master_config(&raw(serde_json::json!({
            "joinMode": "keyword_only",
            "giftAlwaysJoins": true,
            "rules": [
                { "id": "hey", "source": "chat", "trigger": "hey", "action": "join" },
                { "id": "rose", "source": "gift", "trigger": "Rose, Hoa hồng", "action": "camera", "durationMs": 4000 },
                { "id": "rosa", "source": "gift", "giftId": "777", "trigger": "Rosa", "action": "medal", "label": "CÁNH VIP" }
            ]
        })))
    }

    fn gift_event(gift_id: &str, gift_name: &str) -> GameEvent {
        GameEvent {
            kind: "gift".into(),
            gift_id: gift_id.into(),
            gift_name: gift_name.into(),
            ..Default::default()
        }
    }

    fn chat_event(comment: &str) -> GameEvent {
        GameEvent {
            kind: "chat".into(),
            comment: comment.into(),
            ..Default::default()
        }
    }

    #[test]
    fn matches_vietnamese_aliases_without_accents_or_case_sensitivity() {
        let config = master();
        let mut event = gift_event("", "HOA HỒNG");
        let rule = resolve_master_rule(&config, &event).unwrap();
        assert_eq!(rule.id, "rose");

        let rule = rule.clone();
        apply_rule(&mut event, Some(&rule));
        assert_eq!(event.action, "camera");
        assert_eq!(event.duration_ms, 4000);
    }

    #[test]
    fn gift_id_wins_even_when_localized_gift_name_changes() {
        let config = master();
        let event = gift_event("777", "Localized name");
        let rule = resolve_master_rule(&config, &event).unwrap().clone();
        assert_eq!(rule.id, "rosa");

        let mut applied = gift_event("777", "");
        apply_rule(&mut applied, Some(&rule));
        assert_eq!(applied.label, "CÁNH VIP");
    }

    #[test]
    fn gift_id_rule_wins_over_an_earlier_matching_name_rule() {
        let config = sanitize_master_config(&raw(serde_json::json!({
            "rules": [
                { "id": "generic-name", "source": "gift", "trigger": "Rose", "action": "dance" },
                { "id": "learned-id", "source": "gift", "trigger": "Rose", "giftId": "5655", "action": "camera" }
            ]
        })));
        let event = gift_event("5655", "Rose");
        assert_eq!(resolve_master_rule(&config, &event).unwrap().id, "learned-id");
    }

    #[test]
    fn chat_hey_maps_to_join() {
        let config = master();
        let rule = resolve_master_rule(&config, &chat_event("hey")).unwrap();
        assert_eq!(rule.action, "join");
    }

    #[test]
    fn jump_and_nhay_comments_trigger_a_short_built_in_jump() {
        let mut event = chat_event("jump");
        apply_built_in_chat_command(&mut event);
        assert_eq!(event.action, "jump");

        let mut event = chat_event("NHẢY");
        apply_built_in_chat_command(&mut event);
        assert_eq!(event.action, "jump");
        assert_eq!(event.duration_ms, 950);

        // Không áp cho sự kiện gift dù tên quà là "Jump".
        let mut event = gift_event("", "Jump");
        apply_built_in_chat_command(&mut event);
        assert_eq!(event.action, "");
    }

    #[test]
    fn contains_matching_is_opt_in_per_rule() {
        let config = sanitize_master_config(&raw(serde_json::json!({
            "rules": [
                { "id": "exact", "source": "chat", "trigger": "hi", "action": "dance" },
                { "id": "loose", "source": "chat", "trigger": "chao", "match": "contains", "action": "join" }
            ]
        })));
        assert!(resolve_master_rule(&config, &chat_event("hi there")).is_none());
        assert_eq!(
            resolve_master_rule(&config, &chat_event("xin chao moi nguoi")).unwrap().id,
            "loose"
        );
    }

    #[test]
    fn disabled_rules_never_match() {
        let config = sanitize_master_config(&raw(serde_json::json!({
            "rules": [{ "id": "off", "source": "chat", "trigger": "hey", "action": "join", "enabled": false }]
        })));
        assert!(resolve_master_rule(&config, &chat_event("hey")).is_none());
    }

    #[test]
    fn sanitize_clamps_numbers_and_rejects_unknown_enums() {
        let config = sanitize_master_config(&raw(serde_json::json!({
            "joinMode": "nonsense",
            "rules": [{
                "id": "a b/c!", "source": "hack", "action": "selfdestruct", "variant": "rainbow",
                "durationMs": 9_999_999, "fireworkBursts": 999, "displayDiamonds": -5
            }]
        })));
        assert_eq!(config.join_mode, "keyword_only");
        let rule = &config.rules[0];
        assert_eq!(rule.id, "abc"); // ký tự lạ bị lọc
        assert_eq!(rule.source, "gift");
        assert_eq!(rule.action, "dance");
        assert_eq!(rule.variant, "");
        assert_eq!(rule.duration_ms, 300_000);
        assert_eq!(rule.firework_bursts, 12);
        assert_eq!(rule.display_diamonds, 0);
    }

    #[test]
    fn sanitize_caps_the_rule_list_at_one_hundred() {
        let rules: Vec<_> = (0..150)
            .map(|i| serde_json::json!({ "id": format!("r{i}"), "source": "chat", "trigger": "x" }))
            .collect();
        let config = sanitize_master_config(&raw(serde_json::json!({ "rules": rules })));
        assert_eq!(config.rules.len(), 100);
    }

    #[test]
    fn config_round_trips_through_json_unchanged() {
        let config = master();
        let encoded = serde_json::to_string(&config).unwrap();
        let decoded: MasterConfig = serde_json::from_str(&encoded).unwrap();
        assert_eq!(config, decoded);
        // Khoá JSON phải giữ nguyên tên cũ để bảng điều khiển đọc được.
        assert!(encoded.contains("\"joinMode\""));
        assert!(encoded.contains("\"giftAlwaysJoins\""));
        assert!(encoded.contains("\"giftId\""));
        assert!(encoded.contains("\"match\""));
        assert!(encoded.contains("\"displayDiamonds\""));
        assert!(encoded.contains("\"fireworkBursts\""));
    }
}
