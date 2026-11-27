using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.World.Tests
{
    public class WorldPlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator FramesAdvance()
        {
            Assert.That(typeof(WorldAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.World"));
            int start = Time.frameCount;
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(start));
        }
    }
}
