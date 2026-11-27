using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>Transient messages stacked at the top of the screen, newest at the bottom.</summary>
    public sealed class Toasts
    {
        public const int MaxVisible = 5;
        public const float DefaultSeconds = 4f;
        public const float DefaultErrorSeconds = 7f;

        sealed class Entry
        {
            public VisualElement Element;
            public string Message;
            public ToastKind Kind;
            public float Remaining;
        }

        readonly List<Entry> _entries = new List<Entry>();

        public VisualElement Root { get; }

        public int Count => _entries.Count;

        internal Toasts(VisualElement root)
        {
            Root = root;
            Root.pickingMode = PickingMode.Ignore;
        }

        /// <summary>Shows a message. <paramref name="seconds"/> &lt;= 0 picks the default for the kind.</summary>
        public void Show(string message, ToastKind kind = ToastKind.Info, float seconds = 0f)
        {
            if (seconds <= 0f) seconds = kind == ToastKind.Error ? DefaultErrorSeconds : DefaultSeconds;
            var el = new VisualElement();
            el.AddToClassList("tv-toast");
            el.AddToClassList("tv-toast--" + kind.ToString().ToLowerInvariant());
            var bar = new VisualElement();
            bar.AddToClassList("tv-toast__bar");
            bar.pickingMode = PickingMode.Ignore;
            el.Add(bar);
            el.Add(UIUtil.PlainLabel(message, "tv-toast__text"));
            var entry = new Entry { Element = el, Message = message, Kind = kind, Remaining = seconds };
            el.RegisterCallback<ClickEvent>(_ => Remove(entry));
            el.tooltip = "Click to dismiss";
            _entries.Add(entry);
            Root.Add(el);
            while (_entries.Count > MaxVisible) Remove(_entries[0]);
        }

        public void Info(string message) => Show(message, ToastKind.Info);
        public void Warning(string message) => Show(message, ToastKind.Warning);
        public void Error(string message) => Show(message, ToastKind.Error);

        public void Clear()
        {
            while (_entries.Count > 0) Remove(_entries[0]);
        }

        /// <summary>The messages currently shown, oldest first.</summary>
        public IReadOnlyList<string> Messages
        {
            get
            {
                var list = new List<string>(_entries.Count);
                foreach (var e in _entries) list.Add(e.Message);
                return list;
            }
        }

        public IReadOnlyList<ToastKind> Kinds
        {
            get
            {
                var list = new List<ToastKind>(_entries.Count);
                foreach (var e in _entries) list.Add(e.Kind);
                return list;
            }
        }

        internal void Tick(float dt)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                e.Remaining -= dt;
                if (e.Remaining <= 0f) Remove(e);
                else if (e.Remaining < 0.4f) e.Element.AddToClassList("tv-toast--leaving");
            }
        }

        void Remove(Entry e)
        {
            if (!_entries.Remove(e)) return;
            e.Element.RemoveFromHierarchy();
        }
    }
}
