using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>A serialized reference to a texture, as a renderer's would be — what Object.Instantiate copies into a clone.</summary>
    public sealed class TextureHolder : MonoBehaviour
    {
        public Texture2D texture;
    }
}
