#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// The editor simulation end to end: a link to an asset that no catalog carries loads in Play Mode,
    /// and only while its directory is registered.
    ///
    /// The fixture is made here rather than committed: the builder puts everything under a source root into the
    /// directory, so "under the root but absent from the build" only exists if the asset appears after the build.
    /// It lives in its own folder under TestContent/A, is gitignored, and is deleted at both ends of the fixture.
    ///
    /// Editor-only: the fixture needs the AssetDatabase, and the simulation itself is an editor feature. In a player
    /// the seam is null and this file is not compiled.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Simulation")]
    public class SimulationTests
    {
        const string k_FolderA = "Assets/TestContent/A/SimTemp";
        const string k_FolderB = "Assets/TestContent/B/SimTemp";
        const string k_NameA = "SimTemp/late";      // the name the path rule gives it under A's root
        const string k_NameB = "SimTemp/unreg";
        const string k_ScenePath = "Assets/TestContent/A/SimTemp/lateScene.unity";
        const string k_SceneName = "SimTemp/lateScene";

        int m_DirA = -1;

        [OneTimeSetUp]
        public void CreateFixture()
        {
            Delete();                                   // defensive: a crashed run must not leak into the next build
            Create(k_FolderA, "late.txt");
            Create(k_FolderB, "unreg.txt");
            // A scene in the same situation: under a registered root, absent from the build. Copied rather than
            // authored so the fixture needs no scene API (SceneCase is never part of a content directory).
            Assert.That(AssetDatabase.CopyAsset("Assets/TestContent/SceneCase/Empty.unity", k_ScenePath), Is.True,
                "could not copy the scene fixture");
            AssetDatabase.Refresh();

            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));   // B stays unregistered on purpose: its names must not resolve
        }

        [OneTimeTearDown]
        public void DropFixture()
        {
            if (m_DirA >= 0 && !ContentService.UnregisterDirectory(m_DirA)) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = -1;
            Delete();
        }

        static void Create(string folder, string file)
        {
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            var path = $"{folder}/{file}";
            File.WriteAllText(path, "content-link editor simulation fixture");
            AssetDatabase.ImportAsset(path);
        }

        static void Delete()
        {
            AssetDatabase.DeleteAsset(k_FolderA);
            AssetDatabase.DeleteAsset(k_FolderB);
        }

        [SetUp]
        public void TurnSimulationOn() => TestUtil.SetSimulation(true);

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();   // also turns the simulation back off for every other suite
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        // ───────────── the request: link and load without updating the catalog ─────────────

        [Test, Timeout(30000)]
        public async Task S1_NewAssetUnderARegisteredRoot_LoadsWithoutRebuildingTheCatalog()
        {
            Assert.That(ContentService.NameFallback, Is.Not.Null,
                "the editor did not install the simulation seam — is 'Window/ContentLink/Editor Simulation' off?");
            TestUtil.SetSimulation(false);   // TryGetLoadableObjectId answers simulated names too, so ask the catalogs alone
            bool inCatalog = ContentService.TryGetLoadableObjectId(k_NameA, out _);
            TestUtil.SetSimulation(true);
            Assert.That(inCatalog, Is.False,
                "the fixture leaked into a build: the name is already in a catalog, so this test would prove nothing");

            Assert.That(ContentService.Contains(k_NameA), Is.True,
                "Contains must answer for a name only the simulation can resolve — RuntimeKeyIsValid rests on it");

            var handle = await ContentService.AcquireAsync<TextAsset>(k_NameA);
            try
            {
                Assert.That(handle.IsValid && handle.Asset != null, Is.True, "the asset did not load through the simulation");
                Assert.That(handle.Asset.text, Does.Contain("simulation fixture"));

                var simulated = new System.Collections.Generic.List<string>();
                ContentService.SnapshotSimulated(simulated);
                Assert.That(simulated, Does.Contain(k_NameA), "the slot is not marked as simulated, so the exit summary would miss it");
            }
            finally { if (handle.IsValid) handle.Dispose(); }
        }

        [Test, Timeout(30000)]
        public async Task S2_SimulationOff_KeepsTheShippingBehaviour()
        {
            TestUtil.SetSimulation(false);

            Assert.That(ContentService.Contains(k_NameA), Is.False, "with the simulation off only built directories answer");
            LogAssert.Expect(LogType.Error, $"[ContentLink] '{k_NameA}' is not in any registered ContentCatalog.");
            var handle = await ContentService.AcquireAsync<TextAsset>(k_NameA);
            Assert.That(handle.IsValid, Is.False, "the toggle must restore the exact failure a player build would produce");
        }

        [Test, Timeout(30000)]
        public async Task S3_AssetOfAnUnregisteredDirectory_StaysUnresolved()
        {
            // The overlay knows this name (B has a catalog and a source root), but B is not registered, so the
            // simulation must not resolve it. Otherwise a forgotten RegisterDirectory would only fail in a build.
            Assert.That(ContentService.Contains(k_NameB), Is.False,
                "a name from an unregistered directory resolved — the simulation would hide a missing RegisterDirectory");

            LogAssert.Expect(LogType.Error, $"[ContentLink] '{k_NameB}' is not in any registered ContentCatalog.");
            var handle = await ContentService.AcquireAsync<TextAsset>(k_NameB);
            Assert.That(handle.IsValid, Is.False);
        }

        [Test, Timeout(30000)]
        public async Task S4_TheCatalogAssetIsNeverWritten()
        {
            // The overlay is volatile: loading through the simulation must not add the name to the catalog asset.
            var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>("Assets/TestContent/Generated/CatalogA.asset");
            Assert.That(catalog, Is.Not.Null, "CatalogA is missing — run the EditMode content build first");
            int before = catalog.Entries.Count;

            var handle = await ContentService.AcquireAsync<TextAsset>(k_NameA);
            try { Assume.That(handle.IsValid, Is.True); }
            finally { if (handle.IsValid) handle.Dispose(); }

            Assert.That(catalog.Entries.Count, Is.EqualTo(before), "the simulation wrote to the catalog asset");
            foreach (var e in catalog.Entries)
                Assert.That(e.name, Is.Not.EqualTo(k_NameA), "the simulated name reached the catalog asset");
        }

        // ───────────── a held simulated asset vs. unregistering another directory ─────────────

        [Test, Timeout(30000)]
        public async Task S7_HeldSimulatedAsset_DoesNotBlockUnregisteringAnotherDirectory()
        {
            // The asset comes from the project, not from any engine directory, so it belongs to none of them: neither
            // the one whose root served the name nor an unrelated one may refuse to unregister because of it —
            // by name, and by the id TryGetLoadableObjectId hands out for the same name.
            var handle = await ContentService.AcquireAsync<TextAsset>(k_NameA);
            Assume.That(handle.IsValid, Is.True, "the simulated load itself is S1's business");
            Assert.That(ContentService.TryGetLoadableObjectId(k_NameA, out var id), Is.True);
            var byId = await ContentService.AcquireAsync<TextAsset>(id);
            try
            {
                Assume.That(byId.IsValid, Is.True);
                int dirB = ContentService.RegisterDirectory(TestContent.Dir("B"));
                bool ok = ContentService.UnregisterDirectory(dirB);
                if (!ok) ContentService.UnregisterDirectory(dirB, force: true);   // recover so the fixture teardown finds B gone either way
                Assert.That(ok, Is.True, "an asset the editor serves from the project must not block unregistering an unrelated directory");
                Assert.That(handle.IsValid && byId.IsValid, Is.True, "the simulated leases (by name and by id) survive the other directory's unregistration");
            }
            finally { if (handle.IsValid) handle.Dispose(); if (byId.IsValid) byId.Dispose(); }
        }

        // ───────────── references (ContentRef) ─────────────

        [Test, Timeout(30000)]
        public async Task S8_ContentRef_ToANewAssetUnderARegisteredRoot_ResolvesThroughTheSimulation()
        {
            Assert.That(ContentService.RefFallback, Is.Not.Null, "the editor did not install the reference seam");
            var reference = new ContentRef<TextAsset>(AssetDatabase.AssetPathToGUID($"{k_FolderA}/late.txt"));
            Assert.That(ContentService.TryGetName(reference, out var name), Is.True, "the simulation did not answer the file's GUID");
            Assert.That(name, Is.EqualTo(k_NameA), "a reference must resolve to the name a link would use");
            Assert.That(reference.RuntimeKeyIsValid(), Is.True);

            var scope = ContentScope.Root.CreateChild("S8");
            try
            {
                var asset = await scope.LoadAsync(reference);
                Assert.That(asset != null && asset.text.Contains("simulation fixture"), Is.True, "the asset did not load through the reference");
                Assert.That(scope.Contains(k_NameA), Is.True, "the reference holds the name's key, so a name load of the same asset shares it");
            }
            finally { scope.Dispose(); }

            TestUtil.SetSimulation(false);
            try
            {
                Assert.That(ContentService.TryGetName(reference, out _), Is.False, "with the simulation off only built catalogs answer a GUID");
                LogAssert.Expect(LogType.Error, $"[ContentLink] {reference} is not in any registered ContentCatalog.");
                var handle = await ContentService.AcquireAsync(reference);
                Assert.That(handle.IsValid, Is.False, "the toggle must restore the failure a player build would produce");
            }
            finally { TestUtil.SetSimulation(true); }
        }

        [Test, Timeout(60000)]
        public async Task S11_LinkWithASpriteName_ToANewSpriteUnderARegisteredRoot_LoadsThroughTheSimulation()
        {
            // the pair ("SimTemp/lateicon", "lateicon") composes the name the path rule gives the sprite, and the name seam
            // serves it from the project — the catalog was built before the file existed.
            const string path = k_FolderA + "/lateicon.png";
            var texture = new Texture2D(2, 2);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();

            var scope = ContentScope.Root.CreateChild("S11");
            var link = new ContentLink<Sprite>("SimTemp/lateicon", "lateicon");
            try
            {
                Assert.That(link.RuntimeKeyIsValid(), Is.True, "the simulation did not answer the composed name");
                var sprite = await link.LoadAsync(scope);
                Assert.That(sprite != null && sprite.name == "lateicon", Is.True, "the sprite did not load through the simulation");

                TestUtil.SetSimulation(false);
                Assert.That(link.RuntimeKeyIsValid(), Is.False, "with the simulation off only built catalogs answer");
            }
            finally
            {
                TestUtil.SetSimulation(true);
                link.Release();
                scope.Dispose();
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test, Timeout(30000)]
        public async Task S9_ContentRef_ToAnAssetOfAnUnregisteredDirectory_StaysUnresolved()
        {
            // The simulation answers the GUID with a name (it answers names only); the name seam then refuses it,
            // exactly as for S3 — a reference must not hide a missing RegisterDirectory either.
            var reference = new ContentRef<TextAsset>(AssetDatabase.AssetPathToGUID($"{k_FolderB}/unreg.txt"));
            Assert.That(ContentService.Contains(reference), Is.False, "a reference into an unregistered directory resolved");

            LogAssert.Expect(LogType.Error, $"[ContentLink] '{k_NameB}' is not in any registered ContentCatalog.");
            var handle = await ContentService.AcquireAsync(reference);
            Assert.That(handle.IsValid, Is.False);
        }

        [Test, Timeout(30000)]
        public async Task S10_ContentRef_AssetMovedDuringPlay_ResolvesTheNewName()
        {
            // the simulation drops its GUID answers on EditorApplication.projectChanged. This measures whether that event
            // arrives during Play, and after how many frames.
            Create(k_FolderA, "mover.txt");
            var reference = new ContentRef<TextAsset>(AssetDatabase.AssetPathToGUID($"{k_FolderA}/mover.txt"));
            Assert.That(ContentService.TryGetName(reference, out var before), Is.True);
            Assert.That(before, Is.EqualTo("SimTemp/mover"));

            if (!AssetDatabase.IsValidFolder($"{k_FolderA}/Moved")) AssetDatabase.CreateFolder(k_FolderA, "Moved");
            Assert.That(AssetDatabase.MoveAsset($"{k_FolderA}/mover.txt", $"{k_FolderA}/Moved/mover.txt"), Is.Empty, "the move itself failed");

            int frames = -1;
            string now = null;
            for (int i = 0; i <= 30; ++i)
            {
                if (ContentService.TryGetName(reference, out now) && now == "SimTemp/Moved/mover") { frames = i; break; }
                await TestUtil.NextFrame();
            }
            Debug.Log($"[ContentLink.Tests] after MoveAsset during Play the reference resolved to '{now}' after {frames} frame(s) (-1 = never within 30)");
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "projectChanged did not reach the simulation during Play: the answer stayed stale");

            var scope = ContentScope.Root.CreateChild("S10");
            try
            {
                var asset = await scope.LoadAsync(reference);
                Assert.That(asset, Is.Not.Null, "the moved asset did not load under its new name");
            }
            finally { scope.Dispose(); }
        }

        // ───────────── scenes ─────────────

        [Test, Timeout(30000)]
        public async Task S5_UnbuiltSceneName_OpensInPlayMode()
        {
            Assert.That(ContentService.TryGetLoadableSceneId(k_SceneName, out _), Is.False,
                "the scene fixture leaked into a build, so this test would prove nothing");
            Assert.That(ContentService.ContainsScene(k_SceneName), Is.True,
                "ContainsScene must answer for a scene only the simulation can open (ContentSceneLink.RuntimeKeyIsValid rests on it)");

            var scene = await ContentService.LoadSceneAsync(k_SceneName, LoadSceneMode.Additive);
            try
            {
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True, "the scene did not open through the simulation");
                Assert.That(scene.path, Is.EqualTo(k_ScenePath), "the wrong scene was handed back — the path match is what identifies it");

                // Observed: a scene opened this way carries no LoadableSceneId, which is why the id-based
                // Claim cannot find it and the editor matches on the path instead.
                Debug.Log($"[ContentLink.Tests] loadableSceneId.IsValid={scene.loadableSceneId.IsValid} path={scene.path}");
            }
            finally
            {
                // SceneManager directly: ContentService.TryUnloadScene is internal, and the tracking entry is removed
                // by the sceneUnloaded hook either way.
                var op = scene.IsValid() && scene.isLoaded ? SceneManager.UnloadSceneAsync(scene) : null;
                if (op != null) while (!op.isDone) await TestUtil.NextFrame();
            }
        }

        [Test, Timeout(30000)]
        public async Task S6_SimulationOff_SceneKeepsTheShippingBehaviour()
        {
            TestUtil.SetSimulation(false);   // parks every seam, scenes included
            try
            {
                Assert.That(ContentService.ContainsScene(k_SceneName), Is.False);
                LogAssert.Expect(LogType.Error, $"[ContentLink] scene '{k_SceneName}' is not in any registered ContentCatalog.");
                var scene = await ContentService.LoadSceneAsync(k_SceneName, LoadSceneMode.Additive);
                Assert.That(scene.IsValid(), Is.False, "the toggle must restore the exact failure a player build would produce");
            }
            finally { TestUtil.SetSimulation(true); }
        }
    }
}
#else
using NUnit.Framework;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Player stand-in for the fixture above, which needs UnityEditor and so is not compiled into a player build.
    /// Without it the tests would not be reported as skipped — they would be absent from the report altogether, and
    /// "editor only" would read exactly like "gone". The editor PlayMode suite runs the real one.
    /// </summary>
    [Category("Simulation")]
    public class SimulationTests
    {
        [Test]
        public void EditorOnly() => Assert.Ignore(
            "editor only: the editor simulation and its fixtures need the AssetDatabase, and the seams are null in a player. It runs in the editor PlayMode suite.");
    }
}
#endif
