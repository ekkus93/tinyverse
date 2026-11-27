using System;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>The strip along the top: world, connection, ping, people, position, move mode and HUD buttons.</summary>
    public sealed class StatusBar : UIPanel
    {
        readonly Label _world;
        readonly VisualElement _dot;
        readonly Label _state;
        readonly Label _ping;
        readonly Label _people;
        readonly Label _position;
        readonly Label _mode;
        readonly Button _peopleButton;
        readonly Button _buildButton;
        readonly Button _menuButton;

        public event Action PeopleClicked;
        public event Action BuildClicked;
        public event Action MenuClicked;

        public string WorldName => _world.text;
        public ConnectionState ConnectionState { get; private set; }
        public int? PingMs { get; private set; }
        public int PeopleCount { get; private set; }
        public Vector3 Position { get; private set; }
        public MoveMode MoveMode { get; private set; }
        public string StateText => _state.text;
        public string PingText => _ping.text;
        public string PeopleText => _people.text;
        public string PositionText => _position.text;
        public string ModeText => _mode.text;

        internal StatusBar(VisualElement root) : base(root)
        {
            _world = Q<Label>("statusWorld");
            _world.enableRichText = false;
            _dot = Q<VisualElement>("statusDot");
            _state = Q<Label>("statusState");
            _ping = Q<Label>("statusPing");
            _people = Q<Label>("statusPeople");
            _position = Q<Label>("statusPosition");
            _mode = Q<Label>("statusMode");
            _peopleButton = Q<Button>("statusPeopleButton");
            _buildButton = Q<Button>("statusBuildButton");
            _menuButton = Q<Button>("statusMenuButton");
            _peopleButton.Insert(0, new Icon(IconKind.People));
            _buildButton.Insert(0, new Icon(IconKind.Build));
            _menuButton.Add(new Icon(IconKind.Menu));
            _peopleButton.clicked += () => PeopleClicked?.Invoke();
            _buildButton.clicked += () => BuildClicked?.Invoke();
            _menuButton.clicked += () => MenuClicked?.Invoke();

            SetWorldName("");
            SetConnectionState(ConnectionState.Disconnected);
            SetPing(null);
            SetPeopleCount(0);
            SetPosition(Vector3.zero);
            SetMoveMode(MoveMode.Walk);
            SetBuildActive(false);
        }

        public void SetWorldName(string name) => _world.text = string.IsNullOrEmpty(name) ? "Tinyverse" : name;

        public void SetConnectionState(ConnectionState state)
        {
            ConnectionState = state;
            _state.text = state switch
            {
                ConnectionState.Connected => "Connected",
                ConnectionState.Connecting => "Connecting…",
                ConnectionState.Reconnecting => "Reconnecting…",
                _ => "Disconnected",
            };
            foreach (ConnectionState s in Enum.GetValues(typeof(ConnectionState)))
                UIUtil.SetClass(_dot, "tv-dot--" + s.ToString().ToLowerInvariant(), s == state);
            UIUtil.SetShown(_ping, state == ConnectionState.Connected);
        }

        /// <summary>Round-trip time in milliseconds; null when unknown.</summary>
        public void SetPing(int? ms)
        {
            PingMs = ms;
            _ping.text = ms.HasValue ? ms.Value + " ms" : "– ms";
            UIUtil.SetClass(_ping, "tv-ping--good", ms.HasValue && ms.Value < 80);
            UIUtil.SetClass(_ping, "tv-ping--fair", ms.HasValue && ms.Value >= 80 && ms.Value < 200);
            UIUtil.SetClass(_ping, "tv-ping--poor", ms.HasValue && ms.Value >= 200);
        }

        public void SetPeopleCount(int count)
        {
            PeopleCount = count;
            _people.text = count.ToString(CultureInfo.InvariantCulture);
            _peopleButton.tooltip = (count == 1 ? "1 person" : count + " people") + " here (P)";
        }

        public void SetPosition(Vector3 p)
        {
            Position = p;
            _position.text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}, {1:0.0}, {2:0.0}", p.x, p.y, p.z);
        }

        public void SetMoveMode(MoveMode mode)
        {
            MoveMode = mode;
            _mode.text = mode switch { MoveMode.Run => "Run", MoveMode.Fly => "Fly", _ => "Walk" };
            foreach (MoveMode m in Enum.GetValues(typeof(MoveMode)))
                UIUtil.SetClass(_mode, "tv-mode--" + m.ToString().ToLowerInvariant(), m == mode);
        }

        /// <summary>Highlights the build button; UIRoot calls this when build mode changes.</summary>
        public void SetBuildActive(bool active) => UIUtil.SetClass(_buildButton, "tv-toggle-button--on", active);

        public void SetPeopleOpen(bool open) => UIUtil.SetClass(_peopleButton, "tv-toggle-button--on", open);

        internal Button PeopleButton => _peopleButton;
        internal Button BuildButton => _buildButton;
        internal Button MenuButton => _menuButton;
    }
}
