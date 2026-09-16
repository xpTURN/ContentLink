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

        /// <summary>Builds every profile in order and stops at the first failure.</summary>
        public static List<ContentBuildResult> BuildAll(bool showProgress = true)
        {
            var results = new List<ContentBuildResult>();
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

                if (!PrepareOutput(profile.OutputPath, result.Errors)) return Fail(result, null);   // the clear last: a failure before the engine leaves the previous build usable
                touched = true;

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
            if (!ContentDirectoryBuilder.Scan(profile.SourceRoot, scanned))
            {
                errors.Add($"'{profile.SourceRoot}' has duplicate names (see the errors above)");
                return false;
            }

            var groups = Partition(profile, scanned, out int uncovered);
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
                ContentDirectoryBuilder.Write(catalog, profile.SourceRoot, groups[i]);
                catalogPaths?.Add(path);
            }
            if (changedSettings) ContentBuildSettings.instance.MarkDirty();
            return true;
        }

        // ───────────── the pure parts (tested directly) ─────────────

        /// <summary>Splits the scanned assets by category sub folder. Overlap was refused by validation, so an asset lands in at most one group.</summary>
        public static List<ContentDirectoryBuilder.Scanned>[] Partition(Profile profile, List<ContentDirectoryBuilder.Scanned> scanned, out int uncovered)
        {
            var groups = new List<ContentDirectoryBuilder.Scanned>[profile.Catalogs.Length];
            var subs = new string[profile.Catalogs.Length];
            for (int i = 0; i < groups.Length; ++i)
            {
                groups[i] = new List<ContentDirectoryBuilder.Scanned>();
                subs[i] = ContentBuildSettings.NormalizeSubFolder(profile.Catalogs[i]?.SubFolder);
            }

            uncovered = 0;
            foreach (var item in scanned)
            {
                var relative = item.Path.Length > profile.SourceRoot.Length + 1 ? item.Path.Substring(profile.SourceRoot.Length + 1) : "";
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
        /// Makes the output folder ready. The engine cleans a previous build itself; this is the guard for
        /// a wrong path: a folder that is neither empty nor a previous content build is refused, never cleared.
        /// Build calls it only after the catalogs are written, so a failure before the engine leaves the previous build.
        /// </summary>
        public static bool PrepareOutput(string outputPath, List<string> errors)
        {
            if (!CheckOutput(outputPath, errors)) return false;
            if (!Directory.Exists(outputPath)) { Directory.CreateDirectory(outputPath); return true; }
            foreach (var entry in Directory.GetFileSystemEntries(outputPath))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, true);
                else File.Delete(entry);
            }
            return true;
        }

        /// <summary>Whether the output lands inside the project's Assets folder — given relative to the project, or absolute.</summary>
        public static bool InsideAssets(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) return false;
            var path = outputPath.Replace('\\', '/').TrimEnd('/');
            if (!Path.IsPathRooted(path)) return path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal);
            var assets = Application.dataPath.Replace('\\', '/').TrimEnd('/');
            return path == assets || path.StartsWith(assets + "/", StringComparison.Ordinal);
        }

        /// <summary>The object-id hash the manifest keys loadables by, taken from the id's string form; null when the form is not the one we know.</summary>
        public static string ObjectIdHash(LoadableObjectId id)
        {
            var m = s_Oid.Match(id.ToString());
            return m.Success ? m.Groups[1].Value : null;
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
            var type = Type.GetType(name, false);
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
            var mode = profile?.Archive.ToString() ?? "?";
            if (r.Succeeded)
                Debug.Log($"[ContentLink] built '{r.ProfileName}' → {r.OutputPath} ({r.Files.Length} files, {r.Archives.Length} archives, {r.BuiltNames.Count} names, {r.Milliseconds:F0} ms, target {r.Target}, {mode})");
            else
                Debug.LogError($"[ContentLink] build of '{r.ProfileName}' failed ({r.Milliseconds:F0} ms, target {r.Target}):\n  " + string.Join("\n  ", r.Errors));
        }
    }
}
