using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// ContentLinkEditorUtility against the real AssetDatabase. Self-contained: every test builds its own catalog
    /// from TestContent with the builder, so no content-directory build is needed.
    /// The catalog-search and rebuild-visibility tests are the load-bearing ones: the whole catalog index rests on them.
    /// </summary>
    [Category("EditorUtility")]
    public class ContentLinkEditorUtilityTests
    {
        const string k_RootA = "Assets/TestContent/A";
        const string k_RootC = "Assets/TestContent/C";
        const string k_RootSceneCase = "Assets/TestContent/SceneCase";
        const string k_Generated = "Assets/TestContent/Generated";
        const string k_TempRoot = "Assets/TestContent/Generated/P0";   // temp assets live away from the fixtures

        string m_SourceRoot, m_CatalogPath, m_OutputPath, m_TempCatalog, m_TempCatalog2;
        readonly List<string> m_TempAssets = new();

        [SetUp]
        public void SaveStatics()
        {
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            m_OutputPath = ContentDirectoryBuilder.OutputPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            m_TempCatalog = $"{k_Generated}/UtilityTest_{System.Guid.NewGuid():N}.asset";
            m_TempCatalog2 = $"{k_Generated}/UtilityTest_{System.Guid.NewGuid():N}.asset";
            if (!AssetDatabase.IsValidFolder(k_TempRoot)) AssetDatabase.CreateFolder(k_Generated, "P0");
        }

        [TearDown]
        public void RestoreStatics()
        {
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            ContentDirectoryBuilder.OutputPath = m_OutputPath;
            AssetDatabase.DeleteAsset(m_TempCatalog);
            AssetDatabase.DeleteAsset(m_TempCatalog2);
            foreach (var path in m_TempAssets) AssetDatabase.DeleteAsset(path);
            m_TempAssets.Clear();
            AssetDatabase.DeleteAsset(k_TempRoot);
            ContentLinkEditorUtility.Invalidate();
        }

        /// <summary>Builds a catalog from <paramref name="sourceRoot"/> into <paramref name="catalogPath"/>.</summary>
        ContentCatalog BuildAt(string sourceRoot, string catalogPath)
        {
            ContentDirectoryBuilder.SourceRoot = sourceRoot;
            ContentDirectoryBuilder.CatalogPath = catalogPath;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True, $"UpdateCatalog for '{sourceRoot}'");
            ContentLinkEditorUtility.Invalidate();
            var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(catalogPath);
            Assert.That(catalog, Is.Not.Null);
            return catalog;
        }

        /// <summary>A text asset created after a build, so it is under a root but in no catalog.</summary>
        TextAsset CreateTempAsset(string path)
        {
            File.WriteAllText(path, "content-link-test");
            AssetDatabase.ImportAsset(path);
            m_TempAssets.Add(path);
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            Assert.That(asset, Is.Not.Null, $"could not import '{path}'");
            return asset;
        }

        /// <summary>Builds a catalog from <paramref name="sourceRoot"/> into the temp path and drops the utility cache.</summary>
        ContentCatalog Build(string sourceRoot) => BuildAt(sourceRoot, m_TempCatalog);

        // ───────────── the whole index depends on this search finding catalogs ─────────────

        [Test]
        public void U6_FindAssets_FindsContentCatalogAssets()
        {
            var mine = Build(k_RootA);
            var found = ContentLinkEditorUtility.LoadCatalogs();
            Debug.Log($"[ContentLink.Tests] FindAssets(\"t:ContentCatalog\") → {found.Length} catalog(s): {string.Join(", ", found.Select(c => AssetDatabase.GetAssetPath(c)))}");
            Assert.That(found, Is.Not.Empty, "FindAssets(\"t:ContentCatalog\") found nothing — the whole name index would be silently empty, so another way of finding catalogs would be needed");
            Assert.That(found, Does.Contain(mine), "the catalog just built is not in the index");
        }

        // ───────────── a rebuilt catalog must be visible without a manual Invalidate ─────────────

        [Test]
        public void U7_RebuiltCatalog_IsSeenWithoutManualInvalidate()
        {
            Build(k_RootA);
            Assert.That(ContentLinkEditorUtility.Validate("Icons/single", typeof(Texture2D)), Is.EqualTo(NameStatus.Ok), "sanity: the first build is visible");

            // Rebuild the same asset from another source root — content changes, no asset is added or removed.
            ContentDirectoryBuilder.SourceRoot = k_RootC;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);

            var withoutInvalidate = ContentLinkEditorUtility.Validate("Icons/shared", typeof(Texture2D));
            ContentLinkEditorUtility.Invalidate();
            var afterInvalidate = ContentLinkEditorUtility.Validate("Icons/shared", typeof(Texture2D));
            Debug.Log($"[ContentLink.Tests] after rebuilding the catalog: withoutInvalidate={withoutInvalidate} afterInvalidate={afterInvalidate}");

            Assert.That(afterInvalidate, Is.EqualTo(NameStatus.Ok), "an explicit Invalidate must always work");
            Assert.That(withoutInvalidate, Is.EqualTo(NameStatus.Ok),
                "the cache did not notice a catalog rebuild — EditorApplication.projectChanged does not fire for a content-only change, so the utility needs an AssetPostprocessor instead");
        }

        // ───────────── round trip against the real AssetDatabase ─────────────

        [Test]
        public void E1_AssetToName_AndBack()
        {
            Build(k_RootA);
            ContentDirectoryBuilder.SourceRoot = k_RootA;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_RootA}/Icons/single.png");
            var sprite = AssetDatabase.LoadAllAssetRepresentationsAtPath($"{k_RootA}/Icons/single.png").OfType<Sprite>().Single();
            Assert.That(tex, Is.Not.Null);

            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(tex, out var texName, out _), Is.True);
            Assert.That(texName, Is.EqualTo("Icons/single"));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(sprite, out var spriteName, out _), Is.True);
            Assert.That(spriteName, Is.EqualTo("Icons/single[single]"));

            Assert.That(ContentLinkEditorUtility.ResolveAsset("Icons/single"), Is.SameAs(tex));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("Icons/single[single]"), Is.SameAs(sprite));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("Icons/nope"), Is.Null);
        }

        [Test]
        public void E1b_AssetToLink_SplitsTheSpriteName_AndBack()
        {
            Build(k_RootA);
            ContentDirectoryBuilder.SourceRoot = k_RootA;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_RootA}/Icons/single.png");
            var sprite = AssetDatabase.LoadAllAssetRepresentationsAtPath($"{k_RootA}/Icons/single.png").OfType<Sprite>().Single();

            Assert.That(ContentLinkEditorUtility.TryGetLinkForAsset(tex, out var texName, out var texSub, out _), Is.True);
            Assert.That((texName, texSub), Is.EqualTo(("Icons/single", "")));
            Assert.That(ContentLinkEditorUtility.TryGetLinkForAsset(sprite, out var spriteName, out var spriteSub, out _), Is.True);
            Assert.That((spriteName, spriteSub), Is.EqualTo(("Icons/single", "single")), "a sprite is its texture's name plus its own name");

            Assert.That(ContentLinkEditorUtility.ResolveAsset("Icons/single", "single"), Is.SameAs(sprite));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("Icons/single", ""), Is.SameAs(tex));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("Icons/single", "nope"), Is.Null);
            Assert.That(ContentLinkEditorUtility.Validate("Icons/single", "single", typeof(Sprite)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate("Icons/single", "", typeof(Sprite)), Is.EqualTo(NameStatus.TypeMismatch));
            Assert.That(ContentLinkEditorUtility.Validate("Icons/single", "nope", typeof(Sprite)), Is.EqualTo(NameStatus.NotInCatalog));
        }

        [Test]
        public void E2_Validate_TypeRules()
        {
            Build(k_RootA);
            ContentDirectoryBuilder.SourceRoot = k_RootA;

            Assert.That(ContentLinkEditorUtility.Validate("Icons/single[single]", typeof(Sprite)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate("Icons/single", typeof(Texture2D)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate("Icons/single", typeof(Sprite)), Is.EqualTo(NameStatus.TypeMismatch), "the path name is the texture, not the sprite");
            Assert.That(ContentLinkEditorUtility.Validate("", typeof(Sprite)), Is.EqualTo(NameStatus.Empty));
            Assert.That(ContentLinkEditorUtility.Validate("Icons/nope", typeof(Sprite)), Is.EqualTo(NameStatus.NotInCatalog));

            // A component T is held by the prefab that carries it.
            Assert.That(ContentLinkEditorUtility.Validate("Prefabs/Plain", typeof(Transform)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate("Prefabs/Plain", typeof(AudioSource)), Is.EqualTo(NameStatus.TypeMismatch));
            Assert.That(ContentLinkEditorUtility.ObjectFieldTypeFor(typeof(Transform)), Is.EqualTo(typeof(GameObject)));
        }

        [Test]
        public void E3_UncataloguedAssetOutsideRoot_IsRefused_AndPathFallbackFindsUncataloguedNames()
        {
            // The index covers every catalog in the project, so "outside" means outside SourceRoot *and* in no
            // catalog. Dup never builds (its duplicate name is refused) and SceneCase is only ever built into a temp catalog.
            Build($"{k_RootA}/Icons");
            ContentDirectoryBuilder.SourceRoot = k_RootSceneCase;

            var dup = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TestContent/Dup/x.png");
            Assert.That(dup, Is.Not.Null);
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(dup, out _, out var reason), Is.False);
            Assert.That(reason, Does.Contain("not under"));

            // Under the root but in no catalog: the name is still resolvable so a kept name does not look empty.
            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>($"{k_RootSceneCase}/Empty.unity");
            Assert.That(scene, Is.Not.Null);
            Assert.That(ContentLinkEditorUtility.Validate("Empty", null, scene: true), Is.EqualTo(NameStatus.NotInCatalog));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("Empty"), Is.SameAs(scene),
                "the path fallback must still show the asset");
        }

        [Test]
        public void E4_AssetOfAnotherCatalog_KeepsThatCatalogsName_WhateverSourceRootSays()
        {
            // The reverse index is the first source for asset → name, so a name is correct even when the static
            // SourceRoot points at a different content directory. Without it the path rule would produce
            // a name relative to the wrong root, or refuse outright.
            Build(k_RootA);
            ContentDirectoryBuilder.SourceRoot = k_RootSceneCase;

            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_RootA}/Icons/single.png");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(tex, out var name, out var reason), Is.True,
                $"an asset that a catalog knows must resolve regardless of SourceRoot (reason: {reason})");
            Assert.That(name, Is.EqualTo("Icons/single"));
        }

        // ───────────── a catalog remembers its own root, and the path rule uses that root ─────────────

        [Test]
        public void E5_CatalogRemembersItsSourceRoot()
        {
            var catalog = Build(k_RootA);
            Assert.That(catalog.SourceRoot, Is.EqualTo(k_RootA), "the builder must record the folder it built from");

            var fresh = ScriptableObject.CreateInstance<ContentCatalog>();
            try
            {
                Assert.That(fresh.SourceRoot, Is.Empty, "a catalog that was never built knows no root — callers must handle an empty root rather than assume one");
            }
            finally { Object.DestroyImmediate(fresh); }
        }

        [Test]
        public void E6_PathRuleFollowsTheCatalogsRoot_NotTheStaticOne()
        {
            // The asset is created after the build, so it is in no catalog and the path rule decides. That rule used
            // to read the static SourceRoot alone, so an asset under another directory's root was refused.
            BuildAt(k_TempRoot, m_TempCatalog);
            var late = CreateTempAsset($"{k_TempRoot}/late.txt");
            ContentDirectoryBuilder.SourceRoot = k_RootA;
            ContentLinkEditorUtility.Invalidate();

            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(late, out var name, out var reason), Is.True,
                $"an asset under a catalog's own root must be named whatever the static root says (reason: {reason})");
            Assert.That(name, Is.EqualTo("late"));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("late"), Is.SameAs(late), "name → asset must search the same roots");
        }

        [Test]
        public void E7_NestedRoots_TheDeepestOneNamesTheAsset()
        {
            var nested = $"{k_TempRoot}/Nested";
            if (!AssetDatabase.IsValidFolder(nested)) AssetDatabase.CreateFolder(k_TempRoot, "Nested");
            BuildAt(k_TempRoot, m_TempCatalog);
            BuildAt(nested, m_TempCatalog2);
            var x = CreateTempAsset($"{nested}/x.txt");
            ContentLinkEditorUtility.Invalidate();

            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(x, out var name, out var reason), Is.True, reason);
            Assert.That(name, Is.EqualTo("x"), "the deepest root names the asset; the outer root would have called it 'Nested/x'");
        }

        // ───────────── references (ContentRef) ─────────────

        [Test]
        public void R1_AssetToReference_AndBack_ByGuid()
        {
            Build(k_RootA);
            const string sheetPath = k_RootA + "/Icons/sheet.png";
            var sheet = AssetDatabase.LoadAssetAtPath<Texture2D>(sheetPath);
            var s1 = AssetDatabase.LoadAllAssetRepresentationsAtPath(sheetPath).OfType<Sprite>().First(s => s.name == "s1");

            Assert.That(ContentLinkEditorUtility.TryGetRefForAsset(sheet, out var guid, out var sub, out var reason), Is.True, reason);
            Assert.That(guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(sheetPath)));
            Assert.That(sub, Is.EqualTo(""), "a main asset has no sub name");
            Assert.That(ContentLinkEditorUtility.TryGetRefForAsset(s1, out var spriteGuid, out var spriteSub, out reason), Is.True, reason);
            Assert.That(spriteGuid, Is.EqualTo(guid), "a sprite is referenced by its texture's GUID");
            Assert.That(spriteSub, Is.EqualTo("s1"));

            Assert.That(ContentLinkEditorUtility.ResolveRefAsset(guid, ""), Is.EqualTo(sheet));
            Assert.That(ContentLinkEditorUtility.ResolveRefAsset(guid, "s1"), Is.EqualTo(s1));
            Assert.That(ContentLinkEditorUtility.NameForRef(guid, "s1"), Is.EqualTo("Icons/sheet[s1]"));

            Assert.That(ContentLinkEditorUtility.ValidateRef(guid, "s1", typeof(Sprite), false, out var tip), Is.EqualTo(NameStatus.Ok), tip);
            Assert.That(ContentLinkEditorUtility.ValidateRef(guid, "", typeof(Sprite), false, out tip), Is.EqualTo(NameStatus.TypeMismatch));
            Assert.That(tip, Does.Contain("pick the sprite itself"), "a texture reference in a Sprite field says what to do");
            Assert.That(ContentLinkEditorUtility.ValidateRef(guid, "renamed", typeof(Sprite), false, out _), Is.EqualTo(NameStatus.Missing), "a sprite name the texture does not have");
            Assert.That(ContentLinkEditorUtility.ValidateRef("0123456789abcdef0123456789abcdef", "", typeof(Texture2D), false, out _), Is.EqualTo(NameStatus.Missing), "a GUID no asset has");
        }

        [Test]
        public void R2_AssetOutsideEveryRoot_IsRefused_AndAnExistingValueSaysMovingKeepsTheGuid()
        {
            Build(k_RootA);
            const string outsidePath = "Assets/Tests/Editor/DrawerFixture.cs";   // under no content root
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(outsidePath);
            Assert.That(script, Is.Not.Null);
            Assert.That(ContentLinkEditorUtility.TryGetRefForAsset(script, out _, out _, out var reason), Is.False, "a pick outside every root must be refused");
            Assert.That(reason, Does.Contain("not under a content root"));

            var status = ContentLinkEditorUtility.ValidateRef(AssetDatabase.AssetPathToGUID(outsidePath), "", typeof(UnityEngine.Object), false, out var tip);
            Assert.That(status, Is.EqualTo(NameStatus.NotInCatalog), "a value already in the field is shown, never refused");
            Assert.That(tip, Does.Contain("moving it under one keeps its GUID"));
        }

        [Test]
        public void R3_MovedFile_KeepsItsReference_AndTheRebuiltCatalogGivesTheNewName()
        {
            BuildAt(k_TempRoot, m_TempCatalog);
            var from = $"{k_TempRoot}/mover.txt";
            var asset = CreateTempAsset(from);
            BuildAt(k_TempRoot, m_TempCatalog);
            Assert.That(ContentLinkEditorUtility.TryGetRefForAsset(asset, out var guid, out var sub, out var reason), Is.True, reason);
            Assert.That(ContentLinkEditorUtility.NameForRef(guid, sub), Is.EqualTo("mover"));

            AssetDatabase.CreateFolder(k_TempRoot, "Moved");
            Assert.That(AssetDatabase.MoveAsset(from, $"{k_TempRoot}/Moved/mover.txt"), Is.Empty, "the move itself failed");
            ContentLinkEditorUtility.Invalidate();
            Assert.That(ContentLinkEditorUtility.ResolveRefAsset(guid, sub), Is.EqualTo(asset), "the object field still finds the moved file by GUID");
            Assert.That(ContentLinkEditorUtility.NameForRef(guid, sub), Is.EqualTo("mover"), "until the catalog is rebuilt it answers the name it recorded");

            BuildAt(k_TempRoot, m_TempCatalog);
            Assert.That(ContentLinkEditorUtility.NameForRef(guid, sub), Is.EqualTo("Moved/mover"), "the rebuilt catalog names the new path; the serialized reference did not change");
            Assert.That(ContentLinkEditorUtility.ValidateRef(guid, sub, typeof(TextAsset), false, out var tip), Is.EqualTo(NameStatus.Ok), tip);
        }
    }
}
