using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
    /// ContentSceneLink: the serialized, owner-bound scene link. Directory A holds Prefabs/Linked whose
    /// LinkHolder carries an inspector-assigned link "Scenes/One"; directory C holds the scene.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Scenes")]
    public class SceneLinkTests
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

        static async Task<bool> Until(Func<bool> cond, int frames = 30) { for (int i = 0; i < frames; ++i) { if (cond()) return true; await Awaitable.NextFrameAsync(); } return cond(); }

        // ───────────── serialized link on a prefab instance ─────────────

        [Test, Timeout(15000)]
        public async Task K1_SerializedLink_OnInstance_LoadsAndUnloads()
        {
            var scope = ContentScope.Root.CreateChild("K1");
            try
            {
                var inst = await scope.InstantiateAsync("Prefabs/Linked");
                var holder = inst.GetComponent<LinkHolder>();
                Assert.That(holder.level.Name, Is.EqualTo("Scenes/One"), "inspector-assigned name survives the content build");
                Assert.That(holder.level.RuntimeKeyIsValid(), Is.True);
                Assert.That(holder.level.IsLoaded, Is.False);
                Assert.That(holder.level.Scope, Is.Null);

                var scene = await holder.level.LoadAsync(holder);
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
                Assert.That(holder.level.IsLoaded, Is.True);
                Assert.That(holder.level.Scene.handle == scene.handle, Is.True);
                Assert.That(holder.level.Scope, Is.SameAs(inst.Scope()), "owned by the instance's scope");
                Assert.That(inst.Scope().SceneCount, Is.EqualTo(1));
                Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));

                await holder.level.UnloadAsync();
                Assert.That(holder.level.IsLoaded, Is.False);
                Assert.That(holder.level.Scope, Is.Null);
                Assert.That(inst.Scope().SceneCount, Is.EqualTo(0));
                Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True);
                Assert.That(scene.isLoaded, Is.False);

                Object.Destroy(inst);
            }
            finally { scope.Dispose(); }
        }

        // ───────────── double load and SetName rules ─────────────

        [Test, Timeout(15000)]
        public async Task K2_DoubleLoad_AndSetNameWhileLoaded_AreRefused()
        {
            var go = new GameObject("K2");
            var holder = go.AddComponent<LinkHolder>();
            holder.level.SetName("Scenes/Two");
            var first = await holder.level.LoadAsync(holder);
            Assert.That(first.isLoaded, Is.True);

            LogAssert.Expect(LogType.Error, new Regex(@"\[ContentLink\] scene 'Scenes/Two' is already loaded"));
            var again = await holder.level.LoadAsync(holder);
            Assert.That(again.handle == first.handle, Is.True, "the second call returns the loaded scene, loads nothing");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(1));

            LogAssert.Expect(LogType.Error, new Regex(@"\[ContentLink\] Unload scene 'Scenes/Two' before changing the name"));
            holder.level.SetName("Scenes/One");
            Assert.That(holder.level.Name, Is.EqualTo("Scenes/Two"));

            await holder.level.UnloadAsync();
            holder.level.SetName("Scenes/One");
            Assert.That(holder.level.Name, Is.EqualTo("Scenes/One"), "rename allowed once unloaded");
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True);
            Object.Destroy(go);
        }

        // ───────────── owner destroyed → scene unloaded ─────────────

        [Test, Timeout(15000)]
        public async Task K3_OwnerDestroyed_UnloadsTheScene()
        {
            var go = new GameObject("K3");
            var holder = go.AddComponent<LinkHolder>();
            holder.level.SetName("Scenes/One");
            var scene = await holder.level.LoadAsync(holder);
            Assert.That(scene.isLoaded, Is.True);

            Object.Destroy(go);
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True, "the owner's scope ended → scene unloaded");
            Assert.That(scene.isLoaded, Is.False);
            Assert.That(holder.level.IsLoaded, Is.False, "engine truth: the link sees the unload it did not start");
            Assert.That(holder.level.Scope, Is.Null);
        }

        // ───────────── owner rules ─────────────

        [Test, Timeout(15000)]
        public async Task K4_NullOrDisposedOwner_Throws()
        {
            var link = new ContentSceneLink("Scenes/One");
            bool argNull = false;
            try { await link.LoadAsync((ContentScope)null); } catch (ArgumentNullException) { argNull = true; }
            Assert.That(argNull, Is.True, "owner null → ArgumentNullException");

            var disposed = ContentScope.Root.CreateChild("K4");
            disposed.Dispose();
            bool objDisposed = false;
            try { await link.LoadAsync(disposed); } catch (ObjectDisposedException) { objDisposed = true; }
            Assert.That(objDisposed, Is.True, "disposed owner → ObjectDisposedException");
            Assert.That(link.IsLoading, Is.False);
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0));
        }

        // ───────────── UnloadAsync while a load is in flight ─────────────

        [Test, Timeout(15000)]
        public async Task K5_UnloadWhileLoading_ReturnsInvalid_AndUnloadsAfterCompletion()
        {
            var stage = ContentScope.Root.CreateChild("K5");
            var link = new ContentSceneLink("Scenes/One");
            var pending = link.LoadAsync(stage);
            Assert.That(link.IsLoading, Is.True);
            await link.UnloadAsync();
            Assert.That(link.IsLoading, Is.False, "the wait was cut short");

            var result = await pending;
            Assert.That(result.IsValid(), Is.False, "the superseded load reports nothing");
            Assert.That(link.IsLoaded, Is.False);
            Assert.That(stage.SceneCount, Is.EqualTo(0), "handed back to the scope and released");
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0, 60), Is.True, "the engine load completed and the scene was unloaded again");
            stage.Dispose();
        }

        // ───────────── explicit-scope owner via the ContentScope overload ─────────────

        [Test, Timeout(15000)]
        public async Task K6_ExplicitScopeOwner_DisposeUnloads_AndLinkFollows()
        {
            var stage = ContentScope.Root.CreateChild("K6");
            var link = new ContentSceneLink("Scenes/Two");
            var scene = await link.LoadAsync(stage);
            Assert.That(scene.isLoaded && link.IsLoaded, Is.True);
            Assert.That(link.Scope, Is.SameAs(stage));

            stage.Dispose();
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True);
            Assert.That(link.IsLoaded, Is.False);
            await link.UnloadAsync();   // no-op after the owner ended
            Assert.That(link.Scope, Is.Null);
        }
    }
}
