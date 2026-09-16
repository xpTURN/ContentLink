using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Unity.Loading;
using UnityEditor;
using UnityEngine;
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
                }
                foreach (var e in catalog.Scenes)
                {
                    if (string.IsNullOrEmpty(e.name)) continue;
                    s_SceneIdByName[e.name] = e.id;
                    s_NameBySceneId[e.id.ToString()] = e.name;
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

        /// <summary>One line for the inspector tooltip / console warning.</summary>
        public static string Describe(NameStatus status, string name) => status switch
        {
            NameStatus.Ok => name,
            NameStatus.Empty => "no name set",
            NameStatus.NoCatalog => "no ContentCatalog in the project — run Window > ContentLink > Update Content Catalog",
            NameStatus.NotInCatalog => $"'{name}' is not in a catalog — run Window > ContentLink > Update Content Catalog",
            NameStatus.TypeMismatch => $"'{name}' is not of the type this field holds",
            _ => name,
        };
    }
}
