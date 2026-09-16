using System;
using System.Collections.Generic;
using UnityEngine;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// Binds a <see cref="ContentScope"/> to a GameObject. Added on demand by <c>gameObject.Scope()</c>.
    ///
    /// - OnDestroy disposes the scope.
    /// - OnDestroy never runs for objects that were never activated; the scene scope catches those when the scene unloads.
    /// - Clones made with Object.Instantiate inherit the scope's keys (serialized below) and take their own references
    ///   in Awake, so a clone keeps its textures alive even if the original is destroyed first.
    ///   Awake is deferred for clones created inactive, so they take references only when first activated.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ContentScopeOwner : MonoBehaviour
    {
        [SerializeField, HideInInspector] string[] m_InheritKeys = Array.Empty<string>();

        [NonSerialized] ContentScope m_Scope;

        public ContentScope Scope
        {
            get
            {
                if (m_Scope == null || m_Scope.IsDisposed)
                {
                    m_Scope = ContentScope.CreateForGameObject(gameObject);
                    m_Scope.BindOwnerComponent(this);
                }
                return m_Scope;
            }
        }

        internal static ContentScopeOwner Get(GameObject go)
        {
            var c = go.GetComponent<ContentScopeOwner>();
            return c != null ? c : go.AddComponent<ContentScopeOwner>();
        }

        void Awake()
        {
            // Runs on clones: take our own references to what the original held.
            if (m_Scope != null || m_InheritKeys == null || m_InheritKeys.Length == 0) return;
            var keys = m_InheritKeys;
            var scope = Scope;
            foreach (var key in keys)
            {
                if (ContentService.TryShareLive(key, out var h)) scope.KeepHandle(key, h);   // normal case: no I/O
                else scope.Load<UnityEngine.Object>(key);                                   // original already gone
            }
        }

        void OnDestroy()
        {
            var s = m_Scope;
            m_Scope = null;
            s?.DisposeCore(s);
        }

        internal void SyncKeys(IEnumerable<string> keys) => m_InheritKeys = new List<string>(keys).ToArray();

        internal void OnScopeDisposed(ContentScope scope)
        {
            if (m_Scope == scope) m_Scope = null;
        }
    }

    public static class ContentScopeExtensions
    {
        /// <summary>The scope that lives as long as this GameObject.</summary>
        public static ContentScope Scope(this GameObject go) => ContentScopeOwner.Get(go).Scope;

        /// <summary>The scope that lives as long as this component's GameObject.</summary>
        public static ContentScope Scope(this Component component) => component.gameObject.Scope();
    }
}
