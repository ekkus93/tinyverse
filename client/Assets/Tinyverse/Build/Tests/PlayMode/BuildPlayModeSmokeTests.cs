using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Build.Tests
{
    public class BuildPlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator FramesAdvance()
        {
            Assert.That(typeof(BuildAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Build"));
            int start = Time.frameCount;
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(start));
        }
    }
}
