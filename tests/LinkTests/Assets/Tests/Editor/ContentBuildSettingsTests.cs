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

        /// <summary>T27: a root gives an asset one name, so profiles on one root must agree on the style; a value the enum does not define is refused by name.</summary>
        [Test]
        public void T12_ProfilesOnOneRoot_MustAgreeOnTheNameStyle()
        {
            var a = ContentBuildSettings.instance.Add("StyleA");
            var b = ContentBuildSettings.instance.Add("StyleB");
            a.SourceRoot = k_RootSceneCase; a.OutputPath = "Build/Content/StyleA";
            b.SourceRoot = k_RootSceneCase.ToLowerInvariant(); b.OutputPath = "Build/Content/StyleB";   // the same folder in another case
            b.NameStyle = ContentNameStyle.PathWithExtension;
            Assert.That(ContentBuildSettings.instance.Validate(a), Has.Some.Contains("a root gives an asset one name"));
            Assert.That(ContentBuildSettings.instance.Validate(b), Has.Some.Contains("a root gives an asset one name"));
            b.NameStyle = ContentNameStyle.PathWithoutExtension;
            Assert.That(ContentBuildSettings.instance.Validate(a), Has.None.Contains("a root gives an asset one name"), "one root, one style");
            b.SourceRoot = k_RootA; b.NameStyle = (ContentNameStyle)7;   // a root nobody else uses: the same-root check would answer instead
            Assert.That(ContentBuildSettings.instance.Validate(b), Has.Some.Contains("name style 7 is not a ContentNameStyle"));
        }

        [Test]
        public void T13_APackedProfileName_TakesOnlyWhatAnArchiveNameCanCarry()
        {
            // A packed profile's name goes into its archive names, its remote folder and its cache folder; a flat one keeps any file name.
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("Stage#1");
            Assume.That(p, Is.Not.Null);
            p.SourceRoot = k_RootA;
            p.Catalogs = new[] { Category("", $"{k_Generated}/stage1.asset") };
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            Assert.That(settings.Validate(p), Has.Some.Contains("archive name cannot carry"), "a packed profile named after a URL character");
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            Assert.That(settings.Validate(p), Is.Empty, "a flat profile keeps its name");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            Assert.That(settings.Validate(p), Has.Some.Contains("archive name cannot carry"), "packed again");
        }

        [Test]
        public void T14_ASourceRootInAnotherCase_IsRefusedWithTheDiskSpelling()
        {
            // The AssetDatabase finds a folder in any case and answers with the disk's spelling (measured on 6000.6), but the content
            // tools compare the root as written: a root in another case validated, then named nothing under it.
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("Case");
            p.Catalogs = new[] { Category("", $"{k_Generated}/case.asset") };
            foreach (var written in new[] { "Assets/TestContent/a", "assets/TestContent/A", "Assets/testcontent/A" })
            {
                p.SourceRoot = written;
                Assume.That(AssetDatabase.IsValidFolder(written), Is.True, "the engine finds the folder in any case — that is why validation has to refuse it");
                Assert.That(settings.Validate(p), Has.Some.Contains($"source root '{written}' is spelled '{k_RootA}' in the project — write it as '{k_RootA}'"), "the root must be refused with its spelling on disk");
            }
            p.SourceRoot = k_RootA;
            Assert.That(settings.Validate(p), Is.Empty);
        }

        [Test]
        public void T15_ACategoryInAnotherCase_IsRefused_BeforeACatalogIsWritten()
        {
            // Matched as written, a category written 'icons' would leave out every asset under Icons/, with one warning.
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("CaseCategory");
            p.SourceRoot = k_RootA;
            p.Catalogs = new[] { Category("icons", m_TempCatalog) };
            Assume.That(AssetDatabase.IsValidFolder($"{k_RootA}/icons"), Is.True);
            Assert.That(settings.Validate(p), Has.Some.Contains("category 'icons' is spelled 'Icons' in the project — write it as 'Icons'"), "the category must be refused with its spelling on disk");
            Assert.That(ContentBuildRunner.UpdateCatalogs(p, new System.Collections.Generic.List<string>()), Is.False, "a catalog update runs the validation first");
            Assert.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(m_TempCatalog), Is.Null, "nothing is written");
            p.Catalogs = new[] { Category("Icons", m_TempCatalog) };
            Assert.That(settings.Validate(p), Is.Empty);
        }

        /// <summary>
        /// What the command line could never build, or what the build would find only at its end, is refused
        /// before anything is written — 'all' as a name, a mode by a number it does not have, a post-processor it cannot find.
        /// </summary>
        [Test]
        public void T16_AllAsAName_AModeItDoesNotHave_AndAPostProcessorNotFound_AreRefused()
        {
            var settings = ContentBuildSettings.instance;
            var p = settings.Add("All");
            Assume.That(p, Is.Not.Null);
            p.SourceRoot = k_RootA;
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            p.Catalogs = new[] { Category("", m_TempCatalog) };
            Assert.That(settings.Validate(p), Has.Some.Contains("is the command line's word for every profile"), "'-contentlink.profile all' could never build it alone");
            p.Name = "Allx";
            Assert.That(settings.Validate(p), Is.Empty);

            p.Archive = (ContentBuildSettings.ArchiveMode)7; p.Compression = (ContentBuildSettings.CompressionKind)9;   // a hand edit, or a later package's value
            Assert.That(settings.Validate(p), Has.Some.Contains("archive mode 7 is not an ArchiveMode"));
            Assert.That(settings.Validate(p), Has.Some.Contains("compression 9 is not a CompressionKind"));
            p.Archive = ContentBuildSettings.ArchiveMode.None; p.Compression = ContentBuildSettings.CompressionKind.LZ4;

            p.PostProcessors = new[] { ContentBuildWindowModel.NameOf(typeof(ContentArchivePackerTests.RecordA)), "Nope.Missing", "A.B, C.D, E" };
            var errors = settings.Validate(p);
            Assert.That(errors, Has.Some.Contains("post-processor 'Nope.Missing' was not found"), "found before the build, not after the engine");
            Assert.That(errors, Has.Some.Contains("post-processor 'A.B, C.D, E' was not found"), "a malformed assembly part resolves to nothing — it does not throw");
            Assert.That(errors, Has.None.Contains("RecordA"), "the stored form resolves");
            Assert.That(ContentBuildRunner.UpdateCatalogs(p, new System.Collections.Generic.List<string>()), Is.False, "a catalogs-only update refuses it too");
            Assert.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(m_TempCatalog), Is.Null, "nothing is written");
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
