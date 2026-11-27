using NUnit.Framework;

namespace Tinyverse.World.Tests
{
    public class WorldEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(WorldAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.World"));
        }
    }
}
