//! `wss://` and HTTPS with a self-signed certificate.

mod common;

use std::time::Duration;

use common::*;
use tv_proto::Encoding;
use tv_proto::v1::{self, client_message::Body as C, server_message::Body as S};
use tv_server::tls::{TlsFiles, write_self_signed};
use tv_server::{Info, Tuning};

async fn tls_server(world: &TestWorld) -> (tv_server::ServerHandle, TlsFiles) {
    let made = write_self_signed(world.path(), &[], false).unwrap();
    let server = tv_server::start_with(
        world.path(),
        "127.0.0.1:0".parse().unwrap(),
        Tuning::default(),
        Some(&made.files),
    )
    .await
    .unwrap();
    assert_eq!(server.tls_fingerprint(), Some(made.fingerprint.as_str()));
    (server, made.files)
}

#[tokio::test]
async fn handshake_and_chat_over_wss() {
    let world = TestWorld::new();
    let (server, files) = tls_server(&world).await;
    assert!(server.ws_url().starts_with("wss://127.0.0.1:"), "{}", server.ws_url());
    let trust = trusting(&files.cert);

    let mut sessions = Vec::new();
    for (name, encoding) in [("json", Encoding::Json), ("binary", Encoding::Binary)] {
        let client = Client::connect_url(&server.ws_url(), encoding, Some(trust.clone()))
            .await
            .expect("wss connect with the certificate trusted");
        let s = join_client(client, hello(name)).await;
        assert_eq!(s.welcome.world.as_ref().unwrap().name, "Test World");
        sessions.push(s);
    }
    let seq = sessions[0]
        .send(C::Chat(v1::Chat { channel: v1::ChatChannel::World as i32, text: "over TLS".into() }))
        .await;
    for (i, s) in sessions.iter_mut().enumerate() {
        let msg = s.recv_where("chat", |m| matches!(m.body, Some(S::ChatMessage(_)))).await;
        assert_eq!(msg.reply_to, if i == 0 { seq } else { 0 });
        let Some(S::ChatMessage(c)) = msg.body else { unreachable!() };
        assert_eq!(c.entry.unwrap().text, "over TLS");
    }
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn info_and_health_over_https() {
    let world = TestWorld::new();
    let (server, files) = tls_server(&world).await;
    assert!(server.http_url().starts_with("https://"));
    let trust = trusting(&files.cert);
    let (status, body) = https_get(&server, "/info", trust.clone()).await;
    assert_eq!(status, 200);
    let info: Info = serde_json::from_str(&body).unwrap();
    assert_eq!(info.name, "Test World");
    assert_eq!(https_get(&server, "/health", trust).await, (200, "ok".to_owned()));
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn plain_and_untrusted_clients_fail_cleanly() {
    let world = TestWorld::new();
    let (server, files) = tls_server(&world).await;
    let plain = format!("ws://{}/ws", server.addr());

    // Plain ws:// to a TLS port: an error, quickly, not a hang.
    let attempt = Client::connect_url(&plain, Encoding::Json, None);
    let result = tokio::time::timeout(Duration::from_secs(5), attempt).await;
    assert!(matches!(result, Ok(Err(_))), "plain ws:// must fail: {:?}", result.map(|r| r.is_ok()));

    // Plain HTTP likewise gets no answer but a closed connection.
    let attempt = async {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};
        let mut tcp = tokio::net::TcpStream::connect(server.addr()).await.unwrap();
        tcp.write_all(b"GET /health HTTP/1.1\r\nHost: x\r\n\r\n").await.unwrap();
        let mut response = Vec::new();
        let _ = tcp.read_to_end(&mut response).await;
        response
    };
    let response = tokio::time::timeout(Duration::from_secs(5), attempt).await.unwrap();
    assert!(!response.starts_with(b"HTTP/1.1 200"), "plain HTTP must not be served");

    // wss:// without trusting the self-signed certificate is refused.
    let result = Client::connect_url(&server.ws_url(), Encoding::Json, None).await;
    assert!(result.is_err(), "an untrusted certificate must be refused");

    // The server is unharmed.
    let client = Client::connect_url(&server.ws_url(), Encoding::Json, Some(trusting(&files.cert)))
        .await
        .unwrap();
    join_client(client, hello("after")).await;
    server.shutdown().await.unwrap();
}

#[tokio::test]
async fn bad_certificate_files_are_start_up_errors() {
    let world = TestWorld::new();
    let other = tempfile::tempdir().unwrap();
    let mine = write_self_signed(world.path(), &[], false).unwrap();
    let theirs = write_self_signed(other.path(), &[], false).unwrap();
    let bind = "127.0.0.1:0".parse().unwrap();

    let cases = [
        (
            TlsFiles { cert: world.path().join("missing.pem"), key: mine.files.key.clone() },
            "missing.pem",
        ),
        (
            TlsFiles { cert: mine.files.cert.clone(), key: other.path().join("nokey.pem") },
            "nokey.pem",
        ),
        (
            TlsFiles { cert: mine.files.cert.clone(), key: theirs.files.key.clone() },
            "do not form a usable pair",
        ),
        (
            TlsFiles { cert: mine.files.key.clone(), key: mine.files.key.clone() },
            "holds no certificate",
        ),
    ];
    for (files, needle) in cases {
        let err = tv_server::start_with(world.path(), bind, Tuning::default(), Some(&files))
            .await
            .err()
            .unwrap_or_else(|| panic!("{files:?} should not start"));
        let text = format!("{err:#}");
        assert!(text.contains(needle), "{files:?}: {text}");
    }

    // The world itself was not touched: a plain start still works.
    let server = world.start().await;
    join(&server, "fine").await;
    server.shutdown().await.unwrap();
}
