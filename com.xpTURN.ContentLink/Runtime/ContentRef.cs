using System;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// GUID-based serialized reference (the field shape of Addressables' AssetReference): moving or renaming the asset
    /// does not break it. It holds the key only — no load state — so copies are harmless, and loading and releasing
    /// belong to a scope:
    ///
    ///     public ContentRef&lt;Sprite&gt; icon;
    ///     image.sprite = await this.Scope().LoadAsync(icon);   // this.Scope().Release(icon) returns it early
    ///
    /// The runtime turns the reference into the catalog name the build recorded for that GUID and loads by name, so a
    /// reference and a name load of the same asset share one slot and one scope key: releasing either releases both.
    /// For a lease per field, resolve the name once and use a ContentLink (<see cref="ContentService.TryGetName{T}"/>).
    ///
    /// <see cref="RuntimeKeyIsValid"/> means "a registered catalog (or the editor simulation) can load it", as for
    /// ContentLink — not Addressables' "a GUID is set", which is <see cref="IsSet"/>.
    /// </summary>
    [Serializable]
    public struct ContentRef<T> : IEquatable<ContentRef<T>> where T : Object
    {
        [FormerlySerializedAs("m_assetGUID")]   // Addressables carries the same attribute
        [SerializeField] string m_AssetGUID;    // same field names as Addressables' AssetReference, so its data carries over
        [SerializeField] string m_SubObjectName;

        public ContentRef(string assetGuid, string subObjectName = null)
        {
            m_AssetGUID = assetGuid;
            m_SubObjectName = subObjectName;
        }

        public string AssetGUID => m_AssetGUID ?? "";

        /// <summary>The sub-asset name — "sword" for the sprite catalogued as "Icons/sheet[sword]"; empty for the main asset.</summary>
        public string SubObjectName => m_SubObjectName ?? "";

        public bool IsSet => !string.IsNullOrEmpty(m_AssetGUID);

        public bool RuntimeKeyIsValid() => ContentService.Contains(this);

        public bool Equals(ContentRef<T> other)
            => string.Equals(AssetGUID, other.AssetGUID, StringComparison.Ordinal) && string.Equals(SubObjectName, other.SubObjectName, StringComparison.Ordinal);

        public override bool Equals(object obj) => obj is ContentRef<T> other && Equals(other);
        public override int GetHashCode() => RefKey.Hash(AssetGUID, SubObjectName);

        /// <summary>"ContentRef&lt;Sprite&gt;(guid[sub])". Allocates — diagnostics only.</summary>
        public override string ToString()
            => SubObjectName.Length == 0 ? $"ContentRef<{typeof(T).Name}>({AssetGUID})" : $"ContentRef<{typeof(T).Name}>({AssetGUID}[{SubObjectName}])";
    }

    /// <summary>
    /// GUID-based serialized reference to a scene in a content directory. Load it through a scope, which owns the scene
    /// like a name load does:
    ///
    ///     public ContentSceneRef level;
    ///     var scene = await this.Scope().LoadSceneAsync(level);
    /// </summary>
    [Serializable]
    public struct ContentSceneRef : IEquatable<ContentSceneRef>
    {
        [FormerlySerializedAs("m_assetGUID")]   // Addressables' scene references are the same AssetReference field shape
        [SerializeField] string m_AssetGUID;

        public ContentSceneRef(string assetGuid) { m_AssetGUID = assetGuid; }

        public string AssetGUID => m_AssetGUID ?? "";
        public bool IsSet => !string.IsNullOrEmpty(m_AssetGUID);

        public bool RuntimeKeyIsValid() => ContentService.ContainsScene(this);

        public bool Equals(ContentSceneRef other) => string.Equals(AssetGUID, other.AssetGUID, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is ContentSceneRef other && Equals(other);
        public override int GetHashCode() => AssetGUID.GetHashCode();

        /// <summary>"ContentSceneRef(guid)". Allocates — diagnostics only.</summary>
        public override string ToString() => $"ContentSceneRef({AssetGUID})";
    }

    /// <summary>(GUID or main name, sub-asset name), null read as "": the key of the runtime's reference index and link-name cache. Compares ordinally without allocating.</summary>
    readonly struct RefKey : IEquatable<RefKey>
    {
        public readonly string Guid;
        public readonly string Sub;

        public RefKey(string guid, string sub)
        {
            Guid = guid ?? "";
            Sub = sub ?? "";
        }

        public bool Equals(RefKey other) => string.Equals(Guid, other.Guid, StringComparison.Ordinal) && string.Equals(Sub, other.Sub, StringComparison.Ordinal);
        public override bool Equals(object obj) => obj is RefKey other && Equals(other);
        public override int GetHashCode() => Hash(Guid, Sub);

        internal static int Hash(string guid, string sub) => unchecked((guid.GetHashCode() * 397) ^ sub.GetHashCode());
    }
}
