using System;
using UnityEditor;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>Same two lines as <see cref="ContentLinkDrawer"/>, against the catalog's scene entries.</summary>
    [CustomPropertyDrawer(typeof(ContentSceneLink))]   // sealed, so useForChildren is not needed
    public sealed class ContentSceneLinkDrawer : LinkDrawerBase
    {
        protected override bool IsScene => true;
        protected override Type AssetTypeOf(SerializedProperty property) => null;
        protected override Type ObjectFieldType(Type assetType) => typeof(SceneAsset);
    }
}
