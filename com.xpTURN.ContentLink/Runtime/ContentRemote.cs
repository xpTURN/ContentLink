using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace xpTURN.ContentLink
{
    /// <summary>What a <see cref="ContentRemote"/>'s cache folder holds once its download is done.</summary>
    public enum ContentRemoteSource
    {
        None,        // nothing usable: no remote, no complete cache, no built-in copy (Error says why)
        Remote,      // the remote index
        Cache,       // the index committed earlier — the remote was not reachable or was refused (Error says why)
        BuiltIn,     // the build shipped with the app, copied into the cache — the remote was not reachable or was refused
        Simulated,   // the editor serves the profile from the project: nothing is downloaded
    }

    /// <summary>Download progress, polled. The same shape as Addressables' DownloadStatus.</summary>
    public struct ContentDownloadStatus
    {
        public long TotalBytes;
        public long DownloadedBytes;
        public bool IsDone;
        public float Percent => TotalBytes > 0 ? (float)DownloadedBytes / TotalBytes : (IsDone ? 1f : 0f);
    }

    /// <summary>Per call options. Names, meanings and defaults follow Addressables.</summary>
    public sealed class ContentRemoteOptions
    {
        /// <summary>The cache's root; the folder is CacheRoot/&lt;profile&gt;. Null = persistentDataPath/ContentLink.</summary>
        public string CacheRoot;
        /// <summary>A packed build shipped with the app (StreamingAssets): an archive of the same name and MD5 is copied instead of downloaded, and it is the fallback when the remote cannot be used.</summary>
        public string BuiltInDirectory;
        /// <summary>Seconds without a new byte before a request is aborted and retried; 0 = none.</summary>
        public int Timeout;
        /// <summary>Retries per request, for the errors Addressables retries.</summary>
        public int RetryCount;
        /// <summary>0 ~ 128 sets the request's redirect limit; anything else keeps the engine's.</summary>
        public int RedirectLimit = -1;
        /// <summary>A cached archive matches by name and size; true also checks its MD5 (reads every cached archive).</summary>
        public bool VerifyCachedFiles;
        /// <summary>Called for every remote request before it is sent: headers, certificates.</summary>
        public Action<UnityWebRequest> WebRequestOverride;
        /// <summary>Called for every remote URL: signing, cache busting.</summary>
        public Func<string, string> TransformUrl;
    }

    /// <summary>
    /// One profile's packed directory on a server, kept in a cache folder: <see cref="CheckAsync"/> compares the remote index with
    /// the cache, <see cref="DownloadAsync"/> fetches what is missing (verified before it gets its name), and the cache folder is then
    /// registered with <see cref="ContentService.RegisterPackedDirectoryAsync"/> like any packed directory.
    /// </summary>
    public sealed class ContentRemote
    {
        /// <summary>Requests in flight at once, over every ContentRemote — one queue, like Addressables'.</summary>
        public static int MaxConcurrentWebRequests = 3;

        /// <summary>Set by the editor: the output folder of a profile the simulation serves from the project, or null.</summary>
        public static Func<string, string> ProfileFallback;

        public string Profile { get; }
        public string BaseUrl { get; }
        /// <summary>The folder to register once <see cref="DownloadAsync"/> succeeded (the profile's output folder when simulated).</summary>
        public string CacheDirectory { get; private set; }
        public ContentRemoteSource Source { get; private set; }
        /// <summary>Null, or why the remote index was not used / why the download failed.</summary>
        public string Error { get; private set; }
        /// <summary>The content version of what the cache will hold ("" when the build set none).</summary>
        public string ContentVersion => m_Target != null ? m_Target.ContentVersion : "";
        /// <summary>
        /// Network bytes the download receives — an interrupted download counts only its rest, a whole one nothing (only its
        /// check is left); a cancelled download called again counts it again from the cache. It grows during the download when a
        /// file has to come again whole: a server that ignored Range, a file that failed its MD5, a built-in copy that failed.
        /// </summary>
        public long BytesToDownload { get; private set; }

        public ContentDownloadStatus Status
        {
            get
            {
                long got = m_DoneBytes, total = BytesToDownload;
                for (int i = 0; i < m_Active.Count; ++i)
                {
                    var job = m_Active[i];
                    var r = job.Request;
                    if (r == null) continue;
                    got += (long)r.downloadedBytes;
                    if (job.Prefix > 0 && r.responseCode == 200) total += job.Archive.Bytes + job.Prefix;   // the server ignores Range: this body is dropped and the file comes again — counted from the answer on, made part of the total at the end
                }
                if (got > total) got = total;
                return new ContentDownloadStatus { TotalBytes = total, DownloadedBytes = got, IsDone = m_Done };
            }
        }

        public const string PartExtension = ".part";

        sealed class Job
        {
            public ContentArchives.Archive Archive;
            public bool Copy;              // from the built-in folder
            public long Prefix;            // bytes already in the .part when the request started
            public int Attempts;
            public bool NoRange;           // the server answered a Range with the whole file
            public bool FreshRestart;      // the one restart a resumed file that failed its MD5 gets
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Op;
            public ulong LastBytes;
            public double StillSince;
            public int StillFrames;
            public bool TimedOut;
            public bool Reconciled;        // the plan's share was matched to the .part its first start found
        }

        enum Outcome { Done, Retry, Failed }
        enum Begun { Sent, Whole, Unwritable }

        static readonly HashSet<string> s_Busy = new(StringComparer.OrdinalIgnoreCase);
        static int s_Active;
        static readonly List<ContentRemote> s_Running = new();   // downloads in progress: what a session's end aborts

        /// <summary>
        /// Ending Play Mode drops the awaits of a download still running, so its finally never runs (measured): with domain
        /// reload off its requests went on writing and the folder stayed busy. Abort them and forget them — at the end of a
        /// session and, should that not run, at the start of the next.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            EndSession();
            Application.quitting -= EndSession;
            Application.quitting += EndSession;
        }

        static void EndSession()
        {
            foreach (var remote in s_Running) remote.AbortActive();
            s_Running.Clear();
            s_Busy.Clear();
            s_Active = 0;
        }

        readonly ContentRemoteOptions m_Options;
        readonly string m_Folder;   // CacheDirectory, full path
        readonly List<Job> m_Jobs = new();
        readonly List<Job> m_Active = new();
        readonly byte[] m_Buffer = new byte[64 * 1024];
        ContentArchives.Index m_Target, m_BuiltIn;
        byte[] m_TargetBytes;       // the index to commit; null when the cache already holds it
        long m_DoneBytes;
        bool m_Done, m_Ok, m_Called;
        string m_LastError;

        ContentRemote(string profile, string baseUrl, ContentRemoteOptions options)
        {
            Profile = profile;
            BaseUrl = baseUrl ?? "";
            m_Options = options;
            var root = string.IsNullOrEmpty(options.CacheRoot) ? Path.Combine(Application.persistentDataPath, "ContentLink") : options.CacheRoot;
            CacheDirectory = Path.Combine(root, profile);
            m_Folder = Path.GetFullPath(CacheDirectory);
        }

        public static string CacheDirectoryOf(string profile, ContentRemoteOptions options = null)
        {
            CheckProfile(profile, remote: false);
            var root = string.IsNullOrEmpty(options?.CacheRoot) ? Path.Combine(Application.persistentDataPath, "ContentLink") : options.CacheRoot;
            return Path.Combine(root, profile);
        }

        static void CheckProfile(string profile, bool remote)
        {
            if (remote ? !ContentArchives.IsPortableName(profile) : !ContentArchives.IsPlainFileName(profile))
                throw new ArgumentException(remote
                    ? $"[ContentLink] '{profile}' is not a profile name — it names the cache folder and the remote folder of a packed profile: letters, digits, '.', '-' and '_'."
                    : $"[ContentLink] '{profile}' is not a profile name (it names the cache folder).", nameof(profile));
        }

        // ───────────── check ─────────────

        /// <summary>
        /// Compares the remote index of <paramref name="profile"/> at <paramref name="baseUrl"/> with the cache, refusing — before
        /// anything is downloaded — an index this player cannot load. When the remote cannot be used the cache's committed
        /// index, then the built-in copy, takes its place (Source and Error say which and why).
        /// </summary>
        public static async Awaitable<ContentRemote> CheckAsync(string profile, string baseUrl, ContentRemoteOptions options = null, CancellationToken ct = default)
        {
            CheckProfile(profile, remote: true);
            var remote = new ContentRemote(profile, baseUrl, options ?? new ContentRemoteOptions());
            var simulated = ProfileFallback?.Invoke(profile);
            if (simulated != null)
            {
                remote.Source = ContentRemoteSource.Simulated;
                remote.CacheDirectory = simulated;
                remote.m_Done = remote.m_Ok = true;
                return remote;
            }
            await remote.Check(ct);
            return remote;
        }

        async Awaitable Check(CancellationToken ct)
        {
            ContentArchives.Index local = null;
            string localHash = null;
            var localPath = Path.Combine(CacheDirectory, ContentArchives.IndexFile);
            if (File.Exists(localPath))
            {
                byte[] bytes = null;
                try { bytes = File.ReadAllBytes(localPath); }
                catch (Exception e) { Debug.LogError($"[ContentLink] '{localPath}' could not be read — the check goes on without it: {e.Message}"); }
                if (bytes != null)
                {
                    localHash = Md5Hex(bytes);
                    local = ContentArchives.ParseIndex(Encoding.UTF8.GetString(bytes), localPath);
                }
            }

            byte[] builtInBytes = null;
            if (!string.IsNullOrEmpty(m_Options.BuiltInDirectory))
            {
                builtInBytes = await GetLocal(BuiltInUrl(ContentArchives.IndexFile), ct);
                if (builtInBytes != null) m_BuiltIn = ContentArchives.ParseIndex(Encoding.UTF8.GetString(builtInBytes), BuiltInUrl(ContentArchives.IndexFile));
                if (m_BuiltIn != null && m_BuiltIn.Profile != Profile)
                {
                    Debug.LogWarning($"[ContentLink] the built-in folder '{m_Options.BuiltInDirectory}' holds profile '{m_BuiltIn.Profile}', not '{Profile}': not used.");
                    m_BuiltIn = null;
                }
                else if (m_BuiltIn != null && !ContentArchives.CheckCompatible(m_BuiltIn, out var notLoadable))
                {
                    Debug.LogWarning($"[ContentLink] the built-in folder '{m_Options.BuiltInDirectory}' cannot be loaded by this player: {notLoadable}; not used.");
                    m_BuiltIn = null;
                }
            }

            string error = null;
            ContentArchives.Index target = null;
            byte[] targetBytes = null;
            var hashBytes = await GetRemote(ContentArchives.HashFile, ct);
            if (hashBytes == null) error = m_LastError;
            else
            {
                var remoteHash = Encoding.ASCII.GetString(hashBytes).Trim().ToLowerInvariant();
                if (!ContentArchives.IsMd5(remoteHash)) error = $"{ContentArchives.HashFile} does not hold an MD5";
                else if (local != null && remoteHash == localHash)                   // the remote's index is the one committed here
                {
                    if (ContentArchives.CheckCompatible(local, out var why)) target = local;
                    else error = "the remote content cannot be loaded: " + why;     // an app updated to another engine while the server still serves the old build
                }
                else
                {
                    byte[] indexBytes = null;
                    for (int attempt = 0; attempt < 2 && indexBytes == null; ++attempt)
                    {
                        var got = await GetRemote(ContentArchives.IndexFile, ct);
                        if (got == null) { error = m_LastError; break; }
                        if (Md5Hex(got) == remoteHash) indexBytes = got;
                        else error = $"{ContentArchives.IndexFile} does not match {ContentArchives.HashFile} — the server holds two versions";
                    }
                    if (indexBytes != null)
                    {
                        var index = ContentArchives.ParseIndex(Encoding.UTF8.GetString(indexBytes), Url(ContentArchives.IndexFile), out var refused);
                        if (index == null) error = "the remote index was refused: " + refused;
                        else if (index.Profile != Profile) error = $"the remote index is for profile '{index.Profile}', not '{Profile}'";
                        else if (!ContentArchives.CheckCompatible(index, out var why)) error = "the remote content cannot be loaded: " + why;
                        else { target = index; targetBytes = indexBytes; error = null; }
                    }
                }
            }

            if (target != null)
            {
                Source = ContentRemoteSource.Remote;
                await Plan(target, targetBytes, download: true);
                return;
            }
            Error = error;
            if (local != null && ContentArchives.CheckCompatible(local, out _) && await IsComplete(local))
            {
                Source = ContentRemoteSource.Cache;
                m_Target = local;
                return;
            }
            if (m_BuiltIn != null)
            {
                Source = ContentRemoteSource.BuiltIn;
                await Plan(m_BuiltIn, builtInBytes, download: false);
                return;
            }
            Source = ContentRemoteSource.None;
        }

        async Awaitable Plan(ContentArchives.Index target, byte[] bytes, bool download, CancellationToken ct = default)
        {
            m_Target = target;
            m_TargetBytes = bytes;
            long total = 0;
            foreach (var a in target.Archives)
            {
                ct.ThrowIfCancellationRequested();
                if (await IsCached(a)) continue;
                var job = new Job { Archive = a };
                if (m_BuiltIn != null && HasSame(m_BuiltIn, a)) job.Copy = true;
                else if (!download) job.Copy = true;
                else
                {
                    long have = Length(PartPath(a));
                    job.Prefix = have > 0 && have <= a.Bytes ? have : 0;   // a whole .part is only checked
                    total += a.Bytes - job.Prefix;
                }
                m_Jobs.Add(job);
            }
            BytesToDownload = total;
        }

        static bool HasSame(ContentArchives.Index index, ContentArchives.Archive a)
        {
            foreach (var b in index.Archives) if (b.File == a.File && b.Md5 == a.Md5 && b.Bytes == a.Bytes) return true;
            return false;
        }

        async Awaitable<bool> IsCached(ContentArchives.Archive a)
        {
            var path = Path.Combine(CacheDirectory, a.File);
            if (Length(path) != a.Bytes) return false;
            return !m_Options.VerifyCachedFiles || await HashFile(path) == a.Md5;
        }

        async Awaitable<bool> IsComplete(ContentArchives.Index index)
        {
            foreach (var a in index.Archives) if (!await IsCached(a)) return false;
            return true;
        }

        // ───────────── download ─────────────

        /// <summary>
        /// Copies the built-in archives the plan names, downloads the rest (resuming an interrupted .part with a Range request),
        /// checks size and MD5 before a file gets its name, and commits the index when every archive is in the cache.
        /// False (Error says why) leaves the previous commit in place — a file the cache folder cannot take too — and stays
        /// false: check again with CheckAsync before retrying. A cancel keeps the .part files for the next call, which plans
        /// again from the cache. An archive the check counted on that has left the cache since fails it: check again.
        /// Ending Play Mode aborts its requests.
        /// </summary>
        public async Awaitable<bool> DownloadAsync(CancellationToken ct = default)
        {
            if (m_Done) return m_Ok;
            if (Source == ContentRemoteSource.None) { m_Done = true; return false; }
            if (!s_Busy.Add(m_Folder)) { m_Called = true; Error = "another download is working on this cache folder"; Debug.LogError($"[ContentLink] '{CacheDirectory}': {Error}."); return false; }
            s_Running.Add(this);
            try
            {
                if (m_Called) await Replan(ct);   // a cancelled call left archives installed and .part files longer: plan again from the cache
                m_Called = true;
                try { Directory.CreateDirectory(CacheDirectory); }
                catch (Exception e) { return Fail($"the cache folder could not be created: {e.Message}"); }
                var downloads = new Queue<Job>();
                foreach (var job in m_Jobs)
                {
                    if (!job.Copy) { downloads.Enqueue(job); continue; }
                    if (await CopyOne(job, ct)) continue;
                    if (Source != ContentRemoteSource.Remote) return Fail($"the built-in archive '{job.Archive.File}' could not be copied — {m_LastError}");
                    job.Copy = false;                              // the built-in file differs: download it instead
                    BytesToDownload += job.Archive.Bytes;
                    downloads.Enqueue(job);
                }
                bool failed = false;
                while (downloads.Count > 0 || m_Active.Count > 0)
                {
                    if (ct.IsCancellationRequested) { AbortActive(); ct.ThrowIfCancellationRequested(); }
                    while (!failed && downloads.Count > 0 && s_Active < Math.Max(1, MaxConcurrentWebRequests))
                    {
                        var job = downloads.Dequeue();
                        var begun = Start(job);
                        if (begun == Begun.Unwritable) { failed = true; break; }
                        if (begun == Begun.Whole)
                        {
                            var outcome = await Verify(job);
                            if (outcome == Outcome.Retry) downloads.Enqueue(job);
                            else if (outcome == Outcome.Failed) failed = true;
                        }
                    }
                    if (failed && m_Active.Count == 0) break;
                    await Awaitable.NextFrameAsync();
                    double now = Time.realtimeSinceStartupAsDouble;
                    for (int i = m_Active.Count - 1; i >= 0; --i)
                    {
                        var job = m_Active[i];
                        if (!job.Op.isDone)
                        {
                            if (m_Options.Timeout > 0)
                            {
                                if (job.Request.downloadedBytes != job.LastBytes) { job.LastBytes = job.Request.downloadedBytes; job.StillSince = now; job.StillFrames = 0; }
                                else if (++job.StillFrames > 5 && now - job.StillSince >= m_Options.Timeout) { job.TimedOut = true; job.Request.Abort(); }
                            }
                            continue;
                        }
                        m_Active.RemoveAt(i);
                        --s_Active;
                        var result = await Finish(job);
                        if (result == Outcome.Retry && !failed) downloads.Enqueue(job);
                        else if (result == Outcome.Failed) failed = true;
                    }
                }
                if (failed) return Fail(m_LastError);
                foreach (var a in m_Target.Archives)   // the plan is the check's: what left the cache since must not be committed
                    if (Length(Path.Combine(CacheDirectory, a.File)) != a.Bytes) return Fail($"'{a.File}' is not in the cache any more — check again");
                if (!Commit()) return Fail(m_LastError);
                m_Done = m_Ok = true;
                return true;
            }
            finally
            {
                if (m_Active.Count > 0) AbortActive();
                s_Running.Remove(this);
                s_Busy.Remove(m_Folder);
            }
        }

        async Awaitable Replan(CancellationToken ct)
        {
            m_Jobs.Clear();
            m_DoneBytes = 0;
            await Plan(m_Target, m_TargetBytes, download: Source == ContentRemoteSource.Remote, ct);
        }

        bool Fail(string why)
        {
            Error = why;
            m_Done = true;
            Debug.LogError($"[ContentLink] '{Profile}': the download failed — {why}. The cache keeps its previous commit.");
            return false;
        }

        void AbortActive()
        {
            foreach (var job in m_Active)
            {
                var r = job.Request;
                job.Request = null;
                if (r == null) continue;
                r.Abort();
                r.Dispose();
                --s_Active;
            }
            m_Active.Clear();
        }

        /// <summary>Starts the request: Whole when the .part is already whole (only the check is left), Unwritable when the .part cannot be made (m_LastError says why).</summary>
        Begun Start(Job job)
        {
            var part = PartPath(job.Archive);
            long have = Length(part);
            if (have > job.Archive.Bytes || (job.NoRange && have > 0)) { Delete(part); have = 0; }
            if (!job.Reconciled) { job.Reconciled = true; BytesToDownload += job.Prefix - Math.Max(have, 0); }   // the check counted the .part it saw: the one here now decides
            if (have == job.Archive.Bytes) { job.Prefix = have; return Begun.Whole; }
            job.Prefix = have > 0 ? have : 0;
            DownloadHandlerFile handler;
            try { handler = new DownloadHandlerFile(part, job.Prefix > 0) { removeFileOnAbort = false }; }
            catch (Exception e) { m_LastError = $"'{job.Archive.File}': {e.Message}"; return Begun.Unwritable; }   // the handler makes the file at once and throws when it cannot (measured)
            var request = new UnityWebRequest(Url(job.Archive.File), UnityWebRequest.kHttpVerbGET, handler, null);
            if (job.Prefix > 0) request.SetRequestHeader("Range", $"bytes={job.Prefix}-");
            Prepare(request, noCache: false);
            job.Request = request;
            job.TimedOut = false;
            job.LastBytes = 0;
            job.StillFrames = 0;
            job.StillSince = Time.realtimeSinceStartupAsDouble;
            job.Op = request.SendWebRequest();
            m_Active.Add(job);
            ++s_Active;
            return Begun.Sent;
        }

        async Awaitable<Outcome> Finish(Job job)
        {
            var r = job.Request;
            job.Request = null;
            bool ok = r.result == UnityWebRequest.Result.Success;
            bool rangeIgnored = job.Prefix > 0 && r.responseCode == 200;   // the whole body was appended to the prefix (measured) — finished or dropped
            string error = job.TimedOut ? "Request timeout" : r.error;
            long got = (long)r.downloadedBytes;
            r.Dispose();
            var part = PartPath(job.Archive);
            if (rangeIgnored)
            {
                Delete(part);
                m_DoneBytes += got;                                      // received, and dropped: the whole file comes again
                BytesToDownload += got + job.Prefix;                     // what came beyond the request's share, and the file once more
                job.NoRange = true;
                if (ok) return Outcome.Retry;                            // not a failure: the server does not resume
            }
            if (!ok)
            {
                if (!rangeIgnored) m_DoneBytes += got;                    // kept in the .part: the next attempt resumes after them
                m_LastError = $"'{job.Archive.File}': {error}";
                if (job.Attempts < m_Options.RetryCount && (job.TimedOut || ShouldRetry(error))) { ++job.Attempts; return Outcome.Retry; }
                return Outcome.Failed;
            }
            m_DoneBytes += got;
            return await Verify(job);
        }

        async Awaitable<Outcome> Verify(Job job)
        {
            var part = PartPath(job.Archive);
            long length = Length(part);
            string why = null;
            if (length != job.Archive.Bytes) why = $"{length} bytes where the index says {job.Archive.Bytes}";
            else if (await HashFile(part) != job.Archive.Md5) why = "its MD5 differs from the index — the server holds other bytes";
            if (why == null) return Install(job) ? Outcome.Done : Outcome.Failed;
            Delete(part);
            m_LastError = $"'{job.Archive.File}': {why}";
            if (job.Prefix > 0 && !job.FreshRestart) { job.FreshRestart = true; BytesToDownload += job.Archive.Bytes; return Outcome.Retry; }   // the kept prefix may be what is wrong
            if (job.Attempts < m_Options.RetryCount) { ++job.Attempts; BytesToDownload += job.Archive.Bytes; return Outcome.Retry; }   // the file comes again, whole
            return Outcome.Failed;
        }

        async Awaitable<bool> CopyOne(Job job, CancellationToken ct)
        {
            var part = PartPath(job.Archive);
            Delete(part);
            DownloadHandlerFile handler;
            try { handler = new DownloadHandlerFile(part) { removeFileOnAbort = true }; }
            catch (Exception e) { m_LastError = e.Message; return false; }
            var request = new UnityWebRequest(BuiltInUrl(job.Archive.File), UnityWebRequest.kHttpVerbGET, handler, null);
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { request.Abort(); request.Dispose(); ct.ThrowIfCancellationRequested(); }
                await Awaitable.NextFrameAsync();
            }
            bool ok = request.result == UnityWebRequest.Result.Success;
            m_LastError = ok ? "its size or MD5 differs from the index" : request.error;
            request.Dispose();
            if (!ok || Length(part) != job.Archive.Bytes || await HashFile(part) != job.Archive.Md5) { Delete(part); return false; }
            return Install(job);
        }

        /// <summary>The verified .part takes the archive's name — never over a mounted file.</summary>
        bool Install(Job job)
        {
            var part = PartPath(job.Archive);
            var final = Path.Combine(CacheDirectory, job.Archive.File);
            try
            {
                if (File.Exists(final))
                {
                    if (ContentService.IsMountedFile(final)) { m_LastError = $"'{job.Archive.File}' is mounted by a directory that is registered or being registered"; Delete(part); return false; }
                    File.Delete(final);
                }
                File.Move(part, final);
            }
            catch (Exception e) { m_LastError = $"'{job.Archive.File}' could not take its name: {e.Message}"; return false; }   // the verified .part stays for the next call
            NoBackup(final);
            return true;
        }

        bool Commit()
        {
            if (m_TargetBytes == null) return true;   // the cache holds this index already
            var dst = Path.Combine(CacheDirectory, ContentArchives.IndexFile);
            var part = dst + PartExtension;
            try
            {
                File.WriteAllBytes(part, m_TargetBytes);
                if (File.Exists(dst)) File.Replace(part, dst, null);   // File.Move cannot overwrite on this API level (measured)
                else File.Move(part, dst);
            }
            catch (Exception e) { m_LastError = $"{ContentArchives.IndexFile} could not be committed: {e.Message}"; return false; }   // the .part stays: a failed ReplaceFile may have removed the target already
            NoBackup(dst);
            m_TargetBytes = null;
            return true;
        }

        static void NoBackup(string path)
        {
#if UNITY_IOS && !UNITY_EDITOR
            UnityEngine.iOS.Device.SetNoBackupFlag(path);   // per file: the API asks for it each time a file is saved
#endif
        }

        // ───────────── cache ─────────────

        /// <summary>
        /// Deletes the archives and .part files in the cache folder that the committed index does not list — never a file a
        /// directory registered — or being registered — has mounted, never anything else. Returns what it deleted.
        /// </summary>
        public (int Files, long Bytes) CleanCache()
        {
            if (Source == ContentRemoteSource.Simulated) return (0, 0);
            if (s_Busy.Contains(m_Folder)) { Debug.LogWarning($"[ContentLink] '{CacheDirectory}' is downloading; not cleaned."); return (0, 0); }
            var index = ContentArchives.ReadIndex(CacheDirectory);
            if (index == null) return (0, 0);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in index.Archives) keep.Add(a.File);
            int files = 0;
            long bytes = 0;
            foreach (var path in Directory.GetFiles(CacheDirectory))
            {
                var name = Path.GetFileName(path);
                bool ours = name.EndsWith(ContentArchives.ArchiveExtension, StringComparison.Ordinal) || name.EndsWith(PartExtension, StringComparison.Ordinal);
                if (!ours || keep.Contains(name) || ContentService.IsMountedFile(path)) continue;
                long length = Length(path);
                try { File.Delete(path); ++files; bytes += length; }
                catch (Exception e) { Debug.LogWarning($"[ContentLink] '{path}' could not be deleted: {e.Message}"); }
            }
            return (files, bytes);
        }

        /// <summary>Deletes a profile's whole cache folder. Refused while a directory registered from it, or being registered from it, or a download into it is alive.</summary>
        public static bool ClearCache(string profile, ContentRemoteOptions options = null)
        {
            CheckProfile(profile, remote: false);
            var folder = CacheDirectoryOf(profile, options);
            if (ContentService.IsPackedFolderRegistered(folder)) { Debug.LogError($"[ContentLink] '{folder}' is registered or being registered — unregister it before clearing its cache."); return false; }
            if (s_Busy.Contains(Path.GetFullPath(folder))) { Debug.LogError($"[ContentLink] '{folder}' is downloading — not cleared."); return false; }
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            return true;
        }

        // ───────────── requests ─────────────

        string Url(string file)
        {
            var url = BaseUrl.TrimEnd('/') + "/" + file;
            return m_Options.TransformUrl != null ? m_Options.TransformUrl(url) : url;
        }

        string BuiltInUrl(string file)
        {
            var dir = m_Options.BuiltInDirectory;
            if (dir.IndexOf("://", StringComparison.Ordinal) >= 0) return dir.TrimEnd('/') + "/" + file;   // jar:file://… on Android
            return new Uri(Path.GetFullPath(Path.Combine(dir, file))).AbsoluteUri;
        }

        string PartPath(ContentArchives.Archive a) => Path.Combine(CacheDirectory, a.File + PartExtension);

        void Prepare(UnityWebRequest request, bool noCache)
        {
            if (noCache) request.SetRequestHeader("Cache-Control", "no-cache");
            if (m_Options.RedirectLimit >= 0 && m_Options.RedirectLimit <= 128) request.redirectLimit = m_Options.RedirectLimit;
            m_Options.WebRequestOverride?.Invoke(request);
        }

        /// <summary>A small remote file (the hash, the index) into memory, with the options' retries and stall timeout. Null = m_LastError says why.</summary>
        async Awaitable<byte[]> GetRemote(string file, CancellationToken ct)
        {
            for (int attempt = 0; ; ++attempt)
            {
                var request = new UnityWebRequest(Url(file), UnityWebRequest.kHttpVerbGET, new DownloadHandlerBuffer(), null);
                Prepare(request, noCache: true);
                bool timedOut = await Run(request, ct);
                if (request.result == UnityWebRequest.Result.Success)
                {
                    var data = request.downloadHandler.data;
                    request.Dispose();
                    return data;
                }
                string error = timedOut ? "Request timeout" : request.error;
                request.Dispose();
                m_LastError = $"{file}: {error}";
                if (attempt >= m_Options.RetryCount || !(timedOut || ShouldRetry(error))) return null;
            }
        }

        static async Awaitable<byte[]> GetLocal(string url, CancellationToken ct)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET, new DownloadHandlerBuffer(), null);
            var op = request.SendWebRequest();
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { request.Abort(); request.Dispose(); ct.ThrowIfCancellationRequested(); }
                await Awaitable.NextFrameAsync();
            }
            var data = request.result == UnityWebRequest.Result.Success ? request.downloadHandler.data : null;
            request.Dispose();
            return data;
        }

        /// <summary>Sends and waits, aborting when no byte arrived for the options' Timeout; true = it was aborted for that.</summary>
        async Awaitable<bool> Run(UnityWebRequest request, CancellationToken ct)
        {
            var op = request.SendWebRequest();
            ulong last = 0;
            double since = Time.realtimeSinceStartupAsDouble;
            int still = 0;
            while (!op.isDone)
            {
                if (ct.IsCancellationRequested) { request.Abort(); request.Dispose(); ct.ThrowIfCancellationRequested(); }
                await Awaitable.NextFrameAsync();
                if (m_Options.Timeout <= 0 || op.isDone) continue;
                if (request.downloadedBytes != last) { last = request.downloadedBytes; since = Time.realtimeSinceStartupAsDouble; still = 0; }
                else if (++still > 5 && Time.realtimeSinceStartupAsDouble - since >= m_Options.Timeout) { request.Abort(); while (!op.isDone) await Awaitable.NextFrameAsync(); return true; }
            }
            return false;
        }

        /// <summary>Addressables' rule (UnityWebRequestUtilities.ShouldRetryDownloadError): everything is retried but these.</summary>
        static bool ShouldRetry(string error)
        {
            if (string.IsNullOrEmpty(error)) return true;
            switch (error)
            {
                case "Request aborted":
                case "Unable to write data":
                case "Malformed URL":
                case "Out of memory":
                case "Encountered invalid redirect (missing Location header?)":
                case "Cannot modify request at this time":
                case "Unsupported Protocol":
                case "Destination host has an erroneous SSL certificate":
                case "Unable to load SSL Cipher for verification":
                case "SSL CA certificate error":
                case "Unrecognized content-encoding":
                case "Request already transmitted":
                case "Invalid HTTP Method":
                case "Header name contains invalid characters":
                case "Header value contains invalid characters":
                case "Cannot override system-specified headers":
                case "Insecure connection not allowed":
                    return false;
            }
            return true;
        }

        // ───────────── files and hashes ─────────────

        static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : -1;

        static void Delete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) { Debug.LogWarning($"[ContentLink] '{path}' could not be deleted: {e.Message}"); }
        }

        /// <summary>The file's MD5 on a background thread (the main thread keeps its frames); null when it cannot be read.</summary>
        async Awaitable<string> HashFile(string path)
        {
            await Awaitable.BackgroundThreadAsync();
            string hash;
            try { hash = Md5Of(path, m_Buffer); }
            catch (Exception) { hash = null; }
            await Awaitable.MainThreadAsync();
            return hash;
        }

        internal static string Md5Of(string path, byte[] buffer)
        {
            using var md5 = MD5.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) md5.TransformBlock(buffer, 0, n, null, 0);
            md5.TransformFinalBlock(buffer, 0, 0);
            return Hex(md5.Hash);
        }

        internal static string Md5Hex(byte[] bytes)
        {
            using var md5 = MD5.Create();
            return Hex(md5.ComputeHash(bytes));
        }

        static string Hex(byte[] hash)
        {
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
