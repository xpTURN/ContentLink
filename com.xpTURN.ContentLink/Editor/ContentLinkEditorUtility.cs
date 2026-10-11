using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Loading;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>What a serialized link name is, as far as the catalogs can tell.</summary>
    public enum NameStatus
    {
        Ok,
        Empty,          // no name yet — a normal authoring state, not an error
        NoCatalog,      // the project has no ContentCatalog at all
        NotInCatalog,   // the name is not in any catalog (the catalog may simply be out of date)
        TypeMismatch,   // the catalog resolves the name to an object the field cannot hold
        Missing,        // a reference's GUID has no asset, or its asset has no sub-asset of that name
    }

    /// <summary>
    /// Name ↔ asset conversion for the inspector drawers, and the catalog lookups they need.
    /// Deliberately free of IMGUI types so the dotnet verification harness can compile and exercise it.
    ///
    /// Two directions, both catalog-first:
    ///   asset → name : TryGetNameForAsset  (LoadableObjectId reverse index; falls back to the path rule)
    ///   name → asset : ResolveAsset        (catalog index; falls back to a path search under the content roots)
    /// </summary>
    public static class ContentLinkEditorUtility
    {
        // ───────────── name rules (shared with ContentDirectoryBuilder) ─────────────

        /// <summary>
        /// A path or a name in the Unicode form names are made and compared in: composed (NFC) — what an IME types and git keeps. The
        /// macOS editor spells a path's folders decomposed and its file, and an object's name, as on disk (measured on 6000.6): a name
        /// made from those as they come would differ by machine and miss the name typed for it. An ASCII string is returned as it is.
        /// </summary>
        internal static string Composed(string s)
        {
            if (s == null || Ascii(s)) return s;
            return s.IsNormalized(System.Text.NormalizationForm.FormC) ? s : s.Normalize(System.Text.NormalizationForm.FormC);
        }

        static bool Ascii(string s) { foreach (var c in s) if (c >= 0x80) return false; return true; }

        /// <summary>"Assets/Content/Icons/sword.png" under "Assets/Content" → "Icons/sword", or "Icons/sword.png" with <see cref="ContentNameStyle.PathWithExtension"/>. Composed (<see cref="Composed"/>).</summary>
        internal static string ToName(string sourceRoot, string assetPath, ContentNameStyle style)
        {
            sourceRoot = Composed(sourceRoot); assetPath = Composed(assetPath);   // cut in one form: a root typed composed over folders the editor spells decomposed
            var rel = assetPath.Substring(sourceRoot.Length + 1);
            if (style == ContentNameStyle.PathWithExtension) return rel.Replace('\\', '/');
            var dir = Path.GetDirectoryName(rel);
            var file = Path.GetFileNameWithoutExtension(rel);
            return (string.IsNullOrEmpty(dir) ? file : $"{dir}/{file}").Replace('\\', '/');
        }

        /// <summary>The name under a root whose style is looked up (<see cref="NameStyleOf"/>) — for the callers that know only the root.</summary>
        internal static string ToName(string sourceRoot, string assetPath) => ToName(sourceRoot, assetPath, NameStyleOf(sourceRoot));

        /// <summary>Sprite sub-assets are "name[sub]". Only sprites are registered, so only sprites get a name.</summary>
        internal static string ToName(string sourceRoot, string assetPath, Object subAsset)
            => $"{ToName(sourceRoot, assetPath)}[{Composed(subAsset.name)}]";

        /// <summary><see cref="ToName(string, string, Object)"/> with the style given — the builder's, which never looks it up.</summary>
        internal static string ToName(string sourceRoot, string assetPath, Object subAsset, ContentNameStyle style)
            => $"{ToName(sourceRoot, assetPath, style)}[{Composed(subAsset.name)}]";

        internal static bool IsUnder(string sourceRoot, string assetPath)
        {
            if (string.IsNullOrEmpty(sourceRoot) || string.IsNullOrEmpty(assetPath)) return false;
            sourceRoot = Composed(sourceRoot); assetPath = Composed(assetPath);   // in the form ToName cuts in
            return assetPath.Length > sourceRoot.Length + 1
                   && assetPath.StartsWith(sourceRoot, StringComparison.Ordinal)
                   && assetPath[sourceRoot.Length] == '/';
        }

        /// <summary>
        /// The path as the folder or file is spelled in the project, composed (<see cref="Composed"/>), when that differs from the given one
        /// in case at most; null when there is none or it is spelled otherwise (doubled separators). The AssetDatabase finds a path in any
        /// case and either Unicode form and answers with the project's spelling — a deleted one too unless asked for existing assets
        /// (measured on 6000.6) — while the name rules compare a root or a category as written, composed.
        /// </summary>
        internal static string DiskPath(string path)
        {
            var guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var disk = string.IsNullOrEmpty(guid) ? null : Composed(AssetDatabase.GUIDToAssetPath(guid));
            return string.Equals(disk, Composed(path), StringComparison.OrdinalIgnoreCase) ? disk : null;
        }

        /// <summary>
        /// Why an asset under a root written in another case has no name: that root, the project's spelling and where the root is written;
        /// null when the path lies under no such root. The name rules compare a root as written (<see cref="IsUnder"/>).
        /// </summary>
        static string CaseOnlyRoot(string path)
        {
            var composed = Composed(path);
            foreach (var r in SourceRoots())
                if (Composed(r) is string root && composed.Length > root.Length + 1 && composed[root.Length] == '/' && composed.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && DiskPath(r) is string disk && disk != root)
                    return $"'{path}' lies under the content root '{r}' only in another case — the project spells it '{disk}': write it so in {WrittenIn(r)}";
            return null;
        }

        static string WrittenIn(string root)
        {
            foreach (var p in ContentBuildSettings.instance.Profiles)
                if (p != null && p.SourceRoot == root) return $"profile '{p.Name}'";
            return ContentDirectoryBuilder.SourceRoot == root ? "ContentDirectoryBuilder.SourceRoot" : "the profile whose catalog recorded it, then update its catalogs";
        }

        // ───────────── Resources (res: names) ─────────────

        internal static bool IsResourcesName(string name) => name != null && name.StartsWith(ContentService.ResourcesPrefix, StringComparison.Ordinal);

        /// <summary>
        /// The Resources path of a project file: below the innermost folder named Resources (any case — measured), without the
        /// extension. A player lacks the file when an Editor folder sits above that Resources folder; an Editor folder inside it
        /// is built (measured on 6000.6). False when no Resources folder holds the file.
        /// </summary>
        internal static bool TryGetResourcesPath(string assetPath, out string path, out bool editorOnly) => TryGetResourcesPath(assetPath, out path, out editorOnly, out _);

        /// <summary><see cref="TryGetResourcesPath(string, out string, out bool)"/> and the path as the editor spells it — the spelling Resources finds the file at.</summary>
        internal static bool TryGetResourcesPath(string assetPath, out string path, out bool editorOnly, out string spelled)
        {
            path = spelled = null;
            editorOnly = false;
            if (string.IsNullOrEmpty(assetPath)) return false;
            var parts = assetPath.Split('/');
            int res = -1;
            for (int i = parts.Length - 2; i >= 0; --i)   // the innermost: a nested file is reached by two names, one is written
                if (string.Equals(parts[i], "Resources", StringComparison.OrdinalIgnoreCase)) { res = i; break; }
            if (res < 0) return false;
            for (int i = 0; i < res; ++i)
                if (string.Equals(parts[i], "Editor", StringComparison.OrdinalIgnoreCase)) { editorOnly = true; break; }
            var rel = string.Join("/", parts, res + 1, parts.Length - res - 1);
            var dir = Path.GetDirectoryName(rel);
            var file = Path.GetFileNameWithoutExtension(rel);
            spelled = (string.IsNullOrEmpty(dir) ? file : $"{dir}/{file}").Replace('\\', '/');
            path = Composed(spelled);
            return true;
        }

        /// <summary>A res: name split as the runtime splits it: "[sprite]" when it ends with ']', at the last '['. False when malformed.</summary>
        internal static bool TrySplitResourcesName(string name, out string path, out string sub)
        {
            path = sub = null;
            if (!IsResourcesName(name)) return false;
            int start = ContentService.ResourcesPrefix.Length, end = name.Length;
            if (name.Length > start && name[name.Length - 1] == ']')
            {
                int open = name.LastIndexOf('[');
                if (open < start || open == name.Length - 2) return false;
                sub = name.Substring(open + 1, name.Length - open - 2);
                end = open;
            }
            if (end <= start) return false;
            path = name.Substring(start, end - start);
            return true;
        }

        /// <summary>What a res: name loads in a player (<see cref="PlayerResources"/>).</summary>
        static Object ResolveResources(string name)
            => TrySplitResourcesName(name, out var path, out var sub) ? PlayerResources(path, sub) : null;

        /// <summary>
        /// What a player's Resources gives for a path: the main asset, or the first sprite named sub. Resources.Load answers in
        /// the editor with the AssetDatabase's own object (measured), but it also finds editor-only folders, and for a path such
        /// a file shares with a player's file it gives either (measured on 6000.6): a player's file wins. The runtime's
        /// <see cref="ContentService.ResourcesFallback"/>.
        /// </summary>
        internal static Object PlayerResources(string path, string sub)
        {
            foreach (var form in ResourcesSpellings(path))   // the first spelling a player's file is at: the editor's Resources finds an editor-only file at another
                if (PlayerResourcesAt(form, sub) is Object found) return found;
            return null;
        }

        static Object PlayerResourcesAt(string path, string sub)
        {
            if (sub != null)
            {
                sub = Composed(sub);
                foreach (var sprite in Resources.LoadAll<Sprite>(path)) if (Composed(sprite.name) == sub && InPlayer(sprite, path)) return sprite;
                return null;
            }
            var found = Resources.Load(path);
            if (found == null || (found is GameObject && InPlayer(found, path))) return found;
            Object pick = InPlayer(found, path) ? found : null;   // the player's own pick among its files — a prefab first, which the editor's pick does not keep when an editor-only file shares the path (measured)
            foreach (var o in Resources.LoadAll(path))
                if (!AssetDatabase.IsSubAsset(o) && InPlayer(o, path) && (pick == null || (o is GameObject && !(pick is GameObject)))) pick = o;
            return pick;
        }

        /// <summary>
        /// The spellings of a Resources path a player may have it in, the composed first: a player built on macOS has the path as that
        /// editor spelled it — folders decomposed, the file as on disk (measured on 6000.6) — and Resources finds a path only that way;
        /// an editor that spells a path as on disk may keep the folders composed and the file decomposed; a path given in another form
        /// is asked last with its file as given, under either form of the folders, then as given — a name in a file's own spelling,
        /// which no other reaches (<see cref="TryNameFromResources"/>). The runtime tries them in
        /// this order (ContentService.LoadResources), as does <see cref="PlayerResources"/>. The path itself when it is ASCII.
        /// </summary>
        static string[] ResourcesSpellings(string path)
        {
            if (Ascii(path)) return new[] { path };
            var composed = Composed(path);
            int slash = composed.LastIndexOf('/') + 1;
            string folders = composed.Substring(0, slash), file = composed.Substring(slash), given = path.Substring(path.LastIndexOf('/') + 1);
            string dFolders = folders.Normalize(System.Text.NormalizationForm.FormD), dFile = file.Normalize(System.Text.NormalizationForm.FormD);
            var spellings = new List<string>(7) { composed };
            foreach (var form in new[] { dFolders + file, dFolders + dFile, folders + dFile, folders + given, dFolders + given, path }) if (!spellings.Contains(form)) spellings.Add(form);
            return spellings.ToArray();
        }

        /// <summary>
        /// Whether a player has the object under that Resources path: a Resources folder holds it there with no Editor folder above
        /// that folder. The innermost folder alone does not decide — a file in Resources/x/Editor/Resources is a player's under the
        /// outer folder's path and not under the inner one's (measured on 6000.6).
        /// </summary>
        static bool InPlayer(Object o, string path)
        {
            var parts = AssetDatabase.GetAssetPath(o).Split('/');
            for (int i = 0; i < parts.Length - 1; ++i)
            {
                if (string.Equals(parts[i], "Editor", StringComparison.OrdinalIgnoreCase)) return false;   // every Resources folder below is editor-only
                if (!string.Equals(parts[i], "Resources", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = string.Join("/", parts, i + 1, parts.Length - i - 1);
                int dot = rel.LastIndexOf('.');
                if (dot > rel.LastIndexOf('/')) rel = rel.Substring(0, dot);
                if (string.Equals(rel, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>The res: name for an asset in a Resources folder, or the reason there is none. False with a null reason: not in Resources.</summary>
        static bool TryNameFromResources(Object asset, string assetPath, out string name, out string reason)
        {
            name = reason = null;
            if (!TryGetResourcesPath(assetPath, out var path, out bool editorOnly, out var spelled)) return false;
            if (editorOnly) { reason = $"'{assetPath}' is in a Resources folder under an Editor folder — a player does not have it"; return false; }
            if (asset is SceneAsset) { reason = $"'{assetPath}' is a scene in a Resources folder — scenes load by a catalog name: put it under a content root"; return false; }
            if (path.EndsWith("]", StringComparison.Ordinal)) { reason = $"'{assetPath}': a Resources name cannot end with ']' (it would read as a sprite name) — rename the file"; return false; }
            if (AssetDatabase.IsSubAsset(asset) && !(asset is Sprite)) { reason = $"'{asset.name}' is a sub-asset; only sprites are named as 'name[sub]'"; return false; }
            if (AssetDatabase.IsSubAsset(asset) && asset.name.IndexOf('[') >= 0) { reason = $"the sprite '{asset.name}' in '{assetPath}': a Resources sprite name cannot hold '[' (the name would split there) — rename the slice"; return false; }
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var form in ResourcesSpellings(path))   // the files of every spelling the composed name reaches
                foreach (var o in Resources.LoadAll(form))
                {
                    var p = AssetDatabase.GetAssetPath(o);
                    if (TryGetResourcesPath(p, out var other, out _) && string.Equals(other, path, StringComparison.OrdinalIgnoreCase)) files.Add(p);
                }
            if (!Ascii(spelled) && files.Add(assetPath))   // no spelling of the composed path reaches the file (part of its name decomposed, a compatibility character): its own does, after them
            {
                var folders = spelled.Substring(0, spelled.LastIndexOf('/') + 1);
                path = folders.IsNormalized(System.Text.NormalizationForm.FormC) || folders.IsNormalized(System.Text.NormalizationForm.FormD)
                    ? path.Substring(0, path.LastIndexOf('/') + 1) + spelled.Substring(folders.Length)   // the folders composed — asked in either form — and the file as it is spelled
                    : spelled;                                                                           // folders spelled in both forms: only the path as it is spelled reaches it
            }
            if (files.Count > 1)
            {
                reason = $"'{ContentService.ResourcesPrefix}{path}' names {files.Count} files ({string.Join(", ", files)}) — Resources gives one of them for the name; rename one";
                return false;
            }
            name = ContentService.ResourcesPrefix + path + (AssetDatabase.IsSubAsset(asset) ? $"[{Composed(asset.name)}]" : "");
            return true;
        }

        // ───────────── root → name style ─────────────

        static readonly Dictionary<string, ContentNameStyle> s_StyleByCatalogRoot = new(StringComparer.Ordinal);   // what the first catalog by asset path recorded for the root
        static readonly HashSet<ContentCatalog> s_OtherStyle = new();   // catalogs written in a style their root's owner no longer gives: not indexed

        /// <summary>
        /// The name style of a source root: the profile that uses the root, else the static view when it points at it, else what
        /// a catalog built from the root recorded (the first by asset path), else the default. The profiles and the static view
        /// are read every time — nothing to go stale; only the catalog side is kept, with the catalog index.
        /// </summary>
        internal static ContentNameStyle NameStyleOf(string sourceRoot)
        {
            if (string.IsNullOrEmpty(sourceRoot)) return ContentNameStyle.PathWithoutExtension;
            if (TryOwnerStyle(sourceRoot, out var owner)) return owner;
            LoadCatalogs();
            return s_StyleByCatalogRoot.TryGetValue(sourceRoot, out var recorded) ? recorded : ContentNameStyle.PathWithoutExtension;
        }

        /// <summary>The style the root's owner gives it — the profile that uses it, else the static view when it points at it. False when nobody owns it.</summary>
        static bool TryOwnerStyle(string sourceRoot, out ContentNameStyle style)
        {
            var profiles = ContentBuildSettings.instance.Profiles;
            for (int i = 0; i < profiles.Count; ++i)   // by index: a foreach over the interface allocates an enumerator on every call (Mono)
            {
                var profile = profiles[i];
                if (profile != null && profile.SourceRoot == sourceRoot) { style = profile.NameStyle; return true; }
            }
            if (ContentDirectoryBuilder.SourceRoot == sourceRoot) { style = ContentDirectoryBuilder.NameStyle; return true; }
            style = ContentNameStyle.PathWithoutExtension;
            return false;
        }

        // ───────────── catalog index ─────────────
        //
        // The catalogs carry no name index and LoadableObjectIdToObject is an asset load, so an uncached
        // lookup would cost (visible fields × entries) per repaint. Everything below is cached and dropped in one
        // place, Invalidate — which then raises Invalidated for what else answers from the project — on
        // projectChanged, when a catalog or an atlas is imported again (ContentCatalogImportWatcher), when the
        // builder writes a catalog, and when the stamp below changes. Any other asset imported again has changed its
        // content only: an import that adds, removes or renames a sub-asset, or replaces the main object, raises
        // projectChanged (measured on 6000.6). Only the statuses read content (whether a prefab has a field's
        // component) — InvalidateStatuses drops those, and has to drop whatever else comes to read an asset's content.

        static ContentCatalog[] s_Catalogs;
        static readonly Dictionary<string, LoadableObjectId> s_IdByName = new(StringComparer.Ordinal);
        static readonly Dictionary<string, LoadableSceneId> s_SceneIdByName = new(StringComparer.Ordinal);
        static readonly Dictionary<string, string> s_NameById = new(StringComparer.Ordinal);        // id.ToString() → name
        static readonly Dictionary<string, string> s_NameBySceneId = new(StringComparer.Ordinal);
        static readonly Dictionary<string, Object> s_AssetByName = new(StringComparer.Ordinal);     // resolved, negatives included
        static readonly Dictionary<(string, string), string> s_NameByRef = new();                  // (guid, sub) → catalog name
        static readonly Dictionary<string, string> s_SceneNameByGuid = new(StringComparer.Ordinal);
        static readonly Dictionary<(string, string), Object> s_AssetByRef = new();                 // resolved, negatives included
        static readonly Dictionary<(string, string, bool), string> s_RefNames = new();             // path-rule names, negatives included
        static readonly Dictionary<(string, string, Type, bool), (NameStatus, string)> s_RefStatus = new();
        static readonly Dictionary<string, (string, string)> s_LinkById = new(StringComparer.Ordinal);   // id.ToString() → (main name, sprite name) of a sub-asset entry
        static readonly Dictionary<string, (string, string)> s_LinkByName = new(StringComparer.Ordinal); // "main[sprite]" → (main name, sprite name), the same entries
        static readonly Dictionary<(string, string), string> s_LinkNames = new();                  // (main name, sprite name) → "main[sprite]"
        static readonly Dictionary<(string, string), Object> s_AssetByLink = new();                // resolved, negatives included
        static readonly Dictionary<string, Sprite[]> s_PackedByAtlas = new(StringComparer.Ordinal);   // atlas path → the sprites it packs
        static readonly Dictionary<SpriteAtlas, Dictionary<string, Sprite>> s_PackedByName = new();   // an atlas → its packed sprites by name, null for a name two share
        static readonly Dictionary<string, string> s_AtlasGuidByAliasName = new(StringComparer.Ordinal);   // an atlas's name for a sprite → the atlas GUID
        static Dictionary<Sprite, (string root, string atlas)> s_AliasBySprite;                    // a sprite → the first atlas under a root that names it
        static string[] s_Roots;          // content roots, longest first
        static string s_RootsStaticRoot;  // the builder's static root s_Roots was built with
        static int s_RootsRevision = -1;  // the ContentBuildSettings revision s_Roots was built with

        [InitializeOnLoadMethod]
        static void Install()
        {
            EditorApplication.projectChanged -= Invalidate;
            EditorApplication.projectChanged += Invalidate;
        }

        // What the answers were made with besides the project: the profiles (the settings revision), the static view's root and
        // name style, and the settings object's dirty count — an undo made while no build window listens reverts a profile
        // without a revision (measured on 6000.6). Saving the settings raises no projectChanged, so this is compared where the
        // caches are read. Which catalogs are indexed depends on the styles too, so a change drops everything.
        static int s_StampRevision = -1, s_StampDirtyCount = -1;
        static string s_StampStaticRoot;
        static ContentNameStyle s_StampStaticStyle;

        static void SyncStamp()
        {
            var staticRoot = ContentDirectoryBuilder.SourceRoot;   // first: settings with no profile yet make their default one here, which bumps the revision
            var staticStyle = ContentDirectoryBuilder.NameStyle;
            int revision = ContentBuildSettings.Revision;
            int dirtyCount = EditorUtility.GetDirtyCount(ContentBuildSettings.instance);
            if (s_StampRevision == revision && s_StampDirtyCount == dirtyCount && s_StampStaticRoot == staticRoot && s_StampStaticStyle == staticStyle) return;
            s_StampRevision = revision;
            s_StampDirtyCount = dirtyCount;
            s_StampStaticRoot = staticRoot;
            s_StampStaticStyle = staticStyle;
            Invalidate();
        }

        /// <summary>Bumped by every <see cref="Invalidate"/>: what was read from the project may have changed. The build window's messages watch it.</summary>
        internal static int Generation { get; private set; }

        /// <summary>
        /// Raised by every <see cref="Invalidate"/>, after the caches here are dropped: what else answers from the project drops what
        /// it read off the files (the editor simulation). An import that changed an asset's content only (InvalidateStatuses) raises nothing.
        /// </summary>
        internal static event Action Invalidated;

        /// <summary>Drops the cached catalogs and lookups, then raises <see cref="Invalidated"/> for what answers from them. Called on every project change; safe to call by hand.</summary>
        public static void Invalidate()
        {
            ++Generation;
            s_StyleByCatalogRoot.Clear();
            s_OtherStyle.Clear();
            s_Catalogs = null;
            s_IdByName.Clear();
            s_SceneIdByName.Clear();
            s_NameById.Clear();
            s_NameBySceneId.Clear();
            s_AssetByName.Clear();
            s_NameByRef.Clear();
            s_SceneNameByGuid.Clear();
            s_AssetByRef.Clear();
            s_RefNames.Clear();
            s_RefStatus.Clear();
            s_LinkById.Clear();
            s_LinkByName.Clear();
            s_LinkNames.Clear();
            s_AssetByLink.Clear();
            s_PackedByAtlas.Clear();
            s_PackedByName.Clear();
            s_AtlasGuidByAliasName.Clear();
            s_AliasBySprite = null;
            s_Roots = null;
            Invalidated?.Invoke();
        }

        /// <summary>Drops the statuses only (<see cref="ValidateRef"/>): what was read off an asset's content. An asset imported again with no project change.</summary>
        internal static void InvalidateStatuses() => s_RefStatus.Clear();

        /// <summary>Every ContentCatalog in the project — the runtime can register several directories.</summary>
        public static ContentCatalog[] LoadCatalogs()
        {
            SyncStamp();
            if (s_Catalogs != null)
            {
                foreach (var c in s_Catalogs)
                    if (c == null) { Invalidate(); break; }   // an asset was deleted under us
            }
            if (s_Catalogs != null) return s_Catalogs;

            var guids = AssetDatabase.FindAssets("t:ContentCatalog");
            var paths = new List<string>(guids.Length);
            foreach (var guid in guids) paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            paths.Sort((a, b) => string.CompareOrdinal(Composed(a), Composed(b)));   // by asset path, composed: when catalogs of one root disagree on the style, the first answers — the same one every time, on every machine
            var list = new List<ContentCatalog>(paths.Count);
            foreach (var path in paths)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(path);
                if (catalog == null) continue;
                list.Add(catalog);
                var root = catalog.SourceRoot;
                if (string.IsNullOrEmpty(root)) continue;
                if (!s_StyleByCatalogRoot.ContainsKey(root)) s_StyleByCatalogRoot[root] = catalog.NameStyle;
                // Written in a style the root's owner no longer gives: its names are not the ones the next build or a Play served
                // from the project will have. A root nobody owns keeps all its catalogs — they still load as built.
                if (TryOwnerStyle(root, out var owner) && catalog.NameStyle != owner) s_OtherStyle.Add(catalog);
            }
            s_Catalogs = list.ToArray();

            foreach (var catalog in s_Catalogs)
            {
                if (s_OtherStyle.Contains(catalog)) continue;
                foreach (var entry in catalog.Entries)
                {
                    if (string.IsNullOrEmpty(entry.name)) continue;
                    var e = entry;   // composed: a catalog an earlier version wrote on macOS holds names as that editor spelled the paths
                    e.name = Composed(e.name); e.sub = Composed(e.sub);
                    s_IdByName[e.name] = e.id;
                    // An atlas's name for a sprite shares the sprite's id with the sprite's own name: the own name answers the id,
                    // whatever the order — a picked sprite keeps the name it had before an atlas named it too.
                    bool alias = !string.IsNullOrEmpty(e.sub) && !string.IsNullOrEmpty(e.guid) && IsAtlasPath(AssetDatabase.GUIDToAssetPath(e.guid));
                    var idKey = e.id.ToString();
                    if (!alias || !s_NameById.ContainsKey(idKey)) s_NameById[idKey] = e.name;
                    if (alias) s_AtlasGuidByAliasName[e.name] = e.guid;
                    if (!string.IsNullOrEmpty(e.guid)) s_NameByRef[(e.guid, e.sub ?? "")] = e.name;
                    // The builder writes "main[sprite]" and the same sprite name into sub, so the suffix is checked, not parsed;
                    // an entry that does not match (a hand-made catalog) is left to the path rule.
                    if (!string.IsNullOrEmpty(e.sub) && e.name.Length > e.sub.Length + 2 && e.name[e.name.Length - 1] == ']'
                        && e.name[e.name.Length - e.sub.Length - 2] == '[' && string.CompareOrdinal(e.name, e.name.Length - e.sub.Length - 1, e.sub, 0, e.sub.Length) == 0)
                    {
                        var link = (e.name.Substring(0, e.name.Length - e.sub.Length - 2), e.sub);
                        if (!alias || !s_LinkById.ContainsKey(idKey)) s_LinkById[idKey] = link;
                        s_LinkByName[e.name] = link;
                    }
                }
                foreach (var scene in catalog.Scenes)
                {
                    if (string.IsNullOrEmpty(scene.name)) continue;
                    var e = scene;
                    e.name = Composed(e.name);
                    s_SceneIdByName[e.name] = e.id;
                    s_NameBySceneId[e.id.ToString()] = e.name;
                    if (!string.IsNullOrEmpty(e.guid)) s_SceneNameByGuid[e.guid] = e.name;
                }
            }
            return s_Catalogs;
        }

        /// <summary>
        /// The folders the path rule may name assets under: every catalog's SourceRoot, every build profile's
        /// SourceRoot (a profile that has never been built has no catalog to offer yet) and the builder's
        /// static view, longest first so the deepest root wins when they nest. Nothing here is a
        /// fallback: dropping any of them would refuse names that work today.
        /// </summary>
        internal static string[] SourceRoots()
        {
            SyncStamp();   // the simulation asks for the roots first: an undo with no window moves only the stamp's dirty count
            var staticRoot = ContentDirectoryBuilder.SourceRoot;
            int revision = ContentBuildSettings.Revision;
            if (s_Roots != null && s_RootsStaticRoot == staticRoot && s_RootsRevision == revision) return s_Roots;

            var roots = new List<string>();
            foreach (var catalog in LoadCatalogs())
            {
                var root = catalog.SourceRoot;
                if (!string.IsNullOrEmpty(root) && !roots.Contains(root)) roots.Add(root);
            }
            foreach (var profile in ContentBuildSettings.instance.Profiles)
            {
                var root = profile?.SourceRoot;
                if (!string.IsNullOrEmpty(root) && !roots.Contains(root)) roots.Add(root);
            }
            if (!string.IsNullOrEmpty(staticRoot) && !roots.Contains(staticRoot)) roots.Add(staticRoot);
            roots.Sort((a, b) => b.Length.CompareTo(a.Length));

            s_RootsStaticRoot = staticRoot;
            s_RootsRevision = revision;
            s_Roots = roots.ToArray();
            return s_Roots;
        }

        /// <summary>The deepest content root the asset lies under, or null when it lies under none.</summary>
        internal static string RootFor(string assetPath)
        {
            foreach (var root in SourceRoots())
                if (IsUnder(root, assetPath)) return root;
            return null;
        }

        // ───────────── sprite atlases ─────────────
        //
        // A V2 atlas under a content root names each sprite it packs "atlas[sprite]" too. The entry carries the sprite's own id —
        // a build with the packer on binds the original sprite to the atlas page (measured on 6000.6) — so the runtime loads it like
        // any sprite name. Everything here is cached with the catalog index.

        internal static bool IsV1AtlasPath(string path) => path != null && path.EndsWith(".spriteatlas", StringComparison.OrdinalIgnoreCase);
        internal static bool IsAtlasPath(string path) => path != null && (path.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase) || IsV1AtlasPath(path));

        /// <summary>An atlas whose sprites get names: V2 and not a variant (a variant's sprites are bound to the master's page — measured).</summary>
        internal static bool NamesItsSprites(string path, SpriteAtlas atlas)
            => atlas != null && path != null && path.EndsWith(".spriteatlasv2", StringComparison.OrdinalIgnoreCase) && !atlas.isVariant;

        /// <summary>
        /// The sprites an atlas packs: GetPackables expanded — a sprite as it is, a texture's sprites, the sprites of the textures
        /// below a folder (measured: what the engine packs). Empty for a variant. Cached.
        /// </summary>
        internal static Sprite[] PackedSprites(SpriteAtlas atlas)
        {
            if (atlas == null) return Array.Empty<Sprite>();
            SyncStamp();
            var atlasPath = AssetDatabase.GetAssetPath(atlas);
            if (s_PackedByAtlas.TryGetValue(atlasPath, out var cached)) return cached;
            return s_PackedByAtlas[atlasPath] = ExpandPackables(atlas);
        }

        /// <summary><see cref="PackedSprites"/> uncached — the builder's: a build names what the atlas packs now (saving an atlas raises no projectChanged — measured).</summary>
        internal static Sprite[] ExpandPackables(SpriteAtlas atlas)
        {
            if (atlas == null) return Array.Empty<Sprite>();
            var list = new List<Sprite>();
            var seen = new HashSet<Sprite>();
            if (!atlas.isVariant)
                foreach (var packable in atlas.GetPackables())
                {
                    if (packable == null) continue;
                    if (packable is Sprite sprite) { if (seen.Add(sprite)) list.Add(sprite); continue; }
                    var packablePath = AssetDatabase.GetAssetPath(packable);
                    if (AssetDatabase.IsValidFolder(packablePath))
                    {
                        var textures = new List<string>();
                        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { packablePath })) textures.Add(AssetDatabase.GUIDToAssetPath(guid));
                        textures.Sort(StringComparer.Ordinal);
                        foreach (var texture in textures) AddSprites(texture, list, seen);
                    }
                    else if (packable is Texture2D) AddSprites(packablePath, list, seen);
                }
            return list.ToArray();
        }

        static void AddSprites(string texturePath, List<Sprite> list, HashSet<Sprite> seen)
        {
            foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(texturePath))
                if (rep is Sprite sprite && seen.Add(sprite)) list.Add(sprite);
        }

        /// <summary>The packed sprite of that name — null when there is none, or when two share it (the builder refuses that name).</summary>
        internal static Sprite PackedSprite(SpriteAtlas atlas, string name, out bool ambiguous)
        {
            ambiguous = false;
            var packed = PackedSprites(atlas);
            if (packed.Length == 0 || name == null) return null;
            if (!s_PackedByName.TryGetValue(atlas, out var byName))   // each name read once per atlas — a native getter that makes the string
            {
                byName = new Dictionary<string, Sprite>(packed.Length, StringComparer.Ordinal);
                foreach (var sprite in packed) { var n = Composed(sprite.name); byName[n] = byName.ContainsKey(n) ? null : sprite; }
                s_PackedByName[atlas] = byName;
            }
            if (!byName.TryGetValue(Composed(name), out var found)) return null;
            ambiguous = ReferenceEquals(found, null);
            return found;
        }

        /// <summary>The first atlas under a content root (deepest root, then path order) that names the sprite — its only name when its texture lies outside every root.</summary>
        internal static bool TryAliasFor(Sprite sprite, out string root, out string atlasPath)
        {
            root = atlasPath = null;
            if (sprite == null) return false;
            SyncStamp();
            if (s_AliasBySprite == null)
            {
                s_AliasBySprite = new Dictionary<Sprite, (string, string)>();
                foreach (var r in SourceRoots())
                {
                    if (!AssetDatabase.IsValidFolder(r)) continue;
                    var atlases = new List<string>();
                    foreach (var guid in AssetDatabase.FindAssets("t:SpriteAtlas", new[] { r })) atlases.Add(AssetDatabase.GUIDToAssetPath(guid));
                    atlases.Sort((a, b) => string.CompareOrdinal(Composed(a), Composed(b)));   // composed: the same pick on every machine
                    foreach (var path in atlases)
                    {
                        if (RootFor(path) != r) continue;   // under nested roots an atlas is named under the deepest, like any asset
                        var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path);
                        if (!NamesItsSprites(path, atlas)) continue;
                        foreach (var s in PackedSprites(atlas))
                            if (!s_AliasBySprite.ContainsKey(s) && PackedSprite(atlas, s.name, out _) != null) s_AliasBySprite[s] = (r, path);
                    }
                }
            }
            if (!s_AliasBySprite.TryGetValue(sprite, out var hit)) return false;
            (root, atlasPath) = hit;
            return true;
        }

        /// <summary>The atlas behind an atlas's name for a sprite ("atlas[sprite]"): the catalog's entry, else the path rule. False for any other name.</summary>
        internal static bool TryAtlasOfName(string name, out string atlasPath, out string atlasGuid)
        {
            atlasPath = atlasGuid = null;
            if (string.IsNullOrEmpty(name) || IsResourcesName(name) || name[name.Length - 1] != ']') return false;
            LoadCatalogs();
            if (s_AtlasGuidByAliasName.TryGetValue(name, out atlasGuid)) { atlasPath = AssetDatabase.GUIDToAssetPath(atlasGuid); return true; }
            int open = name.LastIndexOf('[');
            if (open <= 0) return false;
            var main = ResolveAsset(name.Substring(0, open)) as SpriteAtlas;
            var path = main != null ? AssetDatabase.GetAssetPath(main) : null;
            if (!NamesItsSprites(path, main)) return false;
            atlasPath = path;
            atlasGuid = AssetDatabase.AssetPathToGUID(path);
            return true;
        }

        /// <summary>
        /// What dropping an object on a link or reference field offers instead of the object: the sprites a V2 atlas under a content
        /// root packs, when the field holds sprites and would refuse the atlas itself. Empty for anything else.
        /// </summary>
        internal static Sprite[] AtlasChoices(Object dropped, Type assetType)
        {
            if (!(dropped is SpriteAtlas atlas) || assetType == null) return Array.Empty<Sprite>();
            if (ObjectFieldTypeFor(assetType).IsAssignableFrom(typeof(SpriteAtlas)) || !assetType.IsAssignableFrom(typeof(Sprite))) return Array.Empty<Sprite>();
            var path = AssetDatabase.GetAssetPath(atlas);
            if (!NamesItsSprites(path, atlas) || RootFor(path) == null) return Array.Empty<Sprite>();
            return PackedSprites(atlas);
        }

        /// <summary>The link a sprite picked through an atlas writes: (the atlas's name, the sprite's name).</summary>
        internal static bool TryGetLinkForAtlasSprite(SpriteAtlas atlas, Sprite sprite, out string name, out string sub)
        {
            name = sub = null;
            var path = AssetDatabase.GetAssetPath(atlas);
            var root = RootFor(path);
            if (sprite == null || root == null || !NamesItsSprites(path, atlas)) return false;
            name = ToName(root, path);
            sub = Composed(sprite.name);
            return true;
        }

        /// <summary>The reference a sprite picked through an atlas writes: (the atlas GUID, the sprite's name) — the shape Addressables wrote.</summary>
        internal static bool TryGetRefForAtlasSprite(SpriteAtlas atlas, Sprite sprite, out string guid, out string sub)
        {
            guid = sub = null;
            var path = AssetDatabase.GetAssetPath(atlas);
            if (sprite == null || RootFor(path) == null || !NamesItsSprites(path, atlas)) return false;
            guid = AssetDatabase.AssetPathToGUID(path);
            sub = Composed(sprite.name);
            return true;
        }

        // ───────────── asset → name ─────────────

        /// <summary>
        /// The catalog name for an asset the user picked. Catalog-first (exact, and independent of the current
        /// SourceRoot); the path rule is the fallback for assets that are not in a catalog yet.
        /// </summary>
        public static bool TryGetNameForAsset(Object asset, out string name, out string reason)
        {
            name = null;
            reason = null;
            if (asset == null) { reason = "no asset"; return false; }

            LoadCatalogs();
            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(asset);
            if (id.IsValid && s_NameById.TryGetValue(id.ToString(), out var known)) { name = known; return true; }
            if (asset is SceneAsset)
            {
                var sceneId = LoadableSceneIdEditorUtility.CreateLoadableSceneId(AssetDatabase.GetAssetPath(asset));
                if (sceneId.IsValid && s_NameBySceneId.TryGetValue(sceneId.ToString(), out var knownScene)) { name = knownScene; return true; }
            }

            var path = AssetDatabase.GetAssetPath(asset);
            var root = RootFor(path);
            if (root == null)
            {
                if (CaseOnlyRoot(path) is string slip) { reason = slip; return false; }   // before Resources and atlases, which would name it otherwise
                if (TryNameFromResources(asset, path, out name, out reason)) return true;
                if (reason != null) return false;
                if (asset is Sprite packed && TryAliasFor(packed, out var aliasRoot, out var atlasPath))   // only an atlas names it: the texture lies outside every root
                {
                    name = ToName(aliasRoot, atlasPath, packed);
                    if (Unpickable(aliasRoot, name, ToName(aliasRoot, atlasPath), packed.name, packed, atlasPath) is string twice) { reason = twice; name = null; return false; }
                    return true;
                }
                reason = $"'{path}' is not in a catalog and not under a content root ({string.Join(", ", SourceRoots())})";
                return false;
            }
            if (AssetDatabase.IsSubAsset(asset) && !(asset is Sprite))
            {
                reason = $"'{asset.name}' is a sub-asset; only sprites are registered as 'name[sub]'";
                return false;
            }
            if (asset is ContentCatalog)
            {
                reason = $"'{path}' is a ContentCatalog — the build refuses a catalog under the source root '{root}': move it out";
                return false;
            }
            bool sprite = AssetDatabase.IsSubAsset(asset);
            name = sprite ? ToName(root, path, asset) : ToName(root, path);
            if (Unpickable(root, name, sprite ? ToName(root, path) : null, sprite ? asset.name : null, asset, path) is string shared)   // the build refuses the name — as a res: path several files share is refused
            {
                reason = shared;
                name = null;
                return false;
            }
            return true;
        }

        // ───────────── name → asset ─────────────

        /// <summary>The asset a name refers to: catalog first, then a path search under SourceRoot.</summary>
        public static Object ResolveAsset(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!IsResourcesName(name)) name = Composed(name);   // a name saved in another form answers as the composed one; a res: name keeps its spelling, which may be the only one that reaches a file (ResourcesSpellings)
            SyncStamp();
            if (s_AssetByName.TryGetValue(name, out var cached)) return cached;
            if (IsResourcesName(name)) return s_AssetByName[name] = ResolveResources(name);   // negatives are cached too

            LoadCatalogs();
            Object found = null;
            if (s_IdByName.TryGetValue(name, out var id))
                found = LoadableObjectIdEditorUtility.LoadableObjectIdToObject(id);
            if (found == null && s_SceneIdByName.TryGetValue(name, out var sceneId))
                found = LoadableSceneIdEditorUtility.LoadableSceneIdToScene(sceneId);
            if (found == null)
                found = FindByPath(name);

            s_AssetByName[name] = found;   // negatives are cached too
            return found;
        }

        public static SceneAsset ResolveScene(string name) => ResolveAsset(name) as SceneAsset;

        /// <summary>
        /// Path fallback: the name is the path under a content root without its extension, so search for the leaf
        /// and confirm with the same rule the builder uses; under a root that keeps the extension the name is the path itself.
        /// A name that ends with "[…]" is the file of that name or the sprite of the name before the last '['. The builder puts
        /// both in one name space and refuses a root where two things share a name, and the editor simulation answers neither:
        /// so the deepest root that has the name answers it when it has it once, else nothing — two files of one name too.
        /// Runs only on a catalog miss, and the result is cached.
        /// </summary>
        static Object FindByPath(string name)
        {
            int open = name.LastIndexOf('[');
            return open > 0 && name.EndsWith("]", StringComparison.Ordinal)
                ? FindByName(name, name.Substring(0, open), name.Substring(open + 1, name.Length - open - 2))
                : FindByName(name, null, null);
        }

        /// <summary><see cref="FindByPath(string)"/> for a name and sprite name already apart: nothing to parse. A null sub is the main asset.</summary>
        static Object FindByPath(string main, string sub)
            => sub == null ? FindByName(main, null, null) : FindByName(LinkName(main, sub), main, sub);

        static Object FindByName(string name, string main, string sub)
        {
            foreach (var decomposed in new[] { false, true })   // files spelled decomposed only when no root has the name composed
            {
                if (decomposed && Ascii(name)) break;
                var roots = SourceRoots();   // longest first, so a nested root wins
                for (int r = 0; r < roots.Length; ++r)
                {
                    if (!AssetDatabase.IsValidFolder(roots[r])) continue;
                    int count = CountIn(roots[r], name, main, sub, out var found, decomposed: decomposed);
                    if (count == 0) continue;
                    for (int n = 0; n < r && !decomposed && !Ascii(name); ++n)   // a root nested in this one wins whatever the forms on disk
                    {
                        if (!IsUnder(roots[r], roots[n]) || !AssetDatabase.IsValidFolder(roots[n])) continue;
                        int inner = CountIn(roots[n], name, main, sub, out var nested, decomposed: true);
                        if (inner > 0) return inner == 1 ? nested : null;
                    }
                    if (count > 1) return null;   // the root holds the name twice: the builder refuses it, the simulation answers neither
                    if (!decomposed && !Ascii(name) && CountIn(roots[r], name, main, sub, out _, decomposed: true) > 0) return null;   // the root holds the name in both forms: the builder refuses it, the simulation answers neither
                    return found;
                }
            }
            return null;
        }

        /// <summary>Whether the file name of <paramref name="path"/> without its extension is <paramref name="leaf"/> — what <see cref="ToName(string, string, ContentNameStyle)"/> keeps of it, without making a string.</summary>
        static bool FileNameIs(string path, string leaf)
        {
            int start = path.LastIndexOf('/') + 1, dot = path.LastIndexOf('.');
            int end = dot >= start ? dot : path.Length;
            return end - start == leaf.Length && string.CompareOrdinal(path, start, leaf, 0, leaf.Length) == 0;
        }

        /// <summary>
        /// How many things under <paramref name="root"/> answer <paramref name="name"/> — its files and, given <paramref name="main"/>,
        /// the sprite <paramref name="sub"/> of the main's one file — counted to two, the one in <paramref name="found"/>. With lists
        /// the files counted go into them: every file of a name several files share, for the pick's reason.
        /// </summary>
        static int CountIn(string root, string name, string main, string sub, out Object found, List<string> files = null, List<string> mains = null, bool decomposed = false)
        {
            var style = NameStyleOf(root);
            Object answer = null;
            int count = Answers(root, style, name, null, ref answer, files, decomposed);
            if (main != null && (count < 2 || files != null)) count += Answers(root, style, main, sub, ref answer, mains, decomposed);
            found = count == 1 ? answer : null;
            return count;
        }

        /// <summary>
        /// Why <paramref name="asset"/>, named <paramref name="name"/> under <paramref name="root"/>, cannot be picked — the field would not
        /// show it there, because the name is another's too — or null. A sprite comes with its main name and sprite name and is never
        /// split again; any other name that ends with "[…]" is split as the path fallback splits it. <paramref name="file"/>: the file
        /// the name was made from.
        /// </summary>
        static string Unpickable(string root, string name, string main, string sub, Object asset, string file)
        {
            if (main == null)
            {
                int open = name.LastIndexOf('[');
                if (open > 0 && name.EndsWith("]", StringComparison.Ordinal)) { main = name.Substring(0, open); sub = name.Substring(open + 1, name.Length - open - 2); }
            }
            var style = NameStyleOf(root);
            var files = new List<string>();
            var mains = new List<string>();
            Object found = null;
            int named = Answers(root, style, name, null, ref found, files);
            int sprites = main != null ? Answers(root, style, main, sub, ref found, mains) : 0;
            if (!Ascii(name))   // and the files spelled decomposed: a name two files answer in two forms is refused by the build as well
            {
                named += Answers(root, style, name, null, ref found, files, true);
                sprites += main != null ? Answers(root, style, main, sub, ref found, mains, true) : 0;
            }
            if (named + sprites == 1 && ReferenceEquals(found, asset)) return null;
            if (named + sprites == 0 && MixedForms(file))
                return $"'{name}': the file's name is spelled in neither Unicode form throughout (part of it decomposed, or a compatibility character), which a search by name cannot find — update the catalog, which names it";
            var why = named > 1 ? $"'{name}' names {files.Count} files under '{root}' ({string.Join(", ", files)})"
                : mains.Count > 1 ? $"'{name}' reads as a sprite of '{main}', which names {mains.Count} files under '{root}' ({string.Join(", ", mains)})"
                : sprites > 1 ? $"'{name}' is {(named == 1 ? $"the file {files[0]} and " : "")}two sprites '{sub}' of {mains[0]} under '{root}'"
                : named == 1 && sprites == 1 ? $"'{name}' is the file {files[0]} and the sprite '{sub}' of {mains[0]} under '{root}'"
                : $"'{name}' answers another asset under '{root}'";
            return why + " — the build refuses a name two things share; rename one";
        }

        /// <summary>A file name neither composed nor decomposed throughout — part of it typed onto a decomposed name, or a compatibility character either form changes: a search by name finds it in neither form.</summary>
        static bool MixedForms(string path)
        {
            int start = path.LastIndexOf('/') + 1, dot = path.LastIndexOf('.');
            var leaf = dot > start ? path.Substring(start, dot - start) : path.Substring(start);
            return !leaf.IsNormalized(System.Text.NormalizationForm.FormC) && !leaf.IsNormalized(System.Text.NormalizationForm.FormD);
        }

        /// <summary>
        /// What under <paramref name="root"/> answers <paramref name="name"/>, counted to two: its files, or with a sprite name the sprite
        /// of its one file (an atlas: one it packs). A name two files share counts two whatever they hold, as in the simulation, and so
        /// do two sprites of that name in its file, which the builder refuses too. With <paramref name="paths"/> the files counted go
        /// into it — every file of a name several files share, for the pick's reason.
        /// </summary>
        static int Answers(string root, ContentNameStyle style, string name, string sub, ref Object found, List<string> paths = null, bool decomposed = false)
        {
            name = Composed(name); sub = Composed(sub);   // as ToName makes them: a sprite's own name is spelled as its file
            string file = null;
            int more = 0;   // the files of the name beyond the first, counted only when they are to be named
            if (style == ContentNameStyle.PathWithExtension)
            {
                if (decomposed) return 0;   // a path is found in either form (measured on 6000.6): the composed pass has the file, or there is none
                // A search term that carries an extension finds nothing (measured), so look at the path. The lookup ignores
                // case and answers with the path on disk, so the name is made again from that path and has to match.
                var direct = AssetDatabase.LoadMainAssetAtPath(root + "/" + name);
                var actual = direct != null ? AssetDatabase.GetAssetPath(direct) : null;
                if (actual == null || AssetDatabase.IsValidFolder(actual) || !IsUnder(root, actual) || ToName(root, actual, ContentNameStyle.PathWithExtension) != name || direct is ContentCatalog) return 0;
                file = actual;
            }
            else
            {
                int cut = name.LastIndexOf('/');
                var leaf = name.Substring(cut + 1);
                if (leaf.Length == 0) return 0;
                var term = decomposed ? leaf.Normalize(System.Text.NormalizationForm.FormD) : leaf;   // a search finds a file only in the form it is spelled in
                if (decomposed && term == leaf) return 0;
                var folder = cut > 0 ? root + "/" + name.Substring(0, cut) : root;   // only a file in the name's folder answers it: a folder is found in either form (measured on 6000.6)
                if (!AssetDatabase.IsValidFolder(folder)) return 0;
                foreach (var guid in AssetDatabase.FindAssets(term, new[] { folder }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!FileNameIs(path, term) || !IsUnder(root, path) || ToName(root, path, style) != name || AssetDatabase.IsValidFolder(path)) continue;   // cheap checks first: most candidates only contain the term — the file name, spelled as searched, before the paths made composed
                    var main = AssetDatabase.LoadMainAssetAtPath(path);
                    if (main == null || main is ContentCatalog) continue;   // the builder skips a file whose main asset does not load — a script that is gone (measured on 6000.6) — and refuses a catalog
                    if (file == null) { file = path; continue; }
                    if (paths == null) return 2;
                    if (more++ == 0) paths.Add(file);
                    paths.Add(path);
                }
                if (more > 0) return 2;
                if (file == null) return 0;
            }
            var asset = AssetDatabase.LoadMainAssetAtPath(file);
            Object answer = null;
            bool twice = false;   // two sprites of that name in the file
            if (sub == null) answer = asset;
            else if (asset is SpriteAtlas atlas) { if (NamesItsSprites(file, atlas)) answer = PackedSprite(atlas, sub, out twice); }   // an atlas's name for a sprite it packs
            else foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(file))
                {
                    if (!(rep is Sprite) || Composed(rep.name) != sub) continue;
                    if (answer != null) { twice = true; answer = null; break; }
                    answer = rep;
                }
            if (twice) { paths?.Add(file); return 2; }
            if (answer == null) return 0;
            paths?.Add(file);
            found = answer;
            return 1;
        }

        // ───────────── field type ─────────────

        /// <summary>
        /// The T of the ContentLink&lt;T&gt; a field holds. Arrays and List&lt;&gt; are unwrapped first: the drawer is
        /// attached per element but fieldInfo is the declaring field, so it is the collection type.
        /// Null for ContentSceneLink and for anything that is not a link.
        /// </summary>
        public static Type AssetTypeOf(FieldInfo fieldInfo)
            => fieldInfo == null ? null : AssetTypeOfLinkType(ElementTypeOf(fieldInfo.FieldType));

        public static Type AssetTypeOfLinkType(Type linkType)
        {
            for (var t = linkType; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ContentLink<>))
                    return t.GetGenericArguments()[0];
            return null;
        }

        internal static Type ElementTypeOf(Type type)
        {
            if (type == null) return null;
            if (type.IsArray) return type.GetElementType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return type.GetGenericArguments()[0];
            return type;
        }

        /// <summary>
        /// What the object field should accept. A component T lives on a prefab, and the catalog entry is the prefab
        /// itself, so the field takes the GameObject and the component is checked separately.
        /// </summary>
        public static Type ObjectFieldTypeFor(Type assetType)
        {
            if (assetType == null) return typeof(Object);
            return typeof(Component).IsAssignableFrom(assetType) ? typeof(GameObject) : assetType;
        }

        // ───────────── validation ─────────────

        public static NameStatus Validate(string name, Type assetType, bool scene = false)
        {
            if (string.IsNullOrEmpty(name)) return NameStatus.Empty;
            if (!IsResourcesName(name)) name = Composed(name);   // a res: name keeps its spelling (ResolveAsset)
            Object asset;
            if (IsResourcesName(name))
            {
                if (scene) return NameStatus.NotInCatalog;   // Resources holds no scenes we load
                asset = ResolveAsset(name);
                if (asset == null) return NameStatus.NotInCatalog;
            }
            else
            {
                if (LoadCatalogs().Length == 0) return NameStatus.NoCatalog;
                bool known = scene ? s_SceneIdByName.ContainsKey(name) : s_IdByName.ContainsKey(name);
                if (!known) return NameStatus.NotInCatalog;
                asset = ResolveAsset(name);
                if (asset == null) return NameStatus.NotInCatalog;
            }
            if (scene) return asset is SceneAsset ? NameStatus.Ok : NameStatus.TypeMismatch;
            if (assetType == null) return NameStatus.Ok;

            if (typeof(Component).IsAssignableFrom(assetType))
            {
                var go = asset as GameObject;
                return go != null && go.GetComponent(assetType) != null ? NameStatus.Ok : NameStatus.TypeMismatch;
            }
            return assetType.IsInstanceOfType(asset) ? NameStatus.Ok : NameStatus.TypeMismatch;
        }

        // ───────────── links with a sprite name (ContentLink m_Name + m_SubObjectName) ─────────────

        /// <summary>
        /// The main name and sprite name for an asset picked into a link field: ("Icons/sheet", "s0") for a sprite, (name, "")
        /// for anything else. The refusals are <see cref="TryGetNameForAsset"/>'s. Catalog-first like it, so a catalogued
        /// sprite keeps the main name its catalog was built with.
        /// </summary>
        public static bool TryGetLinkForAsset(Object asset, out string name, out string sub, out string reason)
        {
            name = null;
            sub = null;
            if (!TryGetNameForAsset(asset, out var full, out reason)) return false;
            if (!AssetDatabase.IsSubAsset(asset))
            {
                name = full;
                sub = "";
                return true;
            }
            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(asset);
            if (id.IsValid && s_LinkById.TryGetValue(id.ToString(), out var catalogued))
            {
                (name, sub) = catalogued;
                return true;
            }
            if (IsResourcesName(full) && TrySplitResourcesName(full, out var resPath, out var resSub) && resSub != null)
            {
                name = ContentService.ResourcesPrefix + resPath;
                sub = resSub;
                return true;
            }
            var path = AssetDatabase.GetAssetPath(asset);
            var root = RootFor(path);
            if (root == null && asset is Sprite packed && TryAliasFor(packed, out var aliasRoot, out var atlasPath)) { name = ToName(aliasRoot, atlasPath); sub = Composed(packed.name); return true; }   // only an atlas names it: the texture lies outside every root
            if (root == null) { name = full; sub = ""; return true; }   // catalogued under a name that does not split: keep it whole, it still loads
            name = ToName(root, path);
            sub = Composed(asset.name);
            return true;
        }

        /// <summary>The catalog name a link resolves to: "main[sprite]", or the name itself for an empty sprite name. Cached.</summary>
        internal static string LinkName(string name, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return name;
            if (!s_LinkNames.TryGetValue((name, sub), out var composed)) s_LinkNames[(name, sub)] = composed = $"{name}[{sub}]";
            return composed;
        }

        /// <summary>The asset a link points to. With a sprite name the path search needs no parsing.</summary>
        public static Object ResolveAsset(string name, string sub)
        {
            if (string.IsNullOrEmpty(sub)) return ResolveAsset(name);
            if (string.IsNullOrEmpty(name)) return null;
            if (IsResourcesName(name)) return ResolveAsset(LinkName(name, sub));   // as given: see ResolveAsset(string)
            name = Composed(name); sub = Composed(sub);
            SyncStamp();
            if (s_AssetByLink.TryGetValue((name, sub), out var cached)) return cached;

            LoadCatalogs();
            Object found = null;
            if (s_IdByName.TryGetValue(LinkName(name, sub), out var id))
                found = LoadableObjectIdEditorUtility.LoadableObjectIdToObject(id);
            if (found == null)
                found = FindByPath(name, sub);

            s_AssetByLink[(name, sub)] = found;   // negatives are cached too
            return found;
        }

        /// <summary>
        /// A link's status. The composed name goes through <see cref="Validate(string, Type, bool)"/> as it is: that checks the
        /// catalog before it resolves, so it never reaches the parsing path search.
        /// </summary>
        public static NameStatus Validate(string name, string sub, Type assetType)
            => string.IsNullOrEmpty(sub) ? Validate(name, assetType) : Validate(string.IsNullOrEmpty(name) ? name : LinkName(name, sub), assetType);

        /// <summary>
        /// A name typed into a link's name line, split into (main name, sprite name) against the project catalogs: a catalogued
        /// "Icons/sheet[s0]" gives ("Icons/sheet", "s0"). Anything else stays whole with no sprite name — nothing is parsed,
        /// since a file name may hold '[' — and a whole name loads as it is. A res: name has no catalog: it splits as the
        /// runtime splits it, at the last '[' when it ends with ']'.
        /// </summary>
        internal static void SplitLinkName(string text, out string name, out string sub)
        {
            text ??= string.Empty;
            if (IsResourcesName(text))
            {
                if (TrySplitResourcesName(text, out var path, out var resSub) && resSub != null) { name = ContentService.ResourcesPrefix + path; sub = resSub; }
                else { name = text; sub = string.Empty; }
                return;
            }
            LoadCatalogs();
            if (s_LinkByName.TryGetValue(Composed(text), out var catalogued)) { (name, sub) = catalogued; return; }
            name = text;
            sub = string.Empty;
        }

        /// <summary>One line for the inspector tooltip / console warning.</summary>
        public static string Describe(NameStatus status, string name) => status switch
        {
            NameStatus.Ok => name,
            NameStatus.Empty => "no name set",
            NameStatus.NoCatalog => "no ContentCatalog in the project — run Window > ContentLink > Update Content Catalog",
            NameStatus.NotInCatalog when IsResourcesName(name) => $"'{name}' is not in a Resources folder a player has — the path is below the Resources folder, without the extension; a Resources folder under an Editor folder is editor-only",
            NameStatus.NotInCatalog => $"'{name}' is not in a catalog — a new asset: run Window > ContentLink > Update Content Catalog; an old name (the asset was renamed or moved, or its root's name style changed): pick the asset again",
            NameStatus.TypeMismatch => $"'{name}' is not of the type this field holds",
            NameStatus.Missing => $"'{name}' has no asset — it was deleted, or its sub-asset was renamed",
            _ => name,
        };

        // ───────────── references (ContentRef / ContentSceneRef) ─────────────
        //
        // A reference is a GUID and a sprite name, so asset ↔ value goes through the AssetDatabase rather than the name
        // rule, and the name it resolves to is for display and validation only. Everything is cached like the name side
        // and dropped with it (Invalidate); the statuses also when an asset is imported again (InvalidateStatuses).

        /// <summary>
        /// The GUID and sprite name for an asset picked into a reference field. The refusals are TryGetNameForAsset's —
        /// outside every catalog and content root, or a sub-asset that is not a sprite — so no reference is made that a
        /// build could not carry.
        /// </summary>
        public static bool TryGetRefForAsset(Object asset, out string guid, out string sub, out string reason)
        {
            guid = null;
            sub = null;
            if (!TryGetNameForAsset(asset, out var named, out reason)) return false;
            if (IsResourcesName(named))
            {
                reason = $"'{AssetDatabase.GetAssetPath(asset)}' is in a Resources folder: a reference needs a catalog — link it by name with a ContentLink ('{named}')";
                return false;
            }
            // Only an atlas names the sprite (its texture lies outside every root): the texture's GUID would reach no catalog, the atlas's does
            if (asset is Sprite && TryAtlasOfName(named, out _, out var atlasGuid))
            {
                guid = atlasGuid;
                sub = Composed(asset.name);
                return true;
            }
            var assetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
            if (string.IsNullOrEmpty(assetGuid))
            {
                reason = $"'{asset.name}' has no asset GUID";
                return false;
            }
            guid = assetGuid;
            sub = AssetDatabase.IsSubAsset(asset) ? Composed(asset.name) : "";
            return true;
        }

        /// <summary>The asset a reference points to — found by GUID, so it follows moves and renames. A sprite of that name first.</summary>
        public static Object ResolveRefAsset(string guid, string sub)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            sub = Composed(sub ?? "");   // a sprite is named as its file: compared composed, whichever form the reference was saved in
            if (s_AssetByRef.TryGetValue((guid, sub), out var cached)) return cached;

            Object found = null;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
                found = sub.Length == 0 ? AssetDatabase.LoadMainAssetAtPath(path) : SubAssetNamed(path, sub);
            if (found == null && sub.Length > 0 && !string.IsNullOrEmpty(path) && AssetDatabase.LoadMainAssetAtPath(path) is SpriteAtlas atlas && NamesItsSprites(path, atlas))
                found = PackedSprite(atlas, sub, out _);   // (the atlas GUID, a sprite it packs) — the shape Addressables wrote
            s_AssetByRef[(guid, sub)] = found;   // negatives are cached too
            return found;
        }

        static Object SubAssetNamed(string path, string sub)
        {
            Object named = null;
            foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (rep == null || Composed(rep.name) != sub) continue;
                if (rep is Sprite) return rep;
                named ??= rep;
            }
            return named;
        }

        /// <summary>
        /// The catalog name a reference resolves to: a project catalog first, then the path rule under the content roots
        /// (with "[sprite]" for a sub-asset). Null when neither can name it.
        /// </summary>
        public static string NameForRef(string guid, string sub, bool scene = false)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            sub = scene ? "" : Composed(sub ?? "");
            LoadCatalogs();
            if (scene ? s_SceneNameByGuid.TryGetValue(guid, out var known) : s_NameByRef.TryGetValue((guid, sub), out known)) return known;
            if (s_RefNames.TryGetValue((guid, sub, scene), out var cached)) return cached;

            string name = null;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var root = string.IsNullOrEmpty(path) ? null : RootFor(path);
            if (root != null) name = sub.Length == 0 ? ToName(root, path) : $"{ToName(root, path)}[{sub}]";
            s_RefNames[(guid, sub, scene)] = name;   // negatives are cached too
            return name;
        }

        /// <summary>
        /// What a serialized reference is, with the tooltip that says why. Where two answers fit, the order decides:
        ///   1. no GUID → Empty
        ///   2. no asset has the GUID → Missing
        ///   3. the asset is a sprite atlas — the atlas has no sub-asset of the sprite's name, so this comes before 4: a V1 or
        ///      variant atlas → NotInCatalog (its sprites get no names) · a name it does not pack → Missing · a name two packed
        ///      sprites share → NotInCatalog (the build refuses it) · a sprite it packs goes on to 6
        ///   4. no sub-asset of that name → Missing: a renamed slice
        ///   5. a sub-asset of that name that is not a sprite → NotInCatalog: only sprites are catalogued as sub-assets
        ///   6. under no catalog and no content root → NotInCatalog: moving the file under a root keeps its GUID
        ///   7. otherwise the name it resolves to is <see cref="Validate"/>d
        /// Cached per (reference, field type); dropped with the other caches, and when any asset is imported again — a prefab
        /// saved with a component added or removed raises no projectChanged (measured on 6000.6).
        /// </summary>
        public static NameStatus ValidateRef(string guid, string sub, Type assetType, bool scene, out string tooltip)
        {
            sub = scene ? "" : Composed(sub ?? "");
            if (string.IsNullOrEmpty(guid)) { tooltip = null; return NameStatus.Empty; }
            SyncStamp();
            if (s_RefStatus.TryGetValue((guid, sub, assetType, scene), out var cached)) { tooltip = cached.Item2; return cached.Item1; }

            var status = ValidateRefUncached(guid, sub, assetType, scene, out tooltip);
            s_RefStatus[(guid, sub, assetType, scene)] = (status, tooltip);
            return status;
        }

        static NameStatus ValidateRefUncached(string guid, string sub, Type assetType, bool scene, out string tooltip)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var main = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
            if (main == null)
            {
                tooltip = $"no asset has the GUID {guid} — it was deleted, or it belongs to another project";
                return NameStatus.Missing;
            }
            if (!scene && main is SpriteAtlas atlas)
            {
                if (IsV1AtlasPath(path))
                {
                    tooltip = $"'{path}' is a V1 sprite atlas — content directories do not carry its textures (the sprite would load without one): move it to Sprite Atlas V2";
                    return NameStatus.NotInCatalog;
                }
                if (atlas.isVariant)
                {
                    tooltip = $"'{path}' is a variant atlas — its sprites are bound to the master's page: pick them through the master atlas";
                    return NameStatus.NotInCatalog;
                }
                if (sub.Length > 0)
                {
                    var packed = PackedSprite(atlas, sub, out bool ambiguous);
                    if (ambiguous)
                    {
                        tooltip = $"'{path}' packs two sprites named '{sub}' — the build refuses that name: rename one";
                        return NameStatus.NotInCatalog;
                    }
                    if (packed == null)
                    {
                        tooltip = $"'{path}' packs no sprite named '{sub}' — was it renamed, or taken out of the atlas?";
                        return NameStatus.Missing;
                    }
                }
            }
            else if (sub.Length > 0)
            {
                var named = SubAssetNamed(path, sub);
                if (named == null)
                {
                    tooltip = $"'{path}' has no sub-asset named '{sub}' — was the slice renamed?";
                    return NameStatus.Missing;
                }
                if (!(named is Sprite))
                {
                    tooltip = $"'{sub}' in '{path}' is a {named.GetType().Name}, not a sprite — only sprites are catalogued as sub-assets";
                    return NameStatus.NotInCatalog;
                }
            }

            var name = NameForRef(guid, sub, scene);
            if (name == null)
            {
                tooltip = CaseOnlyRoot(path) ?? (TryGetResourcesPath(path, out var resPath, out _)
                    ? $"'{path}' is in a Resources folder: a reference needs a catalog — link it by name with a ContentLink ('{ContentService.ResourcesPrefix}{resPath}')"
                    : $"'{path}' is not in a catalog and not under a content root ({string.Join(", ", SourceRoots())}) — moving it under one keeps its GUID");
                return NameStatus.NotInCatalog;
            }

            var status = Validate(name, assetType, scene);
            tooltip = status == NameStatus.TypeMismatch && assetType == typeof(Sprite) && sub.Length == 0 && main is Texture2D
                ? $"'{name}' is the texture, not a sprite — pick the sprite itself (a reference without a sprite name loads the texture)"
                : status == NameStatus.TypeMismatch && assetType == typeof(Sprite) && sub.Length == 0 && main is SpriteAtlas
                ? $"'{name}' is the atlas, not a sprite — pick a sprite it packs (drop the atlas on the field)"
                : Describe(status, name);
            return status;
        }

        /// <summary>The T of the ContentRef&lt;T&gt; a field holds, arrays and List&lt;&gt; unwrapped. Null for anything else, ContentSceneRef included.</summary>
        public static Type AssetTypeOfRef(FieldInfo fieldInfo)
            => fieldInfo == null ? null : AssetTypeOfRefType(ElementTypeOf(fieldInfo.FieldType));

        /// <summary>A struct has no base types to walk, unlike <see cref="AssetTypeOfLinkType"/>.</summary>
        public static Type AssetTypeOfRefType(Type refType)
            => refType != null && refType.IsGenericType && refType.GetGenericTypeDefinition() == typeof(ContentRef<>) ? refType.GetGenericArguments()[0] : null;
    }
}
