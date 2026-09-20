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
            if (m_Scope != null || m_InheritKeys == null || m_InheritKeys.Length == 0 || string.IsNullOrEmpty(m_InheritKeys[0])) return;   // the keys sit at the front of a never-shrinking array: an empty first slot means it holds none
            var keys = m_InheritKeys;
            var scope = m_Scope = ContentScope.CreateForGameObject(gameObject);   // bound after the loop: the keys sync once, not once per key, and never into the array being walked
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key)) continue;   // the tail of a never-shrinking array (serialised as empty strings)
                if (ContentService.TryShareLive(key, out var h)) scope.KeepHandle(key, h);   // normal case: no I/O
                else scope.Load<UnityEngine.Object>(key);                                   // original already gone
            }
            scope.BindOwnerComponent(this);
            scope.ResyncKeys();
        }

        void OnDestroy()
        {
            var s = m_Scope;
            m_Scope = null;
            s?.DisposeCore(s);
        }

        /// <summary>Mirrors the scope's keys into the serialized array in place. The array never shrinks (its tail is null, or empty strings on a clone), so a release followed by a load allocates nothing; clearing stops at the first empty slot, so a key change costs the keys it removed, not the array's length.</summary>
        internal void SyncKeys(Dictionary<string, AssetHandle<UnityEngine.Object>>.KeyCollection keys)
        {
            int n = keys.Count;
            if (m_InheritKeys.Length < n) m_InheritKeys = new string[Math.Max(n, m_InheritKeys.Length * 2)];
            keys.CopyTo(m_InheritKeys, 0);
            for (int i = n; i < m_InheritKeys.Length && !string.IsNullOrEmpty(m_InheritKeys[i]); ++i) m_InheritKeys[i] = null;   // the keys sit at the front: past the first empty slot there is nothing to clear
        }

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
