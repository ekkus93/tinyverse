using System;
using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Tinyverse.UI
{
    /// <summary>
    /// Everyone present. When the local user is an owner, each other person gets a "more" button
    /// that opens <see cref="PersonActionsMenu"/> (kick, ban, set role).
    /// </summary>
    public sealed class PeopleList : UIPanel
    {
        readonly ScrollView _list;
        readonly Label _title;
        readonly Dictionary<ulong, PersonInfo> _people = new Dictionary<ulong, PersonInfo>();
        readonly PersonActionsMenu _actions;
        Role _localRole = Role.Visitor;

        /// <summary>Kick confirmed by the owner.</summary>
        public event Action<PersonInfo> KickRequested;
        /// <summary>Ban confirmed by the owner.</summary>
        public event Action<PersonInfo> BanRequested;
        /// <summary>The owner picked a new role for someone.</summary>
        public event Action<PersonInfo, Role> RoleChangeRequested;

        public int Count => _people.Count;
        public Role LocalRole => _localRole;
        public bool IsLocalOwner => _localRole == Role.Owner;
        public PersonActionsMenu Actions => _actions;

        internal PeopleList(VisualElement root, ConfirmDialog confirm) : base(root)
        {
            _list = Q<ScrollView>("peopleList");
            _title = Q<Label>("peopleTitle");
            var close = Q<Button>("peopleClose");
            close.Add(new Icon(IconKind.Close));
            close.clicked += Hide;
            _actions = new PersonActionsMenu(Q<VisualElement>("personActions"), confirm);
            _actions.KickConfirmed += p => KickRequested?.Invoke(p);
            _actions.BanConfirmed += p => BanRequested?.Invoke(p);
            _actions.RoleChosen += (p, r) => RoleChangeRequested?.Invoke(p, r);
            Rebuild();
            Hide();
        }

        /// <summary>People in display order: you first, then humans, then agents, each by name.</summary>
        public IReadOnlyList<PersonInfo> People
        {
            get
            {
                var list = new List<PersonInfo>(_people.Values);
                list.Sort((a, b) =>
                {
                    if (a.IsLocal != b.IsLocal) return a.IsLocal ? -1 : 1;
                    if (a.IsAgent != b.IsAgent) return a.IsAgent ? 1 : -1;
                    int c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : a.AvatarId.CompareTo(b.AvatarId);
                });
                return list;
            }
        }

        public bool TryGet(ulong avatarId, out PersonInfo person) => _people.TryGetValue(avatarId, out person);

        public void SetPeople(IEnumerable<PersonInfo> people)
        {
            _people.Clear();
            if (people != null)
                foreach (var p in people) _people[p.AvatarId] = p;
            SyncLocalRole();
            Rebuild();
        }

        /// <summary>Adds a person, or updates them (name, role) if already present.</summary>
        public void AddOrUpdate(PersonInfo person)
        {
            _people[person.AvatarId] = person;
            SyncLocalRole();
            Rebuild();
        }

        public void Remove(ulong avatarId)
        {
            if (!_people.Remove(avatarId)) return;
            if (_actions.IsOpen && _actions.Person.AvatarId == avatarId) _actions.Close();
            Rebuild();
        }

        public void Clear() => SetPeople(null);

        /// <summary>Sets the local user's role, which decides whether owner actions are offered.</summary>
        public void SetLocalRole(Role role)
        {
            _localRole = role;
            if (role != Role.Owner) _actions.Close();
            Rebuild();
        }

        /// <summary>Opens the owner menu for a person. Does nothing unless the local user is an owner.</summary>
        public bool OpenActions(ulong avatarId)
        {
            if (!IsLocalOwner || !_people.TryGetValue(avatarId, out var p) || p.IsLocal) return false;
            _actions.Open(p);
            return true;
        }

        /// <summary>Whether the row for this person shows an actions button.</summary>
        public bool HasActionsButton(ulong avatarId)
        {
            var row = FindRow(avatarId);
            return row != null && row.Q<Button>(className: "tv-person__more") != null;
        }

        protected override void OnHidden() => _actions.Close();

        void SyncLocalRole()
        {
            foreach (var p in _people.Values)
                if (p.IsLocal) { _localRole = p.Role; break; }
            if (_localRole != Role.Owner) _actions.Close();
        }

        VisualElement FindRow(ulong avatarId)
        {
            foreach (var child in _list.contentContainer.Children())
                if (child.userData is ulong id && id == avatarId) return child;
            return null;
        }

        void Rebuild()
        {
            _list.Clear();
            var people = People;
            _title.text = people.Count == 1 ? "1 person here" : $"{people.Count} people here";
            foreach (var p in people)
            {
                var row = new VisualElement { userData = p.AvatarId };
                row.AddToClassList("tv-person");
                if (p.IsLocal) row.AddToClassList("tv-person--you");
                var avatar = new VisualElement();
                avatar.AddToClassList("tv-person__avatar");
                avatar.AddToClassList(p.IsAgent ? "tv-person__avatar--agent" : "tv-person__avatar--human");
                avatar.Add(UIUtil.PlainLabel(Initial(p.Name), "tv-person__initial"));
                row.Add(avatar);
                var name = UIUtil.PlainLabel(p.Name ?? "?", "tv-person__name");
                row.Add(name);
                if (p.IsLocal) row.Add(UIUtil.Label("(you)", "tv-person__you"));
                if (p.IsAgent) row.Add(UIUtil.Label("AI", "tv-badge--ai"));
                var spacer = new VisualElement();
                spacer.AddToClassList("tv-spacer");
                row.Add(spacer);
                var role = UIUtil.Label(p.Role.ToString().ToLowerInvariant(), "tv-role");
                role.AddToClassList("tv-role--" + p.Role.ToString().ToLowerInvariant());
                row.Add(role);
                if (IsLocalOwner && !p.IsLocal)
                {
                    ulong id = p.AvatarId;
                    var more = new Button(() => OpenActions(id)) { tooltip = "Moderate" };
                    more.AddToClassList("tv-icon-button");
                    more.AddToClassList("tv-person__more");
                    more.Add(new Icon(IconKind.More));
                    row.Add(more);
                }
                _list.Add(row);
            }
        }

        static string Initial(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            return char.ToUpperInvariant(name.Trim().Length > 0 ? name.Trim()[0] : '?').ToString();
        }
    }
}
