//! Chuẩn hoá chuỗi dùng chung cho luật Master và tra cứu gift.
//!
//! Port nguyên semantics của `normalizeText` trong `src/master/rules.js`:
//! NFD → bỏ dấu tổ hợp U+0300..U+036F → trim → lowercase.

use unicode_normalization::UnicodeNormalization;

/// Bỏ dấu tiếng Việt rồi hạ chữ thường, để so khớp alias không phân biệt dấu/hoa thường.
///
/// Lưu ý quan trọng: `đ`/`Đ` (U+0111/U+0110) **không** bị NFD tách ra nên vẫn giữ nguyên,
/// giống hệt bản JS. Vì vậy `"đổi nv"` → `"đoi nv"` chứ không phải `"doi nv"` — đó là lý do
/// `config/game.json` phải liệt kê cả hai biến thể.
pub fn normalize_text(value: &str) -> String {
    value
        .nfd()
        .filter(|c| !matches!(*c, '\u{0300}'..='\u{036f}'))
        .collect::<String>()
        .trim()
        .to_lowercase()
}

/// Tách danh sách trigger ngăn cách bởi dấu phẩy, chuẩn hoá từng phần, tối đa 20 mục.
pub fn split_triggers(value: &str) -> Vec<String> {
    value
        .split(',')
        .map(normalize_text)
        .filter(|s| !s.is_empty())
        .take(20)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn strips_vietnamese_tone_marks_and_lowercases() {
        assert_eq!(normalize_text("HOA HỒNG"), "hoa hong");
        assert_eq!(normalize_text("  Rose  "), "rose");
        assert_eq!(normalize_text("NHẢY"), "nhay");
    }

    #[test]
    fn keeps_d_with_stroke_exactly_like_the_js_version() {
        // `đ` không phải ký tự tổ hợp nên NFD giữ nguyên — bản JS cũng vậy.
        // Các giá trị kỳ vọng dưới đây lấy trực tiếp từ `normalizeText` của bản JS.
        assert_eq!(normalize_text("đổi nv"), "đoi nv");
        assert_eq!(normalize_text("doi nv"), "doi nv");
        assert_ne!(normalize_text("đổi nv"), normalize_text("doi nv"));
        assert_eq!(normalize_text("đi vòng"), "đi vong");
        assert_eq!(normalize_text("Trái tim ngón tay"), "trai tim ngon tay");
    }

    #[test]
    fn splits_and_normalizes_trigger_lists() {
        assert_eq!(split_triggers("Rose, Hoa hồng"), vec!["rose", "hoa hong"]);
        assert_eq!(split_triggers("  ,  , Rose"), vec!["rose"]);
        assert!(split_triggers("").is_empty());
    }

    #[test]
    fn caps_trigger_list_at_twenty_entries() {
        let many = (0..30).map(|i| i.to_string()).collect::<Vec<_>>().join(",");
        assert_eq!(split_triggers(&many).len(), 20);
    }
}
