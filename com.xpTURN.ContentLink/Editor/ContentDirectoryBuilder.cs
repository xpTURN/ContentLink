using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.U2D;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Registers every asset under SourceRoot in the ContentCatalog as "relative/path/without-extension", or with the
    /// extension when the name style says so (sprite sub-assets as "name[sub]", scenes as scene entries) and builds the
    /// content directory.
    ///
    /// The paths and the name style come from the active build profile (ContentBuildSettings). The four static
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
        static ContentNameStyle? s_NameStyle;

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

        /// <summary>The active profile's name style; a write sets a session override, like the paths.</summary>
        public static ContentNameStyle NameStyle
        {
            get => s_NameStyle ?? ContentBuildSettings.instance.Active.NameStyle;
            set => s_NameStyle = value;
        }

        /// <summary>Drops the session overrides, so the properties read the active profile again.</summary>
        public static void ClearOverrides() { s_SourceRoot = s_CatalogPath = s_OutputPath = null; s_NameStyle = null; }

        /// <summary>True while a session override hides the active profile from this static view — the build window says so.</summary>
        public static bool HasOverrides => s_SourceRoot != null || s_CatalogPath != null || s_OutputPath != null || s_NameStyle != null;

        /// <summary>
        /// The legacy single-catalog build on the static view: updates the catalog and builds the content directory.
        /// Returns null when the output folder was refused, an archived build's leftovers could not be removed, the catalog
        /// update failed, or the build carried a load target another catalog names (all logged — that last one leaves the output). The output folder is guarded as ContentBuildRunner does it but not cleared: the engine
        /// replaces its own files, an archived build's index, hash and archives go first, and anything else stays.
        /// The menu items go through ContentBuildRunner, which builds every category of the active profile.
        /// </summary>
        public static BuildReport Build()
        {
            var errors = new List<string>();
            if (!ContentBuildRunner.CheckOutput(OutputPath, errors)) return Refused(errors);   // before anything is written
            if (!UpdateCatalog()) return null;
            if (Directory.Exists(OutputPath))
                foreach (var entry in Directory.GetFiles(OutputPath))   // the engine keeps an archived build's index and hash, and takes an archive but not its .meta
                {
                    var name = Path.GetFileName(entry);
                    if (name != ContentArchives.IndexFile && name != ContentArchives.HashFile && !name.EndsWith(".archive", System.StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        File.Delete(entry);
                        if (File.Exists(entry + ".meta")) File.Delete(entry + ".meta");
                    }
                    catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException)
                    {
                        errors.Add($"output folder '{OutputPath}' could not be cleared — '{name}': {e.Message}");
                        return Refused(errors);
                    }
                }
            var report = BuildPipeline.BuildContentDirectory(new BuildContentDirectoryParameters
            {
                outputPath = OutputPath,
                rootAssetPaths = new[] { CatalogPath },
            });
            if (report == null || report.summary.result != BuildResult.Succeeded) return report;
            if (!ContentBuildRunner.CheckLoadTargets(ContentBuildManifest.Read(OutputPath, out _), SourceRoot, new[] { CatalogPath }, errors)) return Refused(errors);
            return report;
        }

        static BuildReport Refused(List<string> errors)
        {
            foreach (var e in errors) Debug.LogError($"[ContentLink] {e}");
            return null;
        }

        /// <summary>
        /// Scans SourceRoot into the catalog at CatalogPath. Scan and Write are separate so a profile can scan its
        /// root once and write several catalogs from it; this is the two of them composed.
        /// </summary>
        public static bool UpdateCatalog()
        {
            if (ContentLinkEditorUtility.IsUnder(SourceRoot, CatalogPath))   // what Validate refuses for a profile, said before the catalog is made: a refused scan would leave it under the root
            {
                Debug.LogError($"[ContentLink] catalog '{CatalogPath}' lies under the source root '{SourceRoot}' and would catalogue itself — set ContentDirectoryBuilder.CatalogPath outside the root");
                return false;
            }
            var catalog = LoadOrCreateCatalog(CatalogPath);   // created before the scan, so a refused scan still leaves the (empty) catalog asset behind
            var scanned = new List<Scanned>();
            if (!Scan(SourceRoot, NameStyle, scanned)) return false;
            Write(catalog, SourceRoot, NameStyle, scanned);
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
        /// The name style is looked up for the root (<see cref="ContentLinkEditorUtility.NameStyleOf"/>). Use it with the
        /// <see cref="Write(ContentCatalog, string, IEnumerable{Scanned})"/> that looks it up too: a scan given a style and a
        /// write that looks one up can leave a catalog that records another style than its names have.
        /// </summary>
        public static bool Scan(string sourceRoot, List<Scanned> results)
            => Scan(sourceRoot, ContentLinkEditorUtility.NameStyleOf(sourceRoot), results);

        /// <summary><see cref="Scan(string, List{Scanned})"/> with the name style given: what is built never depends on a lookup.</summary>
        public static bool Scan(string sourceRoot, ContentNameStyle style, List<Scanned> results)
        {
            var seen = new Dictionary<string, string>();   // one name space for assets and scenes
            var sprites = new HashSet<Object>();   // every sprite the catalog names, by its own name or an atlas's — for the atlas checks
            var packedBy = new Dictionary<Object, List<string>>();   // a sprite → the atlases under the root that name it
            var mains = new List<Object>();   // every catalogued asset and scene — the atlas checks follow what they use
            bool rootHasAtlas = false;
            bool ok = true;

            foreach (var guid in AssetDatabase.FindAssets("", new[] { sourceRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;

                var main = AssetDatabase.LoadMainAssetAtPath(path);
                if (main == null) continue;
                if (main is ContentCatalog)
                {
                    Debug.LogError($"[ContentLink] '{path}' is a ContentCatalog under the source root '{sourceRoot}': a catalog is not content — built into this directory, it would bring every asset it names along. Move it out of the root");
                    ok = false;
                    continue;
                }
                mains.Add(main);
                var name = ContentLinkEditorUtility.ToName(sourceRoot, path, style);
                if (main is SceneAsset)
                {
                    if (!Unique(seen, name, path)) { ok = false; continue; }
                    results.Add(new Scanned
                    {
                        Path = path,
                        IsScene = true,
                        Scene = new ContentCatalog.SceneEntry { name = name, id = LoadableSceneIdEditorUtility.CreateLoadableSceneId(path), guid = guid },
                    });
                    continue;
                }

                ok &= Add(results, seen, name, main, path, guid, "");
                foreach (var sub in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                    if (sub is Sprite)
                    {
                        ok &= Add(results, seen, ContentLinkEditorUtility.ToName(sourceRoot, path, sub, style), sub, path, guid, ContentLinkEditorUtility.Composed(sub.name));
                        sprites.Add(sub);
                    }
                if (main is SpriteAtlas atlas)
                {
                    rootHasAtlas = true;
                    ok &= AddAtlas(results, seen, sourceRoot, style, path, guid, atlas, sprites, packedBy);
                }
            }
            ok &= CheckAtlases(sourceRoot, sprites, packedBy, rootHasAtlas, mains);
            return ok;
        }

        /// <summary>
        /// "atlas[sprite]" for every sprite a V2 atlas packs, with the sprite's own id — the original sprite is what a build
        /// with the packer on binds to the page. The entry's guid and sub are the atlas's GUID and the sprite's name, so a reference
        /// (atlas GUID, sprite) migrated from Addressables reaches it. A variant names nothing: its sprites are bound to the master.
        /// </summary>
        static bool AddAtlas(List<Scanned> results, Dictionary<string, string> seen, string sourceRoot, ContentNameStyle style, string atlasPath, string atlasGuid,
            SpriteAtlas atlas, HashSet<Object> sprites, Dictionary<Object, List<string>> packedBy)
        {
            if (ContentLinkEditorUtility.IsV1AtlasPath(atlasPath)) return true;   // no names: CheckAtlases refuses what it packs
            if (atlas.isVariant)
            {
                Debug.LogWarning($"[ContentLink] '{atlasPath}' is a variant atlas: it names no sprites — its sprites are bound to the master's page, name them through the master");
                return true;
            }
            bool ok = true;
            foreach (var sprite in ContentLinkEditorUtility.ExpandPackables(atlas))
            {
                var spritePath = AssetDatabase.GetAssetPath(sprite);
                ok &= Add(results, seen, ContentLinkEditorUtility.ToName(sourceRoot, atlasPath, sprite, style), sprite, atlasPath, atlasGuid, ContentLinkEditorUtility.Composed(sprite.name),
                    $"{atlasPath} ({spritePath})");
                sprites.Add(sprite);
                if (!packedBy.TryGetValue(sprite, out var atlases)) packedBy[sprite] = atlases = new List<string>();
                atlases.Add(atlasPath);
            }
            return ok;
        }

        /// <summary>
        /// What would make a catalogued sprite load without its texture. A V1 atlas's pages are not in a content directory
        /// (measured: packed, texture null), and outside the V2 packer modes a V2 atlas is not packed at all (the same) — anywhere in
        /// the project, since an atlas outside the root packs sprites under it too. With the packer off the sprites keep their own
        /// textures, but an atlas under the root would name sprites of an atlas built empty (measured: no error, no warning).
        /// Under a V2 mode a sprite the catalogued assets and scenes use counts as much as one the catalog names: the build carries
        /// it too, and a player draws it without a texture all the same — as it gives none from a V1 atlas they hold, or from one
        /// under the root, which is refused outright (measured). Under a V1 mode a player drew the sprites the content uses with
        /// their own textures (measured), so that mode keeps to the catalogued ones.
        /// </summary>
        static bool CheckAtlases(string sourceRoot, HashSet<Object> sprites, Dictionary<Object, List<string>> packedBy, bool rootHasAtlas, List<Object> mains)
        {
            bool ok = true;
            foreach (var kv in packedBy)
                if (kv.Value.Count > 1)
                    Debug.LogWarning($"[ContentLink] '{AssetDatabase.GetAssetPath(kv.Key)}' ({kv.Key.name}) is packed by {kv.Value.Count} atlases ({string.Join(", ", kv.Value)}): each of their names loads the same sprite, bound to the one atlas the engine picks");

            var mode = EditorSettings.spritePackerMode;
            if (mode == SpritePackerMode.Disabled)
            {
                if (!rootHasAtlas) return ok;
                Debug.LogError($"[ContentLink] the Sprite Packer mode is Disabled and '{sourceRoot}' holds sprite atlases: they would be built empty — set Project Settings > Editor > Sprite Packer > Mode to Sprite Atlas V2");
                return false;
            }
            bool v2 = mode == SpritePackerMode.SpriteAtlasV2 || mode == SpritePackerMode.SpriteAtlasV2Build;
            if (sprites.Count == 0 && mains.Count == 0) return ok;
            Dictionary<Object, string> used = null, held = null;   // a sprite a catalogued asset uses but the catalog does not name, a V1 atlas it holds → that asset; collected once an atlas needs them
            foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlas"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                bool v1 = ContentLinkEditorUtility.IsV1AtlasPath(path);
                if (v2 && !v1) continue;
                if (v2 && ContentLinkEditorUtility.IsUnder(sourceRoot, path))
                {
                    Debug.LogError($"[ContentLink] '{path}' is a V1 sprite atlas under the source root: content directories do not carry its pages — loaded by its name, it gives sprites without a texture. Move it to Sprite Atlas V2, or out of the root");
                    ok = false;
                    continue;
                }
                var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                if (v2 && used == null) Uses(sourceRoot, mains, sprites, out used, out held);
                Object hit = null;
                string user = null;
                int count = 0;
                foreach (var sprite in ContentLinkEditorUtility.ExpandPackables(atlas))
                {
                    if (sprites.Contains(sprite)) { hit ??= sprite; ++count; }
                    else if (used != null && used.TryGetValue(sprite, out var by)) { if (hit == null) { hit = sprite; user = by; } ++count; }
                }
                if (hit == null && held != null && held.TryGetValue(atlas, out var holder))
                {
                    Debug.LogError($"[ContentLink] '{path}' is a V1 sprite atlas that '{holder}' holds: content directories do not carry its pages, so the sprites it gives would load without a texture. Move it to Sprite Atlas V2");
                    ok = false;
                    continue;
                }
                if (hit == null) continue;
                var what = $"'{path}' packs {count} sprite(s) the content uses ('{AssetDatabase.GetAssetPath(hit)}' {hit.name} first{(user != null ? $", which '{user}' uses" : "")})";
                Debug.LogError(v2
                    ? $"[ContentLink] {what}: it is a V1 sprite atlas — content directories do not carry its textures, so those sprites would load without one. Move it to Sprite Atlas V2"
                    : $"[ContentLink] the Sprite Packer mode is {mode}: {what}, which would load from a content directory without a texture — set Project Settings > Editor > Sprite Packer > Mode to Sprite Atlas V2");
                ok = false;
            }
            return ok;
        }

        /// <summary>
        /// The sprites the assets and scenes use that are not named already, and the V1 atlases they hold → the first of them that
        /// does. An asset's files say what it reaches — not through a packed sprite's link to its atlas, which would count every
        /// sprite the atlas packs (measured); its objects say which sprites (a material holds a texture, not a sprite of it), and
        /// are looked at only when its files reach a V1 atlas outside the root or what one packs.
        /// </summary>
        static void Uses(string sourceRoot, List<Object> mains, HashSet<Object> named, out Dictionary<Object, string> used, out Dictionary<Object, string> held)
        {
            used = new Dictionary<Object, string>();
            held = new Dictionary<Object, string>();
            var v1 = new HashSet<string>();   // the V1 atlases outside the root, and the files of the sprites they pack
            foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlas"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!ContentLinkEditorUtility.IsV1AtlasPath(path) || ContentLinkEditorUtility.IsUnder(sourceRoot, path)) continue;
                v1.Add(path);
                foreach (var sprite in ContentLinkEditorUtility.ExpandPackables(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path))) v1.Add(AssetDatabase.GetAssetPath(sprite));
            }
            foreach (var main in mains)
            {
                var path = AssetDatabase.GetAssetPath(main);
                var files = new HashSet<string>(AssetDatabase.GetDependencies(path, true));
                if (!files.Overlaps(v1)) continue;
                foreach (var dependency in EditorUtility.CollectDependencies(new[] { main }))
                {
                    if (dependency is Sprite sprite && !named.Contains(sprite) && !used.ContainsKey(sprite) && files.Contains(AssetDatabase.GetAssetPath(sprite))) used.Add(sprite, path);
                    if (dependency is SpriteAtlas atlas && !held.ContainsKey(atlas) && files.Contains(AssetDatabase.GetAssetPath(atlas))) held.Add(atlas, path);
                }
            }
        }

        static bool Add(List<Scanned> results, Dictionary<string, string> seen, string name, Object obj, string path, string guid, string sub, string shown = null)
        {
            if (!Unique(seen, name, shown ?? path)) return false;
            results.Add(new Scanned
            {
                Path = path,
                Entry = new ContentCatalog.Entry { name = name, id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(obj), guid = guid, sub = sub },
            });
            return true;
        }

        static bool Unique(Dictionary<string, string> seen, string name, string path)
        {
            if (name.StartsWith(ContentService.ResourcesPrefix, System.StringComparison.Ordinal))
            {
                Debug.LogError($"[ContentLink] '{name}' ({path}) starts with '{ContentService.ResourcesPrefix}', which names a Resources asset — rename the folder");
                return false;
            }
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
        /// root they were named against. The name style it records is looked up for the root — use it with the
        /// <see cref="Scan(string, List{Scanned})"/> that looks it up too.
        /// </summary>
        public static void Write(ContentCatalog catalog, string sourceRoot, IEnumerable<Scanned> items)
            => Write(catalog, sourceRoot, ContentLinkEditorUtility.NameStyleOf(sourceRoot), items);

        /// <summary><see cref="Write(ContentCatalog, string, IEnumerable{Scanned})"/> that records the style the items were named with.</summary>
        public static void Write(ContentCatalog catalog, string sourceRoot, ContentNameStyle style, IEnumerable<Scanned> items)
        {
            if (catalog.NameStyle != style && (catalog.Entries.Count > 0 || catalog.Scenes.Count > 0))
                Debug.LogWarning($"[ContentLink] '{AssetDatabase.GetAssetPath(catalog)}': the names change from {catalog.NameStyle} to {style} — links, code and data that hold the old names no longer resolve (ContentRef fields are unaffected).");
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
            catalog.EditorSetNameStyle(style);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
            ContentLinkEditorUtility.Invalidate();   // saving an existing catalog raises no projectChanged (measured on 6000.6)
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
