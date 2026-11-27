using System;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>A modal yes/no question. Escape or Cancel answers no.</summary>
    public sealed class ConfirmDialog : UIPanel
    {
        readonly Label _title;
        readonly Label _message;
        readonly Button _confirm;
        readonly Button _cancel;
        Action _onConfirm;
        Action _onCancel;

        public string Title => _title.text;
        public string Message => _message.text;

        internal ConfirmDialog(VisualElement root) : base(root)
        {
            _title = Q<Label>("confirmTitle");
            _message = Q<Label>("confirmMessage");
            _message.enableRichText = false;
            _confirm = Q<Button>("confirmOk");
            _cancel = Q<Button>("confirmCancel");
            _confirm.clicked += Confirm;
            _cancel.clicked += Cancel;
            Hide();
        }

        /// <summary>Asks a question; exactly one of the callbacks runs when the user answers.</summary>
        public void Ask(string title, string message, string confirmText, Action onConfirm, Action onCancel = null)
        {
            _title.text = title;
            _message.text = message;
            _confirm.text = string.IsNullOrEmpty(confirmText) ? "OK" : confirmText;
            _onConfirm = onConfirm;
            _onCancel = onCancel;
            Show();
            _cancel.schedule.Execute(() => { if (IsVisible) _cancel.Focus(); });
        }

        public void Confirm()
        {
            if (!IsVisible) return;
            var cb = _onConfirm;
            Close();
            cb?.Invoke();
        }

        public void Cancel()
        {
            if (!IsVisible) return;
            var cb = _onCancel;
            Close();
            cb?.Invoke();
        }

        void Close()
        {
            _onConfirm = null;
            _onCancel = null;
            Hide();
        }
    }
}
