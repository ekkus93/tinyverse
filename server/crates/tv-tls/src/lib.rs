//! TLS helpers shared by `tinyverse-server`, `tv-cli` and `tv-mcp`:
//! certificate fingerprints, and trust on first use by pinning a
//! certificate's SHA-256 in the world URL.
//!
//! A join address may carry a pin as its fragment:
//! `wss://192.168.1.20:7878/ws#sha256=<64 hex digits>`. With a pin the client
//! accepts the server only if the SHA-256 of its leaf certificate (the DER
//! bytes) equals the pin; the host name and the chain are then not checked,
//! but the handshake signature still is, so the server must hold the
//! certificate's private key. Without a pin the normal WebPKI checks apply
//! (public roots plus any extra roots, such as a `--ca-file`). The fragment
//! never leaves the client: it is removed before connecting.
//!
//! Only the `ring` crypto provider is used.

use std::fmt;
use std::sync::{Arc, Mutex};

use rustls::client::WebPkiServerVerifier;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::{CryptoProvider, verify_tls12_signature, verify_tls13_signature};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime};
use rustls::{
    CertificateError, ClientConfig, DigitallySignedStruct, DistinguishedName, OtherError,
    RootCertStore, SignatureScheme,
};
use sha2::{Digest, Sha256};

/// The URL fragment key of a pin: `#sha256=<hex>`.
pub const PIN_PREFIX: &str = "sha256=";

/// The crypto provider used everywhere: `ring`.
pub fn provider() -> Arc<CryptoProvider> {
    Arc::new(rustls::crypto::ring::default_provider())
}

/// Installs `ring` as the process-wide rustls provider, so that nothing in
/// the process can panic for want of one. Safe to call more than once.
pub fn install_crypto_provider() {
    let _ = rustls::crypto::ring::default_provider().install_default();
}

/// The SHA-256 of a certificate as colon-separated upper-case hex
/// (`AB:CD:…`), the form people compare by eye.
pub fn sha256_colons(der: &[u8]) -> String {
    Pin::of(der).colons()
}

/// A certificate pin: the SHA-256 of the leaf certificate's DER bytes.
#[derive(Clone, Copy, PartialEq, Eq, Hash)]
pub struct Pin([u8; 32]);

impl Pin {
    /// The pin of a DER certificate.
    pub fn of(der: &[u8]) -> Pin {
        Pin(Sha256::digest(der).into())
    }

    /// Reads `sha256=<hex>` or bare hex: 64 hex digits in either case,
    /// optionally separated by colons.
    pub fn parse(text: &str) -> Result<Pin, String> {
        let t = text.trim();
        let t = match t.get(..PIN_PREFIX.len()) {
            Some(p) if p.eq_ignore_ascii_case(PIN_PREFIX) => &t[PIN_PREFIX.len()..],
            _ => t,
        };
        let digits: String = t.chars().filter(|&c| c != ':').collect();
        let bad = || {
            format!(
                "\"{text}\" is not a SHA-256 certificate fingerprint (64 hex digits, colons allowed)"
            )
        };
        if digits.len() != 64 {
            return Err(bad());
        }
        let mut bytes = [0u8; 32];
        hex::decode_to_slice(&digits, &mut bytes).map_err(|_| bad())?;
        Ok(Pin(bytes))
    }

    /// Lower-case hex without separators, as used in URLs.
    pub fn hex(&self) -> String {
        hex::encode(self.0)
    }

    /// Colon-separated upper-case hex.
    pub fn colons(&self) -> String {
        self.0.iter().map(|b| format!("{b:02X}")).collect::<Vec<_>>().join(":")
    }

    pub fn matches(&self, der: &[u8]) -> bool {
        Pin::of(der) == *self
    }
}

/// `sha256=<hex>`, the URL fragment form.
impl fmt::Display for Pin {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{PIN_PREFIX}{}", self.hex())
    }
}

impl fmt::Debug for Pin {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "Pin({})", self.hex())
    }
}

/// Whether a URL asks for TLS.
pub fn is_wss(url: &str) -> bool {
    url.trim_start().get(..6).is_some_and(|s| s.eq_ignore_ascii_case("wss://"))
}

/// Splits a world URL into the URL to connect to and its pin, if it has
/// one. A fragment other than `#sha256=<hex>`, and a pin on a `ws://` URL,
/// are errors.
pub fn split_url(url: &str) -> Result<(String, Option<Pin>), String> {
    let Some((base, fragment)) = url.split_once('#') else {
        return Ok((url.to_owned(), None));
    };
    let is_pin =
        fragment.get(..PIN_PREFIX.len()).is_some_and(|p| p.eq_ignore_ascii_case(PIN_PREFIX));
    if !is_pin {
        return Err(format!(
            "the only URL fragment understood is #sha256=<certificate fingerprint>, not #{fragment}"
        ));
    }
    let pin = Pin::parse(fragment)?;
    if !is_wss(base) {
        return Err("a #sha256= certificate pin only applies to wss:// URLs".into());
    }
    Ok((base.to_owned(), Some(pin)))
}

/// `url` (without a fragment) with `pin` as its fragment.
pub fn pinned_url(url: &str, pin: &Pin) -> String {
    let base = url.split_once('#').map_or(url, |(b, _)| b);
    format!("{base}#{pin}")
}

/// The leaf certificate the server presented in the latest handshake made
/// with a configuration from [`client_config`], and whether it was
/// accepted. Use it to explain a refusal.
#[derive(Clone, Debug, Default)]
pub struct Seen(Arc<Mutex<Option<(Pin, bool)>>>);

impl Seen {
    /// The certificate presented, accepted or not.
    pub fn leaf(&self) -> Option<Pin> {
        self.get().map(|(pin, _)| pin)
    }

    /// The certificate presented, if it was refused.
    pub fn refused(&self) -> Option<Pin> {
        self.get().and_then(|(pin, accepted)| (!accepted).then_some(pin))
    }

    fn get(&self) -> Option<(Pin, bool)> {
        *self.0.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn record<T>(&self, der: &[u8], verdict: Result<T, rustls::Error>) -> Result<T, rustls::Error> {
        *self.0.lock().unwrap_or_else(|e| e.into_inner()) = Some((Pin::of(der), verdict.is_ok()));
        verdict
    }
}

/// The server's certificate does not match the pin.
#[derive(Debug)]
pub struct PinMismatch {
    pub expected: Pin,
    pub presented: Pin,
}

impl fmt::Display for PinMismatch {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "the server's certificate has SHA-256 {}, not the pinned {}",
            self.presented.hex(),
            self.expected.hex()
        )
    }
}

impl std::error::Error for PinMismatch {}

/// A client configuration for `wss://`.
///
/// With `pin`, the server is accepted only if its leaf certificate matches
/// it; `extra_roots` are then not used. Without, the server must chain to a
/// public root (webpki-roots) or to one of `extra_roots`, for its name.
/// Either way the returned [`Seen`] reports what the server presented.
pub fn client_config(
    pin: Option<Pin>,
    extra_roots: &[CertificateDer<'static>],
) -> Result<(Arc<ClientConfig>, Seen), String> {
    let provider = provider();
    let seen = Seen::default();
    let verifier = verifier(pin, extra_roots, &provider, &seen)?;
    let config = ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .map_err(|e| format!("TLS setup failed: {e}"))?
        .dangerous()
        .with_custom_certificate_verifier(verifier)
        .with_no_client_auth();
    Ok((Arc::new(config), seen))
}

fn verifier(
    pin: Option<Pin>,
    extra_roots: &[CertificateDer<'static>],
    provider: &Arc<CryptoProvider>,
    seen: &Seen,
) -> Result<Arc<dyn ServerCertVerifier>, String> {
    if let Some(pin) = pin {
        return Ok(Arc::new(PinnedVerifier {
            pin,
            provider: provider.clone(),
            seen: seen.clone(),
        }));
    }
    let mut roots = RootCertStore::empty();
    roots.extend(webpki_roots::TLS_SERVER_ROOTS.iter().cloned());
    for cert in extra_roots {
        roots.add(cert.clone()).map_err(|e| format!("CA certificate not usable: {e}"))?;
    }
    let inner = WebPkiServerVerifier::builder_with_provider(Arc::new(roots), provider.clone())
        .build()
        .map_err(|e| format!("TLS setup failed: {e}"))?;
    Ok(Arc::new(Recording { inner, seen: seen.clone() }))
}

/// The normal WebPKI verifier, remembering the leaf it was shown.
#[derive(Debug)]
struct Recording {
    inner: Arc<WebPkiServerVerifier>,
    seen: Seen,
}

impl ServerCertVerifier for Recording {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        intermediates: &[CertificateDer<'_>],
        server_name: &ServerName<'_>,
        ocsp_response: &[u8],
        now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        let verdict = self.inner.verify_server_cert(
            end_entity,
            intermediates,
            server_name,
            ocsp_response,
            now,
        );
        self.seen.record(end_entity.as_ref(), verdict)
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        self.inner.verify_tls12_signature(message, cert, dss)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        self.inner.verify_tls13_signature(message, cert, dss)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.inner.supported_verify_schemes()
    }

    fn root_hint_subjects(&self) -> Option<&[DistinguishedName]> {
        self.inner.root_hint_subjects()
    }
}

/// Accepts exactly one certificate, by its SHA-256. The handshake signature
/// is still verified against that certificate's key.
#[derive(Debug)]
struct PinnedVerifier {
    pin: Pin,
    provider: Arc<CryptoProvider>,
    seen: Seen,
}

impl ServerCertVerifier for PinnedVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        let presented = Pin::of(end_entity.as_ref());
        let verdict = if presented == self.pin {
            Ok(ServerCertVerified::assertion())
        } else {
            Err(rustls::Error::InvalidCertificate(CertificateError::Other(OtherError(Arc::new(
                PinMismatch { expected: self.pin, presented },
            )))))
        };
        self.seen.record(end_entity.as_ref(), verdict)
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls12_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls13_signature(message, cert, dss, &self.provider.signature_verification_algorithms)
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.provider.signature_verification_algorithms.supported_schemes()
    }
}

/// Why a `wss://` handshake was refused, from what the server presented,
/// in words for a person, with the next step. `None` unless the server's
/// certificate was refused (the failure was something else).
pub fn explain_refusal(url: &str, pin: Option<Pin>, seen: &Seen, have_ca: bool) -> Option<String> {
    let presented = seen.refused()?;
    let base = url.split_once('#').map_or(url, |(b, _)| b);
    Some(match pin {
        Some(pin) => format!(
            "the server's certificate does not match the pin in the URL: it has SHA-256 {}, the \
             URL expects {}. Either this is not the world you think it is, or its host made a new \
             certificate; ask the host for the current address (`tinyverse-server tls \
             fingerprint <world-dir>`)",
            presented.hex(),
            pin.hex()
        ),
        None => format!(
            "the server's TLS certificate is not trusted{}. Its SHA-256 fingerprint is {}. If the \
             host confirms that fingerprint (`tinyverse-server tls fingerprint <world-dir>` shows \
             it), join with {} (or pass the world's cert.pem with --ca-file)",
            if have_ca {
                ": it is signed neither by a certificate in --ca-file nor by a public CA"
            } else {
                ""
            },
            presented.colons(),
            pinned_url(base, &presented)
        ),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const HEX: &str = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    #[test]
    fn pins_parse_in_every_written_form() {
        let pin = Pin::of(b"abc");
        assert_eq!(pin.hex(), HEX);
        assert_eq!(Pin::parse(HEX).unwrap(), pin);
        assert_eq!(Pin::parse(&HEX.to_uppercase()).unwrap(), pin);
        assert_eq!(Pin::parse(&format!("sha256={HEX}")).unwrap(), pin);
        assert_eq!(Pin::parse(&format!("SHA256={HEX}")).unwrap(), pin);
        assert_eq!(Pin::parse(&pin.colons()).unwrap(), pin);
        assert_eq!(pin.to_string(), format!("sha256={HEX}"));
        assert!(pin.colons().starts_with("BA:78:16:BF:"));
        assert!(pin.matches(b"abc"));
        assert!(!pin.matches(b"abd"));
        for bad in ["", "abc", &HEX[..62], &format!("{HEX}00"), &HEX.replace('a', "g")] {
            let err = Pin::parse(bad).unwrap_err();
            assert!(err.contains("64 hex digits"), "{err}");
        }
    }

    #[test]
    fn urls_split_into_address_and_pin() {
        assert_eq!(split_url("wss://h:1/ws").unwrap(), ("wss://h:1/ws".into(), None));
        assert_eq!(split_url("ws://h:1/ws").unwrap(), ("ws://h:1/ws".into(), None));
        let (base, pin) = split_url(&format!("wss://10.0.0.2:7878/ws#sha256={HEX}")).unwrap();
        assert_eq!(base, "wss://10.0.0.2:7878/ws");
        assert_eq!(pin, Some(Pin::of(b"abc")));
        assert!(
            split_url(&format!("WSS://h/ws#SHA256={}", HEX.to_uppercase())).unwrap().1.is_some()
        );

        let err = split_url(&format!("ws://h/ws#sha256={HEX}")).unwrap_err();
        assert!(err.contains("wss://"), "{err}");
        let err = split_url("wss://h/ws#top").unwrap_err();
        assert!(err.contains("#sha256="), "{err}");
        assert!(split_url("wss://h/ws#sha256=1234").is_err());

        let pinned = pinned_url("wss://h:1/ws#old", &Pin::of(b"abc"));
        assert_eq!(pinned, format!("wss://h:1/ws#sha256={HEX}"));
        assert_eq!(split_url(&pinned).unwrap().1, Some(Pin::of(b"abc")));
    }

    #[test]
    fn refusals_name_the_presented_fingerprint_and_the_next_step() {
        let seen = Seen::default();
        assert_eq!(explain_refusal("wss://h/ws", None, &seen, false), None);
        let _ = seen.record(b"abc", Ok(()));
        assert_eq!(explain_refusal("wss://h/ws", None, &seen, false), None, "it was accepted");
        let _ = seen.record(b"abc", Err::<(), _>(rustls::Error::NoCertificatesPresented));
        let untrusted = explain_refusal("wss://h:7878/ws", None, &seen, false).unwrap();
        assert!(untrusted.contains("not trusted"), "{untrusted}");
        assert!(untrusted.contains("BA:78:16:BF"), "{untrusted}");
        assert!(untrusted.contains(&format!("wss://h:7878/ws#sha256={HEX}")), "{untrusted}");
        let wrong = explain_refusal("wss://h/ws", Some(Pin::of(b"other")), &seen, false).unwrap();
        assert!(wrong.contains("does not match the pin"), "{wrong}");
        assert!(wrong.contains(HEX), "{wrong}");
    }

    #[test]
    fn configs_build_with_and_without_a_pin() {
        let cert = rcgen::generate_simple_self_signed(vec!["localhost".to_owned()]).unwrap();
        let der = cert.cert.der().clone();
        assert!(client_config(None, &[]).is_ok());
        assert!(client_config(None, std::slice::from_ref(&der)).is_ok());
        let (_, seen) = client_config(Some(Pin::of(&der)), &[]).unwrap();
        assert_eq!(seen.leaf(), None);
    }

    #[test]
    fn the_pinned_verifier_accepts_only_its_certificate() {
        let a = rcgen::generate_simple_self_signed(vec!["a.test".to_owned()]).unwrap();
        let b = rcgen::generate_simple_self_signed(vec!["b.test".to_owned()]).unwrap();
        let seen = Seen::default();
        let v =
            PinnedVerifier { pin: Pin::of(a.cert.der()), provider: provider(), seen: seen.clone() };
        // Any name: with a pin the host name is not checked.
        let name = ServerName::try_from("192.168.1.20").unwrap();
        assert!(v.verify_server_cert(a.cert.der(), &[], &name, &[], UnixTime::now()).is_ok());
        assert_eq!(seen.leaf(), Some(Pin::of(a.cert.der())));
        assert_eq!(seen.refused(), None);
        let err = v.verify_server_cert(b.cert.der(), &[], &name, &[], UnixTime::now()).unwrap_err();
        assert!(matches!(err, rustls::Error::InvalidCertificate(_)), "{err:?}");
        assert_eq!(seen.refused(), Some(Pin::of(b.cert.der())));
    }

    #[test]
    fn without_a_pin_an_unknown_self_signed_certificate_is_refused() {
        let a = rcgen::generate_simple_self_signed(vec!["localhost".to_owned()]).unwrap();
        let seen = Seen::default();
        let v = verifier(None, &[], &provider(), &seen).unwrap();
        let name = ServerName::try_from("localhost").unwrap();
        let r = v.verify_server_cert(a.cert.der(), &[], &name, &[], UnixTime::now());
        assert!(r.is_err());
        assert_eq!(seen.refused(), Some(Pin::of(a.cert.der())));
        // Trusted as an extra root, for its own name only.
        let v = verifier(None, &[a.cert.der().clone()], &provider(), &seen).unwrap();
        assert!(v.verify_server_cert(a.cert.der(), &[], &name, &[], UnixTime::now()).is_ok());
        let other = ServerName::try_from("example.com").unwrap();
        assert!(v.verify_server_cert(a.cert.der(), &[], &other, &[], UnixTime::now()).is_err());
    }
}
