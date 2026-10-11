using System;
using System.Collections.Generic;
using Unity.Loading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.U2D;
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

        sealed class Entry
        {
            public string Path;   // the file of that name under Root
            public string Root;   // the source root it was scanned under — what the gates compare
            public bool Dup;      // two files share the name under this root: it resolves to nothing there
            public Entry Next;    // the same name under another root — a locale or a patch profile; the registration order picks (Choose)
        }

        static Dictionary<string, Entry> s_Overlay;                                  // null = not built yet
        static Dictionary<string, Entry> s_SceneOverlay;                             // scenes, same shape

        // A file's names by its GUID, for ContentRef: filled by the same scan, so a reference costs a dictionary lookup.
        struct GuidNames
        {
            public string Path;
            public string Name;                                   // under the deepest root that holds the file; null = not under any root
            public string Root;
            public List<KeyValuePair<string, string>> Others;     // (name, root) under shallower roots — null unless roots nest
        }

        static readonly Dictionary<string, (Entry Entry, Object Asset)> s_AssetByName = new(StringComparer.Ordinal);   // the object a name took, and the file it was taken from
        static readonly Dictionary<string, string> s_MainOfSub = new(StringComparer.Ordinal);                            // "main[sub]" → "main" (null: not such a name)
        static readonly Dictionary<string, Entry> s_BracketChains = new(StringComparer.Ordinal);                         // "main[sub]" a file is named too → one entry per root (BracketChain)
        static readonly Dictionary<string, GuidNames> s_NamesByGuid = new(StringComparer.Ordinal);
        static readonly Dictionary<string, GuidNames> s_SceneNamesByGuid = new(StringComparer.Ordinal);
        static readonly Dictionary<(string, string), string> s_SubNames = new();          // (main name, sprite name) → "main[sprite]"
        static readonly HashSet<string> s_ScannedRoots = new(StringComparer.Ordinal);        // roots asked for by registrations
        static readonly HashSet<string> s_Resolved = new(StringComparer.Ordinal);            // names answered under a real directory's root — no catalog has them
        static readonly HashSet<string> s_ResolvedSimulated = new(StringComparer.Ordinal);   // names served from the project for a profile's directory
        static readonly HashSet<string> s_SimulatedProfiles = new(StringComparer.Ordinal);   // profiles whose directory was served this Play

        // Scene loads in flight. An operation does not say which Scene it produced, so — as ContentService does by id — a load takes
        // an instance of its path that arrived while a load of that path was in flight: loads of one name each take their own.
        static readonly Dictionary<string, List<string>> s_SceneLoadsInFlight = new(StringComparer.Ordinal);   // name → the path of each load in flight, oldest first
        static readonly Dictionary<string, int> s_FlightsByPath = new(StringComparer.Ordinal);
        static readonly Dictionary<string, List<Scene>> s_ScenesArrived = new(StringComparer.Ordinal);         // path → instances no load has taken yet
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
                if (!EditorApplication.isPlaying) Drop();   // mid-Play the gates read the toggle; loads in flight and the exit summary still need what was served
                else if (ContentService.HasSimulatedDirectories)
                    Debug.Log("[ContentLink] editor simulation off: directories already served from the project keep working until the next Play.");
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
            ContentService.SceneLoadsInFlightFallback = ScenesInFlight;
            ContentService.RefFallback = OverlayNameFor;
            ContentService.ResourcesFallback = ContentLinkEditorUtility.PlayerResources;   // a res: load in the editor — Edit Mode too — gives what a player's Resources holds
            ContentRemote.ProfileFallback = SimulatedOutputOf;
            ContentArchives.EditorTarget = () => EditorUserBuildSettings.activeBuildTarget.ToString();   // built content is checked against what the editor loads
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.projectChanged -= ForgetGuids;
            EditorApplication.projectChanged += ForgetGuids;
            ContentLinkEditorUtility.Invalidated -= ForgetAnswers;   // whatever drops the utility's caches — an atlas saved, a catalog imported or written, a profile edit
            ContentLinkEditorUtility.Invalidated += ForgetAnswers;
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
        /// The remote seam: with the simulation on, a profile the project can serve answers with its output folder — the folder a
        /// registration then hands back to <see cref="OnRegister"/> — so a CheckAsync → DownloadAsync → register sequence runs
        /// without a server. Null = download for real.
        /// </summary>
        static string SimulatedOutputOf(string profileName)
        {
            if (!Enabled || !EditorApplication.isPlaying) return null;
            var profile = ContentBuildSettings.instance.Find(profileName);
            if (profile == null || string.IsNullOrEmpty(profile.OutputPath)) return null;
            return MatchProfile(profile.OutputPath) != null ? profile.OutputPath : null;
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
            if (ContentLinkEditorUtility.DiskPath(directory.SourceRoot) is string disk && disk != ContentLinkEditorUtility.Composed(directory.SourceRoot))   // found in any case, but nothing under it would be named
                throw new InvalidOperationException(
                    $"[ContentLink] editor simulation: '{path}' is the output of profile '{directory.Profile}', but its source root '{directory.SourceRoot}' is spelled '{disk}' in the project — fix the profile or turn the simulation off.");

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
            ForgetGuids();   // the scan below fills them again

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
            s_BracketChains.Clear();   // a root scanned now may hold either side of such a name
        }

        static void Scan(Dictionary<string, Entry> overlay, Dictionary<string, Entry> scenes, string root)
        {
            var style = ContentLinkEditorUtility.NameStyleOf(root);   // once for the root, not once for every asset under it
            foreach (var guid in AssetDatabase.FindAssets("", new[] { root }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) continue;
                if (!ContentLinkEditorUtility.IsUnder(root, path)) continue;
                if (AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(ContentCatalog))   // by its type, as the builder refuses it: the engine imports a catalog saved as .mat, .anim, .preset … too (measured)
                {
                    Debug.LogWarning($"[ContentLink] editor simulation: '{path}' is a ContentCatalog under '{root}' — a build refuses it, so it does not resolve here either.");
                    continue;
                }

                bool isScene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
                var name = ContentLinkEditorUtility.ToName(root, path, style);
                AddGuidName(isScene ? s_SceneNamesByGuid : s_NamesByGuid, guid, name, root, path);

                // Assets and scenes share one name space in a directory, exactly as the builder's duplicate check does, so a
                // "Foo.txt" next to a "Foo.unity" is refused here too instead of working only in the editor. Under another root
                // the same name is another directory's (a locale, a patch): kept beside it, and the registration order picks.
                var own = isScene ? scenes : overlay;
                own.TryGetValue(name, out var head);
                var rival = Under(head, root) ?? Under((isScene ? overlay : scenes).TryGetValue(name, out var other) ? other : null, root);
                if (rival == null)
                {
                    own[name] = new Entry { Path = path, Root = root, Next = head };
                    continue;
                }
                if (rival.Dup || rival.Path == path) continue;   // already poisoned, or the same file twice

                // The builder refuses a directory with a duplicate name, so resolving one of them here would
                // work in the editor and fail at build time. Drop both and say so.
                Debug.LogWarning($"[ContentLink] editor simulation: duplicate name '{name}' ({rival.Path} / {path}); neither will resolve.");
                rival.Dup = true;
                if (Under(head, root) == null) own[name] = new Entry { Path = path, Root = root, Dup = true, Next = head };
            }
        }

        static Entry Under(Entry head, string root)
        {
            for (var e = head; e != null; e = e.Next) if (e.Root == root) return e;
            return null;
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
            s_SceneLoadsInFlight.Clear();
            s_FlightsByPath.Clear();
            s_ScenesArrived.Clear();
            ForgetGuids();
        }

        /// <summary>The raw overlay lookup, before the registration gates. For tests and diagnostics.</summary>
        public static bool TryGetPath(string name, out string path, out string sourceRoot)
        {
            path = null;
            sourceRoot = null;
            if (name == null) return false;
            name = ContentLinkEditorUtility.Composed(name);   // the overlay holds names composed

            Entry e = null;
            if (s_Overlay != null && HeadOf(name) is Entry head) e = Choose(head, out _, out _) ?? FirstUsable(head);
            if ((e == null || e.Dup) && s_SceneOverlay != null && s_SceneOverlay.TryGetValue(name, out var sceneHead)) e = Choose(sceneHead, out _, out _) ?? FirstUsable(sceneHead);
            if (e == null || e.Dup) return false;

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

            var head = HeadOf(name);
            if (head == null)
            {
                head = Discover(name);           // made during Play
                if (head == null) return default;
                s_Overlay[name] = head;
            }

            var entry = Choose(head, out var dir, out bool simulated);
            if (entry == null || entry.Dup) return default;   // no root a gate accepts, or two files share the name under the one that wins

            var id = MakeId(name, entry);
            if (id.IsValid) (simulated ? s_ResolvedSimulated : s_Resolved).Add(name);
            return simulated ? new ContentService.SimulatedAsset(id, dir) : new ContentService.SimulatedAsset(id);
        }

        /// <summary>
        /// The root whose file answers a name: among the roots that have it, the one the most recently registered directory served
        /// from the project covers — the engine's rule for real directories (ContentService.RegisterDirectory). Under a real
        /// directory's root (the toggle on) only when no served directory has it and one root does: the order of real directories
        /// is not visible here.
        /// </summary>
        static Entry Choose(Entry head, out int dir, out bool simulated)
        {
            Entry best = null, real = null;
            int reals = 0;
            dir = ContentService.NoDirectory;
            for (var e = head; e != null; e = e.Next)
            {
                if (ContentService.TryGetSimulatedDir(e.Root, e.Path, out var d))
                {
                    if (best == null || d > dir) { best = e; dir = d; }
                }
                else if (Enabled && ContentService.IsSourceRootRegistered(e.Root)) { real = e; ++reals; }   // under a real directory: only while the toggle is on
            }
            simulated = best != null;
            if (simulated) return best;
            dir = ContentService.NoDirectory;
            return reals == 1 ? real : null;
        }

        static Entry FirstUsable(Entry head)
        {
            for (var e = head; e != null; e = e.Next) if (!e.Dup) return e;
            return null;
        }

        /// <summary>A name's entries as Resolve sees them: the file of that name, the sprite "main[sub]" of a main the scan has, or both (BracketChain). Null when the scan has neither.</summary>
        static Entry HeadOf(string name)
        {
            if (!s_Overlay.TryGetValue(name, out var head)) return SubOf(name);
            return name[name.Length - 1] == ']' && SubOf(name) is Entry main ? BracketChain(name, head, main) : head;   // a file named "main[sub]" next to a main that may have that sprite
        }

        /// <summary>"main[sub]" whose main name the scan has: the same files, the sub-asset picked when the id is made. Not cached — a root registered later adds to the main's entries.</summary>
        static Entry SubOf(string name)
        {
            if (!s_MainOfSub.TryGetValue(name, out var main))
            {
                int open = name.LastIndexOf('[');
                s_MainOfSub[name] = main = open > 0 && name[name.Length - 1] == ']' ? name.Substring(0, open) : null;
            }
            return main != null && s_Overlay.TryGetValue(main, out var head) ? head : null;
        }

        /// <summary>
        /// "main[sub]" is both a file's name and a sprite's: the builder puts both in one name space and refuses a directory that has both,
        /// so a root holding both gives neither (one warning), and across roots the registration order picks between them. Made once
        /// per name — it loads the main files — and dropped when a root is scanned or the project changes.
        /// </summary>
        static Entry BracketChain(string name, Entry literal, Entry main)
        {
            if (s_BracketChains.TryGetValue(name, out var chain)) return chain;
            for (var e = literal; e != null; e = e.Next)
            {
                var sprite = Under(main, e.Root);
                bool both = sprite != null && (sprite.Dup || AssetOf(name, sprite) != null);
                if (both && !e.Dup) Debug.LogWarning($"[ContentLink] editor simulation: duplicate name '{name}' ({sprite.Path} / {e.Path}); neither will resolve.");
                chain = new Entry { Path = e.Path, Root = e.Root, Dup = e.Dup || both, Next = chain };
            }
            for (var m = main; m != null; m = m.Next)
                if (Under(literal, m.Root) == null && (m.Dup || AssetOf(name, m) != null)) chain = new Entry { Path = m.Path, Root = m.Root, Dup = m.Dup, Next = chain };
            s_BracketChains[name] = chain;
            return chain;
        }

        static LoadableObjectId MakeId(string name, Entry entry)
        {
            if (!s_AssetByName.TryGetValue(name, out var hit) || hit.Entry != entry) s_AssetByName[name] = hit = (entry, AssetOf(name, entry));
            return hit.Asset == null ? default : LoadableObjectIdEditorUtility.CreateLoadableObjectId(hit.Asset);
        }

        /// <summary>
        /// The object a name means in the file the gates picked: the main asset, a sprite of it, or a sprite an atlas packs.
        /// Never a search by name — that answers from whichever root comes first, not the one the registrations picked.
        /// </summary>
        static Object AssetOf(string name, Entry entry)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(entry.Path);
            if (asset == null) return null;
            var main = ContentLinkEditorUtility.ToName(entry.Root, entry.Path);
            if (name == main) return asset;
            if (name.Length < main.Length + 3 || name[main.Length] != '[' || name[name.Length - 1] != ']' || !name.StartsWith(main, StringComparison.Ordinal)) return null;
            var sub = name.Substring(main.Length + 1, name.Length - main.Length - 2);
            if (asset is SpriteAtlas atlas)   // an atlas's name for a sprite it packs
                return ContentLinkEditorUtility.NamesItsSprites(entry.Path, atlas) ? ContentLinkEditorUtility.PackedSprite(atlas, sub, out _) : null;
            foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(entry.Path))
                if (rep is Sprite && ContentLinkEditorUtility.Composed(rep.name) == sub) return rep;
            return null;
        }

        static Entry Discover(string name)
        {
            var asset = ContentLinkEditorUtility.ResolveAsset(name);
            if (asset == null || asset is SceneAsset) return default;   // a scene's name is never an asset's, as in a build — DiscoverScene is the scene side

            var path = AssetDatabase.GetAssetPath(asset);
            var root = RootOfName(name, path);
            // An atlas's name for a sprite: the sprite's texture is elsewhere — the atlas gives the root, and the path a simulated
            // directory is chosen by, as the build's category does
            if (string.IsNullOrEmpty(root) && asset is Sprite && ContentLinkEditorUtility.TryAtlasOfName(name, out var atlasPath, out _))
            {
                path = atlasPath;
                root = RootOfName(name, atlasPath);
            }
            return string.IsNullOrEmpty(root) ? default : new Entry { Path = path, Root = root };
        }

        /// <summary>
        /// The content root <paramref name="name"/> was made under. RootFor answers the deepest root holding the file, which
        /// under nested roots is not the root of a name made under a shallower one — and the gates select on the root.
        /// </summary>
        static string RootOfName(string name, string path)
        {
            foreach (var root in ContentLinkEditorUtility.SourceRoots())   // longest first
            {
                if (!ContentLinkEditorUtility.IsUnder(root, path)) continue;
                var main = ContentLinkEditorUtility.ToName(root, path);
                if (name == main || (name.Length > main.Length && name[main.Length] == '[' && name.StartsWith(main, StringComparison.Ordinal))) return root;
            }
            return null;
        }

        // ───────────── the reference seam ─────────────

        /// <summary>
        /// What ContentService calls for a GUID no registered catalog carries (<see cref="ContentService.RefFallback"/>): the
        /// name the overlay scan made for that file, "name[sprite]" for a sub-asset, or null. It answers the name only — the
        /// name seam that follows applies the gates, and checks that the sprite exists. With no catalog behind a directory
        /// served from the project every reference lookup lands here, so after the first answer it is a dictionary lookup.
        /// A file the scan did not see (created or moved during Play) is looked up once and named under every root holding it.
        /// A file under nested roots has a name per root: the first one a gate accepts, else the deepest root's.
        /// </summary>
        public static string OverlayNameFor(string guid, string sub, bool isScene)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            if (!ContentService.HasSimulatedDirectories && !Enabled) return null;   // this order: the toggle is an EditorPrefs read, and every lookup lands here
            sub = ContentLinkEditorUtility.Composed(sub);   // a reference keeps the sprite's name as it was spelled when it was picked
            if (s_Overlay == null) Rebuild();

            var byGuid = isScene ? s_SceneNamesByGuid : s_NamesByGuid;
            if (!byGuid.TryGetValue(guid, out var names))
            {
                names = default;   // remembered when it is not ours either, so a missing reference does not ask the AssetDatabase again
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) == isScene)
                    foreach (var root in ContentLinkEditorUtility.SourceRoots())
                        if (ContentLinkEditorUtility.IsUnder(root, path)) AddName(ref names, ContentLinkEditorUtility.ToName(root, path), root, path);
                byGuid[guid] = names;
            }
            if (names.Name == null) return null;

            var main = names.Others == null ? names.Name : ChooseServed(names);
            var name = main;
            if (!isScene && !string.IsNullOrEmpty(sub) && !s_SubNames.TryGetValue((main, sub), out name)) s_SubNames[(main, sub)] = name = $"{main}[{sub}]";
            // Another file answers the name Resolve will look at — another root's (a locale, a patch, whether or not its root was scanned for a
            // registration) or a file named "main[sub]" beside a main with that sprite — and no served root holds this file: a player has no
            // catalog with this GUID and misses, while the name would take that other file — so this misses too. The gates are asked only then:
            // one root's own file is a dictionary lookup.
            var head = isScene ? (s_SceneOverlay.TryGetValue(name, out var scene) ? scene : null) : HeadOf(name);
            if (head != null && (head.Next != null || head.Path != names.Path) && !AnyServed(names)) return null;
            return name;
        }

        static void AddGuidName(Dictionary<string, GuidNames> byGuid, string guid, string name, string root, string path)
        {
            byGuid.TryGetValue(guid, out var names);
            if (AddName(ref names, name, root, path)) byGuid[guid] = names;
        }

        /// <summary>The deepest root's name stays first. False when nothing changed (the same root scanned again).</summary>
        static bool AddName(ref GuidNames names, string name, string root, string path)
        {
            if (names.Name == null) { names = new GuidNames { Path = path, Name = name, Root = root }; return true; }
            if (names.Name == name) return false;
            names.Others ??= new List<KeyValuePair<string, string>>();
            if (root.Length > names.Root.Length)
            {
                names.Others.Add(new KeyValuePair<string, string>(names.Name, names.Root));
                names.Name = name;
                names.Root = root;
            }
            else names.Others.Add(new KeyValuePair<string, string>(name, root));
            return true;
        }

        /// <summary>Nested roots: the first name (deepest root first) whose root the gates of <see cref="Resolve"/> / <see cref="TryGetScenePath"/> accept, else the deepest.</summary>
        static string ChooseServed(GuidNames names)
        {
            if (Served(names.Root, names.Path)) return names.Name;
            foreach (var other in names.Others) if (Served(other.Value, names.Path)) return other.Key;
            return names.Name;
        }

        static bool AnyServed(GuidNames names)
        {
            if (Served(names.Root, names.Path)) return true;
            if (names.Others != null) foreach (var other in names.Others) if (Served(other.Value, names.Path)) return true;
            return false;
        }

        static bool Served(string root, string path)
            => ContentService.TryGetSimulatedDir(root, path, out _) || (Enabled && ContentService.IsSourceRootRegistered(root));

        /// <summary>Drops the GUID → name answers, and what was read off the files (<see cref="ForgetAnswers"/>). A moved or renamed file changes them, so a project change drops them too.</summary>
        static void ForgetGuids()
        {
            ForgetAnswers();
            s_NamesByGuid.Clear();
            s_SceneNamesByGuid.Clear();
            s_SubNames.Clear();
        }

        /// <summary>
        /// Drops what was read off the files: the object each name took and the bracketed names' chains. An atlas saved with other
        /// sprites changes them with no project change, so whatever drops the editor utility's caches drops these (Invalidated). The
        /// GUID → name answers stay — paths, which only a project change moves: dropped, a nested root's references would take the
        /// slow path for the rest of the Play.
        /// </summary>
        static void ForgetAnswers()
        {
            s_AssetByName.Clear();
            s_BracketChains.Clear();
        }

        // ───────────── scenes ─────────────

        /// <summary>Does the simulation have this scene name, and may it serve it? (ContainsScene · same gates)</summary>
        public static bool SceneKnown(string name) => TryGetScenePath(name, out _, out _);

        /// <summary>
        /// Starts the load the editor way and arms the capture that will name the resulting Scene.
        /// Null means "not mine", and ContentService then reports the usual error. Called by the seam (ContentService.SceneFallback):
        /// every load it starts is ended by one <see cref="SceneResult"/>, or the instances of its path are kept until Play ends.
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
            if (!s_SceneLoadsInFlight.TryGetValue(name, out var paths)) s_SceneLoadsInFlight[name] = paths = new List<string>(1);
            paths.Add(path);
            s_FlightsByPath.TryGetValue(path, out var flights);
            s_FlightsByPath[path] = flights + 1;

            AsyncOperation op = null;
            try { op = EditorSceneManager.LoadSceneAsyncInPlayMode(path, parameters); }
            catch (Exception e) { Debug.LogException(e); }
            if (op == null) { paths.RemoveAt(paths.Count - 1); EndFlight(name, paths, path); return null; }

            (viaProject ? s_ResolvedSimulated : s_Resolved).Add(name);
            return op;
        }

        /// <summary>
        /// The Scene a load of this name produced, and that load is over: the oldest load in flight whose path has an instance waiting
        /// (the oldest when none has). Called by the seam (ContentService.SceneResultFallback) once per load that started — done,
        /// cancelled (Additive: to unload it · Single: to keep it tracked) or failed; a call just to look would end a load. An instance
        /// arrives before its own load completes (measured on 6000.6), so the load that just completed has one waiting; loads of one
        /// path complete in the order they started, loads of two files need not.
        /// </summary>
        public static Scene SceneResult(string name)
        {
            if (!s_SceneLoadsInFlight.TryGetValue(name, out var paths) || paths.Count == 0) return default;
            int at = 0;   // the oldest load whose path has an instance waiting: loads of one file end in the order they started (measured), of two files need not
            for (int k = 0; k < paths.Count; ++k)
                if (s_ScenesArrived.TryGetValue(paths[k], out var waiting) && AnyLoaded(waiting)) { at = k; break; }
            var path = paths[at];
            paths.RemoveAt(at);
            Scene taken = default;
            if (s_ScenesArrived.TryGetValue(path, out var arrived))
                for (int i = arrived.Count - 1; i >= 0; --i)   // newest first, as ContentService.Claim
                {
                    if (!arrived[i].isLoaded) { arrived.RemoveAt(i); continue; }   // unloaded before any load took it
                    taken = arrived[i];
                    arrived.RemoveAt(i);
                    break;
                }
            EndFlight(name, paths, path);
            return taken;
        }

        /// <summary>The project path of each scene load in flight, once per path — UnregisterDirectory refuses the directories that cover them, as the player does for built ones.</summary>
        static void ScenesInFlight(List<string> paths)
        {
            foreach (var path in s_FlightsByPath.Keys) paths.Add(path);
        }

        static bool AnyLoaded(List<Scene> scenes)
        {
            foreach (var scene in scenes) if (scene.isLoaded) return true;
            return false;
        }

        static void EndFlight(string name, List<string> paths, string path)
        {
            if (paths.Count == 0) s_SceneLoadsInFlight.Remove(name);
            if (s_FlightsByPath.TryGetValue(path, out var flights) && flights > 1) { s_FlightsByPath[path] = flights - 1; return; }
            s_FlightsByPath.Remove(path);
            s_ScenesArrived.Remove(path);   // no load of it in flight: an instance left over arrived for somebody else
        }

        /// <summary>
        /// Matches by the path we asked for, which is exact. The id-based Claim in ContentService cannot help here: a simulated
        /// scene has no id, and matching on the file name alone would pick the wrong scene when two folders hold a scene of the
        /// same name. An instance that arrives while a load of its path is in flight is kept for that load (SceneResult).
        /// </summary>
        static void OnSceneLoadedWhileSimulating(Scene scene, LoadSceneMode mode)
        {
            if (s_FlightsByPath.Count == 0) return;
            var path = scene.path;
            if (!s_FlightsByPath.ContainsKey(path)) return;
            if (!s_ScenesArrived.TryGetValue(path, out var arrived)) s_ScenesArrived[path] = arrived = new List<Scene>(1);
            arrived.Add(scene);
        }

        static bool TryGetScenePath(string name, out string path, out bool viaProject)
        {
            path = null;
            viaProject = false;
            if (string.IsNullOrEmpty(name)) return false;
            if (!Enabled && !ContentService.HasSimulatedDirectories) return false;
            if (s_SceneOverlay == null) Rebuild();

            if (!s_SceneOverlay.TryGetValue(name, out var head))
            {
                head = DiscoverScene(name);          // saved while Play was running
                if (head == null) return false;
                s_SceneOverlay[name] = head;
            }
            var entry = Choose(head, out _, out viaProject);
            if (entry == null || entry.Dup) return false;   // no root a gate accepts, or two files share the name under the one that wins

            path = entry.Path;
            return true;
        }

        static Entry DiscoverScene(string name)
        {
            var scene = ContentLinkEditorUtility.ResolveScene(name);
            if (scene == null) return default;

            var path = AssetDatabase.GetAssetPath(scene);
            var root = RootOfName(name, path);
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
                    foreach (var e in catalog.Entries) names.Add(ContentLinkEditorUtility.Composed(e.name));   // the resolved names are composed
                    foreach (var e in catalog.Scenes) names.Add(ContentLinkEditorUtility.Composed(e.name));
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
