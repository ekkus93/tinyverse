using NUnit.Framework;

namespace Tinyverse.Build.Tests
{
    public class BuildEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(BuildAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Build"));
        }
    }
}
