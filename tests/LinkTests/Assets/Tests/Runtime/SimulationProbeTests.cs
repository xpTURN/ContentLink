#if UNITY_EDITOR
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Loading;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// The probe the editor simulation rests on.
    ///
    /// First question: does the engine load a <see cref="LoadableObjectId"/> whose asset is in no built content
    /// directory, while the editor is in Play Mode? If it does, the simulation can hand the normal slot path an id
    /// the editor made and reference counting keeps the shipped meaning. If it does not, every simulated asset
    /// needs a detached slot and the meaning parts company with the player.
    ///
    /// Second question: if it does load, does releasing it unload the object the way a built directory's asset does
    /// (<see cref="TestEnv.ReleaseObservable"/>)? A different answer is a limit to write down, not a blocker.
    ///
    /// Fixtures: TestContent/Dup never builds — x.png and x.mat both map to the name "x", so the builder refuses
    /// the directory. They are the only assets in the repo that are imported yet in no content directory, and
    /// between them they cover both id flavours: an imported artifact (png → PrimaryArtifact) and a native asset
    /// (mat → SourceAsset).
    ///
    /// Editor-only: the id can only be made with UnityEditor.LoadableObjectIdEditorUtility. The assembly has
    /// no platform restriction, so the file simply compiles out of a player build and the probe is skipped there.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Simulation")]
    public class SimulationProbeTests
    {
        const string k_TexturePath = "Assets/TestContent/Dup/x.png";     // imported artifact
        const string k_MaterialPath = "Assets/TestContent/Dup/x.mat";    // native asset

        bool m_IgnoreWas;

        [SetUp]
        public void IgnoreLoadFailures()
        {
            // The probe asks for ids that may not resolve, and a failed load is an error log. Whether it happens is
            // the measurement, so the log must not decide the test.
            m_IgnoreWas = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            LogAssert.ignoreFailingMessages = m_IgnoreWas;
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the probe");
        }

        struct Result { public bool Loaded; public string Detail; }

        /// <summary>Makes an id for an asset the editor knows and asks ContentService to load it by id.</summary>
        static async Task<Result> AcquireById<T>(string assetPath) where T : Object
        {
            var asset = AssetDatabase.LoadAssetAtPath<T>(assetPath);
            Assert.That(asset, Is.Not.Null, $"fixture missing: {assetPath}");

            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(asset);
            Assert.That(id.IsValid, Is.True, $"the editor could not make an id for '{assetPath}'");

            var handle = await ContentService.AcquireAsync<T>(id);
            var r = new Result
            {
                Loaded = handle.IsValid && handle.Asset != null,
                Detail = $"id={id} status={handle.Status} asset={(handle.IsValid && handle.Asset != null ? handle.Asset.name : "null")}",
            };
            if (handle.IsValid) handle.Dispose();
            ContentService.Collect();
            return r;
        }

        // ───────────── ids of assets in no built directory ─────────────

        [Test, Timeout(30000)]
        public async Task P1a_UnbuiltArtifactId_WithNoDirectoryOfItsOwn()
        {
            int registered = ContentLoadManager.GetContentDirectories()?.Length ?? 0;
            var r = await AcquireById<Texture2D>(k_TexturePath);
            Debug.Log($"[ContentLink.Tests] registeredDirectories={registered} loaded={r.Loaded} {r.Detail}");
            Assert.That(r.Loaded, Is.True,
                "an id for an asset that is in no content directory did not load — the simulation cannot reuse the engine id path and would need detached slots");
        }

        [Test, Timeout(30000)]
        public async Task P1b_UnbuiltArtifactId_WhileADirectoryIsRegistered()
        {
            // The case the design actually depends on: the overlay only resolves names that belong to a
            // registered directory's catalog, so the asset is always missing from a directory that *is* registered.
            int dir = ContentService.RegisterDirectory(TestContent.Dir("A"));
            try
            {
                var r = await AcquireById<Texture2D>(k_TexturePath);
                Debug.Log($"[ContentLink.Tests] loaded={r.Loaded} {r.Detail}");
                Assert.That(r.Loaded, Is.True,
                    "a registered directory that does not carry the asset refused the id — this is the case the whole design rests on");
            }
            finally
            {
                if (!ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true);
            }
        }

        [Test, Timeout(30000)]
        public async Task P1c_UnbuiltNativeAssetId()
        {
            var r = await AcquireById<Material>(k_MaterialPath);
            Debug.Log($"[ContentLink.Tests] native asset: loaded={r.Loaded} {r.Detail}");
            Assert.That(r.Loaded, Is.True, "a native asset's id (SourceAsset) behaved differently from an imported artifact's");
        }

        // ───────────── releasing a simulated asset ─────────────

        [Test, Timeout(30000)]
        public async Task P1d_ReleasingASimulatedAsset_UnloadObservation()
        {
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(k_TexturePath);
            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(asset);
            var handle = await ContentService.AcquireAsync<Texture2D>(id);
            Assume.That(handle.IsValid && handle.Asset != null, Is.True, "the unbuilt id has to load before its release can be measured");

            var target = handle.Asset;
            handle.Dispose();
            ContentService.Collect();

            int frames = 0;
            for (; frames < 10 && target != null; ++frames) await TestUtil.NextFrame();
            bool unloaded = target == null;
            bool editorStillLoadsIt = AssetDatabase.LoadAssetAtPath<Texture2D>(k_TexturePath) != null;

            Debug.Log($"[ContentLink.Tests] unloadedAfterRelease={unloaded} (frames={frames}) " +
                      $"builtDirectoryBehaviour={TestEnv.ReleaseObservable} editorStillLoadsIt={editorStillLoadsIt}");
            Assert.That(editorStillLoadsIt, Is.True, "the asset itself must survive — the probe must not damage the project");

            // Measured: the object stays alive. A built directory's asset unloads on release
            // (TestEnv.ReleaseObservable), so this is where simulation and shipping part company — the README's
            // warning that Play Mode AssetDatabase content is not reference counted, pinned by this assertion.
            Assert.That(unloaded, Is.False,
                "a simulation-resolved asset was unloaded on release — the documented editor/player difference no longer holds");
        }
    }
}
#else
using NUnit.Framework;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Player stand-in for the fixture above, which needs UnityEditor and so is not compiled into a player build.
    /// Without it the tests would not be reported as skipped — they would be absent from the report altogether, and
    /// "editor only" would read exactly like "gone". The editor PlayMode suite runs the real one.
    /// </summary>
    [Category("Simulation")]
    public class SimulationProbeTests
    {
        [Test]
        public void EditorOnly() => Assert.Ignore(
            "editor only: the probe builds LoadableObjectIds with UnityEditor APIs. It runs in the editor PlayMode suite.");
    }
}
#endif
