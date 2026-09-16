using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Build profiles stored in ProjectSettings. The singleton is real and its file path is fixed at compile
    /// time (FilePath), so the one test that saves backs the file up first and puts it back; everything else works
    /// on the in-memory instance, snapshotted as JSON in SetUp and restored in TearDown.
    /// </summary>
    [Category("Build")]
    public class ContentBuildSettingsTests
    {
        const string k_File = "ProjectSettings/ContentLinkBuild.asset";
        const string k_Generated = "Assets/TestContent/Generated";
        const string k_RootA = "Assets/TestContent/A";
        const string k_RootSceneCase = "Assets/TestContent/SceneCase";

        string m_Snapshot;
        bool m_FileExisted;
        byte[] m_FileBackup;
        string m_TempCatalog;

        [SetUp]
        public void Snapshot()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_FileExisted = File.Exists(k_File);
            m_FileBackup = m_FileExisted ? File.ReadAllBytes(k_File) : null;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            m_TempCatalog = $"{k_Generated}/SettingsTest_{System.Guid.NewGuid():N}.asset";
        }

        [TearDown]
        public void Restore()
        {
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            if (m_FileExisted) File.WriteAllBytes(k_File, m_FileBackup);
            else if (File.Exists(k_File)) File.Delete(k_File);
            ContentDirectoryBuilder.ClearOverrides();
            AssetDatabase.DeleteAsset(m_TempCatalog);
            ContentLinkEditorUtility.Invalidate();
        }

        static ContentBuildSettings.CatalogEntry Category(string subFolder, string createAt)
            => new ContentBuildSettings.CatalogEntry { SubFolder = subFolder, CreateAt = createAt };

        // ───────────── a project without settings keeps building what it built ─────────────

        [Test]
        public void T1_NoSettings_MigratesTheLegacyStaticsIntoMain()
        {
            EditorJsonUtility.FromJsonOverwrite("{\"m_Profiles\":[],\"m_ActiveName\":\"\"}", ContentBuildSettings.instance);

            var main = ContentBuildSettings.instance.Active;
            Assert.That(main.Name, Is.EqualTo("Main"));
            Assert.That(main.SourceRoot, Is.EqualTo(ContentDirectoryBuilder.DefaultSourceRoot));
            Assert.That(main.OutputPath, Is.EqualTo(ContentDirectoryBuilder.DefaultOutputPath));
            Assert.That(main.Catalogs[0].ResolvePath(), Is.EqualTo(ContentDirectoryBuilder.DefaultCatalogPath));
            Assert.That(main.Archive, Is.EqualTo(ContentBuildSettings.ArchiveMode.None),
                "the legacy build was flat and uncompressed — a migrated project must keep building what it built");
            Assert.That(ContentDirectoryBuilder.SourceRoot, Is.EqualTo(ContentDirectoryBuilder.DefaultSourceRoot), "the static view reads the migrated profile");
            Assert.That(ContentDirectoryBuilder.CatalogPath, Is.EqualTo(ContentDirectoryBuilder.DefaultCatalogPath));
        }

        [Test]
        public void T2_NewProfile_GetsTheShippingDefaults()
        {
            var p = ContentBuildSettings.instance.Add("Extra");
            Assert.That(p, Is.Not.Null);
            Assert.That(p.OutputPath, Is.EqualTo("Build/Content/Extra"), "the default output is outside Assets/");
            Assert.That(p.Catalogs[0].ResolvePath(), Is.EqualTo("Assets/ContentCatalogs/Extra.asset"), "the default catalog is outside the source root");
            Assert.That(p.Archive, Is.EqualTo(ContentBuildSettings.ArchiveMode.Packed));
            Assert.That(p.MaxArchiveMB, Is.EqualTo(20));
            Assert.That(p.Compression, Is.EqualTo(ContentBuildSettings.CompressionKind.LZ4));
            Assert.That(p.FailBuildWhenErrorsLogged, Is.True);
            Assert.That(p.Target, Is.EqualTo(BuildTarget.NoTarget));
            Assert.That(ContentBuildSettings.instance.Add("Extra"), Is.Null, "profile names are unique");
        }

        // ───────────── validation: what the name check alone cannot see ─────────────

        [Test]
        public void T3_Validate_Overlap_SelfContainment_DuplicateName_Target()
        {
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("V");
            p.SourceRoot = k_RootA;

            p.Catalogs = new[] { Category("Icons", $"{k_Generated}/v1.asset"), Category("Icons/Sub", $"{k_Generated}/v2.asset") };
            Assert.That(settings.Validate(p), Has.Some.Contains("overlap"), "a sub folder inside another puts the same asset in two catalogs");

            p.Catalogs = new[] { Category("", $"{k_Generated}/v1.asset"), Category("Icons", $"{k_Generated}/v2.asset") };
            Assert.That(settings.Validate(p), Has.Some.Contains("overlap"), "the whole root covers every sub folder");

            p.Catalogs = new[] { Category("", $"{k_RootA}/Catalog.asset") };
            Assert.That(settings.Validate(p), Has.Some.Contains("catalogue itself"), "a catalog under its own root ends up in itself");

            p.Catalogs = new[] { Category("", $"{k_Generated}/v1.asset") };
            Assert.That(settings.Validate(p), Is.Empty, "a clean profile validates");

            var q = settings.Add("V2");
            q.Name = "V";
            Assert.That(settings.Validate(p), Has.Some.Contains("used 2 times"));
            q.Name = "V2";

            p.Target = EditorUserBuildSettings.activeBuildTarget == BuildTarget.StandaloneOSX ? BuildTarget.StandaloneWindows64 : BuildTarget.StandaloneOSX;
            Assert.That(settings.Validate(p), Has.Some.Contains("active build target"), "a profile pinned to another target must refuse");
        }

        [Test]
        public void T9_SourceRootStoredWithATrailingSeparator_IsRefusedByName()
        {
            // IsValidFolder accepts "Assets/X/" (measured on 6000.6), but the name rule cuts names from the root's length,
            // so a stored trailing separator would build a catalog whose names all lost their first character.
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("Slash");
            p.SourceRoot = k_RootA + "/";
            p.Catalogs = new[] { Category("", $"{k_Generated}/slash.asset") };
            Assume.That(AssetDatabase.IsValidFolder(p.SourceRoot), Is.True, "the engine accepts the trailing separator — that is why validation has to refuse it");
            Assert.That(settings.Validate(p), Has.Some.Contains("separator").And.Some.Contains($"'{k_RootA}'"), "the stored root must be refused with its normalised form");
            p.SourceRoot = k_RootA;
            Assert.That(settings.Validate(p), Is.Empty);
        }

        [Test]
        public void T11_ProfileNameWithAPathCharacter_IsRefusedByValidate()
        {
            // The name goes into archive file names and the default output and catalog paths, so a path character
            // would fail much later ("archive name … is not a plain file name"); validation says so up front.
            var settings = ContentBuildSettings.instance;
            var bad = settings.Add("bad/name");
            Assume.That(bad, Is.Not.Null);
            bad.SourceRoot = k_RootA;
            bad.Catalogs = new[] { Category("", $"{k_Generated}/badname.asset") };
            Assert.That(settings.Validate(bad), Has.Some.Contains("cannot be in a file name"), "a path character in the profile name must be refused by name");
            var good = settings.Add("good-name_1");
            good.SourceRoot = k_RootA;
            good.Catalogs = new[] { Category("", $"{k_Generated}/goodname.asset") };
            Assert.That(settings.Validate(good), Is.Empty);
        }

        [Test]
        public void T10_OutputPathAndSourceRoot_MustNotContainEachOther()
        {
            // An output inside the root is imported by the build's refresh, and the next build scans the built files
            // as content: measured 9 → 28 catalog entries and 19 → 38 files, without a warning. An output that
            // contains the root would be cleared with the root inside it.
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("Nest");
            p.SourceRoot = k_RootA;
            p.Catalogs = new[] { Category("", $"{k_Generated}/nest.asset") };
            p.OutputPath = k_RootA + "/Build";
            Assert.That(settings.Validate(p), Has.Some.Contains("inside the source root"), "a relative output under the root must be refused");
            p.OutputPath = Path.GetFullPath(k_RootA + "/Build");
            Assert.That(settings.Validate(p), Has.Some.Contains("inside the source root"), "the same output written as an absolute path must be refused too");
            p.OutputPath = "Assets/TestContent";
            Assert.That(settings.Validate(p), Has.Some.Contains("contains the source root"), "an output that contains the root must be refused");
            p.OutputPath = k_RootA;
            Assert.That(settings.Validate(p), Is.Not.Empty, "an output equal to the root must be refused");
            p.OutputPath = k_RootA + "Build";   // Assets/TestContent/ABuild: a sibling whose name merely starts with the root's
            Assert.That(settings.Validate(p), Is.Empty, "a sibling is fine");
            p.OutputPath = "Build/Content/Nest";
            Assert.That(settings.Validate(p), Is.Empty, "a normal output is fine");
        }

        // ───────────── the static API is a view of the active profile ─────────────

        [Test]
        public void T4_StaticView_OverrideIsSessionOnly()
        {
            var before = ContentBuildSettings.instance.Active.SourceRoot;
            ContentDirectoryBuilder.SourceRoot = "Assets/TestContent/B";
            Assert.That(ContentDirectoryBuilder.SourceRoot, Is.EqualTo("Assets/TestContent/B"));
            Assert.That(ContentBuildSettings.instance.Active.SourceRoot, Is.EqualTo(before), "an override must never reach the profile");

            ContentDirectoryBuilder.ClearOverrides();
            Assert.That(ContentDirectoryBuilder.SourceRoot, Is.EqualTo(before));
        }

        [Test]
        public void T5_AProfileRootNamesAssets_BeforeItIsEverBuilt()
        {
            // The drawer's path rule must know a profile's root even though no catalog has been built from it yet:
            // SourceRoots() unions every profile root. SceneCase is never catalogued, and the static view
            // points elsewhere, so only the profile can supply the root.
            ContentDirectoryBuilder.SourceRoot = k_RootA;
            var p = ContentBuildSettings.instance.Add("SceneCase");
            p.SourceRoot = k_RootSceneCase;
            ContentLinkEditorUtility.Invalidate();

            var scene = AssetDatabase.LoadAssetAtPath<SceneAsset>($"{k_RootSceneCase}/Empty.unity");
            Assert.That(scene, Is.Not.Null);
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(scene, out var name, out var reason), Is.True, reason);
            Assert.That(name, Is.EqualTo("Empty"));
        }

        // ───────────── the settings file and the GUID reference ─────────────

        [Test]
        public void T8_TwoProfilesWithTheSameOutputFolder_AreRefused()
        {
            var a = ContentBuildSettings.instance.Add("SameA");
            var b = ContentBuildSettings.instance.Add("SameB");
            a.SourceRoot = k_RootA; b.SourceRoot = k_RootA;
            a.OutputPath = "Build/Content/Same";
            b.OutputPath = "./Build/Content/Same/";   // the same folder, spelled differently
            Assert.That(ContentBuildSettings.instance.Validate(a), Has.Some.Contains("used by 2 profiles"), "the simulation matches registrations by output folder, so two profiles must not share one");
            b.OutputPath = "Build/Content/Other";
            Assert.That(ContentBuildSettings.instance.Validate(a), Has.None.Contains("used by"));
        }

        [Test]
        public void T7_NoSettings_FindAndSetActiveSeeTheMigratedMain()
        {
            EditorJsonUtility.FromJsonOverwrite("{\"m_Profiles\":[],\"m_ActiveName\":\"\"}", ContentBuildSettings.instance);
            Assert.That(ContentBuildSettings.instance.Find("Main"), Is.Not.Null, "asking by name first (the CLI does) must migrate too");
            Assert.That(ContentBuildSettings.instance.SetActive("Main"), Is.True);
        }

        [Test]
        public void T6_Save_WritesProjectSettings_AndStoresTheCatalogAsAGuid()
        {
            var catalog = ScriptableObject.CreateInstance<ContentCatalog>();
            AssetDatabase.CreateAsset(catalog, m_TempCatalog);
            var guid = AssetDatabase.AssetPathToGUID(m_TempCatalog);
            Assume.That(guid, Is.Not.Empty);

            var p = ContentBuildSettings.instance.Add("Saved");
            p.Catalogs[0].Catalog.asset = catalog;
            Assert.That(p.Catalogs[0].ResolvePath(), Is.EqualTo(m_TempCatalog), "a set reference resolves to the asset's path");

            ContentBuildSettings.instance.SaveSettings();

            Assert.That(File.Exists(k_File), Is.True, "Save(true) must write the ProjectSettings file");
            var text = File.ReadAllText(k_File);
            Assert.That(text, Does.Contain("Saved"));
            Assert.That(text, Does.Contain(guid), "the catalog is stored by GUID, so moving the asset does not break the profile");
        }
    }
}
