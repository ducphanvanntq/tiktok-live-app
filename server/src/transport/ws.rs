//! Hub WebSocket: xác thực kết nối, phân quyền client, và điều phối lệnh.

use crate::live::demo;
use crate::domain::event::IncomingEvent;
use crate::domain::display::DisplayConfigMessage;
use crate::session::pipeline::{process_game_event, save_master_config, snapshot_message, viewer_guide_message, MasterConfigMessage};
use crate::live;
use crate::domain::rules::{sanitize_master_config, RawMasterConfig};
use crate::transport::security::{
    is_allowed_host, is_allowed_origin, is_loopback_address, RateLimit, RATE_LIMIT_MAX,
    RATE_LIMIT_WINDOW_MS,
};
use crate::session::state::{now_ms, SharedState, StatusMessage};
use axum::{
    extract::{
        ws::{Message, WebSocket},
        ConnectInfo, FromRequestParts, State, WebSocketUpgrade,
    },
    http::{header, HeaderMap, StatusCode},
    response::{IntoResponse, Response},
};
use futures_util::{SinkExt, StreamExt};
use serde::Serialize;
use serde_json::Value;
use std::net::SocketAddr;

/// Số lệnh sai định dạng liên tiếp trước khi đóng kết nối.
const MAX_INVALID_MESSAGES: u32 = 3;
/// Nhịp ping giữ kết nối.
const HEARTBEAT_SECS: u64 = 30;

/// Vai trò client. Chỉ `control` được sửa Master; lệnh vận hành thì `control`
/// hoặc client gốc (Unity) đều được.
#[derive(Debug, Clone, Copy, PartialEq)]
enum Role {
    Control,
    Overlay,
}

struct Client {
    role: Option<Role>,
    /// Kết nối không có header `Origin` từ loopback — tức ứng dụng gốc (Unity
    /// dùng `ClientWebSocket`, không gửi Origin), không phải trang web.
    native: bool,
    rate_limit: RateLimit,
    invalid_messages: u32,
}

/// Xử lý `/`: bảng điều khiển mở WebSocket tới gốc site, còn trình duyệt truy cập
/// bình thường thì phải được chuyển tới trang control. Phân biệt bằng header upgrade.
pub async fn root(
    state: State<SharedState>,
    remote: ConnectInfo<SocketAddr>,
    request: axum::extract::Request,
) -> Response {
    let (mut parts, _body) = request.into_parts();
    let headers = parts.headers.clone();
    match WebSocketUpgrade::from_request_parts(&mut parts, &()).await {
        Ok(ws) => upgrade(state, remote, headers, ws).await,
        Err(_) => axum::response::Redirect::to("/control.html").into_response(),
    }
}

/// Kiểm tra Host/Origin/địa chỉ trước khi nâng cấp lên WebSocket.
pub async fn upgrade(
    State(state): State<SharedState>,
    ConnectInfo(remote): ConnectInfo<SocketAddr>,
    headers: HeaderMap,
    ws: WebSocketUpgrade,
) -> Response {
    let header_value = |name| {
        headers
            .get(name)
            .and_then(|value| value.to_str().ok())
            .unwrap_or("")
    };
    let origin = header_value(header::ORIGIN);
    let host = header_value(header::HOST);
    let remote_ip = remote.ip().to_string();

    let allow_lan = state.settings.allow_lan;
    if (!allow_lan && !is_loopback_address(&remote_ip))
        || !is_allowed_host(host, state.settings.port, allow_lan)
        || !is_allowed_origin(origin, state.settings.port)
    {
        return (StatusCode::FORBIDDEN, "Forbidden").into_response();
    }

    let native = origin.is_empty() && is_loopback_address(&remote_ip);
    ws.on_upgrade(move |socket| handle(socket, state, native))
}

async fn handle(socket: WebSocket, state: SharedState, native: bool) {
    let (mut sender, mut receiver) = socket.split();
    let mut broadcast_rx = state.broadcast.subscribe();

    // Gửi trạng thái ngay khi mở, trước cả khi client đăng ký vai trò.
    {
        let status = state.status.read().await.clone();
        if send_json(&mut sender, &StatusMessage { kind: "status", status })
            .await
            .is_err()
        {
            return;
        }
    }

    let mut client = Client {
        role: None,
        native,
        rate_limit: RateLimit::default(),
        invalid_messages: 0,
    };
    let mut heartbeat = tokio::time::interval(std::time::Duration::from_secs(HEARTBEAT_SECS));
    heartbeat.tick().await; // bỏ nhịp đầu (kích hoạt ngay lập tức)

    loop {
        tokio::select! {
            // Message phát cho mọi client.
            payload = broadcast_rx.recv() => {
                match payload {
                    Ok(payload) => {
                        if sender.send(Message::Text(payload.into())).await.is_err() {
                            break;
                        }
                    }
                    // Client đọc quá chậm: bỏ qua phần trễ thay vì đóng kết nối.
                    Err(tokio::sync::broadcast::error::RecvError::Lagged(skipped)) => {
                        tracing::warn!("client chậm, bỏ qua {skipped} message");
                    }
                    Err(tokio::sync::broadcast::error::RecvError::Closed) => break,
                }
            }

            _ = heartbeat.tick() => {
                if sender.send(Message::Ping(Vec::new().into())).await.is_err() {
                    break;
                }
            }

            incoming = receiver.next() => {
                let Some(Ok(message)) = incoming else { break };
                let text = match message {
                    Message::Text(text) => text,
                    Message::Binary(_) => break, // chỉ nhận text
                    Message::Close(_) => break,
                    _ => continue, // Ping/Pong do tầng dưới xử lý
                };

                if !client.rate_limit.consume(now_ms(), RATE_LIMIT_MAX, RATE_LIMIT_WINDOW_MS) {
                    tracing::warn!("client vượt hạn mức lệnh, đóng kết nối");
                    break;
                }

                let parsed: Option<Value> = serde_json::from_str(&text).ok();
                let Some(value) = parsed.filter(|v| v.is_object()) else {
                    client.invalid_messages += 1;
                    if client.invalid_messages >= MAX_INVALID_MESSAGES {
                        break;
                    }
                    let _ = send_json(&mut sender, &error_message("Dữ liệu không hợp lệ")).await;
                    continue;
                };

                let kind = value.get("type").and_then(Value::as_str).unwrap_or("");
                if kind.is_empty() || kind.len() > 40 {
                    let _ = send_json(&mut sender, &error_message("Cấu trúc lệnh không hợp lệ.")).await;
                    continue;
                }

                if dispatch(&state, &mut client, &mut sender, kind, &value).await.is_err() {
                    break;
                }
            }
        }
    }
}

type Sender = futures_util::stream::SplitSink<WebSocket, Message>;

async fn send_json<T: Serialize>(sender: &mut Sender, message: &T) -> Result<(), ()> {
    let payload = serde_json::to_string(message).map_err(|error| {
        tracing::error!("không serialize được message: {error}");
    })?;
    sender.send(Message::Text(payload.into())).await.map_err(|_| ())
}

/// Xử lý một lệnh từ client. `Err` nghĩa là phải đóng kết nối.
async fn dispatch(
    state: &SharedState,
    client: &mut Client,
    sender: &mut Sender,
    kind: &str,
    message: &Value,
) -> Result<(), ()> {
    if kind == "ping" {
        return send_json(sender, &PongMessage { kind: "pong", timestamp: now_ms() }).await;
    }

    if kind == "register" {
        if client.role.is_some() {
            return send_json(sender, &error_message("Client đã đăng ký quyền.")).await;
        }
        let role = match message.get("role").and_then(Value::as_str) {
            Some("control") => Role::Control,
            Some("overlay") => Role::Overlay,
            _ => return send_json(sender, &error_message("Vai trò client không hợp lệ.")).await,
        };
        client.role = Some(role);

        // Deliver visibility before the roster so reconnects do not replay hidden UI.
        let display = *state.display.read().await;
        send_json(sender, &DisplayConfigMessage { kind: "display_config", display }).await?;

        send_json(sender, &ConfigMessage {
            kind: "config",
            game: &state.game_config,
            gifts: &state.gift_catalog.raw,
        })
        .await?;
        let status = state.status.read().await.clone();
        send_json(sender, &StatusMessage { kind: "status", status }).await?;
        {
            let metrics = state.session.read().await.metrics.clone();
            send_json(sender, &crate::session::state::MetricsMessage { kind: "metrics", metrics }).await?;
        }

        if role == Role::Control {
            let master = state.master.read().await.clone();
            send_json(sender, &MasterConfigMessage { kind: "master_config", master }).await?;
            let catalog = state.gift_catalog_message().await;
            send_json(sender, &catalog).await?;
        } else {
            let snapshot = snapshot_message(state).await;
            send_json(sender, &snapshot).await?;
            let guide = viewer_guide_message(state).await;
            send_json(sender, &guide).await?;
        }
        return Ok(());
    }

    let Some(role) = client.role else {
        return send_json(sender, &error_message("Client chưa đăng ký quyền.")).await;
    };

    let control_only = matches!(kind, "master_save" | "master_test" | "display_update");
    let operator_only = matches!(
        kind,
        "set_username" | "disconnect_tiktok" | "demo_start" | "demo_stop" | "demo_event" | "reset_game"
    );

    if control_only && role != Role::Control {
        return send_json(sender, &error_message("Lệnh này chỉ dành cho bảng Master.")).await;
    }
    if operator_only && role != Role::Control && !client.native {
        return send_json(sender, &error_message("Client không có quyền điều khiển.")).await;
    }

    match kind {
        "display_update" => {
            let mut current = state.display.write().await;
            let next = match current.patched(message.get("patch").unwrap_or(&Value::Null)) {
                Ok(next) => next,
                Err(message) => return send_json(sender, &serde_json::json!({
                    "type": "display_error", "message": message, "display": *current
                })).await,
            };
            let path = state.paths.config_dir.join("display.json");
            let temporary = state.paths.config_dir.join("display.json.tmp");
            let saved = async {
                let json = serde_json::to_string_pretty(&next).expect("boolean display config");
                tokio::fs::write(&temporary, format!("{json}\n")).await?;
                tokio::fs::rename(&temporary, &path).await
            }.await;
            if let Err(error) = saved {
                tracing::error!("không lưu được display.json: {error}");
                return send_json(sender, &serde_json::json!({
                    "type": "display_error", "message": "Không lưu được cài đặt hiển thị. Thử lại.", "display": *current
                })).await;
            }
            *current = next;
            state.broadcast_json(&DisplayConfigMessage { kind: "display_config", display: next });
            Ok(())
        }
        "master_save" => {
            let raw: RawMasterConfig = message
                .get("master")
                .cloned()
                .and_then(|value| serde_json::from_value(value).ok())
                .unwrap_or_default();
            let master = sanitize_master_config(&raw);
            *state.master.write().await = master.clone();

            if let Err(error) = save_master_config(state, &master).await {
                tracing::error!("không lưu được master.json: {error}");
                return send_json(sender, &error_message("Không thể ghi file Master.")).await;
            }
            state.broadcast_json(&MasterConfigMessage { kind: "master_config", master });
            let guide = viewer_guide_message(state).await;
            state.broadcast_json(&guide);
            send_json(sender, &MasterSavedMessage {
                kind: "master_saved",
                message: "Đã lưu và áp dụng Master.",
            })
            .await
        }

        "master_test" => {
            let rule_id = message.get("ruleId").and_then(Value::as_str).unwrap_or("");
            let rule = state
                .master
                .read()
                .await
                .rules
                .iter()
                .find(|rule| rule.id == rule_id && rule.enabled)
                .cloned();
            let Some(rule) = rule else {
                return send_json(sender, &error_message("Không tìm thấy luật Master để test.")).await;
            };

            let trigger = rule.trigger.split(',').next().unwrap_or("").trim().to_string();
            let mut event = IncomingEvent::new(if rule.source == "chat" { "chat" } else { "gift" });
            event.event_id = format!("master-test-{}", now_ms());
            event.user_id = "master-test".into();
            event.unique_id = "master_test".into();
            event.nickname = "Master Test".into();

            if rule.source == "chat" {
                event.comment = trigger;
            } else {
                event.gift_id = if rule.gift_id.is_empty() {
                    format!("master-{}", rule.id)
                } else {
                    rule.gift_id.clone()
                };
                event.gift_name = if trigger.is_empty() { "Master Gift".into() } else { trigger };
                event.repeat_count = 1;
                event.diamond_count = message
                    .get("diamonds")
                    .and_then(Value::as_i64)
                    .unwrap_or(1)
                    .max(1);
            }
            process_game_event(state, &event).await;
            Ok(())
        }

        "set_username" => {
            let raw = message.get("username").and_then(Value::as_str).unwrap_or("");
            let Some(username) = live::normalize_username(raw) else {
                return send_json(
                    sender,
                    &error_message("Username chỉ được gồm chữ, số, dấu chấm hoặc gạch dưới."),
                )
                .await;
            };
            live::connect(state.clone(), username).await;
            Ok(())
        }

        "disconnect_tiktok" => {
            live::disconnect(state).await;
            state.set_status("idle", None, "Đã ngắt kết nối").await;
            Ok(())
        }

        "demo_start" => {
            live::disconnect(state).await;
            let requested = message.get("count").and_then(Value::as_i64).unwrap_or(20);
            let count = requested.clamp(1, state.max_players as i64) as usize;
            demo::start(state.clone(), count).await;
            Ok(())
        }

        "demo_stop" => {
            demo::stop(state).await;
            state.set_status("idle", None, "Đã dừng demo").await;
            Ok(())
        }

        "demo_event" => {
            demo::emit_action(
                state,
                message.get("action").and_then(Value::as_str).unwrap_or("dance"),
                message.get("userIndex").and_then(Value::as_i64).unwrap_or(1).max(1) as usize,
                message.get("value").and_then(Value::as_i64).unwrap_or(1).max(1),
                message.get("giftName").and_then(Value::as_str).unwrap_or(""),
                message.get("manualUsername").and_then(Value::as_str),
            )
            .await;
            Ok(())
        }

        "reset_game" => {
            {
                let mut session = state.session.write().await;
                let source = session.metrics.source.clone();
                let started_at = session.metrics.started_at;
                session.reset(&source);
                session.metrics.started_at = started_at;
            }
            state.broadcast_json(&serde_json::json!({ "type": "reset" }));
            state.broadcast_metrics().await;
            Ok(())
        }

        _ => send_json(sender, &error_message("Lệnh không được hỗ trợ.")).await,
    }
}

// ─── Message gửi cho client ────────────────────────────────────────────────

#[derive(Serialize)]
struct ErrorMessage {
    #[serde(rename = "type")]
    kind: &'static str,
    message: String,
}

fn error_message(message: impl Into<String>) -> ErrorMessage {
    ErrorMessage {
        kind: "error",
        message: message.into(),
    }
}

#[derive(Serialize)]
struct PongMessage {
    #[serde(rename = "type")]
    kind: &'static str,
    timestamp: i64,
}

#[derive(Serialize)]
struct ConfigMessage<'a> {
    #[serde(rename = "type")]
    kind: &'static str,
    game: &'a serde_json::Value,
    gifts: &'a serde_json::Value,
}

#[derive(Serialize)]
struct MasterSavedMessage {
    #[serde(rename = "type")]
    kind: &'static str,
    message: &'static str,
}
