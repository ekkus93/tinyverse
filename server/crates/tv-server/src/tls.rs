//! TLS: `wss://` and HTTPS on the same port, with rustls and the `ring`
//! crypto provider.
//!
//! A world directory may hold `cert.pem` (a certificate chain, leaf first)
//! and `key.pem` (its private key, PKCS#8, PKCS#1 or SEC1). `run` uses them
//! when both exist; `tinyverse-server tls self-signed` writes a pair.
//!
//! The handshakes run in their own tasks, so a slow or hostile client cannot
//! hold up other connections; finished streams are handed to axum through
//! [`TlsListener`].

use std::net::SocketAddr;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, bail};
use rustls::ServerConfig;
use rustls::pki_types::pem::PemObject;
use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use sha2::{Digest, Sha256};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc;
use tokio::task::JoinHandle;
use tokio_rustls::TlsAcceptor;
use tokio_rustls::server::TlsStream;

/// File name of the certificate chain inside a world directory.
pub const CERT_PEM: &str = "cert.pem";
/// File name of the private key inside a world directory.
pub const KEY_PEM: &str = "key.pem";

/// How long a client may take to finish the TLS handshake.
const HANDSHAKE_TIMEOUT: Duration = Duration::from_secs(10);

/// A certificate and key on disk.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TlsFiles {
    pub cert: PathBuf,
    pub key: PathBuf,
}

impl TlsFiles {
    /// `cert.pem` and `key.pem` in a world directory.
    pub fn in_dir(dir: &Path) -> Self {
        TlsFiles { cert: dir.join(CERT_PEM), key: dir.join(KEY_PEM) }
    }
}

/// A loaded certificate, ready to serve.
#[derive(Clone, Debug)]
pub struct TlsSetup {
    pub config: Arc<ServerConfig>,
    /// SHA-256 fingerprint of the leaf certificate, `AB:CD:...`.
    pub fingerprint: String,
}

/// The crypto provider used everywhere in this crate.
pub fn provider() -> Arc<rustls::crypto::CryptoProvider> {
    Arc::new(rustls::crypto::ring::default_provider())
}

/// The SHA-256 fingerprint of a DER certificate, as colon-separated
/// upper-case hex.
pub fn fingerprint(der: &[u8]) -> String {
    let digest = Sha256::digest(der);
    digest.iter().map(|b| format!("{b:02X}")).collect::<Vec<_>>().join(":")
}

/// Reads and checks a certificate chain and key, failing with a message
/// that names the file and the problem.
pub fn load(files: &TlsFiles) -> anyhow::Result<TlsSetup> {
    let cert_text = std::fs::read(&files.cert)
        .with_context(|| format!("cannot read the TLS certificate {}", files.cert.display()))?;
    let key_text = std::fs::read(&files.key)
        .with_context(|| format!("cannot read the TLS key {}", files.key.display()))?;
    let certs = CertificateDer::pem_slice_iter(&cert_text)
        .collect::<Result<Vec<_>, _>>()
        .with_context(|| format!("{} is not a PEM certificate file", files.cert.display()))?;
    if certs.is_empty() {
        bail!("{} holds no certificate", files.cert.display());
    }
    let key = PrivateKeyDer::from_pem_slice(&key_text)
        .with_context(|| format!("{} holds no PEM private key", files.key.display()))?;
    let fingerprint = fingerprint(certs[0].as_ref());
    let mut config = ServerConfig::builder_with_provider(provider())
        .with_safe_default_protocol_versions()
        .context("TLS setup failed")?
        .with_no_client_auth()
        .with_single_cert(certs, key)
        .map_err(|e| {
            anyhow::anyhow!(
                "{} and {} do not form a usable pair: {e}",
                files.cert.display(),
                files.key.display()
            )
        })?;
    config.alpn_protocols = vec![b"http/1.1".to_vec()];
    Ok(TlsSetup { config: Arc::new(config), fingerprint })
}

/// What `tls self-signed` wrote.
#[derive(Clone, Debug)]
pub struct SelfSigned {
    pub files: TlsFiles,
    pub fingerprint: String,
    pub hosts: Vec<String>,
}

/// Writes a new self-signed certificate for `hosts` (DNS names or IP
/// addresses) and its key (mode 0600) into `dir`. Refuses to replace an
/// existing pair unless `force`.
pub fn write_self_signed(dir: &Path, hosts: &[String], force: bool) -> anyhow::Result<SelfSigned> {
    let files = TlsFiles::in_dir(dir);
    let hosts: Vec<String> = if hosts.is_empty() {
        vec!["localhost".to_owned(), "127.0.0.1".to_owned()]
    } else {
        hosts.to_vec()
    };
    for path in [&files.cert, &files.key] {
        if path.exists() {
            if !force {
                bail!("{} already exists; pass --force to replace it", path.display());
            }
            std::fs::remove_file(path)
                .with_context(|| format!("cannot remove {}", path.display()))?;
        }
    }
    let generated = rcgen::generate_simple_self_signed(hosts.clone())
        .context("cannot generate a certificate")?;
    crate::auth::write_secret_file(&files.key, &generated.signing_key.serialize_pem())
        .with_context(|| format!("cannot write {}", files.key.display()))?;
    std::fs::write(&files.cert, generated.cert.pem())
        .with_context(|| format!("cannot write {}", files.cert.display()))?;
    Ok(SelfSigned { fingerprint: fingerprint(generated.cert.der()), files, hosts })
}

/// A TCP listener that completes TLS handshakes before handing connections
/// to axum.
pub struct TlsListener {
    accepted: mpsc::Receiver<(TlsStream<TcpStream>, SocketAddr)>,
    local: SocketAddr,
    acceptor_task: JoinHandle<()>,
}

impl TlsListener {
    pub fn new(tcp: TcpListener, config: Arc<ServerConfig>) -> std::io::Result<Self> {
        let local = tcp.local_addr()?;
        let acceptor = TlsAcceptor::from(config);
        let (tx, accepted) = mpsc::channel(64);
        let acceptor_task = tokio::spawn(async move {
            loop {
                let (stream, peer) = match tcp.accept().await {
                    Ok(pair) => pair,
                    Err(e) => {
                        // Out of file descriptors and the like: back off.
                        tracing::warn!("accept failed: {e}");
                        tokio::time::sleep(Duration::from_millis(50)).await;
                        continue;
                    }
                };
                let acceptor = acceptor.clone();
                let tx = tx.clone();
                tokio::spawn(async move {
                    match tokio::time::timeout(HANDSHAKE_TIMEOUT, acceptor.accept(stream)).await {
                        Ok(Ok(tls)) => {
                            let _ = tx.send((tls, peer)).await;
                        }
                        Ok(Err(e)) => tracing::debug!(%peer, "TLS handshake failed: {e}"),
                        Err(_) => tracing::debug!(%peer, "TLS handshake timed out"),
                    }
                });
            }
        });
        Ok(TlsListener { accepted, local, acceptor_task })
    }
}

impl Drop for TlsListener {
    fn drop(&mut self) {
        self.acceptor_task.abort();
    }
}

impl axum::serve::Listener for TlsListener {
    type Io = TlsStream<TcpStream>;
    type Addr = SocketAddr;

    async fn accept(&mut self) -> (Self::Io, Self::Addr) {
        match self.accepted.recv().await {
            Some(pair) => pair,
            // The acceptor task never ends while the listener lives.
            None => std::future::pending().await,
        }
    }

    fn local_addr(&self) -> std::io::Result<Self::Addr> {
        Ok(self.local)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn self_signed_pair_loads_and_has_a_private_key() {
        let dir = tempfile::tempdir().unwrap();
        let made = write_self_signed(dir.path(), &[], false).unwrap();
        assert_eq!(made.hosts, vec!["localhost", "127.0.0.1"]);
        let setup = load(&made.files).unwrap();
        assert_eq!(setup.fingerprint, made.fingerprint);
        assert_eq!(made.fingerprint.len(), 32 * 3 - 1);
        assert_eq!(setup.config.alpn_protocols, vec![b"http/1.1".to_vec()]);
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mode = std::fs::metadata(&made.files.key).unwrap().permissions().mode();
            assert_eq!(mode & 0o777, 0o600);
        }
    }

    #[test]
    fn existing_pair_is_kept_unless_forced() {
        let dir = tempfile::tempdir().unwrap();
        let first = write_self_signed(dir.path(), &["example.test".into()], false).unwrap();
        let err = write_self_signed(dir.path(), &[], false).unwrap_err();
        assert!(err.to_string().contains("--force"), "{err}");
        assert_eq!(load(&first.files).unwrap().fingerprint, first.fingerprint);
        let second = write_self_signed(dir.path(), &[], true).unwrap();
        assert_ne!(second.fingerprint, first.fingerprint);
        assert_eq!(load(&second.files).unwrap().fingerprint, second.fingerprint);
    }

    #[test]
    fn mismatched_and_missing_files_are_clear_errors() {
        let a = tempfile::tempdir().unwrap();
        let b = tempfile::tempdir().unwrap();
        let one = write_self_signed(a.path(), &[], false).unwrap();
        let two = write_self_signed(b.path(), &[], false).unwrap();
        let mixed = TlsFiles { cert: one.files.cert.clone(), key: two.files.key.clone() };
        let err = format!("{:#}", load(&mixed).unwrap_err());
        assert!(err.contains("do not form a usable pair"), "{err}");

        let missing = TlsFiles { cert: a.path().join("nope.pem"), key: one.files.key.clone() };
        let err = format!("{:#}", load(&missing).unwrap_err());
        assert!(err.contains("nope.pem"), "{err}");

        // A key file given as the certificate, and vice versa.
        let swapped = TlsFiles { cert: one.files.key.clone(), key: one.files.cert.clone() };
        assert!(load(&swapped).is_err());
    }

    #[test]
    fn fingerprint_format() {
        assert_eq!(
            fingerprint(b"abc"),
            "BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD"
        );
    }
}
