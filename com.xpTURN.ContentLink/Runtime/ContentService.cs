using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Unity.Loading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink
{
    public enum ContentStatus : byte { None, Loading, Loaded, Failed }

    /// <summary>
    /// Reference counting and deterministic release on top of Unity 6.6 content directories. Main thread only.
    ///
    ///   Slot  : one per asset key. Owns exactly one Loadable&lt;Object&gt;, so the engine reference is always 0 or 1.
    ///   Lease : one per holder. Unit of reference counting.
    ///   Release: when a slot reaches zero leases it is queued, and Loadable.Release() is called only in Collect()
    ///            (end of frame, PostLateUpdate, by default).
    /// </summary>
    public static class ContentService
    {
        // ───────────── settings ─────────────

        /// <summary>Call Collect() automatically at the end of every frame.</summary>
        public static bool AutoCollect = true;

        /// <summary>Frames to keep a zero-reference asset before releasing it (absorbs release→reacquire flicker).</summary>
        public static int ReleaseDelayFrames = 0;

        /// <summary>Record stack traces for leases and explicit scopes. Development builds only.</summary>
        public static bool CaptureStackTrace = false;

        // ───────────── directories / catalogs ─────────────

        sealed class DirectoryRecord
        {
            public ContentDirectoryHandle Handle;
            public string Path;
            public ContentCatalog[] Catalogs;
            public bool Alive;
            public Unity.IO.Archive.ArchiveHandle[] Archives;   // set for a packed directory: unmounted when it is unregistered
            // Served from the project by the editor instead of the engine — no handle, no catalogs.
            public bool Simulated;
            public string Profile;
            public string SourceRoot;
            public string[] Covered;
        }

        struct NameEntry { public LoadableObjectId Id; public int Dir; }

        /// <summary>The directory index of a slot that belongs to no registered directory.</summary>
        public const int NoDirectory = -1;
        const int kDirUnknown = NoDirectory;

        static readonly List<DirectoryRecord> s_Dirs = new();
        static readonly Dictionary<string, NameEntry> s_Names = new(StringComparer.Ordinal);
        static readonly Dictionary<string, int> s_DirById = new(StringComparer.Ordinal);
        static readonly Dictionary<string, int> s_SimDirById = new(StringComparer.Ordinal);   // ids the simulation answered → their simulated directory

        /// <summary>
        /// Registers a content directory and indexes its ContentCatalog root assets.
        /// When the same name exists in several directories the most recently registered one wins,
        /// matching the engine's Loadable resolution order.
        /// </summary>
        public static int RegisterDirectory(string path)
        {
            var simulated = DirectoryFallback?.Invoke(path);
            if (simulated != null) return RegisterSimulated(path, simulated);

            var handle = ContentLoadManager.RegisterContentDirectory(path);
            var rec = new DirectoryRecord
            {
                Handle = handle,
                Path = path,
                Catalogs = ContentLoadManager.GetRootAssets<ContentCatalog>(handle) ?? Array.Empty<ContentCatalog>(),
                Alive = true,
            };
            s_Dirs.Add(rec);
            if (rec.Catalogs.Length == 0)
                Debug.LogWarning($"[ContentLink] '{path}' has no ContentCatalog root asset.");
            RebuildNameIndex();
            return s_Dirs.Count - 1;
        }

        /// <summary>
        /// A directory the editor serves from the project: nothing is registered with the engine, the
        /// folder need not exist, and the names come from the editor's scan of the profile's source root rather than
        /// from a catalog. Registering the same path twice is refused the way the engine refuses it.
        /// </summary>
        static int RegisterSimulated(string path, SimulatedDirectory simulated)
        {
            var full = NormalizePath(path);
            for (int d = 0; d < s_Dirs.Count; ++d)
                if (s_Dirs[d].Alive && string.Equals(NormalizePath(s_Dirs[d].Path), full, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"[ContentLink] '{path}' is already registered (directory {d}).");

            var rec = new DirectoryRecord
            {
                Path = path,
                Catalogs = Array.Empty<ContentCatalog>(),
                Alive = true,
                Simulated = true,
                Profile = simulated.Profile ?? "",
                SourceRoot = simulated.SourceRoot ?? "",
                Covered = simulated.Covered ?? Array.Empty<string>(),
            };
            s_Dirs.Add(rec);
            RebuildNameIndex();
            return s_Dirs.Count - 1;
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/'); }
            catch (Exception) { return path.Replace('\\', '/').TrimEnd('/'); }
        }

        /// <summary>
        /// Registers a directory the editor packed into archives: mounts every archive of archives.json
        /// under one namespace, then registers the mount path like any flat directory. Unregistering unmounts again.
        /// Returns -1 (logged) when there is no index, it lists no archives, or an archive's size on disk differs from
        /// the index; throws when an archive does not mount or the engine refuses.
        /// </summary>
        public static async Awaitable<int> RegisterPackedDirectoryAsync(string directory)
        {
            var simulated = DirectoryFallback?.Invoke(directory);
            if (simulated != null) return RegisterSimulated(directory, simulated);   // served from the project: nothing to mount

            var index = ContentArchives.ReadIndex(directory);
            if (index == null)
            {
                Debug.LogError($"[ContentLink] '{directory}' has no {ContentArchives.IndexFile}; use RegisterDirectory for a flat or engine-archive build.");
                return -1;
            }
            if (index.Archives.Length == 0)
            {
                Debug.LogError($"[ContentLink] '{directory}' has an {ContentArchives.IndexFile} that lists no archives; nothing to mount.");
                return -1;
            }
            for (int i = 0; i < index.Archives.Length; ++i)   // a truncated or replaced archive is refused before anything is mounted (the engine mounts a truncated one without complaint)
            {
                var a = index.Archives[i];
                var file = System.IO.Path.Combine(directory, a.File);
                long onDisk = System.IO.File.Exists(file) ? new System.IO.FileInfo(file).Length : -1;
                if (a.Bytes > 0 && onDisk != a.Bytes)
                {
                    Debug.LogError($"[ContentLink] '{directory}': archive '{a.File}' is {onDisk} bytes on disk but the index says {a.Bytes} — truncated or replaced; nothing mounted.");
                    return -1;
                }
            }
            var handles = await ContentArchives.MountAsync(directory, index);
            int dir;
            try { dir = RegisterDirectory(handles[0].GetMountPath()); }
            catch
            {
                ContentArchives.Unmount(handles);
                throw;
            }
            s_Dirs[dir].Archives = handles;
            return dir;
        }

        /// <summary>
        /// Unregisters a directory. The engine requires every Loadable from it to be released first, so this
        /// refuses while live assets remain. force=true invalidates the remaining leases (still refused while loading).
        /// </summary>
        public static bool UnregisterDirectory(int dir, bool force = false)
        {
            if ((uint)dir >= (uint)s_Dirs.Count || !s_Dirs[dir].Alive) return false;
            var rec = s_Dirs[dir];

            Collect(ignoreDelay: true);

            StringBuilder scenes = null;
            foreach (var ls in s_LoadedScenes)
                if (s_DirsBySceneId.TryGetValue(ls.Id, out var dirs) && dirs.Contains(dir))
                    (scenes ??= new StringBuilder($"[ContentLink] '{rec.Path}' still has loaded scene(s) — unload them first:")).Append("\n  ").Append(ls.Key);
            if (scenes != null) { Debug.LogError(scenes.ToString()); return false; }   // cannot be forced: unloading is asynchronous

            var blocking = new List<int>();
            for (int i = 0; i < s_Slots.Count; ++i)
            {
                var s = s_Slots[i];
                if (s.Alive && (s.Dir == dir || (s.Dir == kDirUnknown && !s.Simulated))) blocking.Add(i);   // a slot the editor serves from the project belongs to no engine directory
            }

            if (blocking.Count > 0)
            {
                var sb = new StringBuilder($"[ContentLink] '{rec.Path}' still has {blocking.Count} live asset(s):");
                bool anyLoading = false;
                foreach (var i in blocking)
                {
                    sb.Append($"\n  {s_Slots[i].Key} refs={s_Slots[i].Refs} status={s_Slots[i].Status}");
                    anyLoading |= s_Slots[i].Status == ContentStatus.Loading;
                }
                if (!force || anyLoading) { Debug.LogError(sb.ToString()); return false; }
                Debug.LogWarning(sb.Append("\n  -> force released").ToString());
                foreach (var i in blocking) ForceFreeSlot(i);
            }

            if (!rec.Simulated) ContentLoadManager.UnregisterContentDirectory(rec.Handle);   // a simulated directory was never registered with the engine
            rec.Alive = false;
            rec.Catalogs = Array.Empty<ContentCatalog>();
            if (rec.Archives != null) { ContentArchives.Unmount(rec.Archives); rec.Archives = null; }
            var stale = new List<string>();
            foreach (var kv in s_SimDirById) if (kv.Value == dir) stale.Add(kv.Key);
            foreach (var key in stale) s_SimDirById.Remove(key);
            RebuildNameIndex();
            return true;
        }

        // ───────────── editor simulation seam ─────────────

        /// <summary>
        /// Set by the editor to resolve a name no registered catalog carries; an invalid id means "not mine".
        /// Null in a player, so the whole cost there is one null check on a path that was about to fail anyway.
        /// Deliberately NOT cleared by <see cref="ResetStatics"/>: that runs on every Play entry while the installer
        /// is an [InitializeOnLoadMethod] that only runs on a domain load — with domain reload disabled, clearing it
        /// would kill the simulation for the rest of the editor session.
        /// </summary>
        public static Func<string, SimulatedAsset> NameFallback;

        /// <summary>
        /// The editor's answer for a name no registered catalog carries: the id to load, and the simulated directory
        /// it belongs to — <see cref="NoDirectory"/> when it was resolved under a real directory's root.
        /// A default value means "not mine".
        /// </summary>
        public readonly struct SimulatedAsset
        {
            public readonly LoadableObjectId Id;
            public readonly int Dir;
            public SimulatedAsset(LoadableObjectId id, int dir = NoDirectory) { Id = id; Dir = dir; }
        }

        /// <summary>What the editor answers when a registration should be served from the project instead of the engine.</summary>
        public sealed class SimulatedDirectory
        {
            public string Profile = "";
            public string SourceRoot = "";
            /// <summary>Folders whose files the directory would have held: the profile's categories as absolute or project-relative folder paths.</summary>
            public string[] Covered = Array.Empty<string>();
        }

        /// <summary>
        /// Set by the editor to decide, at registration, whether a directory path is one the project can stand in for.
        /// Null in a player. Not cleared by <see cref="ResetStatics"/>, like <see cref="NameFallback"/>.
        /// </summary>
        public static Func<string, SimulatedDirectory> DirectoryFallback;

        /// <summary>
        /// The simulated directory that covers an asset under a source root: the most recently registered
        /// one wins, as the engine's rule for real directories does. False when none does.
        /// </summary>
        public static bool TryGetSimulatedDir(string sourceRoot, string assetPath, out int dir)
        {
            dir = NoDirectory;
            if (string.IsNullOrEmpty(sourceRoot) || string.IsNullOrEmpty(assetPath)) return false;
            var root = NormalizePath(sourceRoot);
            var path = NormalizePath(assetPath);
            for (int d = s_Dirs.Count - 1; d >= 0; --d)
            {
                var rec = s_Dirs[d];
                if (!rec.Alive || !rec.Simulated || !string.Equals(NormalizePath(rec.SourceRoot), root, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var folder in rec.Covered)
                {
                    var covered = NormalizePath(folder);
                    if (string.Equals(path, covered, StringComparison.OrdinalIgnoreCase) || path.StartsWith(covered + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        dir = d;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>True while a directory served from the project is registered — the editor's simulation toggle defers to it.</summary>
        public static bool HasSimulatedDirectories
        {
            get
            {
                for (int d = 0; d < s_Dirs.Count; ++d) if (s_Dirs[d].Alive && s_Dirs[d].Simulated) return true;
                return false;
            }
        }

        /// <summary>"'Assets/Content' (profile Main), …" for the live simulated directories, or null when there are none.</summary>
        static string SimulatedRootsDescription()
        {
            StringBuilder sb = null;
            for (int d = 0; d < s_Dirs.Count; ++d)
            {
                var rec = s_Dirs[d];
                if (!rec.Alive || !rec.Simulated) continue;
                (sb ??= new StringBuilder()).Append(sb.Length > 0 ? ", " : "").Append('\'').Append(rec.SourceRoot).Append("' (profile ").Append(rec.Profile).Append(')');
            }
            return sb?.ToString();
        }

        /// <summary>
        /// True when a registered directory carries a catalog that was built from <paramref name="sourceRoot"/>.
        /// The editor simulation asks by string on purpose: what registration holds are the catalog
        /// copies inside the built directory, never the project assets the editor scanned, so comparing objects
        /// would always say no.
        /// </summary>
        public static bool IsSourceRootRegistered(string sourceRoot)
        {
            if (string.IsNullOrEmpty(sourceRoot)) return false;
            for (int d = 0; d < s_Dirs.Count; ++d)
            {
                var rec = s_Dirs[d];
                if (!rec.Alive) continue;
                foreach (var catalog in rec.Catalogs)
                    if (catalog != null && string.Equals(catalog.SourceRoot, sourceRoot, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when the name can be loaded: a registered catalog carries it, or the editor simulation can resolve it.
        /// The simulation branch runs only in the editor and may do an asset search the first time it sees a name —
        /// RuntimeKeyIsValid would otherwise answer false for an asset the very next load succeeds on.
        /// </summary>
        public static bool Contains(string name)
        {
            if (name == null) return false;
            if (s_Names.ContainsKey(name)) return true;
            var fallback = NameFallback;
            return fallback != null && fallback(name).Id.IsValid;
        }

        /// <summary>Resolves a catalog name to its LoadableObjectId. False if no registered directory has the name.</summary>
        public static bool TryGetLoadableObjectId(string name, out LoadableObjectId id)
        {
            if (name != null && s_Names.TryGetValue(name, out var e)) { id = e.Id; return true; }
            var fallback = NameFallback;
            if (fallback != null && !string.IsNullOrEmpty(name))
            {
                var simulated = fallback(name);
                if (simulated.Id.IsValid)
                {
                    id = simulated.Id;
                    s_SimDirById[IdKey(id)] = simulated.Dir;   // an id-based load later still knows it is simulated, and its directory (NoDirectory included)
                    return true;
                }
            }
            id = default;
            return false;
        }

        static void RebuildNameIndex()
        {
            s_Names.Clear();
            s_DirById.Clear();
            s_SceneNames.Clear();
            s_DirsBySceneId.Clear();
            s_SceneKeyById.Clear();
            for (int d = 0; d < s_Dirs.Count; ++d)
            {
                var rec = s_Dirs[d];
                if (!rec.Alive) continue;
                foreach (var cat in rec.Catalogs)
                {
                    if (cat == null) continue;
                    foreach (var e in cat.Entries)
                    {
                        s_DirById[IdKey(e.id)] = d;
                        if (!string.IsNullOrEmpty(e.name))
                            s_Names[e.name] = new NameEntry { Id = e.id, Dir = d };
                    }
                    foreach (var e in cat.Scenes)
                    {
                        if (!e.id.IsValid) continue;
                        if (!s_DirsBySceneId.TryGetValue(e.id, out var dirs)) s_DirsBySceneId[e.id] = dirs = new List<int>(1);
                        if (!dirs.Contains(d)) dirs.Add(d);
                        if (string.IsNullOrEmpty(e.name)) continue;
                        s_SceneNames[e.name] = new SceneNameEntry { Id = e.id, Dir = d };
                        s_SceneKeyById[e.id] = e.name;
                    }
                }
            }
        }

        // ───────────── slots ─────────────

        sealed class Slot
        {
            public string Key;
            public int Dir;
            public Loadable<Object> Loadable;
            public ContentStatus Status;
            public int Refs;
            public bool Pending;
            public int ZeroFrame;
            public uint Serial;
            public bool Alive;
            public bool Simulated;   // resolved by the editor simulation, not by a catalog
            public List<AwaitableCompletionSource<bool>> Waiters;
        }

        static readonly List<Slot> s_Slots = new();
        static readonly Stack<int> s_FreeSlots = new();
        static readonly Dictionary<string, int> s_SlotByKey = new(StringComparer.Ordinal);
        static readonly List<int> s_Pending = new();

        public static int LiveAssetCount => s_SlotByKey.Count;

        static int AllocSlot(string key, int dir, Loadable<Object> loadable)
        {
            int si;
            if (s_FreeSlots.Count > 0) si = s_FreeSlots.Pop();
            else { si = s_Slots.Count; s_Slots.Add(new Slot()); }

            var s = s_Slots[si];
            s.Key = key;
            s.Dir = dir;
            s.Loadable = loadable;
            s.Status = ContentStatus.None;
            s.Refs = 0;
            s.Pending = false;
            s.Alive = true;
            s.Simulated = false;
            s_SlotByKey[key] = si;
            return si;
        }

        static bool TryGetSlotByName(string name, out int si)
        {
            if (name != null && s_SlotByKey.TryGetValue(name, out si)) return true;
            if (!string.IsNullOrEmpty(name) && s_Names.TryGetValue(name, out var e))
            {
                si = AllocSlot(name, e.Dir, new Loadable<Object>(e.Id));
                return true;
            }

            // The catalogs of real directories come first; a simulated directory has none, so its names
            // only ever arrive through the fallback, which says which simulated directory answered.
            var fallback = NameFallback;
            if (fallback != null && !string.IsNullOrEmpty(name))
            {
                var simulated = fallback(name);
                if (simulated.Id.IsValid)
                {
                    si = AllocSlot(name, simulated.Dir, new Loadable<Object>(simulated.Id));
                    s_Slots[si].Simulated = true;
                    s_SimDirById[IdKey(simulated.Id)] = simulated.Dir;
                    return true;
                }
            }

            var roots = SimulatedRootsDescription();
            Debug.LogError(name != null && s_SceneNames.ContainsKey(name)
                ? $"[ContentLink] '{name}' is a scene — use LoadSceneAsync."
                : roots != null
                    ? $"[ContentLink] '{name}' is not under {roots} in the project."
                    : $"[ContentLink] '{name}' is not in any registered ContentCatalog.");
            si = -1;
            return false;
        }

        static string IdKey(LoadableObjectId id) => "#" + id.ToString();

        static int GetSlotById(LoadableObjectId id)
        {
            var key = IdKey(id);
            if (s_SlotByKey.TryGetValue(key, out var si)) return si;
            // A catalog's id belongs to its directory. An id the simulation answered is served from the project: it
            // belongs to no engine directory (NoDirectory entries stay until ResetStatics) and never blocks one.
            bool simulated = !s_DirById.TryGetValue(key, out var d) && s_SimDirById.TryGetValue(key, out d);
            if (!simulated && !s_DirById.ContainsKey(key)) d = kDirUnknown;
            si = AllocSlot(key, d, new Loadable<Object>(id));
            s_Slots[si].Simulated = simulated;
            return si;
        }

        static void Enqueue(int si)
        {
            var s = s_Slots[si];
            s.ZeroFrame = Time.frameCount;
            if (s.Pending) return;
            s.Pending = true;
            s_Pending.Add(si);
        }

        static void FreeSlot(int si)
        {
            var s = s_Slots[si];
            try { s.Loadable?.Release(); }
            catch (Exception e) { Debug.LogException(e); }

            s_SlotByKey.Remove(s.Key);
            s.Alive = false;
            s.Serial++;
            s.Key = null;
            s.Loadable = null;
            s.Status = ContentStatus.None;
            s.Refs = 0;
            s.Waiters = null;
            s_FreeSlots.Push(si);
        }

        static void ForceFreeSlot(int si)
        {
            for (int i = 0; i < s_LeaseHigh; ++i)
                if (s_Leases[i].Active && s_Leases[i].Slot == si)
                    FreeLease(i);
            var s = s_Slots[si];
            if (s.Pending) { s.Pending = false; s_Pending.Remove(si); }
            FreeSlot(si);
        }

        // ───────────── loading ─────────────

        static async void StartLoad(Slot s)
        {
            s.Status = ContentStatus.Loading;
            uint serial = s.Serial;
            var loadable = s.Loadable;
            try { await loadable.LoadAsync(); }
            catch (Exception e) { Debug.LogException(e); }

            if (!s.Alive || s.Serial != serial) return;
            FinishLoad(s);
        }

        static void FinishLoad(Slot s)
        {
            if (s.Status != ContentStatus.Loading) return;
            s.Status = s.Loadable.Status == LoadableStatus.Loaded ? ContentStatus.Loaded : ContentStatus.Failed;
            if (s.Status == ContentStatus.Failed)
                Debug.LogError($"[ContentLink] failed to load '{s.Key}'.");

            var waiters = s.Waiters;
            s.Waiters = null;
            if (waiters != null)
                foreach (var w in waiters) w.TrySetResult(true);

            if (s.Refs == 0 && s.Alive && s_SlotByKey.TryGetValue(s.Key, out var si)) Enqueue(si);
        }

        /// <summary>Advanced. Synchronous load (blocks the main thread). Returns an invalid handle on failure.</summary>
        public static AssetHandle<T> Acquire<T>(string name) where T : Object
            => TryGetSlotByName(name, out var si) ? AcquireSync<T>(si) : default;

        public static AssetHandle<T> Acquire<T>(LoadableObjectId id) where T : Object
            => AcquireSync<T>(GetSlotById(id));

        /// <summary>
        /// Advanced. Asynchronous load. The lease is taken before loading starts, so a release elsewhere cannot unload it.
        /// On cancellation the lease is returned immediately; the engine load (not cancellable) completes and is released
        /// at the next Collect(). Returns an invalid handle on failure.
        /// </summary>
        public static Awaitable<AssetHandle<T>> AcquireAsync<T>(string name, CancellationToken ct = default) where T : Object
            => AcquireAsyncCore<T>(name, default, false, ct);

        public static Awaitable<AssetHandle<T>> AcquireAsync<T>(LoadableObjectId id, CancellationToken ct = default) where T : Object
            => AcquireAsyncCore<T>(null, id, true, ct);

        static AssetHandle<T> AcquireSync<T>(int si) where T : Object
        {
            var s = s_Slots[si];
            var h = NewHandle<T>(si);
            if (s.Status == ContentStatus.None) s.Status = ContentStatus.Loading;
            if (s.Status == ContentStatus.Loading)
            {
                try { s.Loadable.Load(); }   // also completes an in-flight async load
                catch (Exception e) { Debug.LogException(e); }
                FinishLoad(s);               // Failed when the engine did not get to Loaded, as on the async path
            }
            if (s.Status != ContentStatus.Loaded) { h.Dispose(); return default; }
            return h;
        }

        static async Awaitable<AssetHandle<T>> AcquireAsyncCore<T>(string name, LoadableObjectId id, bool byId, CancellationToken ct) where T : Object
        {
            ct.ThrowIfCancellationRequested();

            int si;
            if (byId) si = GetSlotById(id);
            else if (!TryGetSlotByName(name, out si)) return default;

            var s = s_Slots[si];
            var h = NewHandle<T>(si);

            if (s.Status == ContentStatus.None) StartLoad(s);

            if (s.Status == ContentStatus.Loading)
            {
                // Awaitable can be awaited only once, so every waiter gets its own completion source.
                var acs = new AwaitableCompletionSource<bool>();
                (s.Waiters ??= new List<AwaitableCompletionSource<bool>>()).Add(acs);
                using (ct.CanBeCanceled ? ct.Register(() => acs.TrySetCanceled()) : default)
                {
                    try { await acs.Awaitable; }
                    catch (OperationCanceledException) { h.Dispose(); throw; }
                }
            }

            if (!h.IsValid) return default;
            if (ct.IsCancellationRequested) { h.Dispose(); ct.ThrowIfCancellationRequested(); }
            if (h.Status != ContentStatus.Loaded) { h.Dispose(); return default; }
            return h;
        }

        /// <summary>Takes a new lease on an asset that is already live (loading or loaded). No I/O.</summary>
        internal static bool TryShareLive(string key, out AssetHandle<Object> handle)
        {
            handle = default;
            if (key == null || !s_SlotByKey.TryGetValue(key, out var si)) return false;
            handle = NewHandle<Object>(si);
            return true;
        }

        /// <summary>Finds the key of a live asset (or of the prefab a component belongs to). Linear scan.</summary>
        internal static bool TryFindKey(Object asset, out string key)
        {
            key = null;
            if (asset == null) return false;
            var go = asset is Component c ? c.gameObject : null;
            foreach (var kv in s_SlotByKey)
            {
                var s = s_Slots[kv.Value];
                if (s.Status != ContentStatus.Loaded) continue;
                var t = s.Loadable.Target;
                if (ReferenceEquals(t, asset) || (!ReferenceEquals(go, null) && ReferenceEquals(t, go))) { key = kv.Key; return true; }
            }
            return false;
        }

        // ───────────── deterministic release ─────────────

        /// <summary>
        /// Releases zero-reference assets. Engine unloads happen only here.
        /// Called automatically at the end of every frame when AutoCollect is on.
        /// </summary>
        public static int Collect() => Collect(false);

        static int Collect(bool ignoreDelay)
        {
            int freed = 0;
            int now = Time.frameCount;
            for (int i = s_Pending.Count - 1; i >= 0; --i)
            {
                int si = s_Pending[i];
                var s = s_Slots[si];

                bool drop = !s.Alive || s.Refs > 0 || s.Status == ContentStatus.Loading;
                if (!drop && !ignoreDelay && now - s.ZeroFrame < ReleaseDelayFrames) continue;

                s.Pending = false;
                s_Pending[i] = s_Pending[s_Pending.Count - 1];
                s_Pending.RemoveAt(s_Pending.Count - 1);

                if (drop) continue;
                FreeSlot(si);
                ++freed;
            }
            return freed;
        }

        // ───────────── leases ─────────────

        struct Lease
        {
            public int Slot;
            public uint Gen;
            public bool Active;
            public int Frame;
            public StackTrace Trace;
        }

        static Lease[] s_Leases = new Lease[256];
        static int s_LeaseHigh;
        static readonly Stack<int> s_FreeLeases = new();

        static AssetHandle<T> NewHandle<T>(int si) where T : Object
        {
            int li;
            if (s_FreeLeases.Count > 0) li = s_FreeLeases.Pop();
            else
            {
                if (s_LeaseHigh == s_Leases.Length) Array.Resize(ref s_Leases, s_Leases.Length * 2);
                li = s_LeaseHigh++;
            }

            ref var l = ref s_Leases[li];
            if (l.Gen == 0) l.Gen = 1;
            l.Slot = si;
            l.Active = true;
            l.Frame = Time.frameCount;
            l.Trace = CaptureStackTrace ? new StackTrace(2, true) : null;

            s_Slots[si].Refs++;
            return new AssetHandle<T>(li, l.Gen);
        }

        static void FreeLease(int li)
        {
            ref var l = ref s_Leases[li];
            l.Active = false;
            l.Gen = l.Gen == uint.MaxValue ? 1 : l.Gen + 1;   // invalidates every copy of the handle
            l.Trace = null;
            s_FreeLeases.Push(li);
        }

        internal static bool IsLeaseAlive(int li, uint gen)
            => gen != 0 && (uint)li < (uint)s_LeaseHigh && s_Leases[li].Active && s_Leases[li].Gen == gen;

        internal static void Release(int li, uint gen)
        {
            if (gen == 0) return;
            if (!IsLeaseAlive(li, gen))
            {
                Debug.LogWarning("[ContentLink] AssetHandle already released (double Dispose or copied handle).");
                return;
            }
            int si = s_Leases[li].Slot;
            FreeLease(li);
            var s = s_Slots[si];
            if (--s.Refs == 0) Enqueue(si);
        }

        internal static AssetHandle<T> Share<T>(int li, uint gen) where T : Object
            => IsLeaseAlive(li, gen) ? NewHandle<T>(s_Leases[li].Slot) : default;

        internal static T GetAsset<T>(int li, uint gen) where T : Object
        {
            if (!IsLeaseAlive(li, gen)) return null;
            var s = s_Slots[s_Leases[li].Slot];
            if (s.Status != ContentStatus.Loaded) return null;
            var target = s.Loadable.Target;
            if (target is T t) return t;
            if (target is GameObject go && typeof(Component).IsAssignableFrom(typeof(T)))
                return go.GetComponent(typeof(T)) as T;
            return null;
        }

        internal static void LogTypeMismatch<T>(int li, uint gen) where T : Object
        {
            if (!IsLeaseAlive(li, gen)) return;
            var s = s_Slots[s_Leases[li].Slot];
            var target = s.Loadable.Target;
            Debug.LogError($"[ContentLink] '{s.Key}' is {(target != null ? target.GetType().Name : "null")}, not {typeof(T).Name}.");
        }

        internal static string GetLeaseKey(int li, uint gen)
            => IsLeaseAlive(li, gen) ? s_Slots[s_Leases[li].Slot].Key : null;

        internal static ContentStatus GetLeaseStatus(int li, uint gen)
            => IsLeaseAlive(li, gen) ? s_Slots[s_Leases[li].Slot].Status : ContentStatus.None;

        // ───────────── scenes ─────────────
        //
        // A scene is not a Loadable: no reference count, no Target. Its lifetime is the scene instance itself.
        // ContentLink resolves names, tracks every loaded scene that belongs to a registered directory (whoever
        // loaded it) so UnregisterDirectory can refuse while one is open, and hands scopes an Additive-only
        // ownership layer (ContentScope.LoadSceneAsync).

        struct SceneNameEntry { public LoadableSceneId Id; public int Dir; }

        struct LoadedScene
        {
            public Scene Scene;
            public LoadableSceneId Id;
            public string Key;
            public bool Claimed;     // handed out by a LoadSceneAsync call (additive duplicates are separate instances)
            public bool Unloading;   // UnloadSceneAsync started by ContentLink
            public bool Foreign;     // loaded while no ContentLink load of this id was in flight: somebody else's, never claimed
        }

        // ContentLink scene loads in flight, per id. The engine does not say which Scene an AsyncOperation produced
        // (sceneLoaded fires before the operation is done — measured), so a load claims the newest instance that arrived
        // during a flight of its id; an instance that arrived outside any flight belongs to whoever loaded it.
        static readonly Dictionary<LoadableSceneId, int> s_Flights = new();
        static void Flight(LoadableSceneId id, int delta)
        {
            s_Flights.TryGetValue(id, out var n); n += delta;
            if (n <= 0) s_Flights.Remove(id); else s_Flights[id] = n;
        }

        static readonly Dictionary<string, SceneNameEntry> s_SceneNames = new(StringComparer.Ordinal);
        static readonly Dictionary<LoadableSceneId, List<int>> s_DirsBySceneId = new();   // every directory that carries the scene
        static readonly Dictionary<LoadableSceneId, string> s_SceneKeyById = new();
        static readonly List<LoadedScene> s_LoadedScenes = new();

        /// <summary>Loaded scenes that belong to a registered directory (whoever loaded them).</summary>
        public static int LoadedSceneCount => s_LoadedScenes.Count;

        /// <summary>
        /// Editor simulation, scene side. Three seams because a scene load is three questions:
        ///   · SceneContainsFallback — does the simulation know this name? (ContainsScene / RuntimeKeyIsValid)
        ///   · SceneFallback         — start the load and give me the operation; null means "not mine".
        ///                             The editor runs it, because EditorSceneManager cannot be called from here.
        ///   · SceneResultFallback   — which Scene did that load produce? An AsyncOperation does not say, and a
        ///                             simulated scene belongs to no directory so the id-based Claim finds nothing,
        ///                             so the editor matches the scene by the path it asked for.
        /// All null in a player. Not cleared by ResetStatics, for the reason NameFallback is not.
        /// </summary>
        public static Func<string, bool> SceneContainsFallback;
        public static Func<string, LoadSceneParameters, AsyncOperation> SceneFallback;
        public static Func<string, Scene> SceneResultFallback;

        public static bool ContainsScene(string name)
        {
            if (name == null) return false;
            if (s_SceneNames.ContainsKey(name)) return true;
            var known = SceneContainsFallback;
            return known != null && known(name);
        }

        /// <summary>Resolves a catalog scene name to its LoadableSceneId. False if no registered directory has the name.</summary>
        public static bool TryGetLoadableSceneId(string name, out LoadableSceneId id)
        {
            if (name != null && s_SceneNames.TryGetValue(name, out var e)) { id = e.Id; return true; }
            id = default;
            return false;
        }

        /// <summary>
        /// Loads a scene from a registered content directory. Returns an invalid Scene (IsValid() == false) on failure.
        /// The scene is not owned by anything: unload it with SceneManager.UnloadSceneAsync, or use
        /// ContentScope.LoadSceneAsync / ContentSceneLink to bind it to a scope. Single mode closes every other scene.
        /// Cancellation stops the wait; an Additive scene is unloaded once the engine load completes, a Single one stays.
        /// </summary>
        public static Awaitable<Scene> LoadSceneAsync(string name, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default)
            => LoadSceneAsync(name, new LoadSceneParameters(mode), ct);

        public static Awaitable<Scene> LoadSceneAsync(string name, LoadSceneParameters parameters, CancellationToken ct = default)
        {
            if (name != null && s_SceneNames.TryGetValue(name, out var e)) return LoadSceneCore(name, e.Id, parameters, ct);
            if (name != null && SceneFallback != null) return LoadSimulatedSceneCore(name, parameters, ct);   // a catalog name wins over the simulation
            Debug.LogError($"[ContentLink] scene '{name}' is not in any registered ContentCatalog.");
            return InvalidScene();
        }

        public static Awaitable<Scene> LoadSceneAsync(LoadableSceneId id, LoadSceneParameters parameters, CancellationToken ct = default)
            => LoadSceneCore(SceneKey(id), id, parameters, ct);

        static Awaitable<Scene> InvalidScene()
        {
            var acs = new AwaitableCompletionSource<Scene>();
            acs.TrySetResult(default);
            return acs.Awaitable;
        }

        /// <summary>
        /// The editor-simulation twin of <see cref="LoadSceneCore"/>. The editor starts the load and
        /// names the resulting Scene; everything else — the cancellation shape, the error wording — matches, so a
        /// caller cannot tell the two apart except that the scene is not part of any directory.
        /// </summary>
        static async Awaitable<Scene> LoadSimulatedSceneCore(string key, LoadSceneParameters parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            AsyncOperation op = null;
            try { op = SceneFallback?.Invoke(key, parameters); }
            catch (Exception ex) { Debug.LogException(ex); }
            if (op == null)
            {
                Debug.LogError($"[ContentLink] scene '{key}' is not in any registered ContentCatalog.");
                return default;
            }

            var acs = new AwaitableCompletionSource<bool>();
            op.completed += _ => acs.TrySetResult(true);
            try
            {
                using (ct.CanBeCanceled ? ct.Register(() => acs.TrySetCanceled()) : default)
                    await acs.Awaitable;
            }
            catch (OperationCanceledException)
            {
                if (parameters.loadSceneMode == LoadSceneMode.Additive)
                    op.completed += _ => { var late = SceneResultFallback?.Invoke(key) ?? default; if (late.IsValid()) _ = TryUnloadScene(late); };
                else
                    Debug.LogWarning($"[ContentLink] load of scene '{key}' was cancelled after a Single-mode load started; the scene stays loaded.");
                throw;
            }

            var scene = SceneResultFallback?.Invoke(key) ?? default;
            if (!scene.IsValid())
            {
                Debug.LogError($"[ContentLink] failed to load scene '{key}'.");
                return default;
            }

            // OnSceneLoaded ignored it: a simulated scene is in no directory. Track it here anyway so the scope
            // that asked for it can unload it; what it stays outside of is UnregisterDirectory's open-scene guard,
            // which is by definition about directories.
            s_LoadedScenes.Add(new LoadedScene { Scene = scene, Id = default, Key = key, Claimed = true });
            return scene;
        }

        static async Awaitable<Scene> LoadSceneCore(string key, LoadableSceneId id, LoadSceneParameters parameters, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!id.IsValid || !s_DirsBySceneId.ContainsKey(id))
            {
                Debug.LogError($"[ContentLink] scene '{key}' is not in any registered content directory.");
                return default;
            }

            AsyncOperation op;
            try { op = SceneManager.LoadSceneAsync(id, parameters); }
            catch (Exception ex) { Debug.LogException(ex); op = null; }
            if (op == null)
            {
                Debug.LogError($"[ContentLink] failed to start loading scene '{key}'.");
                return default;
            }
            Flight(id, +1);   // instances of this id that arrive from now on may be ours

            // Awaitable.FromAsyncOperation(op, ct) cancels the native awaitable, which does not surface as
            // OperationCanceledException to the awaiter. Wait on a managed completion source instead,
            // the same way asset loads do (AcquireAsyncCore).
            var acs = new AwaitableCompletionSource<bool>();
            op.completed += _ => acs.TrySetResult(true);
            try
            {
                using (ct.CanBeCanceled ? ct.Register(() => acs.TrySetCanceled()) : default)
                    await acs.Awaitable;
            }
            catch (OperationCanceledException)
            {
                // The engine load cannot be cancelled. Additive: hand the instance back once it exists.
                // Single: the previous scenes are already gone and the new one is the last scene, so it stays.
                if (parameters.loadSceneMode == LoadSceneMode.Additive)
                    op.completed += _ => { Flight(id, -1); var late = Claim(id); if (late.IsValid()) _ = TryUnloadScene(late); };
                else
                {
                    Debug.LogWarning($"[ContentLink] load of scene '{key}' was cancelled after a Single-mode load started; the scene stays loaded.");
                    op.completed += _ => Flight(id, -1);
                }
                throw;
            }

            Flight(id, -1);
            var scene = Claim(id);
            if (!scene.IsValid()) Debug.LogError($"[ContentLink] failed to load scene '{key}'.");
            return scene;
        }

        static string SceneKey(LoadableSceneId id) => s_SceneKeyById.TryGetValue(id, out var k) ? k : id.ToString();

        static Scene Claim(LoadableSceneId id)
        {
            for (int i = s_LoadedScenes.Count - 1; i >= 0; --i)   // newest first, and never an instance somebody else loaded
            {
                var ls = s_LoadedScenes[i];
                if (!(ls.Id == id) || ls.Foreign || ls.Claimed || ls.Unloading || !ls.Scene.isLoaded) continue;   // isLoaded drops as soon as an unload starts
                ls.Claimed = true;
                s_LoadedScenes[i] = ls;
                return ls.Scene;
            }
            return default;
        }

        static int IndexOfScene(Scene scene)
        {
            for (int i = 0; i < s_LoadedScenes.Count; ++i)
                if (s_LoadedScenes[i].Scene.handle == scene.handle) return i;
            return -1;
        }

        internal static string GetSceneKey(Scene scene)
        {
            int i = IndexOfScene(scene);
            return i >= 0 ? s_LoadedScenes[i].Key : scene.name;
        }

        /// <summary>
        /// Starts unloading a scene on behalf of a scope or link. Null (and nothing started) when the scene is not
        /// loaded, is already being unloaded by ContentLink, or is the last loaded scene (the engine refuses that).
        /// </summary>
        internal static AsyncOperation TryUnloadScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return null;
            int i = IndexOfScene(scene);
            if (i >= 0 && s_LoadedScenes[i].Unloading) return null;
            if (SceneManager.loadedSceneCount <= 1)
            {
                Debug.LogWarning($"[ContentLink] scene '{GetSceneKey(scene)}' is the last loaded scene and stays loaded.");
                return null;
            }
            AsyncOperation op;
            try { op = SceneManager.UnloadSceneAsync(scene); }
            catch (Exception e) { Debug.LogException(e); return null; }
            if (op != null && i >= 0) { var ls = s_LoadedScenes[i]; ls.Unloading = true; s_LoadedScenes[i] = ls; }
            return op;
        }

        static bool HasLoadedScenes(int dir)
        {
            foreach (var ls in s_LoadedScenes)
                if (s_DirsBySceneId.TryGetValue(ls.Id, out var dirs) && dirs.Contains(dir)) return true;
            return false;
        }

        static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            var id = scene.loadableSceneId;
            if (!id.IsValid || !s_DirsBySceneId.ContainsKey(id)) return;   // not from a registered directory: none of our business
            s_LoadedScenes.Add(new LoadedScene { Scene = scene, Id = id, Key = SceneKey(id), Foreign = !s_Flights.ContainsKey(id) });
        }

        static void OnSceneUnloaded(Scene scene)
        {
            int i = IndexOfScene(scene);
            if (i >= 0) s_LoadedScenes.RemoveAt(i);
        }

        // ───────────── diagnostics ─────────────

        public readonly struct LeaseInfo
        {
            public readonly string Key;
            public readonly int Frame;
            public readonly StackTrace Trace;
            public readonly int Dir;      // the directory the asset belongs to, NoDirectory when unknown
            internal LeaseInfo(string key, int frame, StackTrace trace, int dir) { Key = key; Frame = frame; Trace = trace; Dir = dir; }
            public override string ToString() => $"'{Key}' frame={Frame}";
        }

        public static void Snapshot(List<LeaseInfo> results)
        {
            results.Clear();
            for (int i = 0; i < s_LeaseHigh; ++i)
                if (s_Leases[i].Active)
                    results.Add(new LeaseInfo(s_Slots[s_Leases[i].Slot].Key, s_Leases[i].Frame, s_Leases[i].Trace, s_Slots[s_Leases[i].Slot].Dir));
        }

        public readonly struct SceneInfo
        {
            public readonly string Key;
            public readonly int Dir;      // first directory that carries the scene
            public readonly Scene Scene;
            internal SceneInfo(string key, int dir, Scene scene) { Key = key; Dir = dir; Scene = scene; }
            public override string ToString() => $"'{Key}' dir={Dir}";
        }

        /// <summary>
        /// Names whose slots the editor simulation resolved rather than a catalog. Empty in a player.
        /// These are the links that would fail in a build until the content directory is rebuilt.
        /// </summary>
        public static void SnapshotSimulated(List<string> results)
        {
            results.Clear();
            for (int i = 0; i < s_Slots.Count; ++i)
                if (s_Slots[i].Alive && s_Slots[i].Simulated) results.Add(s_Slots[i].Key);
        }

        public static void SnapshotScenes(List<SceneInfo> results)
        {
            results.Clear();
            foreach (var ls in s_LoadedScenes)
                results.Add(new SceneInfo(ls.Key, s_DirsBySceneId.TryGetValue(ls.Id, out var dirs) && dirs.Count > 0 ? dirs[0] : kDirUnknown, ls.Scene));
        }

        // ───────────── lifetime ─────────────

        /// <summary>
        /// In the editor with domain reload disabled, content directories that Shutdown left registered (a scene from
        /// them was still open at quit) survive in the engine and would make the next Play session's
        /// RegisterDirectory throw "already registered". Nothing from the previous session is alive any more
        /// when a new session starts, so hand them back here. In a player the list is empty.
        /// </summary>
        static void ReleaseLeftoverDirectories()
        {
            ContentDirectoryHandle[] leftovers;
            try { leftovers = ContentLoadManager.GetContentDirectories(); }
            catch (Exception e) { Debug.LogException(e); return; }
            int dirs = leftovers?.Length ?? 0;
            for (int i = 0; i < dirs; ++i)
            {
                try { ContentLoadManager.UnregisterContentDirectory(leftovers[i]); }
                catch (Exception e) { Debug.LogException(e); }
            }
            int archives = ContentArchives.UnmountLeftovers();   // packed directories leave their mounts behind the same way — after the directories, which may still read from them
            if (dirs == 0 && archives == 0) return;
            Debug.Log($"[ContentLink] released {dirs} content director{(dirs == 1 ? "y" : "ies")} and unmounted {archives} archive(s) left by the previous session.");
        }

        /// <summary>Disposes the scope tree, reports and returns remaining raw leases, unregisters all directories.</summary>
        public static void Shutdown()
        {
            ContentScope.ShutdownRoot();
            for (int i = 0; i < s_LeaseHigh; ++i)
                if (s_Leases[i].Active)
                {
                    Debug.LogWarning($"[ContentLink] leaked raw AssetHandle at shutdown: '{s_Slots[s_Leases[i].Slot].Key}'\n{s_Leases[i].Trace}");
                    Release(i, s_Leases[i].Gen);
                }
            Collect(ignoreDelay: true);
            for (int d = 0; d < s_Dirs.Count; ++d)
                if (s_Dirs[d].Alive && !HasLoadedScenes(d)) UnregisterDirectory(d, force: true);   // open scenes: the engine tears them down
            s_LoadedScenes.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()   // runs on every Play entry, even with domain reload disabled
        {
            ReleaseLeftoverDirectories();
            s_Dirs.Clear(); s_Names.Clear(); s_DirById.Clear(); s_SimDirById.Clear();
            s_Slots.Clear(); s_FreeSlots.Clear(); s_SlotByKey.Clear(); s_Pending.Clear();
            s_Leases = new Lease[256]; s_LeaseHigh = 0; s_FreeLeases.Clear();
            s_SceneNames.Clear(); s_DirsBySceneId.Clear(); s_SceneKeyById.Clear(); s_LoadedScenes.Clear(); s_Flights.Clear();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }
    }
}
