using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>Settings: values in and out as <see cref="ClientSettings"/>; storing them is the glue's job.</summary>
    public sealed class SettingsPanel : UIPanel
    {
        public const float MinSensitivity = 0.1f, MaxSensitivity = 5f;
        public const float MinFov = 50f, MaxFov = 110f;
        public const float MinUIScale = 0.75f, MaxUIScale = 1.5f;

        public static readonly IReadOnlyList<(string keys, string action)> DefaultKeyBindings = new[]
        {
            ("W A S D", "Move"),
            ("Shift", "Run"),
            ("F", "Toggle fly"),
            ("Space / Ctrl", "Up / down while flying"),
            ("Right mouse drag", "Look around"),
            ("Mouse wheel", "Zoom"),
            ("Enter", "Chat"),
            ("Tab (in chat)", "Switch local / world"),
            ("Up / Down (in chat)", "Previous messages"),
            ("B", "Build mode"),
            ("P", "People"),
            ("Esc", "Close panel / menu"),
        };

        readonly Slider _sensitivity;
        readonly Toggle _invertY;
        readonly Slider _fov;
        readonly DropdownField _quality;
        readonly Toggle _fullscreen;
        readonly Slider _volume;
        readonly Slider _uiScale;
        readonly Label _sensitivityValue, _fovValue, _volumeValue, _uiScaleValue;
        readonly VisualElement _bindings;
        ClientSettings _settings = ClientSettings.Default;

        /// <summary>Raised with the full settings after any change by the user.</summary>
        public event Action<ClientSettings> SettingsChanged;
        /// <summary>The panel was closed with Done or Escape.</summary>
        public event Action Closed;

        public ClientSettings Settings => _settings;

        public IReadOnlyList<string> QualityLevels => _quality.choices;

        internal SettingsPanel(VisualElement root) : base(root)
        {
            _sensitivity = Q<Slider>("settingsSensitivity");
            _invertY = Q<Toggle>("settingsInvertY");
            _fov = Q<Slider>("settingsFov");
            _quality = Q<DropdownField>("settingsQuality");
            _fullscreen = Q<Toggle>("settingsFullscreen");
            _volume = Q<Slider>("settingsVolume");
            _uiScale = Q<Slider>("settingsUIScale");
            _sensitivityValue = Q<Label>("settingsSensitivityValue");
            _fovValue = Q<Label>("settingsFovValue");
            _volumeValue = Q<Label>("settingsVolumeValue");
            _uiScaleValue = Q<Label>("settingsUIScaleValue");
            _bindings = Q<VisualElement>("settingsBindings");

            _sensitivity.lowValue = MinSensitivity; _sensitivity.highValue = MaxSensitivity;
            _fov.lowValue = MinFov; _fov.highValue = MaxFov;
            _volume.lowValue = 0f; _volume.highValue = 1f;
            _uiScale.lowValue = MinUIScale; _uiScale.highValue = MaxUIScale;
            _quality.choices = new List<string> { "Default" };

            _sensitivity.RegisterValueChangedCallback(e => Change(s => { s.MouseSensitivity = e.newValue; return s; }));
            _invertY.RegisterValueChangedCallback(e => Change(s => { s.InvertY = e.newValue; return s; }));
            _fov.RegisterValueChangedCallback(e => Change(s => { s.FieldOfView = Mathf.Round(e.newValue); return s; }));
            _quality.RegisterValueChangedCallback(_ => Change(s => { s.QualityIndex = Mathf.Max(0, _quality.index); return s; }));
            _fullscreen.RegisterValueChangedCallback(e => Change(s => { s.Fullscreen = e.newValue; return s; }));
            _volume.RegisterValueChangedCallback(e => Change(s => { s.MasterVolume = e.newValue; return s; }));
            _uiScale.RegisterValueChangedCallback(e => Change(s => { s.UIScale = Mathf.Round(e.newValue * 20f) / 20f; return s; }));
            Q<Button>("settingsDone").clicked += Close;

            SetKeyBindings(DefaultKeyBindings);
            SetSettings(_settings);
            Hide();
        }

        void Change(Func<ClientSettings, ClientSettings> apply)
        {
            _settings = apply(_settings);
            RefreshValueLabels();
            SettingsChanged?.Invoke(_settings);
        }

        /// <summary>Shows the given values without raising <see cref="SettingsChanged"/>. Out-of-range values are clamped.</summary>
        public void SetSettings(ClientSettings s)
        {
            s.MouseSensitivity = Mathf.Clamp(s.MouseSensitivity, MinSensitivity, MaxSensitivity);
            s.FieldOfView = Mathf.Clamp(s.FieldOfView, MinFov, MaxFov);
            s.MasterVolume = Mathf.Clamp01(s.MasterVolume);
            s.UIScale = Mathf.Clamp(s.UIScale <= 0f ? 1f : s.UIScale, MinUIScale, MaxUIScale);
            s.QualityIndex = Mathf.Clamp(s.QualityIndex, 0, Mathf.Max(0, _quality.choices.Count - 1));
            _settings = s;
            _sensitivity.SetValueWithoutNotify(s.MouseSensitivity);
            _invertY.SetValueWithoutNotify(s.InvertY);
            _fov.SetValueWithoutNotify(s.FieldOfView);
            _fullscreen.SetValueWithoutNotify(s.Fullscreen);
            _volume.SetValueWithoutNotify(s.MasterVolume);
            _uiScale.SetValueWithoutNotify(s.UIScale);
            if (_quality.choices.Count > 0) _quality.SetValueWithoutNotify(_quality.choices[s.QualityIndex]);
            RefreshValueLabels();
        }

        /// <summary>Sets the names in the graphics-quality list (for example from <c>QualitySettings.names</c>).</summary>
        public void SetQualityLevels(IReadOnlyList<string> names)
        {
            var list = new List<string>();
            if (names != null) list.AddRange(names);
            if (list.Count == 0) list.Add("Default");
            _quality.choices = list;
            SetSettings(_settings);
        }

        /// <summary>Replaces the read-only key bindings reference.</summary>
        public void SetKeyBindings(IReadOnlyList<(string keys, string action)> bindings)
        {
            _bindings.Clear();
            foreach (var (keys, action) in bindings)
            {
                var row = new VisualElement();
                row.AddToClassList("tv-binding");
                row.Add(UIUtil.PlainLabel(keys, "tv-binding__keys"));
                row.Add(UIUtil.PlainLabel(action, "tv-binding__action"));
                _bindings.Add(row);
            }
        }

        public int KeyBindingCount => _bindings.childCount;

        public void Close()
        {
            if (!IsVisible) return;
            Hide();
            Closed?.Invoke();
        }

        protected override void OnShown() => _sensitivity.schedule.Execute(() => { if (IsVisible) _sensitivity.Focus(); });

        void RefreshValueLabels()
        {
            _sensitivityValue.text = _settings.MouseSensitivity.ToString("0.00", CultureInfo.InvariantCulture);
            _fovValue.text = Mathf.RoundToInt(_settings.FieldOfView) + "°";
            _volumeValue.text = Mathf.RoundToInt(_settings.MasterVolume * 100f) + "%";
            _uiScaleValue.text = Mathf.RoundToInt(_settings.UIScale * 100f) + "%";
        }

        // Test hooks: drive the controls as a user would.
        internal Slider SensitivitySlider => _sensitivity;
        internal Toggle InvertYToggle => _invertY;
        internal Slider FovSlider => _fov;
        internal DropdownField QualityDropdown => _quality;
        internal Toggle FullscreenToggle => _fullscreen;
        internal Slider VolumeSlider => _volume;
        internal Slider UIScaleSlider => _uiScale;
    }
}
