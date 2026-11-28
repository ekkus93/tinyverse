using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tinyverse.Host.Internal;
using UnityEngine;

namespace Tinyverse.Host
{
    /// <summary>A ready-made place a new world can start from, as the server describes it.</summary>
    public sealed class WorldTemplate
    {
        public WorldTemplate(string name, string title, string description, int objectCount)
        {
            Name = name ?? "";
            Title = string.IsNullOrEmpty(title) ? Name : title;
            Description = description ?? "";
            ObjectCount = objectCount;
        }

        /// <summary>What <c>init --template</c> takes, such as <c>island</c>.</summary>
        public string Name { get; }

        /// <summary>A short display name, such as "Island".</summary>
        public string Title { get; }

        public string Description { get; }

        /// <summary>How many objects a world made from it starts with.</summary>
        public int ObjectCount { get; }

        public override string ToString() => $"{Name} ({Title}, {ObjectCount} objects)";
    }

    /// <summary>
    /// The templates the bundled server knows, from <c>tinyverse-server template list --json</c>, so the client
    /// never hard-codes their names.
    /// </summary>
    public static class WorldTemplates
    {
        /// <summary>The template a world gets when none is chosen.</summary>
        public const string DefaultName = "empty";

        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Runs the server binary's <c>template list --json</c> and parses what it prints. Throws
        /// <see cref="HostException"/> (<see cref="HostErrorKind.LaunchFailed"/>) when it cannot run, fails or
        /// prints nothing usable.
        /// </summary>
        public static async Task<IReadOnlyList<WorldTemplate>> ListAsync(string serverBinaryPath, TimeSpan? timeout = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(serverBinaryPath)) throw new ArgumentException("A server binary is required.", nameof(serverBinaryPath));
            var lines = new List<string>();
            ChildProcess child;
            try
            {
                child = await ChildProcess.Launch(serverBinaryPath, new[] { "template", "list", "--json" }, null,
                    l => { lock (lines) lines.Add(l); }, "tinyverse-server-templates").ConfigureAwait(false);
            }
            catch (Exception e)
            {
                throw new HostException(HostErrorKind.LaunchFailed, $"Could not ask the server for its templates: {e.Message}");
            }
            var wait = Task.Delay(timeout ?? DefaultTimeout, cancellationToken);
            if (await Task.WhenAny(child.Exited, wait).ConfigureAwait(false) != child.Exited)
            {
                child.Kill();
                cancellationToken.ThrowIfCancellationRequested();
                throw new HostException(HostErrorKind.LaunchFailed, "The server did not list its templates in time.");
            }
            List<string> output;
            lock (lines) output = new List<string>(lines);
            int code = await child.Exited.ConfigureAwait(false);
            if (code != 0)
                throw new HostException(HostErrorKind.LaunchFailed,
                    $"The server could not list its templates (exit code {code}).", output, code);
            foreach (string l in output)
            {
                string t = (l ?? "").Trim();
                if (t.StartsWith("{", StringComparison.Ordinal)) return Parse(t);
            }
            throw new HostException(HostErrorKind.LaunchFailed, "The server printed no template list.", output);
        }

        /// <summary>
        /// Parses <c>{"templates": [{"name", "title", "description", "objects"}, ...]}</c>, keeping the server's
        /// order and skipping entries without a name. Throws <see cref="FormatException"/> on invalid JSON.
        /// </summary>
        public static IReadOnlyList<WorldTemplate> Parse(string json)
        {
            ListJson parsed;
            try { parsed = JsonUtility.FromJson<ListJson>(json ?? ""); }
            catch (Exception e) { throw new FormatException("Not a template list: " + e.Message, e); }
            var result = new List<WorldTemplate>();
            if (parsed?.templates == null) return result;
            foreach (var t in parsed.templates)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.name)) continue;
                result.Add(new WorldTemplate(t.name.Trim(), t.title?.Trim(), t.description?.Trim(), Math.Max(0, t.objects)));
            }
            return result;
        }

#pragma warning disable 0649 // assigned by JsonUtility
        [Serializable]
        private sealed class ListJson
        {
            public ItemJson[] templates;
        }

        [Serializable]
        private sealed class ItemJson
        {
            public string name;
            public string title;
            public string description;
            public int objects;
        }
#pragma warning restore 0649
    }
}
