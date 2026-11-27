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

use std::net::{IpAddr, SocketAddr};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use anyhow::{Context, bail};
use rustls::ServerConfig;
use rustls::pki_types::pem::PemObject;
use rustls::pki_types::{CertificateDer, PrivateKeyDer};
use tokio::net::{TcpListener, TcpStream};
use tokio::sync::mpsc;
use tokio::task::JoinHandle;
use tokio_rustls::TlsAcceptor;
use tokio_rustls::server::TlsStream;
pub use tv_tls::Pin;

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
    /// The same, as the pin clients put in a URL (`#sha256=<hex>`).
    pub pin: Pin,
}

/// The crypto provider used everywhere in this crate.
pub fn provider() -> Arc<rustls::crypto::CryptoProvider> {
    Arc::new(rustls::crypto::ring::default_provider())
}

/// The SHA-256 fingerprint of a DER certificate, as colon-separated
/// upper-case hex.
pub fn fingerprint(der: &[u8]) -> String {
    tv_tls::sha256_colons(der)
}

/// The leaf (first) certificate of a PEM chain file.
pub fn read_leaf(cert: &Path) -> anyhow::Result<CertificateDer<'static>> {
    let cert_text = std::fs::read(cert)
        .with_context(|| format!("cannot read the TLS certificate {}", cert.display()))?;
    let certs = CertificateDer::pem_slice_iter(&cert_text)
        .collect::<Result<Vec<_>, _>>()
        .with_context(|| format!("{} is not a PEM certificate file", cert.display()))?;
    certs.into_iter().next().with_context(|| format!("{} holds no certificate", cert.display()))
}

/// The line `run` and `tls fingerprint` print for a certificate, on stdout:
/// `tls certificate sha256=<64 hex digits>`. Scripts and the desktop client
/// look for it; keep it stable.
pub fn certificate_line(pin: &Pin) -> String {
    format!("tls certificate {pin}")
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
    let pin = Pin::of(certs[0].as_ref());
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
    Ok(TlsSetup { config: Arc::new(config), fingerprint, pin })
}

/// What `tls self-signed` wrote.
#[derive(Clone, Debug)]
pub struct SelfSigned {
    pub files: TlsFiles,
    pub fingerprint: String,
    pub pin: Pin,
    pub hosts: Vec<String>,
}

/// This machine's current non-loopback addresses that others on the network
/// could use: IPv4 and IPv6, without link-local IPv6 (which needs a zone).
///
/// Best first: the address of the default route, then other physical
/// interfaces, then bridges, VPNs and container networks; IPv4 before IPv6.
pub fn lan_addresses() -> Vec<IpAddr> {
    order_lan(interfaces(), default_route_source())
}

/// [`lan_addresses`] without those of container and virtual-machine
/// bridges (Docker, libvirt, VirtualBox, VMware), which guests on the
/// network cannot reach: the addresses worth printing for them.
pub fn guest_addresses() -> Vec<IpAddr> {
    let found = interfaces().into_iter().filter(|(name, _)| !is_bridge(name)).collect();
    order_lan(found, default_route_source())
}

fn interfaces() -> Vec<(String, IpAddr)> {
    if_addrs::get_if_addrs()
        .unwrap_or_default()
        .into_iter()
        .map(|i| (i.name.clone(), i.ip()))
        .collect()
}

fn is_bridge(interface: &str) -> bool {
    ["docker", "br-", "veth", "virbr", "vmnet", "vbox"].iter().any(|p| interface.starts_with(p))
}

/// The local address the default route would use. Connecting a UDP socket
/// sends nothing; it only picks the route.
fn default_route_source() -> Option<IpAddr> {
    let socket = std::net::UdpSocket::bind("0.0.0.0:0").ok()?;
    socket.connect("192.0.2.1:9").ok()?;
    socket.local_addr().ok().map(|a| a.ip())
}

/// Orders `(interface, address)` pairs as [`lan_addresses`] describes,
/// dropping unusable addresses and repeats.
fn order_lan(found: Vec<(String, IpAddr)>, default_route: Option<IpAddr>) -> Vec<IpAddr> {
    const VIRTUAL: [&str; 11] =
        ["docker", "br-", "veth", "virbr", "vmnet", "vbox", "tun", "tap", "wg", "tailscale", "zt"];
    let mut usable: Vec<(bool, bool, bool, IpAddr)> = found
        .into_iter()
        .filter(|(_, ip)| usable_lan_address(*ip))
        .map(|(name, ip)| {
            let is_virtual = VIRTUAL.iter().any(|v| name.starts_with(v));
            (Some(ip) != default_route, is_virtual, ip.is_ipv6(), ip)
        })
        .collect();
    usable.sort();
    let mut ordered: Vec<IpAddr> = Vec::new();
    for (_, _, _, ip) in usable {
        if !ordered.contains(&ip) {
            ordered.push(ip);
        }
    }
    ordered
}

fn usable_lan_address(ip: IpAddr) -> bool {
    if ip.is_loopback() || ip.is_unspecified() || ip.is_multicast() {
        return false;
    }
    match ip {
        IpAddr::V4(_) => true,
        // fe80::/10
        IpAddr::V6(v6) => (v6.segments()[0] & 0xffc0) != 0xfe80,
    }
}

/// The names a self-signed certificate is made for: `localhost`,
/// `127.0.0.1`, `::1`, then `lan` and `extra`, without repeats.
pub fn certificate_names(lan: &[IpAddr], extra: &[String]) -> Vec<String> {
    let mut names = vec!["localhost".to_owned(), "127.0.0.1".to_owned(), "::1".to_owned()];
    names.extend(lan.iter().map(IpAddr::to_string));
    names.extend(extra.iter().map(|h| h.trim().trim_matches(['[', ']']).to_owned()));
    let mut seen = std::collections::HashSet::new();
    names.retain(|n| !n.is_empty() && seen.insert(n.to_ascii_lowercase()));
    names
}

/// Writes a new self-signed certificate into `dir`, valid for localhost,
/// 127.0.0.1, ::1, every current LAN address and `extra_hosts` (DNS names or
/// IP addresses): the certificate world-readable (it is public), its key
/// with mode 0600. Refuses to replace an existing pair unless `force`.
pub fn write_self_signed(
    dir: &Path,
    extra_hosts: &[String],
    force: bool,
) -> anyhow::Result<SelfSigned> {
    let files = TlsFiles::in_dir(dir);
    let hosts = certificate_names(&lan_addresses(), extra_hosts);
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
    // Public, so readable by everyone whatever the umask.
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&files.cert, std::fs::Permissions::from_mode(0o644))
            .with_context(|| format!("cannot set the mode of {}", files.cert.display()))?;
    }
    let der = generated.cert.der();
    Ok(SelfSigned { fingerprint: fingerprint(der), pin: Pin::of(der), files, hosts })
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
        assert_eq!(made.hosts[..3], ["localhost", "127.0.0.1", "::1"]);
        let setup = load(&made.files).unwrap();
        assert_eq!(setup.fingerprint, made.fingerprint);
        assert_eq!(setup.pin, made.pin);
        assert_eq!(setup.pin.colons(), made.fingerprint);
        let leaf = read_leaf(&made.files.cert).unwrap();
        assert!(made.pin.matches(leaf.as_ref()));
        assert_eq!(
            certificate_line(&made.pin),
            format!("tls certificate sha256={}", made.pin.hex())
        );
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

    /// Whether a client that trusts the certificate itself would accept it
    /// for `name`: that is, whether `name` is among its subject alternative
    /// names.
    fn valid_for(cert: &CertificateDer<'static>, name: &str) -> bool {
        use rustls::client::danger::ServerCertVerifier;
        use rustls::pki_types::{ServerName, UnixTime};
        let mut roots = rustls::RootCertStore::empty();
        roots.add(cert.clone()).unwrap();
        let verifier = rustls::client::WebPkiServerVerifier::builder_with_provider(
            Arc::new(roots),
            provider(),
        )
        .build()
        .unwrap();
        let name = ServerName::try_from(name.to_owned()).unwrap();
        verifier.verify_server_cert(cert, &[], &name, &[], UnixTime::now()).is_ok()
    }

    #[test]
    fn self_signed_names_cover_loopback_every_lan_address_and_extra_hosts() {
        let dir = tempfile::tempdir().unwrap();
        let made =
            write_self_signed(dir.path(), &["world.example".into(), "10.9.8.7".into()], false)
                .unwrap();
        let leaf = read_leaf(&made.files.cert).unwrap();
        for name in ["localhost", "127.0.0.1", "::1", "world.example", "10.9.8.7"] {
            assert!(valid_for(&leaf, name), "{name} is in {:?}", made.hosts);
        }
        let lan = lan_addresses();
        for ip in &lan {
            assert!(valid_for(&leaf, &ip.to_string()), "LAN address {ip} is in {:?}", made.hosts);
        }
        assert!(!valid_for(&leaf, "other.example"));
        assert!(!valid_for(&leaf, "10.9.8.6"));
        assert_eq!(made.hosts.len(), 5 + lan.len(), "{:?}", made.hosts);
    }

    #[test]
    fn lan_addresses_put_the_default_route_first_and_containers_last() {
        let ip = |s: &str| s.parse::<IpAddr>().unwrap();
        let found = vec![
            ("docker0".to_owned(), ip("172.17.0.1")),
            ("br-1234".to_owned(), ip("172.18.0.1")),
            ("lo".to_owned(), ip("127.0.0.1")),
            ("eth0".to_owned(), ip("10.77.77.1")),
            ("wlan0".to_owned(), ip("fe80::1")),
            ("wlan0".to_owned(), ip("fd00::7")),
            ("wlan0".to_owned(), ip("192.168.87.210")),
            ("wg0".to_owned(), ip("10.8.0.2")),
        ];
        let ordered = order_lan(found.clone(), Some(ip("192.168.87.210")));
        assert_eq!(
            ordered,
            ["192.168.87.210", "10.77.77.1", "fd00::7", "10.8.0.2", "172.17.0.1", "172.18.0.1"]
                .map(ip)
        );
        assert_eq!(order_lan(found, None)[0], ip("10.77.77.1"));
    }

    #[test]
    fn certificate_names_are_deduplicated_and_skip_link_local() {
        let lan: Vec<IpAddr> = vec!["192.168.1.20".parse().unwrap(), "fd00::5".parse().unwrap()];
        let names = certificate_names(&lan, &["LOCALHOST".into(), "[fd00::6]".into(), "".into()]);
        assert_eq!(names, ["localhost", "127.0.0.1", "::1", "192.168.1.20", "fd00::5", "fd00::6"]);
        assert!(usable_lan_address("192.168.1.20".parse().unwrap()));
        assert!(usable_lan_address("2001:db8::1".parse().unwrap()));
        assert!(!usable_lan_address("fe80::1".parse().unwrap()));
        assert!(!usable_lan_address("127.0.0.1".parse().unwrap()));
        assert!(!usable_lan_address("::1".parse().unwrap()));
        assert!(!usable_lan_address("0.0.0.0".parse().unwrap()));
    }

    #[test]
    fn fingerprint_format() {
        assert_eq!(
            fingerprint(b"abc"),
            "BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD"
        );
    }
}
