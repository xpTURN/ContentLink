using System;
using System.Collections.Generic;
using Unity.Loading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// The editor simulation. In Play Mode the project stands in for the content directories, so
    /// an asset can be linked, edited, added or deleted and the next Play shows it without a build. The package's
    /// equivalent of Addressables' "Use Asset Database" play mode: an authoring convenience, never a substitute for
    /// the build. Rules:
    ///   · volatile      — the overlay is a memory index (name → path), built at Play entry or first use and dropped
    ///                     at Play exit. No catalog asset is written.
    ///   · registered    — a directory whose path is a build profile's output is not registered with the engine at
    ///                     all: its names are answered from the profile's source root, so what you see is the project.
    ///                     Any other registration is real, and under its catalogs' root only a name no catalog
    ///                     carries is answered from the project.
    ///   · toggle        — read when a directory is registered; turning it off mid-Play does not orphan directories
    ///                     already served from the project.
    /// Ids are made for the one name that asks: an id index up front would load the whole content folder.
    /// </summary>
    public static class ContentLinkSimulation
    {
        const string k_Menu = "Window/ContentLink/Editor Simulation";

        struct Entry
        {
            public string Path;   // null = the name is ambiguous (two files share it) and resolves to nothing
            public string Root;   // the source root it was scanned under — what the gates compare
        }

        static Dictionary<string, Entry> s_Overlay;                                  // null = not built yet
        static Dictionary<string, Entry> s_SceneOverlay;                             // scenes, same shape
        static readonly HashSet<string> s_ScannedRoots = new(StringComparer.Ordinal);        // roots asked for by registrations
        static readonly HashSet<string> s_Resolved = new(StringComparer.Ordinal);            // names answered under a real directory's root — no catalog has them
        static readonly HashSet<string> s_ResolvedSimulated = new(StringComparer.Ordinal);   // names served from the project for a profile's directory
        static readonly HashSet<string> s_SimulatedProfiles = new(StringComparer.Ordinal);   // profiles whose directory was served this Play

        // A scene load in flight: the path we asked for, and the Scene the editor produced for it.
        static readonly Dictionary<string, string> s_ScenePathInFlight = new(StringComparer.Ordinal);
        static readonly Dictionary<string, Scene> s_SceneResult = new(StringComparer.Ordinal);
        static bool s_WatchingScenes;

        /// <summary>
        /// EditorPrefs is machine-wide, not per project, so the key carries the project — otherwise turning the
        /// simulation off in one project would turn it off in every other one on the machine.
        /// </summary>
        static string PrefKey => "xpTURN.ContentLink.EditorSimulation:" + Application.dataPath;

        /// <summary>
        /// On by default. Off means the editor behaves like a build: only registered directories. The value is
        /// read when a directory is registered; a directory already served from the project keeps being served until
        /// the next Play, so turning it off mid-Play only stops new names under real directories.
        /// </summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefKey, true);
            set
            {
                EditorPrefs.SetBool(PrefKey, value);
                if (value) return;
                if (EditorApplication.isPlaying && ContentService.HasSimulatedDirectories)
                    Debug.Log("[ContentLink] editor simulation off: directories already served from the project keep working until the next Play.");
                else
                    Drop();
            }
        }

        [InitializeOnLoadMethod]
        static void Install()
        {
            // Installed unconditionally and the toggle is read inside: the installer runs on a domain load only, so
            // an install that depended on the toggle would need a domain reload to take effect.
            ContentService.DirectoryFallback = OnRegister;
            ContentService.NameFallback = Resolve;
            ContentService.SceneContainsFallback = SceneKnown;
            ContentService.SceneFallback = StartScene;
            ContentService.SceneResultFallback = SceneResult;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // The exit runs whatever the toggle says now: it may have been turned off during this Play (allowed —
            // served directories keep working), and the names it served still need the summary and the reset.
            if (change == PlayModeStateChange.ExitingPlayMode) { Report(); Drop(); return; }
            if (change == PlayModeStateChange.EnteredPlayMode && Enabled) Rebuild();
        }

        // ───────────── directories served from the project ─────────────

        /// <summary>
        /// The profile whose output folder is <paramref name="path"/>, as what a directory registered there would
        /// have held: the profile's source root and the folders its categories cover. Pure — no toggle, no logging —
        /// so it can be checked in edit mode. Null when no profile has that output.
        /// </summary>
        public static ContentService.SimulatedDirectory MatchProfile(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (var profile in ContentBuildSettings.instance.Profiles)
            {
                if (profile == null || !ContentBuildSettings.SameFolder(profile.OutputPath, path)) continue;
                var root = (profile.SourceRoot ?? "").Replace('\\', '/').TrimEnd('/');
                var covered = new List<string>();
                foreach (var entry in profile.Catalogs ?? Array.Empty<ContentBuildSettings.CatalogEntry>())
                {
                    var sub = ContentBuildSettings.NormalizeSubFolder(entry?.SubFolder);
                    var folder = sub.Length == 0 ? root : root + "/" + sub;
                    if (!covered.Contains(folder)) covered.Add(folder);
                }
                if (covered.Count == 0) covered.Add(root);
                return new ContentService.SimulatedDirectory { Profile = profile.Name, SourceRoot = root, Covered = covered.ToArray() };
            }
            return null;
        }

        /// <summary>
        /// The registration seam: answers only in Play Mode with the simulation on, only for a profile's output
        /// folder, and refuses a profile whose source root is not a folder rather than falling back to the engine.
        /// Scans the root right away, before the caller can ask for a name.
        /// </summary>
        static ContentService.SimulatedDirectory OnRegister(string path)
        {
            if (!EditorApplication.isPlaying || !Enabled) return null;
            var directory = MatchProfile(path);
            if (directory == null) return null;
            if (!AssetDatabase.IsValidFolder(directory.SourceRoot))
                throw new InvalidOperationException(
                    $"[ContentLink] editor simulation: '{path}' is the output of profile '{directory.Profile}', but its source root '{directory.SourceRoot}' is not a folder — fix the profile or turn the simulation off.");

            EnsureScanned(directory.SourceRoot);
            s_SimulatedProfiles.Add(directory.Profile);
            Debug.Log($"[ContentLink] editor simulation: '{path}' is served from the project — profile '{directory.Profile}' · root '{directory.SourceRoot}' · covered {string.Join(", ", directory.Covered)}. " +
                      "Turn the simulation off (Window/ContentLink/Editor Simulation) to load the built directory instead.");
            return directory;
        }

        // ───────────── the overlay ─────────────

        /// <summary>
        /// Indexes name → asset path for every catalog that knows its source root, plus the roots registrations asked
        /// for. Paths only, so this is a folder walk and some string work: no asset is loaded. Returns the
        /// number of names indexed.
        /// </summary>
        public static int Rebuild()
        {
            var overlay = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var scenes = new Dictionary<string, Entry>(StringComparer.Ordinal);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            int rootless = 0;

            foreach (var catalog in ContentLinkEditorUtility.LoadCatalogs())
            {
                var root = catalog != null ? catalog.SourceRoot : null;
                if (string.IsNullOrEmpty(root)) { ++rootless; continue; }      // a catalog that does not record its source root cannot be scanned
                roots.Add(root);
            }
            foreach (var root in s_ScannedRoots) roots.Add(root);
            foreach (var root in roots)
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                Scan(overlay, scenes, root);
            }

            if (rootless > 0)
                Debug.LogWarning($"[ContentLink] {rootless} catalog(s) do not know the folder they were built from, so the editor simulation skips them. " +
                                 "Run 'Window/ContentLink/Update Content Catalog' once for each to record it.");

            s_Overlay = overlay;
            s_SceneOverlay = scenes;
            return overlay.Count + scenes.Count;
        }

        /// <summary>Adds a root to the overlay right away (a registration may come before Play entry's Rebuild).</summary>
        static void EnsureScanned(string root)
        {
            root = root.Replace('\\', '/').TrimEnd('/');
            if (!s_ScannedRoots.Add(root)) return;
            if (s_Overlay == null) { Rebuild(); return; }
            if (AssetDatabase.IsValidFolder(root)) Scan(s_Overlay, s_SceneOverlay, root);
        }

        static void Scan(Dictionary<string, Entry> overlay, Dictionary<string, Entry> scenes, string root)
        {
            foreach (var guid in AssetDatabase.FindAssets("", new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                if (!ContentLinkEditorUtility.IsUnder(root, path)) continue;

                bool isScene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                var name = ContentLinkEditorUtility.ToName(root, path);

                // Assets and scenes share one name space, exactly as the builder's duplicate check does, so a
                // "Foo.txt" next to a "Foo.unity" is refused here too instead of working only in the editor.
                bool inAssets = overlay.TryGetValue(name, out var prevAsset);
                bool inScenes = scenes.TryGetValue(name, out var prevScene);
                if (!inAssets && !inScenes)
                {
                    (isScene ? scenes : overlay)[name] = new Entry { Path = path, Root = root };
                    continue;
                }

                var prev = inAssets ? prevAsset : prevScene;
                if (prev.Path == null || prev.Path == path) continue;   // already poisoned, or the same file twice

                // The builder refuses a directory with a duplicate name, so resolving one of them here would
                // work in the editor and fail at build time. Drop both and say so.
                Debug.LogWarning($"[ContentLink] editor simulation: duplicate name '{name}' ({prev.Path} / {path}); neither will resolve.");
                var poisoned = new Entry { Path = null, Root = prev.Root };
                if (inAssets) overlay[name] = poisoned;
                if (inScenes) scenes[name] = poisoned;
                if (isScene) scenes[name] = poisoned; else overlay[name] = poisoned;
            }
        }

        /// <summary>Forgets the overlay. Play exit does this; so does turning the simulation off outside Play.</summary>
        public static void Drop()
        {
            s_Overlay = null;
            s_SceneOverlay = null;
            s_ScannedRoots.Clear();
            s_Resolved.Clear();
            s_ResolvedSimulated.Clear();
            s_SimulatedProfiles.Clear();
            s_ScenePathInFlight.Clear();
            s_SceneResult.Clear();
        }

        /// <summary>The raw overlay lookup, before the registration gates. For tests and diagnostics.</summary>
        public static bool TryGetPath(string name, out string path, out string sourceRoot)
        {
            path = null;
            sourceRoot = null;
            if (name == null) return false;

            Entry e;
            if (s_Overlay != null && s_Overlay.TryGetValue(name, out e) && e.Path != null) { }
            else if (s_SceneOverlay != null && s_SceneOverlay.TryGetValue(name, out e) && e.Path != null) { }
            else return false;

            path = e.Path;
            sourceRoot = e.Root;
            return true;
        }

        // ───────────── the name seam ─────────────

        /// <summary>
        /// What ContentService calls when no registered catalog carries the name. Two gates, either lets it through:
        ///   · the asset lies under a directory served from the project — answered with that directory, whatever the
        ///     toggle says now (it was read at registration);
        ///   · the asset lies under a real directory's catalog root — answered from the project only while the
        ///     toggle is on.
        /// A default value means "not mine", and the caller then reports the usual error.
        /// </summary>
        public static ContentService.SimulatedAsset Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return default;
            if (!Enabled && !ContentService.HasSimulatedDirectories) return default;
            if (s_Overlay == null) Rebuild();   // a boot script can load before EnteredPlayMode arrives

            if (!s_Overlay.TryGetValue(name, out var entry))
            {
                entry = Discover(name);          // made during Play, or a "name[sub]" the scan cannot see
                if (entry.Path == null) return default;
                s_Overlay[name] = entry;
            }
            if (entry.Path == null) return default;                                   // ambiguous: two files share the name

            if (ContentService.TryGetSimulatedDir(entry.Root, entry.Path, out var dir))
            {
                var id = MakeId(name);
                if (id.IsValid) s_ResolvedSimulated.Add(name);
                return new ContentService.SimulatedAsset(id, dir);
            }
            if (!Enabled || !ContentService.IsSourceRootRegistered(entry.Root)) return default;    // under a real directory: only while the toggle is on

            var idUnderReal = MakeId(name);
            if (idUnderReal.IsValid) s_Resolved.Add(name);
            return new ContentService.SimulatedAsset(idUnderReal);
        }

        static LoadableObjectId MakeId(string name)
        {
            var asset = ContentLinkEditorUtility.ResolveAsset(name);
            return asset == null ? default : LoadableObjectIdEditorUtility.CreateLoadableObjectId(asset);
        }

        static Entry Discover(string name)
        {
            var asset = ContentLinkEditorUtility.ResolveAsset(name);
            if (asset == null) return default;

            var path = AssetDatabase.GetAssetPath(asset);
            var root = ContentLinkEditorUtility.RootFor(path);
            return string.IsNullOrEmpty(root) ? default : new Entry { Path = path, Root = root };
        }

        // ───────────── scenes ─────────────

        /// <summary>Does the simulation have this scene name, and may it serve it? (ContainsScene · same gates)</summary>
        public static bool SceneKnown(string name) => TryGetScenePath(name, out _, out _);

        /// <summary>
        /// Starts the load the editor way and arms the capture that will name the resulting Scene.
        /// Null means "not mine", and ContentService then reports the usual error.
        /// </summary>
        public static AsyncOperation StartScene(string name, LoadSceneParameters parameters)
        {
            if (!TryGetScenePath(name, out var path, out var viaProject)) return null;

            if (!s_WatchingScenes)
            {
                SceneManager.sceneLoaded -= OnSceneLoadedWhileSimulating;
                SceneManager.sceneLoaded += OnSceneLoadedWhileSimulating;
                s_WatchingScenes = true;
            }
            s_SceneResult.Remove(name);
            s_ScenePathInFlight[name] = path;

            AsyncOperation op = null;
            try { op = EditorSceneManager.LoadSceneAsyncInPlayMode(path, parameters); }
            catch (Exception e) { Debug.LogException(e); }
            if (op == null) { s_ScenePathInFlight.Remove(name); return null; }

            (viaProject ? s_ResolvedSimulated : s_Resolved).Add(name);
            return op;
        }

        /// <summary>The Scene the last StartScene for this name produced, then forgets it.</summary>
        public static Scene SceneResult(string name)
        {
            s_ScenePathInFlight.Remove(name);
            if (!s_SceneResult.TryGetValue(name, out var scene)) return default;
            s_SceneResult.Remove(name);
            return scene;
        }

        /// <summary>
        /// Matches by the path we asked for, which is exact. The id-based Claim in ContentService cannot help here:
        /// a simulated scene is in no directory, so it is never tracked, and matching on the file name alone
        /// would pick the wrong scene when two folders hold a scene of the same name.
        /// </summary>
        static void OnSceneLoadedWhileSimulating(Scene scene, LoadSceneMode mode)
        {
            if (s_ScenePathInFlight.Count == 0) return;
            foreach (var pair in s_ScenePathInFlight)
                if (string.Equals(pair.Value, scene.path, StringComparison.Ordinal))
                {
                    s_SceneResult[pair.Key] = scene;
                    return;
                }
        }

        static bool TryGetScenePath(string name, out string path, out bool viaProject)
        {
            path = null;
            viaProject = false;
            if (string.IsNullOrEmpty(name)) return false;
            if (!Enabled && !ContentService.HasSimulatedDirectories) return false;
            if (s_SceneOverlay == null) Rebuild();

            if (!s_SceneOverlay.TryGetValue(name, out var entry))
            {
                entry = DiscoverScene(name);          // saved while Play was running
                if (entry.Path == null) return false;
                s_SceneOverlay[name] = entry;
            }
            if (entry.Path == null) return false;                                     // ambiguous: two files share the name

            if (ContentService.TryGetSimulatedDir(entry.Root, entry.Path, out _)) viaProject = true;     // served from the project for a profile's directory
            else if (!Enabled || !ContentService.IsSourceRootRegistered(entry.Root)) return false;       // under a real directory: only while the toggle is on

            path = entry.Path;
            return true;
        }

        static Entry DiscoverScene(string name)
        {
            var scene = ContentLinkEditorUtility.ResolveScene(name);
            if (scene == null) return default;

            var path = AssetDatabase.GetAssetPath(scene);
            var root = ContentLinkEditorUtility.RootFor(path);
            return string.IsNullOrEmpty(root) ? default : new Entry { Path = path, Root = root };
        }

        // ───────────── the exit summary ─────────────

        /// <summary>
        /// The names that would fail in a build: everything answered under a real directory (no catalog has those
        /// by definition), and the names served for a profile's directory that the profile's own catalogs do not
        /// carry yet (the other names are simply the project standing in for the build).
        /// </summary>
        static void Report()
        {
            var names = new List<string>(s_Resolved);
            if (s_ResolvedSimulated.Count > 0)
            {
                var known = CatalogNamesOf(s_SimulatedProfiles);
                foreach (var name in s_ResolvedSimulated) if (!known.Contains(name)) names.Add(name);
            }
            if (names.Count == 0) return;

            names.Sort(StringComparer.Ordinal);
            int shown = Math.Min(names.Count, 8);
            var list = string.Join(", ", names.GetRange(0, shown));
            if (names.Count > shown) list += $", … (+{names.Count - shown})";

            // Not per load — that would be noise. But saying nothing at all would let a link that only ever worked
            // in the editor reach a build and fail there with no warning.
            Debug.Log($"[ContentLink] editor simulation resolved {names.Count} name(s) no catalog carries: {list}. " +
                      "Build the content directory before shipping.");
        }

        static HashSet<string> CatalogNamesOf(IEnumerable<string> profiles)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var settings = ContentBuildSettings.instance;
            foreach (var profileName in profiles)
            {
                var profile = settings.Find(profileName);
                if (profile == null) continue;
                foreach (var entry in profile.Catalogs ?? Array.Empty<ContentBuildSettings.CatalogEntry>())
                {
                    var path = entry?.ResolvePath();
                    if (string.IsNullOrEmpty(path)) continue;
                    var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(path);
                    if (catalog == null) continue;
                    foreach (var e in catalog.Entries) names.Add(e.name);
                    foreach (var e in catalog.Scenes) names.Add(e.name);
                }
            }
            return names;
        }

        // ───────────── the toggle ─────────────

        [MenuItem(k_Menu)]
        static void ToggleMenu() => Enabled = !Enabled;

        [MenuItem(k_Menu, true)]
        static bool ToggleMenuValidate()
        {
            Menu.SetChecked(k_Menu, Enabled);
            return true;
        }
    }
}
