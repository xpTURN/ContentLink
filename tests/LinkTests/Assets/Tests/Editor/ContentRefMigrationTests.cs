using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.U2D;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Values carried over from Addressables' AssetReferenceSprite, as the inspector shows them.
    /// Addressables' drawer writes three shapes: a texture GUID + sprite name when a sprite is picked, a texture GUID and no
    /// sprite name when the texture is dropped, and an atlas GUID + sprite name when an Addressable atlas holds the sprite.
    /// Only the first resolves; the other two are shown with what to do, never rejected. The two fields carry over
    /// from the class to the struct because both serialize them under the same names.
    /// </summary>
    [Category("EditorUtility")]
    public class ContentRefMigrationTests
    {
        const string k_RootA = "Assets/TestContent/A";
        const string k_Generated = "Assets/TestContent/Generated";

        // An empty .spriteatlasv2 — the smallest file the SpriteAtlas importer accepts.
        const string k_EmptyAtlas =
            "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!612988286 &1\nSpriteAtlasAsset:\n  m_ObjectHideFlags: 0\n" +
            "  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_Name: \n" +
            "  serializedVersion: 2\n  m_MasterAtlas: {fileID: 0}\n  m_ImporterData:\n    packables: []\n  m_IsVariant: 0\n" +
            "  m_ScriptablePacker: {fileID: 0}\n";

        string m_SourceRoot, m_CatalogPath, m_TempCatalog, m_TempAtlas;

        [SetUp]
        public void SaveStatics()
        {
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            m_TempCatalog = $"{k_Generated}/MigrationTest_{System.Guid.NewGuid():N}.asset";
            m_TempAtlas = $"{k_Generated}/MigrationTest_{System.Guid.NewGuid():N}.spriteatlasv2";
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

            // 3 — an Addressable atlas held the sprite: the atlas GUID + the sprite name. Amber: unsupported.
            File.WriteAllText(m_TempAtlas, k_EmptyAtlas);
            AssetDatabase.ImportAsset(m_TempAtlas);
            Assert.That(AssetDatabase.GetMainAssetTypeAtPath(m_TempAtlas), Is.EqualTo(typeof(SpriteAtlas)), "the fixture did not import as a sprite atlas");
            var atlasGuid = AssetDatabase.AssetPathToGUID(m_TempAtlas);
            Assert.That(ContentLinkEditorUtility.ValidateRef(atlasGuid, "single", typeof(Sprite), false, out tip), Is.EqualTo(NameStatus.NotInCatalog));
            Assert.That(tip, Does.Contain("sprite atlas"), "the atlas answer comes before 'no sub-asset of that name' — the atlas has none either");
        }
    }
}
