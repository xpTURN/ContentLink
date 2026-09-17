using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// ContentRef against built content directories. The builder records each entry's GUID and sprite name, so a
    /// reference resolves to the slot a name load uses — in the editor and in a player. The GUIDs below are the committed
    /// .meta GUIDs of the test content; in the editor F0 checks them against the AssetDatabase, so a changed .meta fails
    /// there and not as a puzzling miss.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("ContentRef")]
    public class ContentRefTests
    {
        const string k_SingleGuid = "2db49f3267d3454694e93fa3d8354b29";     // TestContent/A/Icons/single.png
        const string k_SheetGuid = "a36e9cb345c645a8b51669d22a848969";      // TestContent/A/Icons/sheet.png
        const string k_PlainGuid = "70a91ae0892144de9c6dc0703026c5ae";      // TestContent/A/Prefabs/Plain.prefab
        const string k_SceneOneGuid = "28afaf2c2aba44a3b9c07adebc1fa2a3";   // TestContent/C/Scenes/One.unity

        int m_DirA = -1, m_DirC = -1;

        [OneTimeSetUp]
        public void RequireContent()
        {
            foreach (var d in new[] { "A", "C" })
                Assert.That(Directory.Exists(TestContent.Dir(d)), Is.True, $"content directory {d} is missing — run the EditMode test TestContentBuildTests first");
        }

        [SetUp]
        public void RegisterAAndC()
        {
            TestUtil.RestoreStatics();   // simulation off: only the built catalogs answer
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
            m_DirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
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
            bool okC = m_DirC < 0 || ContentService.UnregisterDirectory(m_DirC);
            bool okA = m_DirA < 0 || ContentService.UnregisterDirectory(m_DirA);
            if (!okC && m_DirC >= 0) ContentService.UnregisterDirectory(m_DirC, force: true);
            if (!okA && m_DirA >= 0) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = m_DirC = -1;
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0), "content scenes remain loaded after the test");
            Assert.That(okA && okC, Is.True, "a directory still had live assets at the end of the test");
        }

        static async Task<bool> Until(Func<bool> cond, int frames = 30) { for (int i = 0; i < frames; ++i) { if (cond()) return true; await Awaitable.NextFrameAsync(); } return cond(); }

#if UNITY_EDITOR
        [Test]
        public void F0_TheGuidsAreTheTestContentsGuids()
        {
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/TestContent/A/Icons/single.png"), Is.EqualTo(k_SingleGuid));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/TestContent/A/Icons/sheet.png"), Is.EqualTo(k_SheetGuid));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/TestContent/A/Prefabs/Plain.prefab"), Is.EqualTo(k_PlainGuid));
            Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID("Assets/TestContent/C/Scenes/One.unity"), Is.EqualTo(k_SceneOneGuid));
        }
#endif

        [Test, Timeout(30000)]
        public async Task F1_AReferenceLoadsTheNamesSlot_FromTheBuiltCatalog()
        {
            var sprite = new ContentRef<Sprite>(k_SingleGuid, "single");
            Assert.That(ContentService.TryGetName(sprite, out var name), Is.True, "the built catalog does not carry the GUID — was the test content rebuilt with this package?");
            Assert.That(name, Is.EqualTo("Icons/single[single]"));

            var scope = ContentScope.Root.CreateChild("F1");
            try
            {
                int live = ContentService.LiveAssetCount;
                var viaRef = await scope.LoadAsync(sprite);
                var viaName = await scope.LoadAsync<Sprite>("Icons/single[single]");
                Assert.That(viaRef, Is.Not.Null);
                Assert.That(viaName, Is.SameAs(viaRef));
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live + 1), "one slot for both");
                Assert.That(scope.Count, Is.EqualTo(1), "one scope key for both");
                var link = new ContentLink<Sprite>("Icons/single", "single");   // the main name and the sprite name kept apart
                Assert.That(await link.LoadAsync(scope), Is.SameAs(viaRef));
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live + 1), "a link with a sprite name loads the same slot too");
                link.Release();
                Assert.That(scope.Release(sprite), Is.True);
                Assert.That(scope.Contains("Icons/single[single]"), Is.False, "Release(ref) returned the shared key");

                var s1 = await scope.LoadAsync(new ContentRef<Sprite>(k_SheetGuid, "s1"));
                Assert.That(s1 != null && s1.name == "s1", Is.True, "a sprite of a multiple-sprite sheet, by its texture's GUID and its name");
                Assert.That(scope.Load(new ContentRef<Texture2D>(k_SheetGuid)), Is.Not.Null, "the sheet's texture: the same GUID with no sprite name");
            }
            finally { scope.Dispose(); }
        }

        [Test, Timeout(30000)]
        public async Task F2_AReferenceToAComponent_InstantiatesThePrefab()
        {
            var scope = ContentScope.Root.CreateChild("F2");
            GameObject instance = null;
            try
            {
                instance = await scope.InstantiateAsync(new ContentRef<Transform>(k_PlainGuid));
                Assert.That(instance, Is.Not.Null);
                Assert.That(instance.Scope().Contains("Prefabs/Plain"), Is.True, "the prefab lease lives with the instance");
                Assert.That(scope.Count, Is.EqualTo(0));
            }
            finally
            {
                if (instance != null) Object.Destroy(instance);
                scope.Dispose();
            }
            Assert.That(await Until(() => { ContentService.Collect(); return ContentService.LiveAssetCount == 0; }), Is.True, "the instance ended and took the prefab lease with it");
        }

        [Test, Timeout(30000)]
        public async Task F3_ASceneReference_LoadsTheBuiltScene()
        {
            var one = new ContentSceneRef(k_SceneOneGuid);
            Assert.That(ContentService.TryGetSceneName(one, out var name), Is.True);
            Assert.That(name, Is.EqualTo("Scenes/One"));
            Assert.That(one.RuntimeKeyIsValid(), Is.True);

            var scope = ContentScope.Root.CreateChild("F3");
            var scene = await scope.LoadSceneAsync(one);
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            Assert.That(scope.SceneCount, Is.EqualTo(1));
            scope.Dispose();
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True, "the scope ended and unloaded the scene");
        }

        [Test, Timeout(30000)]
        public async Task F4_ReferencesInAPrefab_SurviveTheContentBuild()
        {
            // TestContent/A/Prefabs/Linked's LinkHolder carries inspector-assigned references next to its links.
            var scope = ContentScope.Root.CreateChild("F4");
            GameObject instance = null;
            try
            {
                instance = await scope.InstantiateAsync("Prefabs/Linked");
                var holder = instance.GetComponent<LinkHolder>();
                Assert.That(holder.iconRef, Is.EqualTo(new ContentRef<Sprite>(k_SingleGuid, "single")), "the serialized reference came through the build");
                Assert.That(holder.levelRef, Is.EqualTo(new ContentSceneRef(k_SceneOneGuid)));

                var icon = await instance.Scope().LoadAsync(holder.iconRef);
                Assert.That(icon != null && icon.name == "single", Is.True);
                var scene = await instance.Scope().LoadSceneAsync(holder.levelRef);
                Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            }
            finally
            {
                if (instance != null) Object.Destroy(instance);
                scope.Dispose();
            }
            Assert.That(await Until(() => ContentService.LoadedSceneCount == 0), Is.True, "the instance ended and unloaded the scene");
        }

        [Test, Timeout(30000)]
        public void F5_AfterUnregistering_AReferenceResolvesNowhere()
        {
            var sprite = new ContentRef<Sprite>(k_SingleGuid, "single");
            Assert.That(ContentService.Contains(sprite), Is.True);
            Assert.That(ContentService.UnregisterDirectory(m_DirA), Is.True);
            m_DirA = -1;
            Assert.That(ContentService.Contains(sprite), Is.False);
            LogAssert.Expect(LogType.Error, $"[ContentLink] {sprite} is not in any registered ContentCatalog.");
            Assert.That(ContentService.Acquire(sprite).IsValid, Is.False);
        }
    }
}
