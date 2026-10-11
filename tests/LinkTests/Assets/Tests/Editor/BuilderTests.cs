using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Loading;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>ContentDirectoryBuilder catalog rules (EditMode). Only UpdateCatalog() is exercised; no content build runs.</summary>
    [Category("Catalog")]
    public class BuilderTests
    {
        static readonly string[] k_NamesA =
        {
            "Audio/tone", "Icons/sheet", "Icons/sheet[s0]", "Icons/sheet[s1]", "Icons/single", "Icons/single[single]",
            "Materials/Plain", "Prefabs/Linked", "Prefabs/Plain",
        };

        string m_SourceRoot, m_CatalogPath, m_OutputPath, m_TempCatalog;

        [SetUp]
        public void SaveStatics()
        {
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            m_OutputPath = ContentDirectoryBuilder.OutputPath;
            if (!AssetDatabase.IsValidFolder(TestContentBuild.GeneratedFolder)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            m_TempCatalog = $"{TestContentBuild.GeneratedFolder}/BuilderTest_{System.Guid.NewGuid():N}.asset";
        }

        [TearDown]
        public void RestoreStatics()
        {
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            ContentDirectoryBuilder.OutputPath = m_OutputPath;
            AssetDatabase.DeleteAsset(m_TempCatalog);
        }

        ContentCatalog Update(string sourceRoot, bool expectOk = true)
        {
            ContentDirectoryBuilder.SourceRoot = sourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.EqualTo(expectOk), $"UpdateCatalog for '{sourceRoot}'");
            var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(m_TempCatalog);
            Assert.That(catalog, Is.Not.Null, "catalog asset was not created");
            return catalog;
        }

        static ContentCatalog.Entry Find(ContentCatalog catalog, string name)
        {
            var hits = catalog.Entries.Where(e => e.name == name).ToList();
            Assert.That(hits, Has.Count.EqualTo(1), $"entry '{name}'");
            return hits[0];
        }

        static Object Resolve(ContentCatalog.Entry e) => LoadableObjectIdEditorUtility.LoadableObjectIdToObject(e.id);

        // ───────────── path name = main asset, [sub] = sprite ─────────────

        [Test]
        public void B1_PathNameIsMainTexture_AndSubNameIsSprite()
        {
            var catalog = Update("Assets/TestContent/A");
            Assert.That(catalog.Entries.Select(e => e.name), Is.EquivalentTo(k_NamesA));

            var tex = Find(catalog, "Icons/single");
            var sprite = Find(catalog, "Icons/single[single]");
            Assert.That(Resolve(tex), Is.TypeOf<Texture2D>(), "Icons/single");
            Assert.That(Resolve(sprite), Is.TypeOf<Sprite>(), "Icons/single[single]");

            Assert.That(LoadableObjectIdEditorUtility.TryDeconstructLoadableObjectId(tex.id, out var texGuid, out var texFileId, out _), Is.True);
            Assert.That(LoadableObjectIdEditorUtility.TryDeconstructLoadableObjectId(sprite.id, out var spriteGuid, out var spriteFileId, out _), Is.True);
            Assert.That(spriteGuid, Is.EqualTo(texGuid), "sprite and texture come from the same asset file");
            Assert.That(spriteFileId, Is.Not.EqualTo(texFileId), "sprite is a distinct sub-object");

            Assert.That(Resolve(Find(catalog, "Prefabs/Plain")), Is.TypeOf<GameObject>());
            Assert.That(Resolve(Find(catalog, "Materials/Plain")), Is.TypeOf<Material>());
            Assert.That(Resolve(Find(catalog, "Audio/tone")), Is.TypeOf<AudioClip>());
        }

        // ───────────── multiple-sprite sheet ─────────────

        [Test]
        public void B2_MultipleSpriteSheet_HasOneEntryPerSprite()
        {
            var catalog = Update("Assets/TestContent/A");
            var s0 = Resolve(Find(catalog, "Icons/sheet[s0]")) as Sprite;
            var s1 = Resolve(Find(catalog, "Icons/sheet[s1]")) as Sprite;
            Assert.That(s0, Is.Not.Null, "s0 is a Sprite");
            Assert.That(s1, Is.Not.Null, "s1 is a Sprite");
            Assert.That(s0.name, Is.EqualTo("s0"));
            Assert.That(s1.name, Is.EqualTo("s1"));
            Assert.That(ReferenceEquals(s0, s1), Is.False);
            Assert.That(Resolve(Find(catalog, "Icons/sheet")), Is.TypeOf<Texture2D>());
        }

        // ───────────── duplicate name → refused, logged once, catalog left empty ─────────────

        [Test]
        public void B3_DuplicateName_RefusesAndLogsOnce_LeavingCatalogEmpty()
        {
            LogAssert.Expect(LogType.Error, new Regex(@"\[ContentLink\] duplicate name 'x'"));
            var catalog = Update("Assets/TestContent/Dup", expectOk: false);
            Assert.That(catalog.Entries, Is.Empty, "entries must not be written on failure");
        }

        // ───────────── deterministic output ─────────────

        [Test]
        public void B4_UpdateTwice_IsSortedOrdinal_AndIdentical()
        {
            var first = Update("Assets/TestContent/A").Entries.Select(e => (e.name, e.id.ToString())).ToList();
            var second = Update("Assets/TestContent/A").Entries.Select(e => (e.name, e.id.ToString())).ToList();
            Assert.That(second, Is.EqualTo(first));
            var names = first.Select(t => t.name).ToList();
            Assert.That(names, Is.EqualTo(names.OrderBy(n => n, System.StringComparer.Ordinal).ToList()), "ordinal order");
        }

        // ───────────── scene asset becomes a scene entry ─────────────

        [Test]
        public void B5_SceneAsset_BecomesSceneEntry_WithoutWarning()
        {
            LogAssert.Expect(LogType.Log, new Regex(@"\[ContentLink\] catalog updated: 0 entries, 1 scenes"));
            var catalog = Update("Assets/TestContent/SceneCase");
            LogAssert.NoUnexpectedReceived();   // in particular: no 'skipping scene' warning — scenes are catalogued, not skipped
            Assert.That(catalog.Entries, Is.Empty, "a scene is not an asset entry");
            Assert.That(catalog.Scenes.Select(e => e.name), Is.EqualTo(new[] { "Empty" }));
            var id = catalog.Scenes[0].id;
            Assert.That(id.IsValid, Is.True);
            Assert.That(LoadableSceneIdEditorUtility.LoadableSceneIdToScene(id), Is.TypeOf<SceneAsset>());
            Assert.That(AssetDatabase.GetAssetPath(LoadableSceneIdEditorUtility.LoadableSceneIdToScene(id)), Is.EqualTo("Assets/TestContent/SceneCase/Empty.unity"));
        }

        // ───────────── scenes and assets share one name space, sorted ordinal ─────────────

        [Test]
        public void B6_DirectoryC_HasTwoScenes_AndSharedSprite()
        {
            var catalog = Update("Assets/TestContent/C");
            Assert.That(catalog.Scenes.Select(e => e.name), Is.EqualTo(new[] { "Scenes/One", "Scenes/Two" }), "scene entries, ordinal order");
            Assert.That(catalog.Entries.Select(e => e.name), Is.EqualTo(new[] { "Icons/shared", "Icons/shared[shared]" }), "asset entries");
            Assert.That(catalog.Scenes[0].id, Is.Not.EqualTo(catalog.Scenes[1].id), "distinct scene ids");
            foreach (var e in catalog.Scenes)
                Assert.That(AssetDatabase.GetAssetPath(LoadableSceneIdEditorUtility.LoadableSceneIdToScene(e.id)), Is.EqualTo($"Assets/TestContent/C/{e.name}.unity"), e.name);
            Assert.That(Resolve(Find(catalog, "Icons/shared[shared]")), Is.TypeOf<Sprite>());

            var again = Update("Assets/TestContent/C");
            Assert.That(again.Scenes.Select(e => (e.name, e.id.ToString())), Is.EqualTo(catalog.Scenes.Select(e => (e.name, e.id.ToString()))), "deterministic");
        }

        // ───────────── GUID and sub-asset name (ContentRef) ─────────────

        [Test]
        public void B7_EveryEntryRecordsItsAssetGuid_AndSpritesTheirSubName()
        {
            var catalog = Update("Assets/TestContent/A");
            foreach (var e in catalog.Entries)
            {
                var path = AssetDatabase.GetAssetPath(Resolve(e));
                Assert.That(e.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)), $"'{e.name}' guid is its file's GUID");
                Assert.That(e.guid, Does.Match("^[0-9a-f]{32}$"), $"'{e.name}' guid is 32 lowercase hex chars");
            }
            Assert.That(Find(catalog, "Icons/single").sub, Is.EqualTo(""), "a main asset has no sub name");
            Assert.That(Find(catalog, "Icons/single[single]").sub, Is.EqualTo("single"));
            Assert.That(Find(catalog, "Icons/sheet[s1]").sub, Is.EqualTo("s1"));
            Assert.That(Find(catalog, "Icons/sheet[s1]").guid, Is.EqualTo(Find(catalog, "Icons/sheet").guid), "a sprite shares its texture's GUID");

            var scenes = Update("Assets/TestContent/C").Scenes;
            foreach (var e in scenes)
                Assert.That(e.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID($"Assets/TestContent/C/{e.name}.unity")), $"scene '{e.name}' guid");
        }

        // ───────────── name style ─────────────

        /// <summary>T27: the folder B3 refuses — two files of one stem — gives two names with the extension, and the catalog records the style.</summary>
        [Test]
        public void B8_WithTheExtension_TwoFilesOfOneStemAreTwoNames_AndTheCatalogRecordsTheStyle()
        {
            ContentDirectoryBuilder.NameStyle = ContentNameStyle.PathWithExtension;
            try
            {
                var catalog = Update("Assets/TestContent/Dup");
                Assert.That(Resolve(Find(catalog, "x.png")), Is.InstanceOf<Texture2D>());
                Assert.That(Resolve(Find(catalog, "x.mat")), Is.InstanceOf<Material>());
                Assert.That(catalog.NameStyle, Is.EqualTo(ContentNameStyle.PathWithExtension));
            }
            finally { ContentDirectoryBuilder.ClearOverrides(); }   // the style too; the tear-down puts the paths back
        }
    }
}
