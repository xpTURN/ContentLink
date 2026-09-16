using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Build profiles for ContentDirectoryBuilder, stored under ProjectSettings so the whole team shares them.
    /// A profile is one content directory: a source folder, an output folder, one or more categories
    /// (a catalog each), and the archive/compression options.
    ///
    /// ScriptableSingleton never saves by itself — call <see cref="SaveSettings"/> after an edit the user committed.
    /// Build results are not stored here (they would dirty a shared file on every build); see SessionState in the
    /// runner. The CLI overrides values in memory and never saves.
    /// </summary>
    [FilePath("ProjectSettings/ContentLinkBuild.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class ContentBuildSettings : ScriptableSingleton<ContentBuildSettings>
    {
        public enum ArchiveMode { None, Engine, Packed }              // None: flat files; Engine: the engine's single archive; Packed: archives split by size
        public enum CompressionKind { Uncompressed, LZ4, LZMA }       // the engine's presets that mean something here

        /// <summary>A category: the catalog it writes and the sub folder it takes. Names stay relative to the profile root, not to the sub folder.</summary>
        [Serializable]
        public sealed class CatalogEntry
        {
            public LazyLoadReference<ContentCatalog> Catalog;   // a GUID reference, so moving the asset does not break it
            public string CreateAt = "";                        // where the catalog is created while Catalog is unset
            public string SubFolder = "";                       // "" = the whole source root. A filter, never the name base

            /// <summary>The catalog's asset path — the referenced asset's, or where one will be created.</summary>
            public string ResolvePath()
            {
                if (Catalog.isSet)
                {
                    var asset = Catalog.asset;
                    if (asset != null) return AssetDatabase.GetAssetPath(asset);
                }
                return CreateAt ?? "";
            }
        }

        [Serializable]
        public sealed class Profile
        {
            public string Name = "Main";                               // unique; the active profile and the CLI pick by it
            public string SourceRoot = "Assets/Content";
            public string OutputPath = "Build/Content/Main";           // outside Assets/: built files must not be imported
            public CatalogEntry[] Catalogs = { new CatalogEntry() };   // one category = the whole root
            public ArchiveMode Archive = ArchiveMode.Packed;           // a single archive is bad for distribution
            public int MaxArchiveMB = 20;                              // Packed: the size an archive is closed at, give or take one file
            public CompressionKind Compression = CompressionKind.LZ4;  // chunk based: no full decompression before use. Ignored when flat
            public bool CleanBuildCache;
            public bool DisableWriteTypeTree;                          // smaller, but only loads on the exact same engine version
            public bool FailBuildWhenErrorsLogged = true;              // an error during the build means the output is suspect
            public BuildTarget Target = BuildTarget.NoTarget;          // NoTarget = whatever target is active; else refuse other targets
            public string[] PostProcessors = Array.Empty<string>();    // assembly qualified type names, run in this order
            public string Partitioner = "";                            // Packed: the IContentArchivePartitioner type name; "" = the built-in hash trie
        }

        [SerializeField] int m_Version = 1;
        [SerializeField] Profile[] m_Profiles = Array.Empty<Profile>();
        [SerializeField] string m_ActiveName = "Main";

        /// <summary>Bumped on every edit. Caches keyed on the profiles (ContentLinkEditorUtility.SourceRoots) watch it.</summary>
        public static int Revision { get; private set; }

        public int Version => m_Version;

        public IReadOnlyList<Profile> Profiles
        {
            get { EnsureDefaults(); return m_Profiles; }
        }

        /// <summary>The profile the menu items, the static view and the window act on. Never null.</summary>
        public Profile Active
        {
            get
            {
                EnsureDefaults();
                var found = Find(m_ActiveName);
                if (found != null) return found;
                m_ActiveName = m_Profiles[0].Name;   // the active one was removed or renamed: fall back, do not invent
                return m_Profiles[0];
            }
        }

        public string ActiveName => Active.Name;

        public Profile Find(string name)
        {
            EnsureDefaults();   // a project without settings must find 'Main' too — the CLI asks by name before anything else
            if (string.IsNullOrEmpty(name) || m_Profiles == null) return null;
            foreach (var p in m_Profiles)
                if (p != null && p.Name == name) return p;
            return null;
        }

        public bool SetActive(string name)
        {
            if (Find(name) == null) return false;
            m_ActiveName = name;
            MarkDirty();
            return true;
        }

        /// <summary>A new profile with the shipping defaults; the catalog asset is created on the first build.</summary>
        public Profile Add(string name)
        {
            EnsureDefaults();
            if (string.IsNullOrEmpty(name) || Find(name) != null) return null;
            var profile = new Profile
            {
                Name = name,
                OutputPath = $"Build/Content/{name}",
                Catalogs = new[] { new CatalogEntry { CreateAt = $"Assets/ContentCatalogs/{name}.asset" } },
            };
            var list = new List<Profile>(m_Profiles) { profile };
            m_Profiles = list.ToArray();
            MarkDirty();
            return profile;
        }

        public bool Remove(string name)
        {
            EnsureDefaults();
            var list = new List<Profile>(m_Profiles);
            int removed = list.RemoveAll(p => p.Name == name);
            if (removed == 0) return false;
            m_Profiles = list.ToArray();
            MarkDirty();
            return true;
        }

        public void MarkDirty() => Revision++;

        /// <summary>Writes ProjectSettings/ContentLinkBuild.asset. The window calls this on committed edits; nothing else does.</summary>
        public void SaveSettings() => Save(true);

        /// <summary>
        /// A project that has no settings yet gets one profile made from the values the static builder API used
        /// until now, so its build keeps doing exactly what it did. Only new profiles get the shipping defaults.
        /// </summary>
        void EnsureDefaults()
        {
            if (m_Profiles != null && m_Profiles.Length > 0) return;
            m_Profiles = new[]
            {
                new Profile
                {
                    Name = "Main",
                    SourceRoot = ContentDirectoryBuilder.DefaultSourceRoot,
                    OutputPath = ContentDirectoryBuilder.DefaultOutputPath,
                    Catalogs = new[] { new CatalogEntry { CreateAt = ContentDirectoryBuilder.DefaultCatalogPath } },
                    Archive = ArchiveMode.None,                     // the legacy build was flat and uncompressed
                    Compression = CompressionKind.Uncompressed,
                },
            };
            m_ActiveName = "Main";
            Revision++;
        }

        // ───────────── validation ─────────────

        /// <summary>
        /// Everything that would make a build of the profile wrong, checked before anything is written. Empty = ok.
        /// Two of these exist because the name check alone cannot see them: overlapping sub folders put the same
        /// asset in two catalogs, and a catalog under its own source root catalogues itself.
        /// </summary>
        static readonly char[] k_NameChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        public List<string> Validate(Profile profile)
        {
            var errors = new List<string>();
            if (profile == null) { errors.Add("profile is null"); return errors; }

            if (string.IsNullOrEmpty(profile.Name)) errors.Add("profile name is empty");
            else if (profile.Name.IndexOfAny(k_NameChars) >= 0) errors.Add($"profile name '{profile.Name}' contains a character that cannot be in a file name (the name is used in archive names and default paths)");
            else
            {
                int same = 0;
                foreach (var p in Profiles) if (p != null && p.Name == profile.Name) ++same;
                if (same > 1) errors.Add($"profile name '{profile.Name}' is used {same} times");
            }

            if (string.IsNullOrEmpty(profile.SourceRoot)) errors.Add("source root is empty");
            else if (profile.SourceRoot != NormalizePath(profile.SourceRoot))   // IsValidFolder accepts "Assets/X/", but names are cut from the root's length (measured on 6000.6)
                errors.Add($"source root '{profile.SourceRoot}' has a trailing separator or backslashes — write it as '{NormalizePath(profile.SourceRoot)}'");
            else if (!AssetDatabase.IsValidFolder(profile.SourceRoot)) errors.Add($"source root '{profile.SourceRoot}' is not a folder");

            if (string.IsNullOrEmpty(profile.OutputPath)) errors.Add("output path is empty");
            else
            {
                int sameOutput = 0;
                foreach (var p in Profiles) if (p != null && SameFolder(p.OutputPath, profile.OutputPath)) ++sameOutput;
                if (sameOutput > 1)
                    errors.Add($"output path '{profile.OutputPath}' is used by {sameOutput} profiles — when a directory at that path is registered, the editor simulation could not tell which profile's source root to serve it from");
            }

            if (!string.IsNullOrEmpty(profile.SourceRoot) && !string.IsNullOrEmpty(profile.OutputPath))
            {
                if (IsInside(profile.SourceRoot, profile.OutputPath))
                    errors.Add($"output path '{profile.OutputPath}' lies inside the source root '{profile.SourceRoot}' — the next build would scan the built files as content");
                else if (IsInside(profile.OutputPath, profile.SourceRoot))
                    errors.Add($"output path '{profile.OutputPath}' contains the source root '{profile.SourceRoot}' — the build clears the output folder");
            }

            if (profile.Catalogs == null || profile.Catalogs.Length == 0) errors.Add("no category: the profile has nothing to build");
            else
            {
                var subs = new string[profile.Catalogs.Length];
                for (int i = 0; i < subs.Length; ++i) subs[i] = NormalizeSubFolder(profile.Catalogs[i]?.SubFolder);
                for (int i = 0; i < subs.Length; ++i)
                    for (int j = i + 1; j < subs.Length; ++j)
                        if (Covers(subs[i], subs[j]) || Covers(subs[j], subs[i]))
                            errors.Add($"categories '{Show(subs[i])}' and '{Show(subs[j])}' overlap: the same asset would be catalogued twice");

                if (!string.IsNullOrEmpty(profile.SourceRoot))
                    foreach (var entry in profile.Catalogs)
                    {
                        var path = entry?.ResolvePath();
                        if (!string.IsNullOrEmpty(path) && ContentLinkEditorUtility.IsUnder(profile.SourceRoot, path))
                            errors.Add($"catalog '{path}' lies under the source root '{profile.SourceRoot}' and would catalogue itself");
                    }
            }

            foreach (var entry in profile.Catalogs ?? Array.Empty<CatalogEntry>())
            {
                var path = entry?.ResolvePath();
                if (string.IsNullOrEmpty(path)) continue;
                if (IsInside(profile.OutputPath, path))
                    errors.Add($"catalog '{path}' lies inside the output path '{profile.OutputPath}' — the build clears that folder");
                if (AssetDatabase.LoadAssetAtPath<ContentCatalog>(path) != null) continue;   // exists: wherever it is
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                    errors.Add($"catalog '{path}' would be created outside Assets/, where the build cannot create it — point the category at an existing catalog or a path under Assets/");
            }

            if (profile.Target != BuildTarget.NoTarget && profile.Target != EditorUserBuildSettings.activeBuildTarget)
                errors.Add($"profile is for {profile.Target} but the active build target is {EditorUserBuildSettings.activeBuildTarget}");

            if (profile.Archive == ArchiveMode.Packed && ContentArchivePacker.ResolvePartitioner(profile.Partitioner) == null)
                errors.Add($"partitioner '{profile.Partitioner}' was not found — the build would fail");

            return errors;
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it — both taken relative to the project when not absolute, case ignored, like <see cref="SameFolder"/>.</summary>
        public static bool IsInside(string folder, string path)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path)) return false;
            string f, q;
            try { f = System.IO.Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/'); q = System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/'); }
            catch (Exception) { return false; }
            return string.Equals(q, f, StringComparison.OrdinalIgnoreCase) || q.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Two output paths that name the same folder once normalised (relative to the project or absolute, either separator, trailing separator ignored, case ignored).</summary>
        public static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            static string Normalize(string p)
            {
                try { return System.IO.Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/'); }
                catch (Exception) { return p.Replace('\\', '/').TrimEnd('/'); }
            }
        }

        /// <summary>A profile path as it is stored: forward slashes, no trailing separator. The window and the CLI apply it to typed values.</summary>
        public static string NormalizePath(string path) => string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/').TrimEnd('/');

        /// <summary>"" for the whole root; otherwise a clean "A/B" without leading or trailing separators.</summary>
        public static string NormalizeSubFolder(string subFolder)
            => string.IsNullOrEmpty(subFolder) ? "" : subFolder.Replace('\\', '/').Trim().Trim('/');

        static bool Covers(string outer, string inner)
            => outer.Length == 0 || inner == outer || inner.StartsWith(outer + "/", StringComparison.Ordinal);

        static string Show(string sub) => sub.Length == 0 ? "(root)" : sub;
    }
}
