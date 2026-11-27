using System;
using System.Collections.Generic;
using Tinyverse.Net;

namespace Tinyverse.Bootstrap
{
    /// <summary>One trusted certificate: the SHA-256 (lower-case hex) a world at <c>host:port</c> presented.</summary>
    [Serializable]
    public sealed class KnownCertificate
    {
        public string hostPort = "";
        public string sha256 = "";
    }

    /// <summary>
    /// Trust on first use for <c>wss://</c> worlds: the certificate fingerprints the player accepted (or
    /// joined with by a pinned address), one per <c>host:port</c>, kept in <c>settings.json</c>. A remembered
    /// fingerprint is pinned on every later join of that address, so a different certificate is refused.
    /// </summary>
    public static class KnownCertificates
    {
        /// <summary>The remembered pin for the host and port of <paramref name="url"/>, or null.</summary>
        public static string PinFor(this StoredSettings s, string url)
        {
            string key = CertificatePin.HostPortKey(url);
            if (key == null || s.certificates == null) return null;
            var known = s.certificates.Find(c => c.hostPort == key);
            return known != null && CertificatePin.TryParse(known.sha256, out var hex) ? hex : null;
        }

        /// <summary>Remembers <paramref name="sha256"/> for the host and port of <paramref name="url"/>.</summary>
        public static void RememberPin(this StoredSettings s, string url, string sha256)
        {
            string key = CertificatePin.HostPortKey(url);
            if (key == null || !CertificatePin.TryParse(sha256, out var hex)) return;
            s.certificates ??= new List<KnownCertificate>();
            var known = s.certificates.Find(c => c.hostPort == key);
            if (known == null) s.certificates.Add(new KnownCertificate { hostPort = key, sha256 = hex });
            else known.sha256 = hex;
        }

        /// <summary>Forgets the pin for the host and port of <paramref name="url"/>. True if there was one.</summary>
        public static bool ForgetPin(this StoredSettings s, string url)
        {
            string key = CertificatePin.HostPortKey(url);
            return key != null && s.certificates != null && s.certificates.RemoveAll(c => c.hostPort == key) > 0;
        }
    }
}
