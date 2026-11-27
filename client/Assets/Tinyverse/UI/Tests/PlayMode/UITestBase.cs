using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Tinyverse.UI.Tests
{
    /// <summary>Builds the whole UI from <see cref="UIRoot.Create"/> for each test and drives it with UI Toolkit events.</summary>
    public abstract class UITestBase
    {
        protected UIRoot UI;

        [UnitySetUp]
        public IEnumerator CreateUI()
        {
            UI = UIRoot.Create();
            yield return null;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator DestroyUI()
        {
            if (UI != null) Object.Destroy(UI.gameObject);
            yield return null;
        }

        /// <summary>Activates a button as keyboard or controller "submit" does; disabled buttons ignore it.</summary>
        protected static void Click(Button b)
        {
            Assert.That(b.panel, Is.Not.Null, $"button '{b.name}' is not in a panel");
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = b;
                b.SendEvent(e);
            }
        }

        protected static void PressKey(VisualElement target, KeyCode key, char c = '\0', EventModifiers mods = EventModifiers.None)
        {
            using (var e = KeyDownEvent.GetPooled(c, key, mods))
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        protected static void PointerDown(VisualElement target, int clickCount)
        {
            var imgui = new Event { type = EventType.MouseDown, button = 0, clickCount = clickCount, mousePosition = target.worldBound.center };
            using (var e = PointerDownEvent.GetPooled(imgui))
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        protected static void Change<T>(BaseField<T> field, T value) => field.value = value;

        protected static bool Shown(VisualElement e)
        {
            for (var p = e; p != null; p = p.hierarchy.parent)
                if (p.style.display == DisplayStyle.None) return false;
            return true;
        }
    }
}
