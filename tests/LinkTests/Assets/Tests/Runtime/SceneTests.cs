using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Loading;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Content-directory scenes against the real engine. Directory C (Scenes/One, Scenes/Two, Icons/shared)
    /// is registered for the fixture. Tests marked "probe" log engine facts (which duplicate instance a lookup
    /// returns, unload edge cases, event ordering) and assert only what the design relies on.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Scenes")]
    public class SceneTests
    {
        int m_DirC = -1;

        [OneTimeSetUp]
        public void RegisterC()
        {
            Assert.That(Directory.Exists(TestContent.Dir("C")), Is.True,
                $"content directory C is missing at '{TestContent.Dir("C")}' — run the EditMode test TestContentBuildTests (or Window/xpTURN/ContentLink/Tests/Build Test Content) first");
            m_DirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
        }

        [OneTimeTearDown]
        public void UnregisterC()
        {
            if (m_DirC < 0) return;
            bool ok = ContentService.UnregisterDirectory(m_DirC);
            if (!ok) ContentService.UnregisterDirectory(m_DirC, force: true);
            m_DirC = -1;
            Assert.That(ok, Is.True, "directory C still had live assets or open scenes at fixture end");
        }

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            // a failed test may leave content scenes open: unload them so the fixture (and later tests) stay clean
            var open = new List<ContentService.SceneInfo>();
            ContentService.SnapshotScenes(open);
            foreach (var info in open)
                if (info.Scene.IsValid() && info.Scene.isLoaded && SceneManager.loadedSceneCount > 1) SceneManager.UnloadSceneAsync(info.Scene);
            for (int i = 0; i < 30 && (ContentService.LiveAssetCount != 0 || ContentService.LoadedSceneCount != 0); ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0), "content scenes remain loaded after the test");
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        static async Task Unload(Scene scene)
        {
            var op = SceneManager.UnloadSceneAsync(scene);
            if (op != null) await op;
        }

        // ───────────── load → tracked → unload ─────────────

        [Test, Timeout(10000)]
        public async Task S1_LoadAdditive_IsTracked_AndUnloadUntracks()
        {
            Assert.That(ContentService.ContainsScene("Scenes/One"), Is.True);
            Assert.That(ContentService.TryGetLoadableSceneId("Scenes/One", out var id), Is.True);
            Assert.That(id.IsValid, Is.True);
            Assert.That(ContentService.Contains("Scenes/One"), Is.False, "scene names are not asset names");

            int loadedEvents = 0;
            void OnLoaded(Scene s, LoadSceneMode m) { if (s.loadableSceneId == id) loadedEvents++; }
            SceneManager.sceneLoaded += OnLoaded;
            Scene scene;
            try { scene = await ContentService.LoadSceneAsync("Scenes/One"); }
            finally { SceneManager.sceneLoaded -= OnLoaded; }

            Assert.That(scene.IsValid(), Is.True, "the catalog's LoadableSceneId alone must pull the scene into the build and load it from the registered directory");
            Assert.That(scene.isLoaded, Is.True);
            Assert.That(loadedEvents, Is.EqualTo(1), "sceneLoaded arrives for a content scene");
            Debug.Log($"[ContentLink.Tests] name={scene.name} path='{scene.path}' loadableSceneId.IsValid={scene.loadableSceneId.IsValid} equalsCatalogId={scene.loadableSceneId == id} buildIndex={scene.buildIndex} rootCount={scene.rootCount}");
            Assert.That(scene.loadableSceneId == id, Is.True, "Scene.loadableSceneId of a content scene is the catalog id (global tracking relies on it)");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1), "tracked");
            Assert.That(scene.rootCount, Is.EqualTo(1), "the built scene has its root object (not an empty stand-in)");
            var sprite = scene.GetRootGameObjects()[0].GetComponent<SpriteRenderer>().sprite;
            Assert.That(sprite, Is.Not.Null, "the scene's sprite dependency was built and loaded with it");
            Assert.That(sprite.name, Is.EqualTo("shared"));

            var sceneScope = ContentScope.ForScene(scene);
            Assert.That(sceneScope.Kind, Is.EqualTo(ContentScopeKind.Scene));
            var snap = new List<ContentService.SceneInfo>();
            ContentService.SnapshotScenes(snap);
            Assert.That(snap.Count, Is.EqualTo(1));
            Assert.That(snap[0].Key, Is.EqualTo("Scenes/One"));
            Assert.That(snap[0].Dir, Is.EqualTo(m_DirC));

            await Unload(scene);
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0), "sceneUnloaded arrives → untracked");
            Assert.That(sceneScope.IsDisposed, Is.True, "the scene scope ended with the scene");
        }

        // ───────────── same id twice ─────────────

        [Test, Timeout(10000)]
        public async Task S2_SameSceneTwice_AreSeparateInstances_BothTracked()
        {
            ContentService.TryGetLoadableSceneId("Scenes/Two", out var id);
            var a = await ContentService.LoadSceneAsync("Scenes/Two");
            var b = await ContentService.LoadSceneAsync("Scenes/Two");
            Assert.That(a.IsValid() && b.IsValid(), Is.True);
            Assert.That(a.handle == b.handle, Is.False, "additive duplicates are separate instances");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(2));
            var byId = SceneManager.GetSceneByLoadableSceneId(id);
            Debug.Log($"[ContentLink.Tests] GetSceneByLoadableSceneId returned {(byId.handle == a.handle ? "the FIRST" : byId.handle == b.handle ? "the SECOND" : "NEITHER")} of two instances (a={a.handle.GetRawData()} b={b.handle.GetRawData()} got={byId.handle.GetRawData()})");
            await Unload(a);
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));
            Assert.That(b.isLoaded, Is.True);
            await Unload(b);
        }

        // ───────────── which instance a scope's load claims ─────────────

        [Test, Timeout(10000)]
        public async Task S6_AScopeLoad_ClaimsTheInstanceItProduced_NotOneTheUserOpened()
        {
            ContentService.TryGetLoadableSceneId("Scenes/One", out var id);
            var userOp = SceneManager.LoadSceneAsync(id, new LoadSceneParameters(LoadSceneMode.Additive));   // the user's own engine load, first
            Assert.That(userOp, Is.Not.Null);
            await userOp;
            var users = SceneManager.GetSceneAt(SceneManager.sceneCount - 1);
            Assume.That(users.loadableSceneId == id && users.isLoaded, Is.True);
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1), "the user's instance is tracked, unclaimed");

            var scope = ContentScope.Root.CreateChild("S6");
            try
            {
                var owned = await scope.LoadSceneAsync("Scenes/One");
                Assert.That(owned.IsValid() && owned.isLoaded, Is.True, "the scope's load produced an instance");
                Assert.That(owned.handle == users.handle, Is.False, "the scope must own the instance its own load produced, not the one the user opened");
            }
            finally { scope.Dispose(); }
            for (int i = 0; i < 30 && ContentService.LoadedSceneCount > 1; ++i) await TestUtil.NextFrame();   // the scope's unload finishes
            Assert.That(users.isLoaded, Is.True, "disposing the scope must not unload the user's instance");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));
            await Unload(users);
        }

        // ───────────── unregister refused while a scene is open ─────────────

        [Test, Timeout(10000)]
        public async Task S3_UnregisterRefused_WhileSceneOpen()
        {
            var scene = await ContentService.LoadSceneAsync("Scenes/One");
            LogAssert.Expect(LogType.Error, new Regex(@"\[ContentLink\] .* still has loaded scene\(s\).*\n\s+Scenes/One"));
            Assert.That(ContentService.UnregisterDirectory(m_DirC), Is.False);
            LogAssert.Expect(LogType.Error, new Regex(@"\[ContentLink\] .* still has loaded scene\(s\)"));
            Assert.That(ContentService.UnregisterDirectory(m_DirC, force: true), Is.False, "force does not unload scenes");
            await Unload(scene);
            // the fixture teardown unregisters for real
        }

        // ───────────── unload edge cases (probe) ─────────────

        [Test, Timeout(10000)]
        public async Task S4_Probe_UnloadEdges()
        {
            var scene = await ContentService.LoadSceneAsync("Scenes/One");
            int before = SceneManager.loadedSceneCount, beforeAll = SceneManager.sceneCount;
            var op = SceneManager.UnloadSceneAsync(scene);
            string second;
            try { var op2 = SceneManager.UnloadSceneAsync(scene); second = op2 == null ? "null" : "op"; }
            catch (Exception e) { second = $"threw {e.GetType().Name}: {e.Message}"; }
            string invalid;
            try { var op3 = SceneManager.UnloadSceneAsync(default(Scene)); invalid = op3 == null ? "null" : "op"; }
            catch (Exception e) { invalid = $"threw {e.GetType().Name}: {e.Message}"; }
            Debug.Log($"[ContentLink.Tests] during unload: isLoaded={scene.isLoaded} IsValid={scene.IsValid()} loadedSceneCount {before}->{SceneManager.loadedSceneCount} sceneCount {beforeAll}->{SceneManager.sceneCount} | second UnloadSceneAsync={second} | invalid scene={invalid}");
            if (op != null) await op;
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0));
        }

        // ───────────── sceneLoaded vs AsyncOperation completion order (probe) ─────────────

        [Test, Timeout(10000)]
        public async Task S5_Probe_SceneLoadedBeforeOperationCompletes()
        {
            ContentService.TryGetLoadableSceneId("Scenes/One", out var id);
            AsyncOperation op = null; bool doneAtEvent = false; int events = 0; bool completedFired = false; bool eventBeforeCompleted = false;
            void OnLoaded(Scene s, LoadSceneMode m) { if (s.loadableSceneId == id) { events++; doneAtEvent = op != null && op.isDone; eventBeforeCompleted = !completedFired; } }
            SceneManager.sceneLoaded += OnLoaded;
            try
            {
                op = SceneManager.LoadSceneAsync(id, new LoadSceneParameters(LoadSceneMode.Additive));
                Assert.That(op, Is.Not.Null);
                op.completed += _ => completedFired = true;
                await op;
            }
            finally { SceneManager.sceneLoaded -= OnLoaded; }
            Debug.Log($"[ContentLink.Tests] sceneLoaded events={events} op.isDone at sceneLoaded={doneAtEvent} sceneLoaded before completed={eventBeforeCompleted}");
            Assert.That(events, Is.EqualTo(1));
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1), "a direct SceneManager load is tracked too");
            var scene = SceneManager.GetSceneByLoadableSceneId(id);
            await Unload(scene);
        }
    }

    /// <summary>Probes that need directory C to be unregistered.</summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Scenes")]
    public class SceneProbeTests
    {
        [Test, Timeout(10000)]
        public async Task P1_Probe_UnregisteredId_EngineReaction()
        {
            int dirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
            Assert.That(ContentService.TryGetLoadableSceneId("Scenes/One", out var id), Is.True);
            Assert.That(ContentService.UnregisterDirectory(dirC), Is.True);
            Assert.That(ContentService.ContainsScene("Scenes/One"), Is.False);

            string reaction; AsyncOperation op = null;
            try { op = SceneManager.LoadSceneAsync(id, new LoadSceneParameters(LoadSceneMode.Additive)); reaction = op == null ? "null" : "op"; }
            catch (Exception e) { reaction = $"threw {e.GetType().Name}: {e.Message}"; }
            if (op != null)
            {
                for (int i = 0; i < 60 && !op.isDone; ++i) await Awaitable.NextFrameAsync();
                var got = SceneManager.GetSceneByLoadableSceneId(id);
                reaction += $" isDone={op.isDone} sceneLoaded={got.IsValid()}";
                if (got.IsValid()) await SceneManager.UnloadSceneAsync(got);
            }
            Debug.Log($"[ContentLink.Tests] SceneManager.LoadSceneAsync with an unregistered content id → {reaction}");

            LogAssert.Expect(LogType.Error, new Regex(@"\[ContentLink\] scene 'Scenes/One' is not in any registered ContentCatalog"));
            var ours = await ContentService.LoadSceneAsync("Scenes/One");
            Assert.That(ours.IsValid(), Is.False, "ContentLink refuses before the engine sees the id");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0));
        }
    }
}
