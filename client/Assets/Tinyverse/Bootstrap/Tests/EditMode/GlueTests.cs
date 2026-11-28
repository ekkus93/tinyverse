using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tinyverse.Avatars;
using Tinyverse.World;
using UnityEngine;
using P = Tinyverse.Proto;
using UI = Tinyverse.UI;

namespace Tinyverse.Bootstrap.Tests
{
    public sealed class ProtoConvertTests
    {
        [Test]
        public void Vectors_RoundTrip()
        {
            var v = new Vector3(1.5f, -2.25f, 1000.125f);
            Assert.AreEqual(v, ProtoConvert.ToVector3(ProtoConvert.ToProto(v)));
            Assert.AreEqual(Vector3.zero, ProtoConvert.ToVector3(null));
        }

        [Test]
        public void Quaternions_RoundTrip_AndZeroIsIdentity()
        {
            var q = Quaternion.Euler(10f, 200f, -35f);
            var back = ProtoConvert.ToQuaternion(ProtoConvert.ToProto(q));
            Assert.AreEqual(q.x, back.x, 1e-6f);
            Assert.AreEqual(q.y, back.y, 1e-6f);
            Assert.AreEqual(q.z, back.z, 1e-6f);
            Assert.AreEqual(q.w, back.w, 1e-6f);
            Assert.AreEqual(Quaternion.identity, ProtoConvert.ToQuaternion(new P.Quat()));
            Assert.AreEqual(Quaternion.identity, ProtoConvert.ToQuaternion(null));
        }

        [Test]
        public void Colours_AreUnchanged_NoColourSpaceConversion()
        {
            var c = new Color(1f, 0.5019608f, 0f, 0.25f); // #ff8000 at 25 %
            var p = ProtoConvert.ToProto(c);
            Assert.AreEqual(1f, p.R);
            Assert.AreEqual(0.5019608f, p.G);
            Assert.AreEqual(0f, p.B);
            Assert.AreEqual(0.25f, p.A);
            Assert.AreEqual(c, ProtoConvert.ToColor(p));
        }

        [TestCase(0f)]
        [TestCase(90f)]
        [TestCase(181.5f)]
        [TestCase(359.9f)]
        public void Yaw_PassesThroughUnchanged(float yaw)
        {
            var pose = new AvatarPose(new Vector3(1, 2, 3), yaw, new Vector3(0, 0, 4), MoveKind.Run, "run");
            var t = ProtoConvert.ToProto(pose);
            Assert.AreEqual(yaw, t.Yaw);
            var back = ProtoConvert.ToPose(t);
            Assert.AreEqual(yaw, back.Yaw);
            Assert.AreEqual(pose.Position, back.Position);
            Assert.AreEqual(pose.Velocity, back.Velocity);
            Assert.AreEqual(MoveKind.Run, back.Mode);
            Assert.AreEqual("run", back.Animation);
            Assert.AreEqual(P.MoveMode.Run, t.MoveMode);
        }

        [Test]
        public void Shapes_EveryValue()
        {
            Assert.AreEqual(PrimShape.Box, ProtoConvert.ToPrimShape(P.Shape.Unspecified));
            foreach (PrimShape s in Enum.GetValues(typeof(PrimShape)))
            {
                Assert.AreEqual(s, ProtoConvert.ToPrimShape(ProtoConvert.ToProto(s)));
                Assert.AreEqual(s, ProtoConvert.ToPrimShape(ProtoConvert.ToUI(s)));
                Assert.AreEqual(s.ToString(), ProtoConvert.ToUI(s).ToString());
            }
            foreach (P.Shape s in Enum.GetValues(typeof(P.Shape)))
                if (s != P.Shape.Unspecified) Assert.AreEqual(s, ProtoConvert.ToProto(ProtoConvert.ToPrimShape(s)));
        }

        [Test]
        public void Materials_EveryValue()
        {
            Assert.AreEqual(PrimMaterial.Matte, ProtoConvert.ToPrimMaterial(P.Material.Unspecified));
            foreach (PrimMaterial m in Enum.GetValues(typeof(PrimMaterial)))
            {
                Assert.AreEqual(m, ProtoConvert.ToPrimMaterial(ProtoConvert.ToProto(m)));
                Assert.AreEqual(m.ToString(), ProtoConvert.ToUI(m).ToString());
                Assert.AreEqual(m, ProtoConvert.ToPrimMaterial(ProtoConvert.ToUI(m)));
            }
            foreach (P.Material m in Enum.GetValues(typeof(P.Material)))
                if (m != P.Material.Unspecified) Assert.AreEqual(m, ProtoConvert.ToProto(ProtoConvert.ToPrimMaterial(m)));
        }

        [Test]
        public void MoveModes_Roles_Channels_States_EveryValue()
        {
            Assert.AreEqual(MoveKind.Walk, ProtoConvert.ToMoveKind(P.MoveMode.Unspecified));
            foreach (MoveKind m in Enum.GetValues(typeof(MoveKind)))
            {
                Assert.AreEqual(m, ProtoConvert.ToMoveKind(ProtoConvert.ToProto(m)));
                Assert.AreEqual(m.ToString(), ProtoConvert.ToUI(m).ToString());
            }

            Assert.AreEqual(UI.Role.Visitor, ProtoConvert.ToUI(P.Role.Unspecified));
            foreach (UI.Role r in Enum.GetValues(typeof(UI.Role)))
                Assert.AreEqual(r, ProtoConvert.ToUI(ProtoConvert.ToProto(r)));
            Assert.AreEqual(UI.Role.Owner, ProtoConvert.ToUI(P.Role.Owner));
            Assert.AreEqual(UI.Role.Builder, ProtoConvert.ToUI(P.Role.Builder));

            Assert.AreEqual(UI.ChatChannel.World, ProtoConvert.ToUI(P.ChatChannel.World));
            Assert.AreEqual(UI.ChatChannel.Local, ProtoConvert.ToUI(P.ChatChannel.Local));
            Assert.AreEqual(UI.ChatChannel.Local, ProtoConvert.ToUI(P.ChatChannel.Unspecified));
            Assert.AreEqual(P.ChatChannel.World, ProtoConvert.ToProto(UI.ChatChannel.World));
            Assert.AreEqual(P.ChatChannel.Local, ProtoConvert.ToProto(UI.ChatChannel.Local));

            Assert.AreEqual(UI.ConnectionState.Disconnected, ProtoConvert.ToUI(Net.ConnectionState.Disconnected));
            Assert.AreEqual(UI.ConnectionState.Connecting, ProtoConvert.ToUI(Net.ConnectionState.Connecting));
            Assert.AreEqual(UI.ConnectionState.Connecting, ProtoConvert.ToUI(Net.ConnectionState.Joining));
            Assert.AreEqual(UI.ConnectionState.Connected, ProtoConvert.ToUI(Net.ConnectionState.Connected));
            Assert.AreEqual(UI.ConnectionState.Reconnecting, ProtoConvert.ToUI(Net.ConnectionState.Reconnecting));

            Assert.IsTrue(ProtoConvert.IsAgent(P.ClientKind.Agent));
            Assert.IsFalse(ProtoConvert.IsAgent(P.ClientKind.Human));
        }

        [Test]
        public void Prims_RoundTripThroughSpec()
        {
            var d = new PrimData
            {
                Id = 42, OwnerId = "u1", Name = "door", Shape = PrimShape.Wedge, Material = PrimMaterial.Glass,
                Position = new Vector3(1, 2, 3), Rotation = Quaternion.Euler(0, 45, 0), Scale = new Vector3(2, 1, 0.5f),
                Color = new Color(1f, 0.5f, 0f, 0.8f), Solid = true, Locked = true,
            };
            var info = new P.ObjectInfo { Id = 42, OwnerId = "u1", Spec = ProtoConvert.ToSpec(d) };
            var back = ProtoConvert.ToPrim(info);
            Assert.AreEqual(d.Id, back.Id);
            Assert.AreEqual(d.OwnerId, back.OwnerId);
            Assert.AreEqual(d.Name, back.Name);
            Assert.AreEqual(d.Shape, back.Shape);
            Assert.AreEqual(d.Material, back.Material);
            Assert.AreEqual(d.Position, back.Position);
            Assert.AreEqual(d.Rotation, back.Rotation);
            Assert.AreEqual(d.Scale, back.Scale);
            Assert.AreEqual(d.Color, back.Color);
            Assert.AreEqual(d.Solid, back.Solid);
            Assert.AreEqual(d.Locked, back.Locked);
        }

        [Test]
        public void Update_CarriesOnlyChangedFields()
        {
            var a = new PrimData { Id = 7, Name = "a", Position = Vector3.one, Rotation = Quaternion.identity, Scale = Vector3.one, Color = Color.red };
            var b = a;
            b.Position = new Vector3(2, 1, 1);
            b.Color = Color.blue;
            var u = ProtoConvert.ToUpdate(a, b);
            Assert.AreEqual(7ul, u.Id);
            Assert.IsNotNull(u.Position);
            Assert.IsNotNull(u.Color);
            Assert.IsNull(u.Rotation);
            Assert.IsNull(u.Scale);
            Assert.IsFalse(u.HasName);
            Assert.IsFalse(u.HasShape);
            Assert.IsFalse(u.HasMaterial);
            Assert.IsFalse(u.HasSolid);
            Assert.IsFalse(u.HasLocked);
        }

        [Test]
        public void Look_UsesAppearance_OrAStableColourFromTheUserId()
        {
            var derived = ProtoConvert.ToLook(null, "user-a").BodyColor;
            Assert.AreEqual(derived, ProtoConvert.ToLook(new P.Appearance(), "user-a").BodyColor);
            Assert.AreEqual(BodyColors.ForUser("user-a"), derived);
            Assert.AreNotEqual(BodyColors.ForUser("user-a"), BodyColors.ForUser("user-b"));
            // A left-out colour arrives all-zero (not chosen); white is a real choice.
            var zero = new P.Appearance { BodyColor = new P.Color() };
            Assert.AreEqual(derived, ProtoConvert.ToLook(zero, "user-a").BodyColor);
            var white = new P.Appearance { BodyColor = new P.Color { R = 1, G = 1, B = 1, A = 1 } };
            Assert.AreEqual(Color.white, ProtoConvert.ToLook(white, "user-a").BodyColor);
            var chosen = new P.Appearance { BodyColor = new P.Color { R = 0.2f, G = 0.4f, B = 0.6f, A = 1 } };
            Assert.AreEqual(new Color(0.2f, 0.4f, 0.6f, 1f), ProtoConvert.ToLook(chosen, "user-a").BodyColor);
        }

        [Test]
        public void Limits_FromWelcome()
        {
            var l = ProtoConvert.ToMoveLimits(new P.Limits { WalkSpeed = 3, RunSpeed = 7, FlySpeed = 12 },
                new P.WorldInfo { BoundsMin = new P.Vec3 { X = -10, Y = -1, Z = -10 }, BoundsMax = new P.Vec3 { X = 10, Y = 50, Z = 10 } });
            Assert.AreEqual(3f, l.WalkSpeed);
            Assert.AreEqual(7f, l.RunSpeed);
            Assert.AreEqual(12f, l.FlySpeed);
            Assert.AreEqual(new Vector3(-10, -1, -10), l.BoundsMin);
            Assert.AreEqual(new Vector3(10, 50, 10), l.BoundsMax);
            var d = ProtoConvert.ToMoveLimits(null, null);
            Assert.AreEqual(MoveLimits.Default.WalkSpeed, d.WalkSpeed);
        }

        [Test]
        public void ChatEntries_BecomeLines()
        {
            var line = ProtoConvert.ToChatLine(new P.ChatEntry
            {
                SenderName = "Scout", SenderKind = P.ClientKind.Agent, Channel = P.ChatChannel.World, Text = "hi", TimestampMs = 1_700_000_000_000,
            });
            Assert.AreEqual("Scout", line.SenderName);
            Assert.IsTrue(line.SenderIsAgent);
            Assert.AreEqual(UI.ChatChannel.World, line.Channel);
            Assert.AreEqual("hi", line.Text);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000).LocalDateTime, line.Time);
        }
    }

    public sealed class PermissionTests
    {
        [TestCase(P.Role.Visitor, false)]
        [TestCase(P.Role.Unspecified, false)]
        [TestCase(P.Role.Builder, true)]
        [TestCase(P.Role.Owner, true)]
        public void CanBuild(P.Role role, bool expected) => Assert.AreEqual(expected, Permissions.CanBuild(role));

        // role, own object, locked → may edit
        [TestCase(P.Role.Visitor, true, false, false)]
        [TestCase(P.Role.Visitor, false, false, false)]
        [TestCase(P.Role.Builder, true, false, true)]
        [TestCase(P.Role.Builder, true, true, true)]
        [TestCase(P.Role.Builder, false, false, true)]
        [TestCase(P.Role.Builder, false, true, false)]
        [TestCase(P.Role.Owner, false, true, true)]
        [TestCase(P.Role.Owner, true, true, true)]
        public void CanEdit(P.Role role, bool own, bool locked, bool expected) =>
            Assert.AreEqual(expected, Permissions.CanEdit(role, "me", own ? "me" : "other", locked));

        [Test]
        public void CanLock()
        {
            Assert.IsTrue(Permissions.CanLock(P.Role.Owner, "me", "other"));
            Assert.IsTrue(Permissions.CanLock(P.Role.Builder, "me", "me"));
            Assert.IsFalse(Permissions.CanLock(P.Role.Builder, "me", "other"));
            Assert.IsFalse(Permissions.CanLock(P.Role.Visitor, "me", "me"));
            Assert.IsFalse(Permissions.CanEdit(P.Role.Builder, "", "", true), "an empty user id owns nothing");
        }
    }

    public sealed class ErrorTextTests
    {
        [Test]
        public void EveryCode_HasItsOwnSentence()
        {
            var codes = Enum.GetValues(typeof(P.ErrorCode)).Cast<P.ErrorCode>().ToList();
            var sentences = codes.Select(ErrorText.Describe).ToList();
            foreach (var s in sentences)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(s));
                Assert.IsTrue(s.EndsWith("."), s);
            }
            Assert.AreEqual(codes.Count, sentences.Distinct().Count(), "sentences are distinct");
            StringAssert.Contains("password", ErrorText.Describe(P.ErrorCode.AuthFailed));
            Assert.AreEqual(ErrorText.Describe(P.ErrorCode.NotFound) + " (object 9)", ErrorText.Describe(P.ErrorCode.NotFound, "object 9"));
        }
    }

    public sealed class CommandLineTests
    {
        [Test]
        public void ParsesEveryOption()
        {
            var c = CommandLine.Parse(new[]
            {
                "player", "-logFile", "x.log", "--connect", "192.168.1.5", "--name", "Ann", "--password=pw",
                "--screenshot-dir", "/tmp/s", "--screenshot-every", "2.5", "--quit-after", "30", "--demo-walk", "--demo-build",
                "--fresh-profile", "/tmp/p", "--screenshot-tour",
            }, skipFirst: true);
            CollectionAssert.IsEmpty(c.Errors);
            Assert.AreEqual("192.168.1.5", c.ConnectAddress);
            Assert.AreEqual("Ann", c.Name);
            Assert.AreEqual("pw", c.Password);
            Assert.AreEqual("/tmp/s", c.ScreenshotDirectory);
            Assert.AreEqual(2.5f, c.ScreenshotEvery);
            Assert.AreEqual(30f, c.QuitAfter);
            Assert.IsTrue(c.DemoWalk);
            Assert.IsTrue(c.DemoBuild);
            Assert.IsTrue(c.ScreenshotTour);
            Assert.AreEqual("/tmp/p", c.ProfileDirectory);
            Assert.IsNull(c.HostWorldDirectory);
        }

        [Test]
        public void Host_WithPort()
        {
            var c = CommandLine.Parse(new[] { "--host", "/w", "--port", "9000" });
            Assert.AreEqual("/w", c.HostWorldDirectory);
            Assert.AreEqual(9000, c.Port);
        }

        [Test]
        public void Host_WithTemplate_AndScreenshotViews()
        {
            var c = CommandLine.Parse(new[] { "--host", "/w", "--template", "island", "--screenshot-views", "1,1.7,-3,90,10; -4.5,2,0,-45,-5 ;bad" });
            Assert.AreEqual("island", c.Template);
            Assert.AreEqual(2, c.ScreenshotViews.Count);
            Assert.AreEqual(new Vector3(-4.5f, 2f, 0f), c.ScreenshotViews[1].Position);
            Assert.AreEqual(-45f, c.ScreenshotViews[1].Yaw);
            Assert.AreEqual(-5f, c.ScreenshotViews[1].Pitch);
            Assert.AreEqual(1, c.Errors.Count, string.Join("; ", c.Errors));
            StringAssert.Contains("bad", c.Errors[0]);
        }

        [Test]
        public void BadValues_AreReported()
        {
            var c = CommandLine.Parse(new[] { "--port", "99999", "--quit-after", "soon", "--connect" });
            Assert.AreEqual(3, c.Errors.Count, string.Join("; ", c.Errors));
            Assert.IsNull(c.Port);
            Assert.AreEqual(0f, c.QuitAfter);
            Assert.IsNull(c.ConnectAddress);
            Assert.AreEqual(1, CommandLine.Parse(new[] { "--connect", "a", "--host", "b" }).Errors.Count);
        }

        [Test]
        public void Empty_IsFine()
        {
            var c = CommandLine.Parse(new string[0]);
            CollectionAssert.IsEmpty(c.Errors);
            Assert.IsFalse(c.DemoWalk);
        }
    }

    public sealed class SettingsStoreTests
    {
        string dir;

        [SetUp]
        public void SetUp()
        {
            dir = Path.Combine(Path.GetTempPath(), "tv-settings-" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Test]
        public void Missing_ReadsAsDefaults()
        {
            var s = new SettingsStore(dir).Load();
            Assert.AreEqual(UI.ClientSettings.Default.FieldOfView, s.client.FieldOfView);
            Assert.AreEqual("", s.lastName);
        }

        [Test]
        public void SaveAndLoad_RoundTrip()
        {
            var store = new SettingsStore(dir);
            var s = store.Load();
            s.client.MouseSensitivity = 2.5f;
            s.client.InvertY = true;
            s.client.FieldOfView = 90f;
            s.client.QualityIndex = 1;
            s.client.Fullscreen = true;
            s.client.MasterVolume = 0.3f;
            s.client.UIScale = 1.25f;
            s.lastName = "Ann";
            s.lastAddress = "myhost:9000";
            s.bodyColor = "#336699";
            store.Save(s);

            var loaded = new SettingsStore(dir).Load();
            Assert.AreEqual(2.5f, loaded.client.MouseSensitivity);
            Assert.IsTrue(loaded.client.InvertY);
            Assert.AreEqual(90f, loaded.client.FieldOfView);
            Assert.AreEqual(1, loaded.client.QualityIndex);
            Assert.IsTrue(loaded.client.Fullscreen);
            Assert.AreEqual(0.3f, loaded.client.MasterVolume);
            Assert.AreEqual(1.25f, loaded.client.UIScale);
            Assert.AreEqual("Ann", loaded.lastName);
            Assert.AreEqual("myhost:9000", loaded.lastAddress);
            Assert.AreEqual(new Color(0.2f, 0.4f, 0.6f, 1f), SettingsStore.ParseBodyColor(loaded.bodyColor));
        }

        [Test]
        public void Corrupt_ReadsAsDefaults_AndOutOfRangeIsClamped()
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, SettingsStore.FileName), "{ not json");
            Assert.AreEqual(UI.ClientSettings.Default.UIScale, new SettingsStore(dir).Load().client.UIScale);

            var c = UI.ClientSettings.Default;
            c.FieldOfView = 500f;
            c.UIScale = 0.1f;
            c.MasterVolume = float.NaN;
            c.QualityIndex = -3;
            var s = SettingsStore.Sanitise(c);
            Assert.AreEqual(UI.SettingsPanel.MaxFov, s.FieldOfView);
            Assert.AreEqual(UI.SettingsPanel.MinUIScale, s.UIScale);
            Assert.AreEqual(UI.ClientSettings.Default.MasterVolume, s.MasterVolume);
            Assert.AreEqual(0, s.QualityIndex);
            Assert.IsNull(SettingsStore.ParseBodyColor(""));
            Assert.IsNull(SettingsStore.ParseBodyColor("nonsense"));
        }
    }

    public sealed class AvatarStateSenderTests
    {
        static AvatarPose At(float x) => new AvatarPose(new Vector3(x, 0, 0), 0f);

        [Test]
        public void Moving_SendsAt15Hz_Idle_At1Hz_AndTheStopAtOnce()
        {
            var s = new AvatarStateSender();
            double t = 0;
            int sent = 0;
            // One second of movement at 60 fps.
            for (int i = 0; i < 60; i++, t += 1 / 60.0)
                if (s.ShouldSend(At(i * 0.05f), t)) sent++;
            Assert.That(sent, Is.InRange(14, 16), "about 15 per second while moving");

            // Stopped: the first unchanged frame after a send goes out at once.
            var still = At(60 * 0.05f);
            Assert.IsTrue(s.ShouldSend(still, t) || s.ShouldSend(still, t + 1 / 60.0));
            t += 2 / 60.0;
            int idle = 0;
            for (int i = 0; i < 300; i++, t += 1 / 60.0)
                if (s.ShouldSend(still, t)) idle++;
            Assert.That(idle, Is.InRange(4, 6), "about once a second while idle");
        }

        [Test]
        public void FirstPose_AndAfterReset_SendImmediately()
        {
            var s = new AvatarStateSender();
            Assert.IsTrue(s.ShouldSend(At(0), 0));
            Assert.IsFalse(s.ShouldSend(At(0), 0.01));
            s.Reset();
            Assert.IsTrue(s.ShouldSend(At(0), 0.02));
        }
    }

    public sealed class TemplateGlueTests
    {
        [Test]
        public void TheTemplateGoesToInit_OnlyForANewWorld()
        {
            string dir = Path.Combine(Path.GetTempPath(), "tv-glue-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var form = new UI.HostSettings { WorldName = "W", WorldFolder = Path.Combine(dir, "new"), Port = 7878, Template = " island " };
                Assert.AreEqual("island", HostingController.ToHostSettings(form).Template);
                form.Template = "";
                Assert.IsNull(HostingController.ToHostSettings(form).Template, "empty: the server's default");

                Directory.CreateDirectory(Path.Combine(dir, "old"));
                File.WriteAllText(Path.Combine(dir, "old", "world.toml"), "name = \"Old\"\n");
                form.WorldFolder = Path.Combine(dir, "old");
                form.Template = "room";
                Assert.IsNull(HostingController.ToHostSettings(form).Template, "an existing world opens as it is");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch (Exception) { }
            }
        }

        [Test]
        public void TemplatesBecomeHostScreenChoices()
        {
            var choices = HostingController.ToChoices(new[]
            {
                new Tinyverse.Host.WorldTemplate("empty", "Empty", "Nothing.", 0),
                new Tinyverse.Host.WorldTemplate("town", "Town", "A square.", 300),
            });
            Assert.AreEqual(new[] { "empty", "town" }, choices.Select(c => c.Name).ToArray());
            Assert.AreEqual("Town", choices[1].Title);
            Assert.AreEqual("A square.", choices[1].Description);
            Assert.IsEmpty(HostingController.ToChoices(null));
        }

        [Test]
        public void TheGroundPresetReachesTheEnvironment()
        {
            var env = ProtoConvert.ToEnvironment(new P.WorldInfo { Ground = "water", Sky = "day", GroundSize = 512 });
            Assert.AreEqual("water", env.Ground);
            Assert.AreEqual("", ProtoConvert.ToEnvironment(new P.WorldInfo()).Ground ?? "");
        }
    }
}
