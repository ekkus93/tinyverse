using System;
using System.Globalization;

namespace Tinyverse.UI
{
    /// <summary>Pure parsing and validation shared by the forms.</summary>
    public static class Validation
    {
        public const int MaxDisplayNameLength = 32;
        public const int MaxWorldNameLength = 64;
        public const int MaxObjectNameLength = 64;

        /// <summary>
        /// Parses a number typed by the user. Accepts a dot or a comma as the decimal separator
        /// and surrounding whitespace; rejects NaN, infinity and anything else.
        /// </summary>
        public static bool TryParseFloat(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim().Replace(',', '.');
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>Formats a number for a field: invariant culture, at most 3 decimals, no trailing zeros.</summary>
        public static string FormatFloat(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>Parses a TCP port, 1 to 65535.</summary>
        public static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }

        /// <summary>Null when valid, else a message for the user.</summary>
        public static string ValidateDisplayName(string name)
        {
            string n = name?.Trim() ?? "";
            if (n.Length == 0) return "Enter a display name.";
            if (n.Length > MaxDisplayNameLength) return $"Display name is too long (at most {MaxDisplayNameLength} characters).";
            return null;
        }

        /// <summary>
        /// Null when the address is valid, else a message. Accepts <c>host</c>, <c>host:port</c>,
        /// <c>[ipv6]:port</c>, and <c>ws://</c> or <c>wss://</c> URLs.
        /// </summary>
        public static string ValidateAddress(string address)
        {
            string a = address?.Trim() ?? "";
            if (a.Length == 0) return "Enter the address of a world, like 192.168.1.20:7878.";
            if (a.IndexOf(' ') >= 0) return "The address cannot contain spaces.";
            if (a.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || a.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(a, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? null : "That URL is not valid.";
            }
            if (a.Contains("://")) return "Use host:port, or a ws:// or wss:// URL.";

            string host = a, portText = null;
            if (a.StartsWith("[", StringComparison.Ordinal))
            {
                int close = a.IndexOf(']');
                if (close < 0) return "Missing ] in the IPv6 address.";
                host = a.Substring(1, close - 1);
                string rest = a.Substring(close + 1);
                if (rest.Length > 0)
                {
                    if (rest[0] != ':') return "Expected :port after the IPv6 address.";
                    portText = rest.Substring(1);
                }
            }
            else
            {
                int colon = a.LastIndexOf(':');
                if (colon >= 0)
                {
                    if (a.IndexOf(':') != colon) return "Put IPv6 addresses in brackets, like [::1]:7878.";
                    host = a.Substring(0, colon);
                    portText = a.Substring(colon + 1);
                }
            }
            if (host.Length == 0) return "The address has no host name.";
            foreach (char ch in host)
            {
                if (!(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' || ch == ':')) return $"The host name cannot contain '{ch}'.";
            }
            if (portText != null && !TryParsePort(portText, out _)) return "The port must be a number from 1 to 65535.";
            return null;
        }

        /// <summary>Returns the first problem with the host form, or null.</summary>
        public static string ValidateHost(HostSettings s, out string field)
        {
            field = "worldName";
            string name = s.WorldName?.Trim() ?? "";
            if (name.Length == 0) return "Give the world a name.";
            if (name.Length > MaxWorldNameLength) return $"The world name is too long (at most {MaxWorldNameLength} characters).";
            field = "worldFolder";
            if (string.IsNullOrWhiteSpace(s.WorldFolder)) return "Choose a folder for the world.";
            field = "port";
            if (s.Port < 1 || s.Port > 65535) return "The port must be a number from 1 to 65535.";
            field = "displayName";
            string nameError = ValidateDisplayName(s.DisplayName);
            if (nameError != null) return nameError;
            field = null;
            return null;
        }

        /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", "4 days ago" or a date.</summary>
        public static string RelativeTime(DateTime then, DateTime now)
        {
            if (then == default) return "";
            TimeSpan d = now - then;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
            if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
            if (d.TotalDays < 2) return "yesterday";
            if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
            return then.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }
    }
}
