using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// A locale overlay — TestContent/G (the base) and TestContent/G.ko (a sibling root with the same names) built as two
    /// directories and registered base first. Icons/flag is 16 px in G and 32 px in G.ko; Icons/only is the base's, Icons/koonly
    /// the locale's; G.ko's Materials/Banner uses the base's Icons/shared; Scenes/Title is in both (roots BaseTitle · KoTitle).
    /// Built directories only — the editor simulation is the EditorPlay suite's.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("LocaleOverlay")]
    public class LocaleOverlayTests
    {
        const string k_BaseFlag = "2175379b6ce6438d9c5d398c151ea1cc";   // TestContent/G/Icons/flag.png
        const string k_KoFlag = "c4872d3610eb45bc8e057f2f2860601d";     // TestContent/G.ko/Icons/flag.png

        int m_G = -1, m_K = -1;

        [SetUp]
        public void Before()
        {
            TestUtil.RestoreStatics();
            TestUtil.SetSimulation(false);   // only the built directories may answer
        }

        [UnityTearDown]
        public IEnumerator After()
        {
            if (m_K >= 0 && !ContentService.UnregisterDirectory(m_K)) ContentService.UnregisterDirectory(m_K, force: true);
            if (m_G >= 0 && !ContentService.UnregisterDirectory(m_G)) ContentService.UnregisterDirectory(m_G, force: true);
            m_G = m_K = -1;
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        void Base() => m_G = ContentService.RegisterDirectory(TestContent.Dir("G"));
        void Locale() => m_K = ContentService.RegisterDirectory(TestContent.Dir("G.ko"));
        static async Task Frames(int n) { for (int i = 0; i < n; ++i) await TestUtil.NextFrame(); }

        /// <summary>T11: the directory registered last wins — a name, the base asset's reference and a link all load the locale's object; names one side has stay that side's.</summary>
        [Test, Timeout(20000)]
        public async Task LO11a_TheLaterDirectoryWins_ForANameAReferenceAndALink()
        {
            Base(); Locale();
            var scope = ContentScope.Root.CreateChild("LO11a");
            var link = new ContentLink<Texture2D>("Icons/flag");
            try
            {
                var byName = await scope.LoadAsync<Texture2D>("Icons/flag");
                var byRef = await scope.LoadAsync(new ContentRef<Texture2D>(k_BaseFlag));
                var byLink = await link.LoadAsync(scope);
                var only = await scope.LoadAsync<Texture2D>("Icons/only");
                var koOnly = await scope.LoadAsync<Texture2D>("Icons/koonly");
                Assert.That(byName != null && byName.width == 32, Is.True, "the locale's flag (32 px)");
                Assert.That(byRef, Is.SameAs(byName), "the base asset's reference takes the name path");
                Assert.That(byLink, Is.SameAs(byName), "the link takes the name path");
                Assert.That(only != null && only.width == 4 && koOnly != null && koOnly.width == 6, Is.True, "the names one side has");
            }
            finally { link.Release(); scope.Dispose(); }
        }

        /// <summary>T11: a slot opened before the locale was registered keeps the base object — a reference asks the same slot — until it is let go.</summary>
        [Test, Timeout(20000)]
        public async Task LO11b_ASlotOpenedBeforeTheLocale_KeepsTheBaseObjectUntilReleased()
        {
            Base();
            var before = ContentService.Acquire<Texture2D>("Icons/flag");
            Locale();
            var after = ContentService.Acquire<Texture2D>("Icons/flag");
            var byRef = ContentService.Acquire(new ContentRef<Texture2D>(k_BaseFlag));
            bool kept = before.Asset != null && before.Asset.width == 16 && ReferenceEquals(before.Asset, after.Asset) && ReferenceEquals(before.Asset, byRef.Asset);
            before.Dispose(); after.Dispose(); byRef.Dispose();
            ContentService.Collect();
            await Frames(2);
            var again = ContentService.Acquire<Texture2D>("Icons/flag");
            bool switched = again.Asset != null && again.Asset.width == 32;
            again.Dispose();
            Assert.That(kept, Is.True, "the open slot keeps the base's flag");
            Assert.That(switched, Is.True, "once released the name opens on the locale's flag");
        }

        /// <summary>T11: unregistering the locale is refused while its asset is held; once released it goes, the base's name answers again and a reference to the locale's asset misses.</summary>
        [Test, Timeout(20000)]
        public async Task LO11c_UnregisteringTheLocale_IsRefusedWhileHeld_AndBringsTheBaseBack()
        {
            Base(); Locale();
            var held = ContentService.Acquire<Texture2D>("Icons/flag");
            LogAssert.Expect(LogType.Error, new Regex("still has 1 live asset"));
            bool refused = !ContentService.UnregisterDirectory(m_K);
            held.Dispose();
            ContentService.Collect();
            await Frames(2);
            bool gone = ContentService.UnregisterDirectory(m_K);
            if (gone) m_K = -1;
            var back = ContentService.Acquire<Texture2D>("Icons/flag");
            LogAssert.Expect(LogType.Error, new Regex("is not in any registered ContentCatalog"));
            var koRef = ContentService.Acquire(new ContentRef<Texture2D>(k_KoFlag));
            bool baseAgain = back.Asset != null && back.Asset.width == 16;
            bool koMissed = !koRef.IsValid;
            back.Dispose(); koRef.Dispose();
            Assert.That(refused, Is.True, "refused while held");
            Assert.That(gone, Is.True, "unregistered once released");
            Assert.That(baseAgain, Is.True, "the base's flag again");
            Assert.That(koMissed, Is.True, "the locale asset's reference misses without the locale");
        }

        /// <summary>T12: a scene name loads the locale's scene; an open scene blocks unregistering the locale; once it is unloaded the locale goes and the base's scene loads.</summary>
        [Test, Timeout(30000)]
        public async Task LO12_ASceneName_TakesTheLocaleScene_AndAnOpenSceneBlocksUnregistering()
        {
            Base(); Locale();
            var scope = ContentScope.Root.CreateChild("LO12");
            var scene = await scope.LoadSceneAsync("Scenes/Title");
            string roots = scene.IsValid() ? string.Join(",", scene.GetRootGameObjects().Select(g => g.name)) : "invalid";
            LogAssert.Expect(LogType.Error, new Regex("still has loaded scene"));
            bool refused = !ContentService.UnregisterDirectory(m_K);
            scope.Dispose();
            Assert.That(await TestUtil.WaitUntil(() => !scene.isLoaded, 60), Is.True, "the scene unloads with its scope");
            await Frames(2);
            bool gone = ContentService.UnregisterDirectory(m_K);
            if (gone) m_K = -1;
            var scope2 = ContentScope.Root.CreateChild("LO12b");
            var scene2 = await scope2.LoadSceneAsync("Scenes/Title");
            string roots2 = scene2.IsValid() ? string.Join(",", scene2.GetRootGameObjects().Select(g => g.name)) : "invalid";
            scope2.Dispose();
            await TestUtil.WaitUntil(() => !scene2.isLoaded, 60);
            Assert.That(roots, Is.EqualTo("KoTitle"), "the locale's scene");
            Assert.That(refused, Is.True, "an open scene blocks unregistering its directory");
            Assert.That(gone, Is.True, "unregistered once the scene is unloaded");
            Assert.That(roots2, Is.EqualTo("BaseTitle"), "the base's scene again");
        }

        /// <summary>
        /// T13: a locale asset that uses a base asset — G.ko's Banner material uses G's Icons/shared. The locale directory carries
        /// that texture too (the same file in both directories) and loads its own copy: the engine does not share an object between
        /// directories, so both are in memory while both are held.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task LO13_ALocaleAssetUsingABaseAsset_IsCarriedAndLoadedTwice()
        {
            var bytesG = Files(TestContent.Dir("G"));
            var bytesK = Files(TestContent.Dir("G.ko"));
            var carried = bytesG.Where(kv => kv.Key.EndsWith(".resS") && kv.Value >= 256 * 256 && bytesK.TryGetValue(kv.Key, out var size) && size == kv.Value).Select(kv => kv.Key).ToArray();
            Assert.That(carried, Is.Not.Empty, "the base texture's data is in both directories under one name");

            Base(); Locale();
            foreach (bool materialFirst in new[] { true, false })
            {
                var scope = ContentScope.Root.CreateChild("LO13");
                try
                {
                    Texture viaMaterial = null, shared = null;
                    if (!materialFirst) shared = await scope.LoadAsync<Texture2D>("Icons/shared");
                    var material = await scope.LoadAsync<Material>("Materials/Banner");
                    viaMaterial = material != null ? material.GetTexture("_BaseMap") : null;
                    if (materialFirst) shared = await scope.LoadAsync<Texture2D>("Icons/shared");
                    int copies = Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t.name == "shared");
                    Debug.Log($"[ContentLink.Tests] LO13 {(materialFirst ? "material first" : "base first")}: copies of 'shared' in memory = {copies} · carried {string.Join(",", carried)}");
                    Assert.That(viaMaterial != null && shared != null, Is.True, "both load");
                    Assert.That(ReferenceEquals(viaMaterial, shared), Is.False, "the material's texture is the locale directory's own copy");
                    Assert.That(copies, Is.GreaterThanOrEqualTo(2), "two copies in memory");
                }
                finally { scope.Dispose(); }
                ContentService.Collect();
                await Frames(5);
            }
        }

        static Dictionary<string, long> Files(string dir)
            => Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta")).ToDictionary(Path.GetFileName, f => new FileInfo(f).Length);
    }
}
