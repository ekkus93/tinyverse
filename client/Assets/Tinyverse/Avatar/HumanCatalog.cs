using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>A body preset: the outfit model that carries the skeleton, the head model and the skin texture.</summary>
    public sealed class BodyPreset
    {
        /// <summary>The name used in <see cref="AvatarLook.BodyPreset"/> (and on the wire).</summary>
        public string Id { get; }
        /// <summary>A label for menus.</summary>
        public string Label { get; }
        /// <summary>Model (under <see cref="HumanCatalog.ModelsFolder"/>) with the humanoid skeleton and the clothes.</summary>
        public string SkeletonModel { get; }
        /// <summary>Model whose head, eyes and eyebrows are taken.</summary>
        public string HeadModel { get; }
        /// <summary>Material (under <see cref="HumanCatalog.MaterialsFolder"/>) for the textured face.</summary>
        public string SkinMaterial { get; }
        /// <summary>The hair style used when the look names none (or an unknown one).</summary>
        public string DefaultHairStyle { get; }

        internal BodyPreset(string id, string label, string skeletonModel, string headModel, string skinMaterial, string defaultHair)
        {
            Id = id;
            Label = label;
            SkeletonModel = skeletonModel;
            HeadModel = headModel;
            SkinMaterial = skinMaterial;
            DefaultHairStyle = defaultHair;
        }

        public override string ToString() => Id;
    }

    /// <summary>A hair style: zero or more hair models, chosen per body preset.</summary>
    public sealed class HairStyle
    {
        public string Id { get; }
        public string Label { get; }
        private readonly string[] masculine, feminine;

        internal HairStyle(string id, string label, string[] masculineModels, string[] feminineModels)
        {
            Id = id;
            Label = label;
            masculine = masculineModels;
            feminine = feminineModels;
        }

        /// <summary>The hair models to put on a body of the given preset.</summary>
        public IReadOnlyList<string> ModelsFor(BodyPreset preset) =>
            preset != null && preset.Id == HumanCatalog.Feminine ? feminine : masculine;

        public override string ToString() => Id;
    }

    /// <summary>
    /// Everything a human avatar can be made of, and how an <see cref="AvatarLook"/> maps onto it. Pure data and
    /// rules; <see cref="HumanFigure"/> builds the models.
    /// </summary>
    public static class HumanCatalog
    {
        public const string ResourceRoot = "Tinyverse/Avatar/";
        public const string ModelsFolder = ResourceRoot + "Models/";
        public const string AnimationsFolder = ResourceRoot + "Animations/";
        public const string MaterialsFolder = ResourceRoot;

        public const string Masculine = "masculine";
        public const string Feminine = "feminine";

        /// <summary>The preset an empty <see cref="AvatarLook.BodyPreset"/> means.</summary>
        public const string DefaultPreset = Masculine;

        public const string SkinPlainMaterial = "AvatarSkinPlain";
        public const string ClothMaterial = "AvatarCloth";
        public const string EyeMaterial = "AvatarEye";

        public static readonly IReadOnlyList<BodyPreset> Presets = new[]
        {
            new BodyPreset(Masculine, "Masculine", "Male_Peasant", "Superhero_Male_FullBody", "AvatarSkinMasculine", "short"),
            new BodyPreset(Feminine, "Feminine", "Female_Peasant", "Superhero_Female_FullBody", "AvatarSkinFeminine", "long"),
        };

        public static readonly IReadOnlyList<HairStyle> HairStyles = new[]
        {
            new HairStyle("short", "Short", new[] { "Hair_SimpleParted" }, new[] { "Hair_SimpleParted" }),
            new HairStyle("buzzed", "Buzzed", new[] { "Hair_Buzzed" }, new[] { "Hair_BuzzedFemale" }),
            new HairStyle("long", "Long", new[] { "Hair_Long" }, new[] { "Hair_Long" }),
            new HairStyle("buns", "Buns", new[] { "Hair_Buns" }, new[] { "Hair_Buns" }),
            new HairStyle("beard", "Short + beard", new[] { "Hair_SimpleParted", "Hair_Beard" }, new[] { "Hair_SimpleParted", "Hair_Beard" }),
            new HairStyle("bald", "Bald", new string[0], new string[0]),
        };

        /// <summary>Other spellings accepted for the presets (agents may send these).</summary>
        private static readonly Dictionary<string, string> PresetAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "male", Masculine }, { "man", Masculine }, { "m", Masculine }, { "masc", Masculine },
            { "female", Feminine }, { "woman", Feminine }, { "f", Feminine }, { "fem", Feminine },
        };

        /// <summary>
        /// The body preset a look asks for: empty means <see cref="DefaultPreset"/>; names are matched without case
        /// and with a few aliases. Null for a name this client does not know (the placeholder figure is drawn).
        /// </summary>
        public static BodyPreset FindPreset(string name)
        {
            string key = string.IsNullOrWhiteSpace(name) ? DefaultPreset : name.Trim();
            if (PresetAliases.TryGetValue(key, out var alias)) key = alias;
            foreach (var p in Presets)
                if (string.Equals(p.Id, key, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>The hair style a look asks for, or the preset's default for an empty or unknown name.</summary>
        public static HairStyle FindHairStyle(string name, BodyPreset preset)
        {
            if (!string.IsNullOrWhiteSpace(name))
                foreach (var h in HairStyles)
                    if (string.Equals(h.Id, name.Trim(), StringComparison.OrdinalIgnoreCase)) return h;
            string fallback = preset?.DefaultHairStyle ?? "short";
            foreach (var h in HairStyles)
                if (h.Id == fallback) return h;
            return HairStyles[0];
        }

        /// <summary>True for the all-zero colour that means "not set".</summary>
        public static bool IsUnset(Color c) => c.r == 0f && c.g == 0f && c.b == 0f && c.a == 0f;

        /// <summary>The look's colour, or <paramref name="fallback"/> when unset; always opaque.</summary>
        public static Color Or(Color c, Color fallback)
        {
            var r = IsUnset(c) ? fallback : c;
            r.a = 1f;
            return r;
        }

        /// <summary>The colours a human figure draws for a look: unset colours take the defaults.</summary>
        public static HumanColors ColorsFor(AvatarLook look)
        {
            var d = AvatarLook.Default;
            var top = IsUnset(look.TopColor) ? Or(look.BodyColor, d.TopColor) : Or(look.TopColor, d.TopColor);
            return new HumanColors
            {
                Skin = Or(look.SkinColor, d.SkinColor),
                Hair = Or(look.HairColor, d.HairColor),
                Top = top,
                Bottom = Or(look.BottomColor, d.BottomColor),
            };
        }
    }

    /// <summary>The colours of a human figure, opaque.</summary>
    public struct HumanColors
    {
        public Color Skin, Hair, Top, Bottom;
    }
}
