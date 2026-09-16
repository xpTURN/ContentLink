using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Registers every asset under SourceRoot in the ContentCatalog as "relative/path/without-extension"
    /// (sprite sub-assets as "name[sub]", scenes as scene entries) and builds the content directory.
    ///
    /// The paths come from the active build profile (ContentBuildSettings). The three static
    /// properties are a view of it: reading gives the profile's value, writing sets a session-only override that
    /// never reaches the settings file. Existing scripts and the tests keep working; the profile stays the truth.
    /// </summary>
    public static class ContentDirectoryBuilder
    {
        // The values the static fields had before profiles existed. A project without settings gets its "Main"
        // profile from these (ContentBuildSettings.EnsureDefaults), which is why they are constants and not the
        // properties below — reading the properties there would recurse into the settings being created.
        public const string DefaultSourceRoot = "Assets/Content";
        public const string DefaultCatalogPath = "Assets/ContentCatalog.asset";
        public const string DefaultOutputPath = "Assets/StreamingAssets/MainContent";

        static string s_SourceRoot, s_CatalogPath, s_OutputPath;   // session overrides; null = read the active profile

        public static string SourceRoot
        {
            get => s_SourceRoot ?? ContentBuildSettings.instance.Active.SourceRoot;
            set => s_SourceRoot = value;
        }

        /// <summary>The active profile's first catalog; a profile that has none yet answers the legacy path.</summary>
        public static string CatalogPath
        {
            get
            {
                if (s_CatalogPath != null) return s_CatalogPath;
                var catalogs = ContentBuildSettings.instance.Active.Catalogs;
                var path = catalogs != null && catalogs.Length > 0 ? catalogs[0]?.ResolvePath() : null;
                return string.IsNullOrEmpty(path) ? DefaultCatalogPath : path;
            }
            set => s_CatalogPath = value;
        }

        public static string OutputPath
        {
            get => s_OutputPath ?? ContentBuildSettings.instance.Active.OutputPath;
            set => s_OutputPath = value;
        }

        /// <summary>Drops the session overrides, so the properties read the active profile again.</summary>
        public static void ClearOverrides() => s_SourceRoot = s_CatalogPath = s_OutputPath = null;

        /// <summary>True while a session override hides the active profile from this static view — the build window says so.</summary>
        public static bool HasOverrides => s_SourceRoot != null || s_CatalogPath != null || s_OutputPath != null;

        /// <summary>
        /// The legacy single-catalog build on the static view: updates the catalog and builds the content directory.
        /// Returns null when the catalog update failed (already logged). The menu items go through
        /// ContentBuildRunner, which builds every category of the active profile.
        /// </summary>
        public static BuildReport Build()
        {
            if (!UpdateCatalog()) return null;
            return BuildPipeline.BuildContentDirectory(new BuildContentDirectoryParameters
            {
                outputPath = OutputPath,
                rootAssetPaths = new[] { CatalogPath },
            });
        }

        /// <summary>
        /// Scans SourceRoot into the catalog at CatalogPath. Scan and Write are separate so a profile can scan its
        /// root once and write several catalogs from it; this is the two of them composed.
        /// </summary>
        public static bool UpdateCatalog()
        {
            var catalog = LoadOrCreateCatalog(CatalogPath);   // created before the scan, so a refused scan still leaves the (empty) catalog asset behind
            var scanned = new List<Scanned>();
            if (!Scan(SourceRoot, scanned)) return false;
            Write(catalog, SourceRoot, scanned);
            return true;
        }

        // ───────────── scan ─────────────

        /// <summary>One catalogued thing and where it came from. Path is what a category filter partitions on.</summary>
        public struct Scanned
        {
            public string Path;
            public bool IsScene;
            public ContentCatalog.Entry Entry;
            public ContentCatalog.SceneEntry Scene;
        }

        /// <summary>
        /// Every asset and scene under <paramref name="sourceRoot"/>, named relative to it. False when a name is
        /// used twice (logged): the runtime keeps one name dictionary across every registered directory, so a
        /// duplicate would silently lose one of the two.
        /// </summary>
        public static bool Scan(string sourceRoot, List<Scanned> results)
        {
            var seen = new Dictionary<string, string>();   // one name space for assets and scenes
            bool ok = true;

            foreach (var guid in AssetDatabase.FindAssets("", new[] { sourceRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;

                var main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main == null) continue;
                var name = ContentLinkEditorUtility.ToName(sourceRoot, path);
                if (main is SceneAsset)
                {
                    if (!Unique(seen, name, path)) { ok = false; continue; }
                    results.Add(new Scanned
                    {
                        Path = path,
                        IsScene = true,
                        Scene = new ContentCatalog.SceneEntry { name = name, id = LoadableSceneIdEditorUtility.CreateLoadableSceneId(path) },
                    });
                    continue;
                }

                ok &= Add(results, seen, name, main, path);
                foreach (var sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                    if (sub is Sprite)
                        ok &= Add(results, seen, ContentLinkEditorUtility.ToName(sourceRoot, path, sub), sub, path);
            }
            return ok;
        }

        static bool Add(List<Scanned> results, Dictionary<string, string> seen, string name, Object obj, string path)
        {
            if (!Unique(seen, name, path)) return false;
            results.Add(new Scanned
            {
                Path = path,
                Entry = new ContentCatalog.Entry { name = name, id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(obj) },
            });
            return true;
        }

        static bool Unique(Dictionary<string, string> seen, string name, string path)
        {
            if (seen.TryGetValue(name, out var prev))
            {
                Debug.LogError($"[ContentLink] duplicate name '{name}': {prev} / {path}");
                return false;
            }
            seen.Add(name, path);
            return true;
        }

        // ───────────── write ─────────────

        public static ContentCatalog LoadOrCreateCatalog(string catalogPath)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(catalogPath);
            if (catalog != null) return catalog;
            EnsureFolder(Path.GetDirectoryName(catalogPath));
            catalog = ScriptableObject.CreateInstance<ContentCatalog>();
            AssetDatabase.CreateAsset(catalog, catalogPath);
            return catalog;
        }

        /// <summary>
        /// Makes sure a folder under Assets/ exists and is imported, creating the missing segments through the
        /// AssetDatabase so they are known at once. A folder that is on disk but not imported yet is imported rather
        /// than created again (CreateFolder would put a "Name 1" next to it — measured on 6000.6). Anything outside
        /// Assets/ is left alone: CreateAsset cannot write there, and ContentBuildSettings.Validate refuses such a
        /// path before a build starts.
        /// </summary>
        public static void EnsureFolder(string folder)
        {
            folder = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if (folder.Length == 0 || AssetDatabase.IsValidFolder(folder)) return;
            if (!folder.StartsWith("Assets/", System.StringComparison.Ordinal)) return;
            int slash = folder.LastIndexOf('/');
            EnsureFolder(folder.Substring(0, slash));
            if (Directory.Exists(folder)) AssetDatabase.ImportAsset(folder);
            else AssetDatabase.CreateFolder(folder.Substring(0, slash), folder.Substring(slash + 1));
        }

        /// <summary>
        /// Stores the scanned items in the catalog, sorted so the same input gives the same asset, and records the
        /// root they were named against.
        /// </summary>
        public static void Write(ContentCatalog catalog, string sourceRoot, IEnumerable<Scanned> items)
        {
            var entries = new List<ContentCatalog.Entry>();
            var scenes = new List<ContentCatalog.SceneEntry>();
            foreach (var item in items)
            {
                if (item.IsScene) scenes.Add(item.Scene);
                else entries.Add(item.Entry);
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.name, b.name));   // deterministic across builds
            scenes.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            catalog.EditorSetEntries(entries.ToArray());
            catalog.EditorSetScenes(scenes.ToArray());
            catalog.EditorSetSourceRoot(sourceRoot);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            Debug.Log($"[ContentLink] catalog updated: {entries.Count} entries, {scenes.Count} scenes");
        }
    }

    static class ContentScopeMenu
    {
        [MenuItem("Window/ContentLink/Dump Scope Tree")]
        static void Dump()
        {
            if (!EditorApplication.isPlaying) { Debug.Log("[ContentLink] enter Play Mode to inspect scopes."); return; }
            Debug.Log($"[ContentLink] live assets: {ContentService.LiveAssetCount}\n{ContentScope.DumpTree()}");
        }
    }
}
