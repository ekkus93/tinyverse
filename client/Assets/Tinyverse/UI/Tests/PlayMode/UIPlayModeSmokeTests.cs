using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.UI.Tests
{
    public class UIPlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator FramesAdvance()
        {
            Assert.That(typeof(UIAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.UI"));
            int start = Time.frameCount;
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(start));
        }
    }
}
