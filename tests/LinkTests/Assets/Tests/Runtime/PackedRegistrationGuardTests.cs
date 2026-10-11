using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Content;
using Unity.IO.Archive;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// What RegisterPackedDirectoryAsync refuses before anything is mounted — T7b a packed folder that is already
    /// registered (mounting it again breaks the registered one — measured) · T9 an index this player cannot load.
    /// </summary>
    [Category("Build")]
    public class PackedRegistrationGuardTests
    {
        static string Runner(string name) => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", name);
        static string Temp => Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "Guard");

        static string CopyOf(string build)
        {
            var folder = Path.Combine(Temp, build);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            foreach (var f in Directory.GetFiles(Runner(build)))
                if (!f.EndsWith(".meta", StringComparison.Ordinal)) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
            return folder;
        }

        static int Listed(string folder) => ArchiveFileInterface.GetMountedArchives(ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(folder))).Length;

        static void UnmountAll(string folder)
        {
            foreach (var h in ArchiveFileInterface.GetMountedArchives(ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(folder)))) h.Unmount().Complete();
        }

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)] static extern int Chmod(string path, uint mode);

        static bool Posix => Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer
                             || Application.platform == RuntimePlatform.LinuxEditor || Application.platform == RuntimePlatform.LinuxPlayer;

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i) { ContentService.Collect(); yield return null; }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        /// <summary>T9: another target · a newer engine — refused (-1, one error) and nothing mounted.</summary>
        [Test, Timeout(60000)]
        public async Task T9_AnIndexThisPlayerCannotLoad_IsRefusedBeforeMounting()
        {
            if (!File.Exists(Path.Combine(Runner("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            foreach (var (what, field, value) in new[] { ("another target", "Target", "iOS"), ("a newer engine", "UnityVersion", "9999.1.0f1") })
            {
                var folder = CopyOf("Packed2");
                var indexPath = Path.Combine(folder, ContentArchives.IndexFile);
                File.WriteAllText(indexPath, Regex.Replace(File.ReadAllText(indexPath), $"\"{field}\": \"[^\"]*\"", $"\"{field}\": \"{value}\""));
                var space = ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(folder));
                LogAssert.Expect(LogType.Error, new Regex("cannot be loaded by this player"));
                int dir = await ContentService.RegisterPackedDirectoryAsync(folder);
                Assert.That(dir, Is.EqualTo(-1), what);
                Assert.That(ArchiveFileInterface.GetMountedArchives(space).Length, Is.EqualTo(0), $"{what}: nothing mounted");
            }
        }

        /// <summary>T7b: the second registration is refused and the first keeps loading — a name it never loaded too.</summary>
        [Test, Timeout(60000)]
        public async Task T7b_ASecondRegistration_LeavesTheFirstLoading()
        {
            if (!File.Exists(Path.Combine(Runner("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var folder = CopyOf("Packed2");
            int first = await ContentService.RegisterPackedDirectoryAsync(folder);
            Assert.That(first, Is.GreaterThanOrEqualTo(0));
            var held = await ContentService.AcquireAsync<Texture2D>("Icons/single");
            int second = -2;
            try
            {
                Exception thrown = null;
                try { second = await ContentService.RegisterPackedDirectoryAsync(folder); }
                catch (Exception e) { thrown = e; }
                Assert.That(thrown, Is.InstanceOf<InvalidOperationException>(), "the second registration is refused");

                var prefab = await ContentService.AcquireAsync<GameObject>("Prefabs/Plain");
                try { Assert.That(prefab.IsValid && prefab.Asset != null, Is.True, "a name not loaded before still loads from the first registration"); }
                finally { if (prefab.IsValid) prefab.Dispose(); }
                Assert.That(held.IsValid && held.Asset != null, Is.True, "the held asset lives");
            }
            finally
            {
                if (held.IsValid) held.Dispose();
                if (second >= 0 && !ContentService.UnregisterDirectory(second)) ContentService.UnregisterDirectory(second, force: true);
                if (!ContentService.UnregisterDirectory(first)) ContentService.UnregisterDirectory(first, force: true);
            }
        }

        /// <summary>T40: two registrations of one folder started on the same frame — one registers, the other is refused before it mounts anything, and the folder keeps loading.</summary>
        [Test, Timeout(60000)]
        public async Task T40_TwoRegistrationsAtOnce_TheSecondIsRefusedBeforeMounting()
        {
            if (!File.Exists(Path.Combine(Runner("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var folder = CopyOf("Packed2");
            var index = ContentArchives.ReadIndex(folder);
            var a = ContentService.RegisterPackedDirectoryAsync(folder);
            var b = ContentService.RegisterPackedDirectoryAsync(folder);
            int da = -1, db = -1;
            Exception ea = null, eb = null;
            try { da = await a; } catch (Exception e) { ea = e; }
            try { db = await b; } catch (Exception e) { eb = e; }
            try
            {
                Debug.Log($"[ContentLink.Tests] T40: first={da} ({ea?.Message ?? "-"}) second={db} ({eb?.Message ?? "-"}) listed={Listed(folder)}");
                Assert.That(ea, Is.Null, "the first registers");
                Assert.That(eb, Is.InstanceOf<InvalidOperationException>(), "the second is refused");
                Assert.That(eb.Message, Does.Contain("is being registered").Or.Contain("is already registered"), "refused by our guard, before it mounts anything");
                Assert.That(Listed(folder), Is.EqualTo(index.Archives.Length), "each archive is mounted once");
                var prefab = await ContentService.AcquireAsync<GameObject>("Prefabs/Plain");
                try { Assert.That(prefab.IsValid && prefab.Asset != null, Is.True, "a name not loaded before loads from the registration"); }
                finally { if (prefab.IsValid) prefab.Dispose(); }
            }
            finally
            {
                foreach (var d in new[] { da, db }) if (d >= 0 && !ContentService.UnregisterDirectory(d)) ContentService.UnregisterDirectory(d, force: true);
                UnmountAll(folder);
            }
        }

        /// <summary>T41: a mount that gives up (here at once — maxFrames 0, the engine's mounts are still in progress) leaves nothing of itself mounted; mounting again mounts each archive once.</summary>
        [Test, Timeout(60000)]
        public async Task T41_AMountThatGivesUp_LeavesNothingMounted()
        {
            if (!File.Exists(Path.Combine(Runner("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var folder = CopyOf("Packed2");
            var index = ContentArchives.ReadIndex(folder);
            ArchiveHandle[] again = null;
            try
            {
                Exception thrown = null;
                try { await ContentArchives.MountAsync(folder, index, maxFrames: 0); } catch (Exception e) { thrown = e; }
                Assume.That(thrown, Is.Not.Null, "every mount was complete at once — nothing gave up");
                Assert.That(thrown, Is.InstanceOf<InvalidOperationException>());
                StringAssert.Contains("(InProgress)", thrown.Message);
                int right = Listed(folder);
                var until = Time.realtimeSinceStartupAsDouble + 0.5;                                    // frames are a fraction of a millisecond in batch mode
                while (Time.realtimeSinceStartupAsDouble < until) await Awaitable.NextFrameAsync();
                int later = Listed(folder);
                again = await ContentArchives.MountAsync(folder, index);
                int then = Listed(folder);
                Debug.Log($"[ContentLink.Tests] T41: listed right after {right} · half a second later {later} · after mounting again {then} (archives {index.Archives.Length})");
                Assert.That($"{right}/{later}/{then}", Is.EqualTo($"0/0/{index.Archives.Length}"), "nothing left by the mount that gave up — then each archive once");
            }
            finally
            {
                if (again != null) ContentArchives.Unmount(again);
                UnmountAll(folder);
            }
        }

        /// <summary>T42: an archive that cannot be read (mode 000 — macOS and Linux) fails its mount; nothing of the registration stays mounted, the failed handle too.</summary>
        [Test, Timeout(60000)]
        public async Task T42_AnArchiveThatCannotBeRead_LeavesNothingMounted()
        {
            if (!Posix) Assert.Ignore("mode 000 needs a POSIX file system");
            if (!File.Exists(Path.Combine(Runner("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var folder = CopyOf("Packed2");
            var index = ContentArchives.ReadIndex(folder);
            var first = Path.Combine(folder, index.Archives[0].File);
            Assume.That(Chmod(first, 0), Is.EqualTo(0));
            try
            {
                LogAssert.ignoreFailingMessages = true;   // the engine's own lines (Unable to open archive file …) come during the mount; set in the body — SetUp is too early
                Exception thrown = null;
                try { await ContentService.RegisterPackedDirectoryAsync(folder); } catch (Exception e) { thrown = e; }
                LogAssert.ignoreFailingMessages = false;
                var until = Time.realtimeSinceStartupAsDouble + 0.5;
                while (Time.realtimeSinceStartupAsDouble < until) await Awaitable.NextFrameAsync();
                Debug.Log($"[ContentLink.Tests] T42: {thrown?.Message ?? "no exception"} · listed {Listed(folder)}");
                Assert.That(thrown, Is.InstanceOf<InvalidOperationException>());
                StringAssert.Contains("(Failed)", thrown.Message);
                Assert.That(Listed(folder), Is.EqualTo(0), "nothing of the failed registration stays listed — the failed handle too");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Chmod(first, 420);
                UnmountAll(folder);
            }
        }
    }
}
