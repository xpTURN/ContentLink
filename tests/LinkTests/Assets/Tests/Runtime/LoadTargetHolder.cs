using Unity.Loading;
using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>Content that holds a catalog, an asset's id, a scene's id or a reference — what a build carries for each (LoadTargetCheckTests).</summary>
    public sealed class LoadTargetHolder : ScriptableObject
    {
        public ContentCatalog catalog;
        public LoadableObjectId id;
        public LoadableSceneId sceneId;
        public ContentRef<Texture2D> reference;
    }
}
