using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink
{
    public enum ContentScopeKind : byte { Root, Scene, GameObject, Explicit }

    /// <summary>
    /// A lifetime that owns loaded assets. Everything loaded into a scope is released when the scope ends.
    ///
    /// Every scope hangs off a parent that ends automatically, so a forgotten Dispose is bounded, never infinite:
    ///   Root       — Application.quitting
    ///   Scene      — SceneManager.sceneUnloaded                    (parent: Root)
    ///   GameObject — OnDestroy of its ContentScopeOwner            (parent: its scene)
    ///   Explicit   — your Dispose(); otherwise cascaded by parent  (parent: any)
    ///
    /// Leak signal: an explicit scope that is reclaimed by a scene unload or by quit (instead of its own Dispose,
    /// a disposed parent, or its owner GameObject being destroyed) logs a warning with its path.
    /// Release(key) is optional and only lowers peak memory (lists, equipment swaps).
    /// </summary>
    public sealed class ContentScope : IDisposable
    {
        /// <summary>Warn once when a scope holds more than this many entries (0 = off). Over-retention detector. Counts entries, so one whose lease a forced unregistration killed counts until it is dropped.</summary>
        public static int DefaultWarnThreshold = 0;

        // ───────────── tree ─────────────

        static ContentScope s_Root;
        static readonly Dictionary<ulong, ContentScope> s_Scenes = new();

        public static ContentScope Root => s_Root ??= new ContentScope("Root", ContentScopeKind.Root, null);

        /// <summary>Scope that lives as long as the given scene.</summary>
        public static ContentScope ForScene(Scene scene)
        {
            if (!scene.IsValid()) return Root;
            ulong key = scene.handle.GetRawData();
            if (!s_Scenes.TryGetValue(key, out var s) || s.IsDisposed)
            {
                var name = scene.name;   // a native getter: every read is a new string, so read it once
                s = new ContentScope(string.IsNullOrEmpty(name) ? $"Scene#{scene.handle}" : name, ContentScopeKind.Scene, Root);
                s.m_SceneHandle = key;
                s_Scenes[key] = s;
            }
            return s;
        }

        internal static ContentScope CreateForGameObject(GameObject go)
        {
            var s = new ContentScope(go.name, ContentScopeKind.GameObject, ForScene(go.scene));
            s.m_Owner = go;
            return s;
        }

        // ───────────── state ─────────────

        readonly Dictionary<string, AssetHandle<Object>> m_Keyed = new(StringComparer.Ordinal);
        readonly HashSet<long> m_Attached = new();
        readonly List<ContentScope> m_Children = new();
        int m_IndexInParent = -1;                              // where the parent keeps me; -1 when I have no parent
        static bool s_IndexWarned;                             // the recovery in RemoveChild is reported once per process
        List<Scene> m_Scenes;   // scenes owned by this scope: unloaded when the scope ends
        CancellationTokenSource m_Cts;
        GameObject m_Owner;
        ContentScopeOwner m_OwnerComponent;
        ulong m_SceneHandle;
        bool m_Warned;
        readonly StackTrace m_CreatedAt;

        public string Name { get; }
        public ContentScopeKind Kind { get; }
        public ContentScope Parent { get; private set; }
        public bool IsDisposed { get; private set; }
        public int WarnThreshold { get; set; }
        /// <summary>Live references held. Entries whose lease a forced unregistration killed are not counted (they are dropped when next touched).</summary>
        public int Count
        {
            get
            {
                int n = 0;
                foreach (var h in m_Keyed.Values) if (h.IsValid) ++n;
                foreach (var packed in m_Attached) { Unpack(packed, out var li, out var gen); if (ContentService.IsLeaseAlive(li, gen)) ++n; }
                return n;
            }
        }
        public int SceneCount => m_Scenes?.Count ?? 0;
        /// <summary>The most entries the scope has held at once — live or not, unlike <see cref="Count"/>.</summary>
        public int PeakCount { get; private set; }
        /// <summary>The children in the order the scope holds them — not creation order: removing a child moves the last sibling into its slot.</summary>
        public IReadOnlyList<ContentScope> Children => m_Children;
        public IEnumerable<string> Keys => m_Keyed.Keys;
        public string Path => Parent == null ? Name : $"{Parent.Path}/{Name}";

        /// <summary>Cancelled when the scope ends. In-flight loads for this scope stop waiting.</summary>
        public CancellationToken DisposedToken => IsDisposed ? new CancellationToken(true) : (m_Cts ??= new CancellationTokenSource()).Token;

        ContentScope(string name, ContentScopeKind kind, ContentScope parent)
        {
            Name = name;
            Kind = kind;
            Parent = parent;
            WarnThreshold = DefaultWarnThreshold;
            if (kind == ContentScopeKind.Explicit && ContentService.CaptureStackTrace) m_CreatedAt = new StackTrace(2, true);
            parent?.AddChild(this);
        }

        void AddChild(ContentScope c) { c.m_IndexInParent = m_Children.Count; m_Children.Add(c); }

        /// <summary>O(1): the child says where it is, the last sibling takes the slot. A stale index (a Children list changed behind the scope's back) is recovered by search.</summary>
        void RemoveChild(ContentScope c)
        {
            int i = c.m_IndexInParent, last = m_Children.Count - 1;
            if (i < 0 || i > last || m_Children[i] != c)
            {
                if (!s_IndexWarned) { s_IndexWarned = true; Debug.LogWarning("[ContentLink] a scope's child index was stale; recovered by search (something changed a Children list behind the scope's back)."); }
                i = m_Children.IndexOf(c); if (i < 0) return;
            }
            if (i != last) { var moved = m_Children[last]; m_Children[i] = moved; moved.m_IndexInParent = i; }
            m_Children.RemoveAt(last);
            c.m_IndexInParent = -1;
        }

        /// <summary>Creates a child scope (stage, popup, cell, ...). Dispose it when that lifetime ends.</summary>
        public ContentScope CreateChild(string name)
        {
            ThrowIfDisposed();
            return new ContentScope(name, ContentScopeKind.Explicit, this);
        }

        // ───────────── loading ─────────────

        /// <summary>
        /// Loads an asset into this scope and returns the asset itself (no handle to release).
        /// The same key is held at most once per scope. Returns null on failure.
        /// Throws OperationCanceledException if ct is cancelled or the scope ends while loading.
        /// </summary>
        public async Awaitable<T> LoadAsync<T>(string key, CancellationToken ct = default) where T : Object
        {
            ThrowIfDisposed();
            if (TryGetKeyed<T>(key, out var cached)) return cached;

            var h = await ContentService.AcquireAsync<Object>(key, ct, DisposedToken);
            return Keep<T>(key, h);
        }

        /// <summary>Synchronous variant of LoadAsync (blocks while loading).</summary>
        public T Load<T>(string key) where T : Object
        {
            ThrowIfDisposed();
            if (TryGetKeyed<T>(key, out var cached)) return cached;
            return Keep<T>(key, ContentService.Acquire<Object>(key));
        }

        /// <summary>
        /// Loads a prefab and instantiates it. The prefab lease belongs to the new instance (its own GameObject scope),
        /// not to this scope: the instance keeps its assets alive until it is destroyed, and clones made later with
        /// Object.Instantiate inherit the lease. This scope only cancels the operation if it ends first.
        /// Scopes never destroy GameObjects.
        /// </summary>
        public async Awaitable<GameObject> InstantiateAsync(string key, Transform parent = null, bool worldPositionStays = false, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var h = await ContentService.AcquireAsync<GameObject>(key, ct, DisposedToken);
            if (!h.IsValid) return null;

            GameObject go;
            try { go = Object.Instantiate(h.Asset, parent, worldPositionStays); }
            catch { h.Dispose(); throw; }

            go.Scope().KeepHandle(key, h.As<Object>());
            return go;
        }

        /// <summary>Returns one asset early. Optional: everything is returned when the scope ends anyway.</summary>
        public bool Release(string key)
        {
            if (key == null || !m_Keyed.TryGetValue(key, out var h)) return false;
            m_Keyed.Remove(key);
            bool live = h.IsValid;   // a lease a forced unregistration killed is dropped quietly: nothing to release, no warning
            if (live) h.Dispose();
            OnKeysChanged();
            return live;
        }

        public bool Contains(string key) => key != null && m_Keyed.TryGetValue(key, out var h) && h.IsValid;

        // ───────────── scenes ─────────────

        /// <summary>
        /// Loads a content-directory scene additively and binds it to this scope: when the scope ends the scene is
        /// unloaded (unless it is the last loaded scene, or the application is quitting). The scene's own
        /// ContentScope.ForScene stays under Root and ends with the scene, as for any scene.
        /// Returns an invalid Scene on failure; throws OperationCanceledException if ct is cancelled or the scope ends first.
        /// </summary>
        public async Awaitable<Scene> LoadSceneAsync(string name, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            var scene = await ContentService.LoadSceneAsync(name, new LoadSceneParameters(LoadSceneMode.Additive), ct, DisposedToken);
            if (!scene.IsValid()) return scene;
            if (IsDisposed) { _ = ContentService.TryUnloadScene(scene); return default; }
            (m_Scenes ??= new List<Scene>()).Add(scene);
            return scene;
        }

        /// <summary>Unloads one owned scene early. Optional: owned scenes are unloaded when the scope ends anyway.</summary>
        public bool ReleaseScene(Scene scene)
        {
            if (!Untrack(scene)) return false;
            _ = ContentService.TryUnloadScene(scene);
            return true;
        }

        internal AsyncOperation ReleaseSceneOp(Scene scene) => Untrack(scene) ? ContentService.TryUnloadScene(scene) : null;

        bool Untrack(Scene scene)
        {
            if (m_Scenes == null) return false;
            for (int i = 0; i < m_Scenes.Count; ++i)
                if (m_Scenes[i].handle == scene.handle) { m_Scenes.RemoveAt(i); return true; }
            return false;
        }

        // ───────────── ownership transfer ─────────────

        /// <summary>Gives <paramref name="target"/> its own reference to an asset held by this scope.</summary>
        public bool ShareTo(ContentScope target, string key)
        {
            if (target == null || target.IsDisposed || !m_Keyed.TryGetValue(key, out var h) || !h.IsValid) return false;
            if (target.Contains(key)) return true;
            target.KeepHandle(key, h.Share());
            return true;
        }

        /// <summary>
        /// Takes a reference to an asset that some other scope loaded, identified by the object itself
        /// (e.g. a Sprite handed over by another system). Linear search; call on hand-over, not per frame.
        /// </summary>
        public bool Adopt(Object asset)
        {
            ThrowIfDisposed();
            if (!ContentService.TryFindKey(asset, out var key)) return false;
            if (Contains(key)) return true;
            if (!ContentService.TryShareLive(key, out var h)) return false;
            KeepHandle(key, h);
            return true;
        }

        /// <summary>Advanced. Transfers a raw handle into this scope. Do not dispose it yourself afterwards.</summary>
        public void Attach<T>(AssetHandle<T> handle) where T : Object
        {
            if (!handle.IsValid) return;
            if (IsDisposed) { handle.Dispose(); return; }
            AttachLease(handle.Lease, handle.Gen);
        }

        // ───────────── references (ContentRef / ContentSceneRef) ─────────────
        //
        // A reference resolves to its catalog name and takes the name overload above, so it holds the same key a name load
        // does: Contains("Icons/sword") is true after LoadAsync(ref), and Release(ref) also returns a name load of that asset.
        // A load through a reference no catalog carries is logged with the reference and gives null (an invalid Scene).

        /// <summary><see cref="LoadAsync{T}(string, CancellationToken)"/> through a reference.</summary>
        public Awaitable<T> LoadAsync<T>(ContentRef<T> reference, CancellationToken ct = default) where T : Object
        {
            if (IsDisposed || ct.IsCancellationRequested)   // the name overload reports these its usual way
            {
                ContentService.TryGetName(reference, out var known);
                return LoadAsync<T>(known, ct);
            }
            return ContentService.ResolveForLoad(reference, out var key) ? LoadAsync<T>(key, ct) : ContentService.Completed<T>(null);
        }

        /// <summary><see cref="Load{T}(string)"/> through a reference.</summary>
        public T Load<T>(ContentRef<T> reference) where T : Object
        {
            ThrowIfDisposed();
            return ContentService.ResolveForLoad(reference, out var key) ? Load<T>(key) : null;
        }

        /// <summary>
        /// <see cref="InstantiateAsync(string, Transform, bool, CancellationToken)"/> through a reference. T may be a component
        /// type: the catalog entry is the prefab either way, and the instance is returned as a GameObject.
        /// </summary>
        public Awaitable<GameObject> InstantiateAsync<T>(ContentRef<T> reference, Transform parent = null, bool worldPositionStays = false, CancellationToken ct = default) where T : Object
        {
            if (IsDisposed || ct.IsCancellationRequested)
            {
                ContentService.TryGetName(reference, out var known);
                return InstantiateAsync(known, parent, worldPositionStays, ct);
            }
            return ContentService.ResolveForLoad(reference, out var key) ? InstantiateAsync(key, parent, worldPositionStays, ct) : ContentService.Completed<GameObject>(null);
        }

        /// <summary><see cref="Release(string)"/> through a reference — also returns a name load of the same asset.</summary>
        public bool Release<T>(ContentRef<T> reference) where T : Object
            => ContentService.TryGetName(reference, out var key) && Release(key);

        public bool Contains<T>(ContentRef<T> reference) where T : Object
            => ContentService.TryGetName(reference, out var key) && Contains(key);

        /// <summary><see cref="ShareTo(ContentScope, string)"/> through a reference.</summary>
        public bool ShareTo<T>(ContentScope target, ContentRef<T> reference) where T : Object
            => ContentService.TryGetName(reference, out var key) && ShareTo(target, key);

        /// <summary><see cref="LoadSceneAsync(string, CancellationToken)"/> through a scene reference.</summary>
        public Awaitable<Scene> LoadSceneAsync(ContentSceneRef reference, CancellationToken ct = default)
        {
            if (IsDisposed)
            {
                ContentService.TryGetSceneName(reference, out var known);
                return LoadSceneAsync(known, ct);
            }
            return ContentService.ResolveSceneForLoad(reference, out var name) ? LoadSceneAsync(name, ct) : ContentService.Completed<Scene>(default);
        }

        // ───────────── dispose ─────────────

        public void Dispose()
        {
            if (Kind == ContentScopeKind.Root)
            {
                Debug.LogError("[ContentLink] Root scope is disposed only at application quit.");
                return;
            }
            DisposeCore(this);
        }

        /// <param name="initiator">The scope whose ending started this cascade.</param>
        internal void DisposeCore(ContentScope initiator)
        {
            if (IsDisposed) return;
            IsDisposed = true;
            m_Cts?.Cancel();

            for (int i = m_Children.Count - 1; i >= 0; --i)
            {
                if (i >= m_Children.Count) continue;
                var c = m_Children[i];

                // An object moved to another scene (MoveGameObjectToScene / DontDestroyOnLoad) survives this scene:
                // hand its scope over to the scene it lives in now instead of pulling its assets out from under it.
                if (Kind == ContentScopeKind.Scene && c.Kind == ContentScopeKind.GameObject &&
                    c.m_Owner != null && c.m_Owner.scene.handle.GetRawData() != m_SceneHandle && !Root.IsDisposed)
                {
                    RemoveChild(c);
                    var np = ForScene(c.m_Owner.scene);
                    c.Parent = np;
                    np.AddChild(c);
                    continue;
                }
                c.DisposeCore(initiator);   // also covers objects destroyed without OnDestroy (never activated)
            }
            m_Children.Clear();   // whatever is still listed is disposed (a duplicate, or a child whose disposal threw), and so is this scope

            bool orphaned = Kind == ContentScopeKind.Explicit && initiator != this &&
                            (initiator.Kind == ContentScopeKind.Scene || initiator.Kind == ContentScopeKind.Root);
            int released = orphaned ? Count : 0;   // the walk only when the leak warning below will print it
            foreach (var h in m_Keyed.Values) if (h.IsValid) h.Dispose();
            foreach (var packed in m_Attached)
            {
                Unpack(packed, out var li, out var gen);
                if (ContentService.IsLeaseAlive(li, gen)) ContentService.Release(li, gen);
            }
            m_Keyed.Clear();
            m_Attached.Clear();

            if (m_Scenes != null)
            {
                if (initiator.Kind != ContentScopeKind.Root)   // quitting: the engine tears the scenes down itself
                    foreach (var sc in m_Scenes) _ = ContentService.TryUnloadScene(sc);
                m_Scenes.Clear();
            }

            // Leak signal: an explicit scope reclaimed by the safety net (scene unload / quit).
            // Ending with an explicitly disposed parent or a destroyed owner GameObject is a normal lifetime.
            if (orphaned)
                Debug.LogWarning($"[ContentLink] scope '{Path}' was not disposed; released {released} asset(s) when '{initiator.Name}' ended.\n{m_CreatedAt}");

            Parent?.RemoveChild(this);
            if (Kind == ContentScopeKind.Scene && s_Scenes.TryGetValue(m_SceneHandle, out var cur) && cur == this)
                s_Scenes.Remove(m_SceneHandle);
            if (m_OwnerComponent != null) m_OwnerComponent.OnScopeDisposed(this);
        }

        // ───────────── internals ─────────────

        bool TryGetKeyed<T>(string key, out T asset) where T : Object
        {
            asset = null;
            if (key == null || !m_Keyed.TryGetValue(key, out var h)) return false;
            if (!h.IsValid) { m_Keyed.Remove(key); return false; }
            asset = ContentService.GetAsset<T>(h.Lease, h.Gen);
            if (asset == null) ContentService.LogTypeMismatch<T>(h.Lease, h.Gen);
            return true;
        }

        T Keep<T>(string key, AssetHandle<Object> h) where T : Object
        {
            if (!h.IsValid) return null;
            if (IsDisposed) { h.Dispose(); throw new OperationCanceledException($"scope '{Path}' ended while loading '{key}'"); }
            if (ContentService.GetAsset<T>(h.Lease, h.Gen) == null) { ContentService.LogTypeMismatch<T>(h.Lease, h.Gen); h.Dispose(); return null; }
            KeepHandle(key, h);
            return m_Keyed.TryGetValue(key, out var kept) ? ContentService.GetAsset<T>(kept.Lease, kept.Gen) : null;
        }

        internal void KeepHandle(string key, AssetHandle<Object> h)
        {
            if (IsDisposed) { h.Dispose(); return; }
            if (m_Keyed.TryGetValue(key, out var prev) && prev.IsValid) { h.Dispose(); return; }  // dedupe
            m_Keyed[key] = h;
            OnKeysChanged();
        }

        internal void AttachLease(int li, uint gen)
        {
            m_Attached.Add(Pack(li, gen));
            OnCountChanged();
        }

        internal void DetachLease(int li, uint gen) => m_Attached.Remove(Pack(li, gen));

        void OnKeysChanged()
        {
            OnCountChanged();
            ResyncKeys();
        }

        void OnCountChanged()
        {
            int c = m_Keyed.Count + m_Attached.Count;   // O(1): the entries, live or not — an upper bound on Count that a forced unregistration inflates until its dead entries are dropped
            if (c > PeakCount) PeakCount = c;
            if (WarnThreshold > 0 && c > WarnThreshold && !m_Warned)
            {
                m_Warned = true;
                Debug.LogWarning($"[ContentLink] scope '{Path}' holds {c} assets (threshold {WarnThreshold}). " +
                                 "Consider Release(key) or a child scope per item.");
            }
        }

        internal void BindOwnerComponent(ContentScopeOwner owner) => m_OwnerComponent = owner;
        internal void ResyncKeys() { if (m_OwnerComponent != null) m_OwnerComponent.SyncKeys(m_Keyed.Keys); }

        void ThrowIfDisposed()
        {
            if (IsDisposed) throw new ObjectDisposedException($"ContentScope '{Path}'");
        }

        static long Pack(int li, uint gen) => ((long)li << 32) | gen;
        static void Unpack(long p, out int li, out uint gen) { li = (int)(p >> 32); gen = (uint)(p & 0xFFFFFFFF); }

        // ───────────── diagnostics ─────────────

        /// <summary>Human-readable dump of the scope tree with counts and keys.</summary>
        public static string DumpTree()
        {
            var sb = new StringBuilder();
            Dump(Root, sb, 0);
            return sb.ToString();
        }

        static void Dump(ContentScope s, StringBuilder sb, int depth)
        {
            sb.Append(' ', depth * 2).Append(s.Name).Append(" [").Append(s.Kind).Append("] ")
              .Append(s.Count).Append(" (peak ").Append(s.PeakCount).Append(')');
            bool any = false;
            foreach (var kv in s.m_Keyed)   // live keys only, so the list agrees with Count
            {
                if (!kv.Value.IsValid) continue;
                sb.Append(any ? ", " : " : ").Append(kv.Key);
                any = true;
            }
            if (s.m_Scenes != null && s.m_Scenes.Count > 0)
            {
                sb.Append(" | scenes: ");
                for (int i = 0; i < s.m_Scenes.Count; ++i)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(ContentService.GetSceneKey(s.m_Scenes[i]));
                }
            }
            sb.Append('\n');
            foreach (var c in s.m_Children) Dump(c, sb, depth + 1);
        }

        // ───────────── lifetime hooks ─────────────

        internal static void ShutdownRoot()
        {
            s_Root?.DisposeCore(s_Root);
        }

        static void OnSceneUnloaded(Scene scene)
        {
            if (s_Scenes.TryGetValue(scene.handle.GetRawData(), out var s)) s.DisposeCore(s);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            s_Root = null;
            s_Scenes.Clear();
            s_IndexWarned = false;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }
    }
}
