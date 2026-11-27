using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// The first screen: address, display name, optional password, recent worlds. It remembers
    /// nothing itself; the glue fills it with <see cref="SetAddress"/> and friends and stores what
    /// the events report.
    /// </summary>
    public sealed class ConnectScreen : UIPanel
    {
        readonly TextField _address;
        readonly TextField _name;
        readonly TextField _password;
        readonly Button _connect;
        readonly Button _host;
        readonly Label _error;
        readonly VisualElement _busy;
        readonly Label _busyText;
        readonly Button _cancel;
        readonly VisualElement _form;
        readonly ScrollView _recentList;
        readonly Label _recentEmpty;
        readonly Label _version;
        readonly VisualElement _spinner;
        IVisualElementScheduledItem _spin;
        float _spinAngle;
        readonly List<RecentWorld> _recent = new List<RecentWorld>();
        int _selectedRecent = -1;

        /// <summary>Connect was pressed (or Enter) and the form is valid.</summary>
        public event Action<ConnectRequest> ConnectRequested;
        /// <summary>Cancel was pressed while busy.</summary>
        public event Action CancelRequested;
        public event Action HostRequested;
        /// <summary>The user removed a recent world; the glue should forget it and call <see cref="SetRecentWorlds"/>.</summary>
        public event Action<RecentWorld> RecentWorldRemoved;
        /// <summary>The settings button; UIRoot opens the settings panel.</summary>
        public event Action SettingsRequested;

        public string Address => _address.value ?? "";
        public string DisplayName => _name.value ?? "";
        public string Password => _password.value ?? "";
        public bool IsBusy { get; private set; }
        public string ErrorText => UIUtil.IsShown(_error) ? _error.text : null;
        public IReadOnlyList<RecentWorld> RecentWorlds => _recent;
        public int SelectedRecentIndex => _selectedRecent;

        internal ConnectScreen(VisualElement root) : base(root)
        {
            _address = Q<TextField>("connectAddress");
            _name = Q<TextField>("connectName");
            _password = Q<TextField>("connectPassword");
            _connect = Q<Button>("connectButton");
            _host = Q<Button>("connectHostButton");
            _error = Q<Label>("connectError");
            _busy = Q<VisualElement>("connectBusy");
            _busyText = Q<Label>("connectBusyText");
            _cancel = Q<Button>("connectCancel");
            _form = Q<VisualElement>("connectForm");
            _recentList = Q<ScrollView>("connectRecentList");
            _recentEmpty = Q<Label>("connectRecentEmpty");
            _version = Q<Label>("connectVersion");
            _spinner = Q<VisualElement>("connectSpinner");
            _spin = _spinner.schedule.Execute(() =>
            {
                _spinAngle = (_spinAngle + 9f) % 360f;
                _spinner.style.rotate = new Rotate(_spinAngle);
            }).Every(16);
            _spin.Pause();

            _password.isPasswordField = true;
            _address.maxLength = 256;
            _name.maxLength = Validation.MaxDisplayNameLength;
            _password.maxLength = 256;
            _address.textEdition.placeholder = "192.168.1.20:7878";
            _name.textEdition.placeholder = "How others see you";
            _password.textEdition.placeholder = "Only if the world has one";
            _error.enableRichText = false;
            _busyText.enableRichText = false;

            _connect.clicked += Submit;
            _host.clicked += () => { if (!IsBusy) HostRequested?.Invoke(); };
            _cancel.clicked += RequestCancel;
            var settings = Q<Button>("connectSettings");
            settings.clicked += () => SettingsRequested?.Invoke();
            Root.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);

            UIUtil.SetShown(_error, false);
            UIUtil.SetShown(_busy, false);
            RebuildRecent();
        }

        public void SetAddress(string address) => _address.SetValueWithoutNotify(address ?? "");
        public void SetDisplayName(string name) => _name.SetValueWithoutNotify(name ?? "");
        public void SetPassword(string password) => _password.SetValueWithoutNotify(password ?? "");
        public void SetVersionText(string text) => _version.text = text ?? "";

        public void ShowError(string message)
        {
            _error.text = message ?? "";
            UIUtil.SetShown(_error, !string.IsNullOrEmpty(message));
        }

        public void ClearError() => ShowError(null);

        /// <summary>Shows or hides the busy overlay with a Cancel button; the form is disabled meanwhile.</summary>
        public void SetBusy(bool busy, string message = null)
        {
            IsBusy = busy;
            _busyText.text = string.IsNullOrEmpty(message) ? "Connecting…" : message;
            UIUtil.SetShown(_busy, busy);
            if (busy) _spin.Resume(); else _spin.Pause();
            _form.SetEnabled(!busy);
            _recentList.SetEnabled(!busy);
            if (busy) _cancel.schedule.Execute(() => { if (IsBusy) _cancel.Focus(); });
        }

        /// <summary>Raises <see cref="CancelRequested"/> if busy, as the Cancel button (or Escape) does.</summary>
        public void RequestCancel()
        {
            if (IsBusy) CancelRequested?.Invoke();
        }

        /// <summary>Replaces the recent-worlds list, most recent first as given.</summary>
        public void SetRecentWorlds(IReadOnlyList<RecentWorld> worlds)
        {
            _recent.Clear();
            if (worlds != null) _recent.AddRange(worlds);
            _selectedRecent = -1;
            RebuildRecent();
        }

        /// <summary>Validates the form and raises <see cref="ConnectRequested"/>, or shows the problem.</summary>
        public void Submit()
        {
            if (IsBusy) return;
            string error = Validation.ValidateAddress(Address);
            TextField bad = _address;
            if (error == null)
            {
                error = Validation.ValidateDisplayName(DisplayName);
                bad = _name;
            }
            UIUtil.SetClass(_address, "tv-field--invalid", error != null && bad == _address);
            UIUtil.SetClass(_name, "tv-field--invalid", error != null && bad == _name);
            if (error != null)
            {
                ShowError(error);
                bad.Focus();
                return;
            }
            ClearError();
            ConnectRequested?.Invoke(new ConnectRequest(Address.Trim(), DisplayName.Trim(), Password));
        }

        /// <summary>Fills the address from a recent world, as a single click does.</summary>
        public void SelectRecent(int index)
        {
            if (index < 0 || index >= _recent.Count) return;
            _selectedRecent = index;
            SetAddress(_recent[index].Address);
            ClearError();
            for (int i = 0; i < _recentList.contentContainer.childCount; i++)
                UIUtil.SetClass(_recentList.contentContainer[i], "tv-recent--selected", i == index);
        }

        /// <summary>Fills the address and connects, as a double click does.</summary>
        public void ActivateRecent(int index)
        {
            if (index < 0 || index >= _recent.Count) return;
            SelectRecent(index);
            Submit();
        }

        public void RemoveRecent(int index)
        {
            if (index < 0 || index >= _recent.Count) return;
            var w = _recent[index];
            _recent.RemoveAt(index);
            _selectedRecent = -1;
            RebuildRecent();
            RecentWorldRemoved?.Invoke(w);
        }

        protected override void OnShown()
        {
            var first = string.IsNullOrEmpty(Address) ? _address : string.IsNullOrEmpty(DisplayName) ? _name : (Focusable)_connect;
            Root.schedule.Execute(() => { if (IsVisible && !IsBusy) first.Focus(); });
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter) return;
            if (IsBusy) return;
            // Enter on a focused recent row or button is handled by that element.
            if (evt.target is VisualElement t && (t is Button || t.ClassListContains("tv-recent"))) return;
            evt.StopPropagation();
            Submit();
        }

        void RebuildRecent()
        {
            _recentList.Clear();
            UIUtil.SetShown(_recentEmpty, _recent.Count == 0);
            var now = DateTime.Now;
            for (int i = 0; i < _recent.Count; i++)
            {
                int index = i;
                var w = _recent[i];
                var row = new VisualElement { focusable = true, tabIndex = 0 };
                row.AddToClassList("tv-recent");
                var text = new VisualElement();
                text.AddToClassList("tv-recent__text");
                text.pickingMode = PickingMode.Ignore;
                var nameLabel = UIUtil.PlainLabel(string.IsNullOrEmpty(w.Name) ? w.Address : w.Name, "tv-recent__name");
                var addrLabel = UIUtil.PlainLabel(w.Address, "tv-recent__address");
                var whenLabel = UIUtil.PlainLabel(Validation.RelativeTime(w.LastVisited, now), "tv-recent__when");
                nameLabel.pickingMode = addrLabel.pickingMode = whenLabel.pickingMode = PickingMode.Ignore;
                text.Add(nameLabel);
                text.Add(addrLabel);
                row.Add(text);
                row.Add(whenLabel);
                var remove = new Button(() => RemoveRecent(index)) { tooltip = "Remove from the list" };
                remove.AddToClassList("tv-icon-button");
                remove.AddToClassList("tv-recent__remove");
                remove.Add(new Icon(IconKind.Close));
                row.Add(remove);
                row.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0 || IsBusy) return;
                    if (e.clickCount >= 2) ActivateRecent(index); else SelectRecent(index);
                });
                row.RegisterCallback<KeyDownEvent>(e =>
                {
                    if (e.target != row || IsBusy) return;
                    if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { e.StopPropagation(); ActivateRecent(index); }
                    else if (e.keyCode == KeyCode.Delete) { e.StopPropagation(); RemoveRecent(index); }
                });
                row.RegisterCallback<FocusInEvent>(e => { if (e.target == row) SelectRecent(index); });
                _recentList.Add(row);
            }
        }

        internal TextField AddressField => _address;
        internal TextField NameField => _name;
        internal TextField PasswordField => _password;
        internal Button ConnectButton => _connect;
        internal Button HostButton => _host;
        internal Button CancelButton => _cancel;
        internal VisualElement RecentRow(int i) => _recentList.contentContainer[i];
    }
}
