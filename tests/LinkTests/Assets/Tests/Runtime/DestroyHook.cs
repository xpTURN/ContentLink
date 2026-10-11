using System;
using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>Runs an action from its OnDestroy — added after the scope owner, so it runs after the owner's.</summary>
    public sealed class DestroyHook : MonoBehaviour
    {
        public Action whenDestroyed;
        void OnDestroy() => whenDestroyed?.Invoke();
    }
}
