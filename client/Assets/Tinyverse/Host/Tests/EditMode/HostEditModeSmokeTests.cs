using NUnit.Framework;

namespace Tinyverse.Host.Tests
{
    public class HostEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(HostAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Host"));
        }
    }
}
