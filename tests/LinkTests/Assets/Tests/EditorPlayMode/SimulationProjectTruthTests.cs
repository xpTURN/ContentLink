using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Loading;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.EditorPlay
{
    /// <summary>
    /// PlayMode tests that need a build profile, so they live in this editor-only assembly (the runtime test
    /// assembly cannot reference the editor assembly without breaking the player build). A profile 'SimA' whose
    /// output is the built A directory is created per test and the settings snapshot restored after.
    /// A player stand-in in the runtime assembly reports these as skipped there.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Simulation")]
    public class SimulationProjectTruthTests
    {
        const string k_RootA = "Assets/TestContent/A";
        const string k_Generated = "Assets/TestContent/Generated";

        string m_Snapshot;
        bool m_EnabledWas;
        int m_Dir = -1;

        [SetUp]
        public void Prepare()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_EnabledWas = ContentLinkSimulation.Enabled;
            ContentLinkSimulation.Enabled = true;
            TestUtil.SetSimulation(true);
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Drop();
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            if (m_Dir >= 0 && !ContentService.UnregisterDirectory(m_Dir)) ContentService.UnregisterDirectory(m_Dir, force: true);
            m_Dir = -1;
            TestUtil.RestoreStatics();   // also resets the service statics and turns the simulation off
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

        /// <summary>A profile whose output is the given folder; its catalogs are never created (no build runs here).</summary>
        static ContentBuildSettings.Profile Profile(string name, string output, params string[] subFolders)
        {
            var p = ContentBuildSettings.instance.Add(name);
            Assert.That(p, Is.Not.Null);
            p.SourceRoot = k_RootA;
            p.OutputPath = output;
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            var catalogs = new ContentBuildSettings.CatalogEntry[subFolders.Length];
            for (int i = 0; i < subFolders.Length; ++i)
                catalogs[i] = new ContentBuildSettings.CatalogEntry { SubFolder = subFolders[i], CreateAt = $"{k_Generated}/Truth_{name}_{i}.asset" };
            p.Catalogs = catalogs;
            ContentBuildSettings.instance.MarkDirty();
            return p;
        }

        static int EngineDirectories => ContentLoadManager.GetContentDirectories()?.Length ?? 0;

        [Test, Timeout(30000)]
        public async Task E1_ProfileOutput_IsServedFromTheProject_NotTheEngine()
        {
            Profile("SimA", TestContent.Dir("A"), "Icons", "Prefabs");
            int engineBefore = EngineDirectories;

            LogAssert.Expect(LogType.Log, new Regex("is served from the project — profile 'SimA'"));
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("A"));
            Assert.That(m_Dir, Is.GreaterThanOrEqualTo(0));
            Assert.That(EngineDirectories, Is.EqualTo(engineBefore), "the registration must not reach the engine");

            Assert.That(ContentService.Contains("Icons/single"), Is.True, "a name under a covered category");
            Assert.That(ContentService.Contains("Materials/Plain"), Is.False, "a name outside the profile's categories is not this directory's");

            var handle = await ContentService.AcquireAsync<Texture2D>("Icons/single");
            try
            {
                Assert.That(handle.IsValid && handle.Asset != null, Is.True, "Icons/single did not load from the project");
                Assert.That(AssetDatabase.Contains(handle.Asset), Is.True, "the object must be the project's (Library) asset, not a built copy");
                Assert.That(AssetDatabase.GetAssetPath(handle.Asset), Is.EqualTo("Assets/TestContent/A/Icons/single.png"));
                Debug.Log($"[ContentLink.Tests] served from the project: {AssetDatabase.GetAssetPath(handle.Asset)} · engine directories {EngineDirectories}");

                LogAssert.Expect(LogType.Error, new Regex("'Materials/Plain' is not under 'Assets/TestContent/A' \\(profile SimA\\) in the project"));
                var outside = await ContentService.AcquireAsync<Material>("Materials/Plain");
                Assert.That(outside.IsValid, Is.False, "a name outside the categories must fail with the project wording");

                Assert.That(() => ContentService.RegisterDirectory(TestContent.Dir("A") + "/"), Throws.InvalidOperationException, "the same folder registered twice is refused");
            }
            finally { if (handle.IsValid) handle.Dispose(); }
        }

        [Test, Timeout(30000)]
        public void E2_TurningTheToggleOffMidPlay_KeepsAServedDirectoryWorking()
        {
            Profile("SimA", TestContent.Dir("A"), "");
            LogAssert.Expect(LogType.Log, new Regex("is served from the project"));
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("A"));
            Assert.That(ContentService.Contains("Icons/single"), Is.True);

            LogAssert.Expect(LogType.Log, new Regex("keep working until the next Play"));
            ContentLinkSimulation.Enabled = false;
            Assert.That(ContentService.Contains("Icons/single"), Is.True, "a directory served from the project keeps answering after the toggle goes off");
            ContentLinkSimulation.Enabled = true;
        }

        [Test, Timeout(30000)]
        public void E3_WithTheToggleOff_TheSameRegistrationIsReal()
        {
            Profile("SimA", TestContent.Dir("A"), "");
            ContentLinkSimulation.Enabled = false;
            int engineBefore = EngineDirectories;
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("A"));
            Assert.That(EngineDirectories, Is.EqualTo(engineBefore + 1), "with the simulation off the registration reaches the engine — the shipping path");
            ContentLinkSimulation.Enabled = true;
        }

        [Test, Timeout(30000)]
        public async Task E4_AProfileThatWasNeverBuilt_StillRegistersAndLoads()
        {
            const string output = "Build/Content/NeverBuilt";
            Assert.That(Directory.Exists(output), Is.False, "the fixture assumes this folder does not exist");
            Profile("NeverBuilt", output, "Icons");
            int engineBefore = EngineDirectories;

            LogAssert.Expect(LogType.Log, new Regex("profile 'NeverBuilt'"));
            m_Dir = ContentService.RegisterDirectory(output);
            Assert.That(m_Dir, Is.GreaterThanOrEqualTo(0), "no build is needed to register a profile's output");
            Assert.That(EngineDirectories, Is.EqualTo(engineBefore));

            var handle = await ContentService.AcquireAsync<Texture2D>("Icons/single");
            try { Assert.That(handle.IsValid && handle.Asset != null, Is.True, "the project answers although nothing was ever built"); }
            finally { if (handle.IsValid) handle.Dispose(); }
        }

        [Test, Timeout(30000)]
        public async Task E5_APackedProfileOutput_IsServedWithoutMounting()
        {
            const string output = "Build/Content/NeverPacked";
            var p = Profile("NeverPacked", output, "Icons");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            int engineBefore = EngineDirectories;

            LogAssert.Expect(LogType.Log, new Regex("profile 'NeverPacked'"));
            m_Dir = await ContentService.RegisterPackedDirectoryAsync(output);
            Assert.That(m_Dir, Is.GreaterThanOrEqualTo(0), "the packed registration is intercepted before archives.json is looked for");
            Assert.That(EngineDirectories, Is.EqualTo(engineBefore));
            Assert.That(ContentService.Contains("Icons/single"), Is.True);
        }

        [Test, Timeout(30000)]
        public void E6_AProfileWhoseSourceRootIsMissing_IsRefused()
        {
            var p = Profile("Broken", "Build/Content/Broken", "");
            p.SourceRoot = "Assets/TestContent/Nope";
            Assert.That(() => ContentService.RegisterDirectory("Build/Content/Broken"),
                Throws.InvalidOperationException.With.Message.Contains("source root"), "a matched profile with no source root must not fall back to the engine silently");
        }

        [Test, Timeout(30000)]
        public async Task E7_TheToggleOffAtTheExit_StillSummarisesAndDrops()
        {
            Profile("SimA", TestContent.Dir("A"), "Icons");   // its catalog is never created, so every name served is one no catalog carries
            LogAssert.Expect(LogType.Log, new Regex("is served from the project"));
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("A"));

            var handle = await ContentService.AcquireAsync<Texture2D>("Icons/single");
            Assert.That(handle.IsValid && handle.Asset != null, Is.True, "Icons/single did not load from the project");
            handle.Dispose();
            Assert.That(ContentLinkSimulation.TryGetPath("Icons/single", out _, out _), Is.True, "the overlay answers while the Play runs");

            LogAssert.Expect(LogType.Log, new Regex("keep working until the next Play"));
            ContentLinkSimulation.Enabled = false;

            // The runner's own exit comes after the test, so the exit handler is driven here.
            LogAssert.Expect(LogType.Log, new Regex("editor simulation resolved 1 name\\(s\\) no catalog carries: Icons/single"));
            typeof(ContentLinkSimulation).GetMethod("OnPlayModeStateChanged", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { PlayModeStateChange.ExitingPlayMode });
            Assert.That(ContentLinkSimulation.TryGetPath("Icons/single", out _, out _), Is.False, "the exit must drop the overlay whatever the toggle says");
            ContentLinkSimulation.Enabled = true;
        }
    }
}
