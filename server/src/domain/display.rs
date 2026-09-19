use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct DisplayConfig {
    pub show_top: bool,
    pub show_welcome: bool,
    pub show_chat: bool,
    pub show_feed: bool,
    pub show_gift_effects: bool,
    pub focus_npc: bool,
    pub focus_chat: bool,
}

impl Default for DisplayConfig {
    fn default() -> Self {
        Self { show_top: true, show_welcome: true, show_chat: true,
            show_feed: true, show_gift_effects: true, focus_npc: true, focus_chat: true }
    }
}

impl DisplayConfig {
    // Patch only changed switches so two control panels do not overwrite each other.
    pub fn patched(self, patch: &Value) -> Result<Self, &'static str> {
        let fields = patch.as_object().filter(|fields| !fields.is_empty())
            .ok_or("Cấu hình hiển thị không hợp lệ.")?;
        let mut next = self;
        for (key, value) in fields {
            let enabled = value.as_bool().ok_or("Công tắc phải là true hoặc false.")?;
            match key.as_str() {
                "showTop" => next.show_top = enabled,
                "showWelcome" => next.show_welcome = enabled,
                "showChat" => next.show_chat = enabled,
                "showFeed" => next.show_feed = enabled,
                "showGiftEffects" => next.show_gift_effects = enabled,
                "focusNpc" => next.focus_npc = enabled,
                "focusChat" => next.focus_chat = enabled,
                _ => return Err("Không tìm thấy công tắc hiển thị."),
            }
        }
        Ok(next)
    }
}

#[derive(Serialize)]
pub struct DisplayConfigMessage {
    #[serde(rename = "type")]
    pub kind: &'static str,
    pub display: DisplayConfig,
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn defaults_and_partial_saved_config_keep_unset_switches_enabled() {
        let config: DisplayConfig = serde_json::from_value(json!({"showTop": false})).unwrap();
        assert_eq!(config, DisplayConfig { show_top: false, ..Default::default() });
        assert_eq!(serde_json::from_value::<DisplayConfig>(json!({})).unwrap(), DisplayConfig::default());
        let upgraded: DisplayConfig = serde_json::from_value(json!({"showTop": false, "showWelcome": true, "showChat": false})).unwrap();
        assert!(upgraded.show_feed && upgraded.show_gift_effects && upgraded.focus_npc && upgraded.focus_chat);
        let mut disabled = DisplayConfig::default();
        for key in ["showTop", "showWelcome", "showChat", "showFeed", "showGiftEffects", "focusNpc", "focusChat"] {
            disabled = disabled.patched(&json!({(key): false})).unwrap();
        }
        assert!(serde_json::to_value(disabled).unwrap().as_object().unwrap().values().all(|value| value == false));
    }

    #[test]
    fn partial_updates_preserve_other_switches_and_round_trip() {
        let config = DisplayConfig::default().patched(&json!({"showChat": false})).unwrap()
            .patched(&json!({"showTop": false})).unwrap();
        assert!(config.show_welcome);
        assert!(!config.show_chat && !config.show_top);
        assert_eq!(serde_json::from_str::<DisplayConfig>(&serde_json::to_string(&config).unwrap()).unwrap(), config);
    }

    #[test]
    fn malformed_updates_are_rejected() {
        for value in [json!(null), json!([]), json!({}), json!({"showTop": "false"}), json!({"showChat": null}), json!({"unknown": true})] {
            assert!(DisplayConfig::default().patched(&value).is_err());
        }
    }
}
