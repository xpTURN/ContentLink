using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Profile = xpTURN.ContentLink.Editor.ContentBuildSettings.Profile;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// The batch-mode entry point:
    /// <code>
    /// Unity -batchmode -nographics -quit -projectPath … -buildTarget StandaloneOSX -logFile … \
    ///       -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
    ///       -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.compression LZ4 -contentlink.partitioner trie
    /// </code>
    /// Overrides change the named profile for the run only — it is put back afterwards, and the settings file is never written from here.
    /// A failed build, an unknown key or an unknown profile ends the editor with exit code 1. There is no progress
    /// bar in batch mode; the runner's stage logs are the progress. tools/build-content.sh wraps the call.
    /// <c>UpdateCatalogs</c> is the second entry point: the same arguments, but only the catalog stage runs —
    /// the command-line twin of the <i>Update Content Catalog</i> menu item.
    /// </summary>
    public static class ContentBuildCLI
    {
        public const string Prefix = "-contentlink.";
        public const string All = "all";

        static readonly string[] k_Keys =
        {
            "profile", "source", "output", "archive", "maxArchiveMB", "compression",
            "cleanBuildCache", "disableWriteTypeTree", "failBuildWhenErrorsLogged", "postProcessors", "partitioner", "nameStyle",
            "contentVersion",
        };

        public sealed class Options
        {
            /// <summary>null = the active profile; "all" = every profile.</summary>
            public string Profile;
            /// <summary>Run-level: stamped into every index this run packs, whichever profiles it builds. null = the setting.</summary>
            public string ContentVersion;
            public readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The -executeMethod target: reads the process arguments and exits with 1 when the build did not succeed.</summary>
        public static void Build()
        {
            int code = Run(Environment.GetCommandLineArgs(), catalogsOnly: false);
            if (code != 0) EditorApplication.Exit(code);
        }

        /// <summary>The second -executeMethod target: the same arguments, but only the catalogs are rewritten — no build. Exits with 1 on failure.</summary>
        public static void UpdateCatalogs()
        {
            int code = Run(Environment.GetCommandLineArgs(), catalogsOnly: true);
            if (code != 0) EditorApplication.Exit(code);
        }

        /// <summary>Everything Build does, with the arguments injected: 0 on success, 1 otherwise. Never writes the settings file.</summary>
        public static int Run(string[] args) => Run(args, catalogsOnly: false);

        /// <summary>Run with the mode chosen: a build, or the catalog stage alone (<paramref name="catalogsOnly"/>). 0 on success, 1 otherwise.</summary>
        public static int Run(string[] args, bool catalogsOnly)
        {
            Profile overridden = null;
            object[] kept = null;
            try
            {
                if (!Parse(args, out var options, out var error)) return Fail(error);
                ContentBuildSettings.ContentVersionOverride = options.ContentVersion;   // in memory for this run only
                var settings = ContentBuildSettings.instance;
                var target = EditorUserBuildSettings.activeBuildTarget;
                string verb = catalogsOnly ? "updating catalogs of" : "building";

                if (options.Profile == All)
                {
                    if (options.Overrides.Count > 0) return Fail("overrides apply to one profile — name it instead of 'all', or drop them");
                    Debug.Log($"[ContentLink CLI] {verb} every profile ({settings.Profiles.Count}) for target {target}");
                    if (catalogsOnly)
                    {
                        int done = 0;
                        foreach (var p in settings.Profiles)
                        {
                            var errors = new List<string>();
                            if (!ContentBuildRunner.UpdateCatalogs(p, errors)) return Fail($"catalog update of '{p.Name}' failed: {string.Join(" | ", errors)} — {done} of {settings.Profiles.Count} updated before it");
                            ++done;
                        }
                        Debug.Log($"[ContentLink CLI] OK · catalogs of {done} profile(s)");
                        return 0;
                    }
                    var results = ContentBuildRunner.BuildAll(showProgress: false);
                    var failed = results.Find(r => !r.Succeeded);
                    if (failed != null) return Fail($"build of '{failed.ProfileName}' failed: {string.Join(" | ", failed.Errors)} — {results.Count - 1} of {settings.Profiles.Count} built before it");
                    Debug.Log($"[ContentLink CLI] OK · {results.Count} profile(s) · target {target}");
                    return 0;
                }

                var profile = options.Profile == null ? settings.Active : settings.Find(options.Profile);
                if (profile == null) return Fail($"profile '{options.Profile}' does not exist (known: {Names(settings)})");
                if (options.Overrides.Count > 0) { overridden = profile; kept = Keep(profile); }
                if (!Apply(profile, options.Overrides, out error)) return Fail(error);
                Debug.Log($"[ContentLink CLI] {verb} '{profile.Name}' for target {target}" + (options.Overrides.Count > 0 ? $" with {options.Overrides.Count} override(s), in memory only" : ""));
                if (catalogsOnly)
                {
                    var errors = new List<string>();
                    var paths = new List<string>();
                    if (!ContentBuildRunner.UpdateCatalogs(profile, errors, paths)) return Fail($"catalog update of '{profile.Name}' failed: {string.Join(" | ", errors)}");
                    Debug.Log($"[ContentLink CLI] OK · catalogs of '{profile.Name}' · {paths.Count} catalog(s)");
                    return 0;
                }
                var result = ContentBuildRunner.Build(profile, showProgress: false);
                if (!result.Succeeded) return Fail($"build of '{profile.Name}' failed: {string.Join(" | ", result.Errors)}");
                Debug.Log($"[ContentLink CLI] OK · '{result.ProfileName}' → {result.OutputPath} · {result.Files.Length} files · {result.Archives.Length} archives · {result.Milliseconds:F0} ms · target {result.Target}");
                return 0;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                return 1;
            }
            finally
            {
                ContentBuildSettings.ContentVersionOverride = null;
                if (overridden != null) { Put(overridden, kept); ContentBuildSettings.instance.MarkDirty(); }   // the overrides were this run's: the profile is the settings' own object
            }
        }

        /// <summary>Picks the -contentlink.&lt;key&gt; &lt;value&gt; pairs out of the arguments; everything else is Unity's. An unknown key or a missing value is an error, never ignored.</summary>
        public static bool Parse(string[] args, out Options options, out string error)
        {
            options = new Options();
            error = null;
            for (int i = 0; i < args.Length; ++i)
            {
                if (!args[i].StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var key = args[i].Substring(Prefix.Length);
                var known = Array.Find(k_Keys, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (known == null) { error = $"unknown option {args[i]} (known: {string.Join(", ", k_Keys)})"; return false; }
                if (i + 1 >= args.Length) { error = $"{args[i]} needs a value"; return false; }
                var value = args[++i];
                if (value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) { error = $"{args[i - 1]} needs a value, not the option {value}"; return false; }
                if (known == "profile") options.Profile = string.Equals(value, All, StringComparison.OrdinalIgnoreCase) ? All : value;
                else if (known == "contentVersion") options.ContentVersion = value;
                else options.Overrides[known] = value;
            }
            return true;
        }

        /// <summary>Writes the overrides into the profile object — in memory; nothing here or in the runner saves the settings.</summary>
        public static bool Apply(Profile profile, Dictionary<string, string> overrides, out string error)
        {
            error = null;
            if (overrides.Count > 0) ContentBuildSettings.instance.MarkDirty();   // the caches that watch the revision see the in-memory change, even one cut short by a refused value; nothing is saved
            foreach (var kv in overrides)
            {
                var value = kv.Value;
                switch (kv.Key)
                {
                    case "source":   // typed: stored as the build window stores it
                        profile.SourceRoot = ContentBuildSettings.NormalizeRoot(value);
                        if (ContentLinkEditorUtility.DiskPath(profile.SourceRoot) is string disk) profile.SourceRoot = disk;
                        break;
                    case "output": profile.OutputPath = ContentBuildSettings.NormalizePath(value); break;
                    case "archive":
                        if (!Enum.TryParse(value, true, out ContentBuildSettings.ArchiveMode archive) || !Enum.IsDefined(typeof(ContentBuildSettings.ArchiveMode), archive)) return Bad(kv, "None | Engine | Packed", out error);
                        profile.Archive = archive; break;
                    case "compression":
                        if (!Enum.TryParse(value, true, out ContentBuildSettings.CompressionKind compression) || !Enum.IsDefined(typeof(ContentBuildSettings.CompressionKind), compression)) return Bad(kv, "Uncompressed | LZ4 | LZMA", out error);
                        profile.Compression = compression; break;
                    case "maxArchiveMB":
                        if (!int.TryParse(value, out int mb) || mb < 1) return Bad(kv, "a whole number of megabytes, 1 or more", out error);
                        profile.MaxArchiveMB = mb; break;
                    case "cleanBuildCache":
                        if (!TryBool(value, out bool clean)) return Bad(kv, "true | false", out error);
                        profile.CleanBuildCache = clean; break;
                    case "disableWriteTypeTree":
                        if (!TryBool(value, out bool noTrees)) return Bad(kv, "true | false", out error);
                        profile.DisableWriteTypeTree = noTrees; break;
                    case "failBuildWhenErrorsLogged":
                        if (!TryBool(value, out bool fail)) return Bad(kv, "true | false", out error);
                        profile.FailBuildWhenErrorsLogged = fail; break;
                    case "postProcessors":
                        var names = TypeNames(value, out var twoWays);
                        if (names == null) { error = $"{Prefix}{kv.Key} '{twoWays[0]}, {twoWays[1]}' is both a type in the assembly '{twoWays[1]}' and two post-processors — for the one, add the type's Version= part; for the two, list '{twoWays[1]}' first"; return false; }
                        profile.PostProcessors = names; break;
                    case "partitioner":
                        if (value.Trim().Length == 0) return Bad(kv, "a type name, trie or sequential", out error);
                        profile.Partitioner = ContentArchivePacker.ExpandAlias(value.Trim()); break;
                    case "nameStyle":
                        if (!Enum.TryParse(value, true, out ContentNameStyle style) || !Enum.IsDefined(typeof(ContentNameStyle), style)) return Bad(kv, "PathWithoutExtension | PathWithExtension", out error);
                        profile.NameStyle = style; break;
                    default: error = $"unknown option {Prefix}{kv.Key}"; return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The comma-separated names; a type and the assembly it is in are one name — "Namespace.Type, Assembly", the form the window stores, with its
        /// Version= parts if given. Null, with the two pieces, when such a pair also reads as two post-processors: the second piece is one itself.
        /// </summary>
        static string[] TypeNames(string value, out string[] twoWays)
        {
            twoWays = null;
            var pieces = new List<string>();
            foreach (var n in value.Split(',')) if (n.Trim().Length > 0) pieces.Add(n.Trim());
            var names = new List<string>();
            for (int i = 0; i < pieces.Count; ++i)
            {
                var name = pieces[i];
                if (i + 1 < pieces.Count && ContentBuildRunner.ResolvePostProcessor(name + ", " + pieces[i + 1]) != null)   // two pieces only: Mono reads "A, Asm, B" as A in Asm
                {
                    bool versioned = i + 2 < pieces.Count && pieces[i + 2].Contains("=");
                    if (!versioned && typeof(IContentBuildPostProcess).IsAssignableFrom(ContentBuildRunner.ResolvePostProcessor(pieces[i + 1])))
                    { twoWays = new[] { name, pieces[i + 1] }; return null; }   // the comma used to make these two names: say so rather than pick one reading
                    name += ", " + pieces[++i];
                    while (i + 1 < pieces.Count && pieces[i + 1].Contains("=")) name += ", " + pieces[++i];
                }
                names.Add(name);
            }
            return names.ToArray();
        }

        static readonly FieldInfo[] s_Kept = typeof(Profile).GetFields(BindingFlags.Public | BindingFlags.Instance);   // every field's value: a key added later is put back too. A category is the same object, so what a build sets in it (the catalog reference) stays
        static object[] Keep(Profile p) => Array.ConvertAll(s_Kept, f => f.GetValue(p));
        static void Put(Profile p, object[] kept) { for (int i = 0; i < s_Kept.Length; ++i) s_Kept[i].SetValue(p, kept[i]); }

        static bool TryBool(string value, out bool result)
        {
            if (bool.TryParse(value, out result)) return true;
            if (value == "1") { result = true; return true; }
            if (value == "0") { result = false; return true; }
            return false;
        }

        static bool Bad(KeyValuePair<string, string> kv, string expected, out string error)
        {
            error = $"{Prefix}{kv.Key} '{kv.Value}' is not valid; expected {expected}";
            return false;
        }

        static string Names(ContentBuildSettings settings)
        {
            var names = new List<string>();
            foreach (var p in settings.Profiles) names.Add(p.Name);
            return string.Join(", ", names);
        }

        static int Fail(string message)
        {
            Debug.LogError("[ContentLink CLI] FAILED · " + message);
            return 1;
        }
    }
}
