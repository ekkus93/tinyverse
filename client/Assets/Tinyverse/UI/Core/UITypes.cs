using System;
using UnityEngine;

// Plain data types the UI exposes. They deliberately mirror, but do not reference, the
// protocol types: the glue in Bootstrap translates between the two.
namespace Tinyverse.UI
{
    /// <summary>Which top-level view <see cref="UIRoot"/> shows.</summary>
    public enum UIScreen { None, Connect, Host, Hud }

    public enum ChatChannel { Local, World, System }

    public enum ConnectionState { Disconnected, Connecting, Connected, Reconnecting }

    public enum MoveMode { Walk, Run, Fly }

    public enum Role { Visitor, Builder, Owner }

    public enum ShapeKind { Box, Sphere, Cylinder, Cone, Wedge, Plane }

    public enum BuildTool { Place, Select, Move, Rotate, Scale }

    public enum MaterialKind { Matte, Glossy, Metal, Glass, Emissive }

    public enum ToastKind { Info, Warning, Error }

    public enum HostState { Stopped, Starting, Running, Stopping, Failed }

    /// <summary>A world the user visited before, shown on the connect screen.</summary>
    [Serializable]
    public struct RecentWorld
    {
        public string Name;
        public string Address;
        public DateTime LastVisited;

        public RecentWorld(string name, string address, DateTime lastVisited)
        {
            Name = name;
            Address = address;
            LastVisited = lastVisited;
        }
    }

    /// <summary>What the user typed on the connect screen. Password is empty when not given.</summary>
    public struct ConnectRequest
    {
        public string Address;
        public string DisplayName;
        public string Password;

        public ConnectRequest(string address, string displayName, string password)
        {
            Address = address;
            DisplayName = displayName;
            Password = password;
        }
    }

    /// <summary>The host form. Password is empty when the world is open.</summary>
    [Serializable]
    public struct HostSettings
    {
        public const int DefaultPort = 7878;

        public string WorldName;
        public string Description;
        public string WorldFolder;
        public int Port;
        public string Password;
        public string DisplayName;

        /// <summary>
        /// Serve plain <c>ws://</c>. By default (false) the world is served over <c>wss://</c> with a
        /// self-signed certificate that guests trust by its fingerprint.
        /// </summary>
        public bool Unencrypted;

        /// <summary>
        /// Name of the template a new world starts from (see <see cref="WorldTemplateChoice"/>); empty for the
        /// server's default. The Host screen leaves it empty when the folder already holds a world.
        /// </summary>
        public string Template;
    }

    /// <summary>A template the Host screen offers for a new world. The glue fills the list from the server.</summary>
    public struct WorldTemplateChoice
    {
        /// <summary>What the server calls it, such as "island".</summary>
        public string Name;
        /// <summary>What the dropdown shows, such as "Island".</summary>
        public string Title;
        public string Description;

        public WorldTemplateChoice(string name, string title, string description)
        {
            Name = name;
            Title = title;
            Description = description;
        }
    }

    /// <summary>One chat line. For <see cref="ChatChannel.System"/> the sender is ignored.</summary>
    public struct ChatLine
    {
        public DateTime Time;
        public string SenderName;
        public bool SenderIsAgent;
        public ChatChannel Channel;
        public string Text;

        public ChatLine(DateTime time, string senderName, bool senderIsAgent, ChatChannel channel, string text)
        {
            Time = time;
            SenderName = senderName;
            SenderIsAgent = senderIsAgent;
            Channel = channel;
            Text = text;
        }

        public static ChatLine System(string text) => new ChatLine(DateTime.Now, null, false, ChatChannel.System, text);
    }

    /// <summary>One person present in the world. Keyed by <see cref="AvatarId"/>.</summary>
    public struct PersonInfo
    {
        public ulong AvatarId;
        public string UserId;
        public string Name;
        public bool IsAgent;
        public Role Role;
        public bool IsLocal;

        public PersonInfo(ulong avatarId, string userId, string name, bool isAgent, Role role, bool isLocal)
        {
            AvatarId = avatarId;
            UserId = userId;
            Name = name;
            IsAgent = isAgent;
            Role = role;
            IsLocal = isLocal;
        }
    }

    /// <summary>The selected object as the build inspector shows it.</summary>
    public struct SelectedObjectInfo
    {
        public ulong Id;
        public string Name;
        public ShapeKind Shape;
        public Vector3 Position;
        /// <summary>Euler angles in degrees.</summary>
        public Vector3 Rotation;
        public Vector3 Scale;
        public Color Color;
        public MaterialKind Material;
        public bool Solid;
        public bool Locked;
        public string OwnerName;
    }

    /// <summary>Client settings. Persistence is the glue's job.</summary>
    [Serializable]
    public struct ClientSettings
    {
        public float MouseSensitivity;
        public bool InvertY;
        public float FieldOfView;
        /// <summary>Index into the names given to <see cref="SettingsPanel.SetQualityLevels"/>.</summary>
        public int QualityIndex;
        public bool Fullscreen;
        /// <summary>0 to 1.</summary>
        public float MasterVolume;
        /// <summary>Multiplier on the reference-resolution scale, 0.75 to 1.5.</summary>
        public float UIScale;

        public static ClientSettings Default => new ClientSettings
        {
            MouseSensitivity = 1f,
            InvertY = false,
            FieldOfView = 70f,
            QualityIndex = 0,
            Fullscreen = false,
            MasterVolume = 0.8f,
            UIScale = 1f,
        };
    }
}
