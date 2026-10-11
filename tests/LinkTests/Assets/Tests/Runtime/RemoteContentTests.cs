using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// T18b · T31 ~ T37: a packed directory on a loopback server (HttpListener), downloaded into a cache folder,
    /// registered and loaded. The served builds are copies of Runner/Packed2 and Runner/Packed3 (EditMode K2 · K5).
    /// </summary>
    [Category("Build")]
    public class RemoteContentTests
    {
        public sealed class LoopbackServer : IDisposable
        {
            readonly HttpListener m_Listener = new HttpListener();
            public string Root;
            public readonly int Port;
            public readonly List<string> Requests = new List<string>();
            public readonly Dictionary<string, int> CutOnce = new Dictionary<string, int>();
            public readonly HashSet<string> Corrupt = new HashSet<string>();
            public readonly HashSet<string> Missing = new HashSet<string>();
            public volatile int ChunkDelayMs;
            public volatile string CacheControl;
            public volatile bool IgnoreRange;                                                         // answer a Range with 200 and the whole file
            public readonly Dictionary<string, (int After, int Ms)> StallOnce = new Dictionary<string, (int, int)>();   // once: after this many body bytes, send nothing for Ms
            public readonly Dictionary<string, byte[]> Body = new Dictionary<string, byte[]>();       // serve these bytes under that name
            public volatile string Stalling;                                                          // the name being stalled right now
            public readonly Dictionary<string, int> CutTimes = new Dictionary<string, int>();         // with CutOnce: cut this many responses (1 when unset)
            readonly ManualResetEvent m_Wake = new ManualResetEvent(false);
            static int s_InFlight;
            public static int MaxInFlightAll;   // over every server: what the client had open at once
            public string BaseUrl => $"http://127.0.0.1:{Port}/c";

            public LoopbackServer(string root)
            {
                Root = root;
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                m_Listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                m_Listener.Start();
                new Thread(() =>
                {
                    while (m_Listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = m_Listener.GetContext(); } catch { return; }
                        ThreadPool.QueueUserWorkItem(_ => Serve(ctx));
                    }
                }) { IsBackground = true }.Start();
            }

            public int Count(string file) { lock (Requests) { int n = 0; foreach (var r in Requests) if (r.StartsWith(file + " ", StringComparison.Ordinal)) ++n; return n; } }
            public int CountArchives() { lock (Requests) { int n = 0; foreach (var r in Requests) if (r.Contains(".archive ")) ++n; return n; } }
            public bool Saw(string text) { lock (Requests) return Requests.Exists(r => r.Contains(text)); }

            void Serve(HttpListenerContext ctx)
            {
                var res = ctx.Response;
                var name = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).Substring("/c/".Length);
                var range = ctx.Request.Headers["Range"];
                lock (Requests) Requests.Add($"{name} range={range ?? "-"} cc={ctx.Request.Headers["Cache-Control"] ?? "-"}");
                int now = Interlocked.Increment(ref s_InFlight);
                lock (typeof(LoopbackServer)) if (now > MaxInFlightAll) MaxInFlightAll = now;
                try
                {
                    var path = Path.Combine(Root, name);
                    byte[] all;
                    lock (Body) Body.TryGetValue(name, out all);
                    if (all == null)
                    {
                        if (Missing.Contains(name) || !File.Exists(path)) { res.StatusCode = 404; res.Close(); return; }
                        all = File.ReadAllBytes(path);
                    }
                    if (Corrupt.Contains(name)) { all = (byte[])all.Clone(); all[all.Length / 2] ^= 0xFF; }
                    long start = 0;
                    if (range != null && range.StartsWith("bytes=") && !IgnoreRange)
                    {
                        start = long.Parse(range.Substring(6, range.IndexOf('-') - 6));
                        res.StatusCode = 206;
                        res.AddHeader("Content-Range", $"bytes {start}-{all.Length - 1}/{all.Length}");
                    }
                    if (CacheControl != null) res.AddHeader("Cache-Control", CacheControl);
                    long len = all.Length - start;
                    res.ContentLength64 = len;
                    int cut = -1;
                    lock (CutOnce)
                    {
                        if (!CutOnce.TryGetValue(name, out cut)) cut = -1;
                        else if (!CutTimes.TryGetValue(name, out var times) || times <= 1) { CutOnce.Remove(name); CutTimes.Remove(name); }
                        else CutTimes[name] = times - 1;
                    }
                    int stallAfter = -1, stallMs = 0;
                    lock (StallOnce) if (StallOnce.TryGetValue(name, out var stall)) { StallOnce.Remove(name); stallAfter = stall.After; stallMs = stall.Ms; }
                    long sent = 0;
                    while (sent < len)
                    {
                        if (stallAfter >= 0 && sent >= stallAfter) { Stalling = name; m_Wake.WaitOne(stallMs); Stalling = null; stallAfter = -1; }
                        int n = (int)Math.Min(16 * 1024, len - sent);
                        if (cut >= 0) n = (int)Math.Min(n, Math.Max(0, cut - sent));
                        if (stallAfter >= 0) n = (int)Math.Min(n, stallAfter - sent);
                        if (n > 0) { res.OutputStream.Write(all, (int)(start + sent), n); res.OutputStream.Flush(); sent += n; }
                        if (cut >= 0 && sent >= cut) { res.Abort(); return; }
                        if (ChunkDelayMs > 0) Thread.Sleep(ChunkDelayMs);
                    }
                    res.Close();
                }
                catch { try { res.Abort(); } catch { } }
                finally { Interlocked.Decrement(ref s_InFlight); }
            }

            public void Dispose() { m_Wake.Set(); try { m_Listener.Stop(); m_Listener.Close(); } catch { } }
        }

        static string Runner(string name) => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", name);
        static string Temp => Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "Remote");
        static string CacheRoot => Path.Combine(Temp, "cache");

        static void CopyBuild(string from, string to, string profile = null)
        {
            if (Directory.Exists(to)) Directory.Delete(to, true);
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
                if (!f.EndsWith(".meta", StringComparison.Ordinal)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            if (profile == null) return;   // a build served under another profile's name: rewrite its index and hash, as a rebuild would
            var indexPath = Path.Combine(to, ContentArchives.IndexFile);
            var json = Regex.Replace(File.ReadAllText(indexPath), "\"Profile\": \"[^\"]*\"", $"\"Profile\": \"{profile}\"");
            var bytes = new UTF8Encoding(false).GetBytes(json);
            File.WriteAllBytes(indexPath, bytes);
            using var md5 = MD5.Create();
            var sb = new StringBuilder(); foreach (var b in md5.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
            File.WriteAllText(Path.Combine(to, ContentArchives.HashFile), sb.ToString());
        }

        static ContentRemoteOptions Options(int retry = 0, string builtIn = null, int timeout = 0) => new ContentRemoteOptions { CacheRoot = CacheRoot, RetryCount = retry, BuiltInDirectory = builtIn, Timeout = timeout };

        [DllImport("libc", EntryPoint = "chmod", SetLastError = true)] static extern int Chmod(string path, uint mode);

        static bool Posix => Application.platform == RuntimePlatform.OSXEditor || Application.platform == RuntimePlatform.OSXPlayer
                             || Application.platform == RuntimePlatform.LinuxEditor || Application.platform == RuntimePlatform.LinuxPlayer;

        static ContentArchives.Archive Biggest(ContentArchives.Index index)
        {
            var biggest = index.Archives[0];
            foreach (var a in index.Archives) if (a.Bytes > biggest.Bytes) biggest = a;
            return biggest;
        }

        [SetUp]
        public void Fresh()
        {
            if (!File.Exists(Path.Combine(Runner("Packed2"), ContentArchives.HashFile)))
                Assert.Ignore("Runner/Packed2 (index v2) is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            if (Directory.Exists(Temp)) Directory.Delete(Temp, true);
            Directory.CreateDirectory(Temp);
        }

        [UnityTearDown]
        public IEnumerator LeakCheck()
        {
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i) { ContentService.Collect(); yield return null; }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        static async Task<string> Load<T>(string name) where T : Object
        {
            var h = await ContentService.AcquireAsync<T>(name);
            string s = h.IsValid && h.Asset != null ? h.Asset.name : "failed";
            if (h.IsValid) h.Dispose();
            return s;
        }

        static void Unregister(int dir) { if (dir >= 0 && !ContentService.UnregisterDirectory(dir)) ContentService.UnregisterDirectory(dir, force: true); }

        /// <summary>T31: check → download → register → load, from a loopback server.</summary>
        [Test, Timeout(60000)]
        public async Task T31_DownloadRegisterAndLoad()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            using var server = new LoopbackServer(served);
            var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            var index = ContentArchives.ReadIndex(served);
            long total = 0; foreach (var a in index.Archives) total += a.Bytes;
            Assert.That(remote.Source, Is.EqualTo(ContentRemoteSource.Remote), remote.Error);
            Assert.That(remote.BytesToDownload, Is.EqualTo(total));
            Assert.That(await remote.DownloadAsync(), Is.True, remote.Error);
            int dir = await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
            try
            {
                Assert.That(dir, Is.GreaterThanOrEqualTo(0));
                Assert.That(await Load<Texture2D>("Icons/single"), Is.EqualTo("single"));
                Assert.That(await Load<GameObject>("Prefabs/Plain"), Is.EqualTo("Plain"));
                Assert.That(server.CountArchives(), Is.EqualTo(index.Archives.Length), "every archive once");
            }
            finally { Unregister(dir); }
        }

        /// <summary>T18b: two directories downloading at once share one limit (the harness runs one at a time and cannot show it).</summary>
        [Test, Timeout(120000)]
        public async Task T18b_TwoDirectoriesAtOnce_ShareTheLimit()
        {
            var a = Path.Combine(Temp, "a"); CopyBuild(Runner("Packed3"), a);
            var b = Path.Combine(Temp, "b"); CopyBuild(Runner("Packed3"), b, profile: "Packed3b");
            using var serverA = new LoopbackServer(a) { ChunkDelayMs = 5 };
            using var serverB = new LoopbackServer(b) { ChunkDelayMs = 5 };
            int was = ContentRemote.MaxConcurrentWebRequests;
            ContentRemote.MaxConcurrentWebRequests = 2;
            try
            {
                var ra = await ContentRemote.CheckAsync("Packed3", serverA.BaseUrl, Options());
                var rb = await ContentRemote.CheckAsync("Packed3b", serverB.BaseUrl, Options());
                LoopbackServer.MaxInFlightAll = 0;
                var da = ra.DownloadAsync(); var db = rb.DownloadAsync();
                Assert.That(await da, Is.True, ra.Error);
                Assert.That(await db, Is.True, rb.Error);
                Assert.That(serverA.CountArchives() > 0 && serverB.CountArchives() > 0, Is.True, "both downloaded");
                Debug.Log($"[ContentLink.Tests] T18b: at most {LoopbackServer.MaxInFlightAll} requests open over both servers (limit 2)");
                // the server counts a request until its handler returns — it may still be closing one the client already finished, so one more may show
                Assert.That(LoopbackServer.MaxInFlightAll, Is.InRange(2, 3), "one limit for both directories, and it was reached");
            }
            finally { ContentRemote.MaxConcurrentWebRequests = was; }
        }

        /// <summary>T32: a dropped connection resumes with Range · wrong bytes are refused and never named · a 404 fails the download.</summary>
        [Test, Timeout(60000)]
        public async Task T32_DropResumes_WrongBytesRefused_NotFoundFails()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            var index = ContentArchives.ReadIndex(served);
            var biggest = index.Archives[0];
            foreach (var a in index.Archives) if (a.Bytes > biggest.Bytes) biggest = a;
            using var server = new LoopbackServer(served);
            server.CutOnce[biggest.File] = (int)(biggest.Bytes / 2);
            var resumed = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options(retry: 1));
            Assert.That(await resumed.DownloadAsync(), Is.True, resumed.Error);
            Assert.That(server.Saw($"{biggest.File} range=bytes={biggest.Bytes / 2}-"), Is.True, "the retry asked for the rest only");
            int dir = await ContentService.RegisterPackedDirectoryAsync(resumed.CacheDirectory);
            try { Assert.That(await Load<GameObject>("Prefabs/Plain"), Is.EqualTo("Plain")); }
            finally { Unregister(dir); }

            ContentRemote.ClearCache("Packed2", Options());
            server.Corrupt.Add(biggest.File);
            LogAssert.ignoreFailingMessages = true;
            var corrupt = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options(retry: 1));
            bool ok = await corrupt.DownloadAsync();
            Assert.That(ok, Is.False);
            Assert.That(corrupt.Error, Does.Contain("MD5"));
            Assert.That(File.Exists(Path.Combine(corrupt.CacheDirectory, biggest.File)), Is.False, "a file that failed its MD5 never gets its name");
            Assert.That(File.Exists(Path.Combine(corrupt.CacheDirectory, ContentArchives.IndexFile)), Is.False, "nothing committed");
            server.Corrupt.Clear();

            server.Missing.Add(biggest.File);
            var missing = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Assert.That(await missing.DownloadAsync(), Is.False);
            Assert.That(missing.Error, Does.Contain("404"));
            LogAssert.ignoreFailingMessages = false;
        }

        /// <summary>T33: a whole cache answers from one request (the hash).</summary>
        [Test, Timeout(60000)]
        public async Task T33_ACacheHit_IsOneRequest()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            using var server = new LoopbackServer(served);
            Assert.That(await (await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options())).DownloadAsync(), Is.True);
            lock (server.Requests) server.Requests.Clear();
            var again = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Assert.That(again.BytesToDownload, Is.EqualTo(0));
            Assert.That(await again.DownloadAsync(), Is.True);
            Assert.That(server.Requests, Has.Count.EqualTo(1).And.Some.StartsWith(ContentArchives.HashFile));
        }

        /// <summary>T34: polling the status allocates nothing; the frames of a running download are counted for GC.Alloc blocks.</summary>
        [Test, Timeout(120000)]
        public async Task T34_StatusPolling_AllocatesNothing()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed3"), served);
            using var server = new LoopbackServer(served) { ChunkDelayMs = 2 };
            var remote = await ContentRemote.CheckAsync("Packed3", server.BaseUrl, Options());
            var download = remote.DownloadAsync();
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            int frames = 0, blocks = 0, framesWithBlocks = 0;
            while (!remote.Status.IsDone && frames < 2000)
            {
                await Awaitable.NextFrameAsync();
                ++frames;
                int b = recorder.sampleBlockCount;
                if (frames > 3) { blocks += b; if (b > 0) ++framesWithBlocks; }
                if (frames == 10) Assert.That(() => { var s = remote.Status; }, Is.Not.AllocatingGCMemory(), "reading the status");
            }
            recorder.enabled = false;
            Assert.That(await download, Is.True, remote.Error);
            Debug.Log($"[ContentLink.Tests] T34: {frames} frames of download · GC.Alloc blocks after the first three frames: {blocks} in {framesWithBlocks} frames · {remote.BytesToDownload} B");
        }

        /// <summary>T35: archives the app ships are copied from StreamingAssets (no archive request) — and with the server gone, the built-in build is taken.</summary>
        [Test, Timeout(60000)]
        public async Task T35_BuiltInCopy_AndOfflineFallback()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            using (var server = new LoopbackServer(served))
            {
                var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options(builtIn: Runner("Packed2")));
                Assert.That(remote.BytesToDownload, Is.EqualTo(0), "everything is shipped");
                Assert.That(await remote.DownloadAsync(), Is.True, remote.Error);
                Assert.That(server.CountArchives(), Is.EqualTo(0), "no archive request");
            }
            ContentRemote.ClearCache("Packed2", Options());
            LogAssert.ignoreFailingMessages = true;
            var offline = await ContentRemote.CheckAsync("Packed2", "http://127.0.0.1:9/c", Options(builtIn: Runner("Packed2")));
            LogAssert.ignoreFailingMessages = false;
            Assert.That(offline.Source, Is.EqualTo(ContentRemoteSource.BuiltIn), offline.Error);
            Assert.That(await offline.DownloadAsync(), Is.True, offline.Error);
            int dir = await ContentService.RegisterPackedDirectoryAsync(offline.CacheDirectory);
            try { Assert.That(await Load<Texture2D>("Icons/single"), Is.EqualTo("single")); }
            finally { Unregister(dir); }
        }

        /// <summary>T36: an update downloaded while the folder is registered — applied by unregistering and registering again; then the old files go.</summary>
        [Test, Timeout(120000)]
        public async Task T36_UpdateWhileRegistered_AppliesOnTheNextRegistration()
        {
            var v1 = Path.Combine(Temp, "v1"); CopyBuild(Runner("Packed2"), v1);
            var v2 = Path.Combine(Temp, "v2"); CopyBuild(Runner("Packed3"), v2, profile: "Packed2");   // another build of the same profile
            using var server = new LoopbackServer(v1);
            var first = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Assert.That(await first.DownloadAsync(), Is.True, first.Error);
            int dir = await ContentService.RegisterPackedDirectoryAsync(first.CacheDirectory);
            Assert.That(await Load<Texture2D>("Icons/single"), Is.EqualTo("single"));

            server.Root = v2;
            var update = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Assert.That(update.BytesToDownload, Is.GreaterThan(0));
            Assert.That(await update.DownloadAsync(), Is.True, update.Error);
            Assert.That(ContentService.Contains("Icons/single"), Is.True, "the registered directory still serves the old build");
            var (keptWhileMounted, _) = update.CleanCache();
            Assert.That(keptWhileMounted, Is.EqualTo(0), "mounted files are never cleaned");

            Assert.That(ContentService.UnregisterDirectory(dir), Is.True);
            dir = await ContentService.RegisterPackedDirectoryAsync(update.CacheDirectory);
            try
            {
                Assert.That(ContentService.Contains("t000"), Is.True, "the new build answers");
                Assert.That(ContentService.Contains("Icons/single"), Is.False, "the old build is gone");
                Assert.That(await Load<Object>("t000"), Is.EqualTo("t000"));
                var (cleaned, _) = update.CleanCache();
                Assert.That(cleaned, Is.EqualTo(ContentArchives.ReadIndex(v1).Archives.Length), "the old build's archives are cleaned now");
            }
            finally { Unregister(dir); }
        }

        /// <summary>T37: the check reaches the server even after a response said max-age (no client cache — measured).</summary>
        [Test, Timeout(60000)]
        public async Task T37_TheCheckReachesTheServer_DespiteMaxAge()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            using var server = new LoopbackServer(served) { CacheControl = "max-age=3600" };
            await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Assert.That(server.Count(ContentArchives.HashFile), Is.EqualTo(2));
            Assert.That(server.Saw($"{ContentArchives.HashFile} range=- cc=no-cache"), Is.True);
        }

        /// <summary>T39: a remote index that names a file outside the cache folder is refused before anything is requested — the file beside the folder keeps its bytes.</summary>
        [Test, Timeout(60000)]
        public async Task T39_ARemoteIndexThatNamesAFileOutsideTheCache_ChangesNothing()
        {
            // The client drops '..' from a URL, so the base ends in /d: the request would land on served/victim.archive while
            // the name resolves beside the profile's cache folder. The victim's size differs from the served bytes, or a
            // cache hit (by size) would hide the write.
            var served = Path.Combine(Temp, "served");
            var d = Path.Combine(served, "d"); CopyBuild(Runner("Packed2"), d);
            var indexPath = Path.Combine(d, ContentArchives.IndexFile);
            var index = JsonUtility.FromJson<ContentArchives.Index>(File.ReadAllText(indexPath));
            var last = index.Archives[index.Archives.Length - 1];
            File.Move(Path.Combine(d, last.File), Path.Combine(served, "victim.archive"));
            last.File = "../victim.archive";
            var bytes = new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(index, true));
            File.WriteAllBytes(indexPath, bytes);
            using (var md5 = MD5.Create())
            {
                var sb = new StringBuilder(); foreach (var b in md5.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
                File.WriteAllText(Path.Combine(d, ContentArchives.HashFile), sb.ToString());
            }
            Directory.CreateDirectory(CacheRoot);
            var victim = Path.Combine(CacheRoot, "victim.archive");
            var original = new byte[777]; new System.Random(39).NextBytes(original); File.WriteAllBytes(victim, original);
            using var server = new LoopbackServer(served);
            LogAssert.Expect(LogType.Error, new Regex("is not an archive name"));
            var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl + "/d", Options());
            bool got = await remote.DownloadAsync();
            bool kept = System.Linq.Enumerable.SequenceEqual(File.ReadAllBytes(victim), original);
            Debug.Log($"[ContentLink.Tests] T39: source={remote.Source} download={got} victim kept={kept} archive requests={server.CountArchives()} victim requests={server.Count("victim.archive")}");
            Assert.That(kept, Is.True, "the file beside the cache folder must keep its bytes");
            Assert.That(server.CountArchives(), Is.EqualTo(0), "nothing may be requested");
            Assert.That(remote.Source, Is.Not.EqualTo(ContentRemoteSource.Remote));
            Assert.That(remote.Error, Does.Contain("is not an archive name"), "the check says why");
        }

        /// <summary>T43: the committed archives.json cannot be read (held with FileShare.None, as a scanner holds it): the check goes on without it — the remote, then the built-in build.</summary>
        [Test, Timeout(60000)]
        public async Task T43_ACommittedIndexThatCannotBeRead_IsNoIndex()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            using var server = new LoopbackServer(served);
            Assert.That(await (await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options())).DownloadAsync(), Is.True, "committed");
            var idx = Path.Combine(CacheRoot, "Packed2", ContentArchives.IndexFile);
            using (new FileStream(idx, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LogAssert.Expect(LogType.Error, new Regex("could not be read"));
                var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
                Assert.That(remote.Source, Is.EqualTo(ContentRemoteSource.Remote), remote.Error);
                server.Dispose();                                                                      // the remote goes away
                LogAssert.Expect(LogType.Error, new Regex("could not be read"));
                var builtIn = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options(builtIn: Runner("Packed2")));
                Assert.That(builtIn.Source, Is.EqualTo(ContentRemoteSource.BuiltIn), builtIn.Error);
            }
        }

        /// <summary>T44: a cancel once two archives are in place, then the same ContentRemote again — an archive the first call installed is not asked for again; the result registers and loads.</summary>
        [Test, Timeout(120000)]
        public async Task T44_ACancelledDownload_CalledAgain_DoesNotAskForWhatItInstalled()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            var index = ContentArchives.ReadIndex(served);
            using var server = new LoopbackServer(served) { ChunkDelayMs = 30 };
            int was = ContentRemote.MaxConcurrentWebRequests;
            ContentRemote.MaxConcurrentWebRequests = 1;                                                // the archives finish in order
            try
            {
                var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
                var installed = new List<string>();
                void Look() { installed.Clear(); foreach (var a in index.Archives) if (File.Exists(Path.Combine(remote.CacheDirectory, a.File))) installed.Add(a.File); }
                var cts = new CancellationTokenSource();
                var awaiter = remote.DownloadAsync(cts.Token).GetAwaiter();
                try
                {
                    var until = Time.realtimeSinceStartupAsDouble + 20;                                 // a time, not frames: batch frames are a fraction of a millisecond
                    while (!awaiter.IsCompleted && Time.realtimeSinceStartupAsDouble < until) { await Awaitable.NextFrameAsync(); Look(); if (installed.Count >= 2) cts.Cancel(); }
                }
                finally { cts.Cancel(); }
                while (!awaiter.IsCompleted) await Awaitable.NextFrameAsync();
                Assert.Throws<OperationCanceledException>(() => awaiter.GetResult());
                Look();
                var first = new List<string>(installed);
                Assume.That(first.Count, Is.InRange(2, index.Archives.Length - 1), "the cancel came between the archives");
                Assert.That(await remote.DownloadAsync(), Is.True, remote.Error);
                var again = new List<string>(); foreach (var file in first) if (server.Count(file) != 1) again.Add($"{file} x{server.Count(file)}");
                Debug.Log($"[ContentLink.Tests] T44: installed by the first call {first.Count} · asked for again {again.Count} ({string.Join(", ", again)}) · requests {server.Requests.Count}");
                Assert.That(again, Is.Empty, "an archive the first call installed is not asked for again");
                int dir = await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
                try { Assert.That(await Load<GameObject>("Prefabs/Plain"), Is.EqualTo("Plain")); }
                finally { Unregister(dir); }
            }
            finally { ContentRemote.MaxConcurrentWebRequests = was; }
        }

        /// <summary>T45: a file the cache cannot take fails the download with false and a reason, not an exception — an archive's name taken by a folder; a cache folder that cannot be written (the .part cannot be made — macOS and Linux).</summary>
        [Test, Timeout(60000)]
        public async Task T45_AFileTheCacheCannotTake_FailsWithAReason()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            var index = ContentArchives.ReadIndex(served);
            using var server = new LoopbackServer(served);
            var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            var blocked = index.Archives[1].File;
            Directory.CreateDirectory(Path.Combine(remote.CacheDirectory, blocked));
            LogAssert.Expect(LogType.Error, new Regex("the download failed"));
            bool ok = true; Exception thrown = null;
            try { ok = await remote.DownloadAsync(); } catch (Exception e) { thrown = e; }
            Debug.Log($"[ContentLink.Tests] T45 folder: {(thrown == null ? ok.ToString() : thrown.GetType().Name + ": " + thrown.Message)} · Error {remote.Error}");
            Assert.That(thrown, Is.Null, "no exception");
            Assert.That(ok, Is.False);
            StringAssert.Contains(blocked, remote.Error);

            Directory.Delete(remote.CacheDirectory, true);
            var unmade = index.Archives[2].File;
            Directory.CreateDirectory(Path.Combine(remote.CacheDirectory, unmade + ContentRemote.PartExtension));   // its .part cannot be made
            var partless = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            LogAssert.Expect(LogType.Error, new Regex("the download failed"));
            ok = true; thrown = null;
            try { ok = await partless.DownloadAsync(); } catch (Exception e) { thrown = e; }
            Debug.Log($"[ContentLink.Tests] T45 .part: {(thrown == null ? ok.ToString() : thrown.GetType().Name + ": " + thrown.Message)} · Error {partless.Error}");
            Assert.That(thrown, Is.Null, "no exception");
            Assert.That(ok, Is.False);
            StringAssert.Contains(unmade, partless.Error);
            Assert.That(File.Exists(Path.Combine(remote.CacheDirectory, ContentArchives.IndexFile)), Is.False, "nothing committed");
            if (!Posix) return;
            Directory.Delete(remote.CacheDirectory, true);
            Directory.CreateDirectory(remote.CacheDirectory);
            Assume.That(Chmod(remote.CacheDirectory, 365), Is.EqualTo(0));                           // 0555
            try
            {
                int before = server.CountArchives();
                var readOnly = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
                LogAssert.Expect(LogType.Error, new Regex("the download failed"));
                ok = true; thrown = null;
                try { ok = await readOnly.DownloadAsync(); } catch (Exception e) { thrown = e; }
                Debug.Log($"[ContentLink.Tests] T45 read-only: {(thrown == null ? ok.ToString() : thrown.GetType().Name + ": " + thrown.Message)} · Error {readOnly.Error}");
                Assert.That(thrown, Is.Null, "no exception");
                Assert.That(ok, Is.False);
                StringAssert.Contains(".part", readOnly.Error);
                Assert.That(server.CountArchives(), Is.EqualTo(before), "nothing asked for");
            }
            finally { Chmod(remote.CacheDirectory, 493); }
        }

        /// <summary>T46: a server that answers a Range with the whole file (200): the appended .part is thrown away and the archive downloaded once more without a Range — without spending a retry.</summary>
        [Test, Timeout(60000)]
        public async Task T46_AServerThatIgnoresRange_RestartsWithoutRange()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            var biggest = Biggest(ContentArchives.ReadIndex(served));
            using var server = new LoopbackServer(served) { IgnoreRange = true };
            server.CutOnce[biggest.File] = (int)(biggest.Bytes / 3);
            server.CutTimes[biggest.File] = 2;
            var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options(retry: 2));
            Assert.That(await remote.DownloadAsync(), Is.True, remote.Error);
            int ranged = 0; lock (server.Requests) foreach (var line in server.Requests) if (line.StartsWith($"{biggest.File} range=bytes=", StringComparison.Ordinal)) ++ranged;
            Debug.Log($"[ContentLink.Tests] T46: requests for {biggest.File}: {server.Count(biggest.File)} · with Range {ranged}");
            Assert.That(server.Saw($"{biggest.File} range=bytes={biggest.Bytes / 3}-"), Is.True, "the retry asked for the rest");
            Assert.That(ranged, Is.EqualTo(1), "the 200 to the Range was seen: no Range again");
            Assert.That(server.Count(biggest.File), Is.EqualTo(3), "cut · a Range answered whole and cut · one plain download");
            Assert.That(new FileInfo(Path.Combine(remote.CacheDirectory, biggest.File)).Length, Is.EqualTo(biggest.Bytes));
            int dir = await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
            try { Assert.That(await Load<GameObject>("Prefabs/Plain"), Is.EqualTo("Plain")); }
            finally { Unregister(dir); }
        }

        /// <summary>T47: a stall longer than Timeout aborts the request, which is retried from the bytes the abort kept (Range); a cancel keeps the .part and the next check resumes from it.</summary>
        [Test, Timeout(120000)]
        public async Task T47_AStallIsRetriedFromItsBytes_ACancelKeepsThem()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            var biggest = Biggest(ContentArchives.ReadIndex(served));
            int half = (int)(biggest.Bytes / 2);
            using var server = new LoopbackServer(served);
            server.StallOnce[biggest.File] = (half, 3000);
            var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options(retry: 1, timeout: 1));
            Assert.That(await remote.DownloadAsync(), Is.True, remote.Error);
            Assert.That(server.Saw($"{biggest.File} range=bytes={half}-"), Is.True, "the retry resumed from the bytes the stalled request kept");

            var until = Time.realtimeSinceStartupAsDouble + 5;
            while (server.Stalling != null && Time.realtimeSinceStartupAsDouble < until) await Awaitable.NextFrameAsync();   // the first stall's server thread still sleeps
            ContentRemote.ClearCache("Packed2", Options());
            server.StallOnce[biggest.File] = (half, 10000);
            var cancelled = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            var cts = new CancellationTokenSource();
            var awaiter = cancelled.DownloadAsync(cts.Token).GetAwaiter();
            var partPath = Path.Combine(cancelled.CacheDirectory, biggest.File + ContentRemote.PartExtension);
            var deadline = Time.realtimeSinceStartupAsDouble + 20; int steady = 0;
            while (!awaiter.IsCompleted && Time.realtimeSinceStartupAsDouble < deadline)
            {
                await Awaitable.NextFrameAsync();
                if (cts.IsCancellationRequested) continue;
                long have = File.Exists(partPath) ? new FileInfo(partPath).Length : -1;
                steady = server.Stalling == biggest.File && have == half ? steady + 1 : 0;   // the half is on disk and nothing more comes
                if (steady >= 5) cts.Cancel();
            }
            if (!cts.IsCancellationRequested) cts.Cancel();
            while (!awaiter.IsCompleted) await Awaitable.NextFrameAsync();

            Assert.Throws<OperationCanceledException>(() => awaiter.GetResult());
            var part = Path.Combine(cancelled.CacheDirectory, biggest.File + ContentRemote.PartExtension);
            long kept = File.Exists(part) ? new FileInfo(part).Length : -1;
            var resume = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Debug.Log($"[ContentLink.Tests] T47: the cancel kept {kept} of {biggest.Bytes} · the next check plans {resume.BytesToDownload}");
            Assert.That(kept, Is.EqualTo(half), "the cancel kept the bytes that arrived");
            Assert.That(await resume.DownloadAsync(), Is.True, resume.Error);
            int ranged = 0; lock (server.Requests) foreach (var r in server.Requests) if (r.StartsWith($"{biggest.File} range=bytes={half}-", StringComparison.Ordinal)) ++ranged;
            Assert.That(ranged, Is.EqualTo(2), "the next download resumed with Range — as the retry did");
        }

        /// <summary>T48: archives.hash answered by a captive portal (200 + HTML) with a whole cache — the cache is used, Error says why, one request.</summary>
        [Test, Timeout(60000)]
        public async Task T48_ACaptivePortalsPage_FallsBackToTheCache()
        {
            var served = Path.Combine(Temp, "served"); CopyBuild(Runner("Packed2"), served);
            using var server = new LoopbackServer(served);
            Assert.That(await (await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options())).DownloadAsync(), Is.True);
            lock (server.Body) server.Body[ContentArchives.HashFile] = Encoding.UTF8.GetBytes("<html><body>Sign in to the Wi-Fi</body></html>");
            lock (server.Requests) server.Requests.Clear();
            var remote = await ContentRemote.CheckAsync("Packed2", server.BaseUrl, Options());
            Assert.That(remote.Source, Is.EqualTo(ContentRemoteSource.Cache), remote.Error);
            StringAssert.Contains("does not hold an MD5", remote.Error);
            Assert.That(server.Requests, Has.Count.EqualTo(1));
            Assert.That(await remote.DownloadAsync(), Is.True);
        }
    }
}
