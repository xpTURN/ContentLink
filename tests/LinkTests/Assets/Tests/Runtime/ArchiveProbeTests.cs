using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Content;
using Unity.IO.Archive;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Registration-side probes on the directories BuildProbeTests (EditMode) leaves under
    /// StreamingAssets/ContentLinkTests/Probe. Registration throws in edit mode, so this is PlayMode work.
    ///   - the engine's own UseArchive output registers as a plain directory
    ///   - archives we packed by hand mount into one path that registers and loads — the condition that
    ///     output-side packing (ContentArchives) rests on
    ///   - two root catalogs sharing a name: which one the runtime serves
    /// </summary>
    [Category("Build")]
    public class ArchiveProbeTests
    {
        static string Probe(string name) => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Probe", name);

        static void RequireProbe(string name)
        {
            if (!Directory.Exists(Probe(name)))
                Assert.Ignore($"probe output '{name}' is missing — run the EditMode probes (BuildProbeTests) first");
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
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the probe");
        }

        static async Task<(bool loaded, string name)> LoadSingle()
        {
            var handle = await ContentService.AcquireAsync<Texture2D>("Icons/single");
            var r = (handle.IsValid && handle.Asset != null, handle.IsValid && handle.Asset != null ? handle.Asset.name : "null");
            if (handle.IsValid) handle.Dispose();
            ContentService.Collect();
            return r;
        }

        static void Unregister(int dir)
        {
            if (dir >= 0 && !ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true);
        }

        // ───────────── the engine's own archive output ─────────────

        [Test, Timeout(30000)]
        public async Task A1_EngineArchiveOutput_RegistersAsADirectory_AndLoads()
        {
            RequireProbe("EngineArchive");
            int dir = -1;
            try
            {
                dir = ContentService.RegisterDirectory(Probe("EngineArchive"));
                var r = await LoadSingle();
                Debug.Log($"[ContentLink.Tests] engine UseArchive output: registered={dir >= 0} loaded={r.loaded} asset={r.name}");
                Assert.That(r.loaded, Is.True, "the engine's own archive build did not register/load as a directory — the Engine mode needs a mount step or must go");
            }
            finally { Unregister(dir); }
        }

        // ───────────── hand-packed archives ─────────────

        [Test, Timeout(60000)]
        public async Task A2_HandPackedArchives_MountIntoOnePath_RegisterAndLoad()
        {
            RequireProbe("Packed");
            var archives = Directory.GetFiles(Probe("Packed"), "*.archive");
            System.Array.Sort(archives, System.StringComparer.Ordinal);
            Assume.That(archives.Length, Is.EqualTo(2), "the EditMode probe BuildProbeTests.P1c should have left two archives");

            var ns = ContentNamespace.GetOrCreateNamespace("clprobe");
            var handles = new ArchiveHandle[archives.Length];
            int dir = -1;
            try
            {
                for (int i = 0; i < archives.Length; ++i)
                {
                    handles[i] = ArchiveFileInterface.MountAsync(ns, archives[i], "clp");
                    for (int f = 0; f < 600 && handles[i].Status == ArchiveStatus.InProgress; ++f) await TestUtil.NextFrame();
                    var files = handles[i].Status == ArchiveStatus.Complete ? handles[i].GetFileInfo() : null;
                    Debug.Log($"[ContentLink.Tests] mount {Path.GetFileName(archives[i])}: status={handles[i].Status} path='{(handles[i].Status == ArchiveStatus.Complete ? handles[i].GetMountPath() : "-")}' files={(files != null ? files.Length : -1)}");
                    Assert.That(handles[i].Status, Is.EqualTo(ArchiveStatus.Complete), $"mounting {archives[i]} failed");
                }

                var mountPath = handles[0].GetMountPath();
                bool samePath = mountPath == handles[1].GetMountPath();
                Debug.Log($"[ContentLink.Tests] same prefix → same mount path: {samePath} ('{mountPath}' / '{handles[1].GetMountPath()}')");

                try { dir = ContentService.RegisterDirectory(mountPath); }
                catch (System.Exception e) { Debug.Log($"[ContentLink.Tests] RegisterDirectory(mountPath) threw: {e.GetType().Name}: {e.Message}"); }

                var r = dir >= 0 ? await LoadSingle() : (false, "not registered");
                Debug.Log($"[ContentLink.Tests] registered={dir >= 0} loaded={r.Item1} asset={r.Item2}");
                Assert.That(dir >= 0 && r.Item1, Is.True,
                    "archives packed by hand did not register/load through a mount — output-side packing does not work, so the build would have to split on the input side instead");
            }
            finally
            {
                Unregister(dir);
                for (int i = 0; i < handles.Length; ++i)
                    if (handles[i].Status == ArchiveStatus.Complete) handles[i].Unmount().Complete();
            }
        }

        [Test, Timeout(60000)]
        public async Task A4_TheSessionReset_LeavesAnotherSystemsNamespaceAlone()
        {
            RequireProbe("Packed");
            var archives = Directory.GetFiles(Probe("Packed"), "*.archive");
            System.Array.Sort(archives, System.StringComparer.Ordinal);
            Assume.That(archives.Length, Is.GreaterThan(0), "the EditMode probe BuildProbeTests.P1c should have left archives");

            // 16 alphanumeric characters starting with 'cl': the engine accepts it, and it is not the shape ContentArchives.NamespaceFor makes
            var foreign = ContentNamespace.GetOrCreateNamespace("clientAssetsNs01");
            var handle = ArchiveFileInterface.MountAsync(foreign, archives[0], "u");
            try
            {
                for (int f = 0; f < 600 && handle.Status == ArchiveStatus.InProgress; ++f) await TestUtil.NextFrame();
                Assume.That(handle.Status, Is.EqualTo(ArchiveStatus.Complete), $"mounting {archives[0]} failed");
                Assume.That(ArchiveFileInterface.GetMountedArchives(foreign).Length, Is.EqualTo(1));

                int unmounted = ContentArchives.UnmountLeftovers();   // what the Play-entry reset runs
                Assert.That(unmounted, Is.EqualTo(0), "the reset must not count another system's archive as ours");
                Assert.That(ArchiveFileInterface.GetMountedArchives(foreign).Length, Is.EqualTo(1), "the reset must leave another system's mount alone");
            }
            finally
            {
                foreach (var h in ArchiveFileInterface.GetMountedArchives(foreign)) h.Unmount().Complete();
            }
        }

        // ───────────── one name in two root catalogs ─────────────

        [Test, Timeout(30000)]
        public async Task A3_TwoCatalogsSharingAName_WhichOneWins()
        {
            RequireProbe("Dup");
            int dir = -1;
            try
            {
                dir = ContentService.RegisterDirectory(Probe("Dup"));
                var r = await LoadSingle();
                // Both catalogs call something "Icons/single": the single texture ('single') and the sheet ('sheet').
                Debug.Log($"[ContentLink.Tests] two root catalogs, one name: registered={dir >= 0} loaded={r.loaded} winner={r.name}");
                Assert.That(r.loaded, Is.True, "a directory with a duplicated name across its catalogs did not even load — the engine refuses it");
            }
            finally { Unregister(dir); }
        }
    }
}
