using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class ChatPanelTests : UITestBase
    {
        ChatPanel Chat => UI.Chat;

        [SetUp]
        public void ShowHud() => UI.ShowScreen(UIScreen.Hud);

        static ChatLine Line(string text, ChatChannel ch = ChatChannel.Local, string who = "Ada", bool ai = false) =>
            new ChatLine(new DateTime(2026, 11, 26, 14, 5, 0), who, ai, ch, text);

        [UnityTest]
        public IEnumerator SubmitRaisesTextAndChannelAndCloses()
        {
            var got = new List<(string, ChatChannel)>();
            Chat.Submitted += (t, c) => got.Add((t, c));
            UI.OpenChat();
            yield return null;
            yield return null;
            Assert.That(Chat.IsInputOpen, Is.True);
            Change(Chat.InputField, "  hello there  ");
            PressKey(Chat.InputField, KeyCode.Return);
            Assert.That(got, Is.EqualTo(new[] { ("hello there", ChatChannel.Local) }));
            Assert.That(Chat.IsInputOpen, Is.False);
            Assert.That(Chat.InputText, Is.EqualTo(""));
        }

        [UnityTest]
        public IEnumerator EmptySubmitJustCloses()
        {
            int count = 0;
            Chat.Submitted += (_, _) => count++;
            UI.OpenChat();
            yield return null;
            yield return null;
            Change(Chat.InputField, "   ");
            PressKey(Chat.InputField, KeyCode.Return);
            Assert.That(count, Is.EqualTo(0));
            Assert.That(Chat.IsInputOpen, Is.False);
        }

        [Test]
        public void EnterThatOpenedTheInputDoesNotCloseIt()
        {
            UI.OpenChat();
            PressKey(Chat.InputField, KeyCode.Return);
            Assert.That(Chat.IsInputOpen, Is.True);
        }

        [Test]
        public void EscapeClosesWithoutSending()
        {
            int count = 0;
            Chat.Submitted += (_, _) => count++;
            UI.OpenChat();
            Change(Chat.InputField, "draft");
            PressKey(Chat.InputField, KeyCode.Escape);
            Assert.That(Chat.IsInputOpen, Is.False);
            Assert.That(count, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TabAndChipToggleChannel()
        {
            var got = new List<ChatChannel>();
            Chat.Submitted += (_, c) => got.Add(c);
            UI.OpenChat();
            Assert.That(Chat.Channel, Is.EqualTo(ChatChannel.Local));
            PressKey(Chat.InputField, KeyCode.Tab, '\t');
            Assert.That(Chat.Channel, Is.EqualTo(ChatChannel.World));
            Assert.That(Chat.ChannelChip.text, Is.EqualTo("World"));
            Assert.That(Chat.InputText, Is.EqualTo(""), "Tab is not typed");
            Click(Chat.ChannelChip);
            Assert.That(Chat.Channel, Is.EqualTo(ChatChannel.Local));
            Click(Chat.ChannelChip);
            yield return null;
            yield return null;
            Change(Chat.InputField, "hi all");
            PressKey(Chat.InputField, KeyCode.Return);
            Assert.That(got, Is.EqualTo(new[] { ChatChannel.World }));
            UI.OpenChat(ChatChannel.Local);
            Assert.That(Chat.Channel, Is.EqualTo(ChatChannel.Local));
        }

        [Test]
        public void LimitAndCounter()
        {
            UI.OpenChat();
            Assert.That(Chat.InputField.maxLength, Is.EqualTo(ChatPanel.MaxMessageLength));
            Change(Chat.InputField, new string('a', 10));
            Assert.That(Chat.CounterText, Is.Null, "no counter far from the limit");
            Change(Chat.InputField, new string('a', ChatPanel.CounterThreshold + 5));
            Assert.That(Chat.CounterText, Is.EqualTo($"{ChatPanel.CounterThreshold + 5}/500"));
            Change(Chat.InputField, new string('a', 600));
            Assert.That(Chat.InputText.Length, Is.LessThanOrEqualTo(ChatPanel.MaxMessageLength));
        }

        [UnityTest]
        public IEnumerator InputHistoryWithUpAndDown()
        {
            foreach (var msg in new[] { "one", "two", "three" })
            {
                UI.OpenChat();
                yield return null;
                yield return null;
                Change(Chat.InputField, msg);
                PressKey(Chat.InputField, KeyCode.Return);
            }
            Assert.That(Chat.InputHistory, Is.EqualTo(new[] { "one", "two", "three" }));
            UI.OpenChat();
            Change(Chat.InputField, "draft");
            PressKey(Chat.InputField, KeyCode.UpArrow);
            Assert.That(Chat.InputText, Is.EqualTo("three"));
            PressKey(Chat.InputField, KeyCode.UpArrow);
            PressKey(Chat.InputField, KeyCode.UpArrow);
            PressKey(Chat.InputField, KeyCode.UpArrow);
            Assert.That(Chat.InputText, Is.EqualTo("one"), "stops at the oldest");
            PressKey(Chat.InputField, KeyCode.DownArrow);
            Assert.That(Chat.InputText, Is.EqualTo("two"));
            PressKey(Chat.InputField, KeyCode.DownArrow);
            PressKey(Chat.InputField, KeyCode.DownArrow);
            Assert.That(Chat.InputText, Is.EqualTo("draft"), "back to the draft");
        }

        [Test]
        public void LinesShowTimeNameBadgeAndChannelStyle()
        {
            Chat.AddLine(Line("hi", ChatChannel.Local, "Ada"));
            Chat.AddLine(Line("hello everyone", ChatChannel.World, "Scout", ai: true));
            Chat.AddSystem("Bo joined");
            Assert.That(Chat.LineElementCount, Is.EqualTo(3));
            var local = Chat.LineElement(0);
            var world = Chat.LineElement(1);
            var system = Chat.LineElement(2);
            Assert.That(local.ClassListContains("tv-chat-line--local"), Is.True);
            Assert.That(world.ClassListContains("tv-chat-line--world"), Is.True);
            Assert.That(system.ClassListContains("tv-chat-line--system"), Is.True);
            Assert.That(local.Q<Label>(className: "tv-chat-line__time").text, Is.EqualTo("14:05"));
            Assert.That(local.Q<Label>(className: "tv-chat-line__name").text, Is.EqualTo("Ada"));
            Assert.That(local.Q<Label>(className: "tv-badge--ai"), Is.Null);
            Assert.That(world.Q<Label>(className: "tv-badge--ai"), Is.Not.Null);
            Assert.That(system.Q<Label>(className: "tv-chat-line__name"), Is.Null);
            Assert.That(system.Q<Label>(className: "tv-chat-line__text").text, Is.EqualTo("Bo joined"));
            Assert.That(local.Q<Label>(className: "tv-chat-line__text").enableRichText, Is.False, "user text is not rich text");
        }

        [Test]
        public void ScrollbackIsBounded()
        {
            for (int i = 0; i < ChatPanel.MaxLines + 30; i++) Chat.AddLine(Line("m" + i));
            Assert.That(Chat.Lines.Count, Is.EqualTo(ChatPanel.MaxLines));
            Assert.That(Chat.LineElementCount, Is.EqualTo(ChatPanel.MaxLines));
            Assert.That(Chat.Lines[0].Text, Is.EqualTo("m30"));
        }

        [Test]
        public void SetHistoryReplacesAndKeepsNewest()
        {
            Chat.AddLine(Line("old"));
            var history = new List<ChatLine>();
            for (int i = 0; i < ChatPanel.MaxLines + 10; i++) history.Add(Line("h" + i, ChatChannel.World));
            Chat.SetHistory(history);
            Assert.That(Chat.Lines.Count, Is.EqualTo(ChatPanel.MaxLines));
            Assert.That(Chat.Lines[0].Text, Is.EqualTo("h10"));
            Assert.That(Chat.IsStuckToBottom, Is.True);
            Chat.SetHistory(new[] { Line("only") });
            Assert.That(Chat.Lines.Count, Is.EqualTo(1));
            Assert.That(Chat.LineElementCount, Is.EqualTo(1));
        }

        [Test]
        public void StickToBottomAndNewMessagesHint()
        {
            Chat.AddLine(Line("a"));
            Assert.That(Chat.IsStuckToBottom, Is.True);
            Chat.SimulateScroll(10f, 200f);
            Assert.That(Chat.IsStuckToBottom, Is.False);
            Chat.AddLine(Line("b"));
            Chat.AddLine(Line("c"));
            Assert.That(Chat.UnseenCount, Is.EqualTo(2));
            Assert.That(Shown(Chat.NewMessagesHint), Is.True);
            Assert.That(Chat.NewMessagesHint.text, Is.EqualTo("2 new messages"));
            Click(Chat.NewMessagesHint);
            Assert.That(Chat.IsStuckToBottom, Is.True);
            Assert.That(Chat.UnseenCount, Is.EqualTo(0));
            Assert.That(Shown(Chat.NewMessagesHint), Is.False);
            Chat.SimulateScroll(0f, 200f);
            Chat.AddLine(Line("d"));
            Chat.SimulateScroll(200f, 200f);
            Assert.That(Chat.IsStuckToBottom, Is.True, "scrolling back down sticks again");
            Assert.That(Shown(Chat.NewMessagesHint), Is.False);
        }

        [Test]
        public void FadesWhenIdle()
        {
            Chat.AddLine(Line("a"));
            Assert.That(Chat.IsIdle, Is.False);
            Chat.Tick(ChatPanel.IdleSeconds + 1f);
            Assert.That(Chat.IsIdle, Is.True);
            Chat.AddLine(Line("b"));
            Assert.That(Chat.IsIdle, Is.False, "a new line wakes it");
            UI.OpenChat();
            Chat.Tick(ChatPanel.IdleSeconds + 1f);
            Assert.That(Chat.IsIdle, Is.False, "never faded while typing");
        }
    }

    public class PeopleListTests : UITestBase
    {
        PeopleList People => UI.People;

        static PersonInfo Me(Role role) => new PersonInfo(1, "u1", "Ada", false, role, true);
        static readonly PersonInfo Bo = new PersonInfo(2, "u2", "Bo", false, Role.Builder, false);
        static readonly PersonInfo Scout = new PersonInfo(3, "u3", "Scout", true, Role.Visitor, false);

        [SetUp]
        public void ShowHud()
        {
            UI.ShowScreen(UIScreen.Hud);
            People.Show();
        }

        [Test]
        public void ListsEveryoneWithYouFirstAndBadges()
        {
            People.SetPeople(new[] { Scout, Bo, Me(Role.Builder) });
            Assert.That(People.Count, Is.EqualTo(3));
            var order = People.People;
            Assert.That(order[0].Name, Is.EqualTo("Ada"));
            Assert.That(order[1].Name, Is.EqualTo("Bo"));
            Assert.That(order[2].Name, Is.EqualTo("Scout"));
            var rows = People.Root.Query(className: "tv-person").ToList();
            Assert.That(rows.Count, Is.EqualTo(3));
            Assert.That(rows[0].Q<Label>(className: "tv-person__you"), Is.Not.Null, "(you)");
            Assert.That(rows[1].Q<Label>(className: "tv-person__you"), Is.Null);
            Assert.That(rows[2].Q<Label>(className: "tv-badge--ai"), Is.Not.Null);
            Assert.That(rows[1].Q<Label>(className: "tv-role").text, Is.EqualTo("builder"));
            Assert.That(People.Root.Q<Label>("peopleTitle").text, Is.EqualTo("3 people here"));
        }

        [Test]
        public void AddUpdateRemove()
        {
            People.AddOrUpdate(Me(Role.Builder));
            People.AddOrUpdate(Bo);
            var renamed = Bo;
            renamed.Name = "Bobby";
            People.AddOrUpdate(renamed);
            Assert.That(People.Count, Is.EqualTo(2));
            Assert.That(People.TryGet(2, out var p) && p.Name == "Bobby", Is.True);
            People.Remove(2);
            Assert.That(People.Count, Is.EqualTo(1));
            People.Remove(99);
            Assert.That(People.Count, Is.EqualTo(1));
        }

        [Test]
        public void OwnerMenuOnlyForOwnersAndNotForSelf()
        {
            People.SetPeople(new[] { Me(Role.Builder), Bo, Scout });
            Assert.That(People.IsLocalOwner, Is.False);
            Assert.That(People.HasActionsButton(2), Is.False);
            Assert.That(People.OpenActions(2), Is.False);

            People.AddOrUpdate(Me(Role.Owner));
            Assert.That(People.IsLocalOwner, Is.True);
            Assert.That(People.HasActionsButton(1), Is.False, "no menu for yourself");
            Assert.That(People.HasActionsButton(2), Is.True);
            Assert.That(People.HasActionsButton(3), Is.True);

            People.SetLocalRole(Role.Visitor);
            Assert.That(People.HasActionsButton(2), Is.False);
        }

        [Test]
        public void KickAndBanNeedConfirmation()
        {
            People.SetPeople(new[] { Me(Role.Owner), Bo, Scout });
            var kicked = new List<PersonInfo>();
            var banned = new List<PersonInfo>();
            People.KickRequested += kicked.Add;
            People.BanRequested += banned.Add;

            var more = People.Root.Query<Button>(className: "tv-person__more").ToList();
            Assert.That(more.Count, Is.EqualTo(2));
            Click(more[0]);
            Assert.That(People.Actions.IsOpen, Is.True);
            Assert.That(People.Actions.Person.Name, Is.EqualTo("Bo"));
            Click(People.Actions.Root.Q<Button>("personKick"));
            Assert.That(UI.Confirm.IsVisible, Is.True);
            Assert.That(UI.Confirm.Title, Does.Contain("Bo"));
            Assert.That(kicked, Is.Empty, "not before confirming");
            Click(UI.Confirm.Root.Q<Button>("confirmOk"));
            Assert.That(kicked.Count, Is.EqualTo(1));
            Assert.That(kicked[0].UserId, Is.EqualTo("u2"));

            People.OpenActions(3);
            People.Actions.Ban();
            Click(UI.Confirm.Root.Q<Button>("confirmCancel"));
            Assert.That(banned, Is.Empty, "cancelled");
            People.OpenActions(3);
            People.Actions.Ban();
            UI.Confirm.Confirm();
            Assert.That(banned.Count, Is.EqualTo(1));
            Assert.That(banned[0].Name, Is.EqualTo("Scout"));
        }

        [Test]
        public void SetRoleRaisesEvent()
        {
            People.SetPeople(new[] { Me(Role.Owner), Bo });
            var got = new List<(PersonInfo, Role)>();
            People.RoleChangeRequested += (p, r) => got.Add((p, r));
            People.OpenActions(2);
            var builder = People.Actions.Root.Q<Button>("personRoleBuilder");
            Assert.That(builder.enabledSelf, Is.False, "current role is not offered");
            Click(builder);
            Assert.That(got, Is.Empty);
            Click(People.Actions.Root.Q<Button>("personRoleOwner"));
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Item1.UserId, Is.EqualTo("u2"));
            Assert.That(got[0].Item2, Is.EqualTo(Role.Owner));
            Assert.That(People.Actions.IsOpen, Is.False);
            People.OpenActions(2);
            Click(People.Actions.Root.Q<Button>("personRoleVisitor"));
            Assert.That(got[1].Item2, Is.EqualTo(Role.Visitor));
        }
    }

    public class StatusBarTests : UITestBase
    {
        [Test]
        public void SettersShowValues()
        {
            UI.ShowScreen(UIScreen.Hud);
            var s = UI.StatusBar;
            s.SetWorldName("Island");
            Assert.That(s.WorldName, Is.EqualTo("Island"));

            s.SetConnectionState(ConnectionState.Connected);
            Assert.That(s.StateText, Is.EqualTo("Connected"));
            s.SetConnectionState(ConnectionState.Reconnecting);
            Assert.That(s.StateText, Is.EqualTo("Reconnecting…"));
            Assert.That(s.Root.Q("statusDot").ClassListContains("tv-dot--reconnecting"), Is.True);
            Assert.That(s.Root.Q("statusDot").ClassListContains("tv-dot--connected"), Is.False);
            s.SetConnectionState(ConnectionState.Disconnected);
            Assert.That(s.StateText, Is.EqualTo("Disconnected"));

            s.SetPing(42);
            Assert.That(s.PingText, Is.EqualTo("42 ms"));
            Assert.That(s.Root.Q("statusPing").ClassListContains("tv-ping--good"), Is.True);
            s.SetPing(350);
            Assert.That(s.Root.Q("statusPing").ClassListContains("tv-ping--poor"), Is.True);
            s.SetPing(null);
            Assert.That(s.PingMs, Is.Null);

            s.SetPeopleCount(5);
            Assert.That(s.PeopleText, Is.EqualTo("5"));
            s.SetPosition(new Vector3(1.234f, -0.26f, 100f));
            Assert.That(s.PositionText, Is.EqualTo("1.2, -0.3, 100.0"));
            s.SetMoveMode(MoveMode.Fly);
            Assert.That(s.ModeText, Is.EqualTo("Fly"));
            s.SetMoveMode(MoveMode.Run);
            Assert.That(s.ModeText, Is.EqualTo("Run"));
            Assert.That(s.MoveMode, Is.EqualTo(MoveMode.Run));
        }

        [Test]
        public void HintLineDismisses()
        {
            UI.ShowScreen(UIScreen.Hud);
            int count = 0;
            UI.Hint.Dismissed += () => count++;
            Assert.That(UI.Hint.Text, Does.Contain("WASD"));
            Click(UI.Hint.Root.Q<Button>("hintClose"));
            Assert.That(UI.Hint.IsVisible, Is.False);
            Assert.That(count, Is.EqualTo(1));
            UI.Hint.Show();
            Assert.That(UI.Hint.IsVisible, Is.True);
        }
    }
}
