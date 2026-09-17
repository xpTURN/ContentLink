using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Loading;
using UnityEditor;
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

        /// <summary>"Assets/Content/Icons/sword.png" under "Assets/Content" → "Icons/sword".</summary>
        internal static string ToName(string sourceRoot, string assetPath)
        {
            var rel = assetPath.Substring(sourceRoot.Length + 1);
            var dir = Path.GetDirectoryName(rel);
            var file = Path.GetFileNameWithoutExtension(rel);
            return (string.IsNullOrEmpty(dir) ? file : $"{dir}/{file}").Replace('\\', '/');
        }

        /// <summary>Sprite sub-assets are "name[sub]". Only sprites are registered, so only sprites get a name.</summary>
        internal static string ToName(string sourceRoot, string assetPath, Object subAsset)
            => $"{ToName(sourceRoot, assetPath)}[{subAsset.name}]";

        internal static bool IsUnder(string sourceRoot, string assetPath)
            => !string.IsNullOrEmpty(sourceRoot) && !string.IsNullOrEmpty(assetPath)
               && assetPath.Length > sourceRoot.Length + 1
               && assetPath.StartsWith(sourceRoot, StringComparison.Ordinal)
               && assetPath[sourceRoot.Length] == '/';

        // ───────────── catalog index ─────────────
        //
        // The catalogs carry no name index and LoadableObjectIdToObject is an asset load, so an uncached
        // lookup would cost (visible fields × entries) per repaint. Everything below is cached and dropped
        // on projectChanged.

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
        static string[] s_Roots;          // content roots, longest first
        static string s_RootsStaticRoot;  // the builder's static root s_Roots was built with
        static int s_RootsRevision = -1;  // the ContentBuildSettings revision s_Roots was built with

        [InitializeOnLoadMethod]
        static void Install()
        {
            EditorApplication.projectChanged -= Invalidate;
            EditorApplication.projectChanged += Invalidate;
        }

        /// <summary>Drops the cached catalogs and lookups. Called on every project change; safe to call by hand.</summary>
        public static void Invalidate()
        {
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
            s_Roots = null;
        }

        /// <summary>Every ContentCatalog in the project — the runtime can register several directories.</summary>
        public static ContentCatalog[] LoadCatalogs()
        {
            if (s_Catalogs != null)
            {
                foreach (var c in s_Catalogs)
                    if (c == null) { Invalidate(); break; }   // an asset was deleted under us
            }
            if (s_Catalogs != null) return s_Catalogs;

            var guids = AssetDatabase.FindAssets("t:ContentCatalog");
            var list = new List<ContentCatalog>(guids.Length);
            foreach (var guid in guids)
            {
                var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>(AssetDatabase.GUIDToAssetPath(guid));
                if (catalog != null) list.Add(catalog);
            }
            s_Catalogs = list.ToArray();

            foreach (var catalog in s_Catalogs)
            {
                foreach (var e in catalog.Entries)
                {
                    if (string.IsNullOrEmpty(e.name)) continue;
                    s_IdByName[e.name] = e.id;
                    s_NameById[e.id.ToString()] = e.name;
                    if (!string.IsNullOrEmpty(e.guid)) s_NameByRef[(e.guid, e.sub ?? "")] = e.name;
                    // The builder writes "main[sprite]" and the same sprite name into sub, so the suffix is checked, not parsed;
                    // an entry that does not match (a hand-made catalog) is left to the path rule.
                    if (!string.IsNullOrEmpty(e.sub) && e.name.Length > e.sub.Length + 2 && e.name[e.name.Length - 1] == ']'
                        && e.name[e.name.Length - e.sub.Length - 2] == '[' && string.CompareOrdinal(e.name, e.name.Length - e.sub.Length - 1, e.sub, 0, e.sub.Length) == 0)
                    {
                        var link = (e.name.Substring(0, e.name.Length - e.sub.Length - 2), e.sub);
                        s_LinkById[e.id.ToString()] = link;
                        s_LinkByName[e.name] = link;
                    }
                }
                foreach (var e in catalog.Scenes)
                {
                    if (string.IsNullOrEmpty(e.name)) continue;
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
                reason = $"'{path}' is not in a catalog and not under a content root ({string.Join(", ", SourceRoots())})";
                return false;
            }
            if (AssetDatabase.IsSubAsset(asset) && !(asset is Sprite))
            {
                reason = $"'{asset.name}' is a sub-asset; only sprites are registered as 'name[sub]'";
                return false;
            }
            name = AssetDatabase.IsSubAsset(asset) ? ToName(root, path, asset) : ToName(root, path);
            return true;
        }

        // ───────────── name → asset ─────────────

        /// <summary>The asset a name refers to: catalog first, then a path search under SourceRoot.</summary>
        public static Object ResolveAsset(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (s_AssetByName.TryGetValue(name, out var cached)) return cached;

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
        /// and confirm with the same rule the builder uses. Runs only on a catalog miss, and the result is cached.
        /// </summary>
        static Object FindByPath(string name)
        {
            string sub = null;
            var main = name;
            int open = name.LastIndexOf('[');
            if (open > 0 && name.EndsWith("]", StringComparison.Ordinal))
            {
                sub = name.Substring(open + 1, name.Length - open - 2);
                main = name.Substring(0, open);
            }
            return FindByPath(main, sub);
        }

        /// <summary><see cref="FindByPath(string)"/> for a name and sprite name already apart: nothing to parse. A null sub is the main asset.</summary>
        static Object FindByPath(string main, string sub)
        {
            var leaf = main;
            int slash = main.LastIndexOf('/');
            if (slash >= 0) leaf = main.Substring(slash + 1);
            if (string.IsNullOrEmpty(leaf)) return null;

            foreach (var root in SourceRoots())   // longest first, so a nested root wins
            {
                if (!AssetDatabase.IsValidFolder(root)) continue;
                foreach (var guid in AssetDatabase.FindAssets(leaf, new[] { root }))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetDatabase.IsValidFolder(path) || !IsUnder(root, path)) continue;
                    if (ToName(root, path) != main) continue;

                    if (sub == null) return AssetDatabase.LoadMainAssetAtPath(path);
                    foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
                        if (rep is Sprite && rep.name == sub) return rep;
                }
            }
            return null;
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
            if (LoadCatalogs().Length == 0) return NameStatus.NoCatalog;

            bool known = scene ? s_SceneIdByName.ContainsKey(name) : s_IdByName.ContainsKey(name);
            if (!known) return NameStatus.NotInCatalog;

            var asset = ResolveAsset(name);
            if (asset == null) return NameStatus.NotInCatalog;
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
            var path = AssetDatabase.GetAssetPath(asset);
            var root = RootFor(path);
            if (root == null) { name = full; sub = ""; return true; }   // catalogued under a name that does not split: keep it whole, it still loads
            name = ToName(root, path);
            sub = asset.name;
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
        /// since a file name may hold '[' — and a whole name loads as it is.
        /// </summary>
        internal static void SplitLinkName(string text, out string name, out string sub)
        {
            text ??= string.Empty;
            LoadCatalogs();
            if (s_LinkByName.TryGetValue(text, out var catalogued)) { (name, sub) = catalogued; return; }
            name = text;
            sub = string.Empty;
        }

        /// <summary>One line for the inspector tooltip / console warning.</summary>
        public static string Describe(NameStatus status, string name) => status switch
        {
            NameStatus.Ok => name,
            NameStatus.Empty => "no name set",
            NameStatus.NoCatalog => "no ContentCatalog in the project — run Window > ContentLink > Update Content Catalog",
            NameStatus.NotInCatalog => $"'{name}' is not in a catalog — run Window > ContentLink > Update Content Catalog",
            NameStatus.TypeMismatch => $"'{name}' is not of the type this field holds",
            NameStatus.Missing => $"'{name}' has no asset — it was deleted, or its sub-asset was renamed",
            _ => name,
        };

        // ───────────── references (ContentRef / ContentSceneRef) ─────────────
        //
        // A reference is a GUID and a sprite name, so asset ↔ value goes through the AssetDatabase rather than the name
        // rule, and the name it resolves to is for display and validation only. Everything is cached like the name side
        // and dropped on projectChanged.

        /// <summary>
        /// The GUID and sprite name for an asset picked into a reference field. The refusals are TryGetNameForAsset's —
        /// outside every catalog and content root, or a sub-asset that is not a sprite — so no reference is made that a
        /// build could not carry.
        /// </summary>
        public static bool TryGetRefForAsset(Object asset, out string guid, out string sub, out string reason)
        {
            guid = null;
            sub = null;
            if (!TryGetNameForAsset(asset, out _, out reason)) return false;
            var assetGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(asset));
            if (string.IsNullOrEmpty(assetGuid))
            {
                reason = $"'{asset.name}' has no asset GUID";
                return false;
            }
            guid = assetGuid;
            sub = AssetDatabase.IsSubAsset(asset) ? asset.name : "";
            return true;
        }

        /// <summary>The asset a reference points to — found by GUID, so it follows moves and renames. A sprite of that name first.</summary>
        public static Object ResolveRefAsset(string guid, string sub)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            sub ??= "";
            if (s_AssetByRef.TryGetValue((guid, sub), out var cached)) return cached;

            Object found = null;
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.IsNullOrEmpty(path))
                found = sub.Length == 0 ? AssetDatabase.LoadMainAssetAtPath(path) : SubAssetNamed(path, sub);
            s_AssetByRef[(guid, sub)] = found;   // negatives are cached too
            return found;
        }

        static Object SubAssetNamed(string path, string sub)
        {
            Object named = null;
            foreach (var rep in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (rep == null || rep.name != sub) continue;
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
            sub = scene ? "" : sub ?? "";
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
        ///   3. the asset is a sprite atlas → NotInCatalog: a sprite reached through an atlas cannot be catalogued (the
        ///      atlas has no sub-asset of the sprite's name either, so this comes before 4)
        ///   4. no sub-asset of that name → Missing: a renamed slice
        ///   5. a sub-asset of that name that is not a sprite → NotInCatalog: only sprites are catalogued as sub-assets
        ///   6. under no catalog and no content root → NotInCatalog: moving the file under a root keeps its GUID
        ///   7. otherwise the name it resolves to is <see cref="Validate"/>d
        /// Cached per (reference, field type); dropped on projectChanged.
        /// </summary>
        public static NameStatus ValidateRef(string guid, string sub, Type assetType, bool scene, out string tooltip)
        {
            sub = scene ? "" : sub ?? "";
            if (string.IsNullOrEmpty(guid)) { tooltip = null; return NameStatus.Empty; }
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
            if (!scene && main is SpriteAtlas)
            {
                tooltip = $"'{path}' is a sprite atlas — a sprite reached through an atlas is not supported; pick the sprite from its texture";
                return NameStatus.NotInCatalog;
            }
            if (sub.Length > 0)
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
                tooltip = $"'{path}' is not in a catalog and not under a content root ({string.Join(", ", SourceRoots())}) — moving it under one keeps its GUID";
                return NameStatus.NotInCatalog;
            }

            var status = Validate(name, assetType, scene);
            tooltip = status == NameStatus.TypeMismatch && assetType == typeof(Sprite) && sub.Length == 0 && main is Texture2D
                ? $"'{name}' is the texture, not a sprite — pick the sprite itself (a reference without a sprite name loads the texture)"
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
