using NUnit.Framework;

namespace Tinyverse.Avatar.Tests
{
    public class AvatarEditModeSmokeTests
    {
        [Test]
        public void ModuleAssemblyIsReferenced()
        {
            Assert.That(typeof(AvatarAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Avatar"));
        }
    }
}
