//! Opening the WebSocket over `ws://` or `wss://`, with an optional extra
//! CA file, and turning connection failures into readable messages.
//!
//! Only the `ring` crypto provider is compiled into the workspace; it is
//! installed as the process default before the first connection, so rustls
//! never panics looking for one.

use std::{path::Path, sync::Arc};

use rustls::{
    ClientConfig, RootCertStore,
    pki_types::{CertificateDer, pem::PemObject},
};
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

/// Opens the WebSocket. With `ca` (only allowed for `wss://`) the server's
/// certificate may chain to one of those certificates or to a public root.
pub async fn open(url: &str, ca: &[CertificateDer<'static>]) -> Result<WsStream, String> {
    install_crypto_provider();
    if ca.is_empty() {
        return connect_async(url).await.map(|(ws, _)| ws).map_err(|e| describe(url, &e, false));
    }
    if !is_wss(url) {
        return Err("--ca-file only applies to wss:// URLs".into());
    }
    let mut roots = RootCertStore::empty();
    let (added, _) = roots.add_parsable_certificates(ca.iter().cloned());
    if added == 0 {
        return Err("none of the certificates in the CA file could be used".into());
    }
    let config = ClientConfig::builder().with_root_certificates(roots).with_no_client_auth();
    let connector = Connector::Rustls(Arc::new(config));
    match connect_async_tls_with_config(url, None, false, Some(connector)).await {
        Ok((ws, _)) => Ok(ws),
        // Not signed by the CA file: the public roots may still vouch for it.
        Err(first) if is_certificate_error(&first) => match connect_async(url).await {
            Ok((ws, _)) => Ok(ws),
            Err(_) => Err(describe(url, &first, true)),
        },
        Err(e) => Err(describe(url, &e, true)),
    }
}
