using System;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>A screen or panel: a root element that can be shown and hidden.</summary>
    public abstract class UIPanel
    {
        public VisualElement Root { get; }

        public bool IsVisible => Root.style.display != DisplayStyle.None;

        /// <summary>Raised with the new visibility whenever it changes.</summary>
        public event Action<bool> VisibilityChanged;

        protected UIPanel(VisualElement root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public void Show() => SetVisible(true);
        public void Hide() => SetVisible(false);
        public void Toggle() => SetVisible(!IsVisible);

        public void SetVisible(bool visible)
        {
            if (visible == IsVisible) return;
            Root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible) OnShown(); else OnHidden();
            VisibilityChanged?.Invoke(visible);
        }

        protected virtual void OnShown() { }
        protected virtual void OnHidden() { }

        /// <summary>Finds a named child or throws, so a UXML typo fails in tests.</summary>
        protected T Q<T>(string name) where T : VisualElement => UIUtil.Require<T>(Root, name);
    }

    public static class UIUtil
    {
        public static T Require<T>(VisualElement root, string name) where T : VisualElement
        {
            var e = root.Q<T>(name);
            if (e == null)
                throw new InvalidOperationException($"Tinyverse UI: no {typeof(T).Name} named '{name}' under '{root.name}'");
            return e;
        }

        public static void SetShown(VisualElement e, bool shown) => e.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;

        public static bool IsShown(VisualElement e) => e.style.display != DisplayStyle.None;

        public static void SetClass(VisualElement e, string cls, bool on)
        {
            if (on) e.AddToClassList(cls); else e.RemoveFromClassList(cls);
        }

        public static Button Button(string text, string cls, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            if (!string.IsNullOrEmpty(cls)) b.AddToClassList(cls);
            return b;
        }

        public static Label Label(string text, string cls)
        {
            var l = new Label(text);
            if (!string.IsNullOrEmpty(cls)) l.AddToClassList(cls);
            return l;
        }

        /// <summary>A text label that never interprets rich-text tags (for user-supplied text).</summary>
        public static Label PlainLabel(string text, string cls)
        {
            var l = Label(text, cls);
            l.enableRichText = false;
            return l;
        }

        /// <summary>Finds whether a focus target is (inside) an editable text field.</summary>
        public static bool IsTextInput(IEventHandler target)
        {
            if (target is not VisualElement e) return false;
            for (var p = e; p != null; p = p.parent)
            {
                if (p is TextField tf) return !tf.isReadOnly;
                if (p is IPanel) break;
            }
            return false;
        }
    }
}
