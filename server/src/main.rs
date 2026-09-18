//! TikTok Live Game Server — điểm khởi động.
//!
//! © 2026 wangnguen — phát hành theo giấy phép MIT.

use std::collections::BTreeMap;
use std::net::SocketAddr;
use std::path::Path;
use std::process::ExitCode;
use std::sync::Arc;
use std::time::Duration;

use tiktok_server::config::{app_root, load_environment_file, Paths, ServerSettings};
use tiktok_server::domain::gifts::{GiftCatalog, ObservedGift};
use tiktok_server::domain::rules::{sanitize_master_config, MasterConfig, RawMasterConfig};
use tiktok_server::session::pipeline::save_observed_gifts;
use tiktok_server::session::state::{AppState, ConnectionStatus, Session, SharedState};
use tiktok_server::transport::http;
use tokio::sync::{broadcast, RwLock};

/// Sức chứa kênh broadcast. Client chậm hơn mức này sẽ bị bỏ bớt message thay vì chặn server.
const BROADCAST_CAPACITY: usize = 1024;
/// Khoảng gom nhóm trước khi ghi `observed-gifts.json`.
const OBSERVED_SAVE_DEBOUNCE_MS: u64 = 250;

#[tokio::main]
async fn main() -> ExitCode {
    let root = app_root();
    if let Err(error) = load_environment_file(&root.join(".env")) {
        eprintln!("Không đọc được file .env: {error}");
        return ExitCode::FAILURE;
    }

    tracing_subscriber::fmt()
        .with_env_filter(
            tracing_subscriber::EnvFilter::try_from_default_env()
                .unwrap_or_else(|_| "tiktok_server=info,warn".into()),
        )
        .with_target(false)
        .init();

    let state = match build_state(&root).await {
        Ok(state) => state,
        Err(error) => {
            eprintln!("\n❌ {error}\n");
            return ExitCode::FAILURE;
        }
    };

    let port = state.settings.port;
    let host = state.settings.host.clone();

    let app = http::router(state.clone());

    let address = format!("{host}:{port}");
    let listener = match tokio::net::TcpListener::bind(&address).await {
        Ok(listener) => listener,
        Err(error) => {
            report_bind_error(&error, port);
            return ExitCode::FAILURE;
        }
    };

    spawn_observed_gift_saver(state.clone());

    println!("TikTok Live Game: http://{host}:{port}");
    println!("Bảng điều khiển: http://{host}:{port}/control.html");

    let serve = axum::serve(
        listener,
        app.into_make_service_with_connect_info::<SocketAddr>(),
    )
    .with_graceful_shutdown(shutdown_signal());

    if let Err(error) = serve.await {
        eprintln!("Lỗi server: {error}");
        return ExitCode::FAILURE;
    }

    // Ghi nốt thư viện gift trước khi thoát.
    if let Err(error) = save_observed_gifts(&state).await {
        eprintln!("Không lưu được thư viện gift lúc thoát: {error}");
    }
    println!("Đã dừng server.");
    ExitCode::SUCCESS
}

async fn build_state(root: &Path) -> Result<SharedState, String> {
    let settings = ServerSettings::from_env();
    let paths = Paths::from_env(root);

    // Mở ra LAN là mở cho cả mạng nội bộ: chỉ cho phép khi vận hành viên chủ động bật.
    if !settings.allow_lan
        && settings.host != "127.0.0.1"
        && settings.host != "::1"
        && !settings.host.eq_ignore_ascii_case("localhost")
    {
        return Err(
            "Từ chối mở ra mạng LAN. Chỉ đặt ALLOW_LAN=1 khi đã có lớp xác thực bên ngoài."
                .to_string(),
        );
    }

    let game_config = read_json(&paths.config_dir.join("game.json")).await?;
    let gifts_config = read_json(&paths.config_dir.join("gifts.json")).await?;

    let master: MasterConfig = match read_json(&paths.master_config()).await {
        Ok(value) => {
            let raw: RawMasterConfig = serde_json::from_value(value)
                .map_err(|error| format!("master.json không hợp lệ: {error}"))?;
            sanitize_master_config(&raw)
        }
        // Chưa có Master là bình thường ở lần chạy đầu.
        Err(_) => MasterConfig::default(),
    };

    let observed_gifts = match read_json(&paths.observed_gifts()).await {
        Ok(value) => {
            let list: Vec<ObservedGift> = serde_json::from_value(value).unwrap_or_default();
            list.into_iter()
                .map(|gift| {
                    let key = if gift.gift_id.is_empty() {
                        gift.gift_name.to_lowercase()
                    } else {
                        gift.gift_id.clone()
                    };
                    (key, gift)
                })
                .filter(|(key, _)| !key.is_empty())
                .collect::<BTreeMap<_, _>>()
        }
        Err(_) => BTreeMap::new(),
    };

    let live_provider = std::env::var("LIVE_PROVIDER")
        .ok()
        .or_else(|| game_config.get("liveProvider").and_then(|v| v.as_str()).map(str::to_string))
        .unwrap_or_else(|| "tikfinity".to_string())
        .to_lowercase();

    let tikfinity_ws_url = std::env::var("TIKFINITY_WS_URL")
        .ok()
        .or_else(|| game_config.get("tikfinityWsUrl").and_then(|v| v.as_str()).map(str::to_string))
        .unwrap_or_else(|| "ws://127.0.0.1:21213/".to_string());

    let max_players = game_config
        .get("maxPlayers")
        .and_then(serde_json::Value::as_u64)
        .unwrap_or(200)
        .clamp(1, 10_000) as usize;
    let player_ttl_ms = game_config
        .get("playerTtlMs")
        .and_then(serde_json::Value::as_i64)
        .unwrap_or(600_000);

    let (broadcast_tx, _rx) = broadcast::channel(BROADCAST_CAPACITY);

    tracing::info!(
        "nguồn live: {live_provider}{}",
        if live_provider == "tikfinity" {
            format!(" ({tikfinity_ws_url})")
        } else {
            " (kết nối thẳng, không cần API key)".to_string()
        }
    );

    Ok(Arc::new(AppState {
        settings,
        paths,
        game_config,
        gift_catalog: GiftCatalog::from_value(gifts_config),
        max_players,
        player_ttl_ms,
        live_provider,
        tikfinity_ws_url,
        log_tiktok_events: std::env::var("LOG_TIKTOK_EVENTS").as_deref() == Ok("1"),
        master: RwLock::new(master),
        observed_gifts: RwLock::new(observed_gifts),
        session: RwLock::new(Session::new()),
        status: RwLock::new(ConnectionStatus::default()),
        broadcast: broadcast_tx,
        observed_dirty: Default::default(),
        demo_task: Default::default(),
        live_task: Default::default(),
    }))
}

async fn read_json(path: &Path) -> Result<serde_json::Value, String> {
    let content = tokio::fs::read_to_string(path)
        .await
        .map_err(|error| format!("Không đọc được {}: {error}", path.display()))?;
    serde_json::from_str(&content)
        .map_err(|error| format!("{} không phải JSON hợp lệ: {error}", path.display()))
}

/// Gom nhóm các lần ghi `observed-gifts.json`: một phiên live đông có thể học
/// hàng chục quà mỗi phút, ghi ngay mỗi lần sẽ quần đĩa vô ích.
fn spawn_observed_gift_saver(state: SharedState) {
    tokio::spawn(async move {
        loop {
            state.observed_dirty.notified().await;
            tokio::time::sleep(Duration::from_millis(OBSERVED_SAVE_DEBOUNCE_MS)).await;
            if let Err(error) = save_observed_gifts(&state).await {
                tracing::error!("không lưu được thư viện gift: {error}");
            }
        }
    });
}

fn report_bind_error(error: &std::io::Error, port: u16) {
    if error.kind() == std::io::ErrorKind::AddrInUse {
        eprintln!("\n❌ Lỗi: Cổng {port} đang bị chương trình khác chiếm.");
        eprintln!("   Có thể bạn đã chạy server trước đó mà chưa tắt.\n");
        eprintln!("   Cách khắc phục:");
        eprintln!("   1. Tắt cửa sổ cmd/terminal cũ đang chạy server");
        eprintln!("   2. Hoặc đổi PORT trong file .env");
        eprintln!("      Bản game dựng sẵn cần PORT=3000.\n");
    } else {
        eprintln!("Lỗi server: {error}");
    }
}

async fn shutdown_signal() {
    let ctrl_c = async {
        tokio::signal::ctrl_c().await.ok();
    };

    #[cfg(unix)]
    let terminate = async {
        use tokio::signal::unix::{signal, SignalKind};
        if let Ok(mut stream) = signal(SignalKind::terminate()) {
            stream.recv().await;
        }
    };
    #[cfg(not(unix))]
    let terminate = std::future::pending::<()>();

    tokio::select! {
        _ = ctrl_c => {}
        _ = terminate => {}
    }
    println!("\nĐang dừng...");
}
