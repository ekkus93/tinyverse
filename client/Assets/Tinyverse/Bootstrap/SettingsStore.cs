using System;
using System.IO;
using System.Text;
using UnityEngine;
using UI = Tinyverse.UI;

namespace Tinyverse.Bootstrap
{
    /// <summary>What is persisted in <c>settings.json</c>.</summary>
    [Serializable]
    public sealed class StoredSettings
    {
        public int version = 1;
        public UI.ClientSettings client = UI.ClientSettings.Default;
        /// <summary>Last name typed on the Connect or Host screen.</summary>
        public string lastName = "";
        /// <summary>Last address typed on the Connect screen.</summary>
        public string lastAddress = "";
        /// <summary>Optional chosen body colour as <c>#rrggbb</c>; empty means "derived from my user id".</summary>
        public string bodyColor = "";
        /// <summary>The name each visited world gave in its Welcome, for the recent-worlds list.</summary>
        public System.Collections.Generic.List<KnownWorld> worlds = new System.Collections.Generic.List<KnownWorld>();
        /// <summary>Trusted certificate fingerprints per host:port (see <see cref="KnownCertificates"/>).</summary>
        public System.Collections.Generic.List<KnownCertificate> certificates = new System.Collections.Generic.List<KnownCertificate>();

        public string WorldName(string url) => worlds?.Find(w => w.url == url)?.name ?? "";

        public void SetWorldName(string url, string name)
        {
            worlds ??= new System.Collections.Generic.List<KnownWorld>();
            var w = worlds.Find(x => x.url == url);
            if (w == null) worlds.Add(new KnownWorld { url = url, name = name ?? "" });
            else w.name = name ?? "";
        }
    }

    [Serializable]
    public sealed class KnownWorld
    {
        public string url = "";
        public string name = "";
    }

    /// <summary>
    /// Client settings as JSON in a directory (normally <c>Application.persistentDataPath</c>). A
    /// missing or corrupt file reads as defaults; values are clamped to the ranges the UI offers.
    /// </summary>
    public sealed class SettingsStore
    {
        public const string FileName = "settings.json";

        readonly string path;

        public SettingsStore(string directory)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
            path = Path.Combine(directory, FileName);
        }

        public string FilePath => path;

        public StoredSettings Current { get; private set; } = new StoredSettings();

        public StoredSettings Load()
        {
            var s = new StoredSettings();
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonUtility.FromJson<StoredSettings>(File.ReadAllText(path, Encoding.UTF8));
                    if (loaded != null) s = loaded;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Settings] {path} is unreadable ({e.Message}); using defaults.");
                s = new StoredSettings();
            }
            s.client = Sanitise(s.client);
            s.lastName ??= "";
            s.lastAddress ??= "";
            s.bodyColor ??= "";
            s.worlds ??= new System.Collections.Generic.List<KnownWorld>();
            s.certificates ??= new System.Collections.Generic.List<KnownCertificate>();
            Current = s;
            return s;
        }

        public void Save(StoredSettings s)
        {
            s.client = Sanitise(s.client);
            Current = s;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonUtility.ToJson(s, true), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Settings] could not save {path}: {e.Message}");
            }
        }

        public void Save() => Save(Current);

        /// <summary>Clamps every value into the range the settings panel offers; replaces NaN with the default.</summary>
        public static UI.ClientSettings Sanitise(UI.ClientSettings c)
        {
            var d = UI.ClientSettings.Default;
            c.MouseSensitivity = Clamp(c.MouseSensitivity, UI.SettingsPanel.MinSensitivity, UI.SettingsPanel.MaxSensitivity, d.MouseSensitivity);
            c.FieldOfView = Clamp(c.FieldOfView, UI.SettingsPanel.MinFov, UI.SettingsPanel.MaxFov, d.FieldOfView);
            c.MasterVolume = Clamp(c.MasterVolume, 0f, 1f, d.MasterVolume);
            c.UIScale = Clamp(c.UIScale, UI.SettingsPanel.MinUIScale, UI.SettingsPanel.MaxUIScale, d.UIScale);
            if (c.QualityIndex < 0) c.QualityIndex = 0;
            return c;
        }

        static float Clamp(float v, float min, float max, float fallback)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || v == 0f && min > 0f) return fallback;
            return Mathf.Clamp(v, min, max);
        }

        /// <summary>The chosen body colour, or null for "derived from the user id".</summary>
        public static Color? ParseBodyColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            string h = hex.Trim();
            if (!h.StartsWith("#", StringComparison.Ordinal)) h = "#" + h;
            return ColorUtility.TryParseHtmlString(h, out var c) ? c : (Color?)null;
        }
    }
}
