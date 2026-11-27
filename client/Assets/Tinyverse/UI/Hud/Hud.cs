using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>The in-world overlay: status bar, chat, people list, build panel and hint line.</summary>
    public sealed class Hud : UIPanel
    {
        public StatusBar StatusBar { get; }
        public ChatPanel Chat { get; }
        public PeopleList People { get; }
        public BuildPanel Build { get; }
        public HintLine Hint { get; }

        internal Hud(VisualElement root, VisualElement buildRoot, ConfirmDialog confirm) : base(root)
        {
            root.pickingMode = PickingMode.Ignore;
            foreach (var layer in root.Query(className: "tv-layer").ToList()) layer.pickingMode = PickingMode.Ignore;
            StatusBar = new StatusBar(Q<VisualElement>("statusBar"));
            Chat = new ChatPanel(Q<VisualElement>("chatPanel"));
            People = new PeopleList(Q<VisualElement>("peoplePanel"), confirm);
            Hint = new HintLine(Q<VisualElement>("hintLine"));
            Q<VisualElement>("buildSlot").Add(buildRoot);
            Build = new BuildPanel(buildRoot);
            People.VisibilityChanged += StatusBar.SetPeopleOpen;
        }
    }
}
