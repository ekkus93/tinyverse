using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Tinyverse.Host.Tests
{
    /// <summary>World templates: listing them from the server and passing the choice to <c>init</c>.</summary>
    public class TemplateTests : FakeServerFixture
    {
        static string WorldToml(HostSettings s) => File.ReadAllText(Path.Combine(s.WorldDirectory, "world.toml"));

        [Test]
        public void ListAsync_ReadsTheServersJsonInOrder()
        {
            var list = Wait(WorldTemplates.ListAsync(FakeServer));
            Assert.That(list.Select(t => t.Name), Is.EqualTo(new[] { "empty", "room", "island" }));
            Assert.That(list[2].Title, Is.EqualTo("Island"));
            Assert.That(list[2].Description, Is.EqualTo("A sandy island with \"palms\"."));
            Assert.That(list[2].ObjectCount, Is.EqualTo(34));
            Assert.That(list[0].ObjectCount, Is.EqualTo(0));
        }

        [Test]
        public void ListAsync_ReportsAFailingOrConfusedServer()
        {
            foreach (string mode in new[] { "fail", "garbage" })
            {
                string wrapper = Path.Combine(Temp, "templates-" + mode);
                File.WriteAllText(wrapper, $"#!/bin/sh\nFAKE_TEMPLATES={mode} exec '{FakeServer}' \"$@\"\n");
                using (var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/chmod", "+x \"" + wrapper + "\"") { UseShellExecute = false }))
                    chmod.WaitForExit(5000);
                var e = WaitFault(WorldTemplates.ListAsync(wrapper));
                Assert.That(e, Is.InstanceOf<HostException>(), mode);
                Assert.That(((HostException)e).Kind, Is.EqualTo(HostErrorKind.LaunchFailed), mode);
                Assert.That(e.Message, Does.Contain("template"), mode);
            }
            var missing = WaitFault(WorldTemplates.ListAsync(Path.Combine(Temp, "no-such-server")));
            Assert.That(missing, Is.InstanceOf<HostException>());
        }

        [Test]
        public void Parse_SkipsNamelessEntriesAndFillsTitles()
        {
            var list = WorldTemplates.Parse("{\"templates\":[{\"name\":\" house \",\"description\":\"d\",\"objects\":5},{\"title\":\"x\"},{\"name\":\"\"}]}");
            Assert.That(list.Count, Is.EqualTo(1));
            Assert.That(list[0].Name, Is.EqualTo("house"));
            Assert.That(list[0].Title, Is.EqualTo("house"), "no title: the name");
            Assert.That(WorldTemplates.Parse("{}"), Is.Empty);
            Assert.Throws<FormatException>(() => WorldTemplates.Parse("not json"));
        }

        [Test]
        public void NewWorld_IsCreatedFromTheChosenTemplate()
        {
            var server = NewServer();
            var settings = Settings("from-template");
            settings.Template = "island";
            var rec = new Recorder(server);
            Wait(server.StartAsync(settings));
            Track(server.ProcessId);
            server.Pump();
            Assert.That(WorldToml(settings), Does.Contain("template = \"island\""));
            Assert.That(rec.Lines.Any(l => l.Contains("island template")), Is.True, rec.AllText);
            Assert.That(settings.ToString(), Does.Contain("template=island"));
        }

        [Test]
        public void NoTemplate_LeavesTheChoiceToTheServer()
        {
            var server = NewServer();
            var settings = Settings("no-template");
            Wait(server.StartAsync(settings));
            Track(server.ProcessId);
            Assert.That(WorldToml(settings), Does.Not.Contain("template ="), "no --template on the command line");
        }

        [Test]
        public void ExistingWorld_IgnoresTheTemplate()
        {
            var settings = Settings("existing-world");
            var first = NewServer();
            Wait(first.StartAsync(settings));
            Track(first.ProcessId);
            Assert.That(Wait(first.StopAsync(TimeSpan.FromSeconds(5))).Graceful, Is.True);
            string before = WorldToml(settings);

            settings.Template = "room";
            var again = NewServer();
            Wait(again.StartAsync(settings));
            Track(again.ProcessId);
            Assert.That(WorldToml(settings), Is.EqualTo(before), "the world on disk is left as it was");
        }

        [Test]
        public void UnknownTemplate_FailsInitWithTheServersReason()
        {
            var server = NewServer();
            var settings = Settings("castle");
            settings.Template = "castle";
            var e = WaitFault(server.StartAsync(settings));
            Assert.That(e, Is.InstanceOf<HostException>());
            Assert.That(((HostException)e).Kind, Is.EqualTo(HostErrorKind.InitFailed));
            Assert.That(e.Message, Does.Contain("castle"));
            Assert.That(WorldDirectories.IsWorld(settings.WorldDirectory), Is.False);
        }
    }
}
