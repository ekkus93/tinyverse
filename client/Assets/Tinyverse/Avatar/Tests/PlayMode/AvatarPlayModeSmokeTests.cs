using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Tinyverse.Avatar.Tests
{
    public class AvatarPlayModeSmokeTests
    {
        [UnityTest]
        public IEnumerator FramesAdvance()
        {
            Assert.That(typeof(AvatarAssembly).Assembly.GetName().Name, Is.EqualTo("Tinyverse.Avatar"));
            int start = Time.frameCount;
            yield return null;
            Assert.That(Time.frameCount, Is.GreaterThan(start));
        }
    }
}
