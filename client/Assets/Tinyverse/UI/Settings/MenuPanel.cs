using System;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>The in-world menu opened with Escape.</summary>
    public sealed class MenuPanel : UIPanel
    {
        readonly Label _world;
        readonly Button _resume;

        /// <summary>Resume was chosen; the menu has already closed itself.</summary>
        public event Action ResumeRequested;
        /// <summary>Settings was chosen; UIRoot opens the settings panel.</summary>
        public event Action SettingsRequested;
        public event Action DisconnectRequested;
        public event Action QuitRequested;

        internal MenuPanel(VisualElement root) : base(root)
        {
            _world = Q<Label>("menuWorld");
            _world.enableRichText = false;
            _resume = Q<Button>("menuResume");
            _resume.clicked += () => { Hide(); ResumeRequested?.Invoke(); };
            Q<Button>("menuSettings").clicked += () => SettingsRequested?.Invoke();
            Q<Button>("menuDisconnect").clicked += () => DisconnectRequested?.Invoke();
            Q<Button>("menuQuit").clicked += () => QuitRequested?.Invoke();
            Hide();
        }

        public void SetWorldName(string name) => _world.text = string.IsNullOrEmpty(name) ? "Tinyverse" : name;

        protected override void OnShown() => _resume.schedule.Execute(() => { if (IsVisible) _resume.Focus(); });
    }
}
