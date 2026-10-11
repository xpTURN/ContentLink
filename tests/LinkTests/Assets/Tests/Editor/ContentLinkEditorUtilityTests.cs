using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
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

        /// <summary>
        /// T8. The names are ones no other catalog has: the test content's catalogs (CatalogC carries Icons/shared) would
        /// answer a name of theirs from a stale index, and the test would pass without the rebuild being seen.
        /// </summary>
        [Test]
        public void U7_RebuiltCatalog_IsSeenWithoutManualInvalidate()
        {
            CreateTempAsset($"{k_TempRoot}/u7first.txt");
            Build(k_TempRoot);
            Assert.That(ContentLinkEditorUtility.Validate("u7first", typeof(TextAsset)), Is.EqualTo(NameStatus.Ok), "sanity: the first build is visible");
            CreateTempAsset($"{k_TempRoot}/u7second.txt");
            Assert.That(ContentLinkEditorUtility.Validate("u7second", typeof(TextAsset)), Is.EqualTo(NameStatus.NotInCatalog), "sanity: the index is loaded, without the new file");

            // Rebuild the same catalog asset — content changes, no asset is added or removed.
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);

            var withoutInvalidate = ContentLinkEditorUtility.Validate("u7second", typeof(TextAsset));
            ContentLinkEditorUtility.Invalidate();
            var afterInvalidate = ContentLinkEditorUtility.Validate("u7second", typeof(TextAsset));
            Debug.Log($"[ContentLink.Tests] after rebuilding the catalog: withoutInvalidate={withoutInvalidate} afterInvalidate={afterInvalidate}");

            Assert.That(afterInvalidate, Is.EqualTo(NameStatus.Ok), "an explicit Invalidate must always work");
            Assert.That(withoutInvalidate, Is.EqualTo(NameStatus.Ok),
                "the cache did not notice a catalog rebuild — EditorApplication.projectChanged does not fire for a content-only change, so the utility needs an AssetPostprocessor instead");
        }

        /// <summary>T9: a catalog changed outside the editor — a version-control update — and refreshed. No signal but the import says so.</summary>
        [Test]
        public void U8_CatalogChangedOutsideTheEditor_IsSeenAfterTheRefresh()
        {
            CreateTempAsset($"{k_TempRoot}/t9.txt");
            Build(k_TempRoot);
            Assert.That(ContentLinkEditorUtility.Validate("t9", typeof(TextAsset)), Is.EqualTo(NameStatus.Ok), "sanity");

            var text = File.ReadAllText(m_TempCatalog);
            Assert.That(text, Does.Contain("name: t9\n"), "sanity: the entry as the catalog asset stores it");
            File.WriteAllText(m_TempCatalog, text.Replace("name: t9\n", "name: t9renamed\n"));
            AssetDatabase.Refresh();

            var seen = ContentLinkEditorUtility.Validate("t9renamed", typeof(TextAsset));
            Debug.Log($"[ContentLink.Tests] a catalog edited outside the editor, after the refresh: 't9renamed' = {seen}");
            Assert.That(seen, Is.EqualTo(NameStatus.Ok), "a catalog changed on disk is not seen after the refresh — it raises no projectChanged (measured)");
        }

        const string k_Plain = "Assets/TestContent/A/Prefabs/Plain.prefab";

        /// <summary>A copy of Plain.prefab under the temp root, in the temp catalog: its GUID.</summary>
        string CataloguedPrefab(string name)
        {
            var path = $"{k_TempRoot}/{name}.prefab";
            Assert.That(AssetDatabase.CopyAsset(k_Plain, path), Is.True);
            m_TempAssets.Add(path);
            Build(k_TempRoot);
            var guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(ContentLinkEditorUtility.ValidateRef(guid, "", typeof(BoxCollider), false, out _), Is.EqualTo(NameStatus.TypeMismatch), "sanity: no collider yet");
            return guid;
        }

        /// <summary>Saves the prefab with its contents changed — Prefab Mode's own way of saving.</summary>
        static void SavePrefab(string path, System.Action<GameObject> change)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                change(root);
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        static NameStatus ColliderStatus(string guid) => ContentLinkEditorUtility.ValidateRef(guid, "", typeof(BoxCollider), false, out _);

        /// <summary>
        /// T61: a catalogued prefab saved with the component a reference's field holds — a save raises no projectChanged
        /// (measured on 6000.6). The reference's status follows without a manual Invalidate.
        /// </summary>
        [Test]
        public void U10_APrefabSavedWithTheComponent_TheReferenceStatusFollows()
        {
            var guid = CataloguedPrefab("u10");
            SavePrefab(AssetDatabase.GUIDToAssetPath(guid), root => root.AddComponent<BoxCollider>());
            Assert.That(ColliderStatus(guid), Is.EqualTo(NameStatus.Ok), "the prefab has the collider now");
        }

        /// <summary>T61b: … and saved without it — the status starts from a fresh Ok, so only following the save turns it into a mismatch.</summary>
        [Test]
        public void U10b_APrefabSavedWithoutTheComponent_TheReferenceStatusFollows()
        {
            var guid = CataloguedPrefab("u10b");
            var path = AssetDatabase.GUIDToAssetPath(guid);
            SavePrefab(path, root => root.AddComponent<BoxCollider>());
            ContentLinkEditorUtility.Invalidate();   // a fresh Ok: the mismatch below can only come from the save
            Assert.That(ColliderStatus(guid), Is.EqualTo(NameStatus.Ok), "sanity: the collider is there");
            SavePrefab(path, root => Object.DestroyImmediate(root.GetComponent<BoxCollider>()));
            Assert.That(ColliderStatus(guid), Is.EqualTo(NameStatus.TypeMismatch), "the collider is gone — an Ok here would let a load fail the type");
        }

        /// <summary>T62: the same through a version-control update — the component written into the prefab on disk, and a refresh.</summary>
        [Test]
        public void U11_APrefabChangedOnDisk_TheReferenceStatusFollowsTheRefresh()
        {
            var guid = CataloguedPrefab("u11");
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var text = File.ReadAllText(path).Replace("\r\n", "\n");
            var gameObject = Regex.Match(text, @"--- !u!1 &(\d+)\n");
            Assert.That(gameObject.Success && text.Contains("  m_Component:\n"), Is.True, "sanity: the prefab as Plain.prefab stores it");
            const string colliderId = "5555555555555555555";
            text = text.Replace("  m_Component:\n", $"  m_Component:\n  - component: {{fileID: {colliderId}}}\n")
                 + $"--- !u!65 &{colliderId}\nBoxCollider:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {{fileID: 0}}\n  m_PrefabInstance: {{fileID: 0}}\n  m_PrefabAsset: {{fileID: 0}}\n"
                 + $"  m_GameObject: {{fileID: {gameObject.Groups[1].Value}}}\n  m_Material: {{fileID: 0}}\n  m_IsTrigger: 0\n  m_Enabled: 1\n  serializedVersion: 3\n  m_Size: {{x: 1, y: 1, z: 1}}\n  m_Center: {{x: 0, y: 0, z: 0}}\n";
            File.WriteAllText(path, text);
            AssetDatabase.Refresh();
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<BoxCollider>(), Is.Not.Null, "sanity: the refresh imported the collider");
            Assert.That(ColliderStatus(guid), Is.EqualTo(NameStatus.Ok), "a prefab changed on disk raises no projectChanged (measured): the status follows the import");
        }

        /// <summary>
        /// T23: under a root that keeps the extension the path fallback looks the path up, and that lookup ignores case
        /// (measured on 6000.6). A name that differs from the file only in case must not resolve — the build names the file as
        /// it is on disk, so the name would work in the editor and fail in a player.
        /// </summary>
        [Test]
        public void U9_UnderARootThatKeepsTheExtension_ANameInAnotherCaseDoesNotResolve()
        {
            var icons = $"{k_TempRoot}/Icons";
            if (!AssetDatabase.IsValidFolder(icons)) AssetDatabase.CreateFolder(k_TempRoot, "Icons");
            var asset = CreateTempAsset($"{icons}/a.v2.txt");
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;   // never catalogued: only the path fallback answers
            ContentDirectoryBuilder.NameStyle = ContentNameStyle.PathWithExtension;
            try
            {
                string R(string name) => ContentLinkEditorUtility.ResolveAsset(name) is Object o && o != null ? AssetDatabase.GetAssetPath(o) : "null";
                var right = R("Icons/a.v2.txt");
                var wrong = new[] { R("Icons/A.v2.txt"), R("icons/a.v2.txt"), R("Icons/a.v2.TXT"), R("ICONS/A.V2.TXT") };
                Debug.Log($"[ContentLink.Tests] 'Icons/a.v2.txt' → {right} · in other cases → {string.Join(", ", wrong)}");
                Assert.That(right, Is.EqualTo(AssetDatabase.GetAssetPath(asset)));
                Assert.That(wrong, Is.All.EqualTo("null"), "a name that differs from the file only in case resolves in the editor (the asset database ignores case)");
            }
            finally { ContentDirectoryBuilder.ClearOverrides(); }   // the style too; the tear-down puts the paths back
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

        /// <summary>
        /// T20: res: names against real Resources folders (Assets/Tests/Resources/ContentLinkTests and the cases around
        /// it) — what a player has is named, the rest refused with its reason, and the status follows the engine's own answers.
        /// </summary>
        [Test]
        public void E8_ResourcesFolders_NamesAndStatus()
        {
            const string res = "Assets/Tests/Resources/ContentLinkTests";
            string Name(string path) => ContentLinkEditorUtility.TryGetNameForAsset(AssetDatabase.LoadMainAssetAtPath(path), out var n, out var r) ? n : "refused: " + r;
            ContentLinkEditorUtility.Invalidate();

            Assert.That(Name($"{res}/logo.png"), Is.EqualTo("res:ContentLinkTests/logo"));
            Assert.That(Name($"{res}/Outer/Resources/ContentLinkTests/nested.txt"), Is.EqualTo("res:ContentLinkTests/nested"), "a nested folder: the innermost Resources folder names it");
            Assert.That(Name("Packages/com.xpturn.contentlink.tests.resources/Resources/ContentLinkTests/package.txt"), Is.EqualTo("res:ContentLinkTests/package"), "a package's Resources folder is built");
            Assert.That(Name("Assets/Tests/Lower/resources/ContentLinkTests/lower.txt"), Is.EqualTo("res:ContentLinkTests/lower"), "a lowercase resources folder is built (measured)");
            Assert.That(Name($"{res}/Editor/inside.txt"), Is.EqualTo("res:ContentLinkTests/Editor/inside"), "an Editor folder inside Resources is built (measured)");
            Assert.That(Name("Assets/Tests/Editor/Resources/ContentLinkTests/edonly.txt"), Does.StartWith("refused").And.Contain("Editor folder"), "a Resources folder under an Editor folder is not built");
            Assert.That(Name($"{res}/Twin/x.png"), Does.StartWith("refused").And.Contain("x.prefab"), "three files share the path: the reason names them");
            Assert.That(Name($"{res}/level.unity"), Does.StartWith("refused").And.Contain("scene"));

            var s0 = AssetDatabase.LoadAllAssetRepresentationsAtPath($"{res}/sheet.png").OfType<Sprite>().First(s => s.name == "s0");
            Assert.That(ContentLinkEditorUtility.TryGetLinkForAsset(s0, out var linkName, out var linkSub, out var reason), Is.True, reason);
            Assert.That((linkName, linkSub), Is.EqualTo(("res:ContentLinkTests/sheet", "s0")));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("res:ContentLinkTests/sheet", "s0"), Is.SameAs(s0));

            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/logo", typeof(Texture2D)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate("res:contentlinktests/LOGO", typeof(Texture2D)), Is.EqualTo(NameStatus.Ok), "the engine ignores case (measured)");
            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/logo", typeof(Sprite)), Is.EqualTo(NameStatus.TypeMismatch), "the main asset is the texture, not its sprite");
            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/sheet", "s1", typeof(Sprite)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/nope", typeof(Texture2D)), Is.EqualTo(NameStatus.NotInCatalog));
            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/edonly", typeof(TextAsset)), Is.EqualTo(NameStatus.NotInCatalog), "editor-only: not what a player has");
            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/hero", typeof(Transform)), Is.EqualTo(NameStatus.Ok), "a prefab's component");
            Assert.That(ContentLinkEditorUtility.Validate("res:ContentLinkTests/Twin/x", typeof(Transform)), Is.EqualTo(NameStatus.Ok), "a shared path: the engine's pick is the prefab (measured)");

            Assert.That(ContentLinkEditorUtility.TryGetRefForAsset(AssetDatabase.LoadMainAssetAtPath($"{res}/logo.png"), out _, out _, out var why), Is.False, "a reference needs a catalog");
            Assert.That(why, Does.Contain("res:ContentLinkTests/logo"));
        }

        static void F16Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            F16Folder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }

        static void F16File(string path, string kind)
        {
            F16Folder(path.Substring(0, path.LastIndexOf('/')));
            if (kind == "prefab") { var go = new GameObject(Path.GetFileNameWithoutExtension(path)); PrefabUtility.SaveAsPrefabAsset(go, path); Object.DestroyImmediate(go); return; }
            if (kind == "txt") File.WriteAllText(path, path);
            else { var t = new Texture2D(4, 4); File.WriteAllBytes(path, t.EncodeToPNG()); Object.DestroyImmediate(t); }
            AssetDatabase.ImportAsset(path);
        }

        /// <summary>
        /// A path a player's file shares with a file in a Resources folder under an Editor folder. The editor's
        /// Resources.Load gives either — nothing a project controls decides which (measured: the order of creation, of folders, of
        /// GUIDs) — while a player has only its own: the answer and the status are the player's file's, whichever the engine gave.
        /// </summary>
        [Test]
        public void E8b_APathAnEditorOnlyFileShares_IsThePlayersFile()
        {
            const string root = "Assets/Tests/F16Pairs";
            var kinds = new[] { "txt", "png", "prefab" };
            var cases = new List<(string rel, string player, System.Type type)>();
            int editorFirst = 0;
            try
            {
                int i = 0;
                foreach (var pk in kinds) foreach (var ek in kinds) foreach (bool playerMadeFirst in new[] { true, false })
                {
                    string player = $"{root}/c{i}/A/Resources/F16/x{i}.{pk}", editor = $"{root}/c{i}/Z/Editor/Resources/F16/x{i}.{ek}";
                    if (playerMadeFirst) { F16File(player, pk); F16File(editor, ek); } else { F16File(editor, ek); F16File(player, pk); }
                    cases.Add(($"F16/x{i}", player, pk == "txt" ? typeof(TextAsset) : pk == "png" ? typeof(Texture2D) : typeof(GameObject)));
                    ++i;
                }
                ContentLinkEditorUtility.Invalidate();
                foreach (var (rel, player, type) in cases)
                {
                    if (AssetDatabase.GetAssetPath(Resources.Load(rel)) != player) ++editorFirst;
                    Assert.That(AssetDatabase.GetAssetPath(ContentLinkEditorUtility.ResolveAsset("res:" + rel)), Is.EqualTo(player), rel);
                    Assert.That(ContentLinkEditorUtility.Validate("res:" + rel, type), Is.EqualTo(NameStatus.Ok), rel);
                }
                Assert.That(editorFirst, Is.GreaterThan(0), "the engine gave the player's file for every path: nothing above was tested");
                Debug.Log($"[ContentLink.Tests] E8b: the engine gave the editor-only file for {editorFirst} of {cases.Count} paths");
            }
            finally { AssetDatabase.DeleteAsset(root); ContentLinkEditorUtility.Invalidate(); }
        }

        /// <summary>
        /// A player's png and prefab share a path with an editor-only png. A player gives the prefab; the editor's own Resources.Load does
        /// not keep that rule when an editor-only file shares the path — it gives either of the three (measured: the player's png in 5 of
        /// 18) — so the answer is the player's prefab, whichever it gave.
        /// </summary>
        [Test]
        public void E8c_APlayersPngAndPrefabWithAnEditorOnlyPng_IsThePlayersPrefab()
        {
            const string root = "Assets/Tests/F16Triples";
            var orders = new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 1 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 }, new[] { 2, 0, 1 }, new[] { 2, 1, 0 } };
            int notThePrefab = 0;
            try
            {
                for (int i = 0; i < 18; ++i)
                {
                    var files = new[] { ($"{root}/c{i}/A/Resources/F16T/y{i}.png", "png"), ($"{root}/c{i}/A/Resources/F16T/y{i}.prefab", "prefab"), ($"{root}/c{i}/Z/Editor/Resources/F16T/y{i}.png", "png") };
                    foreach (var k in orders[i % orders.Length]) F16File(files[k].Item1, files[k].Item2);
                }
                ContentLinkEditorUtility.Invalidate();
                for (int i = 0; i < 18; ++i)
                {
                    string prefab = $"{root}/c{i}/A/Resources/F16T/y{i}.prefab";
                    if (AssetDatabase.GetAssetPath(Resources.Load($"F16T/y{i}")) != prefab) ++notThePrefab;
                    Assert.That(AssetDatabase.GetAssetPath(ContentLinkEditorUtility.ResolveAsset($"res:F16T/y{i}")), Is.EqualTo(prefab), $"y{i}");
                    Assert.That(ContentLinkEditorUtility.Validate($"res:F16T/y{i}", typeof(GameObject)), Is.EqualTo(NameStatus.Ok), $"y{i}");
                }
                Assert.That(notThePrefab, Is.GreaterThan(0), "the engine gave the player's prefab for every path: nothing above was tested");
                Debug.Log($"[ContentLink.Tests] E8c: the engine gave another file than the player's prefab for {notThePrefab} of 18 paths");
            }
            finally { AssetDatabase.DeleteAsset(root); ContentLinkEditorUtility.Invalidate(); }
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

        // ───────────── a bracketed name by the path fallback ─────────────

        /// <summary>
        /// A file whose name ends with ']' — 'foo[1].txt', a scene 'L[1].unity' — that no catalog has yet. The builder names it
        /// 'foo[1]', and the path fallback answers that file rather than a sprite '1' of 'foo' — which would leave the field showing
        /// None right after the pick.
        /// </summary>
        [Test]
        public void E9_ABracketedFileNoCatalogHasYet_ResolvesToTheFile()
        {
            var asset = CreateTempAsset($"{k_TempRoot}/foo[1].txt");
            Assert.That(AssetDatabase.CopyAsset($"{k_RootSceneCase}/Empty.unity", $"{k_TempRoot}/L[1].unity"), Is.True);
            m_TempAssets.Add($"{k_TempRoot}/L[1].unity");
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;   // never catalogued: only the path fallback answers
            ContentLinkEditorUtility.Invalidate();

            Assert.That(ContentLinkEditorUtility.TryGetLinkForAsset(asset, out var name, out var sub, out var reason), Is.True, reason);
            Assert.That($"{name}|{sub}", Is.EqualTo("foo[1]|"));
            Assert.That(ContentLinkEditorUtility.ResolveAsset(name, sub), Is.SameAs(asset), "the field shows the file it was given");
            Assert.That(ContentLinkEditorUtility.ResolveScene("L[1]"), Is.SameAs(AssetDatabase.LoadAssetAtPath<SceneAsset>($"{k_TempRoot}/L[1].unity")));
        }

        /// <summary>
        /// 'sheet[s0].txt' beside 'sheet.png' and its sprite s0 — two things of one name, which the builder refuses for that root. The path
        /// fallback answers neither, as the editor simulation does, and neither is picked — the reason names both files, as for a res: path
        /// several files share.
        /// </summary>
        [Test]
        public void E10_ABracketedFileBesideTheSpriteOfItsName_AnswersNothing_AndIsNotPicked()
        {
            Assert.That(AssetDatabase.CopyAsset($"{k_RootA}/Icons/sheet.png", $"{k_TempRoot}/sheet.png"), Is.True);
            m_TempAssets.Add($"{k_TempRoot}/sheet.png");
            CreateTempAsset($"{k_TempRoot}/sheet[s0].txt");
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;
            ContentLinkEditorUtility.Invalidate();

            Assert.That(ContentLinkEditorUtility.ResolveAsset("sheet[s0]"), Is.Null, "the name answered the sprite");
            Assert.That(ContentLinkEditorUtility.ResolveAsset("sheet", "s0"), Is.Null, "the link answered the sprite");
            var sprites = AssetDatabase.LoadAllAssetRepresentationsAtPath($"{k_TempRoot}/sheet.png").OfType<Sprite>().ToArray();
            var s0 = sprites.First(s => s.name == "s0");
            Assert.That(ContentLinkEditorUtility.ResolveAsset("sheet[s1]"), Is.SameAs(sprites.First(s => s.name == "s1")), "the sheet's other sprite still answers");
            var literal = AssetDatabase.LoadMainAssetAtPath($"{k_TempRoot}/sheet[s0].txt");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(literal, out _, out var why), Is.False, "the pick of the file named it");
            Assert.That(why, Does.Contain($"{k_TempRoot}/sheet.png").And.Contain($"{k_TempRoot}/sheet[s0].txt"), "the reason names both");
            Assert.That(ContentLinkEditorUtility.TryGetLinkForAsset(s0, out _, out _, out var whyLink), Is.False, "the pick of the sprite linked it");
            Assert.That(whyLink, Is.EqualTo(why), "the sprite's pick gives the same reason");
            LogAssert.Expect(LogType.Error, new Regex(@"duplicate name 'sheet\[s0\]'"));
            Assert.That(ContentDirectoryBuilder.Scan(k_TempRoot, new List<ContentDirectoryBuilder.Scanned>()), Is.False, "the builder refuses the root for that name");
        }

        /// <summary>
        /// 'dup.txt' and 'dup.bytes' share the name 'dup', which the builder refuses. The path fallback answers neither — not the one
        /// the search lists first (by path — measured on 6000.6) — and neither is picked.
        /// </summary>
        [Test]
        public void E11_TwoFilesOfOneName_AnswerNothing_AndAreNotPicked()
        {
            CreateTempAsset($"{k_TempRoot}/dup.txt");
            CreateTempAsset($"{k_TempRoot}/dup.bytes");
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;
            ContentLinkEditorUtility.Invalidate();

            Assert.That(ContentLinkEditorUtility.ResolveAsset("dup"), Is.Null, "the name answered one of the two");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(AssetDatabase.LoadMainAssetAtPath($"{k_TempRoot}/dup.txt"), out _, out var why), Is.False, "the pick named it");
            Assert.That(why, Does.Contain($"{k_TempRoot}/dup.txt").And.Contain($"{k_TempRoot}/dup.bytes"), "the reason names both");
            LogAssert.Expect(LogType.Error, new Regex("duplicate name 'dup'"));
            Assert.That(ContentDirectoryBuilder.Scan(k_TempRoot, new List<ContentDirectoryBuilder.Scanned>()), Is.False, "the builder refuses the root for that name");
        }

        /// <summary>
        /// A file whose main asset does not load — a ScriptableObject whose script is gone — beside 'doc.txt'. The engine
        /// lists it by the name 'doc' and loads nothing for it; the builder skips it and builds 'doc.txt' under that name (measured on
        /// 6000.6). The path fallback answers 'doc.txt' too, though the search lists 'doc.asset' first.
        /// </summary>
        [Test]
        public void E12_AFileWhoseMainAssetDoesNotLoad_IsNoSecondFileOfItsName()
        {
            LogAssert.ignoreFailingMessages = true;   // the import complains about the missing script
            var txt = CreateTempAsset($"{k_TempRoot}/doc.txt");
            File.WriteAllText($"{k_TempRoot}/doc.asset", "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!114 &11400000\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: 0}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: 11500000, guid: 0123456789abcdef0123456789abcdef, type: 3}\n  m_Name: doc\n  m_EditorClassIdentifier: \n");
            AssetDatabase.ImportAsset($"{k_TempRoot}/doc.asset");
            m_TempAssets.Add($"{k_TempRoot}/doc.asset");
            Assume.That(AssetDatabase.LoadMainAssetAtPath($"{k_TempRoot}/doc.asset"), Is.Null, "the engine loads such an asset now — the case is gone");
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;
            ContentLinkEditorUtility.Invalidate();

            Assert.That(ContentLinkEditorUtility.ResolveAsset("doc"), Is.SameAs(txt), "the name answered the file that does not load");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(txt, out var name, out var reason), Is.True, reason);
            Assert.That(name, Is.EqualTo("doc"));
            var scanned = new List<ContentDirectoryBuilder.Scanned>();
            Assert.That(ContentDirectoryBuilder.Scan(k_TempRoot, scanned), Is.True, "the builder refused the root");
            Assert.That(scanned.Single(e => e.Entry.name == "doc").Path, Is.EqualTo($"{k_TempRoot}/doc.txt"));
        }

        /// <summary>
        /// A ContentCatalog under the root — another profile's, or one made by hand — is refused by the scan and by the inspector's pick:
        /// built in, it would carry every asset it names into the directory (measured on 6000.6).
        /// </summary>
        [Test]
        public void E13_ACatalogUnderTheRoot_IsRefusedByTheScanAndThePick()
        {
            var catalog = ScriptableObject.CreateInstance<ContentCatalog>();
            AssetDatabase.CreateAsset(catalog, $"{k_TempRoot}/Nested.asset");
            m_TempAssets.Add($"{k_TempRoot}/Nested.asset");
            var plain = CreateTempAsset($"{k_TempRoot}/plain.txt");
            ContentDirectoryBuilder.SourceRoot = k_TempRoot;
            ContentLinkEditorUtility.Invalidate();

            LogAssert.Expect(LogType.Error, new Regex(Regex.Escape($"'{k_TempRoot}/Nested.asset' is a ContentCatalog under the source root '{k_TempRoot}'")));
            var scanned = new List<ContentDirectoryBuilder.Scanned>();
            Assert.That(ContentDirectoryBuilder.Scan(k_TempRoot, scanned), Is.False, "the scan took the catalog as content");
            Assert.That(scanned.Any(e => !e.IsScene && e.Entry.name == "Nested"), Is.False, "the catalog was named");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(catalog, out var name, out var reason), Is.False, $"the pick named it '{name}'");
            Assert.That(reason, Does.Contain($"'{k_TempRoot}/Nested.asset' is a ContentCatalog"));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("Nested"), Is.Null, "the path fallback answered the catalog");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(plain, out var plainName, out var plainReason), Is.True, plainReason);
            Assert.That(plainName, Is.EqualTo("plain"));
        }
    }
}
