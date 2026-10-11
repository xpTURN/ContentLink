using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.Content;
using Unity.IO.Archive;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// T57 ~ T60 · T73 · T75: the engine facts the harness stubs copy (Tests~/Verify Stubs.cs — Awaitable.FromAsyncOperation, the
    /// archive mounts, FakeWeb, DownloadHandlerFile, the order of a destroyed object's OnDestroy, AddComponent while it is destroyed). Measured on 6000.6 in the editor and the Mono and IL2CPP players; they hold with the
    /// package unchanged. A red one means the engine changed: the stub has to change with it.
    /// </summary>
    [Category("StubFidelity")]
    public class StubFidelityTests
    {
        static string Temp => Path.Combine(Application.temporaryCachePath, "ContentLinkTests", "StubFidelity");
        static string Packed2 => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", "Packed2");

        [SetUp]
        public void Fresh()
        {
            if (Directory.Exists(Temp)) Directory.Delete(Temp, true);
            Directory.CreateDirectory(Temp);
        }

        [TearDown]
        public void Clean()
        {
            if (Directory.Exists(Temp)) Directory.Delete(Temp, true);
        }

        /// <summary>Waits on the wall clock: the frames of a batch run are a fraction of a millisecond, too short to count on.</summary>
        static async Task Until(Func<bool> done, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < ms) await Awaitable.NextFrameAsync();
        }

        static int ClosedPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Awaits FromAsyncOperation(op, ct) and notes how the wait ended, and whether the operation was done by then.</summary>
        sealed class Waiter
        {
            public string Outcome = "pending";
            public bool DoneAtOutcome;
            public Waiter(AsyncOperation op, CancellationToken ct) => Run(op, ct);

            async void Run(AsyncOperation op, CancellationToken ct)
            {
                try { await Awaitable.FromAsyncOperation(op, ct); Outcome = "completed"; }
                catch (OperationCanceledException) { Outcome = "OperationCanceledException"; }
                catch (Exception e) { Outcome = e.GetType().Name; }
                DoneAtOutcome = op.isDone;
            }
        }

        static string Call(AsyncOperation op, CancellationToken ct)
        {
            try { Awaitable.FromAsyncOperation(op, ct); return "returned"; }
            catch (OperationCanceledException) { return "OperationCanceledException"; }
        }

        /// <summary>
        /// T57: Awaitable.FromAsyncOperation(op, ct). A cancel while a request flies aborts it, and the awaiter gets
        /// OperationCanceledException when the aborted request is done, not at the cancel; a token cancelled before the call throws at the
        /// call itself — the operation in flight or done — and the operation goes on; a Resources load and UnloadUnusedAssets ignore a cancel
        /// while they fly.
        /// </summary>
        [Test, Timeout(60000)]
        public async Task T57_FromAsyncOperation_ARequestIsAborted_OthersIgnoreTheCancel()
        {
            using var server = new RemoteContentTests.LoopbackServer(Temp);
            lock (server.Body) server.Body["slow"] = new byte[1000];
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();

            lock (server.StallOnce) server.StallOnce["slow"] = (0, 400);
            var request = new UnityWebRequest(server.BaseUrl + "/slow", "GET", new DownloadHandlerBuffer(), null);
            var requestOp = request.SendWebRequest();
            var requestCts = new CancellationTokenSource();
            var requestWaiter = new Waiter(requestOp, requestCts.Token);
            await Awaitable.NextFrameAsync();
            bool requestFlying = !requestOp.isDone;
            requestCts.Cancel();
            var atCancel = requestWaiter.Outcome;
            await Until(() => requestWaiter.Outcome != "pending", 5000);
            Assert.That((requestFlying, atCancel, requestWaiter.Outcome, requestWaiter.DoneAtOutcome, request.result, request.error),
                Is.EqualTo((true, "pending", "OperationCanceledException", true, UnityWebRequest.Result.ConnectionError, "Request aborted")),
                "a request cancelled while it flies: flying · the awaiter at the cancel · then · the request done by then · the request");
            request.Dispose();

            lock (server.StallOnce) server.StallOnce["slow"] = (0, 400);
            var early = new UnityWebRequest(server.BaseUrl + "/slow", "GET", new DownloadHandlerBuffer(), null);
            var earlyOp = early.SendWebRequest();
            bool earlyFlying = !earlyOp.isDone;
            var earlyAtCall = Call(earlyOp, cancelled.Token);
            await Until(() => earlyOp.isDone, 5000);
            var doneAtCall = Call(earlyOp, cancelled.Token);
            Assert.That((earlyFlying, earlyAtCall, early.result, doneAtCall), Is.EqualTo((true, "OperationCanceledException", UnityWebRequest.Result.Success, "OperationCanceledException")),
                "a token cancelled before the call: flying · the call itself · the request · the call on the request done");
            early.Dispose();

            var load = Resources.LoadAsync("ContentLinkTests/hero");
            var loadCts = new CancellationTokenSource();
            var loadWaiter = new Waiter(load, loadCts.Token);
            bool loadFlying = !load.isDone;
            loadCts.Cancel();
            await Until(() => loadWaiter.Outcome != "pending" && load.isDone, 5000);
            Assert.That((loadFlying, loadWaiter.Outcome, load.asset != null), Is.EqualTo((true, "completed", true)),
                "a Resources load cancelled while it flies: flying · the awaiter · the asset");

            var earlyLoad = Resources.LoadAsync("ContentLinkTests/sheet");
            bool earlyLoadFlying = !earlyLoad.isDone;
            var earlyLoadAtCall = Call(earlyLoad, cancelled.Token);
            await Until(() => earlyLoad.isDone, 5000);
            Assert.That((earlyLoadFlying, earlyLoadAtCall, earlyLoad.asset != null), Is.EqualTo((true, "OperationCanceledException", true)),
                "a Resources load with a token cancelled before the call: flying · the call itself · the asset");

            var unload = Resources.UnloadUnusedAssets();
            var unloadCts = new CancellationTokenSource();
            var unloadWaiter = new Waiter(unload, unloadCts.Token);
            bool unloadFlying = !unload.isDone;
            unloadCts.Cancel();
            await Until(() => unloadWaiter.Outcome != "pending" && unload.isDone, 5000);
            Assert.That((unloadFlying, unloadWaiter.Outcome), Is.EqualTo((true, "completed")),
                "UnloadUnusedAssets cancelled while it flies: flying · the awaiter");
        }

        /// <summary>
        /// T58: archives mount one after another in the order they were started — in every frame the ones done are the first ones
        /// started — in one line across namespaces, and a missing file says Failed only after the ones before it are Complete. The engine
        /// mounts on its own thread and may have all of them done before the first look, which shows no order: such a round is run again.
        /// </summary>
        [Test, Timeout(120000)]
        public async Task T58_MountsEndInTheOrderTheyStarted_AFailureAfterTheOnesBefore()
        {
            if (!File.Exists(Path.Combine(Packed2, ContentArchives.HashFile)))
                Assert.Ignore("Runner/Packed2 (index v2) is missing — run the EditMode test ContentArchivePackerTests.K2 first");
            var files = Directory.GetFiles(Packed2, "*.archive");
            Array.Sort(files, StringComparer.Ordinal);
            var paths = files.Take(files.Length / 2).Append(Path.Combine(Temp, "missing1.archive"))
                             .Concat(files.Skip(files.Length / 2)).Append(Path.Combine(Temp, "missing2.archive")).ToArray();
            var end = new string(paths.Select(p => File.Exists(p) ? 'C' : 'F').ToArray());
            var a = ContentNamespace.GetOrCreateNamespace("t58a");
            var b = ContentNamespace.GetOrCreateNamespace("t58b");
            var inconclusive = new List<string>();
            foreach (bool twoNamespaces in new[] { false, true })
            {
                var label = twoNamespaces ? "two namespaces, the first half in one" : "one namespace";
                var seen = new List<string>();
                bool ordered = false;
                for (int attempt = 0; attempt < 5 && !ordered; ++attempt)
                {
                    LogAssert.ignoreFailingMessages = true;   // a player logs an error for a missing file at the call
                    var handles = paths.Select((p, i) => ArchiveFileInterface.MountAsync(twoNamespaces && i >= paths.Length / 2 ? b : a, p, "t58")).ToArray();
                    var rows = new List<string>();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        var row = Row(handles);
                        if (rows.Count == 0 || row != rows[rows.Count - 1]) rows.Add(row);
                        if (row.IndexOf('.') < 0 || sw.ElapsedMilliseconds > 10000) break;
                        await Awaitable.NextFrameAsync();
                    }
                    foreach (var h in handles) h.Unmount().Complete();
                    await Awaitable.NextFrameAsync();
                    LogAssert.ignoreFailingMessages = false;
                    seen.Add(string.Join(" ", rows));
                    Assert.That(rows.All(r => Regex.IsMatch(r, @"^[CF]*\.*$")) && rows[rows.Count - 1] == end, Is.True,
                        $"{label}: the ones done are always the first ones started, and they end {end} — {string.Join(" ", rows)}");
                    ordered = rows.Any(r => r.IndexOf('.') > 0);   // some done, some not: the order was seen
                }
                Debug.Log($"[StubFidelity] T58 {label}: {string.Join(" | ", seen)}");
                if (!ordered) inconclusive.Add($"{label}: {string.Join(" | ", seen)}");
            }
            if (inconclusive.Count > 0) Assert.Inconclusive("every mount was done before the first look, five times — no order seen: " + string.Join(" · ", inconclusive));
        }

        /// <summary>Each handle's status, read from the last to the first: the engine mounts on its own thread, so a later read could see one done that an earlier read saw in progress.</summary>
        static string Row(ArchiveHandle[] handles)
        {
            var row = new char[handles.Length];
            for (int i = handles.Length - 1; i >= 0; --i)
            {
                var status = handles[i].Status;
                row[i] = status == ArchiveStatus.Complete ? 'C' : status == ArchiveStatus.Failed ? 'F' : '.';
            }
            return new string(row);
        }

        /// <summary>
        /// T59: a request is never done inside SendWebRequest, Abort or Dispose — an error (404, file:// missing) is known in a later
        /// frame, and so are an abort and a dispose while it flies; completed does not run inside the call. A request cut in the frame of its
        /// send ends "Request aborted", whatever the server would have answered. A refused loopback connection is left out: the engine's own
        /// thread can have it done by the time SendWebRequest returns (IL2CPP, once).
        /// </summary>
        [Test, Timeout(60000)]
        public async Task T59_ARequestIsNeverDoneInsideSendAbortOrDispose()
        {
            using var server = new RemoteContentTests.LoopbackServer(Temp);
            var errors = new[]
            {
                ("404", server.BaseUrl + "/none", UnityWebRequest.Result.ProtocolError),
                ("file:// missing", "file://" + Path.Combine(Temp, "none.bin"), UnityWebRequest.Result.ProtocolError),
            };
            var wrong = new List<string>();
            foreach (bool abortAtSend in new[] { false, true })
            foreach (var (label, url, result) in errors)
            {
                var r = new UnityWebRequest(url, "GET", new DownloadHandlerBuffer(), null);
                bool inCall = true, ranInCall = false;
                var op = r.SendWebRequest();
                op.completed += _ => ranInCall = inCall;
                if (abortAtSend) r.Abort();
                bool doneInCall = op.isDone;
                inCall = false;
                await Until(() => op.isDone, 10000);
                var expected = abortAtSend ? (UnityWebRequest.Result.ConnectionError, "Request aborted") : (result, r.error);
                if (doneInCall || ranInCall || !op.isDone || (r.result, r.error) != expected)
                    wrong.Add($"{label}{(abortAtSend ? ", aborted at the send" : "")} (in the call {doneInCall}/{ranInCall} · then {op.isDone} {r.result} '{r.error}')");
                r.Dispose();
            }
            lock (server.Body) server.Body["slow"] = new byte[1000];
            foreach (bool dispose in new[] { false, true })
            {
                lock (server.StallOnce) server.StallOnce["slow"] = (0, 400);
                var r = new UnityWebRequest(server.BaseUrl + "/slow", "GET", new DownloadHandlerBuffer(), null);
                var op = r.SendWebRequest();
                await Awaitable.NextFrameAsync();
                bool flying = !op.isDone, inCall = true, ranInCall = false, fired = false;
                op.completed += _ => { fired = true; ranInCall = inCall; };
                if (dispose) r.Dispose(); else r.Abort();
                bool doneInCall = op.isDone;
                inCall = false;
                await Until(() => fired, 5000);
                if (!flying || doneInCall || ranInCall || !fired || !op.isDone || (!dispose && r.error != "Request aborted"))
                    wrong.Add($"{(dispose ? "Dispose" : "Abort")} (flying {flying} · in the call {doneInCall}/{ranInCall} · then {fired}/{op.isDone}{(dispose ? "" : $" '{r.error}'")})");
                if (!dispose) r.Dispose();
            }
            Assert.That(wrong, Is.Empty, "done inside the call, or not as measured");
        }

        /// <summary>
        /// T59b: removeFileOnAbort deletes the handler's file on any failure — at the Abort or the Dispose itself (a request cut
        /// in the frame of its send too), not when the request is done, so a new handler and request on the path right after
        /// (ContentRemote.CopyOne called again) keep their file; and when an error ends the request. Without it the received bytes stay.
        /// </summary>
        [Test, Timeout(60000)]
        public async Task T59b_RemoveFileOnAbort_DeletesOnAnyFailure_AtTheCallForAnAbort()
        {
            using var server = new RemoteContentTests.LoopbackServer(Temp);
            lock (server.Body) { server.Body["slow"] = new byte[200000]; server.Body["ok"] = new byte[1000]; server.Body["drop"] = new byte[200000]; }
            var part = Path.Combine(Temp, "x.part");
            var wrong = new List<string>();
            foreach (var (label, abort, dispose, remove) in new[] { ("Abort", true, false, true), ("Dispose", false, true, true), ("Abort + Dispose", true, true, true), ("Abort, removeFileOnAbort off", true, false, false) })
            {
                lock (server.StallOnce) server.StallOnce["slow"] = (1000, 400);
                var first = new UnityWebRequest(server.BaseUrl + "/slow", "GET", new DownloadHandlerFile(part) { removeFileOnAbort = remove }, null);
                var firstOp = first.SendWebRequest();
                bool firstDone = false;
                firstOp.completed += _ => firstDone = true;
                await Until(() => first.downloadedBytes >= 1000, 5000);
                bool flying = !firstOp.isDone;
                if (abort) first.Abort();
                if (dispose) first.Dispose();
                bool atCall = File.Exists(part);
                if (remove)
                {
                    var retry = new UnityWebRequest(server.BaseUrl + "/ok", "GET", new DownloadHandlerFile(part) { removeFileOnAbort = true }, null);
                    var retryOp = retry.SendWebRequest();
                    await Until(() => firstDone && retryOp.isDone, 5000);
                    if (!dispose) first.Dispose();
                    await Until(() => false, 200);
                    long bytes = File.Exists(part) ? new FileInfo(part).Length : -1;
                    if (!flying || atCall || !firstDone || retry.result != UnityWebRequest.Result.Success || bytes != 1000)
                        wrong.Add($"{label} (flying {flying} · the file at the call {atCall} · the first done {firstDone} · the retry {retry.result}, its file 200 ms after {bytes} B)");
                    retry.Dispose();
                }
                else
                {
                    await Until(() => firstDone, 5000);
                    first.Dispose();
                    long bytes = File.Exists(part) ? new FileInfo(part).Length : -1;
                    if (!flying || !atCall || !firstDone || bytes < 1000)
                        wrong.Add($"{label} (flying {flying} · the file at the call {atCall} · the first done {firstDone} · the file after {bytes} B)");
                }
                if (File.Exists(part)) File.Delete(part);
            }
            int closed = ClosedPort();
            foreach (var (label, url) in new[] { ("404", server.BaseUrl + "/none"), ("a refused connection", $"http://127.0.0.1:{closed}/x"), ("file:// missing", "file://" + Path.Combine(Temp, "none.bin")) })
            {
                var r = new UnityWebRequest(url, "GET", new DownloadHandlerFile(part) { removeFileOnAbort = true }, null);
                var op = r.SendWebRequest();
                r.Abort();
                bool atCall = File.Exists(part);
                await Until(() => op.isDone, 5000);
                if (atCall || File.Exists(part)) wrong.Add($"{label}, aborted at the send (the file at the call {atCall} · after {File.Exists(part)})");
                r.Dispose();
                if (File.Exists(part)) File.Delete(part);
            }
            foreach (bool remove in new[] { false, true })
            foreach (var (label, url) in new[] { ("404", server.BaseUrl + "/none"), ("a drop after 1000 bytes", server.BaseUrl + "/drop") })
            {
                lock (server.CutOnce) server.CutOnce["drop"] = 1000;
                var r = new UnityWebRequest(url, "GET", new DownloadHandlerFile(part) { removeFileOnAbort = remove }, null);
                var op = r.SendWebRequest();
                await Until(() => op.isDone, 5000);
                bool there = File.Exists(part);
                if (r.result == UnityWebRequest.Result.Success || there == remove)
                    wrong.Add($"{label} ending the request, removeFileOnAbort {remove} ({r.result} '{r.error}' · the file there {there})");
                r.Dispose();
                if (File.Exists(part)) File.Delete(part);
            }
            Assert.That(wrong, Is.Empty, "removeFileOnAbort deletes on any failure — at the call for an abort — and a retry on the path keeps its file");
        }

        /// <summary>
        /// T60: CheckAsync with a token cancelled before the call throws OperationCanceledException whatever the server answers —
        /// a missing built-in folder (read first: the server is not asked), nothing served (404), the hash served (200). The harness's N3c pins the same.
        /// </summary>
        [Test, Timeout(60000)]
        public async Task T60_ACheckWithACancelledToken_ThrowsWhateverTheServerAnswers()
        {
            using var server = new RemoteContentTests.LoopbackServer(Temp);
            var cache = Path.Combine(Temp, "cache");
            var cts = new CancellationTokenSource(); cts.Cancel();
            async Task<string> Check(ContentRemoteOptions options)
            {
                try { var r = await ContentRemote.CheckAsync("StubFidelity", server.BaseUrl, options, cts.Token); return $"returned ({r.Source} '{r.Error}')"; }
                catch (OperationCanceledException) { return "OperationCanceledException"; }
            }
            var noBuiltIn = await Check(new ContentRemoteOptions { CacheRoot = cache, BuiltInDirectory = Path.Combine(Temp, "no-built-in") });
            await Until(() => false, 200);
            int asked = server.Count("archives.hash");
            var nothing = await Check(new ContentRemoteOptions { CacheRoot = cache });
            lock (server.Body) server.Body["archives.hash"] = Encoding.ASCII.GetBytes("0123456789abcdef0123456789abcdef\n");
            var served = await Check(new ContentRemoteOptions { CacheRoot = cache });
            Assert.That((noBuiltIn, asked, nothing, served), Is.EqualTo(("OperationCanceledException", 0, "OperationCanceledException", "OperationCanceledException")),
                "a missing built-in folder · the server asked by then · nothing served (404) · the hash served (200)");
        }

        /// <summary>
        /// T73: a destroyed object's components become null only after all their OnDestroy have run — a sibling's
        /// OnDestroy, which runs after the owner's, still sees the owner. The stub's Destroy does the same.
        /// </summary>
        [Test, Timeout(10000)]
        public async Task T73_ASiblingsOnDestroy_StillSeesTheOwner()
        {
            var go = new GameObject("T73");
            go.Scope();
            var hook = go.AddComponent<DestroyHook>();
            bool ownerSeen = false;
            hook.whenDestroyed = () => ownerSeen = go.GetComponent<ContentScopeOwner>() != null;
            UnityEngine.Object.Destroy(go);
            await Awaitable.NextFrameAsync();
            Assert.That(ownerSeen, Is.True, "the owner's OnDestroy ran before, yet it is not null in a sibling's");
            Assert.That(go == null, Is.True, "null once every OnDestroy has run");
        }

        /// <summary>
        /// T75: an object being destroyed adds no component — AddComponent answers null, logs nothing, and
        /// the component never wakes. The stub's AddComponent does the same.
        /// </summary>
        [Test, Timeout(10000)]
        public async Task T75_AnObjectBeingDestroyed_AddsNoComponent()
        {
            var go = new GameObject("T75");
            var hook = go.AddComponent<DestroyHook>();
            bool asked = false; DestroyHook added = null;
            hook.whenDestroyed = () => { asked = true; added = go.AddComponent<DestroyHook>(); };   // a second hook: the component the ⓪ commit has
            UnityEngine.Object.Destroy(go);
            await Awaitable.NextFrameAsync();
            Assert.That(asked, Is.True, "the sibling's OnDestroy ran");
            Assert.That(ReferenceEquals(added, null), Is.True, "AddComponent on an object being destroyed answers null");
        }
    }
}
