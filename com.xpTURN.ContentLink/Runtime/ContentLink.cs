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
    ///
    /// A sprite sub-asset is the main name plus the sprite's name, kept apart like ContentRef's m_SubObjectName:
    /// ("Icons/sheet", "s0") loads the catalog name "Icons/sheet[s0]". With no sub-object name, Name is the catalog name
    /// as it is, so a full "Icons/sheet[s0]" still loads.
    /// A name that starts with "res:" loads from a Resources folder of the player: "res:UI/logo", ("res:UI/sheet", "s0").
    /// </summary>
    [Serializable]
    public class ContentLink<T> where T : Object
    {
        [SerializeField] string m_Name = "";
        [SerializeField] string m_SubObjectName = "";

        [NonSerialized] ContentScope m_Scope;
        [NonSerialized] int m_Lease;
        [NonSerialized] uint m_Gen;
        [NonSerialized] bool m_Loading;
        [NonSerialized] int m_LoadVersion;

        public ContentLink() { }
        public ContentLink(string name, string subObjectName = null) { m_Name = name; m_SubObjectName = subObjectName ?? ""; }

        /// <summary>The main name — "Icons/sheet" for the sprite ("Icons/sheet", "s0"). Copy a link with SetName(Name, SubObjectName).</summary>
        public string Name => m_Name;

        /// <summary>The sprite's name inside the main asset; empty for the main asset itself.</summary>
        public string SubObjectName => m_SubObjectName ?? "";

        public bool IsLoading => m_Loading;
        public bool IsValid => ContentService.IsLeaseAlive(m_Lease, m_Gen);
        public T Asset => ContentService.GetAsset<T>(m_Lease, m_Gen);
        public ContentScope Scope => IsValid ? m_Scope : null;

        public bool RuntimeKeyIsValid() => ContentService.Contains(m_Name, m_SubObjectName);

        /// <summary>
        /// Data-driven remapping (monster/item tables, packet values). Release first if loaded.
        /// Omitting <paramref name="subObjectName"/> clears it, so a full "main[sub]" name never picks up the previous sprite name.
        /// </summary>
        public void SetName(string name, string subObjectName = null)
        {
            if (m_Loading || IsValid)
            {
                Debug.LogError($"[ContentLink] Release '{KeyText()}' before changing the name.");
                return;
            }
            m_Name = name;
            m_SubObjectName = subObjectName ?? "";
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
            try { h = await ContentService.AcquireAsync<T>(ContentService.LinkKey(m_Name, m_SubObjectName), ct, scope.DisposedToken); }   // the caller's token and the scope's, each registered directly: no linked source per load
            finally { if (version == m_LoadVersion) m_Loading = false; }

            if (version != m_LoadVersion) { h.Dispose(); return null; }
            return Commit(scope, h);
        }

        public T Load(Component owner) => Load(ScopeOf(owner));

        public T Load(ContentScope scope)
        {
            if (!BeginLoad(scope)) return Asset;
            var h = ContentService.Acquire<T>(ContentService.LinkKey(m_Name, m_SubObjectName));
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
                Debug.LogError($"[ContentLink] '{KeyText()}' is already loaded. Call Release() first.");
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

        /// <summary>"Icons/sheet[s0]", or the name alone. Allocates — error messages only.</summary>
        string KeyText() => SubObjectName.Length == 0 ? m_Name : $"{m_Name}[{SubObjectName}]";

        public override string ToString() => $"ContentLink<{typeof(T).Name}>({KeyText()})";
    }

    // Non-generic types for inspectors / property drawers (AssetLinkGameObject equivalents)
    [Serializable] public sealed class ContentLinkGameObject : ContentLink<GameObject> { }
    [Serializable] public sealed class ContentLinkSprite : ContentLink<Sprite> { }
    [Serializable] public sealed class ContentLinkTexture2D : ContentLink<Texture2D> { }
}
