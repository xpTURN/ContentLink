using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Prefab component with inspector-assigned links (TestContent/A/Prefabs/Linked):
    /// a ContentLinkSprite and a ContentSceneLink (its scene name is resolved against directory C at runtime),
    /// and the same two as GUID references.
    /// </summary>
    public sealed class LinkHolder : MonoBehaviour
    {
        public ContentLinkSprite icon = new ContentLinkSprite();
        public ContentSceneLink level = new ContentSceneLink();
        public ContentRef<Sprite> iconRef;
        public ContentSceneRef levelRef;
    }
}
