using UnityEngine;
using UnityEngine.U2D;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>An asset that holds a sprite atlas itself, as content that gives sprites at run time would (AtlasSpriteEditorTests.AS25).</summary>
    class AtlasHolderFixture : ScriptableObject
    {
        public SpriteAtlas atlas;
    }
}
