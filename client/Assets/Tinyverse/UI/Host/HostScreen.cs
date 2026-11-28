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
        readonly Toggle _tls;
        readonly DropdownField _template;
        readonly Label _templateText;
        readonly List<WorldTemplateChoice> _templates = new List<WorldTemplateChoice>();
        bool _existingWorld;
        readonly Button _start;
        readonly Button _stop;
        readonly Button _back;
        readonly Button _join;
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
        /// <summary>
        /// "Join world" while the server runs: the way back into the hosted world (Back only works
        /// when stopped). The glue connects to its own server with the form's name and password.
        /// </summary>
        public event Action JoinRequested;
        /// <summary>The share address was copied to the clipboard.</summary>
        public event Action<string> AddressCopied;
        /// <summary>
        /// The world folder was edited (the trimmed text). The glue answers with <see cref="SetExistingWorld"/>,
        /// since only it may look at the disk for a world.
        /// </summary>
        public event Action<string> FolderChanged;

        /// <summary>Shown instead of the description when the folder already holds a world.</summary>
        public const string ExistingWorldNote = "This folder already holds a world, which opens as it is. Choose a new folder to start from a template.";

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
            _tls = Q<Toggle>("hostTls");
            _template = Q<DropdownField>("hostTemplate");
            _templateText = Q<Label>("hostTemplateDescription");
            _start = Q<Button>("hostStart");
            _stop = Q<Button>("hostStop");
            _back = Q<Button>("hostBack");
            _join = Q<Button>("hostJoin");
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
            _templateText.enableRichText = false;
            _template.RegisterValueChangedCallback(_ => ShowTemplateText());
            _folder.RegisterValueChangedCallback(e => FolderChanged?.Invoke((e.newValue ?? "").Trim()));
            _shareAddress.selection.isSelectable = true;
            _log.verticalScroller.valueChanged += v => _logTracker.OnScrolled(v, _log.verticalScroller.highValue);

            _start.clicked += Submit;
            _stop.clicked += () => { if (State == HostState.Running || State == HostState.Starting) StopRequested?.Invoke(); };
            _back.clicked += RequestBack;
            _join.clicked += RequestJoin;
            _copy.clicked += CopyAddress;
            Root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            SetTemplates(null);
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
            _tls.SetValueWithoutNotify(!s.Unencrypted);
            if (!string.IsNullOrEmpty(s.Template)) SelectTemplate(s.Template);
        }

        /// <summary>
        /// The templates to offer, in order; the first is selected unless the current choice is among them. Null or
        /// empty disables the choice (the server makes its default world).
        /// </summary>
        public void SetTemplates(IReadOnlyList<WorldTemplateChoice> templates)
        {
            string current = SelectedTemplate;
            _templates.Clear();
            if (templates != null)
                foreach (var t in templates)
                    if (!string.IsNullOrEmpty(t.Name)) _templates.Add(t);
            var titles = new List<string>();
            foreach (var t in _templates) titles.Add(string.IsNullOrEmpty(t.Title) ? t.Name : t.Title);
            _template.choices = titles.Count > 0 ? titles : new List<string> { "Empty world" };
            int i = _templates.FindIndex(t => t.Name == current);
            _template.SetValueWithoutNotify(_template.choices[Math.Max(0, i)]);
            UpdateTemplateEnabled();
            ShowTemplateText();
        }

        /// <summary>Selects a template by name; false (and no change) if it is not offered.</summary>
        public bool SelectTemplate(string name)
        {
            int i = _templates.FindIndex(t => t.Name == name);
            if (i < 0) return false;
            _template.SetValueWithoutNotify(_template.choices[i]);
            ShowTemplateText();
            return true;
        }

        /// <summary>
        /// Whether the world folder already holds a world. Then the template does not apply: the choice is
        /// disabled and a note says why, and <see cref="Settings"/> carries no template.
        /// </summary>
        public void SetExistingWorld(bool existing)
        {
            _existingWorld = existing;
            UpdateTemplateEnabled();
            ShowTemplateText();
        }

        /// <summary>The name of the selected template, or null when none is offered.</summary>
        public string SelectedTemplate
        {
            get
            {
                int i = _template.index;
                return i >= 0 && i < _templates.Count ? _templates[i].Name : null;
            }
        }

        public bool IsExistingWorld => _existingWorld;

        /// <summary>The description under the choice (or the note for an existing world).</summary>
        public string TemplateText => UIUtil.IsShown(_templateText) ? _templateText.text : null;

        void UpdateTemplateEnabled() => _template.SetEnabled(!_existingWorld && _templates.Count > 0);

        void ShowTemplateText()
        {
            string text;
            if (_existingWorld) text = ExistingWorldNote;
            else
            {
                int i = _template.index;
                text = i >= 0 && i < _templates.Count ? _templates[i].Description : "";
            }
            _templateText.text = text ?? "";
            UIUtil.SetClass(_templateText, "tv-host__template-text--note", _existingWorld);
            UIUtil.SetShown(_templateText, !string.IsNullOrEmpty(text));
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
            Unencrypted = !_tls.value,
            Template = _existingWorld ? "" : SelectedTemplate ?? "",
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
            // While the server runs, Start becomes "Join world" and Back is hidden.
            bool running = state == HostState.Running;
            UIUtil.SetShown(_start, !running);
            UIUtil.SetShown(_join, running);
            UIUtil.SetShown(_back, editable);
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
            bool longAddress = ShareAddress.Length > 32;
            // A pinned address has no spaces; zero-width spaces let it wrap at its natural breaks.
            // Copy and selection-free uses go through ShareAddress, which has none.
            _shareAddress.text = longAddress ? BreakableAddress(ShareAddress) : ShareAddress;
            UIUtil.SetClass(_shareAddress, "tv-host__address--long", longAddress);
            UIUtil.SetShown(_share, ShareAddress.Length > 0);
        }

        static string BreakableAddress(string address)
        {
            var sb = new System.Text.StringBuilder(address.Length + 16);
            int sinceBreak = 0;
            foreach (char c in address)
            {
                sb.Append(c);
                sinceBreak++;
                if (c == '/' || c == '#' || c == '=' || sinceBreak >= 16)
                {
                    sb.Append('​');
                    sinceBreak = 0;
                }
            }
            return sb.ToString();
        }

        /// <summary>Raises <see cref="BackRequested"/> unless the server is running, as Back (or Escape) does.</summary>
        public void RequestBack()
        {
            if (State == HostState.Stopped || State == HostState.Failed) BackRequested?.Invoke();
        }

        /// <summary>Raises <see cref="JoinRequested"/> if the server is running, as "Join world" does.</summary>
        public void RequestJoin()
        {
            if (State == HostState.Running) JoinRequested?.Invoke();
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
        internal Button JoinButton => _join;
        internal Button CopyButton => _copy;
        internal DropdownField TemplateField => _template;

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
