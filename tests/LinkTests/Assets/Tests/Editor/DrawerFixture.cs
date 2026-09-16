using System.Collections.Generic;
using UnityEngine;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Every field shape the drawer has to cover: a derived link, a closed generic one, collection
    /// elements (where fieldInfo is the collection, not the element), a scene link, and a non-link control.
    /// Lives in the Editor test assembly because it does not reference the runtime test assembly (LinkHolder).
    /// </summary>
    class DrawerFixture : ScriptableObject
    {
        public ContentLinkSprite one = new ContentLinkSprite();
        public ContentLinkSprite[] many = { new ContentLinkSprite(), new ContentLinkSprite() };
        public List<ContentLinkGameObject> list = new List<ContentLinkGameObject> { new ContentLinkGameObject() };
        public ContentLink<Texture2D> direct = new ContentLink<Texture2D>();
        public ContentSceneLink scene = new ContentSceneLink();
        public int notALink;
    }
}
