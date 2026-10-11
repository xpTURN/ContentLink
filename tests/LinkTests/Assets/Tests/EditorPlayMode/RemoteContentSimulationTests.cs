using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.EditorPlay
{
    /// <summary>T38: with the simulation on, a remote check of a profile serves it from the project — no server, no download.</summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Remote")]
    public class RemoteContentSimulationTests
    {
        string m_Snapshot;
        bool m_EnabledWas;
        int m_Dir = -1;

        [SetUp]
        public void Prepare()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_EnabledWas = ContentLinkSimulation.Enabled;
            ContentLinkSimulation.Enabled = true;
            TestUtil.SetSimulation(true);
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Drop();
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (m_Dir >= 0 && !ContentService.UnregisterDirectory(m_Dir)) ContentService.UnregisterDirectory(m_Dir, force: true);
            m_Dir = -1;
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i) { ContentService.Collect(); yield return null; }
            ContentLinkSimulation.Drop();
            ContentLinkSimulation.Enabled = m_EnabledWas;
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            ContentLinkEditorUtility.Invalidate();
        }

        [Test]
        public async Task T38_SimulationOn_ServesTheProfile_WithoutAServer()
        {
            var p = ContentBuildSettings.instance.Add("RemoteG");
            p.SourceRoot = "Assets/TestContent/G";
            p.OutputPath = "Build/Remote/G";
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "", CreateAt = "Assets/TestContent/Generated/Remote_G.asset" } };
            ContentBuildSettings.instance.MarkDirty();

            var remote = await ContentRemote.CheckAsync("RemoteG", "http://127.0.0.1:9/never", new ContentRemoteOptions { CacheRoot = Path.Combine(Application.temporaryCachePath, "RemoteSim") });
            Assert.That(remote.Source, Is.EqualTo(ContentRemoteSource.Simulated), remote.Error);
            Assert.That(remote.CacheDirectory, Is.EqualTo("Build/Remote/G"));
            Assert.That(remote.BytesToDownload, Is.EqualTo(0));
            Assert.That(await remote.DownloadAsync(), Is.True);
            m_Dir = await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
            Assert.That(m_Dir, Is.GreaterThanOrEqualTo(0));
            Assert.That(ContentService.HasSimulatedDirectories, Is.True, "served from the project");
            var h = ContentService.Acquire<Texture2D>("Icons/flag");
            try { Assert.That(AssetDatabase.GetAssetPath(h.Asset), Is.EqualTo("Assets/TestContent/G/Icons/flag.png")); }
            finally { h.Dispose(); }
        }
    }
}
