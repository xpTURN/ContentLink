using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// The build window's logic without its IMGUI: a new profile, a category, the messages, the
    /// post-processor list, undo, and a build that reaches the runner. Every committed edit saves the settings file,
    /// so the file is backed up and put back like ContentBuildSettingsTests does.
    /// </summary>
    [Category("Build")]
    public class ContentBuildWindowModelTests
    {
        const string k_File = "ProjectSettings/ContentLinkBuild.asset";
        const string k_Generated = "Assets/TestContent/Generated";
        const string k_RootA = "Assets/TestContent/A";
        const string k_Output = "Assets/StreamingAssets/ContentLinkTests/Runner/Model";

        string m_Snapshot;
        bool m_FileExisted;
        byte[] m_FileBackup;
        readonly List<string> m_TempCatalogs = new();
        ContentBuildWindowModel m_Model;

        [SetUp]
        public void Snapshot()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_FileExisted = File.Exists(k_File);
            m_FileBackup = m_FileExisted ? File.ReadAllBytes(k_File) : null;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            m_Model = new ContentBuildWindowModel();
        }

        [TearDown]
        public void Restore()
        {
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            if (m_FileExisted) File.WriteAllBytes(k_File, m_FileBackup);
            else if (File.Exists(k_File)) File.Delete(k_File);
            ContentDirectoryBuilder.ClearOverrides();
            foreach (var path in m_TempCatalogs) AssetDatabase.DeleteAsset(path);
            m_TempCatalogs.Clear();
            ContentLinkEditorUtility.Invalidate();
        }

        string TempCatalog()
        {
            var path = $"{k_Generated}/Model_{System.Guid.NewGuid():N}.asset";
            m_TempCatalogs.Add(path);
            return path;
        }

        [Test]
        public void W1_AddProfile_SelectsIt_AndEveryCommittedEditSavesTheFile()
        {
            Assert.That(m_Model.AddProfile("Win"), Is.Null);
            Assert.That(m_Model.SelectedName, Is.EqualTo("Win"));
            Assert.That(m_Model.SelectedIsActive, Is.False, "adding does not activate");
            Assert.That(File.Exists(k_File), Is.True, "a committed edit saves the settings file");
            Assert.That(File.ReadAllText(k_File), Does.Contain("Win"));
            Assert.That(m_Model.AddProfile("Win"), Does.Contain("exists"));
            Assert.That(m_Model.AddProfile(" "), Does.Contain("empty"));

            m_Model.Edit(x => x.MaxArchiveMB = 7, "Max archive size");
            Assert.That(File.ReadAllText(k_File), Does.Contain("MaxArchiveMB: 7"), "each committed edit is written out");

            Assert.That(m_Model.Rename(ContentBuildSettings.instance.ActiveName), Does.Contain("exists"), "names stay unique");
            Assert.That(m_Model.Rename("Win2"), Is.Null);
            Assert.That(m_Model.SelectedName, Is.EqualTo("Win2"));
            m_Model.SetSelectedActive();
            Assert.That(ContentBuildSettings.instance.ActiveName, Is.EqualTo("Win2"));
            Assert.That(m_Model.Rename("Win3"), Is.Null);
            Assert.That(ContentBuildSettings.instance.ActiveName, Is.EqualTo("Win3"), "renaming the active profile keeps it active");

            Assert.That(m_Model.RemoveSelected(), Is.Null);
            Assert.That(ContentBuildSettings.instance.Find("Win3"), Is.Null);
            Assert.That(m_Model.SelectedName, Is.EqualTo(ContentBuildSettings.instance.ActiveName), "the selection falls back to the active profile");
        }

        [Test]
        public void W2_AddCategory_DefaultsOutsideTheRoot_RefusesOneInsideIt_AndTheOverlapShowsAsAnError()
        {
            m_Model.AddProfile("Cat");
            m_Model.Edit(x => x.SourceRoot = k_RootA, "Source root");
            Assert.That(ContentBuildWindowModel.DefaultCatalogPath(m_Model.Selected, "Icons/Big"), Is.EqualTo("Assets/ContentCatalogs/Cat.Icons.Big.asset"), "outside every source root, named after profile and category");
            Assert.That(m_Model.AddCategory("Icons", $"{k_RootA}/icons.asset"), Does.Contain("under the source root"), "a catalog under its own root would catalogue itself");
            Assert.That(m_Model.Selected.Catalogs.Length, Is.EqualTo(1));

            Assert.That(m_Model.AddCategory("Icons", TempCatalog()), Is.Null);
            Assert.That(m_Model.Selected.Catalogs.Length, Is.EqualTo(2));
            Assert.That(m_Model.Errors, Has.Some.Contains("overlap"), "the whole-root category the profile started with covers 'Icons'");
            Assert.That(m_Model.CanBuild, Is.False);

            m_Model.RemoveCategory(0);
            Assert.That(m_Model.Errors, Is.Empty, string.Join("\n", m_Model.Errors));
            Assert.That(m_Model.CanBuild, Is.True);
            Assert.That(m_Model.AddCategory("Icons/"), Does.Contain("exists already"), "normalised sub-folders compare equal");
        }

        [Test]
        public void W3_PostProcessors_AreDiscovered_ReorderedHere_AndAMissingOneBlocksTheBuild()
        {
            m_Model.AddProfile("Post");
            m_Model.Edit(x => x.SourceRoot = k_RootA, "Source root");   // the default root does not exist in this project; only the post-processors should decide CanBuild here
            Assert.That(m_Model.CanBuild, Is.True, string.Join("\n", m_Model.Errors));
            Assert.That(m_Model.AvailablePostProcessors, Does.Contain(typeof(ContentArchivePackerTests.RecordA)), "TypeCache finds the implementations");
            Assert.That(m_Model.AddPostProcessor(typeof(ContentArchivePackerTests.RecordA)), Is.Null);
            Assert.That(m_Model.AddPostProcessor(typeof(ContentArchivePackerTests.RecordB)), Is.Null);
            Assert.That(m_Model.AddPostProcessor(typeof(ContentArchivePackerTests.RecordA)), Does.Contain("listed already"));

            var names = m_Model.Selected.PostProcessors;
            Assert.That(names.Length, Is.EqualTo(2));
            Assert.That(ContentBuildRunner.ResolvePostProcessor(names[0]), Is.EqualTo(typeof(ContentArchivePackerTests.RecordA)), "the stored name resolves the way the runner resolves it");
            Assert.That(ContentBuildWindowModel.IsResolved(names[1]), Is.True);

            m_Model.MovePostProcessor(1, -1);
            Assert.That(ContentBuildRunner.ResolvePostProcessor(m_Model.Selected.PostProcessors[0]), Is.EqualTo(typeof(ContentArchivePackerTests.RecordB)), "the post-processor list is the one list that reorders");

            m_Model.Edit(x => x.PostProcessors = new[] { x.PostProcessors[0], "Nope.Missing" }, "test");
            Assert.That(m_Model.Errors, Has.Some.Contains("Nope.Missing"));
            Assert.That(m_Model.CanBuild, Is.False, "a post-processor that would not be found fails the build, so the window blocks it");
            m_Model.RemovePostProcessor(1);
            Assert.That(m_Model.CanBuild, Is.True);
        }

        [Test]
        public void W4_Build_ReachesTheRunner_SavesTheCatalogReference_AndLeavesTheLastResultInSessionState()
        {
            m_Model.AddProfile("Model");
            var catalog = TempCatalog();
            m_Model.Edit(x =>
            {
                x.SourceRoot = k_RootA;
                x.OutputPath = k_Output;
                x.Archive = ContentBuildSettings.ArchiveMode.None;
                x.Catalogs[0].CreateAt = catalog;
            }, "test");
            Assert.That(m_Model.Notes, Has.Some.Contains("inside Assets/"), "an output under Assets/ is allowed but noted");
            Assert.That(m_Model.CanBuild, Is.True, string.Join("\n", m_Model.Errors));

            var r = m_Model.Build(showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            Assert.That(m_Model.LastResult, Is.SameAs(r));
            Assert.That(m_Model.Selected.Catalogs[0].Catalog.isSet, Is.True, "the first build created the catalog and set the reference");
            Assert.That(File.ReadAllText(k_File), Does.Contain(AssetDatabase.AssetPathToGUID(catalog)), "the window saves the reference the build set; the CLI never would");

            var last = ContentBuildWindowModel.ReadLastBuild();
            Assert.That(last.Exists && last.Ok, Is.True);
            Assert.That(last.Profile, Is.EqualTo("Model"));
            Assert.That(last.Files, Is.EqualTo(r.Files.Length));
        }

        [Test]
        public void W5_Undo_RevertsACommittedEdit_AndTheCallbackSavesTheRevertedState()
        {
            m_Model.AddProfile("Undo");
            int before = m_Model.Selected.MaxArchiveMB;
            Undo.IncrementCurrentGroup();
            m_Model.Edit(x => x.MaxArchiveMB = before + 5, "Max archive size");
            Assert.That(m_Model.Selected.MaxArchiveMB, Is.EqualTo(before + 5));

            Undo.PerformUndo();
            Assert.That(m_Model.Selected.MaxArchiveMB, Is.EqualTo(before), "Undo.RecordObject on the settings singleton must revert the edit");
            m_Model.OnUndoRedo();
            Assert.That(File.ReadAllText(k_File), Does.Contain($"MaxArchiveMB: {before}"), "the undo callback writes the reverted state out");

            Undo.PerformRedo();
            Assert.That(m_Model.Selected.MaxArchiveMB, Is.EqualTo(before + 5));
        }

        /// <summary>
        /// A profile that is not the active one, with a file under Sub/ of its root. Not the active one: the static view follows the
        /// active profile, so an edit of that one reaches the editor utility through the static root too and would hide the defect.
        /// </summary>
        string NonActiveProfile(string name, out string guid)
        {
            var root = $"{k_Generated}/{name}Root";
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder(k_Generated, name + "Root");
            if (!AssetDatabase.IsValidFolder(root + "/Sub")) AssetDatabase.CreateFolder(root, "Sub");
            var file = $"{root}/Sub/{name.ToLowerInvariant()}.txt";
            File.WriteAllText(file, name);
            AssetDatabase.ImportAsset(file);
            guid = AssetDatabase.AssetPathToGUID(file);
            Assert.That(m_Model.AddProfile(name), Is.Null);
            m_Model.Edit(x => { x.SourceRoot = root; x.OutputPath = "Build/" + name; }, "setup");
            Assert.That(m_Model.SelectedIsActive, Is.False, "sanity: not the active profile");
            return root;
        }

        /// <summary>T10: the build window moves a profile's root; the inspector's answers follow without a manual Invalidate.</summary>
        [Test]
        public void W7_AnEditedRoot_IsSeenWithoutAManualInvalidate()
        {
            var root = NonActiveProfile("W7", out var guid);
            try
            {
                var before = ContentLinkEditorUtility.NameForRef(guid, "");
                Undo.IncrementCurrentGroup();
                m_Model.Edit(x => x.SourceRoot = root + "/Sub", "Source root");
                var after = ContentLinkEditorUtility.NameForRef(guid, "");
                Assert.That(before, Is.EqualTo("Sub/w7"));
                Assert.That(after, Is.EqualTo("w7"), "the window moved the root and the inspector still answers with the old one — saving the settings raises no projectChanged (measured)");
            }
            finally { AssetDatabase.DeleteAsset(root); }
        }

        /// <summary>T10: an undo while the window is closed reverts the profile and nothing calls OnUndoRedo; the answers follow anyway.</summary>
        [Test]
        public void W8_AnUndoWithNoWindowListening_IsSeen()
        {
            var root = NonActiveProfile("W8", out var guid);
            try
            {
                ContentLinkEditorUtility.NameForRef(guid, "");
                Undo.IncrementCurrentGroup();
                m_Model.Edit(x => x.SourceRoot = root + "/Sub", "Source root");
                ContentLinkEditorUtility.Invalidate();                    // whatever the edit did, the answer before the undo is fresh
                Assert.That(ContentLinkEditorUtility.NameForRef(guid, ""), Is.EqualTo("w8"));

                Undo.PerformUndo();                                       // no OnUndoRedo: the window is not open
                Assert.That(m_Model.Selected.SourceRoot, Is.EqualTo(root), "sanity: the undo reverted the profile");
                Assert.That(ContentLinkEditorUtility.NameForRef(guid, ""), Is.EqualTo("Sub/w8"), "an undo with no window listening is not seen — it bumps no revision");
            }
            finally { AssetDatabase.DeleteAsset(root); }
        }

        /// <summary>
        /// T27: the name style is edited like any field — one undoable, saved step — and while it is not what the profile's
        /// catalog was written with, a note says that updating renames every name; the update clears it.
        /// </summary>
        [Test]
        public void W9_NameStyle_IsAnUndoableEdit_AndTheNoteFollowsTheCatalog()
        {
            var root = NonActiveProfile("W9", out _);
            var catalog = TempCatalog();
            m_Model.Edit(x => x.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = catalog } }, "setup");
            try
            {
                Assert.That(m_Model.UpdateCatalogs(out var message), Is.True, message);
                Assert.That(m_Model.Notes, Has.None.Contains("name style"), "sanity: the catalog was written in the profile's style");

                Undo.IncrementCurrentGroup();
                m_Model.Edit(x => x.NameStyle = ContentNameStyle.PathWithExtension, "Name style");
                Assert.That(File.ReadAllText(k_File), Does.Contain("NameStyle: 1"), "a committed edit saves the file");
                Assert.That(m_Model.Notes, Has.Some.Contains("name style"), "the profile no longer names what its catalog holds");

                Undo.PerformUndo();
                m_Model.OnUndoRedo();
                Assert.That(m_Model.Selected.NameStyle, Is.EqualTo(ContentNameStyle.PathWithoutExtension), "the undo reverts the style");
                Assert.That(m_Model.Notes, Has.None.Contains("name style"), "reverted before any update: nothing to say");

                Undo.PerformRedo();
                m_Model.OnUndoRedo();
                Assert.That(m_Model.UpdateCatalogs(out message), Is.True, message);
                Assert.That(m_Model.Notes, Has.None.Contains("name style"), "the catalog rewritten in the new style: the note goes");
                Assert.That(AssetDatabase.LoadAssetAtPath<ContentCatalog>(catalog).NameStyle, Is.EqualTo(ContentNameStyle.PathWithExtension));
            }
            finally { AssetDatabase.DeleteAsset(root); }
        }

        [Test]
        public void W6_TypedPaths_AreStoredWithoutTrailingSeparatorsOrBackslashes()
        {
            m_Model.AddProfile("Paths");
            m_Model.Edit(x => { x.SourceRoot = k_RootA + "/"; x.OutputPath = "Build\\Content\\Paths\\"; }, "paths");
            Assert.That(m_Model.Selected.SourceRoot, Is.EqualTo(k_RootA), "a typed trailing separator would cut every name by one character at build time");
            Assert.That(m_Model.Selected.OutputPath, Is.EqualTo("Build/Content/Paths"));
            Assert.That(m_Model.CanBuild, Is.True, string.Join("\n", m_Model.Errors));
        }

        [Test]
        public void W10_ARootOrACategoryTypedInAnotherCase_IsStoredInTheDiskSpelling()
        {
            // The AssetDatabase finds a folder in any case (measured on 6000.6), but the content tools compare the root and the
            // categories as written: the window stores the disk's spelling, as it drops a trailing separator (W6).
            m_Model.AddProfile("CaseTyped");
            m_Model.Edit(x => x.SourceRoot = "assets/testcontent/a/", "Source root");
            Assert.That(m_Model.Selected.SourceRoot, Is.EqualTo(k_RootA));
            Assert.That(m_Model.AddCategory("icons", TempCatalog()), Is.Null);
            Assert.That(m_Model.Selected.Catalogs[1].SubFolder, Is.EqualTo("Icons"));
            Assert.That(m_Model.AddCategory("ICONS", TempCatalog()), Does.Contain("exists already"), "the one already there, in the project's spelling");
            m_Model.Edit(x => x.Catalogs[1].SubFolder = "materials", "Category");
            Assert.That(m_Model.Selected.Catalogs[1].SubFolder, Is.EqualTo("Materials"), "an edited category too");
            m_Model.Edit(x => x.Catalogs[1].SubFolder = "nowhere", "Category");
            Assert.That(m_Model.Selected.Catalogs[1].SubFolder, Is.EqualTo("nowhere"), "a folder that does not exist is stored as typed");
        }

        [Test]
        public void W11_ACategoryTypedAfterItsFolderWasDeleted_IsStoredAsTyped()
        {
            // The AssetDatabase still answers a folder deleted this session unless asked for existing assets (measured on 6000.6): a
            // category typed in another case must not take the deleted folder's spelling.
            const string root = k_Generated + "/W11";
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder(k_Generated, "W11");
            AssetDatabase.CreateFolder(root, "icons");
            AssetDatabase.DeleteAsset(root + "/icons");
            try
            {
                m_Model.AddProfile("Deleted");
                m_Model.Edit(x => x.SourceRoot = root, "Source root");
                Assert.That(m_Model.AddCategory("ICONS", TempCatalog()), Is.Null);
                Assert.That(m_Model.Selected.Catalogs[m_Model.Selected.Catalogs.Length - 1].SubFolder, Is.EqualTo("ICONS"), "a deleted folder's spelling is not the project's");
            }
            finally { AssetDatabase.DeleteAsset(root); }
        }
    }
}
