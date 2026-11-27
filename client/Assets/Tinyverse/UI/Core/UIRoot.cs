using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Owns the whole UI: one <see cref="UIDocument"/> with a runtime copy of the PanelSettings asset,
    /// the front-end screens (Connect, Host), the in-world HUD and the overlays (menu, settings,
    /// confirmation, toasts). Create it once with <see cref="Create"/>.
    /// </summary>
    public sealed class UIRoot : MonoBehaviour
    {
        /// <summary>The layout is designed at this size; it scales with the window from there.</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1440, 810);
        /// <summary>0 matches width, 1 matches height; 0.5 blends them on a log scale (like uGUI's CanvasScaler).</summary>
        public const float MatchWidthOrHeight = 0.5f;
        public const int SortingOrder = 100;
        /// <summary>Unity's built-in runtime font, present in every player build.</summary>
        public const string BuiltinFontName = "LegacyRuntime.ttf";

        UIDocument _document;
        PanelSettings _panelSettings;
        VisualElement _root;
        VisualElement _screenLayer;
        float _uiScale = 1f;
        Vector2Int _lastScreen;
        bool _textFocused;
        int _textFocusLostFrame = -10;
        bool _settingsFromMenu;
        bool _buildMode;
        bool _modalOpen;

        public UIDocument Document => _document;
        public PanelSettings PanelSettings => _panelSettings;
        /// <summary>The document's root element (class <c>tv-root</c>).</summary>
        public VisualElement RootElement => _root;

        public UIScreen Screen { get; private set; } = UIScreen.None;

        public ConnectScreen Connect { get; private set; }
        public HostScreen Host { get; private set; }
        public Hud Hud { get; private set; }
        public StatusBar StatusBar => Hud.StatusBar;
        public ChatPanel Chat => Hud.Chat;
        public PeopleList People => Hud.People;
        public BuildPanel Build => Hud.Build;
        public HintLine Hint => Hud.Hint;
        public MenuPanel Menu { get; private set; }
        public SettingsPanel Settings { get; private set; }
        public ConfirmDialog Confirm { get; private set; }
        public Toasts Toasts { get; private set; }

        /// <summary>When true (default) UIRoot reads Escape, Enter, B, P and 1-5 from the keyboard itself.</summary>
        public bool HotkeysEnabled { get; set; } = true;

        /// <summary>True while an editable text field has keyboard focus. Movement keys should be ignored.</summary>
        public bool IsTextInputFocused => _textFocused;

        /// <summary>Raised when <see cref="IsTextInputFocused"/> changes.</summary>
        public event Action<bool> TextInputFocusChanged;

        /// <summary>True while the menu, settings or a confirmation is open in-world (release the cursor, pause input).</summary>
        public bool IsModalOpen => _modalOpen;
        public event Action<bool> ModalOpenChanged;

        /// <summary>The build button or the B key. The glue decides and calls <see cref="SetBuildMode"/>.</summary>
        public event Action BuildModeToggled;

        public bool IsBuildMode => _buildMode;

        public event Action<UIScreen> ScreenChanged;

        /// <summary>True when the mouse pointer is over interactive UI; 3D camera and build tools should ignore it.</summary>
        public bool IsPointerOverUI
        {
            get
            {
                var mouse = Mouse.current;
                return mouse != null && IsPointerOverUIAt(mouse.position.ReadValue());
            }
        }

        /// <summary>Creates the UI under a new GameObject (optionally parented) and shows the Connect screen.</summary>
        public static UIRoot Create(Transform parent = null)
        {
            var go = new GameObject("Tinyverse UI");
            go.SetActive(false);
            if (parent != null) go.transform.SetParent(parent, false);

            // A runtime copy of the asset: keeps its shader references, and changing the scale never
            // dirties the asset itself.
            var settings = Instantiate(UIResources.Load<PanelSettings>(UIResources.PanelSettingsAsset));
            settings.name = "Tinyverse PanelSettings (runtime)";
            settings.themeStyleSheet = UIResources.Load<ThemeStyleSheet>(UIResources.Theme);
            settings.scaleMode = PanelScaleMode.ConstantPixelSize;
            settings.scale = 1f;
            settings.sortingOrder = SortingOrder;
            settings.clearColor = false;

            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = settings;
            var ui = go.AddComponent<UIRoot>();
            ui._document = doc;
            ui._panelSettings = settings;
            go.SetActive(true);
            ui.Construct();
            return ui;
        }

        void Construct()
        {
            _root = _document.rootVisualElement;
            _root.AddToClassList("tv-root");
            _root.pickingMode = PickingMode.Ignore;
            // The runtime theme supplies no font of its own; use the one Unity ships in every player's
            // built-in resources, so text renders without importing or downloading anything.
            var font = Resources.GetBuiltinResource<Font>(BuiltinFontName);
            if (font != null) _root.style.unityFontDefinition = FontDefinition.FromFont(font);

            _screenLayer = Layer("tv-screen-layer");
            var hudLayer = Layer("tv-hud-layer");
            var overlay = UIResources.Clone(UIResources.Overlay);
            overlay.pickingMode = PickingMode.Ignore;

            Confirm = new ConfirmDialog(UIUtil.Require<VisualElement>(overlay, "confirmDialog"));
            Menu = new MenuPanel(UIUtil.Require<VisualElement>(overlay, "menuPanel"));
            Settings = new SettingsPanel(UIUtil.Require<VisualElement>(overlay, "settingsPanel"));
            Toasts = new Toasts(UIUtil.Require<VisualElement>(overlay, "toasts"));
            foreach (var scrim in overlay.Query(className: "tv-scrim").ToList())
                scrim.RegisterCallback<PointerDownEvent>(e => { if (e.target == scrim) HandleEscape(); });

            Connect = new ConnectScreen(UIResources.Clone(UIResources.Connect));
            Host = new HostScreen(UIResources.Clone(UIResources.Host));
            Hud = new Hud(UIResources.Clone(UIResources.Hud), UIResources.Clone(UIResources.Build), Confirm);
            _screenLayer.Add(Connect.Root);
            _screenLayer.Add(Host.Root);
            hudLayer.Add(Hud.Root);
            _root.Add(_screenLayer);
            _root.Add(hudLayer);
            _root.Add(overlay);

            Wire();
            _root.RegisterCallback<FocusInEvent>(e => RefreshTextFocus(e.target as Focusable, true), TrickleDown.TrickleDown);
            _root.RegisterCallback<FocusOutEvent>(e => RefreshTextFocus(e.relatedTarget, true), TrickleDown.TrickleDown);
            // Nothing in the HUD keeps focus after a click, so Space and WASD never "press" a HUD button.
            Hud.Root.RegisterCallback<PointerUpEvent>(_ => Hud.Root.schedule.Execute(BlurNonText), TrickleDown.TrickleDown);

            SetBuildMode(false);
            ApplyScale(true);
            ShowScreen(UIScreen.Connect);
        }

        static VisualElement Layer(string cls)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList("tv-layer");
            e.AddToClassList(cls);
            return e;
        }

        void Wire()
        {
            Connect.SettingsRequested += () => OpenSettings();
            Menu.SettingsRequested += () => OpenSettings();
            Menu.VisibilityChanged += _ => RefreshModal();
            Settings.VisibilityChanged += _ => RefreshModal();
            Confirm.VisibilityChanged += _ => RefreshModal();
            Settings.Closed += () =>
            {
                if (_settingsFromMenu && Screen == UIScreen.Hud) Menu.Show();
                _settingsFromMenu = false;
            };
            Settings.SettingsChanged += s => UIScale = s.UIScale;
            StatusBar.MenuClicked += () => { if (Menu.IsVisible) Menu.Hide(); else OpenMenu(); };
            StatusBar.PeopleClicked += TogglePeople;
            StatusBar.BuildClicked += () => BuildModeToggled?.Invoke();
            Build.CloseRequested += () => BuildModeToggled?.Invoke();
        }

        /// <summary>Switches between Connect, Host and the in-world HUD. Overlays close; toasts stay.</summary>
        public void ShowScreen(UIScreen screen)
        {
            _root.focusController?.focusedElement?.Blur();
            Menu.Hide();
            Settings.Hide();
            Confirm.Cancel();
            People.Actions.Close();
            if (screen != UIScreen.Hud && Chat.IsInputOpen) Chat.CloseInput();
            Connect.SetVisible(screen == UIScreen.Connect);
            Host.SetVisible(screen == UIScreen.Host);
            Hud.SetVisible(screen == UIScreen.Hud);
            UIUtil.SetClass(_screenLayer, "tv-screen-layer--active", screen == UIScreen.Connect || screen == UIScreen.Host);
            bool changed = Screen != screen;
            Screen = screen;
            RefreshModal();
            RefreshTextFocus();
            if (changed) ScreenChanged?.Invoke(screen);
        }

        /// <summary>Shows or hides the build panel and highlights the status-bar button.</summary>
        public void SetBuildMode(bool active)
        {
            _buildMode = active;
            Build.SetVisible(active);
            StatusBar.SetBuildActive(active);
        }

        public void OpenChat(ChatChannel? channel = null)
        {
            if (Screen != UIScreen.Hud) return;
            Chat.OpenInput(channel);
        }

        public void TogglePeople()
        {
            if (Screen != UIScreen.Hud) return;
            People.Toggle();
        }

        public void OpenMenu()
        {
            if (Screen != UIScreen.Hud) return;
            if (Chat.IsInputOpen) Chat.CloseInput();
            Settings.Hide();
            Menu.Show();
        }

        public void CloseMenu() => Menu.Hide();

        /// <summary>Opens settings; from the menu it returns to the menu when closed.</summary>
        public void OpenSettings()
        {
            _settingsFromMenu = Menu.IsVisible;
            Menu.Hide();
            Settings.Show();
        }

        /// <summary>
        /// Escape: leave the focused text field, else close the topmost panel, else (in-world) open the menu.
        /// Returns what it did, for tests and logging.
        /// </summary>
        public string HandleEscape()
        {
            if (_textFocused)
            {
                if (Chat.IsInputOpen) Chat.CloseInput();
                else _root.focusController?.focusedElement?.Blur();
                RefreshTextFocus();
                return "blur";
            }
            if (Confirm.IsVisible) { Confirm.Cancel(); return "confirm"; }
            if (Settings.IsVisible) { Settings.Close(); return "settings"; }
            if (People.Actions.IsOpen) { People.Actions.Close(); return "actions"; }
            switch (Screen)
            {
                case UIScreen.Hud:
                    if (Menu.IsVisible) { Menu.Hide(); return "menu"; }
                    if (People.IsVisible) { People.Hide(); return "people"; }
                    OpenMenu();
                    return "open-menu";
                case UIScreen.Host:
                    if (Host.State == HostState.Running) { Host.RequestJoin(); return "host-join"; }
                    Host.RequestBack();
                    return "host-back";
                case UIScreen.Connect:
                    if (Connect.IsBusy) { Connect.RequestCancel(); return "cancel"; }
                    return "none";
            }
            return "none";
        }

        /// <summary>
        /// Handles one hotkey as if pressed. Returns true if the UI used it. UIRoot calls this itself
        /// from the keyboard when <see cref="HotkeysEnabled"/>; the glue may call it instead.
        /// </summary>
        public bool HandleKey(Key key)
        {
            if (key == Key.Escape)
            {
                HandleEscape();
                return true;
            }
            if (_textFocused || Screen != UIScreen.Hud || _modalOpen) return false;
            switch (key)
            {
                case Key.Enter:
                case Key.NumpadEnter:
                    OpenChat();
                    return true;
                case Key.B:
                    BuildModeToggled?.Invoke();
                    return true;
                case Key.P:
                    TogglePeople();
                    return true;
                case Key.Digit1: case Key.Digit2: case Key.Digit3: case Key.Digit4: case Key.Digit5:
                    if (!_buildMode) return false;
                    Build.ChooseTool((BuildTool)(key - Key.Digit1));
                    return true;
            }
            return false;
        }

        /// <summary>True when <paramref name="screenPosition"/> (pixels, origin bottom-left, as the Input System reports) is over interactive UI.</summary>
        public bool IsPointerOverUIAt(Vector2 screenPosition)
        {
            var panel = _root?.panel;
            if (panel == null) return false;
            var flipped = new Vector2(screenPosition.x, UnityEngine.Screen.height - screenPosition.y);
            var picked = panel.Pick(RuntimePanelUtils.ScreenToPanel(panel, flipped));
            return picked != null && picked != _root && picked.pickingMode == PickingMode.Position;
        }

        /// <summary>Extra UI scale on top of the reference-resolution scale, 0.75 to 1.5.</summary>
        public float UIScale
        {
            get => _uiScale;
            set
            {
                float v = Mathf.Clamp(value <= 0f ? 1f : value, SettingsPanel.MinUIScale, SettingsPanel.MaxUIScale);
                if (Mathf.Approximately(v, _uiScale)) return;
                _uiScale = v;
                ApplyScale(true);
            }
        }

        /// <summary>The panel scale for a window size: reference-resolution matching times the user's UI scale.</summary>
        public static float ComputeScale(int width, int height, float uiScale)
        {
            if (width <= 0 || height <= 0) return uiScale;
            float logW = Mathf.Log(width / ReferenceResolution.x, 2f);
            float logH = Mathf.Log(height / ReferenceResolution.y, 2f);
            float s = Mathf.Pow(2f, Mathf.Lerp(logW, logH, MatchWidthOrHeight));
            return s * uiScale;
        }

        void ApplyScale(bool force)
        {
            var size = new Vector2Int(UnityEngine.Screen.width, UnityEngine.Screen.height);
            if (!force && size == _lastScreen) return;
            _lastScreen = size;
            _panelSettings.scale = ComputeScale(size.x, size.y, _uiScale);
        }

        void Update()
        {
            if (_root == null) return;
            ApplyScale(false);
            RefreshTextFocus();
            float dt = Time.unscaledDeltaTime;
            Toasts.Tick(dt);
            Chat.Tick(dt);
            if (HotkeysEnabled) PollKeyboard();
        }

        void PollKeyboard()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            // The key that closed a text field (Enter, Escape) must not act again this frame or the next.
            bool justLeftText = Time.frameCount - _textFocusLostFrame <= 1;
            if (kb.escapeKey.wasPressedThisFrame && !justLeftText) HandleKey(Key.Escape);
            if (_textFocused) return;
            if ((kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) && !justLeftText) HandleKey(Key.Enter);
            bool modifier = kb.ctrlKey.isPressed || kb.altKey.isPressed || kb.leftMetaKey.isPressed || kb.rightMetaKey.isPressed;
            if (modifier) return;
            if (kb.bKey.wasPressedThisFrame) HandleKey(Key.B);
            if (kb.pKey.wasPressedThisFrame) HandleKey(Key.P);
            for (var k = Key.Digit1; k <= Key.Digit5; k++)
                if (kb[k].wasPressedThisFrame) HandleKey(k);
        }

        void BlurNonText()
        {
            var focused = _root.focusController?.focusedElement as VisualElement;
            if (focused == null || UIUtil.IsTextInput(focused)) return;
            if (Hud.Root.Contains(focused)) focused.Blur();
        }

        void RefreshTextFocus(Focusable next = null, bool leaving = false)
        {
            // During FocusOut the controller still reports the old element; the event names the new one.
            var focused = leaving ? next : _root?.focusController?.focusedElement;
            bool text = focused != null && UIUtil.IsTextInput(focused) && IsDisplayed(focused as VisualElement);
            if (text == _textFocused) return;
            _textFocused = text;
            if (!text) _textFocusLostFrame = Time.frameCount;
            TextInputFocusChanged?.Invoke(text);
        }

        static bool IsDisplayed(VisualElement e)
        {
            for (var p = e; p != null; p = p.hierarchy.parent)
                if (p.style.display == DisplayStyle.None) return false;
            return true;
        }

        void RefreshModal()
        {
            bool modal = Screen == UIScreen.Hud && (Menu.IsVisible || Settings.IsVisible || Confirm.IsVisible);
            if (modal == _modalOpen) return;
            _modalOpen = modal;
            ModalOpenChanged?.Invoke(modal);
        }

        void OnDestroy()
        {
            if (_panelSettings != null) Destroy(_panelSettings);
        }
    }
}
