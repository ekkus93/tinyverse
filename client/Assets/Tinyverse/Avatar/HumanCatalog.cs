using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tinyverse.Avatars
{
    /// <summary>The two sets of human models: generated MakeHuman people (the default) and the older Quaternius figures.</summary>
    public enum AvatarStyle
    {
        /// <summary>MakeHuman (MPFB) people made by tools/avatars: everyday clothes, realistic faces.</summary>
        Modern,
        /// <summary>The CC0 Quaternius figures in a fantasy outfit, kept for comparison (<c>--avatar-style=classic</c>).</summary>
        Classic,
    }

    /// <summary>A body preset: the models and materials that make one kind of person.</summary>
    public sealed class BodyPreset
    {
        /// <summary>The name used in <see cref="AvatarLook.BodyPreset"/> (and on the wire).</summary>
        public string Id { get; }
        /// <summary>A label for menus.</summary>
        public string Label { get; }
        /// <summary>Which set of models this preset belongs to.</summary>
        public AvatarStyle Style { get; }
        /// <summary><see cref="HumanCatalog.Masculine"/> or <see cref="HumanCatalog.Feminine"/>: picks the hair models.</summary>
        public string Gender { get; }
        /// <summary>
        /// Model with the humanoid skeleton and the body: for <see cref="AvatarStyle.Classic"/> the outfit (under
        /// <see cref="HumanCatalog.ModelsFolder"/>), for <see cref="AvatarStyle.Modern"/> body, clothes and eyes
        /// (under <see cref="HumanCatalog.ModernModelsFolder"/>).
        /// </summary>
        public string SkeletonModel { get; }
        /// <summary>Classic only: model whose head, eyes and eyebrows are taken.</summary>
        public string HeadModel { get; }
        /// <summary>Material for the skin (classic: the textured face).</summary>
        public string SkinMaterial { get; }
        /// <summary>The hair style used when the look names none (or an unknown one).</summary>
        public string DefaultHairStyle { get; }

        internal BodyPreset(string id, string label, AvatarStyle style, string gender, string skeletonModel, string headModel, string skinMaterial, string defaultHair)
        {
            Id = id;
            Label = label;
            Style = style;
            Gender = gender;
            SkeletonModel = skeletonModel;
            HeadModel = headModel;
            SkinMaterial = skinMaterial;
            DefaultHairStyle = defaultHair;
        }

        /// <summary>Modern only: materials of the top and of the trousers (with the shoes).</summary>
        public string TopMaterial => "Top_" + Id;
        public string BottomMaterial => "Bottom_" + Id;

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

        /// <summary>
        /// The hair models to put on a body of the given preset. A modern preset has one per style (hair, eyebrows and
        /// eyelashes in one mesh, so even "bald" has one), fitted to that body; its material has the same name.
        /// </summary>
        public IReadOnlyList<string> ModelsFor(BodyPreset preset)
        {
            if (preset != null && preset.Style == AvatarStyle.Modern) return new[] { HumanCatalog.ModernHairModel(preset, this) };
            return preset != null && preset.Gender == HumanCatalog.Feminine ? feminine : masculine;
        }

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
        public const string ModernModelsFolder = ResourceRoot + "Modern/Models/";
        public const string ModernMaterialsFolder = ResourceRoot + "Modern/Materials/";

        public const string Masculine = "masculine";
        public const string Feminine = "feminine";
        public const string MasculineHeavy = "masculine_heavy";
        public const string FeminineSlim = "feminine_slim";

        /// <summary>The preset an empty <see cref="AvatarLook.BodyPreset"/> means.</summary>
        public const string DefaultPreset = Masculine;

        public const string SkinPlainMaterial = "AvatarSkinPlain";
        public const string ClothMaterial = "AvatarCloth";
        public const string EyeMaterial = "AvatarEye";

        /// <summary>The player option that picks the style: <c>--avatar-style=classic</c> (or <c>--avatar-style classic</c>).</summary>
        public const string StyleFlag = "--avatar-style";

        /// <summary>The models this client draws people with. Set once at start-up, before avatars are made.</summary>
        public static AvatarStyle Style { get; set; } = AvatarStyle.Modern;

        public static readonly IReadOnlyList<BodyPreset> ModernPresets = new[]
        {
            Modern(Masculine, "Masculine", Masculine, "short"),
            Modern(Feminine, "Feminine", Feminine, "long"),
            Modern(MasculineHeavy, "Masculine, heavier", Masculine, "short"),
            Modern(FeminineSlim, "Feminine, slimmer", Feminine, "long"),
        };

        public static readonly IReadOnlyList<BodyPreset> ClassicPresets = new[]
        {
            new BodyPreset(Masculine, "Masculine", AvatarStyle.Classic, Masculine, "Male_Peasant", "Superhero_Male_FullBody", "AvatarSkinMasculine", "short"),
            new BodyPreset(Feminine, "Feminine", AvatarStyle.Classic, Feminine, "Female_Peasant", "Superhero_Female_FullBody", "AvatarSkinFeminine", "long"),
        };

        private static BodyPreset Modern(string id, string label, string gender, string hair) =>
            new BodyPreset(id, label, AvatarStyle.Modern, gender, "Body_" + id, null, "Skin_" + id, hair);

        /// <summary>The presets of the current <see cref="Style"/> (what the appearance editor offers).</summary>
        public static IReadOnlyList<BodyPreset> Presets => PresetsOf(Style);

        public static IReadOnlyList<BodyPreset> PresetsOf(AvatarStyle style) => style == AvatarStyle.Classic ? ClassicPresets : ModernPresets;

        public static readonly IReadOnlyList<HairStyle> HairStyles = new[]
        {
            new HairStyle("short", "Short", new[] { "Hair_SimpleParted" }, new[] { "Hair_SimpleParted" }),
            new HairStyle("buzzed", "Buzzed", new[] { "Hair_Buzzed" }, new[] { "Hair_BuzzedFemale" }),
            new HairStyle("long", "Long", new[] { "Hair_Long" }, new[] { "Hair_Long" }),
            new HairStyle("buns", "Tied back", new[] { "Hair_Buns" }, new[] { "Hair_Buns" }),
            new HairStyle("beard", "Short + beard", new[] { "Hair_SimpleParted", "Hair_Beard" }, new[] { "Hair_SimpleParted", "Hair_Beard" }),
            new HairStyle("bald", "Bald", new string[0], new string[0]),
        };

        /// <summary>Model (and material) name of a modern preset's hair style.</summary>
        public static string ModernHairModel(BodyPreset preset, HairStyle style) => $"Hair_{preset.Id}_{style.Id}";

        /// <summary>Other spellings accepted for the presets (agents may send these).</summary>
        private static readonly Dictionary<string, string> PresetAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "male", Masculine }, { "man", Masculine }, { "m", Masculine }, { "masc", Masculine },
            { "female", Feminine }, { "woman", Feminine }, { "f", Feminine }, { "fem", Feminine },
            { "heavy", MasculineHeavy }, { "masculine-heavy", MasculineHeavy }, { "male_heavy", MasculineHeavy },
            { "slim", FeminineSlim }, { "feminine-slim", FeminineSlim }, { "female_slim", FeminineSlim },
        };

        /// <summary>The classic figure drawn for a modern-only preset.</summary>
        private static readonly Dictionary<string, string> ClassicStandIns = new Dictionary<string, string>
        {
            { MasculineHeavy, Masculine }, { FeminineSlim, Feminine },
        };

        /// <summary>
        /// The body preset a look asks for in the current <see cref="Style"/>: empty means <see cref="DefaultPreset"/>;
        /// names are matched without case and with a few aliases. Null for a name this client does not know (the
        /// placeholder figure is drawn).
        /// </summary>
        public static BodyPreset FindPreset(string name) => FindPreset(name, Style);

        public static BodyPreset FindPreset(string name, AvatarStyle style)
        {
            string key = string.IsNullOrWhiteSpace(name) ? DefaultPreset : name.Trim();
            if (PresetAliases.TryGetValue(key, out var alias)) key = alias;
            key = key.ToLowerInvariant();
            if (style == AvatarStyle.Classic && ClassicStandIns.TryGetValue(key, out var standIn)) key = standIn;
            foreach (var p in PresetsOf(style))
                if (p.Id == key) return p;
            return null;
        }

        /// <summary>
        /// The style a command line asks for: <c>--avatar-style=classic</c> or <c>--avatar-style classic</c>; anything
        /// else (or nothing) is <see cref="AvatarStyle.Modern"/>.
        /// </summary>
        public static AvatarStyle ParseStyle(IReadOnlyList<string> args)
        {
            if (args == null) return AvatarStyle.Modern;
            for (int i = 0; i < args.Count; i++)
            {
                string a = args[i] ?? "";
                string value = null;
                if (a.StartsWith(StyleFlag + "=", StringComparison.Ordinal)) value = a.Substring(StyleFlag.Length + 1);
                else if (a == StyleFlag && i + 1 < args.Count) value = args[i + 1];
                if (value != null)
                    return string.Equals(value.Trim(), "classic", StringComparison.OrdinalIgnoreCase) ? AvatarStyle.Classic : AvatarStyle.Modern;
            }
            return AvatarStyle.Modern;
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
