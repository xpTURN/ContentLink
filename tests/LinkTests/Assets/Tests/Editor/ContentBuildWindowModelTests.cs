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

        [Test]
        public void W6_TypedPaths_AreStoredWithoutTrailingSeparatorsOrBackslashes()
        {
            m_Model.AddProfile("Paths");
            m_Model.Edit(x => { x.SourceRoot = k_RootA + "/"; x.OutputPath = "Build\\Content\\Paths\\"; }, "paths");
            Assert.That(m_Model.Selected.SourceRoot, Is.EqualTo(k_RootA), "a typed trailing separator would cut every name by one character at build time");
            Assert.That(m_Model.Selected.OutputPath, Is.EqualTo("Build/Content/Paths"));
            Assert.That(m_Model.CanBuild, Is.True, string.Join("\n", m_Model.Errors));
        }
    }
}
