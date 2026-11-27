using System;
using System.Linq;
using Tinyverse.Avatars;
using Tinyverse.UI;
using UnityEngine;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// Glue for the appearance editor: opens it from the menu with the own avatar's look, previews every change
    /// on the own avatar, and on Done stores the choice in the settings (sent in Hello on the next join) and sends
    /// SetAppearance; Cancel restores the previous look. While it is open the avatar stands still and the camera
    /// turns to face it. <c>--open-appearance</c> opens it once after joining (for screenshots).
    /// </summary>
    public sealed class AppearanceController
    {
        public const string OpenFlag = "--open-appearance";

        readonly SettingsStore store;
        readonly Func<WorldSession> session;
        bool openOnJoin;
        AvatarLook lookBefore;
        float? distanceBefore;

        public AppearancePanel Panel { get; }

        public AppearanceController(UIRoot ui, SettingsStore store, Func<WorldSession> session)
        {
            this.store = store;
            this.session = session;
            openOnJoin = Environment.GetCommandLineArgs().Contains(OpenFlag);
            Panel = AppearancePanel.Attach(ui);
            Panel.SetOptions(
                HumanCatalog.Presets.Select(p => new AppearanceOption(p.Id, p.Label)).ToList(),
                HumanCatalog.HairStyles.Select(h => new AppearanceOption(h.Id, h.Label)).ToList());
            Panel.OpenRequested += Open;
            Panel.Changed += c => Avatar?.SetLook(ToLook(c, lookBefore));
            Panel.Cancelled += _ => Avatar?.SetLook(lookBefore);
            Panel.Saved += Save;
            Panel.VisibilityChanged += shown => { if (!shown) RestoreCamera(); };
        }

        LocalAvatar Avatar => session()?.LocalAvatar;

        /// <summary>Opens the editor with the own avatar's current look. Does nothing outside a world.</summary>
        public void Open()
        {
            var s = session();
            var avatar = s?.LocalAvatar;
            if (avatar == null) return;
            lookBefore = avatar.Look;
            Panel.Open(ToChoice(lookBefore));
            if (s.Orbit != null)
            {
                // Face the avatar from the front, a little to its right so the panel does not cover it.
                s.Orbit.Yaw = avatar.transform.eulerAngles.y + 160f;
                s.Orbit.Pitch = 8f;
                if (!distanceBefore.HasValue) distanceBefore = s.Orbit.Distance;
                s.Orbit.Distance = 3.2f;
            }
        }

        void RestoreCamera()
        {
            var orbit = session()?.Orbit;
            if (orbit != null && distanceBefore.HasValue) orbit.Distance = distanceBefore.Value;
            distanceBefore = null;
        }

        /// <summary>After the session's own tick: keeps the avatar still while editing; opens on join if asked.</summary>
        public void Tick()
        {
            var s = session();
            if (Panel.IsVisible && s?.LocalAvatar != null) s.LocalAvatar.InputEnabled = false;
            if (openOnJoin && s != null && s.SnapshotComplete && s.LocalAvatar != null)
            {
                openOnJoin = false;
                Open();
            }
        }

        void Save(AppearanceChoice c)
        {
            store.Current.appearance = ToStored(c);
            store.Save();
            Avatar?.SetLook(ToLook(c, lookBefore));
            var conn = session()?.Connection;
            if (conn != null)
                conn.SetAppearance(ProtoConvert.ToAppearance(store.Current.appearance, SettingsStore.ParseBodyColor(store.Current.bodyColor)));
            Debug.Log($"[Appearance] saved {c}");
        }

        /// <summary>What the editor shows for a look: the preset and hair style it resolves to, its drawn colours.</summary>
        public static AppearanceChoice ToChoice(AvatarLook look)
        {
            var preset = HumanCatalog.FindPreset(look.BodyPreset) ?? HumanCatalog.FindPreset(HumanCatalog.DefaultPreset);
            var colors = HumanCatalog.ColorsFor(look);
            return new AppearanceChoice
            {
                BodyPreset = preset.Id,
                HairStyle = HumanCatalog.FindHairStyle(look.HairStyle, preset).Id,
                Skin = colors.Skin,
                Hair = colors.Hair,
                Top = colors.Top,
                Bottom = colors.Bottom,
            };
        }

        /// <summary>The look for a choice; the body colour (drawn by clients that only know the placeholder) follows the top.</summary>
        public static AvatarLook ToLook(AppearanceChoice c, AvatarLook basis)
        {
            var look = basis;
            look.BodyPreset = c.BodyPreset ?? "";
            look.HairStyle = c.HairStyle ?? "";
            look.SkinColor = c.Skin;
            look.HairColor = c.Hair;
            look.TopColor = c.Top;
            look.BottomColor = c.Bottom;
            look.BodyColor = c.Top;
            return look;
        }

        public static StoredAppearance ToStored(AppearanceChoice c) => new StoredAppearance
        {
            bodyPreset = c.BodyPreset ?? "",
            hairStyle = c.HairStyle ?? "",
            skin = Hex(c.Skin),
            hair = Hex(c.Hair),
            top = Hex(c.Top),
            bottom = Hex(c.Bottom),
        };

        static string Hex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c).ToLowerInvariant();
    }
}
