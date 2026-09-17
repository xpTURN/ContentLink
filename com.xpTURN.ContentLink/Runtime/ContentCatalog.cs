using System;
using System.Collections.Generic;
using Unity.Loading;
using UnityEngine;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// Root asset of a content directory. Maps a name (address) to a LoadableObjectId, and a scene name to a LoadableSceneId.
    /// Both are tracked references, so the referenced asset or scene and its dependencies are
    /// pulled into the build, but nothing is loaded when the directory is registered.
    /// </summary>
    [CreateAssetMenu(menuName = "xpTURN/ContentLink/Content Catalog", fileName = "ContentCatalog")]
    public sealed class ContentCatalog : ScriptableObject
    {
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

        public IReadOnlyList<Entry> Entries => m_Entries;
        public IReadOnlyList<SceneEntry> Scenes => m_Scenes;

        /// <summary>
        /// The authoring folder this catalog was built from, e.g. "Assets/Content". Empty on a catalog
        /// built before this field existed — running Update Content Catalog once fills it in.
        /// Readable at runtime on purpose: the editor simulation decides what it may resolve by asking the
        /// registered directories for the roots their catalogs carry, and those are the built copies.
        /// </summary>
        public string SourceRoot => m_SourceRoot;

#if UNITY_EDITOR
        public void EditorSetEntries(Entry[] entries) => m_Entries = entries ?? Array.Empty<Entry>();
        public void EditorSetScenes(SceneEntry[] scenes) => m_Scenes = scenes ?? Array.Empty<SceneEntry>();
        public void EditorSetSourceRoot(string sourceRoot) => m_SourceRoot = sourceRoot ?? string.Empty;
#endif
    }
}
