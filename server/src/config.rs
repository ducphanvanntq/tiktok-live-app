//! Đọc `.env` và thiết lập server — port của `src/config/environment.js`.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

/// Cổng mặc định. Chọn 8085 để tránh đụng các cổng dev phổ biến (3000, 5173, 8080).
/// Đổi ở đây thì phải đổi cả `serverUrl` trong `UnityProject/Assets/Scripts/TikTokWebSocketClient.cs`.
const DEFAULT_PORT: u16 = 8085;
const DEFAULT_HOST: &str = "127.0.0.1";

/// Phân tích nội dung `.env`: bỏ qua dòng trống/chú thích, chấp nhận tiền tố `export`,
/// bóc một lớp nháy đơn hoặc kép, và giữ nguyên dấu `=` trong phần giá trị.
pub fn parse_environment(content: &str) -> BTreeMap<String, String> {
    let mut parsed = BTreeMap::new();

    for source_line in content.lines() {
        let mut line = source_line.trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if let Some(rest) = line.strip_prefix("export ") {
            line = rest.trim();
        }

        let Some(separator) = line.find('=') else { continue };
        if separator == 0 {
            continue;
        }
        let key = line[..separator].trim();
        if !is_valid_key(key) {
            continue;
        }

        let mut value = line[separator + 1..].trim();
        if value.len() >= 2 {
            let bytes = value.as_bytes();
            let first = bytes[0];
            let last = bytes[value.len() - 1];
            if (first == b'"' && last == b'"') || (first == b'\'' && last == b'\'') {
                value = &value[1..value.len() - 1];
            }
        }
        parsed.insert(key.to_string(), value.to_string());
    }

    parsed
}

/// Khoá hợp lệ: `[A-Za-z_][A-Za-z0-9_]*`.
fn is_valid_key(key: &str) -> bool {
    let mut chars = key.chars();
    match chars.next() {
        Some(c) if c.is_ascii_alphabetic() || c == '_' => {}
        _ => return false,
    }
    chars.all(|c| c.is_ascii_alphanumeric() || c == '_')
}

/// Nạp `.env` vào process environment. Biến đã có sẵn từ hệ điều hành **không** bị ghi đè.
///
/// Trả về `false` nếu không có file (không phải lỗi), `Err` nếu đọc file thất bại vì lý do khác.
pub fn load_environment_file(path: &Path) -> std::io::Result<bool> {
    let content = match std::fs::read_to_string(path) {
        Ok(content) => content,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(error) => return Err(error),
    };

    for (key, value) in parse_environment(&content) {
        if std::env::var_os(&key).is_none() {
            // SAFETY: chạy một luồng duy nhất lúc khởi động, trước khi spawn tokio runtime.
            unsafe { std::env::set_var(&key, &value) };
        }
    }
    Ok(true)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServerSettings {
    pub port: u16,
    pub host: String,
    pub allow_lan: bool,
}

impl ServerSettings {
    /// Đọc cấu hình từ một bảng tra cứu bất kỳ (để test không cần đụng process env).
    pub fn from_lookup(lookup: impl Fn(&str) -> Option<String>) -> Self {
        let port = lookup("PORT")
            .and_then(|value| value.trim().parse::<u32>().ok())
            .filter(|port| (1..=65535).contains(port))
            .map(|port| port as u16)
            .unwrap_or(DEFAULT_PORT);

        let host = lookup("HOST")
            .map(|value| value.trim().to_string())
            .filter(|value| !value.is_empty())
            .unwrap_or_else(|| DEFAULT_HOST.to_string());

        let allow_lan = lookup("ALLOW_LAN").as_deref() == Some("1");

        Self { port, host, allow_lan }
    }

    pub fn from_env() -> Self {
        Self::from_lookup(|key| std::env::var(key).ok())
    }
}

/// Thư mục dữ liệu. Mặc định `./config`, `./public`, `./assets` cạnh nơi chạy binary,
/// ghi đè được bằng `CONFIG_DIR` / `PUBLIC_DIR` / `ASSETS_DIR` trong `.env`.
#[derive(Debug, Clone)]
pub struct Paths {
    pub config_dir: PathBuf,
    pub public_dir: PathBuf,
    pub assets_dir: PathBuf,
}

impl Paths {
    pub fn from_env(base: &Path) -> Self {
        let resolve = |key: &str, default: &str| -> PathBuf {
            match std::env::var(key) {
                Ok(value) if !value.trim().is_empty() => {
                    let candidate = PathBuf::from(value.trim());
                    if candidate.is_absolute() {
                        candidate
                    } else {
                        base.join(candidate)
                    }
                }
                _ => base.join(default),
            }
        };

        Self {
            config_dir: resolve("CONFIG_DIR", "config"),
            public_dir: resolve("PUBLIC_DIR", "public"),
            assets_dir: resolve("ASSETS_DIR", "assets"),
        }
    }

    pub fn master_config(&self) -> PathBuf {
        self.config_dir.join("master.json")
    }

    pub fn observed_gifts(&self) -> PathBuf {
        self.config_dir.join("observed-gifts.json")
    }

    pub fn gifs_dir(&self) -> PathBuf {
        self.assets_dir.join("gifs")
    }
}

/// Thư mục gốc của ứng dụng: cạnh file binary khi đã build, hoặc thư mục hiện tại khi `cargo run`.
pub fn app_root() -> PathBuf {
    if let Ok(value) = std::env::var("APP_ROOT") {
        if !value.trim().is_empty() {
            return PathBuf::from(value.trim());
        }
    }
    std::env::current_dir().unwrap_or_else(|_| PathBuf::from("."))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lookup_from<'a>(pairs: &'a [(&'a str, &'a str)]) -> impl Fn(&str) -> Option<String> + 'a {
        move |key| {
            pairs
                .iter()
                .find(|(k, _)| *k == key)
                .map(|(_, v)| v.to_string())
        }
    }

    #[test]
    fn parses_comments_quotes_and_values_containing_equals_signs() {
        let parsed = parse_environment(
            "# comment\nPORT=3100\nHOST=\"127.0.0.1\"\nLIVE_PROVIDER='tikfinity'\nTOKEN=value=with=equals",
        );
        assert_eq!(parsed.get("PORT").map(String::as_str), Some("3100"));
        assert_eq!(parsed.get("HOST").map(String::as_str), Some("127.0.0.1"));
        assert_eq!(parsed.get("LIVE_PROVIDER").map(String::as_str), Some("tikfinity"));
        assert_eq!(parsed.get("TOKEN").map(String::as_str), Some("value=with=equals"));
        assert_eq!(parsed.len(), 4);
    }

    #[test]
    fn accepts_export_prefix_and_rejects_invalid_keys() {
        let parsed = parse_environment("export PORT=3100\n1BAD=x\n=novalue\nOK_KEY=1");
        assert_eq!(parsed.get("PORT").map(String::as_str), Some("3100"));
        assert_eq!(parsed.get("OK_KEY").map(String::as_str), Some("1"));
        assert!(!parsed.contains_key("1BAD"));
        assert_eq!(parsed.len(), 2);
    }

    #[test]
    fn uses_safe_defaults_when_port_is_invalid() {
        let settings = ServerSettings::from_lookup(lookup_from(&[("PORT", "not-a-port")]));
        assert_eq!(
            settings,
            ServerSettings {
                port: 8085,
                host: "127.0.0.1".to_string(),
                allow_lan: false
            }
        );
        assert_eq!(ServerSettings::from_lookup(lookup_from(&[("PORT", "3100")])).port, 3100);
        assert_eq!(ServerSettings::from_lookup(lookup_from(&[("PORT", "70000")])).port, 8085);
        assert_eq!(ServerSettings::from_lookup(lookup_from(&[("PORT", "0")])).port, 8085);
    }

    #[test]
    fn allow_lan_requires_the_exact_string_one() {
        assert!(ServerSettings::from_lookup(lookup_from(&[("ALLOW_LAN", "1")])).allow_lan);
        assert!(!ServerSettings::from_lookup(lookup_from(&[("ALLOW_LAN", "true")])).allow_lan);
        assert!(!ServerSettings::from_lookup(lookup_from(&[])).allow_lan);
    }

    #[test]
    fn loads_env_file_without_overwriting_existing_variables() {
        let dir = std::env::temp_dir().join(format!("ongchu-env-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let env_path = dir.join(".env");
        std::fs::write(&env_path, "ONGCHU_TEST_HOST=0.0.0.0\nONGCHU_TEST_PORT=3100\n").unwrap();

        // SAFETY: test đơn luồng, biến chỉ dùng trong test này.
        unsafe { std::env::set_var("ONGCHU_TEST_PORT", "3200") };
        assert!(load_environment_file(&env_path).unwrap());
        assert_eq!(std::env::var("ONGCHU_TEST_PORT").unwrap(), "3200");
        assert_eq!(std::env::var("ONGCHU_TEST_HOST").unwrap(), "0.0.0.0");

        unsafe {
            std::env::remove_var("ONGCHU_TEST_PORT");
            std::env::remove_var("ONGCHU_TEST_HOST");
        }
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn missing_env_file_is_not_an_error() {
        let missing = std::env::temp_dir().join("ongchu-definitely-missing-.env");
        assert!(!load_environment_file(&missing).unwrap());
    }
}
