using System;
using System.Collections.Generic;
using System.IO;
using Unity.Content;
using Unity.IO.Archive;
using UnityEngine;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// The packed layout the editor's packer writes: a flat build's files inside a few archives of
    /// roughly the profile's size limit each, listed by archives.json. Every archive is mounted under one namespace
    /// and one prefix, which yields one virtual folder — and that folder registers exactly like a flat directory
    /// The manifest travels inside an archive of its own (the packer's built-in rules put it first); which archive
    /// holds which file is the packer's business — this side mounts whatever the index lists. BuildManifestHash.txt
    /// stays loose too, for whoever looks at the folder.
    /// </summary>
    public static class ContentArchives
    {
        public const string IndexFile = "archives.json";
        public const string HashFile = "archives.hash";   // the MD5 of archives.json's bytes on one line — what a remote check compares first
        public const int IndexVersion = 2;
        public const string MountPrefix = "p";
        public const string ArchiveExtension = ".archive";

        [Serializable]
        public sealed class Index
        {
            public int Version = IndexVersion;
            public string Profile = "";
            public string Target = "";           // the build target it was built for, e.g. "StandaloneOSX"
            public string UnityVersion = "";     // the editor that built it, e.g. "6000.6.0f1"
            public bool TypeTrees = true;        // false = built with DisableWriteTypeTree: loads only on the same engine and catalog layout
            public int CatalogLayout;            // ContentCatalog.Layout of the package that built it
            public string ContentVersion = "";   // the build's content version; "" when none was set
            public string Compression = "";
            public int MaxArchiveMB;
            public Archive[] Archives = Array.Empty<Archive>();
        }

        [Serializable]
        public sealed class Archive
        {
            public string File = "";
            public long Bytes;        // the archive on disk
            public long InputBytes;   // the files inside, before compression — what the size limit was applied to
            public string Crc = "";   // what ArchiveAndCompress returned for the content — not the CRC32 of the file on disk (measured)
            public string Md5 = "";   // the MD5 of the file's bytes, 32 lowercase hex digits — what a downloaded or copied file is checked against
            public string[] Files = Array.Empty<string>();   // the aliases inside, in the order they were packed
        }

        /// <summary>The index of a packed directory, or null when there is none or <see cref="ParseIndex(string, string)"/> refuses it (logged).</summary>
        public static Index ReadIndex(string directory)
        {
            var path = Path.Combine(directory, IndexFile);
            if (!File.Exists(path)) return null;
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception e)
            {
                Debug.LogError($"[ContentLink] '{path}' could not be read: {e.Message}");
                return null;
            }
            return ParseIndex(text, path);
        }

        /// <summary>
        /// An index from its text, or null (logged, naming <paramref name="source"/>) when it does not parse, is not version
        /// <see cref="IndexVersion"/>, an archive lacks its MD5, or an archive name is not <see cref="IsArchiveName"/> or comes
        /// twice (ignoring case). The one reader for a folder, a download and a built-in copy.
        /// </summary>
        public static Index ParseIndex(string json, string source) => ParseIndex(json, source, out _);

        /// <summary>The same, and why an index was refused (null when it was not) — what a remote check reports.</summary>
        internal static Index ParseIndex(string json, string source, out string why)
        {
            why = null;
            Index index;
            try { index = JsonUtility.FromJson<Index>(json); }
            catch (Exception e)
            {
                why = $"could not be read: {e.Message}";
                Debug.LogError($"[ContentLink] '{source}' {why}");
                return null;
            }
            if (index == null || index.Archives == null)
            {
                why = "could not be read: not an index.";
                Debug.LogError($"[ContentLink] '{source}' {why}");
                return null;
            }
            if (index.Version != IndexVersion)
            {
                why = $"is version {index.Version}; this package reads version {IndexVersion} — rebuild the directory with this package.";
                Debug.LogError($"[ContentLink] '{source}' {why}");
                return null;
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in index.Archives)
            {
                if (a == null || !IsMd5(a.Md5)) why = $"archive '{a?.File}' has no MD5 — rebuild the directory with this package.";
                else if (!IsArchiveName(a.File)) why = $"'{a.File}' is not an archive name — letters, digits, '.', '-' and '_', ending with {ArchiveExtension}, no Windows device name: an index names files of its own folder only. Rename the profile (or what its partitioner names) and rebuild.";
                else if (!names.Add(a.File)) why = $"archive '{a.File}' is listed twice (names are compared ignoring case).";
                if (why == null) continue;
                Debug.LogError($"[ContentLink] '{source}': {why}");
                return null;
            }
            return index;
        }

        internal static bool IsMd5(string s)
        {
            if (s == null || s.Length != 32) return false;
            foreach (char c in s) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        static readonly char[] k_NotInFileName = NotInFileName();   // fixed: Path.GetInvalidFileNameChars() depends on the platform
        static char[] NotInFileName() { var l = new List<char> { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }; for (int c = 0; c < 32; ++c) l.Add((char)c); return l.ToArray(); }

        /// <summary>
        /// A file or folder name in one folder on every platform: not empty, none of / \ : * ? " &lt; &gt; | or a control character, no trailing
        /// dot or space (Windows drops them), not a Windows device name (CON, PRN, AUX, NUL, COM0 ~ 9, LPT0 ~ 9, with any extension).
        /// </summary>
        public static bool IsPlainFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(k_NotInFileName) >= 0) return false;
            char last = name[name.Length - 1];
            return last != '.' && last != ' ' && !IsDeviceName(name);
        }

        static bool IsDeviceName(string name)
        {
            int n = name.IndexOf('.'); if (n < 0) n = name.Length;
            while (n > 0 && name[n - 1] == ' ') --n;
            if (n == 3) return Is(name, "CON") || Is(name, "PRN") || Is(name, "AUX") || Is(name, "NUL");
            return n == 4 && (Is(name, "COM") || Is(name, "LPT")) && name[3] >= '0' && name[3] <= '9';
            static bool Is(string s, string d) => string.Compare(s, 0, d, 0, d.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>A plain file name of ASCII letters, digits, '.', '-' and '_' only — what a URL path and a CDN key carry as is.</summary>
        public static bool IsPortableName(string name)
        {
            if (!IsPlainFileName(name)) return false;
            foreach (char c in name)
                if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_')) return false;
            return true;
        }

        /// <summary>What an index may list: a portable name ending with .archive.</summary>
        public static bool IsArchiveName(string name) => IsPortableName(name) && name.EndsWith(ArchiveExtension, StringComparison.Ordinal);

        // ───────────── compatibility ─────────────

        /// <summary>Set by the editor: the active build target, which is what the editor loads built content for. Null in a player.</summary>
        public static Func<string> EditorTarget;

        static bool s_WarnedPlatform;

        /// <summary>The build targets a player on <paramref name="platform"/> loads, or null when the platform is not in the table (not checked).</summary>
        public static string[] TargetsOf(RuntimePlatform platform)
        {
            switch (platform)
            {
                case RuntimePlatform.OSXPlayer: return new[] { "StandaloneOSX" };
                case RuntimePlatform.WindowsPlayer: return new[] { "StandaloneWindows", "StandaloneWindows64" };
                case RuntimePlatform.LinuxPlayer: return new[] { "StandaloneLinux64" };
                case RuntimePlatform.IPhonePlayer: return new[] { "iOS" };
                case RuntimePlatform.Android: return new[] { "Android" };
                default: return null;
            }
        }

        /// <summary>
        /// Whether this player can load what <paramref name="index"/> describes, before anything is mounted: the platform always;
        /// without type trees the exact engine and catalog layout (another layout crashes the player); with type trees
        /// nothing built by a newer engine than this one. <paramref name="reason"/> says why not.
        /// </summary>
        public static bool CheckCompatible(Index index, out string reason)
        {
            reason = null;
            var editor = EditorTarget?.Invoke();
            if (editor != null)
            {
                if (!string.Equals(index.Target, editor, StringComparison.Ordinal))
                {
                    reason = $"it was built for {index.Target}, but the editor's build target is {editor}";
                    return false;
                }
            }
            else
            {
                var targets = TargetsOf(Application.platform);
                if (targets == null)
                {
                    if (!s_WarnedPlatform) { s_WarnedPlatform = true; Debug.LogWarning($"[ContentLink] {Application.platform} is not in the platform table: built content is not checked against it."); }
                }
                else if (Array.IndexOf(targets, index.Target) < 0)
                {
                    reason = $"it was built for {index.Target}, but this player runs on {Application.platform}";
                    return false;
                }
            }
            int order = CompareUnityVersions(index.UnityVersion, Application.unityVersion, out bool parsed);
            if (!parsed)
            {
                reason = $"its engine version '{index.UnityVersion}' cannot be read";
                return false;
            }
            if (!index.TypeTrees)
            {
                if (order != 0)
                {
                    reason = $"it was built without type trees by {index.UnityVersion} and loads only on that engine (this is {Application.unityVersion})";
                    return false;
                }
                if (index.CatalogLayout != ContentCatalog.Layout)
                {
                    reason = $"it was built without type trees with catalog layout {index.CatalogLayout}; this package reads layout {ContentCatalog.Layout}";
                    return false;
                }
            }
            else if (order > 0)
            {
                reason = $"it was built by a newer engine ({index.UnityVersion}) than this one ({Application.unityVersion})";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Orders two engine versions of the form "6000.6.0f1" — three numbers, a release letter (a, b, f, p in that order)
        /// and its number; anything after is ignored. <paramref name="parsed"/> is false when either cannot be read.
        /// </summary>
        public static int CompareUnityVersions(string a, string b, out bool parsed)
        {
            parsed = TryParseVersion(a, out var x) & TryParseVersion(b, out var y);
            if (!parsed) return 0;
            for (int i = 0; i < 5; ++i)
                if (x[i] != y[i]) return x[i] < y[i] ? -1 : 1;
            return 0;
        }

        static bool TryParseVersion(string s, out int[] v)
        {
            v = new int[5];
            if (string.IsNullOrEmpty(s)) return false;
            int i = 0;
            for (int part = 0; part < 3; ++part)
            {
                int start = i;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') ++i;
                if (i == start || !int.TryParse(s.Substring(start, i - start), out v[part])) return false;
                if (part < 2) { if (i >= s.Length || s[i] != '.') return false; ++i; }
            }
            if (i >= s.Length) return false;
            int letter = "abfp".IndexOf(s[i]);
            if (letter < 0) return false;
            v[3] = letter;
            ++i;
            int numStart = i;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') ++i;
            return i > numStart && int.TryParse(s.Substring(numStart, i - numStart), out v[4]);
        }

        /// <summary>
        /// The namespace a directory's archives mount into: a name the engine accepts (alphanumeric, 16 characters)
        /// that is stable for the path, so a leftover from a previous session can be found and released again.
        /// </summary>
        public static string NamespaceFor(string directory)
        {
            ulong hash = 14695981039346656037UL;                         // FNV-1a
            foreach (char c in directory.Replace('\\', '/'))
                hash = (hash ^ c) * 1099511628211UL;
            return "cl" + hash.ToString("x16").Substring(0, 14);
        }

        /// <summary>Exactly the shape NamespaceFor makes — 'cl' and 14 lowercase hex digits — so another system's 16-character 'cl…' name is left alone.</summary>
        public static bool IsOurNamespace(string name)
        {
            if (name == null || name.Length != 16 || !name.StartsWith("cl", StringComparison.Ordinal)) return false;
            for (int i = 2; i < 16; ++i)
            {
                char c = name[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        /// <summary>
        /// Mounts every archive of the index under one namespace and prefix and waits for all of them. On any failure
        /// every archive it started is unmounted again — in progress or failed too — and an exception says which archive
        /// refused.
        /// </summary>
        public static async Awaitable<ArchiveHandle[]> MountAsync(string directory, Index index, int maxFrames = 600)
        {
            var space = ContentNamespace.GetOrCreateNamespace(NamespaceFor(directory));
            var handles = new ArchiveHandle[index.Archives.Length];
            try
            {
                for (int i = 0; i < handles.Length; ++i)
                    handles[i] = ArchiveFileInterface.MountAsync(space, Path.Combine(directory, index.Archives[i].File), MountPrefix);
            }
            catch
            {
                Unmount(handles);
                throw;
            }

            for (int i = 0; i < handles.Length; ++i)
            {
                for (int frame = 0; frame < maxFrames && handles[i].Status == ArchiveStatus.InProgress; ++frame)
                    await Awaitable.NextFrameAsync();
                if (handles[i].Status == ArchiveStatus.Complete) continue;

                var status = handles[i].Status;
                Unmount(handles);
                throw new InvalidOperationException($"[ContentLink] archive '{index.Archives[i].File}' of '{directory}' did not mount ({status}).");
            }
            return handles;
        }

        /// <summary>Unmounts every handle of the array, whatever its state; one already unmounted is logged and skipped.</summary>
        public static void Unmount(ArchiveHandle[] handles) => UnmountCounted(handles);

        static int UnmountCounted(ArchiveHandle[] handles)
        {
            if (handles == null) return 0;
            int count = 0;
            foreach (var h in handles)
            {
                if (h.Equals(default(ArchiveHandle))) continue;   // in progress and failed too: the engine unmounts both (measured)
                try { h.Unmount().Complete(); ++count; }
                catch (Exception e) { Debug.LogException(e); }
            }
            return count;
        }

        /// <summary>Unmounts whatever an earlier session left in our namespaces (the editor with domain reload off keeps them alive, like leftover directories), a failed mount too. Meant for the start of a session: it unmounts every mount of ours, a registration still mounting included.</summary>
        public static int UnmountLeftovers()
        {
            int count = 0;
            ContentNamespace[] spaces;
            try { spaces = ContentNamespace.GetAll(); }
            catch (Exception e) { Debug.LogException(e); return 0; }
            foreach (var space in spaces)
            {
                if (!space.IsValid || !IsOurNamespace(space.GetName())) continue;
                ArchiveHandle[] mounted;
                try { mounted = ArchiveFileInterface.GetMountedArchives(space); }
                catch (Exception e) { Debug.LogException(e); continue; }
                if (mounted == null) continue;
                count += UnmountCounted(mounted);
            }
            return count;
        }
    }
}
