using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Hosting a world: the form for the bundled server, Start / Stop, its status and log, and the
    /// address to give friends. The glue runs the process and reports back with the setters.
    /// </summary>
    public sealed class HostScreen : UIPanel
    {
        public const int MaxLogLines = 500;

        readonly TextField _worldName;
        readonly TextField _description;
        readonly TextField _folder;
        readonly TextField _port;
        readonly TextField _password;
        readonly TextField _displayName;
        readonly Button _start;
        readonly Button _stop;
        readonly Button _back;
        readonly Button _copy;
        readonly Label _status;
        readonly VisualElement _statusDot;
        readonly Label _error;
        readonly Label _shareAddress;
        readonly VisualElement _share;
        readonly ScrollView _log;
        readonly VisualElement _form;
        readonly ScrollbackTracker _logTracker = new ScrollbackTracker();
        readonly List<string> _lines = new List<string>();

        public event Action<HostSettings> StartRequested;
        public event Action StopRequested;
        public event Action BackRequested;
        /// <summary>The share address was copied to the clipboard.</summary>
        public event Action<string> AddressCopied;

        public HostState State { get; private set; } = HostState.Stopped;
        public string StatusText => _status.text;
        public string ErrorText => UIUtil.IsShown(_error) ? _error.text : null;
        public string ShareAddress { get; private set; } = "";
        public IReadOnlyList<string> LogLines => _lines;

        internal HostScreen(VisualElement root) : base(root)
        {
            _worldName = Q<TextField>("hostWorldName");
            _description = Q<TextField>("hostDescription");
            _folder = Q<TextField>("hostFolder");
            _port = Q<TextField>("hostPort");
            _password = Q<TextField>("hostPassword");
            _displayName = Q<TextField>("hostDisplayName");
            _start = Q<Button>("hostStart");
            _stop = Q<Button>("hostStop");
            _back = Q<Button>("hostBack");
            _copy = Q<Button>("hostCopy");
            _status = Q<Label>("hostStatus");
            _statusDot = Q<VisualElement>("hostStatusDot");
            _error = Q<Label>("hostError");
            _shareAddress = Q<Label>("hostShareAddress");
            _share = Q<VisualElement>("hostShare");
            _log = Q<ScrollView>("hostLog");
            _form = Q<VisualElement>("hostForm");

            _password.isPasswordField = true;
            _description.multiline = true;
            _worldName.maxLength = Validation.MaxWorldNameLength;
            _description.maxLength = 500;
            _port.maxLength = 5;
            _displayName.maxLength = Validation.MaxDisplayNameLength;
            _password.textEdition.placeholder = "Leave empty for an open world";
            _description.textEdition.placeholder = "What is this place?";
            _error.enableRichText = false;
            _status.enableRichText = false;
            _shareAddress.enableRichText = false;
            _shareAddress.selection.isSelectable = true;
            _log.verticalScroller.valueChanged += v => _logTracker.OnScrolled(v, _log.verticalScroller.highValue);

            _start.clicked += Submit;
            _stop.clicked += () => { if (State == HostState.Running || State == HostState.Starting) StopRequested?.Invoke(); };
            _back.clicked += RequestBack;
            _copy.clicked += CopyAddress;
            Root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            SetDefaults(new HostSettings { WorldName = "My world", Port = HostSettings.DefaultPort, WorldFolder = "", DisplayName = "" });
            UIUtil.SetShown(_error, false);
            SetState(HostState.Stopped, null);
            SetShareAddress(null);
        }

        /// <summary>Fills the form without raising events. A port of 0 shows the default.</summary>
        public void SetDefaults(HostSettings s)
        {
            _worldName.SetValueWithoutNotify(s.WorldName ?? "");
            _description.SetValueWithoutNotify(s.Description ?? "");
            _folder.SetValueWithoutNotify(s.WorldFolder ?? "");
            _port.SetValueWithoutNotify((s.Port > 0 ? s.Port : HostSettings.DefaultPort).ToString(CultureInfo.InvariantCulture));
            _password.SetValueWithoutNotify(s.Password ?? "");
            _displayName.SetValueWithoutNotify(s.DisplayName ?? "");
        }

        /// <summary>The form as typed. An unparsable port is reported as 0.</summary>
        public HostSettings Settings => new HostSettings
        {
            WorldName = (_worldName.value ?? "").Trim(),
            Description = (_description.value ?? "").Trim(),
            WorldFolder = (_folder.value ?? "").Trim(),
            Port = Validation.TryParsePort(_port.value, out int p) ? p : 0,
            Password = _password.value ?? "",
            DisplayName = (_displayName.value ?? "").Trim(),
        };

        /// <summary>Validates the form and raises <see cref="StartRequested"/>, or shows the problem.</summary>
        public void Submit()
        {
            if (State != HostState.Stopped && State != HostState.Failed) return;
            var s = Settings;
            string error = Validation.ValidateHost(s, out string field);
            foreach (var f in new[] { _worldName, _folder, _port, _displayName }) f.RemoveFromClassList("tv-field--invalid");
            if (error != null)
            {
                var bad = field switch { "worldFolder" => _folder, "port" => _port, "displayName" => _displayName, _ => _worldName };
                bad.AddToClassList("tv-field--invalid");
                ShowError(error);
                bad.Focus();
                return;
            }
            ShowError(null);
            StartRequested?.Invoke(s);
        }

        /// <summary>Sets the server state and the status line. The form is editable only when stopped or failed.</summary>
        public void SetState(HostState state, string statusText)
        {
            State = state;
            _status.text = string.IsNullOrEmpty(statusText) ? DefaultStatus(state) : statusText;
            foreach (HostState s in Enum.GetValues(typeof(HostState)))
                UIUtil.SetClass(_statusDot, "tv-dot--" + s.ToString().ToLowerInvariant(), s == state);
            bool editable = state == HostState.Stopped || state == HostState.Failed;
            _form.SetEnabled(editable);
            _start.SetEnabled(editable);
            _stop.SetEnabled(state == HostState.Running || state == HostState.Starting);
            _back.SetEnabled(editable);
        }

        public void ShowError(string message)
        {
            _error.text = message ?? "";
            UIUtil.SetShown(_error, !string.IsNullOrEmpty(message));
        }

        /// <summary>The address others should connect to; null or empty hides it.</summary>
        public void SetShareAddress(string address)
        {
            ShareAddress = address ?? "";
            _shareAddress.text = ShareAddress;
            UIUtil.SetShown(_share, ShareAddress.Length > 0);
        }

        /// <summary>Raises <see cref="BackRequested"/> unless the server is running, as Back (or Escape) does.</summary>
        public void RequestBack()
        {
            if (State == HostState.Stopped || State == HostState.Failed) BackRequested?.Invoke();
        }

        public void CopyAddress()
        {
            if (ShareAddress.Length == 0) return;
            GUIUtility.systemCopyBuffer = ShareAddress;
            AddressCopied?.Invoke(ShareAddress);
        }

        /// <summary>Appends one line of server output. Only the newest <see cref="MaxLogLines"/> are kept.</summary>
        public void AppendLog(string line)
        {
            line ??= "";
            _lines.Add(line);
            var label = UIUtil.PlainLabel(line, "tv-log__line");
            label.selection.isSelectable = true;
            if (line.Contains(" ERROR ") || line.StartsWith("ERROR", StringComparison.Ordinal)) label.AddToClassList("tv-log__line--error");
            else if (line.Contains(" WARN ") || line.StartsWith("WARN", StringComparison.Ordinal)) label.AddToClassList("tv-log__line--warn");
            _log.Add(label);
            while (_lines.Count > MaxLogLines)
            {
                _lines.RemoveAt(0);
                _log.contentContainer.RemoveAt(0);
            }
            if (_logTracker.OnLineAdded()) _log.schedule.Execute(() => _log.scrollOffset = new Vector2(0, float.MaxValue));
        }

        public void ClearLog()
        {
            _lines.Clear();
            _log.Clear();
            _logTracker.OnJumpedToBottom();
        }

        internal int LogElementCount => _log.contentContainer.childCount;
        internal TextField PortField => _port;
        internal TextField WorldNameField => _worldName;
        internal TextField FolderField => _folder;
        internal Button StartButton => _start;
        internal Button StopButton => _stop;
        internal Button BackButton => _back;
        internal Button CopyButton => _copy;

        protected override void OnShown() => Root.schedule.Execute(() => { if (IsVisible) _worldName.Focus(); });

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            if (evt.target is Button) return;
            if (evt.target is VisualElement t && (t == _description || t.GetFirstAncestorOfType<TextField>() == _description)) return;
            evt.StopPropagation();
            Submit();
        }

        static string DefaultStatus(HostState s) => s switch
        {
            HostState.Starting => "Starting the server…",
            HostState.Running => "Running",
            HostState.Stopping => "Stopping…",
            HostState.Failed => "The server stopped unexpectedly",
            _ => "Not running",
        };
    }
}
