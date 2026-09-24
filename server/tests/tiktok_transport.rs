//! Exercise the vendored TikTok transport on loopback, without hitting a live room.
use futures_util::{SinkExt, StreamExt};
use piratetok_live_rs::{
    decode::mapper::decode_message, structs::TikTokLiveEvent, websocket::connection::run_websocket,
};
use std::time::Duration;
use tokio::{net::TcpListener, sync::mpsc, time::timeout};
use tokio_tungstenite::tungstenite::Message;

#[tokio::test]
async fn rejected_handshake_never_reports_connected() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let provider = tokio::spawn(async move {
        let (socket, _) = listener.accept().await.unwrap();
        let result = tokio_tungstenite::accept_hdr_async(socket, |_: &tokio_tungstenite::tungstenite::handshake::server::Request,
            _: tokio_tungstenite::tungstenite::handshake::server::Response| {
            Err(tokio_tungstenite::tungstenite::http::Response::builder()
                .status(403)
                .header("Handshake-Msg", "DEVICE_BLOCKED")
                .body(Some("blocked".to_string()))
                .unwrap())
        })
        .await;
        assert!(result.is_err());
    });
    let (tx, mut rx) = mpsc::channel(8);
    let result = timeout(
        Duration::from_secs(3),
        run_websocket(
            &format!("ws://{address}"),
            "",
            "audit",
            "123",
            Duration::from_secs(10),
            Duration::from_secs(1),
            None,
            "en",
            tx,
        ),
    )
    .await
    .unwrap();
    assert!(matches!(
        result,
        Err(piratetok_live_rs::errors::TikTokLiveError::DeviceBlocked)
    ));
    assert!(rx.recv().await.is_none());
    provider.await.unwrap();
}

#[tokio::test]
async fn connected_is_emitted_after_each_successful_websocket_handshake() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let provider = tokio::spawn(async move {
        for _ in 0..2 {
            let (socket, _) = listener.accept().await.unwrap();
            let mut ws = tokio_tungstenite::accept_async(socket).await.unwrap();
            // Initial heartbeat and enter-room frames precede Connected.
            for _ in 0..2 {
                assert!(matches!(
                    ws.next().await.unwrap().unwrap(),
                    Message::Binary(_)
                ));
            }
            ws.send(Message::Binary(vec![0xff].into())).await.unwrap();
            ws.send(Message::Ping(vec![1, 2, 3].into())).await.unwrap();
            assert!(matches!(
                ws.next().await.unwrap().unwrap(),
                Message::Pong(_)
            ));
            ws.close(None).await.unwrap();
        }
    });
    for _ in 0..2 {
        let (tx, mut rx) = mpsc::channel(8);
        timeout(
            Duration::from_secs(3),
            run_websocket(
                &format!("ws://{address}"),
                "",
                "audit",
                "123",
                Duration::from_secs(10),
                Duration::from_secs(1),
                None,
                "en",
                tx,
            ),
        )
        .await
        .unwrap()
        .unwrap();
        assert!(
            matches!(rx.recv().await, Some(TikTokLiveEvent::Connected { room_id }) if room_id == "123")
        );
        assert!(rx.recv().await.is_none());
    }
    provider.await.unwrap();
}

#[tokio::test]
async fn an_open_but_silent_provider_is_closed_by_the_stale_timer() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let address = listener.local_addr().unwrap();
    let provider = tokio::spawn(async move {
        let (socket, _) = listener.accept().await.unwrap();
        let mut ws = tokio_tungstenite::accept_async(socket).await.unwrap();
        while let Some(Ok(_)) = ws.next().await {}
    });
    let (tx, _rx) = mpsc::channel(8);
    timeout(
        Duration::from_secs(2),
        run_websocket(
            &format!("ws://{address}"),
            "",
            "audit",
            "123",
            Duration::from_millis(50),
            Duration::from_millis(150),
            None,
            "en",
            tx,
        ),
    )
    .await
    .unwrap()
    .unwrap();
    provider.await.unwrap();
}

#[test]
fn live_end_and_unknown_or_malformed_protobuf_are_distinct() {
    let ended = decode_message("WebcastControlMessage", &[0x10, 3]);
    assert_eq!(
        ended
            .iter()
            .filter(|event| matches!(event, TikTokLiveEvent::LiveEnded(_)))
            .count(),
        1
    );
    let other_control = decode_message("WebcastControlMessage", &[0x10, 1]);
    assert!(!other_control
        .iter()
        .any(|event| matches!(event, TikTokLiveEvent::LiveEnded(_))));
    for (kind, bytes) in [
        ("WebcastChatMessage", &[0xff][..]),
        ("NewTikTokMessage", &[][..]),
    ] {
        assert!(matches!(
            &decode_message(kind, bytes)[0],
            TikTokLiveEvent::Unknown { .. }
        ));
    }
}
