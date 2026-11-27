//! TLS for `wss://` worlds: rustls with the `ring` provider, trusting the
//! public web roots plus, optionally, the certificates of a `--ca-file`
//! (for a world serving a self-signed certificate).

use std::path::Path;
use std::sync::Arc;

use rustls::pki_types::CertificateDer;
use rustls::pki_types::pem::PemObject;
use rustls::{ClientConfig, RootCertStore};

/// Installs `ring` as the process-wide rustls provider, so that nothing in
/// the process can panic for want of one. Safe to call more than once.
pub fn install_crypto_provider() {
    let _ = rustls::crypto::ring::default_provider().install_default();
}

/// Reads every certificate in a PEM file.
pub fn load_ca_file(path: &Path) -> Result<Vec<CertificateDer<'static>>, String> {
    let shown = path.display();
    let bytes = std::fs::read(path).map_err(|e| format!("cannot read CA file {shown}: {e}"))?;
    let certs = CertificateDer::pem_slice_iter(&bytes)
        .collect::<Result<Vec<_>, _>>()
        .map_err(|e| format!("CA file {shown} is not valid PEM: {e}"))?;
    if certs.is_empty() {
        return Err(format!("CA file {shown} holds no PEM certificates (BEGIN CERTIFICATE)"));
    }
    Ok(certs)
}

/// A client configuration trusting the public roots and `extra`.
pub fn client_config(extra: &[CertificateDer<'static>]) -> Result<Arc<ClientConfig>, String> {
    let mut roots = RootCertStore::empty();
    roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    for cert in extra {
        roots.add(cert.clone()).map_err(|e| format!("CA certificate not usable: {e}"))?;
    }
    let config =
        ClientConfig::builder_with_provider(Arc::new(rustls::crypto::ring::default_provider()))
            .with_safe_default_protocol_versions()
            .map_err(|e| format!("TLS setup failed: {e}"))?
            .with_root_certificates(roots)
            .with_no_client_auth();
    Ok(Arc::new(config))
}

/// Whether a URL asks for TLS.
pub fn is_wss(url: &str) -> bool {
    url.trim_start().to_ascii_lowercase().starts_with("wss://")
}

/// A hint to append to a connection error, naming the likely fix.
pub fn hint(url: &str, error: &str, have_ca: bool) -> &'static str {
    let e = error.to_ascii_lowercase();
    if is_wss(url) {
        if e.contains("unknownissuer") || e.contains("invalid peer certificate") {
            if have_ca {
                " (the server's certificate is not signed by the --ca-file or a public CA)"
            } else {
                " (the server's certificate is not trusted; if the world uses a self-signed \
                 certificate, start tv-mcp with --ca-file pointing at it, e.g. the world's cert.pem)"
            }
        } else if e.contains("corrupt message")
            || e.contains("invalidcontenttype")
            || e.contains("invalid content type")
            || e.contains("wrong version")
        {
            " (the server does not seem to speak TLS on this port; try a ws:// URL)"
        } else {
            ""
        }
    } else if e.contains("reset")
        || e.contains("handshake not finished")
        || e.contains("connection closed")
        || e.contains("unexpected eof")
        || e.contains("httparse")
    {
        " (if the world serves TLS, use a wss:// URL)"
    } else {
        ""
    }
}
