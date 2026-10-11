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

        /// <summary>
        /// Unload a Resources asset when its last reference goes (Resources.UnloadAsset). The engine hands one object to every
        /// caller of a path, so the unload also kills references code took with Resources.Load directly — turn this off while
        /// such code remains; zero-reference assets are then reclaimed by <see cref="SweepResources"/>.
        /// </summary>
        public static bool UnloadResourcesOnRelease = true;

        /// <summary>A name that starts with this loads from a Resources folder built into the player: "res:UI/logo", "res:UI/sheet[s0]".</summary>
        public const string ResourcesPrefix = "res:";

        /// <summary>The directory index of an asset loaded from Resources (<see cref="LeaseInfo.Dir"/>): it belongs to no content directory and never blocks one.</summary>
        public const int ResourcesDirectory = -2;

        // ───────────── directories / catalogs ─────────────

        sealed class DirectoryRecord
        {
            public ContentDirectoryHandle Handle;
            public string Path;
            public ContentCatalog[] Catalogs;
            public bool Alive;
            public Unity.IO.Archive.ArchiveHandle[] Archives;   // set for a packed directory: unmounted when it is unregistered
            public string PackedFolder;                          // a packed directory's folder (full path); Path is the mount path
            public string[] PackedFiles;                         // the archives mounted from it (full paths) — the engine reads them on demand
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
        static readonly HashSet<string> s_Mounting = new(StringComparer.OrdinalIgnoreCase);   // packed folders whose archives are mounting
        static readonly Dictionary<string, NameEntry> s_Names = new(StringComparer.Ordinal);
        static readonly Dictionary<LoadableObjectId, int> s_DirById = new();
        static readonly Dictionary<LoadableObjectId, int> s_SimDirById = new();   // ids the simulation answered → their simulated directory

        /// <summary>
        /// Registers a content directory and indexes its ContentCatalog root assets.
        /// When the same name exists in several directories the most recently registered one wins,
        /// matching the engine's Loadable resolution order — a locale or patch directory registered after its base takes
        /// over the names it carries. A name already open keeps the object it was opened with until it is released, so
        /// register such a directory before loading. The engine refuses a second directory of one build name — a profile's
        /// name, or an unnamed build's folder name.
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
        /// Returns -1 (logged) when there is no index or one it cannot use (another version, an archive without its MD5, an archive
        /// name that is not <see cref="ContentArchives.IsArchiveName"/> or comes twice), it lists no archives, it describes content this player cannot load
        /// (another platform; without type trees another engine or catalog layout; a newer engine), or an archive's size on
        /// disk differs from the index; throws when the folder is already registered or being registered (before anything is
        /// mounted), an archive does not mount or the engine refuses.
        /// </summary>
        public static async Awaitable<int> RegisterPackedDirectoryAsync(string directory)
        {
            var simulated = DirectoryFallback?.Invoke(directory);
            if (simulated != null) return RegisterSimulated(directory, simulated);   // served from the project: nothing to mount

            var folder = NormalizePath(directory);
            for (int d = 0; d < s_Dirs.Count; ++d)   // before anything is mounted: mounting its archives again breaks the registered directory (measured)
                if (s_Dirs[d].Alive && s_Dirs[d].PackedFolder != null && string.Equals(s_Dirs[d].PackedFolder, folder, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"[ContentLink] '{directory}' is already registered (directory {d}) — unregister it before registering it again.");
            if (s_Mounting.Contains(folder))
                throw new InvalidOperationException($"[ContentLink] '{directory}' is being registered — its archives are mounting: await that registration instead of starting another.");

            var index = ContentArchives.ReadIndex(directory);
            if (index == null)
            {
                if (!System.IO.File.Exists(System.IO.Path.Combine(directory, ContentArchives.IndexFile)))
                    Debug.LogError($"[ContentLink] '{directory}' has no {ContentArchives.IndexFile}; use RegisterDirectory for a flat or engine-archive build.");
                return -1;
            }
            if (index.Archives.Length == 0)
            {
                Debug.LogError($"[ContentLink] '{directory}' has an {ContentArchives.IndexFile} that lists no archives; nothing to mount.");
                return -1;
            }
            if (!ContentArchives.CheckCompatible(index, out var why))
            {
                Debug.LogError($"[ContentLink] '{directory}' cannot be loaded by this player: {why}; nothing mounted.");
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
            s_Mounting.Add(folder);
            Unity.IO.Archive.ArchiveHandle[] handles;
            try { handles = await ContentArchives.MountAsync(directory, index); }
            finally { s_Mounting.Remove(folder); }
            int dir;
            try { dir = RegisterDirectory(handles[0].GetMountPath()); }
            catch
            {
                ContentArchives.Unmount(handles);
                throw;
            }
            s_Dirs[dir].Archives = handles;
            s_Dirs[dir].PackedFolder = folder;
            var files = new string[index.Archives.Length];
            for (int i = 0; i < files.Length; ++i) files[i] = NormalizePath(System.IO.Path.Combine(directory, index.Archives[i].File));
            s_Dirs[dir].PackedFiles = files;
            return dir;
        }

        /// <summary>Whether a registered packed directory was mounted from <paramref name="folder"/> — or is being mounted from it.</summary>
        internal static bool IsPackedFolderRegistered(string folder)
        {
            var full = NormalizePath(folder);
            if (s_Mounting.Contains(full)) return true;   // its archives are mounting
            for (int d = 0; d < s_Dirs.Count; ++d)
                if (s_Dirs[d].Alive && s_Dirs[d].PackedFolder != null && string.Equals(s_Dirs[d].PackedFolder, full, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether a registered packed directory has <paramref name="path"/> mounted — or a folder being mounted holds it; the engine reads it on demand, so it must stay (measured: a deleted one fails the loads).</summary>
        internal static bool IsMountedFile(string path)
        {
            var full = NormalizePath(path);
            if (s_Mounting.Count > 0 && s_Mounting.Contains(NormalizePath(System.IO.Path.GetDirectoryName(full)))) return true;   // a folder being mounted
            for (int d = 0; d < s_Dirs.Count; ++d)
            {
                var files = s_Dirs[d].Alive ? s_Dirs[d].PackedFiles : null;
                if (files == null) continue;
                foreach (var f in files) if (string.Equals(f, full, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Unregisters a directory. The engine requires every Loadable from it to be released first, so this
        /// refuses while live assets remain — an asset another directory's name opened counts too when this directory gave it (registered
        /// after that one, it carries the same asset: two profiles whose roots nest). force=true invalidates the remaining leases (still refused while loading).
        /// A scene of it still loaded, or still loading, refuses it too, forced or not. The names it took over answer from the directories
        /// registered before it again — how a locale is switched off.
        /// </summary>
        public static bool UnregisterDirectory(int dir, bool force = false)
        {
            if ((uint)dir >= (uint)s_Dirs.Count || !s_Dirs[dir].Alive) return false;
            var rec = s_Dirs[dir];

            Collect(ignoreDelay: true);

            StringBuilder scenes = null;
            foreach (var ls in s_LoadedScenes)
                if (Holds(dir, ls))
                    (scenes ??= new StringBuilder($"[ContentLink] '{rec.Path}' still has loaded scene(s) — unload them first:")).Append("\n  ").Append(ls.Key);
            if (scenes != null) { Debug.LogError(scenes.ToString()); return false; }   // cannot be forced: unloading is asynchronous
            if (HasSceneLoadsInFlight(dir))   // cannot be forced: unregistering under a scene load crashes the engine (measured)
            {
                var loading = new StringBuilder($"[ContentLink] '{rec.Path}' has scene load(s) in flight — unregister it once they have ended (a cancelled one's too: the engine finishes it):");
                SceneLoadsInFlight(dir, loading);
                Debug.LogError(loading.ToString());
                return false;
            }

            var blocking = new List<int>();
            for (int i = 0; i < s_Slots.Count; ++i)
            {
                var s = s_Slots[i];
                if (s.Alive && (s.Dir == dir || s.Source == dir || (s.Dir == kDirUnknown && !s.Simulated))) blocking.Add(i);   // a slot the editor serves from the project belongs to no engine directory
            }

            if (blocking.Count > 0)
            {
                var sb = new StringBuilder($"[ContentLink] '{rec.Path}' still has {blocking.Count} live asset(s):");
                bool anyLoading = false;
                foreach (var i in blocking)
                {
                    sb.Append($"\n  {s_Slots[i].Key} refs={s_Slots[i].Refs} status={s_Slots[i].Status}");
                    if (s_Slots[i].Dir != dir && s_Slots[i].Dir >= 0) sb.Append($" — a name of '{s_Dirs[s_Slots[i].Dir].Path}', loaded from this directory: registered after it, this one carries the same asset");
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
            var stale = new List<LoadableObjectId>();
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
        /// Set by the editor to turn a GUID no registered catalog carries into a name: (guid, sub-asset name, is scene) → the
        /// name, or null for "not mine". It answers the name only — whether the simulation may serve that name is decided by
        /// the name path's own fallbacks that follow. Null in a player. Not cleared by <see cref="ResetStatics"/>, like <see cref="NameFallback"/>.
        /// </summary>
        public static Func<string, string, bool, string> RefFallback;

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
        /// Set by the editor: what a player's Resources gives for a path — its main asset, or the sprite of that name — or null,
        /// for every res: load in the editor (Edit Mode as well as Play Mode).
        /// The editor's own Resources also finds the Resources folders under an Editor folder, which a player lacks, and may give
        /// such a file for a path a player's file shares (measured on 6000.6). Null in a player. Not cleared by
        /// <see cref="ResetStatics"/>, like <see cref="NameFallback"/>.
        /// </summary>
        public static Func<string, string, Object> ResourcesFallback;

        /// <summary>
        /// The simulated directory that covers an asset under a source root: the most recently registered
        /// one wins, as the engine's rule for real directories does. False when none does.
        /// </summary>
        public static bool TryGetSimulatedDir(string sourceRoot, string assetPath, out int dir)
        {
            dir = NoDirectory;
            if (string.IsNullOrEmpty(sourceRoot) || string.IsNullOrEmpty(assetPath)) return false;
            var root = ComposedOf(NormalizePath(sourceRoot));
            var path = ComposedOf(NormalizePath(assetPath));   // the editor spells a path's folders decomposed on macOS; the covered folders are typed
            for (int d = s_Dirs.Count - 1; d >= 0; --d)
            {
                var rec = s_Dirs[d];
                if (!rec.Alive || !rec.Simulated || !string.Equals(ComposedOf(NormalizePath(rec.SourceRoot)), root, StringComparison.OrdinalIgnoreCase)) continue;
                if (Covers(rec, path))
                {
                    dir = d;
                    return true;
                }
            }
            return false;
        }

        /// <summary>A simulated directory's covered folders hold the file (<paramref name="path"/> normalized).</summary>
        static bool Covers(DirectoryRecord rec, string path)
        {
            path = ComposedOf(path);   // as the covered folders are: the editor spells a scene's folders decomposed on macOS, and a project's own path may be spelled either way
            foreach (var folder in rec.Covered)
            {
                var covered = ComposedOf(NormalizePath(folder));
                if (string.Equals(path, covered, StringComparison.OrdinalIgnoreCase) || path.StartsWith(covered + "/", StringComparison.OrdinalIgnoreCase)) return true;
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
            if (IsResourcesName(name)) return IsWellFormedResourcesName(name);   // no index for Resources: the format only (the file shows at load)
            if (s_Names.ContainsKey(name)) return true;
            if (OtherForm(name) is string composed) return Contains(composed);
            var fallback = NameFallback;
            return fallback != null && fallback(name).Id.IsValid;
        }

        /// <summary>Resolves a catalog name to its LoadableObjectId. False if no registered directory has the name.</summary>
        public static bool TryGetLoadableObjectId(string name, out LoadableObjectId id)
        {
            if (name != null && s_Names.TryGetValue(name, out var e)) { id = e.Id; return true; }
            if (OtherForm(name) is string composed) return TryGetLoadableObjectId(composed, out id);
            var fallback = NameFallback;
            if (fallback != null && !string.IsNullOrEmpty(name) && !IsResourcesName(name))
            {
                var simulated = fallback(name);
                if (simulated.Id.IsValid)
                {
                    id = simulated.Id;
                    s_SimDirById[id] = simulated.Dir;   // an id-based load later still knows it is simulated, and its directory (NoDirectory included)
                    return true;
                }
            }
            id = default;
            return false;
        }

        static readonly Dictionary<string, string> s_ComposedKeys = new(StringComparer.Ordinal);   // a name asked in another Unicode form → its composed form

        /// <summary>
        /// The composed (NFC) form of a name given in another Unicode form, else null. Catalog names are indexed composed — the form
        /// an IME types; a catalog an earlier version built, or a link it saved, may spell a name as the macOS editor spells a path:
        /// folders decomposed, the file as on disk (measured on 6000.6). An ASCII name answers null after a scan and a composed one
        /// after a check, neither allocating nor remembered — a name no catalog has adds nothing; a name in another form is
        /// normalized once and remembered, and the lookups that use it allocate nothing after the first.
        /// </summary>
        static string OtherForm(string name)
        {
            if (name == null || Ascii(name)) return null;
            if (s_ComposedKeys.TryGetValue(name, out var composed)) return composed;
            try
            {
                if (name.IsNormalized(NormalizationForm.FormC)) return null;
                composed = name.Normalize(NormalizationForm.FormC);
            }
            catch (ArgumentException) { return null; }   // not Unicode (an unpaired surrogate): no other form
            return s_ComposedKeys[name] = composed == name ? null : composed;
        }

        /// <summary>The key a scope holds a name under: composed (<see cref="OtherForm"/>) — one key for an asset whichever form loads it.</summary>
        internal static string ScopeKey(string name) => OtherForm(name) ?? name;

        /// <summary>
        /// Two names of one catalog that compose to one — an earlier version named a file spelled composed and one spelled decomposed
        /// apart: one of them answers both now. Warned once a catalog.
        /// </summary>
        static void WarnFormTwins(ContentCatalog cat)
        {
            if (!s_TwinsWarned.Add(cat)) return;
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);   // a composed name → the asset its first entry names
            foreach (var e in cat.Entries)
            {
                if (string.IsNullOrEmpty(e.name)) continue;
                var key = ComposedOf(e.name);
                var asset = string.IsNullOrEmpty(e.guid) ? e.id.ToString() : e.guid;
                if (seen.TryAdd(key, asset)) continue;
                Debug.LogWarning($"[ContentLink] catalog '{cat.name}' holds the name '{key}' twice, in two Unicode forms ({seen[key]} · {asset}) — an earlier version built it: one of them answers both now; update the catalog and build it again");
                return;
            }
        }

        static readonly HashSet<ContentCatalog> s_TwinsWarned = new();

        /// <summary>A name or a path as it is indexed and compared: composed (<see cref="OtherForm"/>) — normalized here, not remembered.</summary>
        static string ComposedOf(string name)
        {
            if (name == null || Ascii(name)) return name;
            return name.IsNormalized(NormalizationForm.FormC) ? name : name.Normalize(NormalizationForm.FormC);
        }

        static bool Ascii(string s) { foreach (var c in s) if (c >= 0x80) return false; return true; }

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
                    bool spelledOtherwise = false;   // a catalog an earlier version built on macOS: names as that editor spelled the paths
                    foreach (var e in cat.Entries)
                    {
                        s_DirById[e.id] = d;
                        if (string.IsNullOrEmpty(e.name)) continue;
                        var key = ComposedOf(e.name);
                        spelledOtherwise |= !ReferenceEquals(key, e.name);
                        s_Names[key] = new NameEntry { Id = e.id, Dir = d };
                    }
                    if (spelledOtherwise) WarnFormTwins(cat);
                    foreach (var e in cat.Scenes)
                    {
                        if (!e.id.IsValid) continue;
                        if (!s_DirsBySceneId.TryGetValue(e.id, out var dirs)) s_DirsBySceneId[e.id] = dirs = new List<int>(1);
                        if (!dirs.Contains(d)) dirs.Add(d);
                        if (string.IsNullOrEmpty(e.name)) continue;
                        var key = ComposedOf(e.name);
                        s_SceneNames[key] = new SceneNameEntry { Id = e.id, Dir = d };
                        s_SceneKeyById[e.id] = key;
                    }
                }
            }
            s_RefIndexDirty = true;   // the reference index follows on its next lookup (EnsureRefIndex)
            s_LinkMisses.Clear();     // a new directory may carry a pair that missed
        }

        // ───────────── references (ContentRef / ContentSceneRef) ─────────────
        //
        // A reference is turned into the catalog name the build recorded for its GUID, and everything after that is the name
        // path: one slot and one scope key per asset, whichever way it was asked for. The index is built on the first
        // reference lookup after the registrations changed, so a project that never uses references never pays for it.

        static readonly Dictionary<RefKey, string> s_NameByRef = new();
        static readonly Dictionary<string, string> s_SceneNameByGuid = new(StringComparer.Ordinal);
        static bool s_RefIndexDirty;

        /// <summary>
        /// The catalog name a reference resolves to — a registered catalog first, then the editor simulation. A lookup only:
        /// a miss logs nothing and allocates nothing (loads through a reference report their misses themselves).
        /// </summary>
        public static bool TryGetName<T>(ContentRef<T> reference, out string name) where T : Object
            => TryResolveName(reference.AssetGUID, reference.SubObjectName, false, out name);

        /// <summary>The catalog scene name a scene reference resolves to. A lookup only, like <see cref="TryGetName{T}"/>.</summary>
        public static bool TryGetSceneName(ContentSceneRef reference, out string name)
            => TryResolveName(reference.AssetGUID, "", true, out name);

        /// <summary>True when the reference can be loaded: it resolves to a name <see cref="Contains(string)"/> accepts.</summary>
        public static bool Contains<T>(ContentRef<T> reference) where T : Object
            => TryGetName(reference, out var name) && Contains(name);

        /// <summary>True when the scene reference can be loaded: it resolves to a name <see cref="ContainsScene(string)"/> accepts.</summary>
        public static bool ContainsScene(ContentSceneRef reference)
            => TryGetSceneName(reference, out var name) && ContainsScene(name);

        static bool TryResolveName(string guid, string sub, bool scene, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(guid)) return false;
            EnsureRefIndex();
            if (scene ? s_SceneNameByGuid.TryGetValue(guid, out name) : s_NameByRef.TryGetValue(new RefKey(guid, sub), out name) || (OtherForm(sub) is string composed && s_NameByRef.TryGetValue(new RefKey(guid, composed), out name))) return true;   // a sprite's name is spelled as its file: indexed composed
            var fallback = RefFallback;
            name = fallback?.Invoke(guid, sub ?? "", scene);
            if (!string.IsNullOrEmpty(name)) return true;
            name = null;
            return false;
        }

        static void EnsureRefIndex()
        {
            if (!s_RefIndexDirty) return;
            s_RefIndexDirty = false;
            s_NameByRef.Clear();
            s_SceneNameByGuid.Clear();
            for (int d = 0; d < s_Dirs.Count; ++d)   // registration order: the most recently registered directory wins, as for names
            {
                var rec = s_Dirs[d];
                if (!rec.Alive) continue;
                foreach (var cat in rec.Catalogs)
                {
                    if (cat == null) continue;
                    foreach (var e in cat.Entries)
                        if (!string.IsNullOrEmpty(e.guid) && !string.IsNullOrEmpty(e.name))
                            s_NameByRef[new RefKey(e.guid, ComposedOf(e.sub))] = ComposedOf(e.name);
                    foreach (var e in cat.Scenes)
                        if (!string.IsNullOrEmpty(e.guid) && !string.IsNullOrEmpty(e.name))
                            s_SceneNameByGuid[e.guid] = ComposedOf(e.name);
                }
            }
        }

        /// <summary>The resolution a load through a reference uses: a miss is logged with the reference.</summary>
        internal static bool ResolveForLoad<T>(ContentRef<T> reference, out string name) where T : Object
        {
            if (TryGetName(reference, out name)) return true;
            LogRefMiss(reference.ToString(), reference.AssetGUID, reference.SubObjectName, false);
            return false;
        }

        internal static bool ResolveSceneForLoad(ContentSceneRef reference, out string name)
        {
            if (TryGetSceneName(reference, out name)) return true;
            LogRefMiss(reference.ToString(), reference.AssetGUID, "", true);
            return false;
        }

        static void LogRefMiss(string reference, string guid, string sub, bool scene)
        {
            if (string.IsNullOrEmpty(guid)) { Debug.LogError($"[ContentLink] {reference} is not set."); return; }
            if (!scene && s_SceneNameByGuid.TryGetValue(guid, out var sceneName))
                Debug.LogError($"[ContentLink] {reference} is the scene '{sceneName}' — use ContentSceneRef.");
            else if (scene && s_NameByRef.TryGetValue(new RefKey(guid, ""), out var assetName))
                Debug.LogError($"[ContentLink] {reference} is the asset '{assetName}', not a scene — use ContentRef<T>.");
            else if (!scene && sub.Length > 0 && s_NameByRef.TryGetValue(new RefKey(guid, ""), out var mainName))
                Debug.LogError($"[ContentLink] {reference}: '{mainName}' is catalogued, but it has no sprite named '{sub}' (a renamed slice, or a sub-asset that is not a sprite).");
            else
            {
                var roots = SimulatedRootsDescription();
                Debug.LogError(roots != null
                    ? $"[ContentLink] {reference} is not under {roots} in the project."
                    : $"[ContentLink] {reference} is not in any registered ContentCatalog.");
            }
        }

        // ───────────── links with a sub-object name (ContentLink) ─────────────
        //
        // ("Icons/sheet", "s0") is the catalog name "Icons/sheet[s0]": the pair is composed once and the string is kept, so a
        // link takes the name path — one slot and one scope key — without allocating after its first lookup. The cache keeps
        // strings, never answers. Misses are remembered only while no editor fallback can discover a file without a
        // registration change, and are forgotten on every registration change.

        static readonly Dictionary<RefKey, string> s_LinkKeys = new();   // (main name, sub) → "main[sub]" for pairs that resolved
        static readonly HashSet<RefKey> s_LinkMisses = new();             // pairs that did not, while NameFallback is null

        /// <summary>
        /// The catalog name of a (main name, sprite name) pair when it can be loaded: <paramref name="name"/> itself for an empty
        /// <paramref name="sub"/>, else "name[sub]" — composed (NFC), as the catalog and a scope hold it. Unlike <see cref="TryGetName{T}(ContentRef{T}, out string)"/>, which only
        /// resolves, this answers the same as <see cref="Contains(string, string)"/>. After the first lookup of a pair it
        /// allocates nothing, hit or miss (in the editor a miss composes each time).
        /// </summary>
        public static bool TryGetName(string name, string sub, out string catalogName)
        {
            if (string.IsNullOrEmpty(sub))
            {
                catalogName = name != null && s_Names.ContainsKey(name) ? name : Contains(name) ? OtherForm(name) ?? name : null;   // the catalog's spelling: composed
                return catalogName != null;
            }
            if (TryLinkName(name, sub, out catalogName)) return true;
            catalogName = null;
            return false;
        }

        /// <summary>True when a (main name, sprite name) pair can be loaded. See <see cref="TryGetName(string, string, out string)"/>.</summary>
        public static bool Contains(string name, string sub) => TryGetName(name, sub, out _);

        static bool TryLinkName(string name, string sub, out string composed)
        {
            composed = null;
            if (string.IsNullOrEmpty(name)) return false;
            var key = new RefKey(name, sub);
            if (s_LinkKeys.TryGetValue(key, out composed)) return Contains(composed);   // its directory may be gone
            if (NameFallback == null && s_LinkMisses.Contains(key)) return false;
            composed = string.Concat(name, "[", sub, "]");
            if (Contains(composed)) { s_LinkKeys[key] = composed = OtherForm(composed) ?? composed; return true; }
            if (NameFallback == null) s_LinkMisses.Add(key);   // a fallback can find a file made during Play without a registration change
            return false;
        }

        /// <summary>The key a ContentLink loads by. A miss still gets a composed name, so the name path reports it.</summary>
        internal static string LinkKey(string name, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return name;
            if (s_LinkKeys.TryGetValue(new RefKey(name, sub), out var cached)) return cached;   // no check: the name path looks at the slot first
            return TryLinkName(name, sub, out var composed) ? composed : string.Concat(name, "[", sub, "]");
        }

        /// <summary>An Awaitable that has already completed with <paramref name="value"/> — what a load answers after a logged miss.</summary>
        internal static Awaitable<T> Completed<T>(T value)
        {
            var acs = new AwaitableCompletionSource<T>();
            acs.TrySetResult(value);
            return acs.Awaitable;
        }

        // ───────────── slots ─────────────

        sealed class Slot
        {
            public string Key;
            public int Dir;
            public int Source;       // the directory the engine serves it from: Dir, or one registered after it that carries the asset under another name
            public Loadable<Object> Loadable;
            public ContentStatus Status;
            public int Refs;
            public bool Pending;
            public int ZeroFrame;
            public uint Serial;
            public bool Alive;
            public bool Simulated;   // resolved by the editor simulation, not by a catalog
            public bool ById;        // opened by GetSlotById: listed in s_SlotById under its Loadable's id
            public List<AwaitableCompletionSource<bool>> Waiters;
            public string ResPath, ResSub;   // a Resources slot (Dir == ResourcesDirectory): the path in a Resources folder, the sprite name or null
            public string ResLoadPath;       // the spelling Resources found the path in, when it is not ResPath (LoadResources)
            public string ResGivenPath;      // the path as a name in another form gave it: its file spelling is asked last (ResourcesForms)
            public Object ResTarget;         // what Resources gave; null until loaded and after release
        }

        static readonly List<Slot> s_Slots = new();
        static readonly Stack<int> s_FreeSlots = new();
        static readonly Dictionary<string, int> s_SlotByKey = new(StringComparer.Ordinal);
        static readonly Dictionary<LoadableObjectId, int> s_SlotById = new();   // the slots opened by id, so a hit costs no string
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
            s.Source = dir;
            s.Loadable = loadable;
            s.Status = ContentStatus.None;
            s.Refs = 0;
            s.Pending = false;
            s.Alive = true;
            s.Simulated = false;
            s.ById = false;
            s.ResPath = s.ResSub = s.ResLoadPath = s.ResGivenPath = null;
            s.ResTarget = null;
            s_SlotByKey[key] = si;
            return si;
        }

        static bool TryGetSlotByName(string name, out int si)
        {
            if (name != null && s_SlotByKey.TryGetValue(name, out si)) return true;
            if (OtherForm(name) is string composed)   // another form: slots and the index know the name composed
            {
                if (!TryGetSlotByName(composed, out si)) return false;
                var s = s_Slots[si];
                if (s.Dir == ResourcesDirectory && s.ResGivenPath == null && TrySplitResourcesName(name, out int end, out _))
                    s.ResGivenPath = name.Substring(ResourcesPrefix.Length, end - ResourcesPrefix.Length);
                return true;
            }
            if (name != null && IsResourcesName(name)) return TryOpenResourcesSlot(name, out si);   // a miss only: a hit never looks at the name
            if (!string.IsNullOrEmpty(name) && s_Names.TryGetValue(name, out var e))
            {
                si = AllocSlot(name, e.Dir, new Loadable<Object>(e.Id));
                if (s_DirById.TryGetValue(e.Id, out var source)) s_Slots[si].Source = source;   // the engine serves an id from the most recently registered directory that carries it, chosen as the load starts — here (measured on 6000.6)
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
                    s_SimDirById[simulated.Id] = simulated.Dir;
                    return true;
                }
            }

            var roots = SimulatedRootsDescription();
            Debug.LogError(name != null && (s_SceneNames.ContainsKey(name) || (SceneContainsFallback?.Invoke(name) ?? false))   // a scene the editor serves from the project too
                ? $"[ContentLink] '{name}' is a scene — use LoadSceneAsync."
                : roots != null
                    ? $"[ContentLink] '{name}' is not under {roots} in the project."
                    : $"[ContentLink] '{name}' is not in any registered ContentCatalog.");
            si = -1;
            return false;
        }

        // ───────────── Resources ─────────────

        static bool IsResourcesName(string name) => name.StartsWith(ResourcesPrefix, StringComparison.Ordinal);

        /// <summary>"res:" + a path, and "[sprite]" when it ends with ']' (split at the last '['): a path that ends with ']' is not reachable.</summary>
        static bool TrySplitResourcesName(string name, out int pathEnd, out int subStart)
        {
            int start = ResourcesPrefix.Length;
            pathEnd = name.Length;
            subStart = -1;
            if (name.Length > start && name[name.Length - 1] == ']')
            {
                int open = name.LastIndexOf('[');
                if (open < start || open == name.Length - 2) return false;   // "res:[s0]" or "res:x[]"
                pathEnd = open;
                subStart = open + 1;
            }
            return pathEnd > start;
        }

        static bool IsWellFormedResourcesName(string name) => TrySplitResourcesName(name, out _, out _);

        static bool TryOpenResourcesSlot(string name, out int si)
        {
            if (!TrySplitResourcesName(name, out int pathEnd, out int subStart))
            {
                Debug.LogError($"[ContentLink] '{name}' is not a Resources name: '{ResourcesPrefix}' then a path in a Resources folder, and '[sprite]' for a sprite.");
                si = -1;
                return false;
            }
            si = AllocSlot(name, ResourcesDirectory, null);
            var s = s_Slots[si];
            s.ResPath = name.Substring(ResourcesPrefix.Length, pathEnd - ResourcesPrefix.Length);
            Count(s_ResPathSlots, s.ResPath);
            s.ResSub = subStart < 0 ? null : name.Substring(subStart, name.Length - 1 - subStart);
            return true;
        }

        /// <summary>The slot's object from what Resources gave for its path: the main asset, or the sprite of that name — in the editor, what a player's Resources holds (<see cref="ResourcesFallback"/>).</summary>
        static Object PickResources(Slot s, Object main)
        {
            var player = ResourcesFallback;   // the editor: what a player's Resources holds, not what the editor's finds
            if (player != null) main = player(s.ResGivenPath ?? s.ResPath, null);   // as the name gave it: the editor asks its spellings too (PlayerResources)
            if (main == null) { Debug.LogError($"[ContentLink] '{s.Key}' is not in a Resources folder of this build."); return null; }
            if (s.ResSub == null) return main;
            var sprite = player != null ? player(s.ResGivenPath ?? s.ResPath, s.ResSub) as Sprite : SpriteOf(s.ResPath, s.ResLoadPath ?? s.ResPath, s.ResSub);
            if (sprite != null) return sprite;
            Debug.LogError($"[ContentLink] '{s.Key}': '{s.ResPath}' has no sprite named '{s.ResSub}'.");
            return null;
        }

        /// <summary>
        /// The first sprite of that name a Resources path has. The names are read once — a native getter that makes the string —
        /// and kept while a slot holds the path, until a <see cref="SweepResources"/>: the next sprite slot of the path reads none.
        /// </summary>
        static Sprite SpriteOf(string path, string loadPath, string name)
        {
            if (!s_ResSpritesByPath.TryGetValue(path, out var byName) || (byName.TryGetValue(name, out var kept) && kept == null))   // new, or code outside ContentLink unloaded it: read the path again
            {
                var sprites = Resources.LoadAll<Sprite>(loadPath);   // the texture is loaded now: a warm call
                byName = new Dictionary<string, Sprite>(sprites.Length, StringComparer.Ordinal);
                foreach (var sprite in sprites) byName.TryAdd(ComposedOf(sprite.name), sprite);   // a sprite is named as its file: keyed composed, as the slot's name is
                s_ResSpritesByPath[path] = byName;
            }
            return byName.TryGetValue(name, out var found) ? found : null;
        }

        static async void StartResourcesLoad(Slot s)
        {
            s.Status = ContentStatus.Loading;
            uint serial = s.Serial;
            var request = Resources.LoadAsync(s.ResPath);
            var forms = ResourcesForms(s.ResPath, s.ResGivenPath);   // asked for at once, not in turn: a spelling that misses costs a load a frame (measured on 6000.6)
            var others = forms.Length == 0 ? null : new ResourceRequest[forms.Length];
            for (int i = 0; i < forms.Length; ++i) if (forms[i] != null) others[i] = Resources.LoadAsync(forms[i]);
            try { await Awaitable.FromAsyncOperation(request); }
            catch (Exception e) { Debug.LogException(e); }
            if (!s.Alive || s.Serial != serial || s.Status != ContentStatus.Loading) return;   // a synchronous load completed it first (it never waits for the request)
            var asset = request.asset;
            for (int i = 0; asset == null && i < forms.Length; ++i)   // the first that loads, in LoadResources' order
            {
                if (others[i] == null) continue;
                try { await Awaitable.FromAsyncOperation(others[i]); }
                catch (Exception e) { Debug.LogException(e); }
                if (!s.Alive || s.Serial != serial || s.Status != ContentStatus.Loading) return;
                if ((asset = others[i].asset) != null) s.ResLoadPath = forms[i];
            }
            s.ResTarget = PickResources(s, asset);
            if (!ReferenceEquals(s.ResTarget, null)) Count(s_ResTargetSlots, s.ResTarget);
            FinishLoad(s);
        }

        /// <summary>
        /// Resources.Load in the spelling the player has the path in: Resources finds a path only as the editor that built the player
        /// spelled it, and the macOS editor spells folders decomposed and the file as on disk (measured on 6000.6). A path not in ASCII
        /// that misses is tried in the other spellings (<see cref="ResourcesForms"/>); the one that loads is kept for the slot's sprites.
        /// </summary>
        static Object LoadResources(Slot s)
        {
            var asset = Resources.Load(s.ResPath);
            if (asset != null) return asset;
            foreach (var form in ResourcesForms(s.ResPath, s.ResGivenPath))
                if (form != null && (asset = Resources.Load(form)) != null) { s.ResLoadPath = form; return asset; }
            return null;
        }

        /// <summary>
        /// The other spellings a player may have a Resources path in, in the order they are tried: the folders decomposed (a build made
        /// on macOS), all of it, the file alone (an editor that spells a path as on disk); for a name given in another form, its file as
        /// given under either form of the folders, then the path as given — a file no other spelling reaches (part of its name decomposed,
        /// a compatibility character). Null where one spells the path as given or as one before it. None for an ASCII path.
        /// </summary>
        static string[] ResourcesForms(string path, string given = null)
        {
            if (Ascii(path)) return Array.Empty<string>();
            int slash = path.LastIndexOf('/') + 1;
            string folders = path.Substring(0, slash), file = path.Substring(slash);
            string dFolders = folders.Normalize(NormalizationForm.FormD), dFile = file.Normalize(NormalizationForm.FormD);
            var gFile = given?.Substring(given.LastIndexOf('/') + 1);
            var forms = given == null ? new[] { dFolders + file, dFolders + dFile, folders + dFile } : new[] { dFolders + file, dFolders + dFile, folders + dFile, folders + gFile, dFolders + gFile, given };
            for (int i = 0; i < forms.Length; ++i)
                if (forms[i] == path || Array.IndexOf(forms, forms[i], 0, i) >= 0) forms[i] = null;
            return forms;
        }

        static readonly List<(string Path, Object Target)> s_DeferredUnloads = new();   // what a slot left loaded while another slot held its path — only paths a slot still holds

        sealed class ReferenceComparer : IEqualityComparer<Object>
        {
            public static readonly ReferenceComparer Instance = new();
            public bool Equals(Object a, Object b) => ReferenceEquals(a, b);
            public int GetHashCode(Object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
        }

        // Live Resources slots per path (any case) and per object they hold, kept as slots open, load and close: a release asks
        // them instead of walking every slot. By reference: an object other code unloaded is still its own key.
        static readonly Dictionary<string, int> s_ResPathSlots = new(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<Object, int> s_ResTargetSlots = new(ReferenceComparer.Instance);
        static readonly Dictionary<string, Dictionary<string, Sprite>> s_ResSpritesByPath = new(StringComparer.OrdinalIgnoreCase);   // a held path's sprites by name (SpriteOf)

        static void Count<TKey>(Dictionary<TKey, int> counts, TKey key) => counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

        /// <summary>Takes one off and returns what is left: how many other slots still hold the key.</summary>
        static int Uncount<TKey>(Dictionary<TKey, int> counts, TKey key)
        {
            if (!counts.TryGetValue(key, out var n)) return 0;
            if (--n == 0) counts.Remove(key); else counts[key] = n;
            return n;
        }

        static bool DefersPath(string path)
        {
            for (int i = 0; i < s_DeferredUnloads.Count; ++i)
                if (string.Equals(s_DeferredUnloads[i].Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool IsDeferred(string path, Object target)
        {
            for (int i = 0; i < s_DeferredUnloads.Count; ++i)
                if (ReferenceEquals(s_DeferredUnloads[i].Target, target) && string.Equals(s_DeferredUnloads[i].Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Lets go of a Resources slot's object. Unloaded only when nothing else of ours holds it or its path: two names reach one
        /// object (another case, a nested Resources folder), and a texture unloaded under its live sprite just reloads — so it
        /// waits, and goes when the path's last slot closes.
        /// </summary>
        static void ReleaseResources(Slot s)
        {
            var target = s.ResTarget;
            s.ResTarget = null;
            int othersHoldingTarget = ReferenceEquals(target, null) ? 0 : Uncount(s_ResTargetSlots, target);   // counted off before anything returns
            int othersOnPath = Uncount(s_ResPathSlots, s.ResPath);
            if (othersOnPath == 0) s_ResSpritesByPath.Remove(s.ResPath);   // the path's last slot: its names are read again next time
            if (!UnloadResourcesOnRelease && !DefersPath(s.ResPath)) return;   // the switch off unloads nothing: only what this path still holds back is let go
            bool unloadable = UnloadResourcesOnRelease && target != null && !(target is GameObject || target is Component);   // a prefab is reclaimed by SweepResources only; the switch off leaves everything to it
            bool pathHeld = othersOnPath > 0;
            if (unloadable && othersHoldingTarget == 0)                       // held by another slot: that slot unloads it when it closes
            {
                if (!pathHeld) Resources.UnloadAsset(target);
                else if (!IsDeferred(s.ResPath, target)) s_DeferredUnloads.Add((s.ResPath, target));   // unloaded with the path's last slot — once
            }
            if (pathHeld) return;
            for (int i = s_DeferredUnloads.Count - 1; i >= 0; --i)           // the path's last slot closed: what its siblings left goes now — whatever became of this slot's own; with the switch off it is only dropped (the sweep takes it)
            {
                var (path, deferred) = s_DeferredUnloads[i];
                if (!string.Equals(path, s.ResPath, StringComparison.OrdinalIgnoreCase)) continue;
                s_DeferredUnloads.RemoveAt(i);
                if (UnloadResourcesOnRelease && deferred != null && !s_ResTargetSlots.ContainsKey(deferred)) Resources.UnloadAsset(deferred);
            }
        }

        /// <summary>
        /// Releases the zero-reference assets now, then lets the engine unload whatever nothing references — the only way a
        /// prefab from Resources (and what Resources assets depend on) leaves memory. A pass over every loaded object: call it
        /// where a hitch is fine (a loading screen, a scene change). What waits for a path a slot still holds (a texture under
        /// its sprite) stays, and goes with that path's last slot.
        /// </summary>
        public static AsyncOperation SweepResources()
        {
            Collect(ignoreDelay: true);
            s_ResSpritesByPath.Clear();   // the held paths' names go too: the sweep reclaims their other sprites, and their next sprite slot reads the path again
            return Resources.UnloadUnusedAssets();
        }

        static Object TargetOf(Slot s) => s.Loadable != null ? s.Loadable.Target : s.ResTarget;

        /// <summary>The key of a slot opened by id — diagnostics, TryFindKey, Snapshot. Made once per slot; lookups go through s_SlotById.</summary>
        static string IdKey(LoadableObjectId id) => "#" + id.ToString();

        static int GetSlotById(LoadableObjectId id)
        {
            if (s_SlotById.TryGetValue(id, out var si)) return si;
            // A catalog's id belongs to its directory. An id the simulation answered is served from the project: it
            // belongs to no engine directory (NoDirectory entries stay until ResetStatics) and never blocks one.
            bool simulated = !s_DirById.TryGetValue(id, out var d) && s_SimDirById.TryGetValue(id, out d);
            if (!simulated && !s_DirById.ContainsKey(id)) d = kDirUnknown;
            si = AllocSlot(IdKey(id), d, new Loadable<Object>(id));
            s_Slots[si].Simulated = simulated;
            s_Slots[si].ById = true;
            s_SlotById[id] = si;
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
            try { if (s.Dir == ResourcesDirectory) ReleaseResources(s); else s.Loadable?.Release(); }
            catch (Exception e) { Debug.LogException(e); }

            s_SlotByKey.Remove(s.Key);
            if (s.ById) { s_SlotById.Remove(s.Loadable.LoadableObjectId); s.ById = false; }
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
            if (s.Dir == ResourcesDirectory) { StartResourcesLoad(s); return; }
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
            bool resources = s.Dir == ResourcesDirectory;
            s.Status = (resources ? s.ResTarget != null : s.Loadable.Status == LoadableStatus.Loaded) ? ContentStatus.Loaded : ContentStatus.Failed;
            if (s.Status == ContentStatus.Failed && !resources)   // a Resources miss was logged with its reason
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
            => AcquireAsyncCore<T>(name, default, false, ct, default);

        public static Awaitable<AssetHandle<T>> AcquireAsync<T>(LoadableObjectId id, CancellationToken ct = default) where T : Object
            => AcquireAsyncCore<T>(null, id, true, ct, default);

        /// <summary>A scope's load: the caller's token and the scope's own, each registered directly — no linked source per load.</summary>
        internal static Awaitable<AssetHandle<T>> AcquireAsync<T>(string name, CancellationToken ct, CancellationToken scopeCt) where T : Object
            => AcquireAsyncCore<T>(name, default, false, ct, scopeCt);

        /// <summary>Advanced. <see cref="Acquire{T}(string)"/> through a reference: the same slot a name load of the asset uses.</summary>
        public static AssetHandle<T> Acquire<T>(ContentRef<T> reference) where T : Object
            => ResolveForLoad(reference, out var name) ? Acquire<T>(name) : default;

        /// <summary>Advanced. <see cref="AcquireAsync{T}(string, CancellationToken)"/> through a reference: the same slot a name load of the asset uses.</summary>
        public static Awaitable<AssetHandle<T>> AcquireAsync<T>(ContentRef<T> reference, CancellationToken ct = default) where T : Object
        {
            if (ct.IsCancellationRequested) return AcquireAsync<T>((string)null, ct);   // a cancelled token wins over a miss, as on the name path
            return ResolveForLoad(reference, out var name) ? AcquireAsync<T>(name, ct) : Completed<AssetHandle<T>>(default);
        }

        static AssetHandle<T> AcquireSync<T>(int si) where T : Object
        {
            var s = s_Slots[si];
            var h = NewHandle<T>(si);
            if (s.Status == ContentStatus.None) s.Status = ContentStatus.Loading;
            if (s.Status == ContentStatus.Loading)
            {
                try
                {
                    if (s.Dir == ResourcesDirectory)   // does not wait for an in-flight request: completes the slot itself
                    {
                        s.ResTarget = PickResources(s, LoadResources(s));
                        if (!ReferenceEquals(s.ResTarget, null)) Count(s_ResTargetSlots, s.ResTarget);
                    }
                    else s.Loadable.Load();   // also completes an in-flight async load
                }
                catch (Exception e) { Debug.LogException(e); }
                FinishLoad(s);               // Failed when the engine did not get to Loaded, as on the async path
            }
            if (s.Status != ContentStatus.Loaded) { h.Dispose(); return default; }
            return h;
        }

        static readonly Action<object> s_CancelWaiter = static state => ((AwaitableCompletionSource<bool>)state).TrySetCanceled();

        static async Awaitable<AssetHandle<T>> AcquireAsyncCore<T>(string name, LoadableObjectId id, bool byId, CancellationToken ct, CancellationToken scopeCt) where T : Object
        {
            ct.ThrowIfCancellationRequested();
            scopeCt.ThrowIfCancellationRequested();

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
                using (ct.CanBeCanceled ? ct.Register(s_CancelWaiter, acs) : default)   // a static delegate with state: no closure per waiter
                using (scopeCt.CanBeCanceled ? scopeCt.Register(s_CancelWaiter, acs) : default)
                {
                    try { await acs.Awaitable; }
                    catch (OperationCanceledException) { h.Dispose(); ct.ThrowIfCancellationRequested(); scopeCt.ThrowIfCancellationRequested(); throw; }   // the waiter's cancellation carries no token: rethrow with the one that cancelled
                }
            }

            if (!h.IsValid) return default;
            if (ct.IsCancellationRequested || scopeCt.IsCancellationRequested) { h.Dispose(); ct.ThrowIfCancellationRequested(); scopeCt.ThrowIfCancellationRequested(); }
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
                var t = TargetOf(s);
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
            var target = TargetOf(s);
            if (target is T t) return t;
            if (target is GameObject go && typeof(Component).IsAssignableFrom(typeof(T)))
                return go.GetComponent(typeof(T)) as T;
            return null;
        }

        internal static void LogTypeMismatch<T>(int li, uint gen) where T : Object
        {
            if (!IsLeaseAlive(li, gen)) return;
            var s = s_Slots[s_Leases[li].Slot];
            var target = TargetOf(s);
            Debug.LogError(s.Status == ContentStatus.Loaded && target == null   // a loaded slot's object is null only once it was unloaded under its holder
                ? $"[ContentLink] '{s.Key}' was unloaded while held — code destroyed or unloaded it, or a directory it came from was unregistered: let go of every reference to it, and it loads again after the next Collect."
                : $"[ContentLink] '{s.Key}' is {(target != null ? target.GetType().Name : "null")}, not {typeof(T).Name}.");
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
        ///                             simulated scene has no id so the id-based Claim finds nothing, so the editor
        ///                             matches the scene by the path it asked for. Called once for every load the
        ///                             fallback started, when its operation completes — a cancelled one too.
        /// All null in a player. Not cleared by ResetStatics, for the reason NameFallback is not.
        /// </summary>
        public static Func<string, bool> SceneContainsFallback;
        public static Func<string, LoadSceneParameters, AsyncOperation> SceneFallback;
        public static Func<string, Scene> SceneResultFallback;

        /// <summary>
        /// Set by the editor: adds the project path of each simulated scene load still in flight — what UnregisterDirectory refuses a
        /// directory served from the project for, as it refuses a built one for its own scene loads. Null in a player. Not cleared by
        /// ResetStatics, for the reason NameFallback is not.
        /// </summary>
        public static Action<List<string>> SceneLoadsInFlightFallback;
        static readonly List<string> s_PathsInFlight = new();
        static bool s_Quitting;   // Shutdown's directory pass: a simulated scene load holds no engine directory (SceneLoadsInFlight)

        public static bool ContainsScene(string name)
        {
            if (name == null || IsResourcesName(name)) return false;
            if (s_SceneNames.ContainsKey(name)) return true;
            if (OtherForm(name) is string composed) return ContainsScene(composed);
            var known = SceneContainsFallback;
            return known != null && known(name);
        }

        /// <summary>Resolves a catalog scene name to its LoadableSceneId. False if no registered directory has the name.</summary>
        public static bool TryGetLoadableSceneId(string name, out LoadableSceneId id)
        {
            if (name != null && s_SceneNames.TryGetValue(name, out var e)) { id = e.Id; return true; }
            if (OtherForm(name) is string composed) return TryGetLoadableSceneId(composed, out id);
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
            => LoadSceneAsync(name, parameters, ct, default);

        /// <summary>A scope's scene load: the caller's token and the scope's own, each registered directly — no linked source per load.</summary>
        internal static Awaitable<Scene> LoadSceneAsync(string name, LoadSceneParameters parameters, CancellationToken ct, CancellationToken scopeCt)
        {
            if (ct.IsCancellationRequested || scopeCt.IsCancellationRequested) return LoadSceneCore(name, default, parameters, ct, scopeCt);   // a cancelled token wins over a miss, as on the asset path
            if (name != null && s_SceneNames.TryGetValue(name, out var e)) return LoadSceneCore(name, e.Id, parameters, ct, scopeCt);
            if (OtherForm(name) is string composed) return LoadSceneAsync(composed, parameters, ct, scopeCt);   // scenes are indexed composed
            if (name != null && SceneFallback != null) return LoadSimulatedSceneCore(name, parameters, ct, scopeCt);   // a catalog name wins over the simulation
            Debug.LogError($"[ContentLink] scene '{name}' is not in any registered ContentCatalog.");
            return InvalidScene();
        }

        public static Awaitable<Scene> LoadSceneAsync(LoadableSceneId id, LoadSceneParameters parameters, CancellationToken ct = default)
            => LoadSceneCore(SceneKey(id), id, parameters, ct, default);

        /// <summary><see cref="LoadSceneAsync(string, LoadSceneMode, CancellationToken)"/> through a scene reference.</summary>
        public static Awaitable<Scene> LoadSceneAsync(ContentSceneRef reference, LoadSceneMode mode = LoadSceneMode.Additive, CancellationToken ct = default)
            => LoadSceneAsync(reference, new LoadSceneParameters(mode), ct);

        public static Awaitable<Scene> LoadSceneAsync(ContentSceneRef reference, LoadSceneParameters parameters, CancellationToken ct = default)
        {
            if (ct.IsCancellationRequested) return LoadSceneAsync((string)null, parameters, ct);   // a cancelled token wins over a miss, as on the name path
            return ResolveSceneForLoad(reference, out var name) ? LoadSceneAsync(name, parameters, ct) : InvalidScene();
        }

        static Awaitable<Scene> InvalidScene()
        {
            var acs = new AwaitableCompletionSource<Scene>();
            acs.TrySetResult(default);
            return acs.Awaitable;
        }

        /// <summary>
        /// The editor-simulation twin of <see cref="LoadSceneCore"/>. The editor starts the load and
        /// names the resulting Scene; everything else — the cancellation shape, the error wording, the tracking — matches, so a
        /// caller cannot tell the two apart except that the scene has no LoadableSceneId.
        /// </summary>
        static async Awaitable<Scene> LoadSimulatedSceneCore(string key, LoadSceneParameters parameters, CancellationToken ct, CancellationToken scopeCt)
        {
            ct.ThrowIfCancellationRequested();
            scopeCt.ThrowIfCancellationRequested();

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
                using (ct.CanBeCanceled ? ct.Register(s_CancelWaiter, acs) : default)
                using (scopeCt.CanBeCanceled ? scopeCt.Register(s_CancelWaiter, acs) : default)
                    await acs.Awaitable;
            }
            catch (OperationCanceledException)
            {
                if (parameters.loadSceneMode == LoadSceneMode.Additive)
                    op.completed += _ => { var late = TrackSimulated(key); if (late.IsValid()) _ = TryUnloadScene(late); };
                else
                {
                    Debug.LogWarning($"[ContentLink] load of scene '{key}' was cancelled after a Single-mode load started; the scene stays loaded.");
                    op.completed += _ => TrackSimulated(key);   // the scene stays loaded, and tracked, as a built one is
                }
                ct.ThrowIfCancellationRequested();   // the waiter's cancellation carries no token: rethrow with the one that cancelled
                scopeCt.ThrowIfCancellationRequested();
                throw;
            }

            var scene = TrackSimulated(key);
            if (!scene.IsValid()) Debug.LogError($"[ContentLink] failed to load scene '{key}'.");
            return scene;
        }

        /// <summary>
        /// The Scene a simulated load produced, tracked: OnSceneLoaded ignores it (a simulated scene has no id), so it is added here — a
        /// cancelled load's too, as a built scene is tracked whoever loaded it — so that the scope that asked for it can unload it and the
        /// open-scene guard finds the directory served from the project that covers its file (Holds).
        /// </summary>
        static Scene TrackSimulated(string key)
        {
            var scene = SceneResultFallback?.Invoke(key) ?? default;
            if (scene.IsValid()) s_LoadedScenes.Add(new LoadedScene { Scene = scene, Id = default, Key = key, Claimed = true });
            return scene;
        }

        static async Awaitable<Scene> LoadSceneCore(string key, LoadableSceneId id, LoadSceneParameters parameters, CancellationToken ct, CancellationToken scopeCt)
        {
            ct.ThrowIfCancellationRequested();
            scopeCt.ThrowIfCancellationRequested();
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
                using (ct.CanBeCanceled ? ct.Register(s_CancelWaiter, acs) : default)
                using (scopeCt.CanBeCanceled ? scopeCt.Register(s_CancelWaiter, acs) : default)
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
                ct.ThrowIfCancellationRequested();   // the waiter's cancellation carries no token: rethrow with the one that cancelled
                scopeCt.ThrowIfCancellationRequested();
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

        /// <summary>
        /// Whether a scene load from the directory is still in flight — what <see cref="UnregisterDirectory"/> refuses it for, forced or
        /// not: a load ContentLink started that the engine has not finished, a cancelled one's too; for a directory served from the
        /// project, a simulated load of a file it covers. False for a directory not registered. Wait for false before unregistering.
        /// </summary>
        public static bool HasSceneLoadsInFlight(int dir) => (uint)dir < (uint)s_Dirs.Count && s_Dirs[dir].Alive && SceneLoadsInFlight(dir, null);

        /// <summary>
        /// The scene loads from the directory still in flight (<see cref="Flight"/>) and, for a directory served from the project, the
        /// simulated loads of the files it covers (the editor's answer, as Holds covers an open one) — each one's name added to names
        /// when given. The simulated ones do not count during Shutdown: a directory served from the project holds no engine directory.
        /// </summary>
        static bool SceneLoadsInFlight(int dir, StringBuilder names)
        {
            bool any = false;
            foreach (var kv in s_Flights)
                if (s_DirsBySceneId.TryGetValue(kv.Key, out var dirs) && dirs.Contains(dir))
                {
                    if (names == null) return true;
                    any = true; names.Append("\n  ").Append(SceneKey(kv.Key));
                }
            var rec = s_Dirs[dir];
            var simulated = SceneLoadsInFlightFallback;
            if (!rec.Simulated || simulated == null || s_Quitting) return any;
            s_PathsInFlight.Clear();
            simulated(s_PathsInFlight);
            foreach (var path in s_PathsInFlight)
                if (Covers(rec, NormalizePath(path)))
                {
                    if (names == null) return true;
                    any = true; names.Append("\n  ").Append(path);
                }
            return any;
        }

        static bool HasLoadedScenes(int dir)
        {
            foreach (var ls in s_LoadedScenes)
                if (s_DirsBySceneId.TryGetValue(ls.Id, out var dirs) && dirs.Contains(dir)) return true;
            return false;
        }

        /// <summary>
        /// The directory holds the loaded scene: its catalog carries the scene's id, or — a scene the editor opened from the project for a
        /// directory it serves (no id) — its covered folders hold the scene's file, which is what the built directory would carry.
        /// </summary>
        static bool Holds(int dir, in LoadedScene ls)
        {
            if (s_DirsBySceneId.TryGetValue(ls.Id, out var dirs)) return dirs.Contains(dir);
            if (ls.Id.IsValid) return false;
            var rec = s_Dirs[dir];
            return rec.Alive && rec.Simulated && Covers(rec, NormalizePath(ls.Scene.path));
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
            public readonly int Dir;      // the directory the asset belongs to (a directory registered after it that gave the asset blocks unregistering too), NoDirectory when unknown, ResourcesDirectory for a res: name
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
                results.Add(new SceneInfo(ls.Key, FirstHolder(ls), ls.Scene));
        }

        static int FirstHolder(in LoadedScene ls)
        {
            if (s_DirsBySceneId.TryGetValue(ls.Id, out var dirs)) return dirs.Count > 0 ? dirs[0] : kDirUnknown;
            if (ls.Id.IsValid) return kDirUnknown;
            for (int d = 0; d < s_Dirs.Count; ++d) if (Holds(d, ls)) return d;
            return kDirUnknown;
        }

        // ───────────── lifetime ─────────────

        /// <summary>
        /// In the editor with domain reload disabled, content directories that Shutdown left registered (a scene from
        /// them was still open at quit, or a load from them still in flight) survive in the engine and would make the next Play session's
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

        /// <summary>
        /// Disposes the scope tree, returns the remaining raw leases — reporting each, but not a load still in flight: its caller has
        /// no handle yet — and unregisters every directory it can. One with a scene still open or a load still in flight cannot be
        /// released now; the next session start lets it go (<see cref="ReleaseLeftoverDirectories"/>), a player's process with it.
        /// </summary>
        public static void Shutdown()
        {
            ContentScope.ShutdownRoot();
            for (int i = 0; i < s_LeaseHigh; ++i)
                if (s_Leases[i].Active)
                {
                    if (s_Slots[s_Leases[i].Slot].Status != ContentStatus.Loading)   // a load still in flight is not a leak: its caller has no handle yet
                        Debug.LogWarning($"[ContentLink] leaked raw AssetHandle at shutdown: '{s_Slots[s_Leases[i].Slot].Key}'\n{s_Leases[i].Trace}");
                    Release(i, s_Leases[i].Gen);
                }
            Collect(ignoreDelay: true);
            for (int i = s_LoadedScenes.Count - 1; i >= 0; --i)
                if (!s_LoadedScenes[i].Id.IsValid) s_LoadedScenes.RemoveAt(i);   // a simulated scene holds no engine directory: its directory goes with the rest
            s_Quitting = true;
            try
            {
                for (int d = 0; d < s_Dirs.Count; ++d)
                    if (s_Dirs[d].Alive && !HasLoadedScenes(d) && !HasLoadsInFlight(d)) UnregisterDirectory(d, force: true);   // open scenes: the engine tears them down · loads in flight: let go at the next session (measured)
            }
            finally { s_Quitting = false; }
            s_LoadedScenes.Clear();
        }

        /// <summary>A load from the directory still in flight — an asset's (the slots <see cref="UnregisterDirectory"/> counts as the directory's) or a scene's: what it refuses even when forced.</summary>
        static bool HasLoadsInFlight(int dir)
        {
            if (HasSceneLoadsInFlight(dir)) return true;
            foreach (var s in s_Slots)
                if (s.Alive && s.Status == ContentStatus.Loading && (s.Dir == dir || s.Source == dir || (s.Dir == kDirUnknown && !s.Simulated))) return true;
            return false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()   // runs on every Play entry, even with domain reload disabled
        {
            ReleaseLeftoverDirectories();
            s_Dirs.Clear(); s_Mounting.Clear(); s_Names.Clear(); s_DirById.Clear(); s_SimDirById.Clear();
            s_Slots.Clear(); s_FreeSlots.Clear(); s_SlotByKey.Clear(); s_SlotById.Clear(); s_Pending.Clear();
            s_Leases = new Lease[256]; s_LeaseHigh = 0; s_FreeLeases.Clear();
            s_SceneNames.Clear(); s_DirsBySceneId.Clear(); s_SceneKeyById.Clear(); s_LoadedScenes.Clear(); s_Flights.Clear();
            s_NameByRef.Clear(); s_SceneNameByGuid.Clear(); s_RefIndexDirty = false;
            s_LinkKeys.Clear(); s_LinkMisses.Clear(); s_ComposedKeys.Clear(); s_TwinsWarned.Clear();
            s_DeferredUnloads.Clear(); s_ResPathSlots.Clear(); s_ResTargetSlots.Clear(); s_ResSpritesByPath.Clear();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
            Application.quitting -= Shutdown;
            Application.quitting += Shutdown;
        }
    }
}
