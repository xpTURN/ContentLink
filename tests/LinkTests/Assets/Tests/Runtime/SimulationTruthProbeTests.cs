#if UNITY_EDITOR
using System.Collections;
using System.IO;
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
    /// The three probes the "project is truth" simulation rests on. Nothing here goes through the
    /// simulation seams: every load is by an editor-made id with no directory registered, which is exactly what a
    /// simulated directory turns every load into.
    ///   - an id whose asset IS in a built directory on disk, with that directory NOT registered — does the
    ///     editor serve it from the Library? (SimulationProbeTests measures only assets in no build at all, and
    ///     A registered with A's own id.)
    ///   - overwrite + reimport: does a live handle see the new size in place? does a reload after the slot
    ///     was freed see it?
    ///   - delete during Play: what happens to a held asset? does a new load of that id fail without crashing?
    /// Results are logged with the [ContentLink.Tests] tag; only the load-bearing facts are asserted.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Simulation")]
    public class SimulationTruthProbeTests
    {
        const string k_Folder = "Assets/TestContent/A/SimTemp";
        const string k_BuiltTexture = "Assets/TestContent/A/Icons/single.png";   // in the built A directory
        const string k_EditPath = k_Folder + "/probe_edit.png";
        const string k_GonePath = k_Folder + "/probe_gone.png";

        bool m_IgnoreWas;
        int m_Errors;

        [SetUp]
        public void Prepare()
        {
            m_IgnoreWas = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;   // a failed load logs an error; whether it happens is the measurement
            Application.logMessageReceived += Count;
            m_Errors = 0;
            if (!AssetDatabase.IsValidFolder(k_Folder)) AssetDatabase.CreateFolder("Assets/TestContent/A", "SimTemp");
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            TestUtil.RestoreStatics();
            // The engine reports a failed load ("cannot be loaded") from a later frame than our await returns in
            // (measured in the delete probe), so the ignore flag stays up until a few frames have passed.
            for (int i = 0; i < 10 && (i < 3 || ContentService.LiveAssetCount != 0); ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Application.logMessageReceived -= Count;
            LogAssert.ignoreFailingMessages = m_IgnoreWas;
            AssetDatabase.DeleteAsset(k_EditPath);
            AssetDatabase.DeleteAsset(k_GonePath);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the probe");
        }

        void Count(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception) ++m_Errors;
        }

        static void WritePng(string path, int size)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = new Color32((byte)(i % 255), (byte)size, 0, 255);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        static LoadableObjectId IdOf(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.That(asset, Is.Not.Null, $"fixture missing: {path}");
            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(asset);
            Assert.That(id.IsValid, Is.True, $"the editor could not make an id for '{path}'");
            return id;
        }

        // ───────────── built but not registered ─────────────

        [Test, Timeout(30000)]
        public async Task U1_IdOfAnAssetInAnUnregisteredBuild_LoadsFromTheLibrary()
        {
            Assert.That(ContentLoadManager.GetContentDirectories(), Is.Empty, "the probe needs no directory registered");
            Assert.That(Directory.Exists(TestContent.Dir("A")), Is.True, "A must exist on disk (built, not registered) — that is the cell being measured");

            var id = IdOf(k_BuiltTexture);
            var handle = await ContentService.AcquireAsync<Texture2D>(id);
            try
            {
                bool loaded = handle.IsValid && handle.Asset != null;
                bool fromEditor = loaded && AssetDatabase.Contains(handle.Asset);
                Debug.Log($"[ContentLink.Tests] built-but-unregistered id: status={handle.Status} loaded={loaded} editorAsset={fromEditor} path={(loaded ? AssetDatabase.GetAssetPath(handle.Asset) : "-")}");
                Assert.That(loaded, Is.True, "an id whose build exists on disk but is not registered did not load — the project-is-truth simulation rests on this and folds if it is false");
                Assert.That(fromEditor, Is.True, "the object is not an editor (Library) asset — the engine found the build without a registration");
            }
            finally { if (handle.IsValid) handle.Dispose(); }
        }

        // ───────────── overwrite and reimport ─────────────

        [Test, Timeout(60000)]
        public async Task U2_OverwriteAndReimport_LiveHandleAndReload()
        {
            WritePng(k_EditPath, 32);
            var id = IdOf(k_EditPath);
            var first = await ContentService.AcquireAsync<Texture2D>(id);
            Assert.That(first.IsValid && first.Asset != null, Is.True, "the 32² fixture did not load");
            int before = first.Asset.width;

            WritePng(k_EditPath, 64);
            var live = first.Asset;
            string inPlace = live == null ? "destroyed" : $"{live.width}x{live.height}";
            Debug.Log($"[ContentLink.Tests] live handle after reimport: {before}² → {inPlace} (same object: {ReferenceEquals(live, first.Asset)})");

            first.Dispose();
            ContentService.Collect();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "the slot must be freed before the reload counts as a reload");

            var second = await ContentService.AcquireAsync<Texture2D>(id);
            try
            {
                int after = second.IsValid && second.Asset != null ? second.Asset.width : -1;
                Debug.Log($"[ContentLink.Tests] reload after free: width={after}");
                Assert.That(before, Is.EqualTo(32));
                Assert.That(after, Is.EqualTo(64), "a reload of the same id after a reimport did not give the new file — the premise that the Library serves the latest import is wrong");
            }
            finally { if (second.IsValid) second.Dispose(); }
        }

        // ───────────── delete during Play ─────────────

        [Test, Timeout(60000)]
        public async Task U3_DeleteDuringPlay_HeldAssetAndNewLoad()
        {
            WritePng(k_GonePath, 16);
            var id = IdOf(k_GonePath);
            var held = await ContentService.AcquireAsync<Texture2D>(id);
            Assert.That(held.IsValid && held.Asset != null, Is.True, "the fixture did not load");

            AssetDatabase.DeleteAsset(k_GonePath);
            var asset = held.Asset;
            string fate = asset == null ? "null (destroyed)" : $"alive {asset.width}x{asset.height}";
            Debug.Log($"[ContentLink.Tests] held asset after DeleteAsset: {fate} · handle valid={held.IsValid}");

            held.Dispose();
            ContentService.Collect();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));

            int errorsBefore = m_Errors;
            // Both errors arrive inside the await (measured): the engine's, then ours. ignoreFailingMessages alone did
            // not keep the engine's from counting as unhandled, so they are expected by text.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("cannot be loaded"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[ContentLink\] failed to load"));
            var again = await ContentService.AcquireAsync<Texture2D>(id);
            try
            {
                bool loaded = again.IsValid && again.Asset != null;
                Debug.Log($"[ContentLink.Tests] new load of the deleted id: status={again.Status} loaded={loaded} errors={m_Errors - errorsBefore}");
                Assert.That(loaded, Is.False, "a deleted asset's id still loaded — the Library artifact survived the delete");
            }
            finally { if (again.IsValid) again.Dispose(); }
        }
    }
}
#else
using NUnit.Framework;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>Player stand-in so the probe is reported as skipped rather than missing.</summary>
    [Category("Simulation")]
    public class SimulationTruthProbeTests
    {
        [Test]
        public void EditorOnly() => Assert.Ignore("editor only: the probe makes ids and reimports assets with UnityEditor APIs. It runs in the editor PlayMode suite.");
    }
}
#endif
