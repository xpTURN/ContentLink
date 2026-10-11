using System;
using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>Runs an action from its Awake — added before the scope owner, so a clone's copy wakes before the clone's owner.</summary>
    public sealed class AwakeHook : MonoBehaviour
    {
        public static Action<GameObject> whenAwake;
        void Awake() => whenAwake?.Invoke(gameObject);
    }
}
