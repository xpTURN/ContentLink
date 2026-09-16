using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Prefab component with inspector-assigned links (TestContent/A/Prefabs/Linked):
    /// a ContentLinkSprite and a ContentSceneLink (its scene name is resolved against directory C at runtime).
    /// </summary>
    public sealed class LinkHolder : MonoBehaviour
    {
        public ContentLinkSprite icon = new ContentLinkSprite();
        public ContentSceneLink level = new ContentSceneLink();
    }
}
