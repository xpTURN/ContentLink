using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Directory E is built with names that keep their extension, A without. Both are registered for the fixture —
    /// the runtime keeps one name space and never reads the style, so two styles side by side are just two sets of names.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("NameStyle")]
    public class NameStyleTests
    {
        const string k_SheetGuid = "172897e43a254e09a04d1a3bfd14dfdb";   // TestContent/E/Icons/Sheet.PNG
        const string k_SmallGuid = "571f2df65fef402f9a0f208e836f2358";   // TestContent/E/Scenes/Small.unity

        int m_DirA = -1, m_DirE = -1;

        [OneTimeSetUp]
        public void RegisterAAndE()
        {
            TestUtil.RestoreStatics();   // the simulation off: only the built catalogs answer
            Assert.That(Directory.Exists(TestContent.Dir("E")), Is.True,
                $"content directory E is missing at '{TestContent.Dir("E")}' — run the EditMode test TestContentBuildTests (or Window/ContentLink/Tests/Build Test Content) first");
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
            m_DirE = ContentService.RegisterDirectory(TestContent.Dir("E"));
        }

        [OneTimeTearDown]
        public void Unregister()
        {
            bool ok = true;
            foreach (var dir in new[] { m_DirE, m_DirA })
            {
                if (dir < 0) continue;
                if (ContentService.UnregisterDirectory(dir)) continue;
                ok = false;
                ContentService.UnregisterDirectory(dir, force: true);   // contain the damage for later fixtures
            }
            m_DirA = m_DirE = -1;
            Assert.That(ok, Is.True, "a directory still had live assets or open scenes at fixture end");
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

        /// <summary>N1: a texture and a prefab of one stem — the case a name without the extension refuses as a duplicate.</summary>
        [Test, Timeout(30000)]
        public async Task N1_TwoFilesOfOneStem_LoadUnderTheirOwnNames()
        {
            var scope = ContentScope.Root.CreateChild("N1");
            try
            {
                Assert.That(await scope.LoadAsync<Texture2D>("Icons/sword.png"), Is.Not.Null);
                Assert.That(await scope.LoadAsync<GameObject>("Icons/sword.prefab"), Is.Not.Null);
                Assert.That(ContentService.Contains("Icons/sword"), Is.False, "the stem alone is no name of E");
            }
            finally { scope.Dispose(); }
        }

        /// <summary>N2: a sprite of the sheet by its whole name, by a link and by a reference — one slot.</summary>
        [Test, Timeout(30000)]
        public async Task N2_ASprite_ByName_ByLink_ByReference_IsOneSlot()
        {
            var scope = ContentScope.Root.CreateChild("N2");
            var link = new ContentLink<Sprite>("Icons/Sheet.PNG", "s0");
            try
            {
                int live = ContentService.LiveAssetCount;
                var byName = await scope.LoadAsync<Sprite>("Icons/Sheet.PNG[s0]");
                var byLink = await link.LoadAsync(scope);
                var reference = new ContentRef<Sprite>(k_SheetGuid, "s0");
                var byRef = await scope.LoadAsync(reference);
                Assert.That(byName, Is.Not.Null);
                Assert.That(byLink, Is.SameAs(byName));
                Assert.That(byRef, Is.SameAs(byName));
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live + 1), "one slot for the three");
                Assert.That(ContentService.TryGetName(reference, out var name) ? name : null, Is.EqualTo("Icons/Sheet.PNG[s0]"), "the reference names the file as it is on disk");
            }
            finally
            {
                link.Release();
                scope.Dispose();
            }
        }

        /// <summary>N3: a scene keeps its extension too.</summary>
        [Test, Timeout(30000)]
        public async Task N3_AScene_KeepsItsExtension()
        {
            Assert.That(ContentService.ContainsScene("Scenes/Small.unity"), Is.True);
            Assert.That(ContentService.ContainsScene("Scenes/Small"), Is.False);
            Assert.That(ContentService.TryGetSceneName(new ContentSceneRef(k_SmallGuid), out var name) ? name : null, Is.EqualTo("Scenes/Small.unity"));
            var scene = await ContentService.LoadSceneAsync("Scenes/Small.unity");
            Assert.That(scene.IsValid() && scene.isLoaded, Is.True);
            var op = SceneManager.UnloadSceneAsync(scene);
            if (op != null) await op;
        }

        /// <summary>N4: A and E registered together each answer their own names.</summary>
        [Test, Timeout(30000)]
        public async Task N4_TwoStyles_RegisteredTogether_BothLoad()
        {
            var scope = ContentScope.Root.CreateChild("N4");
            try
            {
                Assert.That(await scope.LoadAsync<Texture2D>("Icons/single"), Is.Not.Null, "A, named without the extension");
                Assert.That(await scope.LoadAsync<Texture2D>("Icons/sword.png"), Is.Not.Null, "E, named with it");
            }
            finally { scope.Dispose(); }
        }

        /// <summary>N5: a name in the other shape — the stem, another case, a sprite that is not there — is a miss like any other.</summary>
        [Test, Timeout(30000)]
        public async Task N5_ANameInAnotherShape_IsAnOrdinaryMiss()
        {
            foreach (var name in new[] { "Icons/sword", "Icons/Sword.png", "Icons/sheet.png[s0]", "Icons/Sheet.PNG[s9]" })
            {
                LogAssert.Expect(LogType.Error, $"[ContentLink] '{name}' is not in any registered ContentCatalog.");
                var handle = await ContentService.AcquireAsync<Object>(name);
                Assert.That(handle.IsValid, Is.False, name);
            }
            LogAssert.Expect(LogType.Error, "[ContentLink] scene 'Scenes/Small' is not in any registered ContentCatalog.");
            var scene = await ContentService.LoadSceneAsync("Scenes/Small");
            Assert.That(scene.IsValid(), Is.False);
        }
    }
}
