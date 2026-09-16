using System;
using System.Collections.Generic;
using UnityEditor;
using CatalogEntry = xpTURN.ContentLink.Editor.ContentBuildSettings.CatalogEntry;
using Profile = xpTURN.ContentLink.Editor.ContentBuildSettings.Profile;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// What the build window does, without any IMGUI: which profile is shown, every edit as one undoable and
    /// saved step, the messages that block or annotate a build, and the calls into the runner.
    /// The window only draws this; the harness and the EditMode suite drive it directly.
    /// </summary>
    public sealed class ContentBuildWindowModel
    {
        /// <summary>Catalogs the window creates go here — outside every source root, so they never catalogue themselves.</summary>
        public const string CatalogFolder = "Assets/ContentCatalogs";

        string m_Selected;
        int m_MessagesRevision = -1;
        string m_MessagesFor;
        readonly List<string> m_Errors = new();
        readonly List<string> m_Notes = new();
        List<Type> m_Available, m_AvailablePartitioners;

        public ContentBuildSettings Settings => ContentBuildSettings.instance;
        public IReadOnlyList<Profile> Profiles => Settings.Profiles;

        /// <summary>The profile the window shows: the selected one, or the active one once the selection is gone.</summary>
        public Profile Selected
        {
            get
            {
                var p = Settings.Find(m_Selected);
                if (p == null) { p = Settings.Active; m_Selected = p?.Name; }
                return p;
            }
        }
        public string SelectedName => Selected?.Name ?? "";
        public bool SelectedIsActive => Selected != null && Settings.ActiveName == Selected.Name;

        public void Select(string name)
        {
            if (Settings.Find(name) != null) m_Selected = name;
        }

        // ───────────── profiles ─────────────

        /// <summary>Adds and selects a profile with the shipping defaults. Returns the reason when it cannot, null when it did.</summary>
        public string AddProfile(string name)
        {
            name = name?.Trim() ?? "";
            if (name.Length == 0) return "profile name is empty";
            if (Settings.Find(name) != null) return $"profile '{name}' exists already";
            Record("Add build profile");
            m_Selected = Settings.Add(name).Name;
            Commit();
            return null;
        }

        public string RemoveSelected()
        {
            var p = Selected;
            if (p == null) return "nothing selected";
            if (Profiles.Count <= 1) return "the last profile cannot be removed";
            Record("Remove build profile");
            bool wasActive = SelectedIsActive;
            Settings.Remove(p.Name);
            m_Selected = null;
            if (wasActive) Settings.SetActive(Settings.Profiles[0].Name);   // Active falls back to the first anyway; make the file say so
            Commit();
            return null;
        }

        public void SetSelectedActive()
        {
            var p = Selected;
            if (p == null || SelectedIsActive) return;
            Record("Activate build profile");
            Settings.SetActive(p.Name);
            Commit();
        }

        public string Rename(string newName)
        {
            var p = Selected;
            newName = newName?.Trim() ?? "";
            if (p == null) return "nothing selected";
            if (newName == p.Name) return null;
            if (newName.Length == 0) return "profile name is empty";
            if (Settings.Find(newName) != null) return $"profile '{newName}' exists already";
            Record("Rename build profile");
            bool wasActive = SelectedIsActive;
            p.Name = newName;
            m_Selected = newName;
            if (wasActive) Settings.SetActive(newName);   // the active profile is remembered by name
            Commit();
            return null;
        }

        /// <summary>One committed field edit: recorded for undo, applied, marked and saved.</summary>
        public void Edit(Action<Profile> change, string undoName = "Edit build profile")
        {
            var p = Selected;
            if (p == null) return;
            Record(undoName);
            change(p);
            p.SourceRoot = ContentBuildSettings.NormalizePath(p.SourceRoot);   // typed paths: no trailing separator, no backslashes
            p.OutputPath = ContentBuildSettings.NormalizePath(p.OutputPath);
            Commit();
        }

        // ───────────── categories ─────────────

        /// <summary>Where a new category's catalog is created: outside every source root, named after profile and category.</summary>
        public static string DefaultCatalogPath(Profile profile, string subFolder)
        {
            var sub = ContentBuildSettings.NormalizeSubFolder(subFolder);
            var category = sub.Length == 0 ? "" : "." + sub.Replace('/', '.');
            return $"{CatalogFolder}/{profile.Name}{category}.asset";
        }

        /// <summary>Appends a category. The catalog is created on the first build; a path under the source root is refused, since the catalog would then catalogue itself.</summary>
        public string AddCategory(string subFolder, string createAt = null)
        {
            var p = Selected;
            if (p == null) return "nothing selected";
            var sub = ContentBuildSettings.NormalizeSubFolder(subFolder);
            var catalogs = p.Catalogs ?? Array.Empty<CatalogEntry>();
            foreach (var c in catalogs)
                if (ContentBuildSettings.NormalizeSubFolder(c?.SubFolder) == sub)
                    return $"category '{(sub.Length == 0 ? "(whole root)" : sub)}' exists already";
            var path = string.IsNullOrEmpty(createAt) ? DefaultCatalogPath(p, sub) : createAt.Replace('\\', '/');
            if (!string.IsNullOrEmpty(p.SourceRoot) && ContentLinkEditorUtility.IsUnder(p.SourceRoot, path))
                return $"catalog '{path}' would lie under the source root '{p.SourceRoot}' and catalogue itself";
            Record("Add category");
            var list = new List<CatalogEntry>(catalogs) { new CatalogEntry { SubFolder = sub, CreateAt = path } };
            p.Catalogs = list.ToArray();
            Commit();
            return null;
        }

        public void RemoveCategory(int index)
        {
            var p = Selected;
            if (p?.Catalogs == null || (uint)index >= (uint)p.Catalogs.Length) return;
            Record("Remove category");
            var list = new List<CatalogEntry>(p.Catalogs);
            list.RemoveAt(index);
            p.Catalogs = list.ToArray();
            Commit();
        }

        // ───────────── post-processors ─────────────

        /// <summary>Concrete implementations, found by TypeCache and sorted by name. Discovery only — what runs, and in which order, is the profile's list.</summary>
        public IReadOnlyList<Type> AvailablePostProcessors => m_Available ??= ContentBuildRunner.Implementations<IContentBuildPostProcess>();

        // ───────────── partitioner ─────────────

        /// <summary>Concrete IContentArchivePartitioner implementations — the two built-in ones and the project's — sorted by name.</summary>
        public IReadOnlyList<Type> AvailablePartitioners => m_AvailablePartitioners ??= ContentBuildRunner.Implementations<IContentArchivePartitioner>();

        /// <summary>The selected profile's partitioner type, or null when its name resolves to nothing.</summary>
        public Type SelectedPartitioner => Selected == null ? null : ContentArchivePacker.ResolvePartitioner(Selected.Partitioner);

        /// <summary>Stores the type's name on the selected profile (the built-in trie is stored by name too; "" is only what older settings hold).</summary>
        public void SetPartitioner(Type type)
        {
            var p = Selected;
            if (p == null || type == null || SelectedPartitioner == type) return;
            Edit(x => x.Partitioner = NameOf(type), "Partitioner");
        }

        /// <summary>The name the profile stores: full name plus assembly, no version — Type.GetType resolves it and an assembly version bump does not break it.</summary>
        public static string NameOf(Type type) => $"{type.FullName}, {type.Assembly.GetName().Name}";

        public static bool IsResolved(string name) => ContentBuildRunner.ResolvePostProcessor(name) != null;

        public string AddPostProcessor(Type type)
        {
            var p = Selected;
            if (p == null || type == null) return "nothing selected";
            var list = new List<string>(p.PostProcessors ?? Array.Empty<string>());
            foreach (var existing in list)
                if (ContentBuildRunner.ResolvePostProcessor(existing) == type) return $"'{type.FullName}' is listed already";
            Record("Add post-processor");
            list.Add(NameOf(type));
            p.PostProcessors = list.ToArray();
            Commit();
            return null;
        }

        public void RemovePostProcessor(int index)
        {
            var p = Selected;
            if (p?.PostProcessors == null || (uint)index >= (uint)p.PostProcessors.Length) return;
            Record("Remove post-processor");
            var list = new List<string>(p.PostProcessors);
            list.RemoveAt(index);
            p.PostProcessors = list.ToArray();
            Commit();
        }

        /// <summary>The one list the window may reorder — the order is what runs.</summary>
        public void MovePostProcessor(int index, int delta)
        {
            var list = Selected?.PostProcessors;
            int to = index + delta;
            if (list == null || (uint)index >= (uint)list.Length || (uint)to >= (uint)list.Length) return;
            Record("Reorder post-processors");
            (list[index], list[to]) = (list[to], list[index]);
            Commit();
        }

        // ───────────── messages ─────────────

        /// <summary>What stops the build: the validator's findings plus post-processors that would not be found.</summary>
        public IReadOnlyList<string> Errors { get { Refresh(); return m_Errors; } }

        /// <summary>What the user should know but may keep: an output inside Assets/, live static overrides, ignored compression.</summary>
        public IReadOnlyList<string> Notes { get { Refresh(); return m_Notes; } }

        public bool CanBuild => Errors.Count == 0;

        /// <summary>The target a build made now is for — always shown.</summary>
        public static BuildTarget ActiveTarget => EditorUserBuildSettings.activeBuildTarget;

        void Refresh()
        {
            var p = Selected;
            if (m_MessagesRevision == ContentBuildSettings.Revision && m_MessagesFor == p?.Name) return;
            m_MessagesRevision = ContentBuildSettings.Revision;
            m_MessagesFor = p?.Name;
            m_Errors.Clear();
            m_Notes.Clear();
            if (p == null) { m_Errors.Add("no profile"); return; }

            m_Errors.AddRange(Settings.Validate(p));
            foreach (var name in p.PostProcessors ?? Array.Empty<string>())
                if (!IsResolved(name)) m_Errors.Add($"post-processor '{name}' was not found — the build would fail");

            if (p.Archive == ContentBuildSettings.ArchiveMode.None && p.Compression != ContentBuildSettings.CompressionKind.Uncompressed)
                m_Notes.Add("compression belongs to archives: a flat build is written uncompressed");
            var output = (p.OutputPath ?? "").Replace('\\', '/').TrimStart('/');
            if (output == "Assets" || output.StartsWith("Assets/", StringComparison.Ordinal))
                m_Notes.Add($"output '{p.OutputPath}' is inside Assets/: every built file is imported and gets a .meta");
            if (ContentDirectoryBuilder.HasOverrides)
                m_Notes.Add("session overrides on ContentDirectoryBuilder.SourceRoot/CatalogPath/OutputPath are set and hide the active profile from the static API until ClearOverrides()");
        }

        // ───────────── actions ─────────────

        public ContentBuildResult LastResult { get; private set; }

        /// <summary>Builds the selected profile through the runner. A first build sets catalog references — the window saves those, the CLI never does.</summary>
        public ContentBuildResult Build(bool showProgress = true)
        {
            var p = Selected;
            if (p == null) return null;
            int revision = ContentBuildSettings.Revision;
            LastResult = ContentBuildRunner.Build(p, showProgress);
            if (ContentBuildSettings.Revision != revision) Settings.SaveSettings();
            return LastResult;
        }

        public List<ContentBuildResult> BuildAll(bool showProgress = true)
        {
            int revision = ContentBuildSettings.Revision;
            var results = ContentBuildRunner.BuildAll(showProgress);
            if (results.Count > 0) LastResult = results[results.Count - 1];
            if (ContentBuildSettings.Revision != revision) Settings.SaveSettings();
            return results;
        }

        public bool UpdateCatalogs(out string message)
        {
            var p = Selected;
            var errors = new List<string>();
            var paths = new List<string>();
            int revision = ContentBuildSettings.Revision;
            bool ok = p != null && ContentBuildRunner.UpdateCatalogs(p, errors, paths);
            if (ContentBuildSettings.Revision != revision) Settings.SaveSettings();
            message = ok ? $"updated {paths.Count} catalog(s) of '{p.Name}'" : (p == null ? "nothing selected" : string.Join("\n", errors));
            return ok;
        }

        /// <summary>The last build of this editor session as the runner recorded it — SessionState, never the settings file.</summary>
        public struct LastBuild
        {
            public bool Exists, Ok;
            public string Profile, Output, Target, Errors, Time;
            public int Files;
            public float Milliseconds;
        }

        public static LastBuild ReadLastBuild()
        {
            var k = ContentBuildRunner.SessionKey;
            var profile = SessionState.GetString(k + ".profile", "");
            return new LastBuild
            {
                Exists = profile.Length > 0,
                Profile = profile,
                Output = SessionState.GetString(k + ".output", ""),
                Target = SessionState.GetString(k + ".target", ""),
                Ok = SessionState.GetBool(k + ".ok", false),
                Files = SessionState.GetInt(k + ".files", 0),
                Milliseconds = SessionState.GetFloat(k + ".ms", 0f),
                Errors = SessionState.GetString(k + ".errors", ""),
                Time = SessionState.GetString(k + ".time", ""),
            };
        }

        /// <summary>After an undo or redo the object changed behind our back: mark and save so the file follows (the window wires this to Undo.undoRedoPerformed).</summary>
        public void OnUndoRedo() => Commit();

        void Record(string name) => Undo.RecordObject(Settings, name);

        void Commit()
        {
            Settings.MarkDirty();
            Settings.SaveSettings();
        }
    }
}
