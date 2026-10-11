using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// The build runner against the real pipeline on TestContent/A. Builds land under
    /// StreamingAssets/ContentLinkTests/Runner so the PlayMode side (ProfileBuildTests) can register them; the
    /// folder is gitignored. Settings are snapshotted and restored, the temp catalogs deleted.
    /// </summary>
    [Category("Build")]
    public class ContentBuildRunnerTests
    {
        public const string RootA = "Assets/TestContent/A";
        public const string OutputFolder = "Assets/StreamingAssets/ContentLinkTests/Runner";
        const string k_Generated = "Assets/TestContent/Generated";

        string m_Snapshot, m_SourceRoot, m_CatalogPath, m_OutputPath;
        readonly List<string> m_TempCatalogs = new();

        [SetUp]
        public void Snapshot()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            m_OutputPath = ContentDirectoryBuilder.OutputPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
        }

        [TearDown]
        public void Restore()
        {
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            ContentDirectoryBuilder.ClearOverrides();
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            ContentDirectoryBuilder.OutputPath = m_OutputPath;
            foreach (var path in m_TempCatalogs) AssetDatabase.DeleteAsset(path);
            m_TempCatalogs.Clear();
            ContentLinkEditorUtility.Invalidate();
        }

        ContentBuildSettings.Profile Profile(string name, params string[] subFolders)
        {
            var p = ContentBuildSettings.instance.Add(name);
            Assert.That(p, Is.Not.Null);
            p.SourceRoot = RootA;
            p.OutputPath = $"{OutputFolder}/{name}";
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            var catalogs = new ContentBuildSettings.CatalogEntry[subFolders.Length];
            for (int i = 0; i < subFolders.Length; ++i)
            {
                var path = $"{k_Generated}/Runner_{name}_{i}_{System.Guid.NewGuid():N}.asset";
                catalogs[i] = new ContentBuildSettings.CatalogEntry { SubFolder = subFolders[i], CreateAt = path };
                m_TempCatalogs.Add(path);
            }
            p.Catalogs = catalogs;
            return p;
        }

        /// <summary>Serialized files that carry content: every catalog is a serialized file of its own, so the roots are left out.</summary>
        static int ContentFiles(ContentBuildResult r)
        {
            int n = 0;
            foreach (var f in r.Manifest.SerializedFiles) if (!f.IsBuiltIn) ++n;
            return n - r.Manifest.RootAssets.Length;
        }

        [Test]
        public void R1_TwoCategories_BuildIntoOneDirectory_WithTwoRootCatalogs()
        {
            var p = Profile("Two", "Icons", "Prefabs");
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));

            Assert.That(r.Manifest, Is.Not.Null, "the manifest was not read — the mapping and the post-processors would be blind");
            Assert.That(r.Manifest.RootAssets, Has.Length.EqualTo(2), "one directory, two root catalogs");
            // RootAssets are not asset GUIDs: they are the object-id hashes of the root objects, the same key the
            // Loadables use (measured here — the first draft compared GUIDs and was wrong).
            foreach (var entry in p.Catalogs)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(entry.ResolvePath());
                var hash = ContentBuildRunner.ObjectIdHash(LoadableObjectIdEditorUtility.CreateLoadableObjectId(catalog));
                Assert.That(r.Manifest.RootAssets, Does.Contain(hash), $"RootAssets should carry the catalog's object-id hash ({hash}), not its GUID");
            }

            var icons = AssetDatabase.LoadAssetAtPath<ContentCatalog>(p.Catalogs[0].ResolvePath());
            var prefabs = AssetDatabase.LoadAssetAtPath<ContentCatalog>(p.Catalogs[1].ResolvePath());
            Assert.That(icons.SourceRoot, Is.EqualTo(RootA), "every catalog records the profile root");
            Assert.That(prefabs.SourceRoot, Is.EqualTo(RootA));
            var iconNames = new List<string>(); foreach (var e in icons.Entries) iconNames.Add(e.name);
            var prefabNames = new List<string>(); foreach (var e in prefabs.Entries) prefabNames.Add(e.name);
            Assert.That(iconNames, Does.Contain("Icons/single"), "names stay root-relative");
            Assert.That(iconNames, Has.None.StartsWith("Prefabs/"), "a category only takes its own sub folder");
            Assert.That(prefabNames, Does.Contain("Prefabs/Plain"));
            Assert.That(prefabNames, Has.None.StartsWith("Icons/"));
            Assert.That(iconNames, Has.None.EqualTo("Materials/Plain"), "assets outside every category are not built");

            // The catalog ids and the manifest's loadables share one hash (the "oid-…" in LoadableObjectId.ToString()):
            // every entry of both catalogs is listed as built. A loadable → disk file link does not exist — the
            // manifest's serialized-file ids are reference names, none of which matches a file (measured here).
            int entries = icons.Entries.Count + prefabs.Entries.Count;
            Assert.That(r.BuiltNames, Has.Count.EqualTo(entries),
                $"{r.BuiltNames.Count} of {entries} catalog names appear in the manifest — the oid in LoadableObjectId.ToString() does not match the manifest's ObjectIdHash");
            Assert.That(r.BuiltNames, Does.Contain("Icons/single").And.Contain("Prefabs/Plain"));
            int onDisk = 0;
            foreach (var f in r.Manifest.SerializedFiles)
                if (!f.IsBuiltIn && File.Exists(Path.Combine(p.OutputPath, f.ID.Replace(".cfid", ".cf")))) ++onDisk;
            Debug.Log($"[ContentLink.Tests] Two: {r.Files.Length} files, {r.BuiltNames.Count} names built, serialized-file ids that match a disk file: {onDisk} of {r.Manifest.ContentFileCount()}");
            Assert.That(onDisk, Is.EqualTo(0), "a serialized-file id matched a disk file — then a loadable → disk file mapping exists after all, and the runner could record it");
        }

        [Test]
        public void R2_SplittingIntoCategories_DoesNotDuplicateSharedFiles()
        {
            // One BuildContentDirectory call, so a file referenced from two categories is written once. Compare
            // the whole root as one category with the same root as four categories: same number of serialized files.
            var one = Profile("One", "");
            var split = Profile("Split", "Icons", "Prefabs", "Materials", "Audio");
            var rOne = ContentBuildRunner.Build(one, showProgress: false);
            var rSplit = ContentBuildRunner.Build(split, showProgress: false);
            Assert.That(rOne.Succeeded && rSplit.Succeeded, Is.True, string.Join("\n", rOne.Errors) + string.Join("\n", rSplit.Errors));
            // Measured: four categories = 17 serialized files, one category = 14 — the three extra are the three extra
            // catalog assets, each a serialized file of its own. Content files are equal, so nothing was duplicated.
            Debug.Log($"[ContentLink.Tests] content files (minus root catalogs): one category={ContentFiles(rOne)} four categories={ContentFiles(rSplit)} (roots {rOne.Manifest.RootAssets.Length} vs {rSplit.Manifest.RootAssets.Length})");
            Assert.That(ContentFiles(rSplit), Is.EqualTo(ContentFiles(rOne)), "splitting into categories duplicated content files");
            Assert.That(rSplit.BuiltNames, Is.EqualTo(rOne.BuiltNames), "the same names are built either way");
        }

        [Test]
        public void R3_EngineMode_WritesTheEngineArchive_AndTypeTreeOptionBuilds()
        {
            var p = Profile("Engine", "");
            p.Archive = ContentBuildSettings.ArchiveMode.Engine;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            p.DisableWriteTypeTree = true;   // must be paired with SerializeUnityVersion by the runner (the engine refuses the flag alone)
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            Assert.That(r.Archives, Is.EqualTo(new[] { "content0.archive" }), "the engine archive mode writes one archive");
            Assert.That(r.Manifest, Is.Null, "the engine keeps the manifest inside the archive, so there is nothing loose to read");
            Assert.That(r.BuiltNames, Is.Empty);
        }

        [Test]
        public void R4_PackedMode_PacksTheOutput_AndOutputSafetyRefusesAStrangerFolder()
        {
            var p = Profile("Packed", "");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            Assert.That(r.Archives, Is.Not.Empty, "the packer turns the flat engine build into archives");
            Assert.That(File.Exists(Path.Combine(p.OutputPath, ContentArchives.IndexFile)), Is.True);
            Assert.That(r.Manifest, Is.Not.Null, "read before packing");

            var stranger = $"{OutputFolder}/Stranger";
            Directory.CreateDirectory(stranger);
            File.WriteAllText(Path.Combine(stranger, "keep.txt"), "not a content build");
            var q = Profile("Stranger", "");
            LogAssert.Expect(LogType.Error, new Regex("build of 'Stranger' failed"));   // the runner reports a refused build as an error
            var rq = ContentBuildRunner.Build(q, showProgress: false);
            Assert.That(rq.Succeeded, Is.False);
            Assert.That(rq.Errors, Has.Some.Contains("refusing"), "a folder that is not a content build must be refused, not cleared");
            Assert.That(File.Exists(Path.Combine(stranger, "keep.txt")), Is.True, "the stranger's file must survive");
            Directory.Delete(stranger, true);
            if (File.Exists(stranger + ".meta")) File.Delete(stranger + ".meta");

            Assert.That(SessionState.GetString(ContentBuildRunner.SessionKey + ".profile", ""), Is.EqualTo("Stranger"), "the last result is in SessionState, never in the settings file");
            Assert.That(SessionState.GetBool(ContentBuildRunner.SessionKey + ".ok", true), Is.False);
        }

        /// <summary>
        /// A profile added with the shipping defaults keeps its catalog at Assets/ContentCatalogs/&lt;Name&gt;.asset — a folder
        /// nothing in the project creates. The first build must make it (and any deeper folder a category names);
        /// the engine's CreateAsset throws otherwise. The default folder is left alone when the project already had it.
        /// </summary>
        [Test]
        public void R7_FreshProfile_DefaultCatalogPath_BuildsOnFirstRun()
        {
            var folder = ContentBuildWindowModel.CatalogFolder;
            bool folderExisted = AssetDatabase.IsValidFolder(folder);
            var p = ContentBuildSettings.instance.Add("Fresh");
            Assert.That(p, Is.Not.Null);
            p.SourceRoot = RootA;
            p.OutputPath = $"{OutputFolder}/Fresh";
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            var createAt = p.Catalogs[0].CreateAt;   // the default, untouched
            Assert.That(createAt, Is.EqualTo($"{folder}/Fresh.asset"));
            // a second category in a folder that certainly does not exist, so folder creation is checked even where the default folder already is
            var deep = $"{folder}/Probe_{System.Guid.NewGuid():N}/Fresh.Icons.asset";
            var deepFolder = Path.GetDirectoryName(deep).Replace('\\', '/');
            p.Catalogs[0].SubFolder = "Prefabs";
            p.Catalogs = new[] { p.Catalogs[0], new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = deep } };
            m_TempCatalogs.Add(createAt); m_TempCatalogs.Add(deep);
            try
            {
                var r = ContentBuildRunner.Build(p, showProgress: false);
                Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
                Assert.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(createAt), Is.Not.Null, "the catalog exists at the default path");
                Assert.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(deep), Is.Not.Null, "a two-level missing folder is created too");
                Assert.That(p.Catalogs[0].Catalog.isSet && p.Catalogs[1].Catalog.isSet, Is.True, "the profile references both created catalogs");
                Assert.That(AssetDatabase.IsValidFolder(deepFolder), Is.True, "the folder was made through the AssetDatabase, so it is imported at once");
            }
            finally
            {
                if (AssetDatabase.IsValidFolder(deepFolder)) AssetDatabase.DeleteAsset(deepFolder);
                else if (Directory.Exists(deepFolder)) { Directory.Delete(deepFolder, true); if (File.Exists(deepFolder + ".meta")) File.Delete(deepFolder + ".meta"); }
                if (!folderExisted)
                {
                    if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                    else if (Directory.Exists(folder)) { Directory.Delete(folder, true); if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta"); }
                }
            }
        }

        /// <summary>
        /// A catalog folder that is on disk but not imported yet (made by a tool, no refresh): the build imports it
        /// instead of creating it again — CreateFolder on such a folder makes a "Name 1" next to it (measured on 6000.6).
        /// </summary>
        [Test]
        public void R8_CatalogFolderOnDiskButNotImported_IsImportedNotDuplicated()
        {
            var diskFolder = $"{k_Generated}/Disk_{System.Guid.NewGuid():N}";
            Directory.CreateDirectory(diskFolder);   // no refresh: the AssetDatabase does not know it yet
            Assume.That(AssetDatabase.IsValidFolder(diskFolder), Is.False, "the folder must start out unimported");
            var p = Profile("Disk", "Icons");
            var createAt = $"{diskFolder}/Disk.Icons.asset";
            p.Catalogs[0].CreateAt = createAt;
            m_TempCatalogs.Add(createAt);
            try
            {
                var r = ContentBuildRunner.Build(p, showProgress: false);
                Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
                Assert.That(AssetDatabase.IsValidFolder(diskFolder), Is.True, "the folder was imported");
                Assert.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(createAt), Is.Not.Null);
                Assert.That(Directory.Exists(diskFolder + " 1"), Is.False, "no duplicate folder was created next to it");
            }
            finally
            {
                AssetDatabase.DeleteAsset(createAt);   // the asset first: a folder imported mid-build does not always delete with its contents
                foreach (var f in new[] { diskFolder, diskFolder + " 1" })
                {
                    if (AssetDatabase.IsValidFolder(f)) AssetDatabase.DeleteAsset(f);
                    if (Directory.Exists(f)) Directory.Delete(f, true);
                    if (File.Exists(f + ".meta")) File.Delete(f + ".meta");
                }
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// A build that fails before the engine runs (here: a duplicate name found by the catalog stage) must leave the
        /// previous output usable — a Play with the simulation off keeps loading the last good build until the name is fixed.
        /// </summary>
        [Test]
        public void R9_CatalogStageFailure_LeavesThePreviousOutputUsable()
        {
            var p = Profile("Keep", "");
            var first = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(first.Succeeded, Is.True, string.Join("\n", first.Errors));
            var hash = Path.Combine(p.OutputPath, ContentBuildManifest.HashFile);
            Assume.That(File.Exists(hash), Is.True);
            int files = Directory.GetFileSystemEntries(p.OutputPath).Length;

            const string duplicate = RootA + "/Icons/single.txt";   // "Icons/single" twice: the png is already there
            File.WriteAllText(duplicate, "a second asset with the same name");
            AssetDatabase.ImportAsset(duplicate);
            bool ignore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;   // the duplicate and the failed build are both reported as errors
            try
            {
                var second = ContentBuildRunner.Build(p, showProgress: false);
                Assert.That(second.Succeeded, Is.False);
                Assert.That(second.Errors, Has.Some.Contains("duplicate"));
                Assert.That(File.Exists(hash), Is.True, "the previous build's manifest must survive a failure that happened before the engine ran");
                Assert.That(Directory.GetFileSystemEntries(p.OutputPath).Length, Is.EqualTo(files), "the previous output must be untouched");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = ignore;
                AssetDatabase.DeleteAsset(duplicate);
                if (File.Exists(duplicate)) File.Delete(duplicate);
                if (File.Exists(duplicate + ".meta")) File.Delete(duplicate + ".meta");
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// T27: the catalogs record the profile's name style, and a rewrite says so — once — only when it changes the
        /// style of a catalog that holds names: the old names stop resolving.
        /// </summary>
        [Test]
        public void R10_TheCatalogsRecordTheProfilesNameStyle_AndOnlyASwitchWarns()
        {
            var p = Profile("Style", "");
            var warnings = new List<string>();
            void Listen(string message, string stack, LogType type) { if (type == LogType.Warning && message.Contains("the names change from")) warnings.Add(message); }
            Application.logMessageReceived += Listen;
            try
            {
                var errors = new List<string>();
                int Update(ContentNameStyle style)
                {
                    p.NameStyle = style;
                    int before = warnings.Count;
                    Assert.That(ContentBuildRunner.UpdateCatalogs(p, errors), Is.True, string.Join("\n", errors));
                    return warnings.Count - before;
                }
                int first = Update(ContentNameStyle.PathWithoutExtension);
                int switched = Update(ContentNameStyle.PathWithExtension);
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(p.Catalogs[0].ResolvePath());
                var recorded = catalog.NameStyle;
                bool named = false;
                foreach (var e in catalog.Entries) named |= e.name == "Icons/single.png";
                int same = Update(ContentNameStyle.PathWithExtension);
                int back = Update(ContentNameStyle.PathWithoutExtension);
                Assert.That(recorded, Is.EqualTo(ContentNameStyle.PathWithExtension), "the catalog records the profile's style");
                Assert.That(named, Is.True, "and its names are made with it");
                Assert.That(new[] { first, switched, same, back }, Is.EqualTo(new[] { 0, 1, 0, 1 }), "warnings: the first write · a switch · the same style again · back");
            }
            finally { Application.logMessageReceived -= Listen; }
        }

        static string Names(ContentCatalog c)
        {
            var names = new List<string>();
            foreach (var e in c.Entries) names.Add(e.name);
            return string.Join(",", names);
        }

        /// <summary>
        /// Two categories on one catalog stop the build at validation, so the catalog and the previous build stay.
        /// The engine refuses one root asset given twice as well, but only after the catalog stage has rewritten the catalog
        /// and the output folder has been cleared (measured on 6000.6).
        /// </summary>
        [Test]
        public void R11_TwoCategoriesOnOneCatalog_AreRefusedBeforeAnythingIsWritten()
        {
            var p = Profile("Twice", "Icons", "Prefabs");
            var first = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(first.Succeeded, Is.True, string.Join("\n", first.Errors));
            var hash = Path.Combine(p.OutputPath, ContentBuildManifest.HashFile);
            int files = Directory.GetFileSystemEntries(p.OutputPath).Length;
            var catalog = p.Catalogs[0].Catalog.asset;
            var before = Names(catalog);
            Assume.That(before.Length, Is.GreaterThan(0));
            p.Catalogs[1].Catalog.asset = catalog;   // the second category picks the first one's catalog

            LogAssert.Expect(LogType.Error, new Regex("build of 'Twice' failed"));
            var second = ContentBuildRunner.Build(p, showProgress: false);
            Debug.Log($"[ContentLink.Tests] R11: succeeded={second.Succeeded} output {files}→{Directory.GetFileSystemEntries(p.OutputPath).Length} catalog [{before}]→[{Names(catalog)}] errors={string.Join(" | ", second.Errors)}");
            Assert.That(second.Succeeded, Is.False);
            Assert.That(second.Errors, Has.Some.Contains("write the same catalog"));
            Assert.That(Names(catalog), Is.EqualTo(before), "the catalog keeps its names");
            Assert.That(File.Exists(hash), Is.True, "the previous build's manifest stays");
            Assert.That(Directory.GetFileSystemEntries(p.OutputPath).Length, Is.EqualTo(files), "the previous output stays");
        }

        /// <summary>
        /// The static ContentDirectoryBuilder.Build guards its output folder as the runner does and removes what an archived build
        /// leaves that the engine does not — archives.json, archives.hash, and an archive's .meta (the engine takes the archive, not its .meta;
        /// measured on 6000.6) — and nothing else: a folder it shares with other files keeps them. In a folder that is no content build the
        /// engine would remove another tool's files of its kinds; that folder is refused.
        /// </summary>
        [Test]
        public void R14_TheStaticBuild_RemovesAPackedBuildsLeftovers_KeepsTheRest_AndRefusesAStrangerFolder()
        {
            var p = Profile("LegacyOver", "");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            Assume.That(File.Exists(Path.Combine(p.OutputPath, ContentArchives.IndexFile)), Is.True);
            var archives = Directory.GetFiles(p.OutputPath, "*.archive");
            Assume.That(archives.Length, Is.GreaterThan(0));
            Assume.That(File.Exists(archives[0] + ".meta"), Is.True, "the runner refreshed the imported output");
            var keep = Path.Combine(p.OutputPath, "keep.txt");
            var audio = Path.Combine(p.OutputPath, "Audio");
            File.WriteAllText(keep, "another tool's file");
            Directory.CreateDirectory(audio);
            File.WriteAllText(Path.Combine(audio, "bank.bytes"), "another tool's folder");

            var catalog = $"{k_Generated}/Legacy_{System.Guid.NewGuid():N}.asset";
            m_TempCatalogs.Add(catalog);
            ContentDirectoryBuilder.SourceRoot = RootA;
            ContentDirectoryBuilder.CatalogPath = catalog;
            ContentDirectoryBuilder.OutputPath = p.OutputPath;
            try
            {
                var report = ContentDirectoryBuilder.Build();
                Assert.That(report, Is.Not.Null);
                Assert.That(report.summary.result, Is.EqualTo(UnityEditor.Build.Reporting.BuildResult.Succeeded));
                Assert.That(File.Exists(Path.Combine(p.OutputPath, ContentArchives.IndexFile)), Is.False, "the packed build's index goes first");
                Assert.That(File.Exists(Path.Combine(p.OutputPath, ContentArchives.HashFile)), Is.False, "… and its hash");
                foreach (var a in archives)
                {
                    Assert.That(File.Exists(a), Is.False, "the archive is gone");
                    Assert.That(File.Exists(a + ".meta"), Is.False, "… with its .meta — the next refresh has nothing to warn of");
                }
                Assert.That(File.Exists(keep), Is.True, "another tool's file stays, as it always did");
                Assert.That(File.Exists(Path.Combine(audio, "bank.bytes")), Is.True, "… and its folder");
            }
            finally
            {
                File.Delete(keep);
                if (File.Exists(keep + ".meta")) File.Delete(keep + ".meta");
                Directory.Delete(audio, true);
                if (File.Exists(audio + ".meta")) File.Delete(audio + ".meta");
            }

            var stranger = $"{OutputFolder}/LegacyStranger";
            Directory.CreateDirectory(stranger);
            File.WriteAllText(Path.Combine(stranger, "keep.txt"), "not a content build");
            File.WriteAllText(Path.Combine(stranger, "mine.archive"), "another tool's archive");
            ContentDirectoryBuilder.OutputPath = stranger;
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("refusing to clear it"));
                Assert.That(ContentDirectoryBuilder.Build(), Is.Null, "refused");
                Assert.That(File.Exists(Path.Combine(stranger, "keep.txt")), Is.True);
                Assert.That(File.Exists(Path.Combine(stranger, "mine.archive")), Is.True, "the engine never ran in it");
            }
            finally
            {
                Directory.Delete(stranger, true);
                if (File.Exists(stranger + ".meta")) File.Delete(stranger + ".meta");
            }
        }

        /// <summary>
        /// The clear keeps the marker for the engine to write anew in place — but the engine cannot write a read-only one and
        /// fails the build ('Failed to write …BuildManifestHash.txt' — measured on 6000.6), so such a marker goes with the rest.
        /// Windows cannot delete a read-only file either; the clear names it there.
        /// </summary>
        [Test]
        public void R15_AReadOnlyMarker_GoesWithTheClear_AndTheBuildSucceeds()
        {
            Assume.That(Application.platform, Is.Not.EqualTo(RuntimePlatform.WindowsEditor));
            var p = Profile("ReadOnlyMarker", "");
            var first = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(first.Succeeded, Is.True, string.Join("\n", first.Errors));
            var marker = Path.Combine(p.OutputPath, ContentBuildManifest.HashFile);
            File.SetAttributes(marker, FileAttributes.ReadOnly);
            Assume.That((File.GetAttributes(marker) & FileAttributes.ReadOnly) != 0, Is.True);
            try
            {
                var second = ContentBuildRunner.Build(p, showProgress: false);
                Assert.That(second.Succeeded, Is.True, string.Join("\n", second.Errors));
                Assert.That((File.GetAttributes(marker) & FileAttributes.ReadOnly) == 0, Is.True, "the engine wrote a new marker");
            }
            finally
            {
                if (File.Exists(marker)) File.SetAttributes(marker, FileAttributes.Normal);
            }
        }

        /// <summary>A catalogs-only update refuses one catalog picked for two categories of a profile, and writes nothing.</summary>
        [Test]
        public void R12a_ACatalogsOnlyUpdate_RefusesOneCatalogPickedTwice()
        {
            var p = Profile("PickedTwice", "Icons", "Prefabs");
            var errors = new List<string>();
            Assert.That(ContentBuildRunner.UpdateCatalogs(p, errors), Is.True, string.Join("\n", errors));
            var catalog = p.Catalogs[0].Catalog.asset;
            var before = Names(catalog);
            Assume.That(before.Length, Is.GreaterThan(0));

            p.Catalogs[1].Catalog.asset = catalog;
            errors.Clear();
            var paths = new List<string>();
            bool ok = ContentBuildRunner.UpdateCatalogs(p, errors, paths);
            Debug.Log($"[ContentLink.Tests] R12a: ok={ok} paths={paths.Count} catalog [{before}]→[{Names(catalog)}] errors={string.Join(" | ", errors)}");
            Assert.That(ok, Is.False);
            Assert.That(errors, Has.Some.Contains("write the same catalog"));
            Assert.That(paths, Is.Empty);
            Assert.That(Names(catalog), Is.EqualTo(before), "the catalog keeps its names");
        }

        /// <summary>Another profile's category whose CreateAt names the catalog in another case is refused — the AssetDatabase would find the same asset — and nothing is written.</summary>
        [Test]
        public void R12b_ACatalogsOnlyUpdate_RefusesAnotherProfilesCategoryOnTheCatalog_InAnotherCase()
        {
            var p = Profile("CaseBase", "Icons");
            var errors = new List<string>();
            Assert.That(ContentBuildRunner.UpdateCatalogs(p, errors), Is.True, string.Join("\n", errors));
            var catalog = p.Catalogs[0].Catalog.asset;
            var before = Names(catalog);
            Assume.That(before.Length, Is.GreaterThan(0));

            var q = Profile("CaseOther", "Prefabs");
            var path = AssetDatabase.GetAssetPath(catalog);
            q.Catalogs[0].CreateAt = Path.GetDirectoryName(path).Replace('\\', '/') + "/" + Path.GetFileNameWithoutExtension(path).ToUpperInvariant() + ".asset";
            Assume.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(q.Catalogs[0].CreateAt), Is.SameAs(catalog), "the AssetDatabase finds the catalog by the other case");
            errors.Clear();
            bool ok = ContentBuildRunner.UpdateCatalogs(q, errors);
            Debug.Log($"[ContentLink.Tests] R12b: ok={ok} createAt={q.Catalogs[0].CreateAt} catalog [{before}]→[{Names(catalog)}] errors={string.Join(" | ", errors)}");
            Assert.That(ok, Is.False);
            Assert.That(errors, Has.Some.Contains("also written by profile 'CaseBase'"));
            Assert.That(Names(catalog), Is.EqualTo(before), "the catalog keeps its names");
        }

        /// <summary>A category whose picked catalog was deleted and which has no path to create one at is refused by validation, and the window cannot build.</summary>
        [Test]
        public void R13_ACategoryWhoseCatalogWasDeleted_AndHasNoPath_IsRefusedByValidation()
        {
            var p = Profile("Gone", "Icons");
            var errors = new List<string>();
            Assert.That(ContentBuildRunner.UpdateCatalogs(p, errors), Is.True, string.Join("\n", errors));
            var path = p.Catalogs[0].ResolvePath();
            p.Catalogs[0].CreateAt = "";   // a category made by picking a catalog
            Assert.That(ContentBuildSettings.instance.Validate(p), Is.Empty, "a picked catalog with no CreateAt is a catalog");
            AssetDatabase.DeleteAsset(path);
            var model = new ContentBuildWindowModel();
            model.Select(p.Name);
            var validation = ContentBuildSettings.instance.Validate(p);
            Debug.Log($"[ContentLink.Tests] R13: resolve='{p.Catalogs[0].ResolvePath()}' canBuild={model.CanBuild} errors={string.Join(" | ", validation)}");
            Assert.That(validation, Has.Some.Contains("has no catalog"));
            Assert.That(model.CanBuild, Is.False);
        }
    }
}
