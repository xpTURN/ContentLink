using UnityEngine;

namespace xpTURN.ContentLink.Samples
{
    /// <summary>
    /// What a showcase draws once its fields are loaded: two sprites side by side, a quad with the material under them,
    /// and an audio source playing the clip (LinkShowcase adds a badge above the sprites). A field that did not load is
    /// simply missing from the picture, and the log line says which one.
    /// </summary>
    static class ShowcaseView
    {
        public static void Show(Transform root, string title, Sprite iconA, Sprite iconB, AudioClip clip, Material material)
        {
            AddSprite(root, "IconA", iconA, new Vector3(-1.7f, 2f, 0f));
            AddSprite(root, "IconB", iconB, new Vector3(1.7f, 2f, 0f));

            if (material != null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "Material";
                quad.transform.SetParent(root, false);
                quad.transform.localPosition = new Vector3(0f, -2.5f, 0f);
                quad.transform.localScale = new Vector3(3f, 3f, 1f);
                quad.GetComponent<MeshRenderer>().sharedMaterial = material;
            }

            if (clip != null)
            {
                var source = root.gameObject.AddComponent<AudioSource>();
                source.clip = clip;
                source.Play();
            }

            Debug.Log($"[LinkSample] {title}: iconA={NameOf(iconA)} iconB={NameOf(iconB)} clip={NameOf(clip)} material={NameOf(material)} · live assets {ContentService.LiveAssetCount}");
        }

        public static void ShowBadge(Transform root, string title, Sprite badge)
        {
            AddSprite(root, "Badge", badge, new Vector3(0f, 4.9f, 0f));
            Debug.Log($"[LinkSample] {title}: badge={NameOf(badge)} (res: — a Resources folder)");
        }

        static void AddSprite(Transform root, string name, Sprite sprite, Vector3 position)
        {
            if (sprite == null) return;
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.localPosition = position;
            go.AddComponent<SpriteRenderer>().sprite = sprite;
        }

        static string NameOf(Object asset) => asset != null ? asset.name : "<not loaded>";
    }
}
