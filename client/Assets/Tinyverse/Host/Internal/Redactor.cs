using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Tinyverse.Host.Internal
{
    /// <summary>
    /// Removes secrets from text before it reaches a log line, the ring or an exception message:
    /// known values (password, owner token) and anything shaped like <c>token: xyz</c> / <c>password=xyz</c>.
    /// Also strips ANSI colour codes. Thread-safe.
    /// </summary>
    internal sealed class Redactor
    {
        public const string Mask = "[redacted]";

        private static readonly Regex Labelled = new Regex(
            @"(?i)\b(token|password|passwd|secret)(\s*[:=]\s*|\s+)(""[^""]*""|'[^']*'|\S+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

        private readonly object _lock = new object();
        private readonly List<string> _secrets = new List<string>();

        public void Add(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return;
            lock (_lock)
            {
                if (_secrets.Contains(secret)) return;
                _secrets.Add(secret);
                // Longest first, so a secret containing another is masked whole.
                _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
            }
        }

        public string Apply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            text = Ansi.Replace(text, "");
            lock (_lock)
            {
                foreach (string s in _secrets)
                    text = text.Replace(s, Mask);
            }
            return Labelled.Replace(text, m =>
            {
                string word = m.Groups[1].Value;
                // "token create" / "password set" style prose is not a secret; only mask what follows a separator
                // or a value that looks random (long, no spaces).
                string value = m.Groups[3].Value;
                bool separated = m.Groups[2].Value.Trim().Length > 0;
                if (!separated && !LooksRandom(value)) return m.Value;
                return word + m.Groups[2].Value + Mask;
            });
        }

        private static bool LooksRandom(string value)
        {
            if (value.Length < 16) return false;
            int letters = 0, digits = 0;
            foreach (char c in value)
            {
                if (char.IsLetter(c)) letters++;
                else if (char.IsDigit(c)) digits++;
            }
            return letters > 0 && digits > 0;
        }
    }
}
