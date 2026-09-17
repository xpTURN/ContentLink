using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Scene lifetime and regressions against the real engine: Single-mode transitions, cancellation,
    /// scene dependencies shared with catalog assets, and scope ownership of loaded scenes.
    /// Directories A (assets) and C (scenes) are registered for the fixture.
    /// Single-mode loads close the UTF bootstrap scene too, so tests that use them leave an empty scene behind (Keep()).
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Scenes")]
    public class SceneLifetimeTests
    {
        int m_DirA = -1, m_DirC = -1;

        [OneTimeSetUp]
        public void Register()
        {
            foreach (var d in new[] { "A", "C" })
                Assert.That(Directory.Exists(TestContent.Dir(d)), Is.True, $"content directory {d} is missing — run the EditMode test TestContentBuildTests first");
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
            m_DirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
        }

        [OneTimeTearDown]
        public void Unregister()
        {
            bool okC = m_DirC < 0 || ContentService.UnregisterDirectory(m_DirC);
            bool okA = m_DirA < 0 || ContentService.UnregisterDirectory(m_DirA);
            if (!okC && m_DirC >= 0) ContentService.UnregisterDirectory(m_DirC, force: true);
            if (!okA && m_DirA >= 0) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = m_DirC = -1;
            Assert.That(okA && okC, Is.True, "a directory still had live assets or open scenes at fixture end");
        }

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            var open = new List<ContentService.SceneInfo>();
            ContentService.SnapshotScenes(open);
            if (open.Count > 0 && SceneManager.loadedSceneCount <= open.Count) Keep();   // never unload the last scene
            foreach (var info in open)
                if (info.Scene.IsValid() && info.Scene.isLoaded) SceneManager.UnloadSceneAsync(info.Scene);
            for (int i = 0; i < 30 && (ContentService.LiveAssetCount != 0 || ContentService.LoadedSceneCount != 0); ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0), "content scenes remain loaded after the test");
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        static Scene Keep() => SceneManager.CreateScene($"Keep_{Guid.NewGuid():N}");
        static async Task Unload(Scene scene) { var op = SceneManager.UnloadSceneAsync(scene); if (op != null) await op; }
        static async Task Frames(int n) { for (int i = 0; i < n; ++i) await Awaitable.NextFrameAsync(); }
        static async Task<bool> Until(Func<bool> cond, int frames = 30) { for (int i = 0; i < frames; ++i) { if (cond()) return true; await Awaitable.NextFrameAsync(); } return cond(); }
        static Sprite SpriteIn(Scene scene) => scene.GetRootGameObjects()[0].GetComponent<SpriteRenderer>().sprite;

        // ───────────── Single-mode transition ─────────────

        [Test, Timeout(15000)]
        public async Task R2_SingleLoad_ClosesPreviousContentScene_AndLoadableHeldAssetsSurvive()
        {
            var holder = ContentScope.Root.CreateChild("R2Hold");
            var tex = await holder.LoadAsync<Texture2D>("Icons/single");
            Assert.That(tex, Is.Not.Null);

            var one = await ContentService.LoadSceneAsync("Scenes/One");
            var inOne = ContentScope.ForScene(one).CreateChild("InOne");
            var clip = await inOne.LoadAsync<AudioClip>("Audio/tone");
            Assert.That(clip, Is.Not.Null);
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));

            LogAssert.Expect(LogType.Warning, new Regex(@"\[ContentLink\] scope 'Root/One/InOne' was not disposed"));
            var two = await ContentService.LoadSceneAsync("Scenes/Two", LoadSceneMode.Single);
            Assert.That(two.IsValid() && two.isLoaded, Is.True, "Single-mode content scene loaded");
            Assert.That(one.isLoaded, Is.False, "the previous content scene was closed by the Single load");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1), "only the new scene is tracked");
            Assert.That(inOne.IsDisposed, Is.True, "the closed scene's scope cascaded");
            Assert.That(await Until(() => ContentService.LiveAssetCount == 1), Is.True, "Audio/tone released after the cascade");
            await Frames(3);   // give Resources.UnloadUnusedAssets (automatic after a Single load) time to run
            Debug.Log($"[ContentLink.Tests] after Single load: loadedSceneCount={SceneManager.loadedSceneCount} texAlive={tex != null} clipAlive={clip != null} spriteInTwo={SpriteIn(two) != null}");
            Assert.That(tex, Is.Not.Null, "an asset held by a Loadable survives the automatic UnloadUnusedAssets");
            Assert.That(holder.Contains("Icons/single"), Is.True);
            Assert.That(SpriteIn(two), Is.Not.Null);

            Keep();
            await Unload(two);
            holder.Dispose();
        }

        // ───────────── cancellation ─────────────

        [Test, Timeout(15000)]
        public async Task R4_CancelAdditive_UnloadsAfterCompletion_CancelSingle_KeepsScene()
        {
            ContentService.TryGetLoadableSceneId("Scenes/One", out var oneId);
            ContentService.TryGetLoadableSceneId("Scenes/Two", out var twoId);

            var cts = new CancellationTokenSource();
            var pending = ContentService.LoadSceneAsync("Scenes/One", LoadSceneMode.Additive, cts.Token);
            cts.Cancel();
            bool cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Assert.That(cancelled, Is.True, "cancel → OperationCanceledException");
            bool sawTracked = false; int framesToGone = 0;
            for (; framesToGone < 60; ++framesToGone)
            {
                if (ContentService.LoadedSceneCount == 1) sawTracked = true;
                if (sawTracked && ContentService.LoadedSceneCount == 0 && !SceneManager.GetSceneByLoadableSceneId(oneId).IsValid()) break;
                await Awaitable.NextFrameAsync();
            }
            Debug.Log($"[ContentLink.Tests] additive cancel: sawTracked={sawTracked} gone after {framesToGone} frame(s)");
            Assert.That(sawTracked, Is.True, "the engine load still completed (not cancellable) and was tracked");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0), "the cancelled additive scene was unloaded again");
            Assert.That(SceneManager.GetSceneByLoadableSceneId(oneId).IsValid(), Is.False);

            cts = new CancellationTokenSource();
            LogAssert.Expect(LogType.Warning, new Regex(@"\[ContentLink\] load of scene 'Scenes/Two' was cancelled after a Single-mode load started"));
            pending = ContentService.LoadSceneAsync("Scenes/Two", LoadSceneMode.Single, cts.Token);
            cts.Cancel();
            cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Assert.That(cancelled, Is.True);
            Assert.That(await Until(() => SceneManager.GetSceneByLoadableSceneId(twoId).isLoaded, 60), Is.True, "the Single scene finished loading");
            await Frames(2);
            var two = SceneManager.GetSceneByLoadableSceneId(twoId);
            Assert.That(two.isLoaded, Is.True, "a cancelled Single-mode scene stays loaded (it is the last scene)");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));

            Keep();
            await Unload(two);
        }

        // ───────────── scene dependency shared with a catalog asset ─────────────

        [Test, Timeout(15000)]
        public async Task R5_SceneSprite_IsTheCatalogSprite_AndOutlivesTheScene()
        {
            var one = await ContentService.LoadSceneAsync("Scenes/One");
            var inScene = SpriteIn(one);
            Assert.That(inScene, Is.Not.Null);

            var scope = ContentScope.Root.CreateChild("R5");
            var asset = await scope.LoadAsync<Sprite>("Icons/shared[shared]");
            Assert.That(asset, Is.Not.Null);
            Debug.Log($"[ContentLink.Tests] same object={ReferenceEquals(inScene, asset)} sceneSprite={inScene.GetEntityId()} asset={asset.GetEntityId()}");
            Assert.That(ReferenceEquals(inScene, asset), Is.True, "one copy — the scene references the same sprite object the catalog entry loads");

            await Unload(one);
            await Frames(3);
            Assert.That(asset, Is.Not.Null, "unloading the scene does not touch the Loadable-held sprite");
            Assert.That(scope.Contains("Icons/shared[shared]"), Is.True);

            scope.Dispose();
            Assert.That(await Until(() => ContentService.LiveAssetCount == 0), Is.True);
            await Frames(3);
            Assert.That(asset == null, Is.True, "released → unloaded (ReleaseObservable)");
        }

        // ───────────── scope ownership ─────────────

        [Test, Timeout(15000)]
        public async Task L1_ExplicitScopeOwner_DisposeUnloadsTheScene()
        {
            var stage = ContentScope.Root.CreateChild("L1");
            var owned = await stage.LoadSceneAsync("Scenes/One");
            Assert.That(owned.IsValid() && owned.isLoaded, Is.True);
            Assert.That(stage.SceneCount, Is.EqualTo(1));
            Assert.That(stage.Count, Is.EqualTo(0), "scenes are not counted as assets");
            Assert.That(ContentScope.DumpTree(), Does.Contain("scenes: Scenes/One"));
            var sceneScope = ContentScope.ForScene(owned);

            stage.Dispose();
            Assert.That(stage.SceneCount, Is.EqualTo(0));
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True, "the owned scene was unloaded when the scope ended");
            Assert.That(owned.isLoaded, Is.False);
            Assert.That(sceneScope.IsDisposed, Is.True, "the scene's own scope ended with the scene");
        }

        [Test, Timeout(15000)]
        public async Task L2_GameObjectOwner_DestroyUnloadsTheScene()
        {
            var go = new GameObject("L2Owner");
            var owned = await go.Scope().LoadSceneAsync("Scenes/Two");
            Assert.That(owned.isLoaded, Is.True);
            Object.Destroy(go);
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True, "destroying the owner unloads the scene");
        }

        [Test, Timeout(15000)]
        public async Task L3_ObjectInsideAnOwnedScene_OwningAnotherInstance_UnloadsInCascade_WithoutErrors()
        {
            var first = await ContentService.LoadSceneAsync("Scenes/One");
            var inner = new GameObject("L3Inner");
            SceneManager.MoveGameObjectToScene(inner, first);
            var second = await inner.Scope().LoadSceneAsync("Scenes/One");
            Assert.That(second.handle == first.handle, Is.False);
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(2));

            await Unload(first);   // destroys inner → its scope ends → the second instance is unloaded; no double-unload errors
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True);
            Assert.That(second.isLoaded, Is.False);
        }

        [Test, Timeout(15000)]
        public async Task L4_LastLoadedScene_StaysWithWarning_WhenItsOwnerEnds()
        {
            var two = await ContentService.LoadSceneAsync("Scenes/Two", LoadSceneMode.Single);   // closes everything else
            var stage = ContentScope.Root.CreateChild("L4");
            var one = await stage.LoadSceneAsync("Scenes/One");
            await Unload(two);
            Debug.Log($"[ContentLink.Tests] owned scene is the last one: loadedSceneCount={SceneManager.loadedSceneCount} sceneCount={SceneManager.sceneCount}");
            Assert.That(SceneManager.loadedSceneCount, Is.EqualTo(1));

            LogAssert.Expect(LogType.Warning, new Regex(@"\[ContentLink\] scene 'Scenes/One' is the last loaded scene and stays loaded"));
            stage.Dispose();
            await Frames(3);
            Assert.That(one.isLoaded, Is.True, "the last loaded scene is kept");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));

            Keep();
            await Unload(one);
        }

        [Test, Timeout(15000)]
        public async Task L5_ReleaseScene_UnloadsEarly_AndSecondCallIsFalse()
        {
            var stage = ContentScope.Root.CreateChild("L5");
            var owned = await stage.LoadSceneAsync("Scenes/Two");
            Assert.That(stage.ReleaseScene(owned), Is.True);
            Assert.That(stage.SceneCount, Is.EqualTo(0));
            Assert.That(stage.ReleaseScene(owned), Is.False, "no longer owned");
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True);
            stage.Dispose();
        }
        [UnityTest, Timeout(60000)]
        public IEnumerator L9_GameObjectScopesDestroyedInRandomOrder_ThenSceneUnload_LeavesNothing()
        {
            // the scope tree's child list is edited in whatever order objects die; none of it may leak or log.
            var scene = SceneManager.CreateScene("O1Random");
            var objects = new List<GameObject>();
            for (int i = 0; i < 300; ++i)
            {
                var go = new GameObject("o" + i);
                SceneManager.MoveGameObjectToScene(go, scene);
                Assert.That(go.Scope().Load<Texture2D>("Icons/single"), Is.Not.Null);
                objects.Add(go);
            }
            var rnd = new System.Random(7);
            for (int n = 0; n < 100; ++n) { int i = rnd.Next(objects.Count); Object.Destroy(objects[i]); objects.RemoveAt(i); }
            yield return null;   // the destroys land at the end of the frame
            Assert.That(ContentScope.ForScene(scene).Children.Count, Is.EqualTo(200), "the survivors' scopes are what the scene scope lists");
            var op = SceneManager.UnloadSceneAsync(scene);
            while (op != null && !op.isDone) yield return null;
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i) { ContentService.Collect(); yield return null; }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "the scene unload must release every survivor's asset");
            LogAssert.NoUnexpectedReceived();
        }

    }

    /// <summary>
    /// Quit probe. Runs last (fixture name) and deliberately leaves a content scene open and directory C registered
    /// when Play Mode ends, so Shutdown takes the path that skips unregistering a directory that still has an open scene.
    /// Whether the next session in the same domain can register C again is what the editor menu
    /// Window/ContentLink/Tests/Run PlayMode Twice tells.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Scenes")]
    public class ZSceneQuitProbe
    {
        [Test, Timeout(15000)]
        public async Task Z1_LeaveSceneOpenAtQuit_D9()
        {
            int dirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
            var stage = ContentScope.Root.CreateChild("QuitProbe");
            var owned = await stage.LoadSceneAsync("Scenes/One");
            Assert.That(owned.isLoaded, Is.True);
            Debug.Log($"[ContentLink.Tests] leaving 'Scenes/One' open with directory C (index {dirC}) registered at the end of the run (Shutdown skips a directory with an open scene)");
        }
    }
}
