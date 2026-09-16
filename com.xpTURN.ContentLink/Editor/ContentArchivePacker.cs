using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor.Build.Content;
using UnityEngine;
using Profile = xpTURN.ContentLink.Editor.ContentBuildSettings.Profile;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Turns a flat build into archives. The files are never cut — each goes whole into one archive — so the
    /// manifest and its dependency table stay right, and the runtime mounts the archives back into one folder
    /// (ContentArchives). Which file goes where, and what the archives are called, is the profile's
    /// <see cref="IContentArchivePartitioner"/> (the hash-prefix trie by default); the packer checks the answer
    /// against the rules the runtime and the cleanup depend on, and checks it is a pure function of the input by
    /// asking twice with the files in a different order. The engine's own output bytes were measured to be
    /// reproducible, so an archive whose file list did not change is byte-identical to the previous build's.
    /// </summary>
    public static class ContentArchivePacker
    {
        /// <summary>Tests only: an exact byte limit instead of the profile's MaxArchiveMB, so a small fixture still splits.</summary>
        public static long? TestLimitBytes;

        public const string ManifestLabel = "manifest";
        public const string AllLabel = "all";
        public const string BigPrefix = "big-";

        // ───────────── built-in partitioners ─────────────

        /// <summary>
        /// The default: the manifest and its hash file in an archive of their own; a file larger than the limit in an
        /// archive of its own; the rest split by the bits of MD5(key) — a bucket that fits the limit is a leaf,
        /// one that does not is split on the next bit. The key of a serialized file is its manifest ID, which the
        /// engine derives from the asset's identity and keeps when the content changes (measured), so an edited
        /// asset stays in its leaf; a .resS/.resource has no ID and is keyed by its name. A change to one file thus
        /// touches that file's leaf (and, when the leaf splits or merges, its neighbour); every other archive keeps
        /// its name and its bytes. The name is the bit path plus a hash of the file list, so a name never means two
        /// different contents. Without a parsed manifest every file is keyed by name — still deterministic, but an
        /// edited .cf then moves to another leaf.
        /// </summary>
        public sealed class HashTriePartitioner : IContentArchivePartitioner
        {
            public IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput input)
            {
                var groups = new List<ArchiveGroup>();
                var manifest = ManifestGroup(input);
                if (manifest != null) groups.Add(manifest);

                var rest = new List<string>();
                var big = new List<string>();
                foreach (var f in ContentFiles(input))
                    if (input.SizeOf(f) > input.LimitBytes) big.Add(f); else rest.Add(f);
                rest.Sort(StringComparer.Ordinal);

                var leaves = new List<(string label, List<string> files)>();
                var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                var identity = IdentityKeys(input);
                foreach (var f in rest) keys[f] = ArchiveNaming.Md5(identity.TryGetValue(f, out var id) ? id : f);
                Node("", rest, 0, input, keys, leaves);
                leaves.Sort((a, b) => string.CompareOrdinal(a.label, b.label));
                foreach (var (label, files) in leaves)
                    groups.Add(new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, label.Length == 0 ? AllLabel : label, files), files));

                foreach (var group in BigGroups(input, big)) groups.Add(group);
                return groups;
            }

            /// <summary>Serialized file on disk → its manifest ID, for every file the manifest names by ContentHash.</summary>
            public static Dictionary<string, string> IdentityKeys(ArchivePartitionInput input)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (input.Manifest?.SerializedFiles == null) return map;
                foreach (var sf in input.Manifest.SerializedFiles)
                {
                    var file = ContentBuildManifest.FileOf(sf);
                    if (file != null && !string.IsNullOrEmpty(sf.ID)) map[file] = sf.ID;
                }
                return map;
            }

            static void Node(string prefix, List<string> files, int depth, ArchivePartitionInput input, Dictionary<string, byte[]> keys, List<(string, List<string>)> leaves)
            {
                if (files.Count == 0) return;
                long total = 0;
                foreach (var f in files) total += input.SizeOf(f);
                if (total <= input.LimitBytes || files.Count == 1 || depth == 128) { leaves.Add((prefix, files)); return; }
                var zero = new List<string>(); var one = new List<string>();
                foreach (var f in files)
                    if ((keys[f][depth >> 3] & (0x80 >> (depth & 7))) == 0) zero.Add(f); else one.Add(f);
                Node(prefix + "0", zero, depth + 1, input, keys, leaves);
                Node(prefix + "1", one, depth + 1, input, keys, leaves);
            }
        }

        /// <summary>
        /// The previous rule, kept as a choice: files sorted by name and filled front to back, an archive closed when
        /// the next file would push it past the limit — fewer, fuller archives, but an inserted or removed file
        /// shifts every archive after it. Names carry the list hash, so an archive whose content did not shift keeps its name.
        /// </summary>
        public sealed class SequentialFillPartitioner : IContentArchivePartitioner
        {
            public IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput input)
            {
                var groups = new List<ArchiveGroup>();
                var manifest = ManifestGroup(input);
                if (manifest != null) groups.Add(manifest);

                var rest = new List<string>();
                var big = new List<string>();
                foreach (var f in ContentFiles(input))
                    if (input.SizeOf(f) > input.LimitBytes) big.Add(f); else rest.Add(f);
                rest.Sort(StringComparer.Ordinal);

                var current = new List<string>();
                long currentBytes = 0;
                foreach (var f in rest)
                {
                    long size = input.SizeOf(f);
                    if (current.Count > 0 && currentBytes + size > input.LimitBytes)
                    {
                        groups.Add(new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, "seq", current), current));
                        current = new List<string>();
                        currentBytes = 0;
                    }
                    current.Add(f);
                    currentBytes += size;
                }
                if (current.Count > 0) groups.Add(new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, "seq", current), current));

                foreach (var group in BigGroups(input, big)) groups.Add(group);
                return groups;
            }
        }

        static ArchiveGroup ManifestGroup(ArchivePartitionInput input)
        {
            if (input.ManifestFiles == null || input.ManifestFiles.Count == 0) return null;
            var files = new List<string>(input.ManifestFiles);
            files.Sort(StringComparer.Ordinal);
            return new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, ManifestLabel, files), files);
        }

        static IEnumerable<string> ContentFiles(ArchivePartitionInput input)
        {
            var manifest = new HashSet<string>(input.ManifestFiles ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var f in input.Files) if (!manifest.Contains(f)) yield return f;
        }

        /// <summary>One archive per oversized file, labelled by the first eight characters of the alias (a longer stem when two share them).</summary>
        static IEnumerable<ArchiveGroup> BigGroups(ArchivePartitionInput input, List<string> big)
        {
            big.Sort(StringComparer.Ordinal);
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in big)
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                var label = stem.Length > 8 ? stem.Substring(0, 8) : stem;
                labels[f] = label;
                used[label] = used.TryGetValue(label, out var n) ? n + 1 : 1;
            }
            foreach (var f in big)
            {
                var label = used[labels[f]] > 1 ? f.Replace('.', '_') : labels[f];
                var files = new List<string> { f };
                yield return new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, BigPrefix + label, files), files);
            }
        }

        // ───────────── resolution ─────────────

        public const string TrieAlias = "trie";
        public const string SequentialAlias = "sequential";

        /// <summary>The stored type name for a CLI alias, or the value itself when it is not an alias.</summary>
        public static string ExpandAlias(string value)
        {
            if (string.Equals(value, TrieAlias, StringComparison.OrdinalIgnoreCase)) return ContentBuildWindowModel.NameOf(typeof(HashTriePartitioner));
            if (string.Equals(value, SequentialAlias, StringComparison.OrdinalIgnoreCase)) return ContentBuildWindowModel.NameOf(typeof(SequentialFillPartitioner));
            return value;
        }

        /// <summary>The profile's partitioner type: the hash trie when the profile names none, else the resolved type or null (not found).</summary>
        public static Type ResolvePartitioner(string name)
            => string.IsNullOrEmpty(name) ? typeof(HashTriePartitioner) : ContentBuildRunner.ResolveType<IContentArchivePartitioner>(name);

        // ───────────── packing ─────────────

        /// <summary>
        /// Packs the result's flat files in place: writes the archives and archives.json, removes the loose files
        /// (BuildManifestHash.txt stays, as the engine's own archive mode leaves it), and updates the result.
        /// </summary>
        public static bool Pack(ContentBuildResult result, Profile profile, List<string> errors)
        {
            var output = result.OutputPath;
            if (result.Files.Length == 0) { errors.Add("nothing to pack: the build produced no files"); return false; }

            var type = ResolvePartitioner(profile.Partitioner);
            if (type == null) { errors.Add($"partitioner '{profile.Partitioner}' was not found"); return false; }
            IContentArchivePartitioner partitioner;
            try { partitioner = Activator.CreateInstance(type) as IContentArchivePartitioner; }
            catch (Exception e) { errors.Add($"partitioner '{type.FullName}' could not be created: {e.Message}"); return false; }
            if (partitioner == null) { errors.Add($"partitioner '{type.FullName}' does not implement IContentArchivePartitioner"); return false; }

            if (!BuildInput(result, profile, errors, out var input)) return false;

            IReadOnlyList<ArchiveGroup> groups, again;
            try
            {
                groups = partitioner.Partition(input);
                again = partitioner.Partition(Permuted(input));
            }
            catch (Exception e) { errors.Add($"partitioner '{type.FullName}' threw {e.GetType().Name}: {e.Message}"); return false; }
            if (!ArchivePartitionValidator.Same(groups, again))
            {
                errors.Add($"partitioner '{type.FullName}' is not a pure function of its input: the archives changed when the files were listed in a different order");
                return false;
            }
            var warnings = new List<string>();
            if (!ArchivePartitionValidator.Validate(input, groups, errors, warnings)) return false;
            if (partitioner is HashTriePartitioner)
            {
                if (input.Manifest == null)
                    warnings.Add("the build manifest could not be read: files are keyed by name, so an edited asset will move to another archive on the next build");
                else if (HashTriePartitioner.IdentityKeys(input).Count == 0 && HasSerializedFiles(input))
                    warnings.Add("the build manifest names no file by ContentHash (an older manifest shape?): files are keyed by name, so an edited asset will move to another archive on the next build");
            }
            foreach (var w in warnings) Debug.LogWarning($"[ContentLink] {profile.Name}: {w}");

            var compression = ContentBuildRunner.Compression(profile.Compression);
            var index = new ContentArchives.Index
            {
                Profile = profile.Name,
                Compression = profile.Compression.ToString(),
                MaxArchiveMB = profile.MaxArchiveMB,
                Archives = new ContentArchives.Archive[groups.Count],
            };

            for (int k = 0; k < groups.Count; ++k)
            {
                var group = groups[k];
                var resources = new ResourceFile[group.Files.Count];
                for (int i = 0; i < resources.Length; ++i)
                    resources[i] = new ResourceFile
                    {
                        fileName = Path.GetFullPath(Path.Combine(output, group.Files[i])),
                        fileAlias = group.Files[i],
                        serializedFile = group.Files[i].EndsWith(".cf", StringComparison.Ordinal),
                    };
                var archivePath = Path.Combine(output, group.Name);
                uint crc = ContentBuildInterface.ArchiveAndCompress(resources, archivePath, compression);
                if (crc == 0 || !File.Exists(archivePath))
                {
                    errors.Add($"ArchiveAndCompress failed for '{group.Name}'");
                    return false;
                }
                long inputBytes = 0;
                foreach (var rel in group.Files) inputBytes += input.SizeOf(rel);
                var files = new string[group.Files.Count];
                for (int i = 0; i < files.Length; ++i) files[i] = group.Files[i];
                index.Archives[k] = new ContentArchives.Archive
                {
                    File = group.Name,
                    Bytes = new FileInfo(archivePath).Length,
                    InputBytes = inputBytes,
                    Crc = crc.ToString("X8"),
                    Files = files,
                };
            }

            // The loose files are inside the archives now. The hash file stays: it names the manifest for anyone
            // looking at the folder, the same way the engine's archive mode leaves it.
            foreach (var group in groups)
                foreach (var rel in group.Files)
                {
                    if (rel == ContentBuildManifest.HashFile) continue;
                    var path = Path.Combine(output, rel);
                    File.Delete(path);
                    if (File.Exists(path + ".meta")) File.Delete(path + ".meta");
                }
            File.WriteAllText(Path.Combine(output, ContentArchives.IndexFile), JsonUtility.ToJson(index, true));

            var all = new List<string>();
            foreach (var f in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
                if (!f.EndsWith(".meta", StringComparison.Ordinal))
                    all.Add(f.Substring(output.Length).TrimStart('/', '\\').Replace('\\', '/'));
            all.Sort(StringComparer.Ordinal);
            result.Files = all.ToArray();
            var archives = new string[groups.Count];
            for (int k = 0; k < archives.Length; ++k) archives[k] = index.Archives[k].File;
            result.Archives = archives;
            result.ManifestPath = "";   // inside an archive now; the parsed manifest and the built names stay
            return true;
        }

        /// <summary>
        /// What the partitioner sees. The manifest's members come from BuildManifestHash.txt itself, not from whether
        /// the manifest parsed: a manifest of a version this package does not know must still travel with its hash file.
        /// </summary>
        static bool HasSerializedFiles(ArchivePartitionInput input)
        {
            foreach (var f in input.Files) if (f.EndsWith(".cf", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool BuildInput(ContentBuildResult result, Profile profile, List<string> errors, out ArchivePartitionInput input)
        {
            input = null;
            var output = result.OutputPath;
            var files = new List<string>(result.Files);
            var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var rel in files) sizes[rel] = new FileInfo(Path.Combine(output, rel)).Length;

            if (!files.Contains(ContentBuildManifest.HashFile))
            {
                errors.Add($"cannot pack: the build has no {ContentBuildManifest.HashFile} — nothing could register it");
                return false;
            }
            var manifestName = File.ReadAllText(Path.Combine(output, ContentBuildManifest.HashFile)).Trim() + ".json";
            if (!files.Contains(manifestName))
            {
                errors.Add($"cannot pack: {ContentBuildManifest.HashFile} names '{manifestName}', which the build did not produce");
                return false;
            }

            input = new ArchivePartitionInput
            {
                ProfileName = profile.Name,
                LimitBytes = TestLimitBytes ?? Math.Max(1, profile.MaxArchiveMB) * 1024L * 1024L,
                Files = files,
                Sizes = sizes,
                ManifestFiles = new[] { ContentBuildManifest.HashFile, manifestName },
                Manifest = result.Manifest,
                Loadables = result.Loadables,
            };
            return true;
        }

        /// <summary>The same input with the files in another, fixed order (by the MD5 of the alias) — the second call of the purity check.</summary>
        static ArchivePartitionInput Permuted(ArchivePartitionInput input)
        {
            var files = new List<string>(input.Files);
            var key = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in files) key[f] = ArchiveNaming.Hex(ArchiveNaming.Md5(f), 32);
            files.Sort((a, b) => string.CompareOrdinal(key[a], key[b]));
            return new ArchivePartitionInput
            {
                ProfileName = input.ProfileName,
                LimitBytes = input.LimitBytes,
                Files = files,
                Sizes = input.Sizes,
                ManifestFiles = input.ManifestFiles,
                Manifest = input.Manifest,
                Loadables = input.Loadables,
            };
        }
    }
}
