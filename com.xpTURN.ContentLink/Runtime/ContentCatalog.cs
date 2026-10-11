using System;
using System.Collections.Generic;
using Unity.Loading;
using UnityEngine;

namespace xpTURN.ContentLink
{
    /// <summary>How a catalog's names are made from the paths under its source root.</summary>
    public enum ContentNameStyle
    {
        PathWithoutExtension = 0,   // "Icons/sword" — the default, and what a catalog without the field reads as
        PathWithExtension = 1,      // "Icons/sword.png"
    }

    /// <summary>
    /// Root asset of a content directory. Maps a name (address) to a LoadableObjectId, and a scene name to a LoadableSceneId.
    /// Both are tracked references, so the referenced asset or scene and its dependencies are
    /// pulled into the build, but nothing is loaded when the directory is registered.
    /// </summary>
    [CreateAssetMenu(menuName = "xpTURN/ContentLink/Content Catalog", fileName = "ContentCatalog")]
    public sealed class ContentCatalog : ScriptableObject
    {
        /// <summary>
        /// The serialized layout of this class and its entries. Bump it whenever a serialized field of ContentCatalog, Entry or
        /// SceneEntry changes: a directory built without type trees under another layout crashes the player that registers it,
        /// so the packed index records the layout and the registration refuses a mismatch.
        /// </summary>
        public const int Layout = 1;

        [Serializable]
        public struct Entry
        {
            public string name;           // e.g. "Prefabs/Cat", "Sprites/icons[sword]"
            public LoadableObjectId id;
            public string guid;           // the asset's GUID (32 hex chars), what a ContentRef resolves against
            public string sub;            // the sub-asset name of a "name[sub]" entry; "" for a main asset
        }

        [Serializable]
        public struct SceneEntry
        {
            public string name;           // e.g. "Scenes/Arena"
            public LoadableSceneId id;
            public string guid;           // the scene asset's GUID, what a ContentSceneRef resolves against
        }

        [SerializeField] Entry[] m_Entries = Array.Empty<Entry>();
        [SerializeField] SceneEntry[] m_Scenes = Array.Empty<SceneEntry>();
        [SerializeField] string m_SourceRoot = string.Empty;
        [SerializeField] ContentNameStyle m_NameStyle;

        public IReadOnlyList<Entry> Entries => m_Entries;
        public IReadOnlyList<SceneEntry> Scenes => m_Scenes;

        /// <summary>
        /// The authoring folder this catalog was built from, e.g. "Assets/Content". Empty on a catalog
        /// built before this field existed — running Update Content Catalog once fills it in.
        /// Readable at runtime on purpose: the editor simulation decides what it may resolve by asking the
        /// registered directories for the roots their catalogs carry, and those are the built copies.
        /// </summary>
        public string SourceRoot => m_SourceRoot;

        /// <summary>The rule the names were made with. Readable at runtime, like <see cref="SourceRoot"/>.</summary>
        public ContentNameStyle NameStyle => m_NameStyle;

#if UNITY_EDITOR
        public void EditorSetEntries(Entry[] entries) => m_Entries = entries ?? Array.Empty<Entry>();
        public void EditorSetScenes(SceneEntry[] scenes) => m_Scenes = scenes ?? Array.Empty<SceneEntry>();
        public void EditorSetSourceRoot(string sourceRoot) => m_SourceRoot = sourceRoot ?? string.Empty;
        public void EditorSetNameStyle(ContentNameStyle style) => m_NameStyle = style;
#endif
    }
}
