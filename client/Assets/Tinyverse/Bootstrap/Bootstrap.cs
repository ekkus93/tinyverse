using UnityEngine;

namespace Tinyverse.Bootstrap
{
    /// <summary>
    /// The only scene object. Everything else is created from code at startup.
    /// </summary>
    public sealed class Bootstrap : MonoBehaviour
    {
        private void Awake()
        {
            Debug.Log("Tinyverse bootstrap");
        }
    }
}
