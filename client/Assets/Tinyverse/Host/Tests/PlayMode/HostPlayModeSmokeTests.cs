using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Host.Tests
{
    public class HostPlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator FramesAdvance()
        {
            Assert.That(typeof(HostAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Host"));
            int start = Time.frameCount;
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(start));
        }
    }
}
