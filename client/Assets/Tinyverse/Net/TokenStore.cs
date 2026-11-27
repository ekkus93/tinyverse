using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Tinyverse.Net
{
    /// <summary>What is remembered about one world.</summary>
    public sealed class WorldRecord
    {
        /// <summary>Canonical URL (see <see cref="WorldAddress"/>); the key.</summary>
        public string Url { get; }

        /// <summary>Empty if none is stored.</summary>
        public string SessionToken { get; }

        public string DisplayName { get; }
        public DateTimeOffset LastConnected { get; }

        public WorldRecord(string url, string sessionToken, string displayName, DateTimeOffset lastConnected)
        {
            Url = url ?? "";
            SessionToken = sessionToken ?? "";
            DisplayName = displayName ?? "";
            LastConnected = lastConnected;
        }
    }

    /// <summary>Per-world session tokens and the "recent worlds" list.</summary>
    public interface ITokenStore
    {
        /// <summary>The record for a world, or null. The URL is normalised first.</summary>
        WorldRecord Get(string worldUrl);

        /// <summary>All remembered worlds, most recently connected first.</summary>
        IReadOnlyList<WorldRecord> GetRecentWorlds();

        /// <summary>
        /// Records a successful join. An empty or null <paramref name="sessionToken"/> keeps the
        /// token already stored.
        /// </summary>
        void Remember(string worldUrl, string sessionToken, string displayName, DateTimeOffset connectedAt);

        /// <summary>Drops the stored token but keeps the world in the recent list (e.g. after INVALID_TOKEN).</summary>
        void ForgetToken(string worldUrl);

        /// <summary>Removes the world entirely.</summary>
        void Remove(string worldUrl);
    }

    /// <summary>
    /// <see cref="ITokenStore"/> in a JSON file (<c>worlds.json</c>) in a caller-supplied directory,
    /// e.g. <c>Application.persistentDataPath</c>. The file holds secrets, so on Linux and macOS it
    /// is created with mode 0600. A missing or corrupt file reads as empty. Thread-safe within one process.
    /// </summary>
    public sealed class FileTokenStore : ITokenStore
    {
        public const string DefaultFileName = "worlds.json";

        readonly object gate = new object();
        readonly string path;
        List<WorldRecord> records;

        public FileTokenStore(string directory, string fileName = DefaultFileName)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentException("A directory is required.", nameof(directory));
            path = Path.Combine(directory, fileName ?? DefaultFileName);
        }

        /// <summary>Full path of the JSON file.</summary>
        public string FilePath => path;

        public WorldRecord Get(string worldUrl)
        {
            string key = Key(worldUrl);
            lock (gate) return Load().FirstOrDefault(r => r.Url == key);
        }

        public IReadOnlyList<WorldRecord> GetRecentWorlds()
        {
            lock (gate) return Load().OrderByDescending(r => r.LastConnected).ToList();
        }

        public void Remember(string worldUrl, string sessionToken, string displayName, DateTimeOffset connectedAt)
        {
            string key = Key(worldUrl);
            lock (gate)
            {
                var list = Load();
                int i = list.FindIndex(r => r.Url == key);
                string token = string.IsNullOrEmpty(sessionToken) && i >= 0 ? list[i].SessionToken : sessionToken;
                var record = new WorldRecord(key, token, displayName, connectedAt);
                if (i >= 0) list[i] = record;
                else list.Add(record);
                Save(list);
            }
        }

        public void ForgetToken(string worldUrl)
        {
            string key = Key(worldUrl);
            lock (gate)
            {
                var list = Load();
                int i = list.FindIndex(r => r.Url == key);
                if (i < 0) return;
                list[i] = new WorldRecord(key, "", list[i].DisplayName, list[i].LastConnected);
                Save(list);
            }
        }

        public void Remove(string worldUrl)
        {
            string key = Key(worldUrl);
            lock (gate)
            {
                var list = Load();
                if (list.RemoveAll(r => r.Url == key) > 0) Save(list);
            }
        }

        static string Key(string worldUrl) =>
            WorldAddress.TryNormalize(worldUrl, out var url, out _) ? url : (worldUrl ?? "").Trim();

        List<WorldRecord> Load()
        {
            if (records != null) return records;
            records = new List<WorldRecord>();
            try
            {
                if (!File.Exists(path)) return records;
                var file = UnityEngine.JsonUtility.FromJson<FileDto>(File.ReadAllText(path, Encoding.UTF8));
                if (file?.worlds == null) return records;
                foreach (var w in file.worlds)
                {
                    if (w == null || string.IsNullOrEmpty(w.url)) continue;
                    DateTimeOffset when;
                    try { when = DateTimeOffset.FromUnixTimeMilliseconds(w.lastConnectedUnixMs); }
                    catch (ArgumentOutOfRangeException) { when = DateTimeOffset.FromUnixTimeMilliseconds(0); }
                    records.RemoveAll(r => r.Url == w.url);
                    records.Add(new WorldRecord(w.url, w.sessionToken, w.displayName, when));
                }
            }
            catch (Exception)
            {
                records.Clear(); // Corrupt or unreadable: treat as empty.
            }
            return records;
        }

        void Save(List<WorldRecord> list)
        {
            records = list;
            var dto = new FileDto
            {
                version = 1,
                worlds = list.Select(r => new WorldDto
                {
                    url = r.Url,
                    sessionToken = r.SessionToken,
                    displayName = r.DisplayName,
                    lastConnectedUnixMs = r.LastConnected.ToUnixTimeMilliseconds(),
                }).ToList(),
            };
            string json = UnityEngine.JsonUtility.ToJson(dto, true);

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            // Create empty, restrict, then write, so the secret never sits in a world-readable file.
            using (File.Create(tmp)) { }
            RestrictToOwner(tmp);
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(tmp, path, null);
                }
                catch (Exception)
                {
                    File.Delete(path);
                    File.Move(tmp, path);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
            RestrictToOwner(path);
        }

        /// <summary>chmod 0600 on Linux and macOS; a no-op elsewhere or if it fails.</summary>
        internal static void RestrictToOwner(string file)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return;
            const uint mode = 0x180; // 0600
            try
            {
                chmod(file, mode);
            }
            catch (Exception)
            {
                try { chmod_so6(file, mode); } catch (Exception) { /* best effort */ }
            }
        }

        [DllImport("libc", SetLastError = true)]
        static extern int chmod(string pathname, uint mode);

        [DllImport("libc.so.6", EntryPoint = "chmod", SetLastError = true)]
        static extern int chmod_so6(string pathname, uint mode);

        [Serializable]
        internal sealed class FileDto
        {
            public int version;
            public List<WorldDto> worlds;
        }

        [Serializable]
        internal sealed class WorldDto
        {
            public string url;
            public string sessionToken;
            public string displayName;
            public long lastConnectedUnixMs;
        }
    }
}
