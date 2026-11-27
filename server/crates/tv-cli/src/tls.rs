//! Opening the WebSocket over `ws://` or `wss://`, with an optional extra
//! CA file or a certificate pin in the URL (`#sha256=<hex>`, see
//! [`tv_tls`]), and turning connection failures into readable messages.
//!
//! Only the `ring` crypto provider is compiled into the workspace; it is
//! installed as the process default before the first connection, so rustls
//! never panics looking for one.

use std::path::Path;

use rustls::pki_types::{CertificateDer, pem::PemObject};
use tokio::net::TcpStream;
use tokio_tungstenite::{
    Connector, MaybeTlsStream, WebSocketStream, connect_async, connect_async_tls_with_config,
    tungstenite::{self, error::TlsError},
};

pub type WsStream = WebSocketStream<MaybeTlsStream<TcpStream>>;

/// Installs `ring` as the process-wide rustls provider (once; later calls
/// and an already installed provider are fine).
pub fn install_crypto_provider() {
    static ONCE: std::sync::Once = std::sync::Once::new();
    ONCE.call_once(|| {
        let _ = rustls::crypto::ring::default_provider().install_default();
    });
}

/// Reads every certificate in a PEM file. A file without certificates is
/// an error.
pub fn load_ca_file(path: &Path) -> Result<Vec<CertificateDer<'static>>, String> {
    let bytes =
        std::fs::read(path).map_err(|e| format!("cannot read CA file {}: {e}", path.display()))?;
    let certs = CertificateDer::pem_slice_iter(&bytes)
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| format!("CA file {} is not valid PEM: {e}", path.display()))?;
    if certs.is_empty() {
        return Err(format!("CA file {} holds no certificates", path.display()));
    }
    Ok(certs)
}

fn is_wss(url: &str) -> bool {
    url.get(..6).is_some_and(|s| s.eq_ignore_ascii_case("wss://"))
}

/// The rustls error behind a connection failure, if there is one.
fn rustls_error(e: &tungstenite::Error) -> Option<&rustls::Error> {
    match e {
        tungstenite::Error::Tls(TlsError::Rustls(r)) => Some(r),
        tungstenite::Error::Io(io) => io.get_ref()?.downcast_ref::<rustls::Error>(),
        _ => None,
    }
}

fn is_certificate_error(e: &tungstenite::Error) -> bool {
    matches!(
        rustls_error(e),
        Some(rustls::Error::InvalidCertificate(_)) | Some(rustls::Error::NoCertificatesPresented)
    )
}

/// A readable reason for a failed connection, with a hint where one helps.
pub fn describe(url: &str, e: &tungstenite::Error, with_ca: bool) -> String {
    use std::io::ErrorKind;
    if let Some(r) = rustls_error(e) {
        return match r {
            rustls::Error::InvalidCertificate(c) if with_ca => format!(
                "the server's TLS certificate is not trusted ({c:?}): it is signed neither by a \
                 certificate in --ca-file nor by a public CA"
            ),
            rustls::Error::InvalidCertificate(c) => format!(
                "the server's TLS certificate is not trusted ({c:?}); if the server uses a \
                 self-signed certificate, pass it with --ca-file <cert.pem>"
            ),
            other => format!(
                "the TLS handshake failed ({other}); the server may not serve TLS on this port \
                 (try ws:// instead of wss://)"
            ),
        };
    }
    let plain_hint = if is_wss(url) {
        "; the server may not serve TLS on this port (try ws:// instead of wss://)"
    } else {
        "; if the server serves TLS on this port, use wss:// instead of ws://"
    };
    match e {
        tungstenite::Error::Io(io) if io.kind() == ErrorKind::ConnectionRefused => {
            "connection refused (is the server running, and on this port?)".into()
        }
        tungstenite::Error::Io(io)
            if matches!(
                io.kind(),
                ErrorKind::ConnectionReset | ErrorKind::UnexpectedEof | ErrorKind::BrokenPipe
            ) =>
        {
            format!("the server closed the connection during the handshake{plain_hint}")
        }
        tungstenite::Error::Protocol(p) => {
            format!("the WebSocket handshake failed ({p}){plain_hint}")
        }
        tungstenite::Error::Http(resp) => format!(
            "the server answered HTTP {} instead of upgrading to a WebSocket (is the path /ws?)",
            resp.status()
        ),
        tungstenite::Error::Url(u) => format!("bad URL: {u}"),
        other => other.to_string(),
    }
}

/// Opens the WebSocket.
///
/// A `wss://` URL may end in `#sha256=<hex>`: then the server must present
/// exactly that certificate, and `ca` is not used. Without a pin the
/// server's certificate must chain to a public root or to one of `ca` (only
/// allowed for `wss://`). A refused certificate is reported with its
/// fingerprint and the pinned URL to use if the host confirms it.
pub async fn open(url: &str, ca: &[CertificateDer<'static>]) -> Result<WsStream, String> {
    install_crypto_provider();
    let (target, pin) = tv_tls::split_url(url)?;
    if !is_wss(&target) {
        if !ca.is_empty() {
            return Err("--ca-file only applies to wss:// URLs".into());
        }
        return connect_async(target.as_str())
            .await
            .map(|(ws, _)| ws)
            .map_err(|e| describe(&target, &e, false));
    }
    let with_ca = pin.is_none() && !ca.is_empty();
    let (config, seen) = tv_tls::client_config(pin, if pin.is_some() { &[] } else { ca })?;
    let connector = Connector::Rustls(config);
    match connect_async_tls_with_config(target.as_str(), None, false, Some(connector)).await {
        Ok((ws, _)) => Ok(ws),
        Err(e) if is_certificate_error(&e) => {
            Err(tv_tls::explain_refusal(&target, pin, &seen, with_ca)
                .unwrap_or_else(|| describe(&target, &e, with_ca)))
        }
        Err(e) => Err(describe(&target, &e, with_ca)),
    }
}
