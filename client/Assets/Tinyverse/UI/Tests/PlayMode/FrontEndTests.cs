using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    public class ConnectScreenTests : UITestBase
    {
        ConnectScreen C => UI.Connect;

        [Test]
        public void ConnectRaisesRequestWithTrimmedValues()
        {
            var got = new List<ConnectRequest>();
            C.ConnectRequested += got.Add;
            Change(C.AddressField, " 192.168.1.20:7878 ");
            Change(C.NameField, " Ada ");
            Change(C.PasswordField, "sesame");
            Click(C.ConnectButton);
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Address, Is.EqualTo("192.168.1.20:7878"));
            Assert.That(got[0].DisplayName, Is.EqualTo("Ada"));
            Assert.That(got[0].Password, Is.EqualTo("sesame"));
            Assert.That(C.ErrorText, Is.Null);
        }

        [Test]
        public void PasswordIsMaskedAndOptional()
        {
            Assert.That(C.PasswordField.isPasswordField, Is.True);
            var got = new List<ConnectRequest>();
            C.ConnectRequested += got.Add;
            C.SetAddress("localhost");
            C.SetDisplayName("Ada");
            C.Submit();
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Password, Is.EqualTo(""));
        }

        [Test]
        public void InvalidFormShowsErrorInlineAndRaisesNothing()
        {
            int count = 0;
            C.ConnectRequested += _ => count++;
            C.SetAddress("");
            C.SetDisplayName("Ada");
            C.Submit();
            Assert.That(C.ErrorText, Does.Contain("address"));
            Assert.That(C.AddressField.ClassListContains("tv-field--invalid"), Is.True);

            C.SetAddress("host:99999");
            C.Submit();
            Assert.That(C.ErrorText, Does.Contain("port"));

            C.SetAddress("host");
            C.SetDisplayName("   ");
            C.Submit();
            Assert.That(C.ErrorText, Does.Contain("display name"));
            Assert.That(C.NameField.ClassListContains("tv-field--invalid"), Is.True);
            Assert.That(C.AddressField.ClassListContains("tv-field--invalid"), Is.False);
            Assert.That(count, Is.EqualTo(0));

            C.SetDisplayName("Ada");
            C.Submit();
            Assert.That(count, Is.EqualTo(1));
            Assert.That(C.ErrorText, Is.Null, "a good submit clears the error");
        }

        [Test]
        public void EnterSubmits()
        {
            int count = 0;
            C.ConnectRequested += _ => count++;
            C.SetAddress("localhost:7878");
            C.SetDisplayName("Ada");
            PressKey(C.PasswordField, KeyCode.Return);
            Assert.That(count, Is.EqualTo(1));
            PressKey(C.NameField, KeyCode.KeypadEnter);
            Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void ShowErrorAndClear()
        {
            C.ShowError("World is full");
            Assert.That(C.ErrorText, Is.EqualTo("World is full"));
            C.ClearError();
            Assert.That(C.ErrorText, Is.Null);
        }

        [Test]
        public void BusyStateDisablesFormAndOffersCancel()
        {
            int connects = 0, cancels = 0;
            C.ConnectRequested += _ => connects++;
            C.CancelRequested += () => cancels++;
            C.SetAddress("localhost");
            C.SetDisplayName("Ada");
            C.SetBusy(true, "Connecting to localhost…");
            Assert.That(C.IsBusy, Is.True);
            Assert.That(Shown(C.CancelButton), Is.True);
            Assert.That(C.ConnectButton.enabledInHierarchy, Is.False);
            C.Submit();
            PressKey(C.NameField, KeyCode.Return);
            Assert.That(connects, Is.EqualTo(0), "no connect while busy");
            Click(C.CancelButton);
            Assert.That(cancels, Is.EqualTo(1));
            C.SetBusy(false);
            Assert.That(Shown(C.CancelButton), Is.False);
            Assert.That(C.ConnectButton.enabledInHierarchy, Is.True);
            Click(C.CancelButton);
            Assert.That(cancels, Is.EqualTo(1), "cancel does nothing when idle");
        }

        [Test]
        public void HostButtonRaisesHostRequested()
        {
            int count = 0;
            C.HostRequested += () => count++;
            Click(C.HostButton);
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void RecentWorldsFillConnectAndRemove()
        {
            var worlds = new[]
            {
                new RecentWorld("Island", "10.0.0.5:7878", DateTime.Now.AddHours(-2)),
                new RecentWorld("Town", "town.local", DateTime.Now.AddDays(-3)),
            };
            C.SetRecentWorlds(worlds);
            C.SetDisplayName("Ada");
            Assert.That(C.RecentWorlds.Count, Is.EqualTo(2));

            PointerDown(C.RecentRow(1), 1);
            Assert.That(C.Address, Is.EqualTo("town.local"), "click fills the address");
            Assert.That(C.SelectedRecentIndex, Is.EqualTo(1));

            var got = new List<ConnectRequest>();
            C.ConnectRequested += got.Add;
            PointerDown(C.RecentRow(0), 2);
            Assert.That(got.Count, Is.EqualTo(1), "double-click connects");
            Assert.That(got[0].Address, Is.EqualTo("10.0.0.5:7878"));

            PressKey(C.RecentRow(1), KeyCode.Return);
            Assert.That(got.Count, Is.EqualTo(2), "Enter on a focused row connects");
            Assert.That(got[1].Address, Is.EqualTo("town.local"));

            var removed = new List<RecentWorld>();
            C.RecentWorldRemoved += removed.Add;
            var removeButton = C.RecentRow(0).Q<UnityEngine.UIElements.Button>(className: "tv-recent__remove");
            Click(removeButton);
            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(removed[0].Name, Is.EqualTo("Island"));
            Assert.That(C.RecentWorlds.Count, Is.EqualTo(1));
            Assert.That(C.RecentWorlds[0].Name, Is.EqualTo("Town"));
        }

        [Test]
        public void SettingsButtonOpensSettings()
        {
            Click(UI.Connect.Root.Q<UnityEngine.UIElements.Button>("connectSettings"));
            Assert.That(UI.Settings.IsVisible, Is.True);
            UI.Settings.Close();
            Assert.That(UI.Menu.IsVisible, Is.False, "the in-world menu does not appear on the connect screen");
        }
    }

    public class HostScreenTests : UITestBase
    {
        HostScreen H => UI.Host;

        [SetUp]
        public void ShowHost() => UI.ShowScreen(UIScreen.Host);

        static HostSettings Defaults => new HostSettings
        {
            WorldName = "Home", Description = "A quiet place", WorldFolder = "/home/ada/.local/share/tinyverse/home",
            Port = 0, Password = "", DisplayName = "Ada",
        };

        [Test]
        public void DefaultsFillTheFormWithPort7878()
        {
            H.SetDefaults(Defaults);
            Assert.That(H.PortField.value, Is.EqualTo("7878"));
            var s = H.Settings;
            Assert.That(s.WorldName, Is.EqualTo("Home"));
            Assert.That(s.WorldFolder, Is.EqualTo("/home/ada/.local/share/tinyverse/home"));
            Assert.That(s.Port, Is.EqualTo(7878));
            Assert.That(s.DisplayName, Is.EqualTo("Ada"));
        }

        [Test]
        public void StartRaisesSettings()
        {
            H.SetDefaults(Defaults);
            Change(H.PortField, "9000");
            var got = new List<HostSettings>();
            H.StartRequested += got.Add;
            Click(H.StartButton);
            Assert.That(got.Count, Is.EqualTo(1));
            Assert.That(got[0].Port, Is.EqualTo(9000));
            Assert.That(got[0].Description, Is.EqualTo("A quiet place"));
        }

        [Test]
        public void ValidatesPortNameAndFolder()
        {
            int count = 0;
            H.StartRequested += _ => count++;
            H.SetDefaults(Defaults);

            Change(H.PortField, "70000");
            H.Submit();
            Assert.That(H.ErrorText, Does.Contain("port"));
            Assert.That(H.PortField.ClassListContains("tv-field--invalid"), Is.True);

            Change(H.PortField, "abc");
            H.Submit();
            Assert.That(H.ErrorText, Does.Contain("port"));

            Change(H.PortField, "7878");
            Change(H.WorldNameField, "  ");
            H.Submit();
            Assert.That(H.ErrorText, Does.Contain("name"));

            Change(H.WorldNameField, "Home");
            Change(H.FolderField, "");
            H.Submit();
            Assert.That(H.ErrorText, Does.Contain("folder"));
            Assert.That(count, Is.EqualTo(0));

            Change(H.FolderField, "/tmp/w");
            PressKey(H.FolderField, KeyCode.Return);
            Assert.That(count, Is.EqualTo(1), "Enter submits");
            Assert.That(H.ErrorText, Is.Null);
        }

        [Test]
        public void StateControlsButtonsAndForm()
        {
            int stops = 0, backs = 0, starts = 0;
            H.StopRequested += () => stops++;
            H.BackRequested += () => backs++;
            H.StartRequested += _ => starts++;
            H.SetDefaults(Defaults);

            Assert.That(H.StopButton.enabledSelf, Is.False);
            H.SetState(HostState.Running, null);
            Assert.That(H.StatusText, Is.EqualTo("Running"));
            Assert.That(H.StartButton.enabledSelf, Is.False);
            Assert.That(H.StopButton.enabledSelf, Is.True);
            Assert.That(H.PortField.enabledInHierarchy, Is.False, "form locked while running");
            H.Submit();
            Assert.That(starts, Is.EqualTo(0));
            H.RequestBack();
            Assert.That(backs, Is.EqualTo(0), "no back while running");
            Assert.That(Shown(H.BackButton), Is.False, "Back is hidden while running");
            Click(H.StopButton);
            Assert.That(stops, Is.EqualTo(1));

            H.SetState(HostState.Failed, "Port 7878 is in use");
            Assert.That(H.StatusText, Is.EqualTo("Port 7878 is in use"));
            Assert.That(H.StartButton.enabledSelf, Is.True);
            Click(H.BackButton);
            Assert.That(backs, Is.EqualTo(1));
        }

        [Test]
        public void JoinIsTheWayOutWhileRunning()
        {
            int joins = 0;
            H.JoinRequested += () => joins++;
            Assert.That(Shown(H.JoinButton), Is.False, "no Join while stopped");
            Assert.That(Shown(H.StartButton), Is.True);
            H.RequestJoin();
            Assert.That(joins, Is.EqualTo(0));

            H.SetState(HostState.Starting, null);
            Assert.That(Shown(H.JoinButton), Is.False, "no Join while starting");

            H.SetState(HostState.Running, "Running at ws://127.0.0.1:7878/ws");
            Assert.That(Shown(H.JoinButton), Is.True);
            Assert.That(Shown(H.StartButton), Is.False);
            Click(H.JoinButton);
            Assert.That(joins, Is.EqualTo(1));
            Assert.That(UI.HandleEscape(), Is.EqualTo("host-join"), "Escape also goes back into the world");
            Assert.That(joins, Is.EqualTo(2));

            H.SetState(HostState.Stopped, null);
            Assert.That(Shown(H.JoinButton), Is.False);
            Assert.That(Shown(H.BackButton), Is.True);
            Assert.That(UI.HandleEscape(), Is.EqualTo("host-back"));
        }

        [Test]
        public void LogIsBounded()
        {
            for (int i = 0; i < HostScreen.MaxLogLines + 50; i++) H.AppendLog("line " + i);
            Assert.That(H.LogLines.Count, Is.EqualTo(HostScreen.MaxLogLines));
            Assert.That(H.LogElementCount, Is.EqualTo(HostScreen.MaxLogLines));
            Assert.That(H.LogLines[0], Is.EqualTo("line 50"));
            Assert.That(H.LogLines[H.LogLines.Count - 1], Is.EqualTo("line " + (HostScreen.MaxLogLines + 49)));
            H.ClearLog();
            Assert.That(H.LogLines.Count, Is.EqualTo(0));
            Assert.That(H.LogElementCount, Is.EqualTo(0));
        }

        [Test]
        public void LogLinesAreNotRichText()
        {
            H.AppendLog("<b>not bold</b> 2026-11-26 WARN something");
            Assert.That(H.LogLines[0], Is.EqualTo("<b>not bold</b> 2026-11-26 WARN something"));
        }

        [Test]
        public void ShareAddressAndCopy()
        {
            var copied = new List<string>();
            H.AddressCopied += copied.Add;
            H.SetShareAddress(null);
            Click(H.CopyButton);
            Assert.That(copied, Is.Empty);
            H.SetShareAddress("192.168.1.20:7878");
            Assert.That(H.ShareAddress, Is.EqualTo("192.168.1.20:7878"));
            Click(H.CopyButton);
            Assert.That(copied, Is.EqualTo(new[] { "192.168.1.20:7878" }));
        }
    }
}
