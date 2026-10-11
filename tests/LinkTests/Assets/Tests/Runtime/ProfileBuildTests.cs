using System.Collections;
using System.IO;
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
    /// The directory the runner built from two categories (ContentBuildRunnerTests.R1, EditMode) is
    /// registered and both categories' names load. Registration throws in edit mode, so this half is PlayMode.
    /// </summary>
    [Category("Build")]
    public class ProfileBuildTests
    {
        static string Output(string name) => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", name);

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        [Test, Timeout(30000)]
        public async Task B1_TwoCategoryDirectory_LoadsNamesFromBothCatalogs()
        {
            if (!Directory.Exists(Output("Two")))
                Assert.Ignore("Runner/Two is missing — run the EditMode test ContentBuildRunnerTests.R1 first");

            int dir = ContentService.RegisterDirectory(Output("Two"));
            try
            {
                Assert.That(ContentService.Contains("Icons/single"), Is.True, "the Icons catalog's name is not indexed");
                Assert.That(ContentService.Contains("Prefabs/Plain"), Is.True, "the Prefabs catalog's name is not indexed");
                Assert.That(ContentService.Contains("Materials/Plain"), Is.False, "an asset outside every category must not be in the directory");

                var icon = await ContentService.AcquireAsync<Texture2D>("Icons/single");
                var prefab = await ContentService.AcquireAsync<GameObject>("Prefabs/Plain");
                try
                {
                    Assert.That(icon.IsValid && icon.Asset != null, Is.True, "Icons/single did not load from the first catalog");
                    Assert.That(prefab.IsValid && prefab.Asset != null, Is.True, "Prefabs/Plain did not load from the second catalog");
                    Assert.That(prefab.Asset.name, Is.EqualTo("Plain"));
                }
                finally
                {
                    if (icon.IsValid) icon.Dispose();
                    if (prefab.IsValid) prefab.Dispose();
                }
            }
            finally
            {
                if (!ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true);
            }
        }

        [Test, Timeout(60000)]
        public async Task B2_PackedDirectory_MountsAndLoadsBothCategories_AndUnregisterUnmounts()
        {
            if (!File.Exists(Path.Combine(Output("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");

            var index = ContentArchives.ReadIndex(Output("Packed2"));
            var space = ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(Output("Packed2")));
            int dir = await ContentService.RegisterPackedDirectoryAsync(Output("Packed2"));
            try
            {
                Assert.That(dir, Is.GreaterThanOrEqualTo(0));
                Assert.That(ArchiveFileInterface.GetMountedArchives(space).Length, Is.EqualTo(index.Archives.Length), "every archive of the index is mounted");
                Assert.That(ContentService.Contains("Icons/single"), Is.True, "the Icons catalog's name is not indexed");
                Assert.That(ContentService.Contains("Prefabs/Plain"), Is.True, "the Prefabs catalog's name is not indexed");
                Assert.That(ContentService.Contains("Materials/Plain"), Is.False);

                var icon = await ContentService.AcquireAsync<Texture2D>("Icons/single");
                var prefab = await ContentService.AcquireAsync<GameObject>("Prefabs/Plain");
                try
                {
                    Assert.That(icon.IsValid && icon.Asset != null, Is.True, "Icons/single did not load from the mounted archives");
                    Assert.That(prefab.IsValid && prefab.Asset != null, Is.True, "Prefabs/Plain did not load from the mounted archives");
                    Debug.Log($"[ContentLink.Tests] packed directory: {index.Archives.Length} archives mounted in '{space.GetName()}' → loaded '{icon.Asset.name}' and '{prefab.Asset.name}'");
                }
                finally
                {
                    if (icon.IsValid) icon.Dispose();
                    if (prefab.IsValid) prefab.Dispose();
                }
            }
            finally
            {
                if (dir >= 0 && !ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true);
            }
            Assert.That(ArchiveFileInterface.GetMountedArchives(space).Length, Is.EqualTo(0), "unregistering must unmount the archives again");
        }

        [Test, Timeout(120000)]
        public async Task B3_ManyArchives_MountTime()
        {
            // MountAsync starts every mount first and then waits; the wall-clock cost is the slowest one, not the sum. Measured, not assumed.
            if (!File.Exists(Path.Combine(Output("Packed3"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed3 is missing — run the EditMode test ContentArchivePackerTests.K5 first");
            var index = ContentArchives.ReadIndex(Output("Packed3"));
            var space = ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(Output("Packed3")));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int dir = await ContentService.RegisterPackedDirectoryAsync(Output("Packed3"));
            watch.Stop();
            try
            {
                Assert.That(dir, Is.GreaterThanOrEqualTo(0));
                Assert.That(ArchiveFileInterface.GetMountedArchives(space).Length, Is.EqualTo(index.Archives.Length));
                Assert.That(ContentService.Contains("t000"), Is.True);
                Debug.Log($"[ContentLink.Tests] B3: {index.Archives.Length} archives mounted and registered in {watch.Elapsed.TotalMilliseconds:F0} ms ({watch.Elapsed.TotalMilliseconds / index.Archives.Length:F2} ms per archive)");
            }
            finally
            {
                if (dir >= 0 && !ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true);
            }
        }

        [Test, Timeout(60000)]
        public async Task B4_CustomPartition_RegistersAndLoadsLikeAnyOther()
        {
            if (!File.Exists(Path.Combine(Output("Packed6"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed6 is missing — run the EditMode test ContentArchivePackerTests.K6 first");
            int dir = await ContentService.RegisterPackedDirectoryAsync(Output("Packed6"));
            try
            {
                Assert.That(dir, Is.GreaterThanOrEqualTo(0));
                var icon = await ContentService.AcquireAsync<Texture2D>("Icons/single");
                var prefab = await ContentService.AcquireAsync<GameObject>("Prefabs/Plain");
                try
                {
                    Assert.That(icon.IsValid && icon.Asset != null, Is.True, "Icons/single did not load from the custom-partitioned archives");
                    Assert.That(prefab.IsValid && prefab.Asset != null, Is.True, "Prefabs/Plain did not load from the custom-partitioned archives");
                }
                finally
                {
                    if (icon.IsValid) icon.Dispose();
                    if (prefab.IsValid) prefab.Dispose();
                }
            }
            finally
            {
                if (dir >= 0 && !ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true);
            }
        }

        [Test, Timeout(30000)]
        public async Task B5_AnIndexThatListsNoArchives_IsRefusedNotThrown()
        {
            // A hand-edited or badly merged archives.json: structurally fine, but nothing to mount.
            var folder = Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "EmptyIndex");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, ContentArchives.IndexFile), "{\"Version\":2,\"Profile\":\"P\",\"Compression\":\"LZ4\",\"MaxArchiveMB\":20,\"Archives\":[]}");
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("lists no archives"));
                int dir = -2; string thrown = "";
                try { dir = await ContentService.RegisterPackedDirectoryAsync(folder); }
                catch (System.Exception e) { thrown = e.GetType().Name; Debug.Log($"[ContentLink.Tests] B5: RegisterPackedDirectoryAsync threw {thrown}"); }
                Assert.That(thrown, Is.Empty, $"an index that lists no archives must be refused, not thrown ({thrown})");
                Assert.That(dir, Is.EqualTo(-1), "an index that lists no archives must be refused with -1, like a missing index");
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test, Timeout(60000)]
        public async Task B6_ATruncatedArchive_IsRefusedBeforeAnythingMounts()
        {
            // A download cut short: the archive on disk is shorter than the index says. The size check is cheap and
            // runs before any mount; same-length corruption is the CRC's job and stays the caller's.
            if (!File.Exists(Path.Combine(Output("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var folder = Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "Truncated");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            Directory.CreateDirectory(folder);
            foreach (var f in Directory.GetFiles(Output("Packed2"))) if (!f.EndsWith(".meta")) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
            var index = ContentArchives.ReadIndex(folder);
            var victim = Path.Combine(folder, index.Archives[index.Archives.Length - 1].File);
            var bytes = File.ReadAllBytes(victim);
            File.WriteAllBytes(victim, new System.ArraySegment<byte>(bytes, 0, bytes.Length / 2).ToArray());
            var space = ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(folder));
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("bytes on disk"));
                int dir = -2; string thrown = "";
                try { dir = await ContentService.RegisterPackedDirectoryAsync(folder); }
                catch (System.Exception e) { thrown = e.GetType().Name; Debug.Log($"[ContentLink.Tests] B6: RegisterPackedDirectoryAsync threw {thrown}: {e.Message}"); }
                Debug.Log($"[ContentLink.Tests] B6: dir={dir} mounted={ArchiveFileInterface.GetMountedArchives(space).Length}");
                Assert.That(thrown, Is.Empty, $"a truncated archive must be refused, not thrown ({thrown})");
                Assert.That(dir, Is.EqualTo(-1), "a truncated archive must be refused with -1");
                Assert.That(ArchiveFileInterface.GetMountedArchives(space).Length, Is.EqualTo(0), "nothing may be mounted when the index and the disk disagree");
            }
            finally
            {
                foreach (var h in ArchiveFileInterface.GetMountedArchives(space)) h.Unmount().Complete();
                Directory.Delete(folder, true);
            }
        }

        [Test, Timeout(60000)]
        public async Task B7_AnIndexThatNamesAFileOutsideItsFolder_IsRefusedBeforeAnythingMounts()
        {
            // An index names files of its own folder: the size check, the mount and the registration would all follow a name
            // that leaves it. One error says why — not a second one saying the index is missing.
            if (!File.Exists(Path.Combine(Output("Packed2"), ContentArchives.IndexFile)))
                Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var parent = Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "Outside");
            var folder = Path.Combine(parent, "pk");
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
            Directory.CreateDirectory(folder);
            foreach (var f in Directory.GetFiles(Output("Packed2"))) if (!f.EndsWith(".meta")) File.Copy(f, Path.Combine(folder, Path.GetFileName(f)));
            var indexPath = Path.Combine(folder, ContentArchives.IndexFile);
            var index = JsonUtility.FromJson<ContentArchives.Index>(File.ReadAllText(indexPath));
            var last = index.Archives[index.Archives.Length - 1];
            File.Move(Path.Combine(folder, last.File), Path.Combine(parent, "outside.archive"));
            last.File = "../outside.archive";
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
            var space = ContentNamespace.GetOrCreateNamespace(ContentArchives.NamespaceFor(folder));
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("is not an archive name"));
                int dir = -2; string thrown = "";
                try { dir = await ContentService.RegisterPackedDirectoryAsync(folder); }
                catch (System.Exception e) { thrown = e.GetType().Name; Debug.Log($"[ContentLink.Tests] B7: RegisterPackedDirectoryAsync threw {thrown}: {e.Message}"); }
                int mounted = ArchiveFileInterface.GetMountedArchives(space).Length;
                Debug.Log($"[ContentLink.Tests] B7: dir={dir} mounted={mounted}");
                if (dir >= 0) ContentService.UnregisterDirectory(dir, force: true);
                Assert.That(thrown, Is.Empty, $"an index that names a file outside its folder must be refused, not thrown ({thrown})");
                Assert.That(dir, Is.EqualTo(-1), "an index that names a file outside its folder must be refused with -1");
                Assert.That(mounted, Is.EqualTo(0), "nothing may be mounted");
            }
            finally
            {
                foreach (var h in ArchiveFileInterface.GetMountedArchives(space)) h.Unmount().Complete();
                Directory.Delete(parent, true);
            }
        }
    }
}
