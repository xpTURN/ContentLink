using System;
using System.Collections.Generic;
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
    /// Overrides change the named profile in memory only — the settings file is never written from here.
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
            "cleanBuildCache", "disableWriteTypeTree", "failBuildWhenErrorsLogged", "postProcessors", "partitioner",
        };

        public sealed class Options
        {
            /// <summary>null = the active profile; "all" = every profile.</summary>
            public string Profile;
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
            try
            {
                if (!Parse(args, out var options, out var error)) return Fail(error);
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
                if (known == "profile") options.Profile = string.Equals(value, All, StringComparison.OrdinalIgnoreCase) ? All : value;
                else options.Overrides[known] = value;
            }
            return true;
        }

        /// <summary>Writes the overrides into the profile object — in memory; nothing here or in the runner saves the settings.</summary>
        public static bool Apply(Profile profile, Dictionary<string, string> overrides, out string error)
        {
            error = null;
            foreach (var kv in overrides)
            {
                var value = kv.Value;
                switch (kv.Key)
                {
                    case "source": profile.SourceRoot = ContentBuildSettings.NormalizePath(value); break;
                    case "output": profile.OutputPath = ContentBuildSettings.NormalizePath(value); break;
                    case "archive":
                        if (!Enum.TryParse(value, true, out ContentBuildSettings.ArchiveMode archive)) return Bad(kv, "None | Engine | Packed", out error);
                        profile.Archive = archive; break;
                    case "compression":
                        if (!Enum.TryParse(value, true, out ContentBuildSettings.CompressionKind compression)) return Bad(kv, "Uncompressed | LZ4 | LZMA", out error);
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
                        var names = new List<string>();
                        foreach (var n in value.Split(',')) if (n.Trim().Length > 0) names.Add(n.Trim());
                        profile.PostProcessors = names.ToArray(); break;
                    case "partitioner":
                        if (value.Trim().Length == 0) return Bad(kv, "a type name, trie or sequential", out error);
                        profile.Partitioner = ContentArchivePacker.ExpandAlias(value.Trim()); break;
                    default: error = $"unknown option {Prefix}{kv.Key}"; return false;
                }
            }
            return true;
        }

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
