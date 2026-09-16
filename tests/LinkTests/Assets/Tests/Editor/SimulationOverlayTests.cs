using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// The overlay scan against the real AssetDatabase. Only the scan: registering a content directory throws in
    /// edit mode, so the per-directory root filter and the loads are PlayMode work (SimulationTests), and the
    /// filter's own logic is covered by the dotnet harness.
    /// </summary>
    [Category("Simulation")]
    public class SimulationOverlayTests
    {
        const string k_Generated = "Assets/TestContent/Generated";
        const string k_Root = "Assets/TestContent/Generated/P2";

        string m_SourceRoot, m_CatalogPath, m_TempCatalog;

        [SetUp]
        public void SaveStatics()
        {
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            if (!AssetDatabase.IsValidFolder(k_Root)) AssetDatabase.CreateFolder(k_Generated, "P2");
            m_TempCatalog = $"{k_Generated}/SimTest_{System.Guid.NewGuid():N}.asset";
        }

        [TearDown]
        public void RestoreStatics()
        {
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            AssetDatabase.DeleteAsset(m_TempCatalog);
            AssetDatabase.DeleteAsset(k_Root);
            ContentLinkSimulation.Drop();
            ContentLinkEditorUtility.Invalidate();
        }

        [Test]
        public void O1_Rebuild_IndexesAnAssetNoCatalogCarries()
        {
            ContentDirectoryBuilder.SourceRoot = k_Root;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);

            // Created after the build: the catalog cannot know it, which is the whole situation the simulation is for.
            var path = $"{k_Root}/late.txt";
            File.WriteAllText(path, "late");
            AssetDatabase.ImportAsset(path);
            ContentLinkEditorUtility.Invalidate();

            int count = ContentLinkSimulation.Rebuild();
            Assert.That(count, Is.GreaterThan(0), "the scan produced nothing");
            Assert.That(ContentLinkSimulation.TryGetPath("late", out var found, out var root), Is.True,
                "the overlay did not pick up an asset added after the catalog was built");
            Assert.That(found, Is.EqualTo(path));
            Assert.That(root, Is.EqualTo(k_Root), "the entry must remember which catalog root it came from (the per-directory filter selects on it)");
        }

        [Test]
        public void O3_RebuildCost_StaysWithinBudget()
        {
            // The scan must stay a folder walk. If it ever loaded the assets it indexes — which is what
            // building ids up front would require — the number here would jump by an order of magnitude.
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Rebuild();          // warm the catalog index and the AssetDatabase caches

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int count = ContentLinkSimulation.Rebuild();
            watch.Stop();

            double ms = watch.Elapsed.TotalMilliseconds;
            double perThousand = count > 0 ? ms * 1000.0 / count : 0;
            Debug.Log($"[ContentLink.Tests] Rebuild: {ms:F1} ms for {count} name(s) → {perThousand:F1} ms per 1000 (budget 50)");
            Assert.That(count, Is.GreaterThan(0), "nothing was indexed, so the measurement means nothing");
            Assert.That(ms, Is.LessThan(250), $"the overlay scan took {ms:F1} ms on a fixture project — it is loading assets, not walking folders");
        }

        [Test]
        public void O2_DroppingTheOverlay_ForgetsEverything()
        {
            ContentDirectoryBuilder.SourceRoot = k_Root;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
            var path = $"{k_Root}/volatile.txt";
            File.WriteAllText(path, "volatile");
            AssetDatabase.ImportAsset(path);
            ContentLinkEditorUtility.Invalidate();

            ContentLinkSimulation.Rebuild();
            Assume.That(ContentLinkSimulation.TryGetPath("volatile", out _, out _), Is.True);

            ContentLinkSimulation.Drop();
            Assert.That(ContentLinkSimulation.TryGetPath("volatile", out _, out _), Is.False,
                "the overlay must be volatile — Play exit drops it and nothing is stored");
        }
    }
}
