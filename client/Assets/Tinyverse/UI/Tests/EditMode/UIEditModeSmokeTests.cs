using NUnit.Framework;

namespace Tinyverse.UI.Tests
{
    public class UIEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(UIAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.UI"));
        }
    }
}
