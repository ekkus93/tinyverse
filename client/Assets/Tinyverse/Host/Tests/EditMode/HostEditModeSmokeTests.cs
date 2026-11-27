using NUnit.Framework;

namespace Tinyverse.Host.Tests
{
    public class HostEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(ServerProcess).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Host"));
        }
    }
}
