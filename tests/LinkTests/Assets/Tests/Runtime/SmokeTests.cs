using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Smoke tests against the real content-directory pipeline.
    /// The content is built beforehand by the EditMode step (TestContentBuildTests / menu); the prebuild hook only validates it.
    /// Directory A stays registered for the fixture; B is registered inside the tests that need it.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Smoke")]
    public class SmokeTests
    {
        static readonly string[] k_NamesA =
        {
            "Icons/single", "Icons/single[single]", "Icons/sheet", "Icons/sheet[s0]", "Icons/sheet[s1]",
            "Prefabs/Plain", "Prefabs/Linked", "Materials/Plain", "Audio/tone",
        };

        int m_DirA = -1;

        [OneTimeSetUp]
        public void RegisterA()
        {
            Assert.That(Directory.Exists(TestContent.Dir("A")), Is.True,
                $"content directory A is missing at '{TestContent.Dir("A")}' — run the EditMode test TestContentBuildTests (or Window/xpTURN/ContentLink/Tests/Build Test Content) first");
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
        }

        [OneTimeTearDown]
        public void UnregisterA()
        {
            if (m_DirA < 0) return;
            bool ok = ContentService.UnregisterDirectory(m_DirA);
            if (!ok) ContentService.UnregisterDirectory(m_DirA, force: true);   // contain the damage for later fixtures
            m_DirA = -1;
            Assert.That(ok, Is.True, "directory A still had live assets at fixture end (leak)");
        }

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        // ───────────── build hook output and catalog names ─────────────

        [Test, Timeout(10000)]
        public void S1_BuildHook_BuiltBothDirectories_AndCatalogAHasEveryName()
        {
            Assert.That(Directory.Exists(TestContent.Dir("B")), Is.True, "content directory B is missing");
            foreach (var name in k_NamesA)
            {
                Assert.That(ContentService.Contains(name), Is.True, $"catalog A has no entry '{name}'");
                Assert.That(ContentService.TryGetLoadableObjectId(name, out var id), Is.True, $"no id for '{name}'");
                Assert.That(id.IsValid, Is.True, $"invalid id for '{name}'");
            }
            Assert.That(ContentService.Contains("Icons/nope"), Is.False);
            Assert.That(ContentService.TryGetLoadableObjectId("Icons/nope", out _), Is.False);
        }

        // ───────────── load every type through Loadable<Object> ─────────────

        [Test, Timeout(10000)]
        public async Task S2_LoadAsync_EveryType_ReturnsTheRightObject()
        {
            var scope = ContentScope.Root.CreateChild("S2");
            try
            {
                var tex = await scope.LoadAsync<Texture2D>("Icons/single");
                var single = await scope.LoadAsync<Sprite>("Icons/single[single]");
                var sheetTex = await scope.LoadAsync<Texture2D>("Icons/sheet");
                var s0 = await scope.LoadAsync<Sprite>("Icons/sheet[s0]");
                var s1 = await scope.LoadAsync<Sprite>("Icons/sheet[s1]");
                var plain = await scope.LoadAsync<GameObject>("Prefabs/Plain");
                var linked = await scope.LoadAsync<GameObject>("Prefabs/Linked");
                var mat = await scope.LoadAsync<Material>("Materials/Plain");
                var clip = await scope.LoadAsync<AudioClip>("Audio/tone");

                Assert.That(tex, Is.Not.Null, "Texture2D Icons/single");
                Assert.That(single, Is.Not.Null, "Sprite Icons/single[single]");
                Assert.That(sheetTex, Is.Not.Null, "Texture2D Icons/sheet");
                Assert.That(s0, Is.Not.Null, "Sprite Icons/sheet[s0]");
                Assert.That(s1, Is.Not.Null, "Sprite Icons/sheet[s1]");
                Assert.That(plain, Is.Not.Null, "GameObject Prefabs/Plain");
                Assert.That(linked, Is.Not.Null, "GameObject Prefabs/Linked");
                Assert.That(mat, Is.Not.Null, "Material Materials/Plain");
                Assert.That(clip, Is.Not.Null, "AudioClip Audio/tone");

                Assert.That(tex.name, Is.EqualTo("single"));
                Assert.That(single.name, Is.EqualTo("single"));
                Assert.That(s0.name, Is.EqualTo("s0"));
                Assert.That(s1.name, Is.EqualTo("s1"));
                Assert.That(ReferenceEquals(s0, s1), Is.False, "sheet sprites must be distinct objects");
                Assert.That(single.texture, Is.SameAs(tex), "single sprite belongs to Icons/single");
                Assert.That(plain.name, Is.EqualTo("Plain"));
                Assert.That(linked.GetComponent<LinkHolder>(), Is.Not.Null, "Linked prefab keeps its LinkHolder");
                Assert.That(mat.name, Is.EqualTo("Plain"));
                Assert.That(clip.name, Is.EqualTo("tone"));
                Assert.That(scope.Count, Is.EqualTo(9));
            }
            finally { scope.Dispose(); }
        }

        // ───────────── release → unregister, and whether the engine unload is observable ─────────────

        [Test, Timeout(10000)]
        public async Task S3a_ReleaseAfterScopeDispose_LetsDirectoryBUnregister()
        {
            var r = await LoadReleaseUnregisterB();
            Assert.That(r.loaded, Is.True, "Texture2D Icons/other loaded from B");
            Assert.That(r.unregistered, Is.True, "B refused to unregister after the scope released its only asset");
        }

        [Test, Timeout(10000)]
        public async Task S3b_ReleasedObject_IsUnloaded_WhereObservable()
        {
            var r = await LoadReleaseUnregisterB();
            Assume.That(r.loaded && r.unregistered, Is.True);
            Debug.Log($"[ContentLink.Tests] editor={Application.isEditor} unloadedAfterRelease={r.unloadedAfterRelease} (frames={r.framesToUnload}) unloadedAfterUnregister={r.unloadedAfterUnregister} → ReleaseObservable={TestEnv.ReleaseObservable}");
            if (!TestEnv.ReleaseObservable)
                Assert.Ignore($"unload not asserted on this platform (observed unloadedAfterRelease={r.unloadedAfterRelease})");
            Assert.That(r.unloadedAfterRelease, Is.True, "object is still alive after its last lease was released (before the directory was unregistered)");
        }

        struct ReleaseObservation { public bool loaded, unregistered, unloadedAfterRelease, unloadedAfterUnregister; public int framesToUnload; }

        static async Task<ReleaseObservation> LoadReleaseUnregisterB()
        {
            var r = new ReleaseObservation();
            int dirB = ContentService.RegisterDirectory(TestContent.Dir("B"));
            Texture2D tex;
            var scope = ContentScope.Root.CreateChild("S3");
            try { tex = await scope.LoadAsync<Texture2D>("Icons/other"); }
            finally { scope.Dispose(); }
            r.loaded = tex != null;
            ContentService.Collect();                                  // Loadable.Release() happens here
            for (r.framesToUnload = 0; r.framesToUnload < 10 && tex != null; ++r.framesToUnload)   // Unity fake-null once the native object is gone
                await TestUtil.NextFrame();
            r.unloadedAfterRelease = tex == null;
            r.unregistered = ContentService.UnregisterDirectory(dirB);
            if (!r.unregistered) ContentService.UnregisterDirectory(dirB, force: true);
            await TestUtil.NextFrame();
            r.unloadedAfterUnregister = tex == null;
            return r;
        }

        // ───────────── what the engine does with a sync Load while the async load is in flight ─────────────

        [Test, Timeout(10000)]
        public async Task S4_SyncLoadDuringAsyncLoad_Recorded()
        {
            var scope = ContentScope.Root.CreateChild("S4");
            try
            {
                var pending = scope.LoadAsync<AudioClip>("Audio/tone");
                bool completedSynchronously = scope.Contains("Audio/tone");

                AudioClip sync = null; string syncOutcome;
                try { sync = scope.Load<AudioClip>("Audio/tone"); syncOutcome = sync != null ? "asset" : "null"; }
                catch (Exception e) { syncOutcome = $"threw {e.GetType().Name}: {e.Message}"; }

                AudioClip async = null; string asyncOutcome;
                try { async = await pending; asyncOutcome = async != null ? "asset" : "null"; }
                catch (Exception e) { asyncOutcome = $"threw {e.GetType().Name}: {e.Message}"; }

                Debug.Log($"[ContentLink.Tests] asyncCompletedSynchronously={completedSynchronously} syncLoad={syncOutcome} asyncLoad={asyncOutcome} same={ReferenceEquals(sync, async)} scopeCount={scope.Count}");
                Assert.That(scope.Count, Is.LessThanOrEqualTo(1), "the key must be held at most once (dedupe)");
            }
            finally { scope.Dispose(); }
        }
    }
}
