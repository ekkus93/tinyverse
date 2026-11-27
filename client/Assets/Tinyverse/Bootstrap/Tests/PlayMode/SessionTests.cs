using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tinyverse.Avatars;
using Tinyverse.World;
using UnityEngine;
using UnityEngine.TestTools;
using P = Tinyverse.Proto;
using UI = Tinyverse.UI;

namespace Tinyverse.Bootstrap.Tests
{
    /// <summary>The real WorldSession against the real server, with tv-cli as the other participant.</summary>
    public sealed class SessionTests
    {
        ServerFixture server;
        SessionHarness h;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            server = new ServerFixture();
            yield return server.Start();
            h = new SessionHarness();
        }

        [TearDown]
        public void TearDown()
        {
            h?.Dispose();
            server?.Dispose();
            server?.AssertNothingLeft();
        }

        [UnityTest]
        public IEnumerator Join_BuildsTheEnvironmentAndOwnAvatar()
        {
            yield return h.Join(server.Url, "Joiner");
            var s = h.Session;
            Assert.AreEqual("Test World", s.WorldName);
            Assert.AreEqual(P.Role.Builder, s.Role);
            Assert.IsNotNull(s.Environment.Ground, "ground");
            Assert.AreEqual(256f, s.Environment.Ground.transform.localScale.x, 0.01f, "ground size from WorldInfo");
            Assert.AreEqual("day", s.Environment.Current.Sky);
            Assert.IsNotNull(RenderSettings.skybox, "sky");
            Assert.IsNotNull(s.LocalAvatar);
            Assert.AreEqual(s.OwnAvatarId, s.LocalAvatar.Id);
            Assert.Less(Vector3.Distance(Vector3.zero, s.LocalAvatar.transform.position), 1f, "at the spawn point");
            Assert.AreEqual(new Vector3(128, 256, 128), s.LocalAvatar.Limits.BoundsMax);
            Assert.AreEqual(1, h.UI.People.Count);
            Assert.AreEqual("Test World", h.UI.StatusBar.WorldName);
            Assert.AreEqual(UI.ConnectionState.Connected, h.UI.StatusBar.ConnectionState);
            Assert.IsNotNull(s.Orbit);
            Assert.AreSame(s.LocalAvatar.transform, s.Orbit.Target);
        }

        [UnityTest]
        public IEnumerator CircleBot_AppearsAndMovesSmoothlyAlongTheCircle()
        {
            yield return h.Join(server.Url, "Watcher");
            server.Cli("Circler", "bot", "circle", "--radius", "5");
            RemoteAvatar bot = null;
            yield return Wait.For(() => (bot = h.Session.RemoteAvatars.Values.FirstOrDefault(r => r.DisplayName == "Circler")) != null, 10f, "the circle bot");
            Assert.IsTrue(bot.IsAgent, "agent badge");
            Assert.AreEqual(2, h.UI.People.Count);
            // The bot walks out from the spawn point first.
            yield return Wait.For(() => Flat(bot.transform.position).magnitude > 4.7f, 10f, "the bot to reach its circle");

            var last = bot.transform.position;
            float path = 0f, maxStep = 0f, minR = float.MaxValue, maxR = 0f;
            float end = Time.realtimeSinceStartup + 4f;
            int frames = 0;
            while (Time.realtimeSinceStartup < end)
            {
                yield return null;
                var p = bot.transform.position;
                float step = Vector3.Distance(p, last);
                float allowed = 4f * 1.5f * Mathf.Max(Time.deltaTime, 0.02f) + 0.05f;
                maxStep = Mathf.Max(maxStep, step / allowed);
                path += step;
                float r = Flat(p).magnitude;
                minR = Mathf.Min(minR, r);
                maxR = Mathf.Max(maxR, r);
                last = p;
                frames++;
            }
            Debug.Log($"[Test] circle: {frames} frames, path {path:0.00} m, radius {minR:0.00}..{maxR:0.00}, worst step {maxStep:0.00} of allowed");
            Assert.Greater(path, 8f, "it moved");
            Assert.That(minR, Is.GreaterThan(4.5f));
            Assert.That(maxR, Is.LessThan(5.5f));
            Assert.LessOrEqual(maxStep, 1f, "no frame jumps faster than walking (no snapping)");
        }

        static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        [UnityTest]
        public IEnumerator LocalMovement_IsSeenByTvCli_AndDrawsNoCorrection()
        {
            yield return h.Join(server.Url, "Mover");
            var watcher = server.Cli("Watcher", "connect");
            yield return Wait.For(() => watcher.Lines.Any(l => l.StartsWith("joined")), 10f, "tv-cli to join");
            var a = h.Session.LocalAvatar;
            var input = h.Input;
            int sentBefore = h.Session.StatesSent;

            input.Move = new Vector2(0, 1);                              // walk 3 s
            yield return Wait.Seconds(3f);
            input.Run = true;                                            // run 2 s
            yield return Wait.Seconds(2f);
            input.Jump();                                                // a running jump
            yield return Wait.Seconds(1f);
            input.Run = false;
            input.ToggleFly();                                           // fly up and forward
            input.Up = true;
            yield return Wait.Seconds(1.5f);
            Assert.IsTrue(a.IsFlying, "flying");
            input.Up = false;
            input.Run = true;
            yield return Wait.Seconds(1.5f);
            float highest = a.transform.position.y;
            input.Run = false;
            input.Move = Vector2.zero;
            input.Down = true;                                           // land
            yield return Wait.For(() => a.IsGrounded, 10f, "landing");
            input.Down = false;
            yield return Wait.Seconds(1.2f);

            var final = a.transform.position;
            Debug.Log($"[Test] moved to {final}, highest {highest:0.0}, {h.Session.StatesSent - sentBefore} reports, corrections {h.Session.CorrectionCount} ({h.Session.LastCorrectionReason})");
            Assert.AreEqual(0, h.Session.CorrectionCount, "no correction: " + h.Session.LastCorrectionReason);
            Assert.Greater(highest, 3f, "flew up");
            Assert.Greater(final.magnitude, 20f, "moved a fair way");

            var re = new Regex(@"^states: Mover \((-?[\d.]+), (-?[\d.]+), (-?[\d.]+)\)");
            var seen = watcher.Lines.Select(l => re.Match(l)).Where(m => m.Success)
                .Select(m => new Vector3(float.Parse(m.Groups[1].Value), float.Parse(m.Groups[2].Value), float.Parse(m.Groups[3].Value))).ToList();
            Assert.Greater(seen.Count, 50, "tv-cli saw a stream of states");
            Assert.Less(Vector3.Distance(seen.Last(), final), 0.5f, $"tv-cli's last view {seen.Last()} matches {final}");
            Assert.Greater(seen.Max(p => p.y), 3f, "tv-cli saw the flight");
        }

        [UnityTest]
        public IEnumerator Chat_BothWays_AndHistoryOnRejoin()
        {
            yield return h.Join(server.Url, "Talker");
            var cli = server.Cli("Echo", "connect");
            yield return Wait.For(() => cli.Lines.Any(l => l.StartsWith("joined")), 10f, "tv-cli to join");
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Channel == UI.ChatChannel.System && l.Text.Contains("Echo joined")), 5f, "the join line");

            cli.WriteLine("hello from tv-cli");
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Text == "hello from tv-cli"), 5f, "tv-cli's line in the chat panel");
            var line = h.UI.Chat.Lines.First(l => l.Text == "hello from tv-cli");
            Assert.AreEqual("Echo", line.SenderName);
            Assert.IsTrue(line.SenderIsAgent);
            Assert.AreEqual(UI.ChatChannel.World, line.Channel);

            h.UI.Chat.OpenInput(UI.ChatChannel.World);
            h.UI.Chat.SetInputText("hello from unity");
            h.UI.Chat.Submit();
            yield return Wait.For(() => cli.Lines.Any(l => l.Contains("Talker") && l.Contains("hello from unity")), 5f, "tv-cli to hear Unity");
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Text == "hello from unity" && l.SenderName == "Talker"), 5f, "our own echo");

            h.UI.Chat.OpenInput(UI.ChatChannel.Local);
            h.UI.Chat.SetInputText("local words");
            h.UI.Chat.Submit();
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Text == "local words" && l.Channel == UI.ChatChannel.Local), 5f, "local chat");

            cli.WriteLine("/quit");
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Channel == UI.ChatChannel.System && l.Text.Contains("Echo left")), 5f, "the leave line");

            // Rejoin: the world chat is replayed from the server's history (local chat is not kept).
            h.Leave();
            yield return h.Join(server.Url, "Talker");
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Text == "hello from unity"), 5f, "history");
            var texts = h.UI.Chat.Lines.Select(l => l.Text).ToList();
            CollectionAssert.Contains(texts, "hello from tv-cli");
            CollectionAssert.DoesNotContain(texts, "local words");
            Assert.Less(texts.IndexOf("hello from tv-cli"), texts.IndexOf("hello from unity"), "oldest first");
        }

        [UnityTest]
        public IEnumerator Objects_CreatedUpdatedDeletedByTvCli_FollowInTheRegistry()
        {
            yield return h.Join(server.Url, "Viewer");
            var create = server.CliRun("--name", "Maker", "create", "--shape", "cone", "--pos", "3,1,4", "--rot", "0,45,0",
                "--scale", "1,2,0.5", "--color", "#ff8000", "--material", "metal", "--object-name", "beacon");
            yield return Wait.Task(create, 20f, "tv-cli create");
            Assert.AreEqual(0, create.Result.code, create.Result.output);
            ulong id = ServerFixture.ParseObjectId(create.Result.output);

            PrimData d = default;
            yield return Wait.For(() => h.Session.Objects.TryGet(id, out d), 5f, "the object in the registry");
            Assert.AreEqual(PrimShape.Cone, d.Shape);
            Assert.AreEqual(PrimMaterial.Metal, d.Material);
            Assert.AreEqual("beacon", d.Name);
            Assert.AreEqual(1f, d.Color.r, 1e-3f);
            Assert.AreEqual(128f / 255f, d.Color.g, 1e-3f);
            Assert.AreEqual(0f, d.Color.b, 1e-3f);
            Assert.AreEqual(new Vector3(3, 1, 4), d.Position);
            Assert.AreEqual(new Vector3(1, 2, 0.5f), d.Scale);
            Assert.Less(Quaternion.Angle(Quaternion.Euler(0, 45, 0), d.Rotation), 0.1f);
            var go = h.Session.Objects.GetGameObject(id);
            Assert.AreEqual(new Vector3(3, 1, 4), go.transform.position);
            Assert.IsNotNull(go.GetComponent<MeshRenderer>().sharedMaterial);

            var update = server.CliRun("--name", "Maker", "update", id.ToString(), "--pos", "5,1,4", "--color", "#00ff00");
            yield return Wait.Task(update, 20f, "tv-cli update");
            Assert.AreEqual(0, update.Result.code, update.Result.output);
            yield return Wait.For(() => h.Session.Objects.TryGet(id, out d) && d.Position.x == 5f, 5f, "the update");
            Assert.AreEqual(Color.green, d.Color);
            yield return Wait.For(() => (go.transform.position - new Vector3(5, 1, 4)).sqrMagnitude < 1e-6f, 2f, "the smoothed move to finish");

            var delete = server.CliRun("--name", "Maker", "delete", id.ToString());
            yield return Wait.Task(delete, 20f, "tv-cli delete");
            Assert.AreEqual(0, delete.Result.code, delete.Result.output);
            yield return Wait.For(() => !h.Session.Objects.Contains(id), 5f, "the deletion");
            yield return null;
            Assert.IsTrue(go == null, "its GameObject is destroyed");
        }

        [UnityTest]
        public IEnumerator ServerRestart_Reconnects_AndRebuildsTheWorld()
        {
            yield return h.Join(server.Url, "Stayer");
            var create = server.CliRun("--name", "Maker", "create", "--shape", "sphere", "--pos", "2,1,2", "--color", "#3366ff");
            yield return Wait.Task(create, 20f, "tv-cli create");
            ulong id = ServerFixture.ParseObjectId(create.Result.output);
            yield return Wait.For(() => h.Session.Objects.Contains(id), 5f, "the object");
            var oldGround = h.Session.Environment.Ground;
            int port = server.Port;

            server.Stop(); // SIGTERM: the server saves the world and exits
            yield return Wait.For(() => h.Session.Connection.State == Net.ConnectionState.Reconnecting, 10f, "reconnecting");
            Assert.AreEqual(UI.ConnectionState.Reconnecting, h.UI.StatusBar.ConnectionState);
            Assert.IsTrue(h.UI.Chat.Lines.Any(l => l.Text.Contains("Reconnecting")), "a chat line says so");

            yield return server.Start(port);
            yield return Wait.For(() => h.Session.WelcomeCount == 2 && h.Session.SnapshotComplete, 30f, "the reconnect");
            Assert.IsFalse(h.Ended);
            Assert.IsTrue(h.Session.Objects.Contains(id), "the persisted object is back");
            Assert.AreEqual(1, h.Session.Objects.Count);
            Assert.IsNotNull(h.Session.Environment.Ground);
            Assert.AreSame(oldGround, h.Session.Environment.Ground, "the environment is updated in place");
            Assert.AreEqual(UI.ConnectionState.Connected, h.UI.StatusBar.ConnectionState);
            yield return Wait.For(() => h.UI.Chat.Lines.Any(l => l.Text.Contains("Reconnected")), 5f, "the reconnect line");
            Assert.AreEqual(1, h.UI.People.Count);
        }

        [UnityTest]
        public IEnumerator ServerErrors_BecomeToasts()
        {
            yield return h.Join(server.Url, "Erring");
            // An update of an object that does not exist: NOT_FOUND as a readable toast.
            h.Session.Connection.UpdateObject(new P.ObjectUpdate { Id = 999999, Position = new P.Vec3 { X = 1 } });
            yield return Wait.For(() => h.UI.Toasts.Messages.Any(m => m.StartsWith(ErrorText.Describe(P.ErrorCode.NotFound))), 5f, "the toast");
        }
    }
}
