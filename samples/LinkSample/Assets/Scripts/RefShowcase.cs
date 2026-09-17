using System;
using UnityEngine;

namespace xpTURN.ContentLink.Samples
{
    /// <summary>
    /// ContentRef fields: asset GUIDs (and sprite names) set in the inspector (Prefabs/RefShowcase), so moving or renaming
    /// the files does not break them. A reference holds a key only; on Start each loads into this GameObject's scope.
    /// LinkShowcase loads the same assets by name, so the two share their slots — the live asset count does not grow.
    /// </summary>
    public sealed class RefShowcase : MonoBehaviour
    {
        public ContentRef<Sprite> iconA;
        public ContentRef<Sprite> iconB;
        public ContentRef<AudioClip> clip;
        public ContentRef<Material> material;

        async void Start()
        {
            var scope = this.Scope();
            try
            {
                var a = await scope.LoadAsync(iconA);
                var b = await scope.LoadAsync(iconB);
                var c = await scope.LoadAsync(clip);
                var m = await scope.LoadAsync(material);
                ShowcaseView.Show(transform, "ContentRef", a, b, c, m);
            }
            catch (OperationCanceledException) { }   // destroyed while loading: the scope ended and returned what it held
        }
    }
}
