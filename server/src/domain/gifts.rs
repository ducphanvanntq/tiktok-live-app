//! Bảng quà: tra cứu hiệu ứng theo Gift ID / tên / mốc kim cương, và thư viện gift học được.
//!
//! Port của `resolveGiftRule` trong `server.js` cùng `src/tiktok/observed-gift.js`.

use crate::domain::event::GameEvent;
use crate::domain::text::normalize_text;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

/// Hiệu ứng gắn cho một sự kiện gift.
#[derive(Debug, Clone, Default, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct GiftRule {
    pub action: String,
    pub duration_ms: i64,
    pub label: String,
    pub variant: String,
    pub firework_bursts: i64,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct NamedGiftRule {
    aliases: Vec<String>,
    #[serde(flatten)]
    rule: GiftRule,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct DiamondBand {
    minimum: i64,
    #[serde(flatten)]
    rule: GiftRule,
}

#[derive(Debug, Clone, Default, Deserialize)]
#[serde(default, rename_all = "camelCase")]
struct GiftConfigView {
    by_gift_id: BTreeMap<String, GiftRule>,
    by_gift_name: Vec<NamedGiftRule>,
    diamond_bands: Vec<DiamondBand>,
}

/// Bảng quà đã nạp. Giữ cả bản JSON gốc để `/api/config` trả về nguyên vẹn —
/// client còn đọc các khoá khác (`evolutionTiers`, `spotlightMs`…) mà server không dùng.
#[derive(Debug, Clone)]
pub struct GiftCatalog {
    pub raw: serde_json::Value,
    view: GiftConfigView,
    /// Alias đã chuẩn hoá sẵn, tránh normalize lại cho mỗi sự kiện.
    named: Vec<(Vec<String>, GiftRule)>,
}

impl GiftCatalog {
    pub fn from_value(raw: serde_json::Value) -> Self {
        let mut view: GiftConfigView = serde_json::from_value(raw.clone()).unwrap_or_default();

        // Mốc kim cương xét từ cao xuống thấp: mốc lớn nhất thoả mãn sẽ thắng.
        view.diamond_bands.sort_by_key(|band| std::cmp::Reverse(band.minimum));

        let named = view
            .by_gift_name
            .iter()
            .map(|entry| {
                let aliases = entry.aliases.iter().map(|a| normalize_text(a)).collect();
                (aliases, entry.rule.clone())
            })
            .collect();

        Self { raw, view, named }
    }

    /// Tra hiệu ứng cho sự kiện gift: Gift ID → tên quà → mốc kim cương.
    pub fn resolve(&self, event: &GameEvent) -> Option<&GiftRule> {
        if let Some(rule) = self.view.by_gift_id.get(&event.gift_id) {
            return Some(rule);
        }

        let gift_name = normalize_text(&event.gift_name);
        if !gift_name.is_empty() {
            if let Some((_, rule)) = self
                .named
                .iter()
                .find(|(aliases, _)| aliases.contains(&gift_name))
            {
                return Some(rule);
            }
        }

        self.view
            .diamond_bands
            .iter()
            .find(|band| event.diamond_count >= band.minimum)
            .map(|band| &band.rule)
    }
}

/// Một quà đã từng thấy trong phiên live, dùng dựng catalog cho bảng điều khiển.
#[derive(Debug, Clone, Default, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct ObservedGift {
    pub gift_id: String,
    pub gift_name: String,
    pub diamond_count: i64,
    pub gift_picture_url: String,
    pub last_seen_at: i64,
    /// Giữ lại khoá lạ để không mất dữ liệu khi ghi đè `observed-gifts.json`.
    #[serde(flatten)]
    pub extra: serde_json::Map<String, serde_json::Value>,
}

/// Gộp thông tin quà mới thấy vào bản ghi cũ.
///
/// Tên quà rỗng hoặc chung chung (`"Gift"`) không được ghi đè tên đã học trước đó —
/// TikTok đôi khi gửi placeholder trước khi gửi tên thật.
pub fn merge_observed_gift(previous: &ObservedGift, event: &GameEvent, now: i64) -> ObservedGift {
    let incoming_name = event.gift_name.trim();
    let gift_name = if !incoming_name.is_empty() && !incoming_name.eq_ignore_ascii_case("gift") {
        incoming_name.to_string()
    } else if previous.gift_name.is_empty() {
        "Gift".to_string()
    } else {
        previous.gift_name.clone()
    };

    let repeats = event.repeat_count.max(1);
    let unit_diamonds = if event.unit_diamond_count > 0 {
        event.unit_diamond_count
    } else {
        // Làm tròn như Math.round bên JS.
        (event.diamond_count as f64 / repeats as f64).round() as i64
    }
    .max(0);

    let gift_id = if !event.gift_id.trim().is_empty() {
        event.gift_id.trim().to_string()
    } else {
        previous.gift_id.clone()
    };

    let gift_picture_url = if !event.gift_picture_url.trim().is_empty() {
        event.gift_picture_url.trim().to_string()
    } else {
        previous.gift_picture_url.clone()
    };

    ObservedGift {
        gift_id,
        gift_name,
        diamond_count: if unit_diamonds > 0 { unit_diamonds } else { previous.diamond_count },
        gift_picture_url,
        last_seen_at: now,
        extra: previous.extra.clone(),
    }
}

/// Quà "thật" mới được học. Loại trừ sự kiện do demo hoặc nút test của bảng Master sinh ra.
pub fn is_real_observed_gift(event: &GameEvent) -> bool {
    event.user_id != "master-test"
        && !event.gift_id.starts_with("demo-")
        && !event.gift_id.starts_with("master-")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn catalog() -> GiftCatalog {
        GiftCatalog::from_value(serde_json::json!({
            "byGiftId": {
                "5655": { "action": "topdj", "durationMs": 12000, "label": "TOP DJ" }
            },
            "byGiftName": [
                { "aliases": ["rose", "hoa hồng"], "action": "camera", "durationMs": 4000, "label": "ZOOM CAMERA" }
            ],
            "diamondBands": [
                { "minimum": 1, "action": "dance", "durationMs": 3000, "label": "CẢM ƠN QUÀ" },
                { "minimum": 500, "action": "fireworks", "durationMs": 12000, "label": "MƯA PHÁO HOA", "fireworkBursts": 4 },
                { "minimum": 50, "action": "medal", "durationMs": 90000, "label": "VUA SÀN NHẢY", "variant": "fire" }
            ],
            "evolutionTiers": [{ "minimum": 10, "tier": 1 }]
        }))
    }

    fn gift(gift_id: &str, gift_name: &str, diamonds: i64) -> GameEvent {
        GameEvent {
            kind: "gift".into(),
            gift_id: gift_id.into(),
            gift_name: gift_name.into(),
            diamond_count: diamonds,
            ..Default::default()
        }
    }

    #[test]
    fn gift_id_takes_priority_over_name_and_diamond_bands() {
        let rule = catalog().resolve(&gift("5655", "Rose", 1000)).unwrap().clone();
        assert_eq!(rule.action, "topdj");
    }

    #[test]
    fn gift_name_aliases_ignore_accents_and_case() {
        let catalog = catalog();
        assert_eq!(catalog.resolve(&gift("", "HOA HỒNG", 1)).unwrap().action, "camera");
        assert_eq!(catalog.resolve(&gift("", "rose", 1)).unwrap().action, "camera");
    }

    #[test]
    fn diamond_bands_pick_the_highest_matching_tier() {
        let catalog = catalog();
        assert_eq!(catalog.resolve(&gift("", "Unknown", 1000)).unwrap().action, "fireworks");
        assert_eq!(catalog.resolve(&gift("", "Unknown", 60)).unwrap().action, "medal");
        assert_eq!(catalog.resolve(&gift("", "Unknown", 5)).unwrap().action, "dance");
        assert!(catalog.resolve(&gift("", "Unknown", 0)).is_none());
    }

    #[test]
    fn unknown_config_keys_survive_for_the_api_passthrough() {
        let catalog = catalog();
        assert!(catalog.raw.get("evolutionTiers").is_some());
    }

    #[test]
    fn observed_gift_keeps_a_real_name_over_a_placeholder() {
        let previous = ObservedGift {
            gift_name: "Rose".into(),
            ..Default::default()
        };
        let merged = merge_observed_gift(&previous, &gift("5655", "Gift", 1), 100);
        assert_eq!(merged.gift_name, "Rose");

        let merged = merge_observed_gift(&previous, &gift("5655", "Galaxy", 1), 100);
        assert_eq!(merged.gift_name, "Galaxy");
    }

    #[test]
    fn observed_gift_derives_unit_price_from_a_streak_total() {
        let mut event = gift("5655", "Rose", 10);
        event.repeat_count = 5;
        let merged = merge_observed_gift(&ObservedGift::default(), &event, 100);
        assert_eq!(merged.diamond_count, 2);
        assert_eq!(merged.last_seen_at, 100);
    }

    #[test]
    fn observed_gift_prefers_an_explicit_unit_price() {
        let mut event = gift("5655", "Rose", 10);
        event.repeat_count = 5;
        event.unit_diamond_count = 7;
        assert_eq!(merge_observed_gift(&ObservedGift::default(), &event, 100).diamond_count, 7);
    }

    #[test]
    fn observed_gift_retains_previous_picture_and_extra_keys() {
        let mut extra = serde_json::Map::new();
        extra.insert("note".into(), serde_json::json!("giu lai"));
        let previous = ObservedGift {
            gift_picture_url: "https://cdn.example/rose.png".into(),
            extra,
            ..Default::default()
        };
        let merged = merge_observed_gift(&previous, &gift("5655", "Rose", 1), 100);
        assert_eq!(merged.gift_picture_url, "https://cdn.example/rose.png");
        assert_eq!(merged.extra.get("note").unwrap(), "giu lai");
    }

    #[test]
    fn demo_and_master_test_gifts_are_not_learned() {
        assert!(!is_real_observed_gift(&gift("demo-100", "Rose", 100)));
        assert!(!is_real_observed_gift(&gift("master-abc", "Rose", 100)));
        let mut event = gift("5655", "Rose", 100);
        event.user_id = "master-test".into();
        assert!(!is_real_observed_gift(&event));
        assert!(is_real_observed_gift(&gift("5655", "Rose", 100)));
    }
}
