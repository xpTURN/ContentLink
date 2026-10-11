using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Values carried over from Addressables' AssetReferenceSprite, as the inspector shows them.
    /// Addressables' drawer writes three shapes: a texture GUID + sprite name when a sprite is picked, a texture GUID and no
    /// sprite name when the texture is dropped, and an atlas GUID + sprite name when an Addressable atlas holds the sprite.
    /// The first and the third resolve — the third through the name the build gives every sprite an atlas under a content root
    /// packs; the second is shown with what to do, never rejected. The two fields carry over from the class to the
    /// struct because both serialize them under the same names.
    /// </summary>
    [Category("EditorUtility")]
    public class ContentRefMigrationTests
    {
        const string k_RootA = "Assets/TestContent/A";
        const string k_Generated = "Assets/TestContent/Generated";
        const string k_TempFolder = "Assets/TestContent/A/SimTemp";   // gitignored, like the simulation fixtures: the atlas must lie under the root

        string m_SourceRoot, m_CatalogPath, m_TempCatalog, m_TempAtlas;

        [SetUp]
        public void SaveStatics()
        {
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            m_TempCatalog = $"{k_Generated}/MigrationTest_{System.Guid.NewGuid():N}.asset";
            m_TempAtlas = $"{k_TempFolder}/MigrationAtlas_{System.Guid.NewGuid():N}.spriteatlasv2";
        }

        [TearDown]
        public void RestoreStatics()
        {
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            AssetDatabase.DeleteAsset(m_TempCatalog);
            AssetDatabase.DeleteAsset(m_TempAtlas);
            ContentLinkEditorUtility.Invalidate();
        }

        [Test]
        public void M1_TheThreeShapesAddressablesWrites()
        {
            // An atlas that packs the texture, made before the catalog so the catalog names its sprite too
            if (!AssetDatabase.IsValidFolder(k_TempFolder)) AssetDatabase.CreateFolder(k_RootA, "SimTemp");
            var packed = new SpriteAtlasAsset();
            packed.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_RootA}/Icons/single.png") });
            SpriteAtlasAsset.Save(packed, m_TempAtlas);
            AssetDatabase.ImportAsset(m_TempAtlas, ImportAssetOptions.ForceSynchronousImport);
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(m_TempAtlas), Is.EqualTo(typeof(SpriteAtlas)), "the fixture did not import as a sprite atlas");

            ContentDirectoryBuilder.SourceRoot = k_RootA;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
            ContentLinkEditorUtility.Invalidate();
            var textureGuid = AssetDatabase.AssetPathToGUID($"{k_RootA}/Icons/single.png");

            // 1 — a sprite picked: the texture's GUID + the sprite name. Resolves.
            Assert.That(ContentLinkEditorUtility.ValidateRef(textureGuid, "single", typeof(Sprite), false, out var tip), Is.EqualTo(NameStatus.Ok), tip);
            Assert.That(ContentLinkEditorUtility.NameForRef(textureGuid, "single"), Is.EqualTo("Icons/single[single]"));

            // 2 — the texture dropped: its GUID and no sprite name. Red, and says to pick the sprite.
            Assert.That(ContentLinkEditorUtility.ValidateRef(textureGuid, "", typeof(Sprite), false, out tip), Is.EqualTo(NameStatus.TypeMismatch));
            Assert.That(tip, Does.Contain("pick the sprite itself"));

            // 3 — an Addressable atlas held the sprite: the atlas GUID + the sprite name. Resolves through the atlas's name for it.
            var atlasGuid = AssetDatabase.AssetPathToGUID(m_TempAtlas);
            Assert.That(ContentLinkEditorUtility.ValidateRef(atlasGuid, "single", typeof(Sprite), false, out tip), Is.EqualTo(NameStatus.Ok), tip);
            Assert.That(ContentLinkEditorUtility.NameForRef(atlasGuid, "single"), Is.EqualTo($"SimTemp/{Path.GetFileNameWithoutExtension(m_TempAtlas)}[single]"));
            Assert.That(ContentLinkEditorUtility.ResolveRefAsset(atlasGuid, "single"), Is.SameAs(ContentLinkEditorUtility.ResolveRefAsset(textureGuid, "single")),
                "the atlas's name loads the texture's own sprite");
            Assert.That(ContentLinkEditorUtility.ValidateRef(atlasGuid, "nope", typeof(Sprite), false, out tip), Is.EqualTo(NameStatus.Missing));
            Assert.That(tip, Does.Contain("packs no sprite named"), "a sprite name the atlas does not pack is a renamed or removed sprite");
        }
    }
}
