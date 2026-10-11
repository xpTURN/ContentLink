using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Profile = xpTURN.ContentLink.Editor.ContentBuildSettings.Profile;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// The build window: a thin IMGUI face over <see cref="ContentBuildWindowModel"/>. Every committed edit goes
    /// through the model, which records it for undo and saves the settings file; the only state kept here is the
    /// text of unfinished inputs. Not compiled in the dotnet verification harness, which has no IMGUI.
    /// </summary>
    public sealed class ContentBuildWindow : EditorWindow
    {
        const string k_Menu = "Window/ContentLink/Build Window";
        const string k_SubFolderControl = "ContentLink.CategorySubFolder.";

        ContentBuildWindowModel m_Model;
        Vector2 m_Scroll;
        string m_NewProfile = "", m_NewCategory = "";
        string m_Notice;
        MessageType m_NoticeType;
        int m_PickedPostProcessor;

        static GUIStyle s_Bad;
        static GUIStyle Bad => s_Bad ??= new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(1f, 0.5f, 0.5f) } };
        static GUIStyle s_Placeholder;
        static GUIStyle Placeholder => s_Placeholder ??= new GUIStyle(EditorStyles.label) { normal = { textColor = Color.gray }, hover = { textColor = Color.gray } };

        [MenuItem(k_Menu)]
        public static void Open() => GetWindow<ContentBuildWindow>("Content Build");

        void OnEnable()
        {
            m_Model ??= new ContentBuildWindowModel();
            Undo.undoRedoPerformed += OnUndoRedo;
        }

        void OnDisable() => Undo.undoRedoPerformed -= OnUndoRedo;

        void OnUndoRedo()
        {
            m_Model?.OnUndoRedo();
            Repaint();
        }

        void OnGUI()
        {
            m_Model ??= new ContentBuildWindowModel();
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);
            DrawProfiles();
            var p = m_Model.Selected;
            if (p != null)
            {
                DrawProfile(p);
                DrawCategories(p);
                DrawArchive(p);
                DrawPostProcessors(p);
                DrawMessages();
                DrawActions();
            }
            DrawLastBuild();
            EditorGUILayout.EndScrollView();
        }

        void Notice(string text, MessageType type = MessageType.Warning)
        {
            m_Notice = text;
            m_NoticeType = type;
        }

        // ───────────── profiles ─────────────

        void DrawProfiles()
        {
            var version = EditorGUILayout.DelayedTextField(
                new GUIContent("Content version", "Stamped into every packed index this project builds — the value a base and its locales are paired by. \"\" = none. -contentlink.contentVersion overrides it for one CLI run."),
                m_Model.Settings.ContentVersion);
            if (version != m_Model.Settings.ContentVersion) m_Model.SetContentVersion(version);
            EditorGUILayout.LabelField("Profiles", EditorStyles.boldLabel);
            var profiles = m_Model.Profiles;
            var names = new string[profiles.Count];
            int selected = 0;
            for (int i = 0; i < names.Length; ++i)
            {
                names[i] = profiles[i].Name + (profiles[i].Name == m_Model.Settings.ActiveName ? "  (active)" : "");
                if (profiles[i].Name == m_Model.SelectedName) selected = i;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                int pick = EditorGUILayout.Popup(selected, names);
                if (pick != selected) m_Model.Select(profiles[pick].Name);
                using (new EditorGUI.DisabledScope(m_Model.SelectedIsActive))
                    if (GUILayout.Button("Set active", GUILayout.Width(80))) m_Model.SetSelectedActive();
                using (new EditorGUI.DisabledScope(profiles.Count <= 1))
                    if (GUILayout.Button("Remove", GUILayout.Width(70))
                        && EditorUtility.DisplayDialog("Remove profile", $"Remove build profile '{m_Model.SelectedName}'?\nBuilt output and catalog assets stay.", "Remove", "Cancel"))
                    {
                        Notice(m_Model.RemoveSelected());
                        GUIUtility.ExitGUI();
                    }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                m_NewProfile = EditorGUILayout.TextField("New profile", m_NewProfile);
                if (GUILayout.Button("Add", GUILayout.Width(50)))
                {
                    Notice(m_Model.AddProfile(m_NewProfile));
                    if (m_Notice == null) m_NewProfile = "";
                    GUI.FocusControl(null);
                }
            }
        }

        void DrawProfile(Profile p)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Profile", EditorStyles.boldLabel);
            var name = EditorGUILayout.DelayedTextField("Name", p.Name);
            if (name != p.Name) Notice(m_Model.Rename(name));
            PathField("Source root", p.SourceRoot, v => m_Model.Edit(x => x.SourceRoot = v, "Source root"));
            var style = (ContentNameStyle)EditorGUILayout.EnumPopup(
                new GUIContent("Name style", "PathWithoutExtension: Icons/sword · PathWithExtension: Icons/sword.png — the names change on the next catalog update"), p.NameStyle);
            if (style != p.NameStyle) m_Model.Edit(x => x.NameStyle = style, "Name style");
            PathField("Output path", p.OutputPath, v => m_Model.Edit(x => x.OutputPath = v, "Output path"));
            var target = (BuildTarget)EditorGUILayout.EnumPopup(new GUIContent("Target", "NoTarget: whatever target is active. Anything else refuses to build on another target."), p.Target);
            if (target != p.Target) m_Model.Edit(x => x.Target = target, "Target");
            EditorGUILayout.LabelField("Active build target", ContentBuildWindowModel.ActiveTarget.ToString());
        }

        static void PathField(string label, string value, Action<string> commit)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var v = EditorGUILayout.DelayedTextField(label, value);
                if (v != value) commit(v);
                if (GUILayout.Button("…", GUILayout.Width(24)))
                {
                    var picked = EditorUtility.OpenFolderPanel(label, value, "");
                    if (!string.IsNullOrEmpty(picked))
                    {
                        var relative = ProjectRelative(picked);
                        if (relative != value) commit(relative);
                    }
                    GUIUtility.ExitGUI();
                }
            }
        }

        /// <summary>Paths inside the project are kept relative, as the settings file expects them; anything else stays absolute.</summary>
        static string ProjectRelative(string absolute)
        {
            var project = ContentLinkEditorUtility.Composed(Path.GetFullPath(Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/') + "/");
            var full = ContentLinkEditorUtility.Composed(Path.GetFullPath(absolute).Replace('\\', '/'));   // the panel and the project may spell a folder in either form
            return full.StartsWith(project, StringComparison.Ordinal) ? full.Substring(project.Length).TrimEnd('/') : full;
        }

        // ───────────── categories ─────────────

        void DrawCategories(Profile p)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Categories  (sub-folder of the source root · \"\" = whole root · build order is by catalog path)", EditorStyles.boldLabel);
            var catalogs = p.Catalogs ?? Array.Empty<ContentBuildSettings.CatalogEntry>();
            var errors = m_Model.CategoryErrors;
            if (catalogs.Length > 0)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.LabelField(EditorGUILayout.GetControlRect(false), "Sub-folder", EditorStyles.miniLabel);
                    EditorGUI.LabelField(EditorGUILayout.GetControlRect(false, GUILayout.Width(200)), "Catalog", EditorStyles.miniLabel);
                    GUILayoutUtility.GetRect(GUIContent.none, GUI.skin.button, GUILayout.Width(22));
                }
            for (int i = 0; i < catalogs.Length; ++i)
            {
                int index = i;
                var entry = catalogs[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    var control = k_SubFolderControl + index;
                    GUI.SetNextControlName(control);
                    var sub = EditorGUILayout.DelayedTextField(entry.SubFolder ?? "");
                    if (string.IsNullOrEmpty(entry.SubFolder) && Event.current.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != control)
                        GUI.Label(GUILayoutUtility.GetLastRect(), "(whole root)", Placeholder);
                    if (sub != entry.SubFolder) m_Model.Edit(x => x.Catalogs[index].SubFolder = ContentBuildSettings.NormalizeSubFolder(sub), "Category sub-folder");
                    var current = entry.Catalog.isSet ? entry.Catalog.asset : null;
                    var picked = (ContentCatalog)EditorGUILayout.ObjectField(current, typeof(ContentCatalog), false, GUILayout.Width(200));
                    if (picked != current) m_Model.Edit(x => x.Catalogs[index].Catalog.asset = picked, "Category catalog");
                    if (GUILayout.Button("−", GUILayout.Width(22)))
                    {
                        m_Model.RemoveCategory(index);
                        GUIUtility.ExitGUI();
                    }
                }
                if (!entry.Catalog.isSet)
                    EditorGUILayout.LabelField(" ", $"created on the first build at {entry.CreateAt}", EditorStyles.miniLabel);
                foreach (var (row, error) in errors)
                    if (row == index) EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                m_NewCategory = EditorGUILayout.TextField("Add category", m_NewCategory);
                if (GUILayout.Button("Add", GUILayout.Width(50)))
                {
                    Notice(m_Model.AddCategory(m_NewCategory));
                    if (m_Notice == null) m_NewCategory = "";
                    GUI.FocusControl(null);
                }
            }
        }

        // ───────────── archive · compression · options ─────────────

        void DrawArchive(Profile p)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Archive & compression", EditorStyles.boldLabel);
            var archive = (ContentBuildSettings.ArchiveMode)EditorGUILayout.EnumPopup(
                new GUIContent("Archive", "None: flat files · Engine: the engine's single archive · Packed: archives of about Max archive MB each"), p.Archive);
            if (archive != p.Archive) m_Model.Edit(x => x.Archive = archive, "Archive mode");
            using (new EditorGUI.DisabledScope(p.Archive != ContentBuildSettings.ArchiveMode.Packed))
            {
                var mb = EditorGUILayout.DelayedIntField(new GUIContent("Max archive MB", "input bytes per archive, before compression; a single larger file gets an archive of its own"), p.MaxArchiveMB);
                if (mb != p.MaxArchiveMB) m_Model.Edit(x => x.MaxArchiveMB = Mathf.Max(1, mb), "Max archive size");
                DrawPartitioner(p);
            }
            using (new EditorGUI.DisabledScope(p.Archive == ContentBuildSettings.ArchiveMode.None))   // compression applies to archives only; a flat build has nothing to compress
            {
                var compression = (ContentBuildSettings.CompressionKind)EditorGUILayout.EnumPopup("Compression", p.Compression);
                if (compression != p.Compression) m_Model.Edit(x => x.Compression = compression, "Compression");
            }
            Toggle(new GUIContent("Clean build cache"), p.CleanBuildCache, v => m_Model.Edit(x => x.CleanBuildCache = v, "Clean build cache"));
            Toggle(new GUIContent("Disable type trees", "smaller output that loads only on this exact engine version; the runner adds SerializeUnityVersion"),
                   p.DisableWriteTypeTree, v => m_Model.Edit(x => x.DisableWriteTypeTree = v, "Type trees"));
            Toggle(new GUIContent("Fail when errors are logged"), p.FailBuildWhenErrorsLogged, v => m_Model.Edit(x => x.FailBuildWhenErrorsLogged = v, "Fail on errors"));
        }

        void DrawPartitioner(Profile p)
        {
            var available = m_Model.AvailablePartitioners;
            var current = m_Model.SelectedPartitioner;
            var names = new string[available.Count + (current == null ? 1 : 0)];
            int index = -1;
            for (int i = 0; i < available.Count; ++i)
            {
                names[i] = available[i].FullName;
                if (available[i] == current) index = i;
            }
            if (current == null) { index = available.Count; names[index] = $"{p.Partitioner}   (not found — the build fails)"; }
            var picked = EditorGUILayout.Popup(new GUIContent("Partitioner", "which files share an archive and what it is called; the hash trie keeps a change local to one archive"), index, names);
            if (picked != index && picked < available.Count) m_Model.SetPartitioner(available[picked]);
        }

        static void Toggle(GUIContent label, bool value, Action<bool> commit)
        {
            var v = EditorGUILayout.Toggle(label, value);
            if (v != value) commit(v);
        }

        // ───────────── post-processors ─────────────

        void DrawPostProcessors(Profile p)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Post-processors  (run in this order after the build)", EditorStyles.boldLabel);
            var list = p.PostProcessors ?? Array.Empty<string>();
            for (int i = 0; i < list.Length; ++i)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool empty = string.IsNullOrEmpty(list[i]);   // skipped by validation and the run alike
                    bool ok = empty || ContentBuildWindowModel.IsResolved(list[i]);
                    EditorGUILayout.LabelField(empty ? "(empty — skipped)" : ok ? list[i] : list[i] + "   (not found — the build fails)", ok ? EditorStyles.label : Bad);
                    using (new EditorGUI.DisabledScope(i == 0))
                        if (GUILayout.Button("▲", GUILayout.Width(22))) { m_Model.MovePostProcessor(i, -1); GUIUtility.ExitGUI(); }
                    using (new EditorGUI.DisabledScope(i == list.Length - 1))
                        if (GUILayout.Button("▼", GUILayout.Width(22))) { m_Model.MovePostProcessor(i, +1); GUIUtility.ExitGUI(); }
                    if (GUILayout.Button("−", GUILayout.Width(22))) { m_Model.RemovePostProcessor(i); GUIUtility.ExitGUI(); }
                }
            }
            var available = m_Model.AvailablePostProcessors;
            if (available.Count == 0)
            {
                EditorGUILayout.LabelField("no IContentBuildPostProcess implementation in the project", EditorStyles.miniLabel);
                return;
            }
            var names = new string[available.Count];
            for (int i = 0; i < names.Length; ++i) names[i] = available[i].FullName;
            using (new EditorGUILayout.HorizontalScope())
            {
                m_PickedPostProcessor = EditorGUILayout.Popup("Add", Mathf.Clamp(m_PickedPostProcessor, 0, names.Length - 1), names);
                if (GUILayout.Button("Add", GUILayout.Width(50))) Notice(m_Model.AddPostProcessor(available[m_PickedPostProcessor]));
            }
        }

        // ───────────── messages · actions · last result ─────────────

        void DrawMessages()
        {
            EditorGUILayout.Space();
            foreach (var e in m_Model.ProfileErrors) EditorGUILayout.HelpBox(e, MessageType.Error);
            int categoryErrors = m_Model.Errors.Count - m_Model.ProfileErrors.Count;
            if (categoryErrors > 0) EditorGUILayout.HelpBox($"{categoryErrors} category error{(categoryErrors == 1 ? "" : "s")} — see the rows above", MessageType.Error);
            foreach (var n in m_Model.Notes) EditorGUILayout.HelpBox(n, MessageType.Info);
            if (!string.IsNullOrEmpty(m_Notice)) EditorGUILayout.HelpBox(m_Notice, m_NoticeType);
        }

        void DrawActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!m_Model.CanBuild))
                {
                    if (GUILayout.Button("Update catalogs"))
                    {
                        bool ok = m_Model.UpdateCatalogs(out var message);
                        Notice(message, ok ? MessageType.Info : MessageType.Error);
                        GUIUtility.ExitGUI();
                    }
                    if (GUILayout.Button($"Build '{m_Model.SelectedName}'"))
                    {
                        Notice(null);
                        m_Model.Build();
                        GUIUtility.ExitGUI();
                    }
                }
                if (GUILayout.Button("Build all"))
                {
                    Notice(null);
                    m_Model.BuildAll();
                    GUIUtility.ExitGUI();
                }
            }
        }

        void DrawLastBuild()
        {
            var last = ContentBuildWindowModel.ReadLastBuild();
            if (!last.Exists) return;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Last build (this session)", EditorStyles.boldLabel);
            var line = $"{(last.Ok ? "✔" : "✘")} '{last.Profile}' → {last.Output}   ·   {last.Files} files   ·   {last.Milliseconds:F0} ms   ·   target {last.Target}   ·   {last.Time}";
            EditorGUILayout.HelpBox(last.Ok ? line : line + "\n" + last.Errors, last.Ok ? MessageType.Info : MessageType.Error);
            using (new EditorGUI.DisabledScope(!Directory.Exists(last.Output)))
                if (GUILayout.Button("Show output", GUILayout.Width(100))) EditorUtility.RevealInFinder(last.Output);
        }
    }
}
