using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// Decides which flat build files go into which archive, and what each archive is called. The packer resolves
    /// the profile's implementation, calls it, checks the result (<see cref="ArchivePartitionValidator"/>) and
    /// writes the archives; the runtime only ever reads archives.json, so any partition that passes the validator
    /// mounts and loads. Must be a pure function of the input: the packer calls it twice, the second time with the
    /// files in a different order, and fails the build when the two answers differ.
    /// </summary>
    public interface IContentArchivePartitioner
    {
        IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput input);
    }

    /// <summary>One archive: its file name and the aliases that go inside, in that order. The order of the groups is the order of archives.json.</summary>
    public sealed class ArchiveGroup
    {
        public string Name;
        public IReadOnlyList<string> Files;

        public ArchiveGroup() { }
        public ArchiveGroup(string name, IReadOnlyList<string> files) { Name = name; Files = files; }
    }

    /// <summary>A catalog entry the engine built: which catalog it came from, its name, its object-id hash and the serialized file on disk that holds it (null when the manifest did not say).</summary>
    public readonly struct LoadableInfo
    {
        public readonly string Catalog;
        public readonly string Name;
        public readonly string ObjectIdHash;
        public readonly string File;

        public LoadableInfo(string catalog, string name, string objectIdHash, string file)
        {
            Catalog = catalog; Name = name; ObjectIdHash = objectIdHash; File = file;
        }
    }

    public sealed class ArchivePartitionInput
    {
        public string ProfileName = "";
        public long LimitBytes;
        /// <summary>Every flat build file, relative to the output folder — the manifest files included. Do not rely on the order.</summary>
        public IReadOnlyList<string> Files;
        public IReadOnlyDictionary<string, long> Sizes;
        /// <summary>BuildManifestHash.txt and the manifest it names. The built-in partitioners give them an archive of their own.</summary>
        public IReadOnlyList<string> ManifestFiles;
        /// <summary>The parsed manifest — null when it was not a shape the package knows. ContentHash links a serialized file to its file on disk.</summary>
        public ContentBuildManifest Manifest;
        /// <summary>Catalog entries the engine built, with their catalog and, where the manifest says, their file on disk.</summary>
        public IReadOnlyList<LoadableInfo> Loadables;

        public long SizeOf(string file) => Sizes != null && Sizes.TryGetValue(file, out var size) ? size : 0;
    }

    /// <summary>Name helpers the built-in partitioners share; a custom one may use them too.</summary>
    public static class ArchiveNaming
    {
        public const string Extension = ".archive";

        public static byte[] Md5(string text)
        {
            using var md5 = MD5.Create();
            return md5.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
        }

        public static string Hex(byte[] bytes, int chars)
        {
            var sb = new StringBuilder(chars);
            for (int i = 0; i < bytes.Length && sb.Length < chars; ++i) sb.Append(bytes[i].ToString("x2"));
            return sb.Length > chars ? sb.ToString(0, chars) : sb.ToString();
        }

        /// <summary>Eight hex digits of the MD5 of the file list — the same list gives the same digits, so the name says what is inside.</summary>
        public static string ListHash(IEnumerable<string> files) => Hex(Md5(string.Join("\n", files)), 8);

        public static string Name(string profile, string label, IEnumerable<string> files) => $"{profile}.{label}-{ListHash(files)}{Extension}";
    }

    /// <summary>
    /// What the packer requires of any partition before it writes archives. Errors fail the build; warnings are logged.
    /// The rules are the ones the runtime and the loose-file cleanup actually depend on — a partition that passes
    /// mounts into one folder and does not overwrite or lose a file.
    /// </summary>
    public static class ArchivePartitionValidator
    {
        static readonly char[] k_InvalidNameChars = InvalidNameChars();
        static char[] InvalidNameChars() { var l = new List<char> { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }; for (int c = 0; c < 32; ++c) l.Add((char)c); return l.ToArray(); }
        public static bool Validate(ArchivePartitionInput input, IReadOnlyList<ArchiveGroup> groups, List<string> errors, List<string> warnings)
        {
            int before = errors.Count;
            if (groups == null) { errors.Add("partitioner returned null"); return false; }

            var invalid = k_InvalidNameChars;   // a fixed set: Path.GetInvalidFileNameChars() depends on the platform the build runs on
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inputs = new HashSet<string>(input.Files ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in input.Files ?? Array.Empty<string>()) seen[f] = 0;

            var manifestFiles = new HashSet<string>(input.ManifestFiles ?? Array.Empty<string>(), StringComparer.Ordinal);
            var manifestGroups = new HashSet<int>();
            for (int g = 0; g < groups.Count; ++g)
            {
                var group = groups[g];
                if (group == null) { errors.Add($"group {g} is null"); continue; }
                var name = group.Name ?? "";
                if (name.Length == 0) errors.Add($"group {g} has no name");
                else
                {
                    if (name.IndexOfAny(invalid) >= 0 || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name == "." || name == "..")
                        errors.Add($"archive name '{name}' is not a plain file name");
                    if (!name.EndsWith(ArchiveNaming.Extension, StringComparison.Ordinal))
                        errors.Add($"archive name '{name}' does not end with {ArchiveNaming.Extension}");
                    if (string.Equals(name, ContentArchives.IndexFile, StringComparison.OrdinalIgnoreCase) || inputs.Contains(name))
                        errors.Add($"archive name '{name}' collides with a file of the build");
                    if (!names.Add(name))
                        errors.Add($"archive name '{name}' is used twice (names are compared ignoring case)");
                }
                if (group.Files == null || group.Files.Count == 0) { errors.Add($"archive '{name}' has no files"); continue; }
                foreach (var f in group.Files)
                {
                    if (f == null || !seen.ContainsKey(f)) { errors.Add($"archive '{name}' lists '{f}', which is not a file of the build"); continue; }
                    if (++seen[f] > 1) errors.Add($"file '{f}' is assigned more than once");
                    if (manifestFiles.Contains(f)) manifestGroups.Add(g);
                }
                long total = 0;
                foreach (var f in group.Files) total += input.SizeOf(f);
                bool manifestOnly = true;
                foreach (var f in group.Files) if (!manifestFiles.Contains(f)) { manifestOnly = false; break; }
                if (!manifestOnly && group.Files.Count > 1 && input.LimitBytes > 0 && total > input.LimitBytes)   // the manifest archive is exempt from the limit by design
                    warnings?.Add($"archive '{name}' holds {total} input bytes with several files, over the {input.LimitBytes} B limit");
            }
            foreach (var kv in seen)
                if (kv.Value == 0) errors.Add($"file '{kv.Key}' is not assigned to any archive");
            if (manifestGroups.Count > 1)
                warnings?.Add("the manifest and BuildManifestHash.txt are in different archives — they still mount into one folder, but every patch then re-downloads both");
            return errors.Count == before;
        }

        /// <summary>True when both partitions are the same archives with the same files in the same order.</summary>
        public static bool Same(IReadOnlyList<ArchiveGroup> a, IReadOnlyList<ArchiveGroup> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; ++i)
            {
                if (a[i] == null || b[i] == null || !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)) return false;
                var fa = a[i].Files; var fb = b[i].Files;
                if (fa == null || fb == null || fa.Count != fb.Count) return false;
                for (int j = 0; j < fa.Count; ++j) if (!string.Equals(fa[j], fb[j], StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}
