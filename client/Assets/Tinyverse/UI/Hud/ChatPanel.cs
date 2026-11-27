using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Chat scrollback and input. The input opens with Enter (via <see cref="UIRoot"/>) and closes
    /// with Enter (sending) or Escape; Tab switches local / world while typing; Up / Down recall
    /// earlier messages. The panel fades when idle.
    /// </summary>
    public sealed class ChatPanel : UIPanel
    {
        public const int MaxLines = 200;
        public const int MaxMessageLength = 500;
        public const int CounterThreshold = 400;
        public const int MaxInputHistory = 50;
        public const float IdleSeconds = 10f;

        readonly ScrollView _log;
        readonly Button _newHint;
        readonly VisualElement _inputRow;
        readonly TextField _input;
        readonly Button _channelChip;
        readonly Label _counter;
        readonly ScrollbackTracker _tracker = new ScrollbackTracker();
        readonly List<ChatLine> _lines = new List<ChatLine>();
        readonly List<string> _history = new List<string>();
        int _historyIndex = -1;
        string _draft = "";
        float _idle;
        int _openedFrame = -10;
        bool _hovered;

        /// <summary>The user sent a message: trimmed text, at most 500 characters, and the channel.</summary>
        public event Action<string, ChatChannel> Submitted;
        /// <summary>Raised with true when the input opens and false when it closes.</summary>
        public event Action<bool> InputOpenChanged;

        public ChatChannel Channel { get; private set; } = ChatChannel.Local;
        public bool IsInputOpen { get; private set; }
        public IReadOnlyList<ChatLine> Lines => _lines;
        public IReadOnlyList<string> InputHistory => _history;
        public bool IsStuckToBottom => _tracker.StickToBottom;
        public int UnseenCount => _tracker.UnseenCount;
        public bool IsIdle => Root.ClassListContains("tv-chat--idle");
        public string InputText => _input.value ?? "";
        public string CounterText => UIUtil.IsShown(_counter) ? _counter.text : null;

        internal ChatPanel(VisualElement root) : base(root)
        {
            _log = Q<ScrollView>("chatLog");
            _newHint = Q<Button>("chatNewHint");
            _inputRow = Q<VisualElement>("chatInputRow");
            _input = Q<TextField>("chatInput");
            _channelChip = Q<Button>("chatChannel");
            _counter = Q<Label>("chatCounter");

            _input.maxLength = MaxMessageLength;
            _input.textEdition.placeholder = "Say something…";
            _log.verticalScroller.valueChanged += v => OnScrolled(v);
            _log.contentContainer.RegisterCallback<GeometryChangedEvent>(_ => { if (_tracker.StickToBottom) ScrollViewToEnd(); });
            _newHint.clicked += ScrollToBottom;
            _channelChip.clicked += () =>
            {
                ToggleChannel();
                if (IsInputOpen) _input.schedule.Execute(() => _input.Focus());
            };
            _channelChip.focusable = false;
            _input.RegisterValueChangedCallback(e =>
            {
                // maxLength stops typing; this also catches pasted or programmatic text.
                if (e.newValue != null && e.newValue.Length > MaxMessageLength)
                    _input.SetValueWithoutNotify(e.newValue.Substring(0, MaxMessageLength));
                UpdateCounter();
            });
            _input.RegisterCallback<KeyDownEvent>(OnInputKey, TrickleDown.TrickleDown);
            _input.RegisterCallback<NavigationMoveEvent>(e =>
            {
                if (e.direction == NavigationMoveEvent.Direction.Next || e.direction == NavigationMoveEvent.Direction.Previous)
                {
                    e.StopPropagation();
                    _input.focusController?.IgnoreEvent(e);
                }
            }, TrickleDown.TrickleDown);
            _input.RegisterCallback<NavigationCancelEvent>(e => { e.StopPropagation(); CloseInput(); }, TrickleDown.TrickleDown);
            _input.RegisterCallback<FocusOutEvent>(_ => Root.schedule.Execute(() =>
            {
                // Clicking elsewhere closes an empty input; a draft stays open.
                if (IsInputOpen && _input.focusController?.focusedElement == null && string.IsNullOrEmpty(_input.value)) CloseInput();
            }));
            Root.RegisterCallback<PointerEnterEvent>(_ => { _hovered = true; Wake(); });
            Root.RegisterCallback<PointerLeaveEvent>(_ => _hovered = false);

            UIUtil.SetShown(_inputRow, false);
            UIUtil.SetShown(_newHint, false);
            UIUtil.SetShown(_counter, false);
            SetChannel(ChatChannel.Local);
        }

        /// <summary>Appends a line; the oldest lines beyond <see cref="MaxLines"/> are dropped.</summary>
        public void AddLine(ChatLine line)
        {
            AppendLine(line);
            Trim();
            if (_tracker.OnLineAdded()) ScrollViewToEnd();
            RefreshNewHint();
            Wake();
        }

        public void AddSystem(string text) => AddLine(ChatLine.System(text));

        /// <summary>Replaces the scrollback with the lines received on join, oldest first.</summary>
        public void SetHistory(IReadOnlyList<ChatLine> lines)
        {
            _lines.Clear();
            _log.Clear();
            if (lines != null)
            {
                int start = Math.Max(0, lines.Count - MaxLines);
                for (int i = start; i < lines.Count; i++) AppendLine(lines[i]);
            }
            _tracker.OnJumpedToBottom();
            ScrollViewToEnd();
            RefreshNewHint();
            Wake();
        }

        public void Clear() => SetHistory(null);

        /// <summary>Opens and focuses the input, optionally on a given channel.</summary>
        public void OpenInput(ChatChannel? channel = null)
        {
            if (channel.HasValue) SetChannel(channel.Value);
            bool was = IsInputOpen;
            IsInputOpen = true;
            _openedFrame = Time.frameCount;
            UIUtil.SetShown(_inputRow, true);
            Root.AddToClassList("tv-chat--open");
            Wake();
            _input.Focus();
            // Focus can fail before the first layout; try again next frame.
            _input.schedule.Execute(() => { if (IsInputOpen && _input.focusController?.focusedElement == null) _input.Focus(); });
            if (!was) InputOpenChanged?.Invoke(true);
        }

        /// <summary>Closes the input and drops focus. The typed text is discarded.</summary>
        public void CloseInput()
        {
            if (!IsInputOpen) return;
            IsInputOpen = false;
            _input.SetValueWithoutNotify("");
            _historyIndex = -1;
            _draft = "";
            UpdateCounter();
            _input.Blur();
            UIUtil.SetShown(_inputRow, false);
            Root.RemoveFromClassList("tv-chat--open");
            InputOpenChanged?.Invoke(false);
        }

        /// <summary>Sends the typed text (if any) and closes the input.</summary>
        public void Submit()
        {
            string text = (_input.value ?? "").Trim();
            if (text.Length > MaxMessageLength) text = text.Substring(0, MaxMessageLength);
            if (text.Length > 0)
            {
                if (_history.Count == 0 || _history[_history.Count - 1] != text) _history.Add(text);
                if (_history.Count > MaxInputHistory) _history.RemoveAt(0);
                Submitted?.Invoke(text, Channel);
            }
            CloseInput();
        }

        /// <summary>Sets the text in the input (without opening it), as if typed.</summary>
        public void SetInputText(string text) => _input.value = text ?? "";

        public void SetChannel(ChatChannel channel)
        {
            if (channel == ChatChannel.System) channel = ChatChannel.Local;
            Channel = channel;
            _channelChip.text = channel == ChatChannel.World ? "World" : "Local";
            _channelChip.tooltip = channel == ChatChannel.World ? "Everyone in the world hears you (Tab to switch)" : "People within 20 m hear you (Tab to switch)";
            UIUtil.SetClass(_channelChip, "tv-chip--world", channel == ChatChannel.World);
            UIUtil.SetClass(_inputRow, "tv-chat__input-row--world", channel == ChatChannel.World);
        }

        public void ToggleChannel() => SetChannel(Channel == ChatChannel.World ? ChatChannel.Local : ChatChannel.World);

        /// <summary>Recalls an older (Up) or newer (Down) message from the input history.</summary>
        public void RecallHistory(bool older)
        {
            if (_history.Count == 0) return;
            if (_historyIndex == -1) _draft = _input.value ?? "";
            int next = Mathf.Clamp(_historyIndex + (older ? 1 : -1), -1, _history.Count - 1);
            if (next == _historyIndex) return;
            _historyIndex = next;
            string text = next == -1 ? _draft : _history[_history.Count - 1 - next];
            _input.value = text;
            _input.SelectRange(text.Length, text.Length);
        }

        public void ScrollToBottom()
        {
            _tracker.OnJumpedToBottom();
            ScrollViewToEnd();
            RefreshNewHint();
        }

        internal void Tick(float dt)
        {
            if (IsInputOpen || _hovered) { _idle = 0f; Root.RemoveFromClassList("tv-chat--idle"); return; }
            _idle += dt;
            if (_idle >= IdleSeconds) Root.AddToClassList("tv-chat--idle");
        }

        /// <summary>Test hook: what a user scroll reports, without needing layout.</summary>
        internal void SimulateScroll(float offset, float maxOffset)
        {
            _tracker.OnScrolled(offset, maxOffset);
            RefreshNewHint();
        }

        internal TextField InputField => _input;
        internal Button ChannelChip => _channelChip;
        internal Button NewMessagesHint => _newHint;
        internal int LineElementCount => _log.contentContainer.childCount;
        internal VisualElement LineElement(int i) => _log.contentContainer[i];

        void OnScrolled(float value)
        {
            _tracker.OnScrolled(value, _log.verticalScroller.highValue);
            RefreshNewHint();
            Wake();
        }

        void Wake()
        {
            _idle = 0f;
            Root.RemoveFromClassList("tv-chat--idle");
        }

        void RefreshNewHint()
        {
            int n = _tracker.UnseenCount;
            UIUtil.SetShown(_newHint, n > 0);
            _newHint.text = n == 1 ? "1 new message" : $"{n} new messages";
        }

        void ScrollViewToEnd() => _log.schedule.Execute(() =>
        {
            if (_tracker.StickToBottom) _log.scrollOffset = new Vector2(0, _log.verticalScroller.highValue);
        });

        void UpdateCounter()
        {
            int len = (_input.value ?? "").Length;
            UIUtil.SetShown(_counter, len >= CounterThreshold);
            _counter.text = $"{len}/{MaxMessageLength}";
            UIUtil.SetClass(_counter, "tv-chat__counter--full", len >= MaxMessageLength - 20);
        }

        void OnInputKey(KeyDownEvent e)
        {
            switch (e.keyCode)
            {
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    e.StopPropagation();
                    // The Enter that opened the input can reach it too; do not let it close it again.
                    if (Time.frameCount - _openedFrame <= 1 && string.IsNullOrEmpty(_input.value)) break;
                    Submit();
                    break;
                case KeyCode.Escape:
                    e.StopPropagation();
                    CloseInput();
                    break;
                case KeyCode.Tab:
                    e.StopPropagation();
                    _input.focusController?.IgnoreEvent(e);
                    ToggleChannel();
                    break;
                case KeyCode.UpArrow:
                    e.StopPropagation();
                    RecallHistory(true);
                    break;
                case KeyCode.DownArrow:
                    e.StopPropagation();
                    RecallHistory(false);
                    break;
                default:
                    // Tab also arrives as a character event; do not insert it.
                    if (e.character == '\t' || e.character == '\n' || e.character == '\r')
                    {
                        e.StopPropagation();
                        _input.focusController?.IgnoreEvent(e);
                    }
                    break;
            }
        }

        void AppendLine(ChatLine line)
        {
            _lines.Add(line);
            _log.Add(BuildLine(line));
        }

        void Trim()
        {
            while (_lines.Count > MaxLines)
            {
                _lines.RemoveAt(0);
                _log.contentContainer.RemoveAt(0);
            }
        }

        static VisualElement BuildLine(ChatLine line)
        {
            var row = new VisualElement();
            row.AddToClassList("tv-chat-line");
            row.AddToClassList("tv-chat-line--" + line.Channel.ToString().ToLowerInvariant());
            var time = line.Time == default ? DateTime.Now : line.Time;
            row.Add(UIUtil.PlainLabel(time.ToString("HH:mm", CultureInfo.InvariantCulture), "tv-chat-line__time"));
            if (line.Channel != ChatChannel.System)
            {
                if (line.Channel == ChatChannel.World) row.Add(UIUtil.Label("world", "tv-chat-line__channel"));
                if (line.SenderIsAgent) row.Add(UIUtil.Label("AI", "tv-badge--ai"));
                row.Add(UIUtil.PlainLabel(string.IsNullOrEmpty(line.SenderName) ? "?" : line.SenderName, "tv-chat-line__name"));
            }
            row.Add(UIUtil.PlainLabel(line.Text ?? "", "tv-chat-line__text"));
            foreach (var child in row.Children()) child.pickingMode = PickingMode.Ignore;
            return row;
        }
    }
}
