//! Kiểm tra bridge đọc/ghi được đúng các file cấu hình thật trong `config/`.
//!
//! Đây là chốt chặn quan trọng nhất cho việc chạy song song với bản Node: nếu Rust
//! ghi `master.json` hay `observed-gifts.json` khác hình dạng, bản Node sẽ đọc hỏng.

use std::collections::BTreeSet;
use std::path::PathBuf;
use tiktok_server::domain::gifts::{GiftCatalog, ObservedGift};
use tiktok_server::domain::rules::{sanitize_master_config, MasterConfig, RawMasterConfig};

fn config_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("config")
}

fn read(name: &str) -> serde_json::Value {
    let path = config_dir().join(name);
    let content = std::fs::read_to_string(&path)
        .unwrap_or_else(|error| panic!("không đọc được {}: {error}", path.display()));
    serde_json::from_str(&content)
        .unwrap_or_else(|error| panic!("{} không phải JSON hợp lệ: {error}", path.display()))
}

fn keys_of(value: &serde_json::Value) -> BTreeSet<String> {
    value
        .as_object()
        .map(|map| map.keys().cloned().collect())
        .unwrap_or_default()
}

#[test]
fn master_config_round_trips_without_losing_or_renaming_keys() {
    let original = read("master.json");
    let raw: RawMasterConfig = serde_json::from_value(original.clone()).unwrap();
    let sanitized = sanitize_master_config(&raw);
    let written = serde_json::to_value(&sanitized).unwrap();

    assert_eq!(
        keys_of(&original),
        keys_of(&written),
        "khoá cấp cao nhất của master.json bị đổi"
    );

    let original_rules = original["rules"].as_array().unwrap();
    let written_rules = written["rules"].as_array().unwrap();
    assert_eq!(
        original_rules.len(),
        written_rules.len(),
        "số luật thay đổi sau khi sanitize"
    );

    for (index, (before, after)) in original_rules.iter().zip(written_rules).enumerate() {
        assert_eq!(
            keys_of(before),
            keys_of(after),
            "luật #{index} ({}) bị đổi tập khoá",
            before["id"]
        );
        // Các giá trị mà vận hành viên đã cấu hình phải giữ nguyên.
        for field in ["id", "source", "trigger", "giftId", "match", "action", "label", "variant"] {
            assert_eq!(before[field], after[field], "luật #{index}: field {field} bị đổi");
        }
        for field in ["enabled", "displayDiamonds", "durationMs", "fireworkBursts"] {
            assert_eq!(before[field], after[field], "luật #{index}: field {field} bị đổi");
        }
    }
}

#[test]
fn master_config_is_stable_across_a_second_pass() {
    let raw: RawMasterConfig = serde_json::from_value(read("master.json")).unwrap();
    let once = sanitize_master_config(&raw);

    let reparsed: RawMasterConfig = serde_json::from_value(serde_json::to_value(&once).unwrap()).unwrap();
    let twice = sanitize_master_config(&reparsed);

    assert_eq!(once, twice, "sanitize không idempotent — file sẽ trôi mỗi lần lưu");
}

#[test]
fn observed_gifts_round_trip_without_losing_keys() {
    let original = read("observed-gifts.json");
    let gifts: Vec<ObservedGift> = serde_json::from_value(original.clone()).unwrap();
    let written = serde_json::to_value(&gifts).unwrap();

    let original_items = original.as_array().unwrap();
    assert!(!original_items.is_empty(), "observed-gifts.json trống, test vô nghĩa");
    assert_eq!(original_items.len(), gifts.len());

    for (before, after) in original_items.iter().zip(written.as_array().unwrap()) {
        assert_eq!(keys_of(before), keys_of(after), "gift {} bị đổi tập khoá", before["giftId"]);
        assert_eq!(before, after, "gift {} bị đổi giá trị", before["giftId"]);
    }
}

#[test]
fn game_and_gift_config_load_and_resolve() {
    let game = read("game.json");
    assert!(game["maxPlayers"].as_u64().is_some(), "game.json thiếu maxPlayers");
    assert!(game["playerTtlMs"].as_i64().is_some(), "game.json thiếu playerTtlMs");

    let catalog = GiftCatalog::from_value(read("gifts.json"));

    // Mọi mốc kim cương trong file thật phải tra ra được hiệu ứng.
    let bands = catalog.raw["diamondBands"].as_array().unwrap();
    assert!(!bands.is_empty());
    for band in bands {
        let minimum = band["minimum"].as_i64().unwrap();
        let event = tiktok_server::domain::event::GameEvent {
            kind: "gift".into(),
            gift_name: "Khong Co Trong Bang".into(),
            diamond_count: minimum,
            ..Default::default()
        };
        let rule = catalog
            .resolve(&event)
            .unwrap_or_else(|| panic!("không tra được hiệu ứng cho mốc {minimum} kim cương"));
        assert!(!rule.action.is_empty(), "mốc {minimum} trả về hành động rỗng");
    }
}

#[test]
fn every_gift_name_alias_in_the_real_config_resolves() {
    let catalog = GiftCatalog::from_value(read("gifts.json"));

    for entry in catalog.raw["byGiftName"].as_array().unwrap() {
        let expected = entry["action"].as_str().unwrap();
        for alias in entry["aliases"].as_array().unwrap() {
            let alias = alias.as_str().unwrap();
            let event = tiktok_server::domain::event::GameEvent {
                kind: "gift".into(),
                gift_name: alias.to_string(),
                // Kim cương = 0 để chắc chắn kết quả đến từ alias chứ không từ mốc.
                diamond_count: 0,
                ..Default::default()
            };
            let rule = catalog
                .resolve(&event)
                .unwrap_or_else(|| panic!("alias \"{alias}\" không tra được hiệu ứng"));
            assert_eq!(rule.action, expected, "alias \"{alias}\" ra sai hành động");
        }
    }
}

#[test]
fn master_rules_in_the_real_config_all_use_supported_actions() {
    let raw: RawMasterConfig = serde_json::from_value(read("master.json")).unwrap();
    let config: MasterConfig = sanitize_master_config(&raw);

    for rule in &config.rules {
        // Sanitize đổi action lạ thành "dance"; nếu file gốc khác thì là cấu hình sai.
        let original = read("master.json");
        let before = original["rules"]
            .as_array()
            .unwrap()
            .iter()
            .find(|r| r["id"] == rule.id.as_str())
            .unwrap();
        assert_eq!(
            before["action"].as_str().unwrap(),
            rule.action,
            "luật {} dùng action không được hỗ trợ",
            rule.id
        );
    }
}
