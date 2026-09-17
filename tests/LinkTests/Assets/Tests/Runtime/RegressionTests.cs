using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Regressions against the real engine for four review findings (concurrent loads of one key, id-loaded assets and
    /// directory unregistering, a link swap during an in-flight load, type mismatches), plus ownership transfer between
    /// scopes, prefab-component loads with inspector-assigned links and the directory-unregister contract.
    /// Directory A stays registered for the fixture; B is registered inside the tests that need it.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Regression")]
    public class RegressionTests
    {
        int m_DirA = -1;
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
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            m_Objects.Add(go);
            return go;
        }

        static async Task<int> FramesUntilLive(int expected, int maxFrames)
        {
            for (int i = 0; i <= maxFrames; ++i)
            {
                if (ContentService.LiveAssetCount == expected) return i;
                await TestUtil.NextFrame();
            }
            return -1;
        }

        static async Awaitable<T> Track<T>(Awaitable<T> load, Action done) where T : Object
        {
            var result = await load;
            done();
            return result;
        }

        // ───────────── same key loaded twice at once, and a sync Load while the async one is in flight ─────────────

        [Test, Timeout(10000)]
        public async Task R1_SameKeyConcurrentLoads_BothGetTheAsset_AndOneLease()
        {
            var scope = ContentScope.Root.CreateChild("R1");
            try
            {
                int order = 0, first = -1, second = -1;
                var w1 = Track(scope.LoadAsync<Texture2D>("Icons/single"), () => first = ++order);
                var w2 = Track(scope.LoadAsync<Texture2D>("Icons/single"), () => second = ++order);
                var r1 = await w1;
                var r2 = await w2;
                Debug.Log($"[ContentLink.Tests] Awaitable resume order: first call={first}, second call={second}");
                Assert.That(r1, Is.Not.Null, "first concurrent call");
                Assert.That(r2, Is.Not.Null, "second concurrent call");
                Assert.That(ReferenceEquals(r1, r2), Is.True, "both calls see the same object");
                Assert.That(scope.Count, Is.EqualTo(1));
            }
            finally { scope.Dispose(); }

            var scope2 = ContentScope.Root.CreateChild("R1sync");
            try
            {
                var pending = scope2.LoadAsync<Sprite>("Icons/single[single]");
                var sync = scope2.Load<Sprite>("Icons/single[single]");
                var async = await pending;
                Assert.That(sync, Is.Not.Null, "sync Load while the async load is in flight");
                Assert.That(async, Is.Not.Null, "async load after the sync one completed it");
                Assert.That(ReferenceEquals(sync, async), Is.True);
                Assert.That(scope2.Count, Is.EqualTo(1));
            }
            finally { scope2.Dispose(); }
        }

        // ───────────── id-loaded asset belongs to its own directory only ─────────────

        [Test, Timeout(10000)]
        public async Task R2_IdLoadedAsset_BlocksOnlyItsOwnDirectory()
        {
            var scope = ContentScope.Root.CreateChild("R2");
            var scope2 = ContentScope.Root.CreateChild("R2b");
            try
            {
                int dirB = ContentService.RegisterDirectory(TestContent.Dir("B"));

                Assert.That(ContentService.TryGetLoadableObjectId("Icons/single", out var id), Is.True);
                var handle = await ContentService.AcquireAsync<Texture2D>(id);
                Assert.That(handle.IsValid, Is.True, "id-based acquire from A");
                var tex = handle.Asset;
                Assert.That(tex, Is.Not.Null);
                scope.Attach(handle);

                Assert.That(ContentService.UnregisterDirectory(dirB), Is.True, "an id-loaded asset from A must not block unregistering B");
                Assert.That(ContentService.TryGetLoadableObjectId("Icons/other", out _), Is.False, "B's names are gone after unregister");
                Assert.That(handle.IsValid, Is.True);

                dirB = ContentService.RegisterDirectory(TestContent.Dir("B"));
                Assert.That(await scope2.LoadAsync<Texture2D>("Icons/other"), Is.Not.Null);
                Assert.That(ContentService.UnregisterDirectory(dirB, force: true), Is.True, "force unregister B while B has a live asset");
                Assert.That(handle.IsValid, Is.True, "force on B must not touch A's id-loaded lease");
                Assert.That(tex != null, Is.True, "A's asset is still alive");
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "only A's asset remains");

                LogAssert.Expect(LogType.Error, new Regex(@"still has 1 live asset\(s\)"));
                Assert.That(ContentService.UnregisterDirectory(m_DirA), Is.False, "A is refused while its own asset is held");
            }
            finally { scope.Dispose(); scope2.Dispose(); }
        }

        // ───────────── ContentLink swap while the previous load is in flight ─────────────

        [Test, Timeout(10000)]
        public async Task R3_ReleaseSetNameLoad_WhilePreviousLoadIsInFlight()
        {
            var go = NewObject("R3");
            var holder = go.AddComponent<LinkHolder>();
            holder.icon.SetName("Icons/single", "single");
            var first = holder.icon.LoadAsync(go);
            bool window = holder.icon.IsLoading;
            holder.icon.Release();
            holder.icon.SetName("Icons/sheet", "s0");
            var second = holder.icon.LoadAsync(go);
            var oldResult = await first;
            var newResult = await second;
            if (!window) Assert.Ignore("the first load completed synchronously — no in-flight window to test");

            Assert.That((holder.icon.Name, holder.icon.SubObjectName), Is.EqualTo(("Icons/sheet", "s0")));
            Assert.That(oldResult, Is.Null, "the released in-flight load returns null");
            Assert.That(newResult, Is.Not.Null, "the new load returns the new sprite");
            Assert.That(newResult.name, Is.EqualTo("s0"));
            Assert.That(ReferenceEquals(holder.icon.Asset, newResult), Is.True);
            ContentService.Collect();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "the old sprite's slot is released; only the new one is live");
            Assert.That(go.Scope().Count, Is.EqualTo(1), "the owner scope holds exactly one lease");
        }

        // ───────────── type mismatch is an error and nothing is held ─────────────

        [Test, Timeout(10000)]
        public async Task R4_TypeMismatch_LogsError_AndHoldsNothing()
        {
            var scope = ContentScope.Root.CreateChild("R4");
            try
            {
                LogAssert.Expect(LogType.Error, new Regex(@"'Icons/single' is Texture2D, not Sprite"));
                var sprite = await scope.LoadAsync<Sprite>("Icons/single");
                Assert.That(sprite, Is.Null);
                Assert.That(scope.Count, Is.EqualTo(0), "the scope must not keep a lease on a mismatched asset");
                ContentService.Collect();
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
            }
            finally { scope.Dispose(); }

            var go = NewObject("R4link");
            var holder = go.AddComponent<LinkHolder>();
            holder.icon.SetName("Icons/single");
            LogAssert.Expect(LogType.Error, new Regex(@"'Icons/single' is Texture2D, not Sprite"));
            var asset = await holder.icon.LoadAsync(go);
            Assert.That(asset, Is.Null);
            Assert.That(holder.icon.IsValid, Is.False, "ContentLink must not hold a lease on a mismatched asset");
            ContentService.Collect();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
        }

        // ───────────── Adopt / ShareTo rely on object identity from the engine ─────────────

        [Test, Timeout(10000)]
        public async Task R5_AdoptAndShareTo_TransferOwnership_BetweenScopes()
        {
            var a = ContentScope.Root.CreateChild("R5a");
            var b = ContentScope.Root.CreateChild("R5b");
            try
            {
                var sprite = await a.LoadAsync<Sprite>("Icons/sheet[s0]");
                var holder = await a.LoadAsync<LinkHolder>("Prefabs/Linked");   // component on the prefab asset
                var tex = await a.LoadAsync<Texture2D>("Icons/single");
                Assert.That(sprite, Is.Not.Null);
                Assert.That(holder, Is.Not.Null);
                Assert.That(tex, Is.Not.Null);

                Assert.That(b.Adopt(sprite), Is.True, "Adopt by sub-asset object (Loadable.Target identity)");
                Assert.That(b.Adopt(holder), Is.True, "Adopt by a component of the prefab asset");
                Assert.That(a.ShareTo(b, "Icons/single"), Is.True, "ShareTo by key");
                Assert.That(b.Count, Is.EqualTo(3));

                a.Dispose();
                ContentService.Collect();
                await TestUtil.NextFrame();
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(3), "the receiving scope keeps all three alive");
                Assert.That(sprite != null && holder != null && tex != null, Is.True, "objects survive the source scope");

                b.Dispose();
                int frames = await FramesUntilLive(0, 5);
                Assert.That(frames, Is.GreaterThanOrEqualTo(0), "released when the receiving scope ends");
            }
            finally { a.Dispose(); b.Dispose(); }
        }

        // ───────────── prefab loaded as a component type; inspector-assigned ContentLink survives the content build ─────────────

        [Test, Timeout(10000)]
        public async Task R6_PrefabAsComponent_AndInspectorAssignedLink_WorkOnAnInstance()
        {
            var scope = ContentScope.Root.CreateChild("R6");
            try
            {
                var holder = await scope.LoadAsync<LinkHolder>("Prefabs/Linked");
                Assert.That(holder, Is.Not.Null, "GetAsset<Component> path returns the component on the prefab asset");
                Assert.That((holder.icon.Name, holder.icon.SubObjectName), Is.EqualTo(("Icons/single", "single")), "inspector-assigned name and sprite name survived the content build");
                Assert.That(holder.icon.RuntimeKeyIsValid(), Is.True);

                var inst = await scope.InstantiateAsync("Prefabs/Linked");
                Assert.That(inst, Is.Not.Null);
                m_Objects.Add(inst);
                var instHolder = inst.GetComponent<LinkHolder>();
                var sprite = await instHolder.icon.LoadAsync(inst);
                Assert.That(sprite, Is.Not.Null, "ContentLinkSprite on the instance loads its sprite");
                Assert.That(sprite.name, Is.EqualTo("single"));
                Assert.That(instHolder.icon.IsValid, Is.True);
                Assert.That(instHolder.icon.Scope, Is.SameAs(inst.Scope()), "the instance owns the link's lease");
            }
            finally { scope.Dispose(); }
        }

        // ───────────── UnregisterDirectory contract ─────────────

        [Test, Timeout(10000)]
        public async Task E1_UnregisterWhileHeld_IsRefusedWithError()
        {
            var scope = ContentScope.Root.CreateChild("E1");
            try
            {
                Assert.That(await scope.LoadAsync<Texture2D>("Icons/single"), Is.Not.Null);
                LogAssert.Expect(LogType.Error, new Regex(@"still has 1 live asset\(s\):\s+Icons/single"));
                Assert.That(ContentService.UnregisterDirectory(m_DirA), Is.False);
                Assert.That(scope.Contains("Icons/single"), Is.True, "a refused unregister changes nothing");
            }
            finally { scope.Dispose(); }
        }

        [Test, Timeout(10000)]
        public async Task E2_ForceUnregister_InvalidatesHandles_AndUnloads()
        {
            int dirB = ContentService.RegisterDirectory(TestContent.Dir("B"));
            var scope = ContentScope.Root.CreateChild("E2");
            try
            {
                var handle = await ContentService.AcquireAsync<Texture2D>("Icons/other");
                Assert.That(handle.IsValid, Is.True);
                var tex = handle.Asset;
                scope.Attach(handle);

                Assert.That(ContentService.UnregisterDirectory(dirB, force: true), Is.True);
                Assert.That(handle.IsValid, Is.False, "every lease is invalidated by force");
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
                await TestUtil.NextFrame(); await TestUtil.NextFrame();
                if (TestEnv.ReleaseObservable) Assert.That(tex == null, Is.True, "the object is unloaded");
            }
            finally { scope.Dispose(); }
        }

        [Test, Timeout(10000)]
        public async Task E3_AfterForceUnregister_TheScopeApiStaysQuiet()
        {
            // The scope still holds entries whose leases force killed. They are not the user's doing: Count must not
            // count them, Release must not warn about a "double Dispose", and the tree dump must not list them.
            int dirB = ContentService.RegisterDirectory(TestContent.Dir("B"));
            var scope = ContentScope.Root.CreateChild("E3");
            int warnings = 0;
            void OnLog(string message, string stack, LogType type) { if (type == LogType.Warning && message.Contains("already released")) ++warnings; }
            Application.logMessageReceived += OnLog;   // the test framework does not fail on warnings, so count them
            try
            {
                Assert.That(await scope.LoadAsync<Texture2D>("Icons/other"), Is.Not.Null);
                var handle = await ContentService.AcquireAsync<Texture2D>("Icons/other");
                scope.Attach(handle);
                Assert.That(scope.Count, Is.EqualTo(2), "one keyed, one attached");

                Assert.That(ContentService.UnregisterDirectory(dirB, force: true), Is.True);
                Assert.That(scope.Contains("Icons/other"), Is.False);
                Assert.That(scope.Count, Is.EqualTo(0), "Count must count live leases only");
                Assert.That(ContentScope.DumpTree(), Does.Not.Contain("Icons/other"), "the dump must not list a dead key");
                Assert.That(scope.Release("Icons/other"), Is.False, "releasing a key whose lease force killed is a no-op");
                Assert.That(warnings, Is.EqualTo(0), "no 'already released' warning for a lease the user never double-disposed");
            }
            finally { Application.logMessageReceived -= OnLog; scope.Dispose(); }
        }
    }
}
