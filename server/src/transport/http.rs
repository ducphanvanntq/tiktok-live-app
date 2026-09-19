//! Router HTTP — port phần Express của `server.js`.

use crate::transport::security::is_allowed_host;
use crate::session::state::SharedState;
use axum::{
    extract::{Request, State},
    handler::HandlerWithoutStateExt,
    http::{header, HeaderValue, StatusCode},
    middleware::{self, Next},
    response::{IntoResponse, Response},
    routing::{any, get},
    Json, Router,
};
use serde_json::json;
use tower_http::services::ServeDir;

pub fn router(state: SharedState) -> Router {
    let public_dir = state.paths.public_dir.clone();
    let assets_dir = state.paths.assets_dir.clone();

    Router::new()
        // `/` vừa là trang chủ (chuyển tới control.html) vừa là điểm nối WebSocket.
        .route("/", any(crate::transport::ws::root))
        .route("/ws", any(crate::transport::ws::upgrade))
        .route("/api/health", get(health))
        .route("/api/config", get(config))
        .route("/api/gifs", get(gifs))
        .nest_service("/assets", ServeDir::new(assets_dir))
        .fallback_service(ServeDir::new(public_dir).fallback(not_found.into_service()))
        .layer(middleware::from_fn_with_state(state.clone(), guard))
        .with_state(state)
}

/// Chặn Host lạ, chặn dotfile, và gắn header bảo mật cho mọi phản hồi.
async fn guard(State(state): State<SharedState>, request: Request, next: Next) -> Response {
    let host = request
        .headers()
        .get(header::HOST)
        .and_then(|value| value.to_str().ok())
        .unwrap_or("");

    if !is_allowed_host(host, state.settings.port, state.settings.allow_lan) {
        return (StatusCode::FORBIDDEN, "Forbidden").into_response();
    }

    let path = request.uri().path().to_string();
    // Không phục vụ file ẩn (`.env`, `.git`…), kể cả khi nằm trong public/ hay assets/.
    if path.split('/').any(|segment| segment.starts_with('.') && segment.len() > 1) {
        return (StatusCode::FORBIDDEN, "Forbidden").into_response();
    }

    let mut response = next.run(request).await;
    let headers = response.headers_mut();

    headers.insert("X-Content-Type-Options", HeaderValue::from_static("nosniff"));
    headers.insert("X-Frame-Options", HeaderValue::from_static("DENY"));
    headers.insert("Referrer-Policy", HeaderValue::from_static("no-referrer"));
    headers.insert(
        "Permissions-Policy",
        HeaderValue::from_static("camera=(), microphone=(), geolocation=(), payment=()"),
    );
    headers.insert(
        "Cross-Origin-Resource-Policy",
        HeaderValue::from_static("same-origin"),
    );

    let port = state.settings.port;
    let csp = format!(
        "default-src 'self'; base-uri 'none'; object-src 'none'; frame-ancestors 'none'; \
         form-action 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; \
         img-src 'self' data: blob: https:; media-src 'self' blob:; \
         connect-src 'self' ws://127.0.0.1:{port} ws://localhost:{port}"
    );
    if let Ok(value) = HeaderValue::from_str(&csp) {
        headers.insert("Content-Security-Policy", value);
    }

    // API và trang HTML không được cache — cấu hình đổi theo phiên live.
    if path.starts_with("/api/") || path.ends_with(".html") {
        headers.insert(header::CACHE_CONTROL, HeaderValue::from_static("no-store"));
    }

    response
}

async fn health() -> impl IntoResponse {
    Json(json!({
        "status": "ok",
        "appId": "wangnguen-brigde",
        "version": env!("CARGO_PKG_VERSION"),
    }))
}

async fn config(State(state): State<SharedState>) -> impl IntoResponse {
    let master = state.master.read().await.clone();
    let observed: Vec<_> = state.observed_gifts.read().await.values().cloned().collect();
    Json(json!({
        "game": state.game_config,
        "gifts": state.gift_catalog.raw,
        "master": master,
        "observedGifts": observed,
    }))
}

/// Danh sách file `.gif` trong `assets/gifs`, để bảng điều khiển chọn hiệu ứng.
async fn gifs(State(state): State<SharedState>) -> Response {
    let dir = state.paths.gifs_dir();
    let mut entries = match tokio::fs::read_dir(&dir).await {
        Ok(entries) => entries,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return Json(Vec::<String>::new()).into_response()
        }
        Err(error) => {
            tracing::error!("không đọc được thư mục GIF {}: {error}", dir.display());
            return (
                StatusCode::INTERNAL_SERVER_ERROR,
                Json(json!({ "error": "Không thể đọc danh sách GIF" })),
            )
                .into_response();
        }
    };

    let mut files = Vec::new();
    while let Ok(Some(entry)) = entries.next_entry().await {
        let name = entry.file_name().to_string_lossy().to_string();
        if name.to_lowercase().ends_with(".gif") {
            files.push(name);
        }
    }
    files.sort();
    Json(files).into_response()
}

async fn not_found() -> impl IntoResponse {
    (StatusCode::NOT_FOUND, "Not found")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::config::{Paths, ServerSettings};
    use crate::domain::gifts::GiftCatalog;
    use crate::domain::rules::MasterConfig;
    use crate::session::state::{AppState, ConnectionStatus, Session};
    use axum::body::Body;
    use axum::http::Request as HttpRequest;
    use std::collections::BTreeMap;
    use std::path::PathBuf;
    use std::sync::Arc;
    use tokio::sync::{broadcast, RwLock};
    use tower::ServiceExt;

    fn test_state(dir: PathBuf) -> SharedState {
        let (tx, _rx) = broadcast::channel(64);
        Arc::new(AppState {
            settings: ServerSettings {
                port: 3000,
                host: "127.0.0.1".into(),
                allow_lan: false,
            },
            paths: Paths {
                config_dir: dir.join("config"),
                public_dir: dir.join("public"),
                assets_dir: dir.join("assets"),
            },
            game_config: json!({ "maxPlayers": 400, "custom": "giu-nguyen" }),
            gift_catalog: GiftCatalog::from_value(json!({ "diamondBands": [] })),
            max_players: 400,
            player_ttl_ms: 600_000,
            live_provider: "tikfinity".into(),
            tikfinity_ws_url: "ws://127.0.0.1:21213/".into(),
            log_tiktok_events: false,
            master: RwLock::new(MasterConfig::default()),
            display: RwLock::new(Default::default()),
            observed_gifts: RwLock::new(BTreeMap::new()),
            session: RwLock::new(Session::new()),
            status: RwLock::new(ConnectionStatus::default()),
            broadcast: tx,
            observed_dirty: Default::default(),
            demo_task: Default::default(),
            live_task: Default::default(),
        })
    }

    fn get_request(path: &str, host: &str) -> HttpRequest<Body> {
        let mut request = HttpRequest::builder()
            .uri(path)
            .header("host", host)
            .body(Body::empty())
            .unwrap();
        // Ngoài đời `into_make_service_with_connect_info` gắn sẵn; trong test phải tự thêm.
        request.extensions_mut().insert(axum::extract::ConnectInfo(
            "127.0.0.1:54321".parse::<std::net::SocketAddr>().unwrap(),
        ));
        request
    }

    async fn body_string(response: Response) -> String {
        let bytes = axum::body::to_bytes(response.into_body(), 1 << 20).await.unwrap();
        String::from_utf8(bytes.to_vec()).unwrap()
    }

    #[tokio::test]
    async fn health_reports_the_app_identity() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app
            .oneshot(get_request("/api/health", "127.0.0.1:3000"))
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        let body = body_string(response).await;
        assert!(body.contains("\"appId\":\"wangnguen-brigde\""));
        assert!(body.contains("\"status\":\"ok\""));
    }

    #[tokio::test]
    async fn requests_with_a_foreign_host_header_are_refused() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app
            .oneshot(get_request("/api/health", "evil.example:3000"))
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }

    #[tokio::test]
    async fn dotfiles_are_never_served() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app
            .oneshot(get_request("/.env", "127.0.0.1:3000"))
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::FORBIDDEN);
    }

    #[tokio::test]
    async fn security_headers_and_no_store_are_applied_to_api_routes() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app
            .oneshot(get_request("/api/health", "127.0.0.1:3000"))
            .await
            .unwrap();

        let headers = response.headers();
        assert_eq!(headers["X-Content-Type-Options"], "nosniff");
        assert_eq!(headers["X-Frame-Options"], "DENY");
        assert_eq!(headers["Referrer-Policy"], "no-referrer");
        assert_eq!(headers["Cache-Control"], "no-store");
        let csp = headers["Content-Security-Policy"].to_str().unwrap();
        assert!(csp.contains("frame-ancestors 'none'"));
        // WebSocket của chính máy phải nằm trong connect-src, nếu không control.html bị chặn.
        assert!(csp.contains("ws://127.0.0.1:3000"));
        assert!(csp.contains("ws://localhost:3000"));
    }

    #[tokio::test]
    async fn config_passes_game_and_gift_json_through_untouched() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app
            .oneshot(get_request("/api/config", "127.0.0.1:3000"))
            .await
            .unwrap();
        let body = body_string(response).await;
        // Khoá server không dùng vẫn phải tới được client.
        assert!(body.contains("giu-nguyen"));
        assert!(body.contains("\"master\""));
        assert!(body.contains("\"observedGifts\""));
    }

    #[tokio::test]
    async fn missing_gifs_directory_returns_an_empty_list() {
        let state = test_state(std::env::temp_dir().join("ongchu-khong-ton-tai"));
        let app = router(state);
        let response = app
            .oneshot(get_request("/api/gifs", "127.0.0.1:3000"))
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        assert_eq!(body_string(response).await, "[]");
    }

    #[tokio::test]
    async fn root_redirects_to_the_control_panel() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app.oneshot(get_request("/", "127.0.0.1:3000")).await.unwrap();
        assert_eq!(response.status(), StatusCode::SEE_OTHER);
        assert_eq!(response.headers()["location"], "/control.html");
    }

    #[tokio::test]
    async fn unknown_paths_return_a_plain_text_404() {
        let app = router(test_state(std::env::temp_dir()));
        let response = app
            .oneshot(get_request("/khong-co-trang-nay", "127.0.0.1:3000"))
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::NOT_FOUND);
        assert_eq!(body_string(response).await, "Not found");
    }
}
