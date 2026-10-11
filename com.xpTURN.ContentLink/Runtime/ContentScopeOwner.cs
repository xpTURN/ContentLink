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
    /// - Clones made with Object.Instantiate inherit the scope's keys (serialized below, res: names alike) and take their own references
    ///   when their scope opens — in Awake, or at the first Scope() call if that comes first — so a clone keeps its textures alive even
    ///   if the original is destroyed first. Awake is deferred for clones created inactive: touch Scope() to take the references at once.
    ///   An asset that unloads before then is loaded again as a new object; what the clone copied from the original still points at the old one.
    ///   A touched clone that never wakes gets no OnDestroy: dispose its scope before destroying it.
    /// - An explicit Dispose of the scope empties the key list: clones made afterwards inherit nothing, and the next Scope() starts empty.
    /// - While the object is destroyed (its OnDestroy, a sibling's, a load's cancellation) Scope() hands back the ended scope, never a new one —
    ///   an ended one too for an object that never had a scope (the engine adds no component to an object being destroyed).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("")]
    public sealed class ContentScopeOwner : MonoBehaviour
    {
        [SerializeField, HideInInspector] string[] m_InheritKeys = Array.Empty<string>();

        [NonSerialized] ContentScope m_Scope;
        [NonSerialized] bool m_Ended;   // OnDestroy has run: the scope stays ended

        public ContentScope Scope
        {
            get
            {
                if (m_Scope == null || m_Scope.IsDisposed)
                {
                    if (m_Ended) return m_Scope ??= Ended(gameObject);   // the object is going: nothing would dispose a new scope
                    if (m_Scope != null) ClearKeys();          // ended outside OnDestroy before it could tell us (its cancellation runs first): what it held is gone
                    Open();                                    // a clone touched before its Awake takes its references here
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
            // Runs on clones: take our own references to what the original held — unless Scope() already opened the scope and took them.
            if (m_Scope != null || !HasKeys()) return;
            Open();
        }

        bool HasKeys() => m_InheritKeys != null && m_InheritKeys.Length > 0 && !string.IsNullOrEmpty(m_InheritKeys[0]);   // the keys sit at the front of a never-shrinking array: an empty first slot means it holds none

        /// <summary>Opens the owner's scope; a clone's copy of the original's keys becomes its own references.</summary>
        void Open()
        {
            var scope = m_Scope = ContentScope.CreateForGameObject(gameObject);   // set first: a Scope() asked from inside a load below gets this scope
            if (!HasKeys()) { scope.BindOwnerComponent(this); return; }   // nothing to inherit: no key sync
            foreach (var key in m_InheritKeys)   // bound after the loop: the keys sync once, not once per key, and never into the array being walked
            {
                if (string.IsNullOrEmpty(key)) continue;   // the tail of a never-shrinking array (serialised as empty strings)
                if (ContentService.TryShareLive(key, out var h)) scope.KeepHandle(key, h);   // normal case: no I/O
                else scope.Load<UnityEngine.Object>(key);                                   // original already gone: a new object
            }
            scope.BindOwnerComponent(this);
            scope.ResyncKeys();
        }

        /// <summary>The scope handed back while an object is destroyed when it has none: ended at once.</summary>
        internal static ContentScope Ended(GameObject go)
        {
            var scope = ContentScope.CreateForGameObject(go);
            scope.DisposeCore(scope);
            return scope;
        }

        void OnDestroy()
        {
            m_Ended = true;   // from here Scope() hands back this ended scope: a load's cancellation or a sibling's OnDestroy runs while the engine destroys us
            m_Scope?.DisposeCore(m_Scope);
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
            if (m_Scope != scope || m_Ended) return;   // OnDestroy keeps the ended scope (Scope() hands it back)
            m_Scope = null;
            ClearKeys();   // what the scope held is gone: clones made afterwards inherit nothing, nor does the next scope
        }

        void ClearKeys() { for (int i = 0; i < m_InheritKeys.Length && !string.IsNullOrEmpty(m_InheritKeys[i]); ++i) m_InheritKeys[i] = null; }   // the keys sit at the front: past the first empty slot there is nothing to clear
    }

    public static class ContentScopeExtensions
    {
        /// <summary>The scope that lives as long as this GameObject.</summary>
        public static ContentScope Scope(this GameObject go)
            => ContentScopeOwner.Get(go) is { } owner ? owner.Scope : ContentScopeOwner.Ended(go);   // an object being destroyed adds no component (a plain null): an ended scope, as for one with an owner

        /// <summary>The scope that lives as long as this component's GameObject.</summary>
        public static ContentScope Scope(this Component component) => component.gameObject.Scope();
    }
}
