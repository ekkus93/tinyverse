using System;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>A small line of controls help for first-time users; the glue remembers dismissal.</summary>
    public sealed class HintLine : UIPanel
    {
        public const string DefaultText = "WASD move  ·  Right-drag look  ·  F fly  ·  Enter chat  ·  B build  ·  P people  ·  Esc menu";

        readonly Label _text;

        /// <summary>The user closed the hint; store it and call <c>Hide()</c> on later sessions.</summary>
        public event Action Dismissed;

        public string Text => _text.text;

        internal HintLine(VisualElement root) : base(root)
        {
            _text = Q<Label>("hintText");
            _text.text = DefaultText;
            var close = Q<Button>("hintClose");
            close.Add(new Icon(IconKind.Close));
            close.clicked += Dismiss;
        }

        public void SetText(string text) => _text.text = string.IsNullOrEmpty(text) ? DefaultText : text;

        public void Dismiss()
        {
            if (!IsVisible) return;
            Hide();
            Dismissed?.Invoke();
        }
    }
}
