using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Content;
using Unity.IO.Archive;
using Unity.Loading;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Probe: how the engine itself reacts when an archive of a packed directory is not there.
    /// ContentService refuses before the engine sees it (the size check), so these go around it with the engine API.
    ///   M1 — mounting a path that does not exist
    ///   M2 — each archive of Runner/Packed2 left out in turn: registration, an async load of every catalog entry, root assets
    /// The engine knows only what is mounted, so leaving an archive unmounted is the missing file as far as it can tell.
    /// Records only.
    /// </summary>
    [Category("Build")]
    public class MissingArchiveProbeTests
    {
        const string Tag = "[ContentLink.Probe]";
        const double LoadLimitMs = 10000;

        static string Packed2 => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", "Packed2");

        readonly List<string> m_Logs = new();

        void OnLog(string message, string stack, LogType type)
        {
            if (message.StartsWith(Tag, StringComparison.Ordinal)) return;
            lock (m_Logs) m_Logs.Add($"{type}: {Flatten(message)}");
        }

        string TakeLogs()
        {
            lock (m_Logs)
            {
                var s = m_Logs.Count == 0 ? "none" : string.Join(" | ", m_Logs);
                m_Logs.Clear();
                return s;
            }
        }

        static string Flatten(string s)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
        }

        static void Env(string test)
        {
            LogAssert.ignoreFailingMessages = true;   // the engine's own load errors are the observation; set in the test body, SetUp is too early
            Debug.Log($"{Tag} {test} env: editor={Application.isEditor} platform={Application.platform} unity={Application.unityVersion}");
        }

        [SetUp]
        public void Hook() => Application.logMessageReceivedThreaded += OnLog;

        [TearDown]
        public void Unhook() => Application.logMessageReceivedThreaded -= OnLog;

        [Test, Timeout(60000)]
        public async Task M1_MountingAFileThatIsNotThere()
        {
            Env("M1");
            var path = Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "no-such.archive");
            if (File.Exists(path)) File.Delete(path);
            var ns = ContentNamespace.GetOrCreateNamespace("clprobemissing");
            TakeLogs();

            var watch = Stopwatch.StartNew();
            ArchiveHandle handle = default;
            string thrown = "none";
            try { handle = ArchiveFileInterface.MountAsync(ns, path, ContentArchives.MountPrefix); }
            catch (Exception e) { thrown = $"{e.GetType().Name}: {Flatten(e.Message)}"; }
            string atCall = thrown == "none" ? handle.Status.ToString() : "-";
            int frames = 0;
            if (thrown == "none")
                for (; frames < 600 && handle.Status == ArchiveStatus.InProgress; ++frames) await TestUtil.NextFrame();
            double ms = watch.Elapsed.TotalMilliseconds;
            await TestUtil.NextFrame();
            await TestUtil.NextFrame();

            string final = thrown == "none" ? handle.Status.ToString() : "-";
            Debug.Log($"{Tag} M1 mount of a missing file: thrown={thrown} statusAtCall={atCall} final={final} frames={frames} ms={ms:F1} mountedInNamespace={ArchiveFileInterface.GetMountedArchives(ns).Length} logs={TakeLogs()}");
            if (thrown == "none" && handle.Status == ArchiveStatus.Complete) handle.Unmount().Complete();
        }

        [Test, Timeout(600000)]
        public async Task M2_EachArchiveLeftOut_RegisterLoadAndRootAssets()
        {
            var index = ContentArchives.ReadIndex(Packed2);
            if (index == null)
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            Env("M2");

            var entries = new List<(string name, LoadableObjectId id)>();
            if (!await Scenario(index, -1, entries) || entries.Count == 0)
            {
                Debug.Log($"{Tag} M2 stopped: the baseline did not come through (entries={entries.Count})");
                return;
            }
            for (int i = 0; i < index.Archives.Length; ++i)
            {
                if (await Scenario(index, i, entries)) continue;
                Debug.Log($"{Tag} M2 stopped after leaving out archive {i}: a load was still in flight, so the directory could not be released");
                break;
            }
        }

        /// <summary>False when a load never finished — the directory stays registered and the rest of the matrix would be polluted.</summary>
        async Task<bool> Scenario(ContentArchives.Index index, int leftOut, List<(string name, LoadableObjectId id)> entries)
        {
            string label = leftOut < 0 ? "none" : $"{leftOut} {index.Archives[leftOut].File} [{string.Join(",", index.Archives[leftOut].Files)}]";
            var ns = ContentNamespace.GetOrCreateNamespace($"clprobemiss{leftOut + 1:D2}");
            TakeLogs();

            var handles = new List<ArchiveHandle>();
            for (int i = 0; i < index.Archives.Length; ++i)
                if (i != leftOut)
                    handles.Add(ArchiveFileInterface.MountAsync(ns, Path.Combine(Packed2, index.Archives[i].File), ContentArchives.MountPrefix));
            int complete = 0;
            foreach (var h in handles)
            {
                for (int f = 0; f < 600 && h.Status == ArchiveStatus.InProgress; ++f) await TestUtil.NextFrame();
                if (h.Status == ArchiveStatus.Complete) ++complete;
            }
            Debug.Log($"{Tag} M2[{label}] mounted {complete}/{handles.Count} logs={TakeLogs()}");
            if (complete != handles.Count)
            {
                ContentArchives.Unmount(handles.ToArray());
                return true;
            }

            var watch = Stopwatch.StartNew();
            ContentDirectoryHandle dir = default;
            bool registered = false;
            string thrown = "none";
            try { dir = ContentLoadManager.RegisterContentDirectory(handles[0].GetMountPath()); registered = true; }
            catch (Exception e) { thrown = $"{e.GetType().Name}: {Flatten(e.Message)}"; }
            double ms = watch.Elapsed.TotalMilliseconds;
            await TestUtil.NextFrame();
            Debug.Log($"{Tag} M2[{label}] register: ok={registered} thrown={thrown} ms={ms:F1} logs={TakeLogs()}");
            if (!registered)
            {
                ContentArchives.Unmount(handles.ToArray());
                return true;
            }

            if (leftOut < 0)
            {
                foreach (var catalog in ContentLoadManager.GetRootAssets<ContentCatalog>(dir) ?? Array.Empty<ContentCatalog>())
                    foreach (var e in catalog.Entries)
                        entries.Add((e.name, e.id));
                var names = new List<string>();
                foreach (var e in entries) names.Add(e.name);
                Debug.Log($"{Tag} M2[{label}] entries={entries.Count} [{string.Join(", ", names)}] logs={TakeLogs()}");
            }

            // every entry, asynchronously only: a synchronous load of a broken file has blocked inside the engine before
            bool clean = true;
            var done = new List<Loadable<Object>>();
            foreach (var (name, id) in entries)
            {
                var loadable = new Loadable<Object>(id);
                thrown = "none";
                watch.Restart();
                try { _ = loadable.LoadAsync(); }
                catch (Exception e) { thrown = $"{e.GetType().Name}: {Flatten(e.Message)}"; }
                int frames = 0;
                while (thrown == "none" && (loadable.Status == LoadableStatus.Loading || loadable.Status == LoadableStatus.None) && watch.Elapsed.TotalMilliseconds < LoadLimitMs)
                {
                    await TestUtil.NextFrame();
                    ++frames;
                }
                ms = watch.Elapsed.TotalMilliseconds;
                await TestUtil.NextFrame();
                var status = loadable.Status;
                var target = status == LoadableStatus.Loaded ? loadable.Target : null;
                Debug.Log($"{Tag} M2[{label}] load '{name}': status={status} target={(target != null ? target.name : "null")} thrown={thrown} frames={frames} ms={ms:F1} logs={TakeLogs()}");
                if (status == LoadableStatus.Loading) clean = false;
                else done.Add(loadable);
            }

            // the synchronous call RegisterDirectory makes right after registering — last, in case it blocks
            if (leftOut >= 0)
            {
                watch.Restart();
                thrown = "none";
                int count = -1;
                try { count = ContentLoadManager.GetRootAssets<ContentCatalog>(dir)?.Length ?? -1; }
                catch (Exception e) { thrown = $"{e.GetType().Name}: {Flatten(e.Message)}"; }
                ms = watch.Elapsed.TotalMilliseconds;
                await TestUtil.NextFrame();
                Debug.Log($"{Tag} M2[{label}] root catalogs={count} thrown={thrown} ms={ms:F1} logs={TakeLogs()}");
            }

            foreach (var loadable in done)
            {
                try { loadable.Release(); }
                catch (Exception e) { Debug.Log($"{Tag} M2[{label}] release threw {e.GetType().Name}: {Flatten(e.Message)}"); }
            }
            if (!clean) return false;

            thrown = "none";
            try { ContentLoadManager.UnregisterContentDirectory(dir); }
            catch (Exception e) { thrown = $"{e.GetType().Name}: {Flatten(e.Message)}"; }
            ContentArchives.Unmount(handles.ToArray());
            await TestUtil.NextFrame();
            Debug.Log($"{Tag} M2[{label}] unregister thrown={thrown} unmounted, left in namespace={ArchiveFileInterface.GetMountedArchives(ns).Length} logs={TakeLogs()}");
            return true;
        }
    }
}
