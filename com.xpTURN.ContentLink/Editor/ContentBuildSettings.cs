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
    /// runner. The CLI overrides values for one run, puts them back and never saves.
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
            public ContentNameStyle NameStyle = ContentNameStyle.PathWithoutExtension;   // how names are made from the paths under the source root
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
        [SerializeField] string m_ContentVersion = "";

        /// <summary>Stamped into every packed index this project builds — one value per build, so a base and its locales can be paired. "" = none.</summary>
        public string ContentVersion
        {
            get => m_ContentVersion ?? "";
            set { m_ContentVersion = value ?? ""; MarkDirty(); }
        }

        /// <summary>The CLI's run-level value (in memory, never saved); null = <see cref="ContentVersion"/>.</summary>
        public static string ContentVersionOverride;

        /// <summary>What a build stamps: the CLI's value when it gave one, else the setting.</summary>
        public static string EffectiveContentVersion => ContentVersionOverride ?? instance.ContentVersion;

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
        /// Three of these exist because the name check alone cannot see them: overlapping sub folders put the same
        /// asset in two catalogs, a catalog under its own source root catalogues itself, and two categories on one
        /// catalog leave it with the names of the one written last.
        /// </summary>
        public List<string> Validate(Profile profile) => Validate(profile, null);

        /// <summary>
        /// <see cref="Validate(Profile)"/>, also telling which category rows each finding is about: <paramref name="categoryErrors"/>
        /// gets one (row, finding) pair per row a finding names — a finding about two rows twice, one about the profile itself none.
        /// </summary>
        public List<string> Validate(Profile profile, List<(int Row, string Error)> categoryErrors)
        {
            var errors = new List<string>();
            if (profile == null) { errors.Add("profile is null"); return errors; }

            if (string.IsNullOrEmpty(profile.Name)) errors.Add("profile name is empty");
            else if (!ContentArchives.IsPlainFileName(profile.Name)) errors.Add($"profile name '{profile.Name}' cannot be in a file name on every platform — no / \\ : * ? \" < > | or control character, no trailing dot or space, not a Windows device name such as CON (the name is used in archive names and default paths)");
            else if (string.Equals(profile.Name, ContentBuildCLI.All, StringComparison.OrdinalIgnoreCase)) errors.Add($"profile name '{profile.Name}' is the command line's word for every profile (-contentlink.profile {ContentBuildCLI.All}, in any case) — no command line could build it alone: rename it");
            else if (profile.Archive == ArchiveMode.Packed && !ContentArchives.IsPortableName(profile.Name)) errors.Add($"profile name '{profile.Name}' has a character an archive name cannot carry: a packed profile's name goes into its archive names, its remote folder and its cache folder — letters, digits, '.', '-' and '_' only");
            else
            {
                int same = 0;
                foreach (var p in Profiles) if (p != null && p.Name == profile.Name) ++same;
                if (same > 1) errors.Add($"profile name '{profile.Name}' is used {same} times");
            }

            if (string.IsNullOrEmpty(profile.SourceRoot)) errors.Add("source root is empty");
            else if (profile.SourceRoot != NormalizeRoot(profile.SourceRoot))   // IsValidFolder accepts "Assets/X/" and "Assets//X", but names are cut from the root's length (measured on 6000.6)
                errors.Add($"source root '{profile.SourceRoot}' has a trailing separator, doubled separators or backslashes — write it as '{NormalizeRoot(profile.SourceRoot)}'");
            else if (!AssetDatabase.IsValidFolder(profile.SourceRoot)) errors.Add($"source root '{profile.SourceRoot}' is not a folder");
            else if (ContentLinkEditorUtility.DiskPath(profile.SourceRoot) is string disk && disk != ContentLinkEditorUtility.Composed(profile.SourceRoot))   // found in any case; the name rules compare it as written, composed
                errors.Add($"source root '{profile.SourceRoot}' is spelled '{disk}' in the project — write it as '{disk}', and if the folder was renamed only in case, make version control record the rename: the content tools compare the root as written");

            if (!Enum.IsDefined(typeof(ContentNameStyle), profile.NameStyle)) errors.Add($"name style {(int)profile.NameStyle} is not a ContentNameStyle");
            else if (!string.IsNullOrEmpty(profile.SourceRoot))
                foreach (var p in Profiles)
                    if (p != null && !ReferenceEquals(p, profile) && SameFolder(p.SourceRoot, profile.SourceRoot) && p.NameStyle != profile.NameStyle)
                    {
                        errors.Add($"profile '{p.Name}' uses the same source root '{profile.SourceRoot}' with name style {p.NameStyle} — a root gives an asset one name, so the profiles must agree");
                        break;
                    }

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
                            Category($"categories '{Show(subs[i])}' and '{Show(subs[j])}' overlap: the same asset would be catalogued twice", i, j);

                if (!string.IsNullOrEmpty(profile.SourceRoot))
                    for (int i = 0; i < subs.Length; ++i)
                    {
                        var folder = subs[i].Length == 0 ? null : profile.SourceRoot + "/" + subs[i];
                        if (folder == null || !AssetDatabase.IsValidFolder(folder)) continue;
                        var disk = ContentLinkEditorUtility.DiskPath(folder);
                        var spelled = disk != null ? disk.Substring(ContentLinkEditorUtility.Composed(profile.SourceRoot).Length + 1) : subs[i];
                        if (spelled != ContentLinkEditorUtility.Composed(subs[i]))
                            Category($"category '{subs[i]}' is spelled '{spelled}' in the project — write it as '{spelled}', and if the folder was renamed only in case, make version control record the rename: a build matches the category as written and would leave its assets out", i);
                    }

                if (!string.IsNullOrEmpty(profile.SourceRoot))
                    for (int i = 0; i < profile.Catalogs.Length; ++i)
                    {
                        var path = profile.Catalogs[i]?.ResolvePath();
                        if (!string.IsNullOrEmpty(path) && ContentLinkEditorUtility.IsUnder(profile.SourceRoot, path))
                            Category($"catalog '{path}' lies under the source root '{profile.SourceRoot}' and would catalogue itself", i);
                    }

                var written = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // catalog → the first category that writes it
                var paths = new string[subs.Length];
                for (int i = 0; i < subs.Length; ++i)
                {
                    var path = paths[i] = profile.Catalogs[i]?.ResolvePath();
                    if (string.IsNullOrEmpty(path)) { Category($"category '{Show(subs[i])}' has no catalog — pick one", i); continue; }
                    var key = ContentLinkEditorUtility.Composed(path.Replace('\\', '/'));   // a catalog picked is spelled as the editor spells its path, one typed as typed
                    if (!written.TryGetValue(key, out int first)) written.Add(key, i);
                    else if (ContentLinkEditorUtility.Composed(subs[first]) != ContentLinkEditorUtility.Composed(subs[i]))   // the same sub-folder twice is the overlap above
                        Category($"categories '{Show(subs[first])}' and '{Show(subs[i])}' write the same catalog '{paths[first]}'{(ContentLinkEditorUtility.Composed(paths[first]) == ContentLinkEditorUtility.Composed(path) ? "" : $" (as '{path}')")} — the one written last would replace the other's names: pick another catalog for one of them", first, i);
                }
                foreach (var other in Profiles)
                {
                    if (other == null || ReferenceEquals(other, profile)) continue;
                    foreach (var entry in other.Catalogs ?? Array.Empty<CatalogEntry>())
                    {
                        var path = entry?.ResolvePath();
                        if (string.IsNullOrEmpty(path) || !written.TryGetValue(ContentLinkEditorUtility.Composed(path.Replace('\\', '/')), out int i)) continue;
                        var otherSub = NormalizeSubFolder(entry.SubFolder);
                        if (ContentLinkEditorUtility.Composed(other.SourceRoot) == ContentLinkEditorUtility.Composed(profile.SourceRoot) && ContentLinkEditorUtility.Composed(otherSub) == ContentLinkEditorUtility.Composed(subs[i])) continue;   // the same catalog written the same way: one root has one name style
                        Category($"catalog '{paths[i]}' of category '{Show(subs[i])}' is also written by profile '{other.Name}' — its category '{Show(otherSub)}' of '{other.SourceRoot}': each build or catalog update would replace the other's names; give each profile its own catalog", i);
                    }
                }
            }

            var entries = profile.Catalogs ?? Array.Empty<CatalogEntry>();
            for (int i = 0; i < entries.Length; ++i)
            {
                var path = entries[i]?.ResolvePath();
                if (string.IsNullOrEmpty(path)) continue;
                if (IsInside(profile.OutputPath, path))
                    Category($"catalog '{path}' lies inside the output path '{profile.OutputPath}' — the build clears that folder", i);
                if (AssetDatabase.LoadAssetAtPath<ContentCatalog>(path) != null) continue;   // exists: wherever it is
                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                    Category($"catalog '{path}' would be created outside Assets/, where the build cannot create it — point the category at an existing catalog or a path under Assets/", i);
            }

            if (!Enum.IsDefined(typeof(ArchiveMode), profile.Archive)) errors.Add($"archive mode {(int)profile.Archive} is not an ArchiveMode");
            if (!Enum.IsDefined(typeof(CompressionKind), profile.Compression)) errors.Add($"compression {(int)profile.Compression} is not a CompressionKind");

            if (profile.Target != BuildTarget.NoTarget && profile.Target != EditorUserBuildSettings.activeBuildTarget)
                errors.Add($"profile is for {profile.Target} but the active build target is {EditorUserBuildSettings.activeBuildTarget}");

            if (profile.Archive == ArchiveMode.Packed && ContentArchivePacker.ResolvePartitioner(profile.Partitioner) == null)
                errors.Add($"partitioner '{profile.Partitioner}' was not found — the build would fail");
            foreach (var name in profile.PostProcessors ?? Array.Empty<string>())
                if (!string.IsNullOrEmpty(name) && ContentBuildRunner.ResolvePostProcessor(name) == null) errors.Add($"post-processor '{name}' was not found — the build would fail");

            return errors;

            void Category(string error, int row, int other = -1)
            {
                errors.Add(error);
                if (categoryErrors == null) return;
                categoryErrors.Add((row, error));
                if (other >= 0) categoryErrors.Add((other, error));
            }
        }

        /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it — both taken relative to the project when not absolute, case and Unicode form ignored, like <see cref="SameFolder"/>.</summary>
        public static bool IsInside(string folder, string path)
        {
            if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(path)) return false;
            string f, q;
            try { f = ContentLinkEditorUtility.Composed(System.IO.Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/')); q = ContentLinkEditorUtility.Composed(System.IO.Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/')); }
            catch (Exception) { return false; }
            return string.Equals(q, f, StringComparison.OrdinalIgnoreCase) || q.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Two output paths that name the same folder once normalised (relative to the project or absolute, either separator, trailing separator ignored, case and Unicode form ignored).</summary>
        public static bool SameFolder(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            static string Normalize(string p)
            {
                try { return ContentLinkEditorUtility.Composed(System.IO.Path.GetFullPath(p).Replace('\\', '/').TrimEnd('/')); }   // a project's path may be spelled either way
                catch (Exception) { return ContentLinkEditorUtility.Composed(p.Replace('\\', '/').TrimEnd('/')); }
            }
        }

        /// <summary>A profile path as it is stored: forward slashes, no trailing separator. The window and the CLI apply it to typed values.</summary>
        public static string NormalizePath(string path) => string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/').TrimEnd('/');

        /// <summary>A source root as typed: <see cref="NormalizePath"/> without doubled separators — the engine finds "Assets//X" too, and names are cut from the root's length (measured on 6000.6).</summary>
        public static string NormalizeRoot(string path)
        {
            var root = NormalizePath(path);
            while (root.Contains("//")) root = root.Replace("//", "/");
            return root;
        }

        /// <summary>"" for the whole root; otherwise a clean "A/B" without leading or trailing separators.</summary>
        public static string NormalizeSubFolder(string subFolder)
            => string.IsNullOrEmpty(subFolder) ? "" : subFolder.Replace('\\', '/').Trim().Trim('/');

        static bool Covers(string outer, string inner)
        {
            outer = ContentLinkEditorUtility.Composed(outer); inner = ContentLinkEditorUtility.Composed(inner);   // a category pasted decomposed overlaps the one typed
            return outer.Length == 0 || inner == outer || inner.StartsWith(outer + "/", StringComparison.Ordinal);
        }

        static string Show(string sub) => sub.Length == 0 ? "(root)" : sub;
    }
}
