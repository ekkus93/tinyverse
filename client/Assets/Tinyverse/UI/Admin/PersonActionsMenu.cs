using System;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>The owner's menu for one person: Kick and Ban (each confirmed) and Set role.</summary>
    public sealed class PersonActionsMenu : UIPanel
    {
        readonly Label _name;
        readonly Button _kick;
        readonly Button _ban;
        readonly Button[] _roles = new Button[3];
        readonly ConfirmDialog _confirm;

        internal event Action<PersonInfo> KickConfirmed;
        internal event Action<PersonInfo> BanConfirmed;
        internal event Action<PersonInfo, Role> RoleChosen;

        public PersonInfo Person { get; private set; }
        public bool IsOpen => IsVisible;

        internal PersonActionsMenu(VisualElement root, ConfirmDialog confirm) : base(root)
        {
            _confirm = confirm;
            _name = Q<Label>("personActionsName");
            _name.enableRichText = false;
            _kick = Q<Button>("personKick");
            _ban = Q<Button>("personBan");
            _roles[(int)Role.Visitor] = Q<Button>("personRoleVisitor");
            _roles[(int)Role.Builder] = Q<Button>("personRoleBuilder");
            _roles[(int)Role.Owner] = Q<Button>("personRoleOwner");
            var close = Q<Button>("personActionsClose");
            close.Add(new Icon(IconKind.Close));
            close.clicked += Close;
            _kick.clicked += Kick;
            _ban.clicked += Ban;
            for (int i = 0; i < _roles.Length; i++)
            {
                var role = (Role)i;
                _roles[i].clicked += () => ChooseRole(role);
            }
            Hide();
        }

        internal void Open(PersonInfo person)
        {
            Person = person;
            _name.text = person.Name;
            for (int i = 0; i < _roles.Length; i++)
            {
                bool current = (Role)i == person.Role;
                UIUtil.SetClass(_roles[i], "tv-segment--active", current);
                _roles[i].SetEnabled(!current);
            }
            Show();
        }

        public void Close() => Hide();

        /// <summary>Asks for confirmation, then raises the people list's KickRequested.</summary>
        public void Kick()
        {
            if (!IsOpen) return;
            var p = Person;
            Close();
            _confirm.Ask("Kick " + p.Name + "?", "They are disconnected now but can join again.", "Kick", () => KickConfirmed?.Invoke(p));
        }

        /// <summary>Asks for confirmation, then raises the people list's BanRequested.</summary>
        public void Ban()
        {
            if (!IsOpen) return;
            var p = Person;
            Close();
            _confirm.Ask("Ban " + p.Name + "?", "They are disconnected and cannot join this world again until the host lifts the ban.", "Ban", () => BanConfirmed?.Invoke(p));
        }

        public void ChooseRole(Role role)
        {
            if (!IsOpen || role == Person.Role) return;
            var p = Person;
            Close();
            RoleChosen?.Invoke(p, role);
        }
    }
}
