using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Play Mode ending while a download runs. This project enters Play Mode without a domain reload, so the
    /// statics outlive the session — and the download's awaits are dropped when the session ends (measured), so its own
    /// finally never runs.
    /// </summary>
    [Category("Build")]
    public class RemoteSessionTests
    {
        sealed class Slow : IDisposable
        {
            readonly HttpListener m_Listener = new HttpListener();
            readonly ManualResetEvent m_Stop = new ManualResetEvent(false);
            public readonly int Port;
            public int Served;
            public string BaseUrl => $"http://127.0.0.1:{Port}/c";

            public Slow(string root, int chunk, int delayMs)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); Port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                m_Listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                m_Listener.Start();
                new Thread(() =>
                {
                    while (m_Listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = m_Listener.GetContext(); } catch { return; }
                        ThreadPool.QueueUserWorkItem(_ =>
                        {
                            var res = ctx.Response;
                            try
                            {
                                var name = Uri.UnescapeDataString(ctx.Request.Url.AbsolutePath).Substring("/c/".Length);
                                var path = Path.Combine(root, name);
                                if (!File.Exists(path)) { res.StatusCode = 404; res.Close(); return; }
                                var all = File.ReadAllBytes(path);
                                long start = 0;
                                var range = ctx.Request.Headers["Range"];
                                if (range != null && range.StartsWith("bytes=")) { start = long.Parse(range.Substring(6, range.IndexOf('-') - 6)); res.StatusCode = 206; res.AddHeader("Content-Range", $"bytes {start}-{all.Length - 1}/{all.Length}"); }
                                res.ContentLength64 = all.Length - start;
                                bool slow = name.EndsWith(".archive", StringComparison.Ordinal);
                                for (long sent = 0; sent < all.Length - start;)
                                {
                                    int n = (int)Math.Min(slow ? chunk : 1 << 20, all.Length - start - sent);
                                    res.OutputStream.Write(all, (int)(start + sent), n); res.OutputStream.Flush(); sent += n; Interlocked.Add(ref Served, n);
                                    if (slow && m_Stop.WaitOne(delayMs)) break;
                                }
                                res.Close();
                            }
                            catch { try { res.Abort(); } catch { } }
                        });
                    }
                }) { IsBackground = true }.Start();
            }

            public void Dispose() { m_Stop.Set(); try { m_Listener.Stop(); m_Listener.Close(); } catch { } }
        }

        static IEnumerator Wait(double seconds) { var until = Time.realtimeSinceStartupAsDouble + seconds; while (Time.realtimeSinceStartupAsDouble < until) yield return null; }

        /// <summary>T49: Play Mode ends with a download running — its requests stop with it, and the next session downloads into the same folder.</summary>
        [UnityTest, Timeout(240000)]
        public IEnumerator T49_PlayModeEndsMidDownload_TheNextSessionDownloads()
        {
            var runner = Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", "Packed2");
            if (!File.Exists(Path.Combine(runner, ContentArchives.IndexFile))) Assert.Ignore("Runner/Packed2 is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var temp = Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "Session");
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
            var served = Path.Combine(temp, "served"); Directory.CreateDirectory(served);
            foreach (var f in Directory.GetFiles(runner)) if (!f.EndsWith(".meta", StringComparison.Ordinal)) File.Copy(f, Path.Combine(served, Path.GetFileName(f)));
            var options = new ContentRemoteOptions { CacheRoot = Path.Combine(temp, "cache") };
            const int Chunk = 64;                                                                         // small pieces: what is left keeps coming for seconds unless it is stopped
            using var server = new Slow(served, Chunk, delayMs: 150);

            yield return new EnterPlayMode();
            var check = ContentRemote.CheckAsync("Packed2", server.BaseUrl, options).GetAwaiter();
            while (!check.IsCompleted) yield return null;
            var remote = check.GetResult();
            Assert.That(remote.Source, Is.EqualTo(ContentRemoteSource.Remote), remote.Error);
            remote.DownloadAsync();                                                                       // left running when the session ends
            yield return Wait(0.5);
            Assume.That(Volatile.Read(ref server.Served), Is.GreaterThan(0), "the download had started");
            yield return new ExitPlayMode();

            int atExit = Volatile.Read(ref server.Served);
            yield return Wait(3);
            int afterExit = Volatile.Read(ref server.Served) - atExit;

            yield return new EnterPlayMode();
            var again = ContentRemote.CheckAsync("Packed2", server.BaseUrl, options).GetAwaiter();
            while (!again.IsCompleted) yield return null;
            var next = again.GetResult();
            var download = next.DownloadAsync().GetAwaiter();
            var until = Time.realtimeSinceStartupAsDouble + 60;
            while (!download.IsCompleted && Time.realtimeSinceStartupAsDouble < until) yield return null;
            string result = download.IsCompleted ? (download.GetResult() ? "true" : $"false · {next.Error}") : "still running after 60 s";
            Debug.Log($"[ContentLink.Tests] T49: bytes served after Play Mode ended {afterExit} · the next session's download {result}");
            yield return new ExitPlayMode();
            Assert.That(afterExit, Is.LessThanOrEqualTo(16 * Chunk), "the session's end stops its requests (a few pieces may be on their way)");
            Assert.That(result, Is.EqualTo("true"), "the next session downloads into the same folder");
        }
    }
}
