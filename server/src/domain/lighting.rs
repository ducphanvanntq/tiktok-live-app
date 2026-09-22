use serde::{Deserialize, Serialize};
use serde_json::Value;

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq)]
#[serde(default, rename_all = "camelCase")]
pub struct LightingConfig {
    pub floor_enabled: bool,
    pub background_enabled: bool,
    pub lights_enabled: bool,
    pub floor_brightness: f32,
    pub background_brightness: f32,
    pub lights_brightness: f32,
    pub beam_width: f32,
    pub floor_palette: u8,
    pub background_palette: u8,
    pub lights_palette: u8,
    pub floor_pattern: u8,
}

impl Default for LightingConfig {
    fn default() -> Self {
        Self { floor_enabled: true, background_enabled: true, lights_enabled: true,
            floor_brightness: 1.5, background_brightness: 1.25, lights_brightness: 1.5,
            beam_width: 2.5, floor_palette: 3, background_palette: 1, lights_palette: 3, floor_pattern: 0 }
    }
}

impl LightingConfig {
    pub fn patched(self, patch: &Value) -> Result<Self, &'static str> {
        let fields = patch.as_object().filter(|p| !p.is_empty()).ok_or("Cấu hình ánh sáng không hợp lệ.")?;
        let mut next = self;
        for (key, value) in fields {
            match key.as_str() {
                "floorEnabled" | "backgroundEnabled" | "lightsEnabled" => {
                    let enabled = value.as_bool().ok_or("Công tắc phải là true hoặc false.")?;
                    match key.as_str() {
                        "floorEnabled" => next.floor_enabled = enabled,
                        "backgroundEnabled" => next.background_enabled = enabled,
                        _ => next.lights_enabled = enabled,
                    }
                }
                "floorBrightness" | "backgroundBrightness" | "lightsBrightness" | "beamWidth" => {
                    let number = value.as_f64().ok_or("Độ sáng và độ rộng phải là số.")?;
                    let (min, max) = if key == "beamWidth" { (0.5, 4.0) } else { (0.0, 2.0) };
                    if !number.is_finite() || !(min..=max).contains(&number) { return Err("Giá trị ánh sáng ngoài phạm vi."); }
                    match key.as_str() {
                        "floorBrightness" => next.floor_brightness = number as f32,
                        "backgroundBrightness" => next.background_brightness = number as f32,
                        "lightsBrightness" => next.lights_brightness = number as f32,
                        _ => next.beam_width = number as f32,
                    }
                }
                "floorPalette" | "backgroundPalette" | "lightsPalette" | "floorPattern" => {
                    let number = value.as_u64().ok_or("Mã phối màu không hợp lệ.")?;
                    let max = if key == "floorPattern" { 2 } else if key == "backgroundPalette" { 6 } else { 5 };
                    if number > max { return Err("Không tìm thấy phối màu hoặc hiệu ứng."); }
                    match key.as_str() {
                        "floorPalette" => next.floor_palette = number as u8,
                        "backgroundPalette" => next.background_palette = number as u8,
                        "lightsPalette" => next.lights_palette = number as u8,
                        _ => next.floor_pattern = number as u8,
                    }
                }
                _ => return Err("Không tìm thấy tùy chỉnh ánh sáng."),
            }
        }
        Ok(next)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;
    #[test]
    fn preserves_other_fields_and_validates_ranges() {
        let initial = LightingConfig::default();
        let changed = initial.patched(&json!({"floorEnabled":false,"beamWidth":4,"floorPalette":5})).unwrap();
        assert!(!changed.floor_enabled);
        assert_eq!(changed.beam_width, 4.0);
        assert_eq!(changed.background_brightness, initial.background_brightness);
        assert_eq!(serde_json::from_value::<LightingConfig>(serde_json::to_value(changed).unwrap()).unwrap(), changed);
        for invalid in [json!({}),json!({"floorBrightness":-1}),json!({"lightsBrightness":2.1}),json!({"beamWidth":0.4}),
            json!({"floorPalette":6}),json!({"floorPalette":1.5}),json!({"floorPattern":3}),json!({"lightsEnabled":1}),
            json!({"unknown":true}),json!({"beamWidth":"2"})] {
            assert!(initial.patched(&invalid).is_err(), "{invalid}");
        }
    }
}
