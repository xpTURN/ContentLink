using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.EditorPlay
{
    /// <summary>
    /// T9: the editor simulation with two profiles that share names follows the registration order, as the built directories do.
    /// Profiles over TestContent/G and G.ko (the locale overlay, catalogs built), and over two sibling roots made in G/SimTemp (gitignored)
    /// with no catalog: H and the longer H.ko, each with Icons/hflag — the base's must not be answered from the longer root.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("LocaleOverlay")]
    public class LocaleOverlaySimulationTests
    {
        const string k_Temp = "Assets/TestContent/G/SimTemp";
        const string k_Sib = "Assets/TestContent/SceneCase/SimTemp";   // sibling roots under no catalog's root (G/SimTemp is under G's)
        const string k_BaseFlag = "2175379b6ce6438d9c5d398c151ea1cc";   // TestContent/G/Icons/flag.png

        string m_Snapshot;
        bool m_EnabledWas;
        readonly List<int> m_Dirs = new();
        readonly List<string> m_Warnings = new();

        [OneTimeSetUp]
        public void CreateFixture()
        {
            AssetDatabase.DeleteAsset(k_Temp);
            AssetDatabase.CreateFolder("Assets/TestContent/G", "SimTemp");
            foreach (var root in new[] { "H", "H.ko" })
            {
                AssetDatabase.CreateFolder(k_Temp, root);
                AssetDatabase.CreateFolder($"{k_Temp}/{root}", "Icons");
            }
            AssetDatabase.CopyAsset("Assets/TestContent/G/Icons/flag.png", $"{k_Temp}/H/Icons/hflag.png");
            AssetDatabase.CopyAsset("Assets/TestContent/G.ko/Icons/flag.png", $"{k_Temp}/H.ko/Icons/hflag.png");
            AssetDatabase.DeleteAsset(k_Sib);
            AssetDatabase.CreateFolder("Assets/TestContent/SceneCase", "SimTemp");
            foreach (var root in new[] { "H", "H.ko" })
            {
                AssetDatabase.CreateFolder(k_Sib, root);
                AssetDatabase.CreateFolder($"{k_Sib}/{root}", "Icons");
            }
            AssetDatabase.CopyAsset("Assets/TestContent/G/Icons/flag.png", $"{k_Sib}/H/Icons/hflag.png");
            AssetDatabase.CopyAsset("Assets/TestContent/G.ko/Icons/flag.png", $"{k_Sib}/H.ko/Icons/hflag.png");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        [OneTimeTearDown]
        public void DropFixture() { AssetDatabase.DeleteAsset(k_Temp); AssetDatabase.DeleteAsset(k_Sib); }

        [SetUp]
        public void Prepare()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_EnabledWas = ContentLinkSimulation.Enabled;
            ContentLinkSimulation.Enabled = true;
            TestUtil.SetSimulation(true);
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Drop();
            m_Warnings.Clear();
            Application.logMessageReceived += OnLog;
        }

        void OnLog(string message, string stack, LogType type) { if (type == LogType.Warning) m_Warnings.Add(message); }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            Application.logMessageReceived -= OnLog;
            for (int i = m_Dirs.Count - 1; i >= 0; --i)
                if (!ContentService.UnregisterDirectory(m_Dirs[i])) ContentService.UnregisterDirectory(m_Dirs[i], force: true);
            m_Dirs.Clear();
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            ContentLinkSimulation.Drop();
            ContentLinkSimulation.Enabled = m_EnabledWas;
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            ContentLinkEditorUtility.Invalidate();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        /// <summary>A profile whose output is the given folder; its catalog is never created (no build runs here).</summary>
        static void Profile(string name, string root, string output)
        {
            var p = ContentBuildSettings.instance.Add(name);
            Assert.That(p, Is.Not.Null, name);
            p.SourceRoot = root;
            p.OutputPath = output;
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "", CreateAt = $"Assets/TestContent/Generated/Overlay_{name}.asset" } };
            ContentBuildSettings.instance.MarkDirty();
        }

        static void OverlayProfiles()
        {
            Profile("OverlayG", "Assets/TestContent/G", TestContent.Dir("G"));
            Profile("OverlayG.ko", "Assets/TestContent/G.ko", TestContent.Dir("G.ko"));
        }

        int Register(string output)
        {
            var dir = ContentService.RegisterDirectory(output);
            m_Dirs.Add(dir);
            return dir;
        }

        /// <summary>The project file a name loads from, or "null" — a miss logs an error, which is the answer here.</summary>
        static string PathOf(string name)
        {
            LogAssert.ignoreFailingMessages = true;
            var h = ContentService.Acquire<Object>(name);
            var path = h.IsValid && h.Asset != null ? AssetDatabase.GetAssetPath(h.Asset) : "null";
            h.Dispose();
            LogAssert.ignoreFailingMessages = false;
            return path;
        }

        static string PathOfRef(string guid)
        {
            LogAssert.ignoreFailingMessages = true;
            var h = ContentService.Acquire(new ContentRef<Texture2D>(guid));
            var path = h.IsValid && h.Asset != null ? AssetDatabase.GetAssetPath(h.Asset) : "null";
            h.Dispose();
            LogAssert.ignoreFailingMessages = false;
            return path;
        }

        bool DuplicateWarned(string name) => m_Warnings.Exists(w => w.Contains($"duplicate name '{name}'"));

        static async Task<Scene> OpenRef(string guid)
        {
            var scene = await ContentService.LoadSceneAsync(new ContentSceneRef(guid));
            return scene;
        }

        static async Task Unload(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var op = SceneManager.UnloadSceneAsync(scene);
            while (op != null && !op.isDone) await Awaitable.NextFrameAsync();
        }

        /// <summary>T53: a scene reference to the other profile's file misses while only one profile is served, as in a player; both served, the registration order picks.</summary>
        [Test, Timeout(60000)]
        public async Task LO10a_ASceneReferenceToTheOtherProfilesFile_MissesUnlessServed()
        {
            OverlayProfiles();
            var baseTitle = AssetDatabase.AssetPathToGUID("Assets/TestContent/G/Scenes/Title.unity");
            var koTitle = AssetDatabase.AssetPathToGUID("Assets/TestContent/G.ko/Scenes/Title.unity");
            int ko = Register(TestContent.Dir("G.ko"));
            Assert.That(ContentService.ContainsScene(new ContentSceneRef(baseTitle)), Is.False, "the base scene's reference with only the locale served");
            LogAssert.Expect(LogType.Error, new Regex("is not under .* in the project"));
            var missed = await OpenRef(baseTitle);
            Assert.That(missed.IsValid(), Is.False, "another profile's scene opened");
            var own = await OpenRef(koTitle);
            Assert.That(own.path, Is.EqualTo("Assets/TestContent/G.ko/Scenes/Title.unity"));
            await Unload(own);
            Assert.That(ContentService.UnregisterDirectory(ko), Is.True); m_Dirs.Remove(ko);
            ContentLinkSimulation.Drop();

            Register(TestContent.Dir("G"));
            Assert.That(ContentService.ContainsScene(new ContentSceneRef(koTitle)), Is.False, "the locale scene's reference with only the base served");
            Register(TestContent.Dir("G.ko"));
            var both = await OpenRef(baseTitle);
            Assert.That(both.path, Is.EqualTo("Assets/TestContent/G.ko/Scenes/Title.unity"), "base then locale: the most recently registered file");
            await Unload(both);
        }

        /// <summary>T54: a directory served from the project with its scene open refuses to be unregistered, forced or not, until the scene is unloaded.</summary>
        [Test, Timeout(60000)]
        public async Task LO10b_AServedDirectoryWithItsSceneOpen_RefusesToGo()
        {
            OverlayProfiles();
            int g = Register(TestContent.Dir("G"));
            var scene = await ContentService.LoadSceneAsync("Scenes/Title");
            try
            {
                Assert.That(scene.IsValid() && scene.path == "Assets/TestContent/G/Scenes/Title.unity", Is.True);
                var snapshot = new List<ContentService.SceneInfo>();
                ContentService.SnapshotScenes(snapshot);
                Assert.That(snapshot.Exists(i => i.Scene.handle == scene.handle && i.Dir == g), Is.True, "the snapshot names the directory");
                LogAssert.Expect(LogType.Error, new Regex("still has loaded scene\\(s\\)"));
                Assert.That(ContentService.UnregisterDirectory(g), Is.False);
                LogAssert.Expect(LogType.Error, new Regex("still has loaded scene\\(s\\)"));
                Assert.That(ContentService.UnregisterDirectory(g, force: true), Is.False);
            }
            finally { await Unload(scene); }
            Assert.That(ContentService.UnregisterDirectory(g), Is.True, "after the unload it goes");
            m_Dirs.Remove(g);
        }

        /// <summary>
        /// T69: a directory served from the project is refused while a simulated load of a scene file it covers is in
        /// flight, forced or not, as a player refuses a built one; the base, which does not cover the locale's file, goes; once the
        /// load has arrived and is unloaded, the locale goes too.
        /// </summary>
        [Test, Timeout(60000)]
        public async Task LO10e_AServedDirectoryWithASceneLoadInFlight_RefusesToGo()
        {
            OverlayProfiles();
            int g = Register(TestContent.Dir("G"));
            int ko = Register(TestContent.Dir("G.ko"));
            var pending = ContentService.LoadSceneAsync("Scenes/Title");   // the locale's file: registered last
            Assume.That(pending.GetAwaiter().IsCompleted, Is.False, "the simulated load is in flight");
            Assert.That(ContentService.UnregisterDirectory(g), Is.True, "the base does not cover the loading file: it goes");
            m_Dirs.Remove(g);
            LogAssert.Expect(LogType.Error, new Regex("has scene load\\(s\\) in flight"));
            Assert.That(ContentService.UnregisterDirectory(ko, force: true), Is.False, "the locale covers it: refused, forced or not");
            var scene = await pending;
            try { Assert.That(scene.IsValid() && scene.path == "Assets/TestContent/G.ko/Scenes/Title.unity", Is.True, "the load arrives"); }
            finally { await Unload(scene); }
            Assert.That(ContentService.UnregisterDirectory(ko), Is.True, "after the load and its unload it goes");
            m_Dirs.Remove(ko);
        }

        /// <summary>T55: a reference to a file of a profile never built nor registered misses while the other profile is served — sibling roots under no catalog's root.</summary>
        [Test]
        public void LO10c_AReferenceToAProfileNeverBuilt_Misses()
        {
            Profile("OverlaySib", $"{k_Sib}/H", "Build/OverlaySib/H");
            Profile("OverlaySib.ko", $"{k_Sib}/H.ko", "Build/OverlaySib/H.ko");
            var hBase = AssetDatabase.AssetPathToGUID($"{k_Sib}/H/Icons/hflag.png");
            var hKo = AssetDatabase.AssetPathToGUID($"{k_Sib}/H.ko/Icons/hflag.png");
            int ko = Register("Build/OverlaySib/H.ko");
            Assert.That(PathOfRef(hBase), Is.EqualTo("null"), "the base's reference took the locale's file");
            Assert.That(PathOfRef(hKo), Is.EqualTo($"{k_Sib}/H.ko/Icons/hflag.png"));
            Assert.That(ContentService.UnregisterDirectory(ko), Is.True); m_Dirs.Remove(ko);
            ContentLinkSimulation.Drop();
            Register("Build/OverlaySib/H");
            Assert.That(PathOfRef(hKo), Is.EqualTo("null"), "the locale's reference took the base's file");
            Assert.That(PathOfRef(hBase), Is.EqualTo($"{k_Sib}/H/Icons/hflag.png"));
        }

        /// <summary>T56: a registration between two loads of one name — each load gets the file it asked for, though the name's loads now span two files.</summary>
        [Test, Timeout(60000)]
        public async Task LO10d_ARegistrationBetweenTwoLoadsOfOneName_EachGetsItsFile()
        {
            OverlayProfiles();
            Register(TestContent.Dir("G"));
            var first = ContentService.LoadSceneAsync("Scenes/Title");
            Register(TestContent.Dir("G.ko"));   // the name is the locale's from now on
            var second = ContentService.LoadSceneAsync("Scenes/Title");
            var a = await first; var b = await second;
            try
            {
                Assert.That(a.IsValid() ? a.path : "invalid", Is.EqualTo("Assets/TestContent/G/Scenes/Title.unity"), "the first load");
                Assert.That(b.IsValid() ? b.path : "invalid", Is.EqualTo("Assets/TestContent/G.ko/Scenes/Title.unity"), "the second load");
            }
            finally { await Unload(a); await Unload(b); }
        }

        /// <summary>T9a: the base alone, no catalog anywhere — its own file, not the longer sibling root's of the same name.</summary>
        [Test]
        public void LO9a_TheBaseAlone_TheBaseFile_NotTheLongerSiblingRoot()
        {
            Profile("OverlayH", $"{k_Temp}/H", "Build/Overlay/H");
            Profile("OverlayH.ko", $"{k_Temp}/H.ko", "Build/Overlay/H.ko");
            Register("Build/Overlay/H");
            Assert.That(PathOf("Icons/hflag"), Is.EqualTo($"{k_Temp}/H/Icons/hflag.png"));
        }

        /// <summary>T9b: the base alone while the locale's catalog is in the project — the base's file and scene, no duplicate warning for the shared names.</summary>
        [Test]
        public void LO9b_TheBaseAlone_ALocaleCatalogInTheProject_TheBaseFile()
        {
            OverlayProfiles();
            Register(TestContent.Dir("G"));
            Assert.That(PathOf("Icons/flag"), Is.EqualTo("Assets/TestContent/G/Icons/flag.png"));
            Assert.That(ContentService.ContainsScene("Scenes/Title"), Is.True, "the base's scene");
            Assert.That(DuplicateWarned("Icons/flag") || DuplicateWarned("Scenes/Title"), Is.False, string.Join(" | ", m_Warnings));
        }

        /// <summary>T9c: base then locale — the locale's file for a name and for the base asset's reference; the scene name too.</summary>
        [Test]
        public void LO9c_BaseThenLocale_TheLocaleFile_ForANameAndTheBaseAssetsReference()
        {
            OverlayProfiles();
            Register(TestContent.Dir("G"));
            Register(TestContent.Dir("G.ko"));
            Assert.That(PathOf("Icons/flag"), Is.EqualTo("Assets/TestContent/G.ko/Icons/flag.png"));
            Assert.That(PathOfRef(k_BaseFlag), Is.EqualTo("Assets/TestContent/G.ko/Icons/flag.png"));
            Assert.That(ContentService.ContainsScene("Scenes/Title"), Is.True, "the scene name");
        }

        /// <summary>T9d: locale then base — the base's file: the order decides, not the root's length or name.</summary>
        [Test]
        public void LO9d_LocaleThenBase_TheBaseFile()
        {
            OverlayProfiles();
            Register(TestContent.Dir("G.ko"));
            Register(TestContent.Dir("G"));
            Assert.That(PathOf("Icons/flag"), Is.EqualTo("Assets/TestContent/G/Icons/flag.png"));
        }

        /// <summary>T9e: the locale unregistered — the base's file again, and the locale's own name is gone with it.</summary>
        [UnityTest]
        public IEnumerator LO9e_TheLocaleUnregistered_TheBaseFileAgain()
        {
            OverlayProfiles();
            Register(TestContent.Dir("G"));
            int locale = Register(TestContent.Dir("G.ko"));
            Assert.That(PathOf("Icons/flag"), Is.EqualTo("Assets/TestContent/G.ko/Icons/flag.png"));
            ContentService.Collect();
            yield return null;
            Assert.That(ContentService.UnregisterDirectory(locale), Is.True);
            m_Dirs.Remove(locale);
            Assert.That(PathOf("Icons/flag"), Is.EqualTo("Assets/TestContent/G/Icons/flag.png"));
            Assert.That(PathOf("Icons/koonly"), Is.EqualTo("null"), "the locale's own name is gone with it");
        }
    }
}
