using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Scope lifetimes against the real engine: OnDestroy, scene unload, moved objects, Instantiate inheritance,
    /// PlayerLoop collect, release delay, and cancellation of an in-flight load. Directory A stays registered for the fixture.
    /// Rule: move a GameObject into the test scene BEFORE calling Scope() — the scope binds to go.scene at that moment.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Lifetime")]
    public class LifetimeTests
    {
        int m_DirA = -1;
        readonly List<Scene> m_Scenes = new List<Scene>();
        readonly List<GameObject> m_Objects = new List<GameObject>();

        [OneTimeSetUp]
        public void RegisterA()
        {
            Assert.That(System.IO.Directory.Exists(TestContent.Dir("A")), Is.True,
                $"content directory A is missing at '{TestContent.Dir("A")}' — run the EditMode test TestContentBuildTests first");
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
        }

        [OneTimeTearDown]
        public void UnregisterA()
        {
            if (m_DirA < 0) return;
            bool ok = ContentService.UnregisterDirectory(m_DirA);
            if (!ok) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = -1;
            Assert.That(ok, Is.True, "directory A still had live assets at fixture end (leak)");
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            TestUtil.RestoreStatics();
            foreach (var go in m_Objects) if (go != null) Object.Destroy(go);
            m_Objects.Clear();
            foreach (var scene in m_Scenes)
                if (scene.IsValid() && scene.isLoaded)
                {
                    var op = SceneManager.UnloadSceneAsync(scene);
                    while (op != null && !op.isDone) yield return null;
                }
            m_Scenes.Clear();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        // ───────────── helpers ─────────────

        Scene NewScene(string tag)
        {
            var scene = SceneManager.CreateScene($"{tag}_{Guid.NewGuid():N}");
            m_Scenes.Add(scene);
            return scene;
        }

        static async Task Unload(Scene scene)
        {
            var op = SceneManager.UnloadSceneAsync(scene);
            while (op != null && !op.isDone) await TestUtil.NextFrame();
        }

        GameObject NewObject(string name, Scene scene, bool active = true)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);   // before Scope(): the scope binds to go.scene
            if (!active) go.SetActive(false);
            m_Objects.Add(go);
            return go;
        }

        /// <summary>Frames until LiveAssetCount reaches <paramref name="expected"/>, or -1 after <paramref name="maxFrames"/>.</summary>
        static async Task<int> FramesUntilLive(int expected, int maxFrames)
        {
            for (int i = 0; i <= maxFrames; ++i)
            {
                if (ContentService.LiveAssetCount == expected) return i;
                await TestUtil.NextFrame();
            }
            return -1;
        }

        // ───────────── GameObject destroyed → released by the player loop ─────────────

        [Test, Timeout(15000)]
        public async Task L1_DestroyedGameObject_ReleasesAtEndOfFrame_WithoutManualCollect()
        {
            var scene = NewScene("L1");
            var go = NewObject("Holder", scene);
            var tex = await go.Scope().LoadAsync<Texture2D>("Icons/single");
            Assert.That(tex, Is.Not.Null);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1));

            Object.Destroy(go);
            int frames = await FramesUntilLive(0, 3);
            Debug.Log($"[ContentLink.Tests] released {frames} frame(s) after Destroy (no manual Collect)");
            Assert.That(frames, Is.InRange(0, 2), "PlayerLoop collect must release within 2 frames of Destroy");
        }

        // ───────────── scene unload cascades; a forgotten explicit scope warns ─────────────

        [Test, Timeout(15000)]
        public async Task L2_SceneUnload_ReleasesEverything_AndWarnsForForgottenExplicitScope()
        {
            var scene = NewScene("L2");
            var forgotten = ContentScope.ForScene(scene).CreateChild("ForgottenPopup");
            Assert.That(await forgotten.LoadAsync<Texture2D>("Icons/single"), Is.Not.Null);
            var go = NewObject("Holder", scene);
            Assert.That(await go.Scope().LoadAsync<Material>("Materials/Plain"), Is.Not.Null);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(2));

            LogAssert.Expect(LogType.Warning, new Regex($@"scope '.*ForgottenPopup' was not disposed; released 1 asset\(s\) when '{Regex.Escape(scene.name)}' ended"));
            await Unload(scene);

            Assert.That(forgotten.IsDisposed, Is.True, "explicit scope reclaimed by the scene");
            int frames = await FramesUntilLive(0, 5);
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "both assets released after scene unload");
        }

        // ───────────── never-activated object gets no OnDestroy; the scene unload reclaims it ─────────────

        [Test, Timeout(15000)]
        public async Task L3_NeverActivatedObject_IsReclaimedBySceneUnload_NotByDestroy()
        {
            var scene = NewScene("L3");
            var go = NewObject("PooledTemplate", scene, active: false);
            var scope = go.Scope();                                   // component added while inactive: Awake never runs
            Assert.That(scope.Load<Texture2D>("Icons/single"), Is.Not.Null);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1));

            Object.Destroy(go);
            await TestUtil.NextFrame(); await TestUtil.NextFrame(); await TestUtil.NextFrame();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "no OnDestroy for a never-activated object → still held (bounded over-retention)");
            Assert.That(scope.IsDisposed, Is.False);

            await Unload(scene);
            int frames = await FramesUntilLive(0, 5);
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "scene unload cascade reclaims the orphan scope");
            Assert.That(scope.IsDisposed, Is.True);
        }

        // ───────────── moved objects and DontDestroyOnLoad keep their assets ─────────────

        [Test, Timeout(15000)]
        public async Task L4_MovedAndDontDestroyOnLoadObjects_KeepAssets_WhenOriginalSceneUnloads()
        {
            var sceneA = NewScene("L4a");
            var sceneB = NewScene("L4b");
            var mover = NewObject("Mover", sceneA);
            var ddol = NewObject("Hud", sceneA);
            var movedTex = await mover.Scope().LoadAsync<Texture2D>("Icons/single");
            var hudMat = await ddol.Scope().LoadAsync<Material>("Materials/Plain");
            Assert.That(movedTex, Is.Not.Null);
            Assert.That(hudMat, Is.Not.Null);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(2));

            SceneManager.MoveGameObjectToScene(mover, sceneB);
            Object.DontDestroyOnLoad(ddol);
            await Unload(sceneA);
            await TestUtil.NextFrame(); await TestUtil.NextFrame();

            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(2), "assets of surviving objects must not be released");
            Assert.That(movedTex != null && hudMat != null, Is.True, "surviving objects' assets are still alive");
            Assert.That(mover.Scope().Parent.Name, Is.EqualTo(sceneB.name), "scope handed over to the scene the object lives in");
            Assert.That(ddol.Scope().Parent.Name, Is.EqualTo("DontDestroyOnLoad"));

            await Unload(sceneB);
            int frames = await FramesUntilLive(1, 5);
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "moved object released when its new scene unloads; DDOL still held");
            Object.Destroy(ddol);
            frames = await FramesUntilLive(0, 5);
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "DDOL object released when destroyed");
        }

        // ───────────── InstantiateAsync → clones inherit the lease; inactive clones take it on activation ─────────────

        [Test, Timeout(15000)]
        public async Task L5_InstantiateAsync_ClonesInheritTheLease_InactiveClonesOnActivation()
        {
            var scene = NewScene("L5");
            var requester = ContentScope.ForScene(scene).CreateChild("Spawner");
            var inst = await requester.InstantiateAsync("Prefabs/Plain");
            Assert.That(inst, Is.Not.Null);
            m_Objects.Add(inst);
            requester.Dispose();
            await TestUtil.NextFrame();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "the instance, not the requesting scope, holds the prefab");
            Assert.That(inst.Scope().Contains("Prefabs/Plain"), Is.True);

            var clone = Object.Instantiate(inst);
            m_Objects.Add(clone);
            Assert.That(clone.Scope().Contains("Prefabs/Plain"), Is.True, "active clone inherits the key in Awake");
            Object.Destroy(inst);
            await TestUtil.NextFrame(); await TestUtil.NextFrame();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "clone keeps the prefab alive after the original is destroyed");

            var parent = new GameObject("InactiveParent");
            m_Objects.Add(parent);
            parent.SetActive(false);
            var lateClone = Object.Instantiate(clone, parent.transform);   // Awake deferred until activation
            Assert.That(lateClone.GetComponent<ContentScopeOwner>(), Is.Not.Null, "owner component is cloned");
            Object.Destroy(clone);
            int frames = await FramesUntilLive(0, 5);
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "with only an inactive clone left, the prefab is released");

            parent.SetActive(true);                                        // Awake now: original gone → synchronous reload
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "activated clone re-acquires the prefab");
            Assert.That(lateClone.Scope().Contains("Prefabs/Plain"), Is.True);

            Object.Destroy(parent);
            frames = await FramesUntilLive(0, 5);
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "released when the late clone is destroyed");
        }

        // ───────────── AutoCollect off / ReleaseDelayFrames ─────────────

        [Test, Timeout(15000)]
        public async Task L6_AutoCollectOff_HoldsUntilManualCollect_AndReleaseDelayFramesHolds()
        {
            ContentService.AutoCollect = false;
            var scope = ContentScope.Root.CreateChild("L6a");
            Assert.That(await scope.LoadAsync<Texture2D>("Icons/single"), Is.Not.Null);
            scope.Dispose();
            await TestUtil.NextFrame(); await TestUtil.NextFrame(); await TestUtil.NextFrame();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "nothing is released while AutoCollect is off");
            ContentService.Collect();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "manual Collect releases");
            ContentService.AutoCollect = true;

            ContentService.ReleaseDelayFrames = 2;
            scope = ContentScope.Root.CreateChild("L6b");
            Assert.That(await scope.LoadAsync<Texture2D>("Icons/single"), Is.Not.Null);
            scope.Dispose();
            int frames = await FramesUntilLive(0, 6);
            Debug.Log($"[ContentLink.Tests] ReleaseDelayFrames=2 → released after {frames} frame(s)");
            Assert.That(frames, Is.InRange(2, 4), "held for at least the delay, then released");
            ContentService.ReleaseDelayFrames = 0;
        }

        // ───────────── scope ends while a load is in flight ─────────────

        [Test, Timeout(15000)]
        public async Task L7_ScopeDisposedWhileLoading_ThrowsCanceled_AndLeaksNothing()
        {
            var scope = ContentScope.Root.CreateChild("Popup");
            var pending = scope.LoadAsync<AudioClip>("Audio/tone");
            scope.Dispose();

            try
            {
                var clip = await pending;
                Assert.Fail($"expected OperationCanceledException, got {(clip != null ? clip.name : "null")}");
            }
            catch (OperationCanceledException) { }

            int frames = await FramesUntilLive(0, 30);
            Debug.Log($"[ContentLink.Tests] engine load finished and slot released {frames} frame(s) after the cancelled request");
            Assert.That(frames, Is.GreaterThanOrEqualTo(0), "the lease returned on cancel; the engine load completes and is collected");
        }
    }
}
