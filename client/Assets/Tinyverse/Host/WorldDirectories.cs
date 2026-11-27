using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tinyverse.Host
{
    /// <summary>A world found on disk.</summary>
    public sealed class WorldInfo
    {
        internal WorldInfo(string directory, string name, bool nameFromFile)
        {
            Directory = directory;
            Name = name;
            NameFromFile = nameFromFile;
        }

        /// <summary>Full path of the world directory.</summary>
        public string Directory { get; }

        /// <summary>The <c>name</c> from <c>world.toml</c>, or the folder name if it has none.</summary>
        public string Name { get; }

        /// <summary>False if <see cref="Name"/> fell back to the folder name.</summary>
        public bool NameFromFile { get; }

        public override string ToString() => $"{Name} ({Directory})";
    }

    /// <summary>Where worlds live by default, which ones exist, and how to name a new one's folder.</summary>
    public static class WorldDirectories
    {
        public const string WorldFile = "world.toml";
        public const string DefaultFolderName = "worlds";
        private const int MaxFolderNameLength = 64;

        private static readonly HashSet<string> WindowsReserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
            "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        };

        /// <summary>The default folder for worlds: <c>&lt;baseDirectory&gt;/worlds</c> (the glue passes <c>Application.persistentDataPath</c>).</summary>
        public static string DefaultRoot(string baseDirectory)
        {
            if (string.IsNullOrEmpty(baseDirectory)) throw new ArgumentException("A base directory is required.", nameof(baseDirectory));
            return Path.Combine(Path.GetFullPath(baseDirectory), DefaultFolderName);
        }

        /// <summary>A directory is a world if it contains <c>world.toml</c>.</summary>
        public static bool IsWorld(string directory) =>
            !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, WorldFile));

        /// <summary>The worlds directly under <paramref name="root"/>, sorted by name. Empty if the folder does not exist.</summary>
        public static IReadOnlyList<WorldInfo> List(string root)
        {
            var result = new List<WorldInfo>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return result;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception) { return result; }
            foreach (string d in dirs)
            {
                if (!IsWorld(d)) continue;
                string name = ReadName(d);
                string full = Path.GetFullPath(d);
                result.Add(name != null ? new WorldInfo(full, name, true) : new WorldInfo(full, Path.GetFileName(full), false));
            }
            result.Sort((a, b) =>
            {
                int c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(a.Directory, b.Directory);
            });
            return result;
        }

        /// <summary>The top-level <c>name</c> from <c>world.toml</c>, or null if missing or unreadable.</summary>
        public static string ReadName(string worldDirectory)
        {
            try
            {
                string path = Path.Combine(worldDirectory, WorldFile);
                return File.Exists(path) ? ParseName(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// A tolerant line parser for <c>name = "..."</c> in the root table of a TOML document. Handles basic and
        /// literal strings, common escapes, comments and whitespace; stops at the first <c>[table]</c>.
        /// </summary>
        public static string ParseName(string toml)
        {
            if (toml == null) return null;
            foreach (string rawLine in toml.Split('\n'))
            {
                string line = rawLine.Trim().TrimStart('﻿');
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[') return null; // left the root table
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().Trim('"', '\'');
                if (key != "name") continue;
                string value = line.Substring(eq + 1).Trim();
                string parsed = ParseString(value);
                if (parsed != null) return parsed.Trim().Length == 0 ? null : parsed;
            }
            return null;
        }

        private static string ParseString(string v)
        {
            if (v.Length == 0) return null;
            char q = v[0];
            if (q == '\'')
            {
                int end = v.IndexOf('\'', 1);
                return end < 0 ? null : v.Substring(1, end - 1);
            }
            if (q != '"') return null;
            var sb = new StringBuilder();
            for (int i = 1; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '"') return sb.ToString();
                if (c != '\\' || i + 1 >= v.Length) { sb.Append(c); continue; }
                char e = v[++i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'u':
                    case 'U':
                        int len = e == 'u' ? 4 : 8;
                        if (i + len < v.Length && int.TryParse(v.Substring(i + 1, len), System.Globalization.NumberStyles.HexNumber, null, out int cp))
                        {
                            try { sb.Append(char.ConvertFromUtf32(cp)); } catch (ArgumentOutOfRangeException) { sb.Append('�'); }
                            i += len;
                        }
                        else sb.Append(e);
                        break;
                    default: sb.Append(e); break;
                }
            }
            return null; // unterminated
        }

        /// <summary>
        /// Turns a world name into a folder name that is safe on Linux, macOS and Windows: ASCII letters and digits
        /// kept (lower-cased), everything else collapsed to single hyphens, at most 64 characters, never empty
        /// ("world"), never a reserved device name.
        /// </summary>
        public static string SafeFolderName(string worldName)
        {
            var sb = new StringBuilder();
            bool pendingHyphen = false;
            foreach (char ch in (worldName ?? "").Normalize(NormalizationForm.FormD))
            {
                // Strip accents: decomposed combining marks are dropped.
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark)
                    continue;
                char c = char.ToLowerInvariant(ch);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    bool hyphen = pendingHyphen && sb.Length > 0;
                    if (sb.Length + (hyphen ? 2 : 1) > MaxFolderNameLength) break;
                    if (hyphen) sb.Append('-');
                    pendingHyphen = false;
                    sb.Append(c);
                }
                else
                {
                    pendingHyphen = true;
                }
            }
            string name = sb.ToString().Trim('-');
            if (name.Length == 0) name = "world";
            if (WindowsReserved.Contains(name)) name += "-world";
            return name;
        }

        /// <summary>
        /// A path under <paramref name="root"/> for a new world called <paramref name="worldName"/> that does not
        /// exist yet: <c>my-world</c>, then <c>my-world-2</c>, <c>my-world-3</c>, ... Creates nothing.
        /// </summary>
        public static string NewWorldDirectory(string root, string worldName)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("A root directory is required.", nameof(root));
            string baseName = SafeFolderName(worldName);
            string candidate = Path.Combine(Path.GetFullPath(root), baseName);
            for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++)
                candidate = Path.Combine(Path.GetFullPath(root), baseName + "-" + n);
            return candidate;
        }
    }
}
