using System.Collections;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
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

        /// <summary>
        /// A ContentCatalog under the root — saved as .asset, or under another extension the engine imports as a
        /// catalog all the same (.preset — measured on 6000.6; a .mat one makes ShaderGraph's material postprocessor throw). A build
        /// refuses it, so the overlay does not serve it either.
        /// </summary>
        [Test]
        public void O5_ACatalogUnderTheRoot_IsNotServed()
        {
            ContentDirectoryBuilder.SourceRoot = k_Root;
            ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
            File.WriteAllText($"{k_Root}/plain.txt", "plain");
            AssetDatabase.ImportAsset($"{k_Root}/plain.txt");
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContentCatalog>(), $"{k_Root}/Nested.asset");
            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<ContentCatalog>(), $"{k_Root}/Renamed.preset");
            ContentLinkEditorUtility.Invalidate();

            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape($"'{k_Root}/Nested.asset' is a ContentCatalog under '{k_Root}'")));
            LogAssert.Expect(LogType.Warning, new Regex(Regex.Escape($"'{k_Root}/Renamed.preset' is a ContentCatalog under '{k_Root}'")));
            ContentLinkSimulation.Rebuild();
            Assert.That(ContentLinkSimulation.TryGetPath("Nested", out _, out _), Is.False, "the overlay served a catalog under the root");
            Assert.That(ContentLinkSimulation.TryGetPath("Renamed", out _, out _), Is.False, "the overlay served a catalog saved as .preset");
            Assert.That(ContentLinkSimulation.TryGetPath("plain", out var found, out _), Is.True, "the plain file beside them was not served");
            Assert.That(found, Is.EqualTo($"{k_Root}/plain.txt"));
        }

        // ───────────── references (ContentRef) ─────────────

        [UnityTest]
        public IEnumerator O4_AFilesGuid_AnswersItsName_AndAMoveAnswersTheNewOne()
        {
            bool enabled = ContentLinkSimulation.Enabled;
            ContentLinkSimulation.Enabled = true;   // the reference seam answers only while the simulation is on (or a directory is served from the project)
            try
            {
                ContentDirectoryBuilder.SourceRoot = k_Root;
                ContentDirectoryBuilder.CatalogPath = m_TempCatalog;
                Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
                var path = $"{k_Root}/late.txt";
                File.WriteAllText(path, "late");
                AssetDatabase.ImportAsset(path);
                ContentLinkEditorUtility.Invalidate();

                ContentLinkSimulation.Rebuild();
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(ContentLinkSimulation.OverlayNameFor(guid, "", false), Is.EqualTo("late"), "the scan did not answer the file's GUID with its name");
                Assert.That(ContentLinkSimulation.OverlayNameFor(guid, "", true), Is.Null, "a text file is not a scene");
                Assert.That(ContentLinkSimulation.OverlayNameFor("0123456789abcdef0123456789abcdef", "", false), Is.Null, "an unknown GUID");

                AssetDatabase.CreateFolder(k_Root, "Sub");
                Assert.That(AssetDatabase.MoveAsset(path, $"{k_Root}/Sub/late.txt"), Is.Empty, "the move itself failed");
                string now = null;
                int frames = -1;
                for (int i = 0; i <= 30; ++i)
                {
                    now = ContentLinkSimulation.OverlayNameFor(guid, "", false);
                    if (now == "Sub/late") { frames = i; break; }
                    yield return null;
                }
                Debug.Log($"[ContentLink.Tests] edit mode: after MoveAsset the GUID answered '{now}' after {frames} editor update(s)");
                Assert.That(frames, Is.GreaterThanOrEqualTo(0), "the project change did not drop the stale answer");
            }
            finally { ContentLinkSimulation.Enabled = enabled; }
        }
    }
}
