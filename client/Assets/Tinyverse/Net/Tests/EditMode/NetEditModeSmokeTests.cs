using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using NUnit.Framework;

namespace Tinyverse.Net.Tests
{
    public class NetEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(NetAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Net"));
        }

        [Test]
        public void ProtobufRoundTrips()
        {
            var ts = new Timestamp { Seconds = 1234567890, Nanos = 42 };
            var copy = Timestamp.Parser.ParseFrom(ts.ToByteArray());
            Assert.That(copy, Is.EqualTo(ts));
        }
    }
}
