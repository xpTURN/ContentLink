using System;
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
        public const int IndexVersion = 1;
        public const string MountPrefix = "p";

        [Serializable]
        public sealed class Index
        {
            public int Version = IndexVersion;
            public string Profile = "";
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
            public string[] Files = Array.Empty<string>();   // the aliases inside, in the order they were packed
        }

        /// <summary>The index of a packed directory, or null when there is none or it is not a version we know (logged).</summary>
        public static Index ReadIndex(string directory)
        {
            var path = Path.Combine(directory, IndexFile);
            if (!File.Exists(path)) return null;
            Index index;
            try { index = JsonUtility.FromJson<Index>(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Debug.LogError($"[ContentLink] '{path}' could not be read: {e.Message}");
                return null;
            }
            if (index == null || index.Version != IndexVersion || index.Archives == null)
            {
                Debug.LogError($"[ContentLink] '{path}' is version {index?.Version}; this package knows version {IndexVersion}.");
                return null;
            }
            return index;
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
        /// the ones that did mount are unmounted again and an exception says which archive refused.
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

        public static void Unmount(ArchiveHandle[] handles)
        {
            if (handles == null) return;
            foreach (var h in handles)
            {
                if (h.Equals(default(ArchiveHandle)) || h.Status != ArchiveStatus.Complete) continue;
                try { h.Unmount().Complete(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        /// <summary>Unmounts whatever an earlier session left in our namespaces (the editor with domain reload off keeps them alive, like leftover directories).</summary>
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
                Unmount(mounted);
                count += mounted.Length;
            }
            return count;
        }
    }
}
