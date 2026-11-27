using System;
using System.Globalization;

namespace Tinyverse.Net
{
    /// <summary>
    /// Turns what a person types into a canonical WebSocket URL:
    /// <c>192.168.1.5</c> → <c>ws://192.168.1.5:7878/ws</c>, <c>myhost:9000</c> → <c>ws://myhost:9000/ws</c>,
    /// <c>ws://host/ws</c> → <c>ws://host:7878/ws</c>. The scheme <c>wss</c> is kept;
    /// <c>http</c>/<c>https</c> are read as <c>ws</c>/<c>wss</c>. Default port 7878 for both.
    /// An empty path becomes <c>/ws</c>; any other path is kept.
    /// </summary>
    public static class WorldAddress
    {
        public const int DefaultPort = 7878;
        public const string DefaultPath = "/ws";

        /// <summary>Returns false with a sentence for the person in <paramref name="error"/>.</summary>
        public static bool TryNormalize(string input, out string url, out string error)
        {
            url = null;
            error = null;
            string s = input?.Trim() ?? "";
            if (s.Length == 0) return Fail("Enter a world address, such as 192.168.1.5 or myhost:7878.", out error);
            foreach (char c in s)
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                    return Fail("A world address cannot contain spaces.", out error);

            string scheme = "ws";
            int sep = s.IndexOf("://", StringComparison.Ordinal);
            if (sep >= 0)
            {
                string given = s.Substring(0, sep).ToLowerInvariant();
                switch (given)
                {
                    case "ws":
                    case "http":
                        scheme = "ws";
                        break;
                    case "wss":
                    case "https":
                        scheme = "wss";
                        break;
                    default:
                        return Fail($"\"{given}://\" is not supported; use ws:// or leave it out.", out error);
                }
                s = s.Substring(sep + 3);
            }

            if (s.IndexOfAny(new[] { '?', '#' }) >= 0)
                return Fail("A world address cannot contain '?' or '#'.", out error);

            int slash = s.IndexOf('/');
            string authority = slash >= 0 ? s.Substring(0, slash) : s;
            string path = slash >= 0 ? s.Substring(slash) : "";
            if (authority.IndexOf('@') >= 0) return Fail("A world address cannot contain a user name ('@').", out error);

            string host;
            string portText = null;
            if (authority.StartsWith("[", StringComparison.Ordinal))
            {
                int close = authority.IndexOf(']');
                if (close < 0) return Fail("An IPv6 address is missing its closing ']'.", out error);
                host = authority.Substring(0, close + 1);
                string rest = authority.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':') return Fail($"Unexpected text after the address: \"{rest}\".", out error);
                    portText = rest.Substring(1);
                }
                if (Uri.CheckHostName(host.Substring(1, host.Length - 2)) != UriHostNameType.IPv6)
                    return Fail($"\"{host}\" is not a valid IPv6 address.", out error);
            }
            else
            {
                int colon = authority.IndexOf(':');
                if (colon >= 0 && authority.IndexOf(':', colon + 1) >= 0)
                    return Fail("Put an IPv6 address in brackets, e.g. [::1]:7878.", out error);
                host = colon >= 0 ? authority.Substring(0, colon) : authority;
                if (colon >= 0) portText = authority.Substring(colon + 1);
                if (host.Length == 0) return Fail("The address has no host name.", out error);
                var kind = Uri.CheckHostName(host);
                if (kind != UriHostNameType.Dns && kind != UriHostNameType.IPv4)
                    return Fail($"\"{host}\" is not a valid host name or IP address.", out error);
            }

            int port = DefaultPort;
            if (portText != null)
            {
                if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) ||
                    port < 1 || port > 65535)
                    return Fail($"\"{portText}\" is not a port number (1 to 65535).", out error);
            }

            if (path.Length == 0 || path == "/") path = DefaultPath;
            url = $"{scheme}://{host.ToLowerInvariant()}:{port.ToString(CultureInfo.InvariantCulture)}{path}";
            return true;
        }

        /// <summary>Like <see cref="TryNormalize"/> but throws <see cref="FormatException"/>.</summary>
        public static string Normalize(string input)
        {
            if (!TryNormalize(input, out var url, out var error)) throw new FormatException(error);
            return url;
        }

        static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
