using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Unity.Loading;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Profile = xpTURN.ContentLink.Editor.ContentBuildSettings.Profile;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Builds a profile: validate → prepare the output folder → scan the source root once → split the
    /// assets into the categories → write a catalog per category → one BuildContentDirectory call → collect the
    /// result. The window, the menu items and the CLI all come through here.
    ///
    /// Everything that is ordered is ordered by us: catalogs by asset path, entries by name, output files by name.
    /// The runtime reads a directory's catalogs in the order the engine hands them back, which is why a name may
    /// exist only once per profile (the scan refuses duplicates).
    /// </summary>
    public static class ContentBuildRunner
    {
        public const string SessionKey = "xpTURN.ContentLink.LastBuild";
        const string k_Title = "ContentLink";

        static readonly Regex s_Oid = new("oid-([0-9a-f]{32})", RegexOptions.Compiled);

        // ───────────── menu ─────────────

        [MenuItem("Window/ContentLink/Build Content Directory")]
        static void BuildActiveMenu() => Build(ContentBuildSettings.instance.Active);

        [MenuItem("Window/ContentLink/Update Content Catalog")]
        static void UpdateActiveCatalogsMenu()
        {
            var profile = ContentBuildSettings.instance.Active;
            var errors = new List<string>();
            if (UpdateCatalogs(profile, errors)) Debug.Log($"[ContentLink] catalogs of '{profile.Name}' updated.");
            else Debug.LogError($"[ContentLink] catalog update of '{profile.Name}' failed:\n  " + string.Join("\n  ", errors));
        }

        // ───────────── entry points ─────────────

        /// <summary>Builds every profile in order and stops at the first failure. Every profile's catalogs are updated first: a build checks what it carries against the others' catalogs.</summary>
        public static List<ContentBuildResult> BuildAll(bool showProgress = true)
        {
            var results = new List<ContentBuildResult>();
            foreach (var profile in ContentBuildSettings.instance.Profiles) UpdateCatalogs(profile, new List<string>());   // a failure is the build's to report, in its turn
            foreach (var profile in ContentBuildSettings.instance.Profiles)
            {
                var result = Build(profile, showProgress);
                results.Add(result);
                if (!result.Succeeded) break;
            }
            return results;
        }

        public static ContentBuildResult Build(Profile profile, bool showProgress = true)
        {
            var watch = Stopwatch.StartNew();
            var result = new ContentBuildResult
            {
                ProfileName = profile?.Name ?? "",
                Target = EditorUserBuildSettings.activeBuildTarget,
                OutputPath = profile?.OutputPath ?? "",
            };
            bool touched = false;   // the output folder was cleared or written: an imported output then needs a refresh
            try
            {
                Progress(showProgress, "validating", 0f);
                var errors = ContentBuildSettings.instance.Validate(profile);
                if (errors.Count > 0) return Fail(result, errors);

                if (!CheckOutput(profile.OutputPath, result.Errors)) return Fail(result, null);   // the guard first, before anything is written

                Progress(showProgress, "scanning", 0.1f);
                var catalogPaths = new List<string>();
                if (!UpdateCatalogs(profile, result.Errors, catalogPaths)) return Fail(result, null);

                touched = true;   // before the clear: one cut short has changed the folder too
                if (!PrepareOutput(profile.OutputPath, result.Errors)) return Fail(result, null);   // the clear last: a failure before the engine leaves the previous build usable

                Progress(showProgress, "building", 0.3f);
                var parameters = Parameters(profile, catalogPaths.ToArray());
                result.Report = BuildPipeline.BuildContentDirectory(parameters);
                if (result.Report == null || result.Report.summary.result != BuildResult.Succeeded)
                {
                    result.Errors.Add($"BuildContentDirectory returned {result.Report?.summary.result.ToString() ?? "null"}");
                    return Fail(result, null);
                }

                Progress(showProgress, "collecting", 0.8f);
                Collect(result, profile, catalogPaths);
                if (!CheckLoadTargets(result.Manifest, profile.SourceRoot, catalogPaths, result.Errors)) return Fail(result, null);   // before packing: a refused build is not packed

                if (profile.Archive == ContentBuildSettings.ArchiveMode.Packed)
                {
                    Progress(showProgress, "packing", 0.85f);
                    if (!ContentArchivePacker.Pack(result, profile, result.Errors)) return Fail(result, null);
                }

                Progress(showProgress, "post-processing", 0.95f);
                result.Succeeded = true;   // what the post-processors see; one of them failing turns it back
                if (!RunPostProcessors(profile, result)) return Fail(result, null);
                return result;
            }
            catch (Exception e)
            {
                result.Errors.Add($"{e.GetType().Name}: {e.Message}");   // a stage that throws is a failed build with a reason, for every entry point
                return Fail(result, null);
            }
            finally
            {
                result.Milliseconds = watch.Elapsed.TotalMilliseconds;
                if (showProgress) EditorUtility.ClearProgressBar();
                Record(result);
                Summary(result, profile);
                if (touched && InsideAssets(profile.OutputPath)) AssetDatabase.Refresh();   // built files under Assets/ are imported: the Project view shows them only after a refresh
            }
        }

        /// <summary>The catalog stage alone: scan once, split into the categories, write each catalog. False with reasons on failure.</summary>
        public static bool UpdateCatalogs(Profile profile, List<string> errors, List<string> catalogPaths = null)
        {
            var validation = ContentBuildSettings.instance.Validate(profile);
            if (validation.Count > 0) { errors.AddRange(validation); return false; }

            var scanned = new List<ContentDirectoryBuilder.Scanned>();
            if (!ContentDirectoryBuilder.Scan(profile.SourceRoot, profile.NameStyle, scanned))
            {
                errors.Add($"the scan of '{profile.SourceRoot}' refused the build — a duplicate or '{ContentService.ResourcesPrefix}' name, a catalog under the root or a sprite atlas the build cannot carry (see the errors above)");
                return false;
            }

            var groups = Partition(profile, scanned, out int uncovered);
            if (uncovered > 0 && CaseOnlyCategory(profile, scanned) is string slip) { errors.Add(slip); return false; }   // the scanned paths decide: validation cannot see it where the AssetDatabase does not find a folder by another case
            if (uncovered > 0)
                Debug.LogWarning($"[ContentLink] {uncovered} asset(s) under '{profile.SourceRoot}' belong to no category of '{profile.Name}' and are not built.");

            // Catalogs in asset-path order, so the same profile always writes them the same way.
            var order = new List<int>();
            for (int i = 0; i < profile.Catalogs.Length; ++i) order.Add(i);
            order.Sort((a, b) => string.CompareOrdinal(profile.Catalogs[a].ResolvePath(), profile.Catalogs[b].ResolvePath()));

            bool changedSettings = false;
            foreach (int i in order)
            {
                var entry = profile.Catalogs[i];
                var path = entry.ResolvePath();
                if (string.IsNullOrEmpty(path)) { errors.Add($"category {i} of '{profile.Name}' has no catalog path"); return false; }

                var catalog = ContentDirectoryBuilder.LoadOrCreateCatalog(path);
                if (!entry.Catalog.isSet) { entry.Catalog.asset = catalog; changedSettings = true; }   // created on the first build; the profile now references it
                ContentDirectoryBuilder.Write(catalog, profile.SourceRoot, profile.NameStyle, groups[i]);
                catalogPaths?.Add(path);
            }
            if (changedSettings) ContentBuildSettings.instance.MarkDirty();
            return true;
        }

        // ───────────── the pure parts (tested directly) ─────────────

        /// <summary>
        /// A category the scanned paths — what <see cref="Partition"/> compares — match only in another case: the error naming their
        /// spelling; null when there is none. Validation refuses it first where the AssetDatabase finds a folder in any case.
        /// </summary>
        public static string CaseOnlyCategory(Profile profile, List<ContentDirectoryBuilder.Scanned> scanned)
        {
            var root = ContentLinkEditorUtility.Composed(profile.SourceRoot);
            foreach (var item in scanned)
            {
                var path = ContentLinkEditorUtility.Composed(item.Path);   // compared composed: the editor spells a path's folders decomposed on macOS
                var relative = path.Length > root.Length + 1 ? path.Substring(root.Length + 1) : "";
                string near = null;
                foreach (var entry in profile.Catalogs)
                {
                    var sub = ContentLinkEditorUtility.Composed(ContentBuildSettings.NormalizeSubFolder(entry?.SubFolder));
                    if (sub.Length == 0 || relative.StartsWith(sub + "/", StringComparison.Ordinal)) { near = null; break; }   // it has a home
                    if (near == null && relative.StartsWith(sub + "/", StringComparison.OrdinalIgnoreCase)) near = sub;
                }
                if (near != null)
                    return $"category '{near}' is spelled '{relative.Substring(0, near.Length)}' in the project — write it as '{relative.Substring(0, near.Length)}', and if the folder was renamed only in case, make version control record the rename: a build matches the category as written and would leave its assets out";
            }
            return null;
        }

        /// <summary>Splits the scanned assets by category sub folder. Overlap was refused by validation, so an asset lands in at most one group.</summary>
        public static List<ContentDirectoryBuilder.Scanned>[] Partition(Profile profile, List<ContentDirectoryBuilder.Scanned> scanned, out int uncovered)
        {
            var groups = new List<ContentDirectoryBuilder.Scanned>[profile.Catalogs.Length];
            var subs = new string[profile.Catalogs.Length];
            for (int i = 0; i < groups.Length; ++i)
            {
                groups[i] = new List<ContentDirectoryBuilder.Scanned>();
                subs[i] = ContentLinkEditorUtility.Composed(ContentBuildSettings.NormalizeSubFolder(profile.Catalogs[i]?.SubFolder));
            }

            uncovered = 0;
            var root = ContentLinkEditorUtility.Composed(profile.SourceRoot);
            foreach (var item in scanned)
            {
                var path = ContentLinkEditorUtility.Composed(item.Path);   // compared composed: the editor spells a path's folders decomposed on macOS
                var relative = path.Length > root.Length + 1 ? path.Substring(root.Length + 1) : "";
                int home = -1;
                for (int i = 0; i < subs.Length; ++i)
                    if (subs[i].Length == 0 || relative.StartsWith(subs[i] + "/", StringComparison.Ordinal)) { home = i; break; }
                if (home < 0) { ++uncovered; continue; }
                groups[home].Add(item);
            }
            return groups;
        }

        /// <summary>
        /// The engine parameters for a profile. Two rules measured on the engine: choosing a compression makes the
        /// engine write an archive whether or not UseArchive is set, so a flat build must pass Uncompressed; and
        /// DisableWriteTypeTree is refused unless SerializeUnityVersion comes with it.
        /// </summary>
        public static BuildContentDirectoryParameters Parameters(Profile profile, string[] catalogPaths)
        {
            var options = BuildContentOptions.None;
            if (profile.CleanBuildCache) options |= BuildContentOptions.CleanBuildCache;
            if (profile.FailBuildWhenErrorsLogged) options |= BuildContentOptions.FailBuildWhenErrorsLogged;
            if (profile.DisableWriteTypeTree) options |= BuildContentOptions.DisableWriteTypeTree | BuildContentOptions.SerializeUnityVersion;

            var compression = BuildCompression.Uncompressed;
            if (profile.Archive == ContentBuildSettings.ArchiveMode.Engine)
            {
                options |= BuildContentOptions.UseArchive;
                compression = Compression(profile.Compression);
            }
            // None and Packed build flat: the packer (Packed) applies the profile's compression itself.

            return new BuildContentDirectoryParameters
            {
                name = profile.Name,
                outputPath = profile.OutputPath,
                rootAssetPaths = catalogPaths,
                compression = compression,
                options = options,
            };
        }

        public static BuildCompression Compression(ContentBuildSettings.CompressionKind kind)
        {
            switch (kind)
            {
                case ContentBuildSettings.CompressionKind.LZ4: return BuildCompression.LZ4;
                case ContentBuildSettings.CompressionKind.LZMA: return BuildCompression.LZMA;
                default: return BuildCompression.Uncompressed;
            }
        }

        /// <summary>
        /// The guard alone, writing nothing: false (with the reason) when the folder exists and is neither empty
        /// nor a previous content build. Build runs it before the catalogs are written; PrepareOutput runs it again.
        /// </summary>
        public static bool CheckOutput(string outputPath, List<string> errors)
        {
            if (string.IsNullOrEmpty(outputPath)) { errors.Add("output path is empty"); return false; }
            if (!Directory.Exists(outputPath)) return true;

            bool previousBuild = File.Exists(Path.Combine(outputPath, ContentBuildManifest.HashFile));
            bool empty = true;
            foreach (var entry in Directory.GetFileSystemEntries(outputPath))
                if (!entry.EndsWith(".meta", StringComparison.Ordinal)) { empty = false; break; }
            if (!previousBuild && !empty)
            {
                errors.Add($"output folder '{outputPath}' is neither empty nor a previous content build — refusing to clear it");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Makes the output folder ready: everything but the marker goes, and the engine writes the marker anew in place. The
        /// engine itself removes only the top level's files of its own kinds (.cf, .archive, .resS, .resource) and those named
        /// by a hash, 32 hex digits before the first dot (measured on 6000.6) — a packed build's index would stay. A folder that
        /// is neither empty nor a previous content build is refused, never cleared. Build calls it only after the catalogs are
        /// written, so a failure before the engine leaves the previous build; an entry that cannot be removed fails it with the
        /// marker kept. A marker the engine could not write (read-only) goes with the rest.
        /// </summary>
        public static bool PrepareOutput(string outputPath, List<string> errors)
        {
            if (!CheckOutput(outputPath, errors)) return false;
            if (!Directory.Exists(outputPath)) { Directory.CreateDirectory(outputPath); return true; }
            foreach (var entry in Directory.GetFileSystemEntries(outputPath))
            {
                if (Path.GetFileName(entry) == ContentBuildManifest.HashFile && Writable(entry)) continue;   // kept: a clear or an engine cut short leaves a folder the next build clears again
                try
                {
                    if (Directory.Exists(entry)) Directory.Delete(entry, true);
                    else File.Delete(entry);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    errors.Add($"output folder '{outputPath}' could not be cleared — '{Path.GetFileName(entry)}': {e.Message}");
                    return false;
                }
            }
            return true;
        }

        static bool Writable(string path)
        {
            try { using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) return true; }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return false; }
        }

        /// <summary>Whether the output lands inside the project's Assets folder — given relative to the project, or absolute.</summary>
        public static bool InsideAssets(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) return false;
            var path = ContentLinkEditorUtility.Composed(outputPath.Replace('\\', '/').TrimEnd('/'));
            if (!Path.IsPathRooted(path)) return path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal);
            var assets = ContentLinkEditorUtility.Composed(Application.dataPath.Replace('\\', '/').TrimEnd('/'));   // a project's path may be spelled either way
            return path == assets || path.StartsWith(assets + "/", StringComparison.Ordinal);
        }

        /// <summary>The object-id hash the manifest keys loadables by, taken from the id's string form; null when the form is not the one we know.</summary>
        public static string ObjectIdHash(LoadableObjectId id)
        {
            var m = s_Oid.Match(id.ToString());
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// A build carries as load targets what its catalogs name, and — with no name — whatever its content, or an asset that content
        /// references, holds a LoadableObjectId or a LoadableSceneId of, every name of a catalog it holds included (measured on 6000.6: the
        /// dependency queries see none of the ids). One that another catalog of the project names is refused: with both directories
        /// registered, the one registered later serves it, and unregistering that one unloads it under its holder — a whole scene too
        /// (measured). Every catalog that names one gets its own error. One that no catalog names is a warning: an asset loads by its id only.
        /// The catalogs are read as they are saved: <see cref="BuildAll"/> updates every profile's first. False with the reasons added to
        /// <paramref name="errors"/>. Without a manifest (an Engine archive keeps it inside) nothing is checked.
        /// </summary>
        internal static bool CheckLoadTargets(ContentBuildManifest manifest, string sourceRoot, IReadOnlyList<string> catalogPaths, List<string> errors)
        {
            if (manifest?.Loadables == null) return true;
            var own = new HashSet<string>(catalogPaths, StringComparer.Ordinal);
            var named = new HashSet<string>(StringComparer.Ordinal);
            var namedScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (manifest.RootAssets != null) named.UnionWith(manifest.RootAssets);   // the catalogs themselves
            foreach (var path in catalogPaths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(path);
                if (catalog == null) continue;
                foreach (var entry in catalog.Entries) if (ObjectIdHash(entry.id) is string hash) named.Add(hash);
                foreach (var scene in catalog.Scenes) if (!string.IsNullOrEmpty(scene.guid)) namedScenes.Add(scene.guid);
            }
            var unnamed = new List<string>();
            foreach (var loadable in manifest.Loadables)
                if (loadable != null && !string.IsNullOrEmpty(loadable.ObjectIdHash) && !named.Contains(loadable.ObjectIdHash)) unnamed.Add(loadable.ObjectIdHash);
            var unnamedScenes = new List<ContentBuildManifest.LoadableSceneData>();
            if (manifest.LoadableScenes != null)
                foreach (var scene in manifest.LoadableScenes)
                    if (scene != null && !string.IsNullOrEmpty(scene.GUID) && !namedScenes.Contains(scene.GUID)) unnamedScenes.Add(scene);
            if (unnamed.Count == 0 && unnamedScenes.Count == 0) return true;

            // who names them: every other catalog of the project, in path order — looked up only when something is carried with no name
            var carried = new HashSet<string>(unnamed, StringComparer.Ordinal);
            var carriedScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scene in unnamedScenes) carriedScenes.Add(scene.GUID);
            var paths = new List<string>();
            foreach (var guid in AssetDatabase.FindAssets("t:ContentCatalog"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!own.Contains(path)) paths.Add(path);
            }
            paths.Sort((a, b) => string.CompareOrdinal(ContentLinkEditorUtility.Composed(a), ContentLinkEditorUtility.Composed(b)));   // the same order on every machine
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the hashes and scene GUIDs a catalog names
            bool refused = false;
            foreach (var path in paths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(path);
                if (catalog == null) continue;
                var names = new List<string>();
                int scenes = 0;
                foreach (var scene in catalog.Scenes)
                    if (!string.IsNullOrEmpty(scene.guid) && carriedScenes.Contains(scene.guid))
                    {
                        names.Add($"scene '{scene.name}' ({AssetDatabase.GUIDToAssetPath(scene.guid)})");
                        claimed.Add(scene.guid);
                        ++scenes;
                    }
                var seen = new HashSet<string>(StringComparer.Ordinal);   // an atlas's name for a sprite carries the sprite's own id: one asset, once
                foreach (var entry in catalog.Entries)
                    if (ObjectIdHash(entry.id) is string hash && carried.Contains(hash) && seen.Add(hash))
                    {
                        names.Add($"'{entry.name}' ({AssetDatabase.GUIDToAssetPath(entry.guid)})");
                        claimed.Add(hash);
                    }
                if (names.Count == 0) continue;
                int assets = names.Count - scenes;
                var what = assets > 0 && scenes > 0 ? $"{assets} asset(s) and {scenes} scene(s)" : assets > 0 ? $"{assets} asset(s)" : $"{scenes} scene(s)";
                errors.Add($"this build carries {what} that '{path}' names, with no name of its own — {Few(names)}: content under '{sourceRoot}', or an asset it references, holds that catalog or their LoadableObjectId or LoadableSceneId. With both directories registered, the one registered later serves them, and unregistering it unloads them under their holders. Hold a name, a ContentRef or a ContentSceneRef instead");
                refused = true;
            }
            var loose = new List<string>();
            foreach (var scene in unnamedScenes) if (!claimed.Contains(scene.GUID)) loose.Add($"scene '{scene.Path}'");
            foreach (var hash in unnamed) if (!claimed.Contains(hash)) loose.Add("oid-" + hash);
            if (loose.Count > 0)
                Debug.LogWarning($"[ContentLink] content under '{sourceRoot}', or an asset it references, holds the LoadableObjectId or LoadableSceneId of {loose.Count} load target(s) no catalog of this project names as it is saved now — {Few(loose)}: the build carries them with no name — an asset loads by that id only, and a scene does not load through ContentService. A profile whose catalog is out of date names nothing yet: build every profile together, which updates every catalog first.");
            return !refused;

            static string Few(List<string> list) => list.Count <= 5 ? string.Join(", ", list) : string.Join(", ", list.GetRange(0, 5)) + $" and {list.Count - 5} more";
        }

        // ───────────── post-processors ─────────────

        /// <summary>Runs the profile's post-processors in the listed order. A missing type or an exception fails the build; the output stays for inspection.</summary>
        static bool RunPostProcessors(Profile profile, ContentBuildResult result)
        {
            if (profile.PostProcessors == null) return true;
            foreach (var name in profile.PostProcessors)
            {
                if (string.IsNullOrEmpty(name)) continue;
                var type = ResolvePostProcessor(name);
                if (type == null) { result.Errors.Add($"post-processor '{name}' was not found"); return false; }
                object instance;
                try { instance = Activator.CreateInstance(type); }
                catch (Exception e)
                {
                    result.Errors.Add($"post-processor '{type.FullName}' could not be created: {e.Message}");
                    return false;
                }
                if (!(instance is IContentBuildPostProcess step))
                {
                    result.Errors.Add($"post-processor '{type.FullName}' does not implement IContentBuildPostProcess");
                    return false;
                }
                try { step.OnBuilt(result); }
                catch (Exception e)
                {
                    result.Errors.Add($"post-processor '{type.FullName}' threw {e.GetType().Name}: {e.Message}");
                    return false;
                }
            }
            return true;
        }

        public static Type ResolvePostProcessor(string name) => ResolveType<IContentBuildPostProcess>(name);

        /// <summary>
        /// A type the profile names, for post-processors and partitioners alike: by (assembly-)qualified name first,
        /// then by full name among the implementations TypeCache knows — never AppDomain.GetAssemblies(), which in
        /// Unity can hand back unloaded assemblies (UAC0005). Null when nothing matches; the caller checks the interface.
        /// </summary>
        public static Type ResolveType<T>(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            Type type;
            try { type = Type.GetType(name, false); }
            catch (Exception) { type = null; }   // a malformed assembly part ("A, B, C") throws even so — a name that resolves to nothing
            if (type != null) return type;
            foreach (var candidate in TypeCache.GetTypesDerivedFrom<T>())
                if (candidate.FullName == name || candidate.AssemblyQualifiedName == name) return candidate;
            return null;
        }

        /// <summary>Concrete, constructible implementations of an interface, sorted by full name — what the window offers.</summary>
        public static List<Type> Implementations<T>()
        {
            var list = new List<Type>();
            foreach (var t in TypeCache.GetTypesDerivedFrom<T>())
                if (t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && t.GetConstructor(Type.EmptyTypes) != null) list.Add(t);
            list.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));
            return list;
        }

        // ───────────── result ─────────────

        static void Collect(ContentBuildResult result, Profile profile, List<string> catalogPaths)
        {
            var files = new List<string>();
            var archives = new List<string>();
            foreach (var f in Directory.GetFiles(profile.OutputPath, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".meta", StringComparison.Ordinal)) continue;
                var relative = f.Substring(profile.OutputPath.Length).TrimStart('/', '\\').Replace('\\', '/');
                files.Add(relative);
                if (relative.EndsWith(".archive", StringComparison.Ordinal)) archives.Add(relative);
            }
            files.Sort(StringComparer.Ordinal);
            archives.Sort(StringComparer.Ordinal);
            result.Files = files.ToArray();
            result.Archives = archives.ToArray();

            result.Manifest = ContentBuildManifest.Read(profile.OutputPath, out var manifestPath);
            result.ManifestPath = manifestPath;
            if (result.Manifest?.Loadables == null) return;

            // The manifest keys loadables by the hash LoadableObjectId prints as "oid-…" — that link holds (measured).
            // A loadable's file on disk is SerializedFiles[SerializedFile].ContentHash + ".cf" (measured on 6000.6);
            // SerializedFiles[i].ID is only the name other files reference it by.
            var fileOf = new Dictionary<string, string>(StringComparer.Ordinal);   // oid hash → file on disk (or null)
            var onDisk = new HashSet<string>(files, StringComparer.Ordinal);
            foreach (var loadable in result.Manifest.Loadables)
            {
                if (string.IsNullOrEmpty(loadable.ObjectIdHash)) continue;
                string file = null;
                if (result.Manifest.SerializedFiles != null && (uint)loadable.SerializedFile < (uint)result.Manifest.SerializedFiles.Length)
                    file = ContentBuildManifest.FileOf(result.Manifest.SerializedFiles[loadable.SerializedFile]);
                fileOf[loadable.ObjectIdHash] = file != null && onDisk.Contains(file) ? file : null;
            }
            foreach (var path in catalogPaths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(path);
                if (catalog == null) continue;
                foreach (var entry in catalog.Entries)
                {
                    var hash = ObjectIdHash(entry.id);
                    if (hash == null || !fileOf.TryGetValue(hash, out var file)) continue;
                    result.BuiltNames.Add(entry.name);
                    result.Loadables.Add(new LoadableInfo(path, entry.name, hash, file));
                }
            }
            result.BuiltNames.Sort(StringComparer.Ordinal);
            result.Loadables.Sort((a, b) => { int c = string.CompareOrdinal(a.Catalog, b.Catalog); return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name); });
        }

        static ContentBuildResult Fail(ContentBuildResult result, List<string> errors)
        {
            if (errors != null) result.Errors.AddRange(errors);
            result.Succeeded = false;
            return result;
        }

        static void Progress(bool show, string stage, float t)
        {
            if (show) EditorUtility.DisplayProgressBar(k_Title, stage, t);
        }

        /// <summary>The last result lives in SessionState: it must not touch the shared settings file.</summary>
        static void Record(ContentBuildResult r)
        {
            SessionState.SetString(SessionKey + ".profile", r.ProfileName);
            SessionState.SetString(SessionKey + ".output", r.OutputPath);
            SessionState.SetString(SessionKey + ".target", r.Target.ToString());
            SessionState.SetBool(SessionKey + ".ok", r.Succeeded);
            SessionState.SetInt(SessionKey + ".files", r.Files.Length);
            SessionState.SetFloat(SessionKey + ".ms", (float)r.Milliseconds);
            SessionState.SetString(SessionKey + ".errors", string.Join("\n", r.Errors));
            SessionState.SetString(SessionKey + ".time", DateTime.Now.ToString("s"));
        }

        static void Summary(ContentBuildResult r, Profile profile)
        {
            var mode = profile == null ? "?" : profile.Archive == ContentBuildSettings.ArchiveMode.Engine ? "Engine — load targets not checked: the manifest is inside the archive" : profile.Archive.ToString();
            if (r.Succeeded)
                Debug.Log($"[ContentLink] built '{r.ProfileName}' → {r.OutputPath} ({r.Files.Length} files, {r.Archives.Length} archives, {r.BuiltNames.Count} names, {r.Milliseconds:F0} ms, target {r.Target}, {mode})");
            else
                Debug.LogError($"[ContentLink] build of '{r.ProfileName}' failed ({r.Milliseconds:F0} ms, target {r.Target}):\n  " + string.Join("\n  ", r.Errors));
        }
    }
}
