using System;
using System.Threading;
using UnityEngine;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// Name-based serialized reference (AssetLink style) whose lifetime is always owned by a scope.
    ///
    ///     public ContentLinkSprite icon;
    ///     image.sprite = await icon.LoadAsync(this);   // owner = this GameObject
    ///
    /// Release() is optional. Call it to return the asset early (list cells, equipment swap);
    /// otherwise it is returned when the owner scope ends. Correctness never depends on Release().
    /// One ContentLink holds at most one reference, like Loadable&lt;T&gt;.
    /// </summary>
    [Serializable]
    public class ContentLink<T> where T : Object
    {
        [SerializeField] string m_Name = "";

        [NonSerialized] ContentScope m_Scope;
        [NonSerialized] int m_Lease;
        [NonSerialized] uint m_Gen;
        [NonSerialized] bool m_Loading;
        [NonSerialized] int m_LoadVersion;

        public ContentLink() { }
        public ContentLink(string name) { m_Name = name; }

        public string Name => m_Name;
        public bool IsLoading => m_Loading;
        public bool IsValid => ContentService.IsLeaseAlive(m_Lease, m_Gen);
        public T Asset => ContentService.GetAsset<T>(m_Lease, m_Gen);
        public ContentScope Scope => IsValid ? m_Scope : null;

        public bool RuntimeKeyIsValid() => ContentService.Contains(m_Name);

        /// <summary>Data-driven remapping (monster/item tables, packet values). Release first if loaded.</summary>
        public void SetName(string name)
        {
            if (m_Loading || IsValid)
            {
                Debug.LogError($"[ContentLink] Release '{m_Name}' before changing the name.");
                return;
            }
            m_Name = name;
        }

        public Awaitable<T> LoadAsync(Component owner, CancellationToken ct = default) => LoadAsync(ScopeOf(owner), ct);
        public Awaitable<T> LoadAsync(GameObject owner, CancellationToken ct = default) => LoadAsync(ScopeOf(owner), ct);

        static ContentScope ScopeOf(Component owner) => owner != null ? owner.Scope() : throw new ArgumentNullException(nameof(owner));
        static ContentScope ScopeOf(GameObject owner) => owner != null ? owner.Scope() : throw new ArgumentNullException(nameof(owner));

        public async Awaitable<T> LoadAsync(ContentScope scope, CancellationToken ct = default)
        {
            if (!BeginLoad(scope)) return Asset;
            int version = m_LoadVersion;

            AssetHandle<T> h;
            var linked = ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, scope.DisposedToken) : null;   // a default token needs no link: the scope's own token cancels the load
            try { h = await ContentService.AcquireAsync<T>(m_Name, linked != null ? linked.Token : scope.DisposedToken); }
            finally { linked?.Dispose(); if (version == m_LoadVersion) m_Loading = false; }

            if (version != m_LoadVersion) { h.Dispose(); return null; }
            return Commit(scope, h);
        }

        public T Load(Component owner) => Load(ScopeOf(owner));

        public T Load(ContentScope scope)
        {
            if (!BeginLoad(scope)) return Asset;
            var h = ContentService.Acquire<T>(m_Name);
            m_Loading = false;
            return Commit(scope, h);
        }

        /// <summary>Optional early return. Safe to call at any time, including while loading or after the scope ended.</summary>
        public void Release()
        {
            if (m_Loading) { m_Loading = false; m_LoadVersion++; }
            if (IsValid)
            {
                m_Scope?.DetachLease(m_Lease, m_Gen);
                ContentService.Release(m_Lease, m_Gen);
            }
            m_Scope = null;
            m_Lease = 0;
            m_Gen = 0;
        }

        bool BeginLoad(ContentScope scope)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope), "ContentLink needs an owner scope.");
            if (scope.IsDisposed) throw new ObjectDisposedException($"ContentScope '{scope.Path}'");
            if (m_Loading || IsValid)
            {
                Debug.LogError($"[ContentLink] '{m_Name}' is already loaded. Call Release() first.");
                return false;
            }
            m_Loading = true;
            return true;
        }

        T Commit(ContentScope scope, AssetHandle<T> h)
        {
            if (!h.IsValid) return null;
            if (scope.IsDisposed)
            {
                h.Dispose();
                return null;
            }
            if (h.Asset == null)
            {
                ContentService.LogTypeMismatch<T>(h.Lease, h.Gen);
                h.Dispose();
                return null;
            }
            m_Scope = scope;
            m_Lease = h.Lease;
            m_Gen = h.Gen;
            scope.AttachLease(h.Lease, h.Gen);
            return h.Asset;
        }

        public override string ToString() => $"ContentLink<{typeof(T).Name}>({m_Name})";
    }

    // Non-generic types for inspectors / property drawers (AssetLinkGameObject equivalents)
    [Serializable] public sealed class ContentLinkGameObject : ContentLink<GameObject> { }
    [Serializable] public sealed class ContentLinkSprite : ContentLink<Sprite> { }
    [Serializable] public sealed class ContentLinkTexture2D : ContentLink<Texture2D> { }
}
