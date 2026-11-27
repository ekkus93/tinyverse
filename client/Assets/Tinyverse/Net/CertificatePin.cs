using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Tinyverse.Net
{
    /// <summary>
    /// Certificate pins in world addresses: <c>wss://192.168.1.20:7878/ws#sha256=&lt;64 hex digits&gt;</c>.
    /// The pin is the SHA-256 of the server's leaf certificate (its DER bytes). With a pin the connection
    /// succeeds only if the server presents exactly that certificate; the host name and the chain are
    /// then not checked. Without one the system's normal certificate checks apply. The fragment never
    /// leaves the client. Same rules as the Rust <c>tv-tls</c> crate.
    /// </summary>
    public static class CertificatePin
    {
        /// <summary>The fragment key: <c>#sha256=</c>.</summary>
        public const string FragmentPrefix = "sha256=";

        /// <summary>
        /// Reads <c>sha256=&lt;hex&gt;</c> or bare hex (64 digits, either case, colons allowed) into
        /// lower-case hex without separators.
        /// </summary>
        public static bool TryParse(string text, out string hex)
        {
            hex = null;
            if (text == null) return false;
            string t = text.Trim();
            if (t.StartsWith(FragmentPrefix, StringComparison.OrdinalIgnoreCase)) t = t.Substring(FragmentPrefix.Length);
            var sb = new StringBuilder(64);
            foreach (char c in t)
            {
                if (c == ':') continue;
                if (!Uri.IsHexDigit(c)) return false;
                sb.Append(char.ToLowerInvariant(c));
            }
            if (sb.Length != 64) return false;
            hex = sb.ToString();
            return true;
        }

        /// <summary>The pin of a DER certificate, as lower-case hex.</summary>
        public static string Sha256Hex(byte[] der)
        {
            if (der == null) throw new ArgumentNullException(nameof(der));
            using (var sha = SHA256.Create())
            {
                var digest = sha.ComputeHash(der);
                var sb = new StringBuilder(64);
                foreach (byte b in digest) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary><c>AB:CD:…</c>, the form people compare by eye. Returns the input when it is not a pin.</summary>
        public static string Colons(string hex)
        {
            if (!TryParse(hex, out var h)) return hex ?? "";
            var sb = new StringBuilder(95);
            for (int i = 0; i < h.Length; i += 2)
            {
                if (i > 0) sb.Append(':');
                sb.Append(char.ToUpperInvariant(h[i])).Append(char.ToUpperInvariant(h[i + 1]));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Splits a URL into the part before <c>#</c> and its pin (null when there is no fragment).
        /// Returns false with a sentence in <paramref name="error"/> for any other fragment, a malformed
        /// pin, or a pin on a URL that is not <c>wss://</c>.
        /// </summary>
        public static bool TrySplit(string url, out string baseUrl, out string pinHex, out string error)
        {
            baseUrl = url ?? "";
            pinHex = null;
            error = null;
            int hash = baseUrl.IndexOf('#');
            if (hash < 0) return true;
            string fragment = baseUrl.Substring(hash + 1);
            baseUrl = baseUrl.Substring(0, hash);
            if (!fragment.StartsWith(FragmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "The only thing that may follow '#' in a world address is a certificate fingerprint, #sha256=….";
                return false;
            }
            if (!TryParse(fragment, out pinHex))
            {
                error = "The #sha256= fingerprint in the address must be 64 hex digits.";
                return false;
            }
            if (!baseUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                error = "A #sha256= certificate fingerprint only applies to wss:// addresses.";
                pinHex = null;
                return false;
            }
            return true;
        }

        /// <summary>The URL without its fragment.</summary>
        public static string Strip(string url)
        {
            if (url == null) return null;
            int hash = url.IndexOf('#');
            return hash < 0 ? url : url.Substring(0, hash);
        }

        /// <summary>The pin in a URL, or null.</summary>
        public static string PinOf(string url) =>
            TrySplit(url, out _, out var pin, out _) ? pin : null;

        /// <summary><paramref name="url"/> (any fragment dropped) with the pin as its fragment.</summary>
        public static string WithPin(string url, string pinHex)
        {
            if (!TryParse(pinHex, out var hex)) throw new ArgumentException("Not a SHA-256 fingerprint.", nameof(pinHex));
            return Strip(url) + "#" + FragmentPrefix + hex;
        }

        /// <summary>The <c>host:port</c> a pin is remembered under, e.g. <c>192.168.1.20:7878</c> or <c>[::1]:7878</c>.</summary>
        public static string HostPortKey(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            string host = uri.Host.ToLowerInvariant();
            if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith("[", StringComparison.Ordinal)) host = "[" + host + "]";
            return host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary><see cref="HostPortKey(Uri)"/> of a URL string, or null if it is not absolute.</summary>
        public static string HostPortKey(string url) =>
            Uri.TryCreate(Strip(url), UriKind.Absolute, out var u) ? HostPortKey(u) : null;
    }
}
