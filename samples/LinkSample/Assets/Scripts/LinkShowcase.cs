using System;
using UnityEngine;

namespace xpTURN.ContentLink.Samples
{
    /// <summary>
    /// ContentLink fields: catalog names set in the inspector (Prefabs/LinkShowcase.prefab). On Start each link loads into
    /// this GameObject's scope — the owner — and everything goes back when the showcase is destroyed. iconB names its
    /// sprite through the sprite atlas that packs it (Atlas/SampleAtlas.spriteatlasv2[piece_2_2]); badge is a res: name,
    /// loaded from a Resources folder of the player into the same scope.
    /// </summary>
    public sealed class LinkShowcase : MonoBehaviour
    {
        public ContentLinkSprite iconA = new ContentLinkSprite();
        public ContentLinkSprite iconB = new ContentLinkSprite();
        public ContentLink<AudioClip> clip = new ContentLink<AudioClip>();
        public ContentLink<Material> material = new ContentLink<Material>();
        public ContentLinkSprite badge = new ContentLinkSprite();

        async void Start()
        {
            try
            {
                var a = await iconA.LoadAsync(this);
                var b = await iconB.LoadAsync(this);
                var c = await clip.LoadAsync(this);
                var m = await material.LoadAsync(this);
                var r = await badge.LoadAsync(this);
                ShowcaseView.Show(transform, "ContentLink", a, b, c, m);
                ShowcaseView.ShowBadge(transform, "ContentLink", r);
            }
            catch (OperationCanceledException) { }   // destroyed while loading: the scope ended and returned what it held
        }
    }
}
