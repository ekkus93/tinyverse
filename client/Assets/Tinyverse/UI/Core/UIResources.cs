using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Paths of every UI asset under <c>UI/Resources/</c>. All loading goes through here so that a
    /// missing or renamed asset fails loudly (and in a test) rather than rendering nothing.
    /// </summary>
    public static class UIResources
    {
        public const string Folder = "Tinyverse/UI/";
        public const string Theme = Folder + "TinyverseTheme";
        /// <summary>
        /// Editor-made PanelSettings. It carries references to UI Toolkit's runtime shaders and ICU
        /// text data that a PanelSettings made with CreateInstance in a player would lack.
        /// </summary>
        public const string PanelSettingsAsset = Folder + "TinyversePanelSettings";
        public const string ThemeStyles = Folder + "Theme";
        public const string Connect = Folder + "Connect";
        public const string Host = Folder + "Host";
        public const string Hud = Folder + "Hud";
        public const string Build = Folder + "Build";
        public const string Overlay = Folder + "Overlay";

        /// <summary>Every asset the UI loads, with the type it must load as.</summary>
        public static IReadOnlyList<(string path, Type type)> All { get; } = new List<(string, Type)>
        {
            (Theme, typeof(ThemeStyleSheet)),
            (PanelSettingsAsset, typeof(PanelSettings)),
            (ThemeStyles, typeof(StyleSheet)),
            (Connect, typeof(VisualTreeAsset)),
            (Connect, typeof(StyleSheet)),
            (Host, typeof(VisualTreeAsset)),
            (Host, typeof(StyleSheet)),
            (Hud, typeof(VisualTreeAsset)),
            (Hud, typeof(StyleSheet)),
            (Build, typeof(VisualTreeAsset)),
            (Build, typeof(StyleSheet)),
            (Overlay, typeof(VisualTreeAsset)),
            (Overlay, typeof(StyleSheet)),
        };

        public static T Load<T>(string path) where T : UnityEngine.Object
        {
            var asset = Resources.Load<T>(path);
            if (asset == null)
                throw new InvalidOperationException($"Tinyverse UI: missing {typeof(T).Name} at Resources/{path}");
            return asset;
        }

        /// <summary>Clones a UXML asset and returns its single top-level element.</summary>
        public static VisualElement Clone(string path)
        {
            var tree = Load<VisualTreeAsset>(path).Instantiate();
            if (tree.childCount != 1)
                throw new InvalidOperationException($"Tinyverse UI: {path}.uxml must have exactly one top-level element");
            var root = tree[0];
            // Keep the style sheets the UXML linked: they live on the TemplateContainer.
            foreach (var sheet in StyleSheetsOf(tree)) root.styleSheets.Add(sheet);
            root.RemoveFromHierarchy();
            return root;
        }

        static IEnumerable<StyleSheet> StyleSheetsOf(VisualElement e)
        {
            for (int i = 0; i < e.styleSheets.count; i++) yield return e.styleSheets[i];
        }
    }
}
