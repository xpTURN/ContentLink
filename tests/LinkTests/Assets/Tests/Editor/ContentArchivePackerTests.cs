using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Packing the flat engine output into archives on the real engine — the hash-prefix trie's locality (K4), a
    /// synthetic fixture large enough to measure (K5), a custom partitioner (K6) — and the post-processor hook.
    /// The packed directories K2/K5/K6 leave under StreamingAssets/ContentLinkTests/Runner/Packed{2,3,6} are what
    /// ProfileBuildTests.B2/B3/B4 (PlayMode) mount, register and load from.
    /// </summary>
    [Category("Build")]
    public class ContentArchivePackerTests
    {
        const string k_Generated = "Assets/TestContent/Generated";

        string m_Snapshot, m_SourceRoot, m_CatalogPath, m_OutputPath;
        readonly List<string> m_TempCatalogs = new();

        public static readonly List<string> PostProcessCalls = new();
        public sealed class RecordA : IContentBuildPostProcess { public void OnBuilt(ContentBuildResult r) => PostProcessCalls.Add($"A:{r.Files.Length}:{(r.Succeeded ? "ok" : "fail")}"); }
        public sealed class RecordB : IContentBuildPostProcess { public void OnBuilt(ContentBuildResult r) => PostProcessCalls.Add($"B:{r.Files.Length}:{(r.Succeeded ? "ok" : "fail")}"); }
        public sealed class Throwing : IContentBuildPostProcess { public void OnBuilt(ContentBuildResult r) => throw new System.InvalidOperationException("boom"); }

        /// <summary>A project-side partitioner: one archive per file extension. Nothing the runtime cares about — the index is the contract.</summary>
        public sealed class ByExtensionPartitioner : IContentArchivePartitioner
        {
            public IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput input)
            {
                var manifest = new List<string>(input.ManifestFiles); manifest.Sort(System.StringComparer.Ordinal);
                var byExt = new SortedDictionary<string, List<string>>(System.StringComparer.Ordinal);
                foreach (var f in input.Files)
                {
                    if (manifest.Contains(f)) continue;
                    var ext = Path.GetExtension(f).TrimStart('.');
                    if (!byExt.TryGetValue(ext, out var list)) byExt[ext] = list = new List<string>();
                    list.Add(f);
                }
                var groups = new List<ArchiveGroup> { new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, "ext-manifest", manifest), manifest) };
                foreach (var kv in byExt) { kv.Value.Sort(System.StringComparer.Ordinal); groups.Add(new ArchiveGroup(ArchiveNaming.Name(input.ProfileName, "ext-" + kv.Key, kv.Value), kv.Value)); }
                return groups;
            }
        }

        /// <summary>Every file in one archive whose name holds spaces — a plain file name that a URL does not carry as is.</summary>
        public sealed class SpacedNamePartitioner : IContentArchivePartitioner
        {
            public IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput input)
            {
                var files = new List<string>(input.Files); files.Sort(System.StringComparer.Ordinal);
                return new[] { new ArchiveGroup($"{input.ProfileName} all {ArchiveNaming.ListHash(files)}{ArchiveNaming.Extension}", files) };
            }
        }

        readonly List<string> m_TempFolders = new();

        [SetUp]
        public void Snapshot()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            m_OutputPath = ContentDirectoryBuilder.OutputPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            PostProcessCalls.Clear();
        }

        [TearDown]
        public void Restore()
        {
            ContentArchivePacker.TestLimitBytes = null;
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            ContentDirectoryBuilder.ClearOverrides();
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            ContentDirectoryBuilder.OutputPath = m_OutputPath;
            foreach (var path in m_TempCatalogs) AssetDatabase.DeleteAsset(path);
            m_TempCatalogs.Clear();
            foreach (var folder in m_TempFolders) if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            m_TempFolders.Clear();
            ContentLinkEditorUtility.Invalidate();
        }

        /// <summary>A generated source root under TestContent/Generated, deleted in TearDown. Returns the asset path.</summary>
        string TempRoot(string name)
        {
            var folder = $"{k_Generated}/{name}";
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            AssetDatabase.CreateFolder(k_Generated, name);
            m_TempFolders.Add(folder);
            return folder;
        }

        static void WritePng(string path, int size, int seed)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color32[size * size];
            for (int i = 0; i < pixels.Length; ++i) pixels[i] = new Color32((byte)((i * 7 + seed) % 255), (byte)((i / size + seed * 3) % 255), (byte)(seed % 255), 255);
            tex.SetPixels32(pixels);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }

        static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; ++k) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
            return ~crc;
        }

        static HashSet<string> AllFiles(ContentArchives.Index index)
        {
            var set = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var a in index.Archives) foreach (var f in a.Files) set.Add(f);
            return set;
        }

        static string FileOf(ContentBuildResult r, string name)
        {
            foreach (var l in r.Loadables) if (l.Name == name) return l.File;
            return null;
        }

        /// <summary>The manifest ID ("&lt;hash&gt;.cfid") of the serialized file that holds the named loadable.</summary>
        static string IdOf(ContentBuildResult r, string name)
        {
            foreach (var l in r.Loadables)
                if (l.Name == name)
                    foreach (var m in r.Manifest.Loadables)
                        if (m.ObjectIdHash == l.ObjectIdHash) return r.Manifest.SerializedFiles[m.SerializedFile].ID;
            return null;
        }

        static HashSet<string> Ids(ContentBuildResult r)
        {
            var set = new HashSet<string>(System.StringComparer.Ordinal);
            foreach (var f in r.Manifest.SerializedFiles) if (!f.IsBuiltIn) set.Add(f.ID);
            return set;
        }

        ContentBuildSettings.Profile Profile(string name, params string[] subFolders)
        {
            var p = ContentBuildSettings.instance.Add(name);
            Assert.That(p, Is.Not.Null);
            p.SourceRoot = ContentBuildRunnerTests.RootA;
            p.OutputPath = $"{ContentBuildRunnerTests.OutputFolder}/{name}";
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            var catalogs = new ContentBuildSettings.CatalogEntry[subFolders.Length];
            for (int i = 0; i < subFolders.Length; ++i)
            {
                var path = $"{k_Generated}/Packer_{name}_{i}_{System.Guid.NewGuid():N}.asset";
                catalogs[i] = new ContentBuildSettings.CatalogEntry { SubFolder = subFolders[i], CreateAt = path };
                m_TempCatalogs.Add(path);
            }
            p.Catalogs = catalogs;
            return p;
        }

        static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            return System.BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        [Test]
        public void K1_HashTrie_ManifestFirst_SplitsOnBits_OversizeAlone_OrderIndependent()
        {
            var sizes = new Dictionary<string, long> { ["c.cf"] = 3, ["a.cf"] = 4, ["b.cf"] = 5, ["big.resS"] = 20, ["BuildManifestHash.txt"] = 1, ["abc123.json"] = 2 };
            ArchivePartitionInput In(params string[] files) => new ArchivePartitionInput { ProfileName = "T", LimitBytes = 8, Files = files, Sizes = sizes, ManifestFiles = new[] { "BuildManifestHash.txt", "abc123.json" } };
            var trie = new ContentArchivePacker.HashTriePartitioner();
            var g = trie.Partition(In("c.cf", "big.resS", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"));
            Assert.That(g.Count, Is.EqualTo(4));
            Assert.That(g[0].Files, Is.EqualTo(new[] { "BuildManifestHash.txt", "abc123.json" }), "the manifest and its hash file first, in an archive of their own");
            Assert.That(g[0].Name, Is.EqualTo("T.manifest-" + ArchiveNaming.ListHash(g[0].Files) + ".archive"));
            Assert.That(g[1].Files, Is.EqualTo(new[] { "b.cf", "c.cf" }), "MD5 bit 0: b and c fall on the 0 side and fit the cap together");
            Assert.That(g[1].Name, Does.StartWith("T.0-"));
            Assert.That(g[2].Files, Is.EqualTo(new[] { "a.cf" }));
            Assert.That(g[2].Name, Does.StartWith("T.1-"));
            Assert.That(g[3].Files, Is.EqualTo(new[] { "big.resS" }), "a file over the cap gets an archive of its own");
            Assert.That(g[3].Name, Does.StartWith("T.big-big-"));
            var shuffled = trie.Partition(In("b.cf", "BuildManifestHash.txt", "a.cf", "big.resS", "abc123.json", "c.cf"));
            Assert.That(ArchivePartitionValidator.Same(g, shuffled), Is.True, "the input order must not matter");
            var errors = new List<string>(); var warnings = new List<string>();
            Assert.That(ArchivePartitionValidator.Validate(In("c.cf", "big.resS", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"), g, errors, warnings), Is.True, string.Join("; ", errors));
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void K8_ArchiveNameWithAColonOrQuestionMark_IsRejectedOnEveryPlatform()
        {
            // Path.GetInvalidFileNameChars() is platform dependent (macOS: '/' and NUL only), so a name that passes
            // validation on the build machine could still be unwritable on another platform. The rule is a fixed set.
            var sizes = new Dictionary<string, long> { ["a.cf"] = 4, ["BuildManifestHash.txt"] = 1, ["abc123.json"] = 2 };
            var input = new ArchivePartitionInput { ProfileName = "T", LimitBytes = 8, Files = new[] { "a.cf", "BuildManifestHash.txt", "abc123.json" }, Sizes = sizes, ManifestFiles = new[] { "BuildManifestHash.txt", "abc123.json" } };
            foreach (var name in new[] { "T:x.archive", "T?x.archive" })
            {
                var errors = new List<string>();
                var groups = new[] { new ArchiveGroup("T.m.archive", new[] { "BuildManifestHash.txt", "abc123.json" }), new ArchiveGroup(name, new[] { "a.cf" }) };
                Assert.That(ArchivePartitionValidator.Validate(input, groups, errors, null), Is.False, $"'{name}' must be rejected here too");
                Assert.That(errors, Has.Some.Contains(name));
            }
        }

        /// <summary>T8: the real packer writes index v2 — target, engine, type trees, layout · every archive's MD5 · the hash of the index's bytes.</summary>
        [Test]
        public void K8_IndexV2_RecordsTheTargetEngineAndEveryArchivesMd5()
        {
            var p = Profile("PackedV2", "Icons", "Prefabs");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            p.CleanBuildCache = true;
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            var index = ContentArchives.ReadIndex(p.OutputPath);
            Assert.That(index, Is.Not.Null);
            Assert.That(index.Version, Is.EqualTo(ContentArchives.IndexVersion));
            Assert.That(index.Target, Is.EqualTo(EditorUserBuildSettings.activeBuildTarget.ToString()));
            Assert.That(index.UnityVersion, Is.EqualTo(Application.unityVersion));
            Assert.That(index.TypeTrees, Is.True);
            Assert.That(index.CatalogLayout, Is.EqualTo(ContentCatalog.Layout));
            foreach (var a in index.Archives) Assert.That(a.Md5, Is.EqualTo(Md5Hex(File.ReadAllBytes(Path.Combine(p.OutputPath, a.File)))), a.File);
            var bytes = File.ReadAllBytes(Path.Combine(p.OutputPath, ContentArchives.IndexFile));
            Assert.That(File.ReadAllText(Path.Combine(p.OutputPath, ContentArchives.HashFile)), Is.EqualTo(Md5Hex(bytes)), "the hash file is the MD5 of the index's bytes");
        }

        static string Md5Hex(byte[] bytes)
        {
            using var md5 = MD5.Create();
            var sb = new System.Text.StringBuilder();
            foreach (var b in md5.ComputeHash(bytes)) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Test]
        public void K2_PackedBuild_SplitsAtTheCap_AndACleanRebuildIsByteIdentical()
        {
            var p = Profile("Packed2", "Icons", "Prefabs");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            p.CleanBuildCache = true;
            const long cap = 4 * 1024;   // the fixture's flat files run 32 B – 8.5 KB: this splits, and leaves the big ones alone
            ContentArchivePacker.TestLimitBytes = cap;

            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            Assert.That(r.Archives.Length, Is.GreaterThan(1), "the cap must split the fixture into several archives");
            Assert.That(Directory.GetFiles(p.OutputPath, "*.cf"), Is.Empty, "the loose serialized files went into the archives");
            Assert.That(File.Exists(Path.Combine(p.OutputPath, ContentBuildManifest.HashFile)), Is.True, "the hash file stays loose, as in the engine's archive mode");
            Assert.That(r.ManifestPath, Is.EqualTo(""), "the manifest is inside an archive now");
            Assert.That(r.Manifest, Is.Not.Null, "the parsed manifest and the built names stay in the result");
            Assert.That(r.BuiltNames, Is.Not.Empty);

            var index = ContentArchives.ReadIndex(p.OutputPath);
            Assert.That(index, Is.Not.Null);
            Assert.That(index.Archives.Length, Is.EqualTo(r.Archives.Length));
            Assert.That(index.Archives[0].File, Does.StartWith("Packed2.manifest-"), "the manifest archive comes first");
            Assert.That(index.Archives[0].Files.Length, Is.EqualTo(2), "the hash file and the manifest share it");
            Assert.That(index.Archives[0].Files, Does.Contain(ContentBuildManifest.HashFile));
            Assert.That(index.Archives[0].Files, Has.Some.EndsWith(".json"));
            int alone = 0, crcMatches = 0;
            var log = new System.Text.StringBuilder();
            foreach (var a in index.Archives)
            {
                log.Append($"{a.File}: {a.Files.Length} files, input {a.InputBytes} B → {a.Bytes} B (CRC {a.Crc})\n");
                if (a.Crc == Crc32(File.ReadAllBytes(Path.Combine(p.OutputPath, a.File))).ToString("X8")) ++crcMatches;   // is the index CRC the CRC32 of the archive bytes?
                if (a.Files.Length == 1) { ++alone; continue; }
                if (a.File.Contains(".manifest-")) continue;   // the manifest archive is exempt from the cap by design
                Assert.That(a.InputBytes, Is.LessThanOrEqualTo(cap), $"{a.File} holds more than the cap although it has several files");
            }
            Debug.Log($"[ContentLink.Tests] index Crc equals CRC32 of the archive bytes for {crcMatches} of {index.Archives.Length} archives");
            Assert.That(r.Loadables.Count, Is.EqualTo(r.BuiltNames.Count), "every built name has a Loadables entry");
            foreach (var l in r.Loadables) { Assert.That(l.File, Is.Not.Null, $"'{l.Name}': the manifest's ContentHash must name its file on disk"); Assert.That(l.File, Does.EndWith(".cf")); }
            Debug.Log($"[ContentLink.Tests] packed {r.Archives.Length} archives ({alone} single-file) under a {cap} B cap:\n{log}");

            var first = new Dictionary<string, string>();
            foreach (var a in r.Archives) first[a] = Sha256(Path.Combine(p.OutputPath, a));
            var firstIndex = File.ReadAllText(Path.Combine(p.OutputPath, ContentArchives.IndexFile));

            var r2 = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r2.Succeeded, Is.True, string.Join("\n", r2.Errors));
            Assert.That(r2.Archives, Is.EqualTo(r.Archives), "the second clean build must produce the same archive list");
            foreach (var a in r2.Archives)
                Assert.That(Sha256(Path.Combine(p.OutputPath, a)), Is.EqualTo(first[a]), $"{a} differs between two clean builds of the same input");
            Assert.That(File.ReadAllText(Path.Combine(p.OutputPath, ContentArchives.IndexFile)), Is.EqualTo(firstIndex), "archives.json (sizes, CRCs, file lists) must be identical too");
        }

        [Test]
        public void K4_ModifyingOneAsset_ChangesItsOwnArchivesOnly()
        {
            // A dependency chain: texture ← material ← prefab. Editing the texture must rename the texture's files only.
            var root = TempRoot("K4");
            var texPath = $"{root}/tex.png";
            WritePng(texPath, 128, seed: 1);
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var mat = new Material(shader) { name = "mat", mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath) };
            AssetDatabase.CreateAsset(mat, $"{root}/mat.mat");
            var go = new GameObject("prefab");
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            PrefabUtility.SaveAsPrefabAsset(go, $"{root}/prefab.prefab");
            Object.DestroyImmediate(go);
            for (int i = 0; i < 6; ++i) { WritePng($"{root}/filler{i}.png", 32, seed: 10 + i); AssetDatabase.ImportAsset($"{root}/filler{i}.png", ImportAssetOptions.ForceUpdate); }
            AssetDatabase.SaveAssets();

            var p = Profile("Packed4", "");
            p.SourceRoot = root;
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            p.CleanBuildCache = true;
            ContentArchivePacker.TestLimitBytes = 4 * 1024;

            var a = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(a.Succeeded, Is.True, string.Join("\n", a.Errors));
            var indexA = ContentArchives.ReadIndex(p.OutputPath);
            var shaA = new Dictionary<string, string>();
            foreach (var arc in indexA.Archives) shaA[arc.File] = Sha256(Path.Combine(p.OutputPath, arc.File));
            string texA = FileOf(a, "tex"), matA = FileOf(a, "mat"), prefabA = FileOf(a, "prefab");
            Assert.That(texA, Is.Not.Null.And.Not.EqualTo(matA), "the loadables map to files");

            WritePng(texPath, 128, seed: 2);   // the modification
            AssetDatabase.ImportAsset(texPath, ImportAssetOptions.ForceUpdate);
            var b = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(b.Succeeded, Is.True, string.Join("\n", b.Errors));
            var indexB = ContentArchives.ReadIndex(p.OutputPath);

            var filesA = AllFiles(indexA); var filesB = AllFiles(indexB);
            static bool IsManifest(string f) => f == ContentBuildManifest.HashFile || f.EndsWith(".json");   // the manifest is renamed on every build by design
            filesA.RemoveWhere(IsManifest); filesB.RemoveWhere(IsManifest);
            var added = new List<string>(); foreach (var f in filesB) if (!filesA.Contains(f)) added.Add(f);
            var removed = new List<string>(); foreach (var f in filesA) if (!filesB.Contains(f)) removed.Add(f);
            var newArchives = new List<string>(); var sameNameDifferentBytes = new List<string>();
            foreach (var arc in indexB.Archives)
            {
                if (!shaA.TryGetValue(arc.File, out var sha)) newArchives.Add(arc.File);
                else if (Sha256(Path.Combine(p.OutputPath, arc.File)) != sha) sameNameDifferentBytes.Add(arc.File);
            }
            Debug.Log($"[ContentLink.Tests] K4: files renamed {removed.Count} → {added.Count} ({string.Join(", ", added)}); archives changed {newArchives.Count} / {indexB.Archives.Length} ({string.Join(", ", newArchives)})");

            Assert.That(texA, Is.Not.EqualTo(FileOf(b, "tex")), "the modified texture's serialized file is renamed");
            Assert.That(IdOf(b, "tex"), Is.EqualTo(IdOf(a, "tex")), "the modified texture's file keeps its manifest ID although its content hash changed");
            Assert.That(Ids(b), Is.EquivalentTo(Ids(a)), "every serialized file's ID (dependency-only files included) is the same in both builds");
            Debug.Log($"[ContentLink.Tests] K4: tex ID {IdOf(a, "tex")} → {IdOf(b, "tex")}; {Ids(a).Count} IDs in both builds");
            Assert.That(FileOf(b, "mat"), Is.EqualTo(matA), "the material that references the texture keeps its file");
            Assert.That(FileOf(b, "prefab"), Is.EqualTo(prefabA), "the prefab that references the material keeps its file");
            Assert.That(removed, Has.No.Member(matA), "the material's file is not among the renamed ones");
            Assert.That(removed, Has.No.Member(prefabA), "the prefab's file is not among the renamed ones");
            Assert.That(added.Count, Is.EqualTo(removed.Count).And.LessThanOrEqualTo(2), "one texture is at most .cf + .resS");
            Assert.That(newArchives.Count, Is.LessThanOrEqualTo(added.Count * 2 + 1), "locality: at most two archives per renamed file, plus the manifest archive");
            Assert.That(sameNameDifferentBytes, Is.Empty, "an archive that kept its name kept its bytes");
        }

        [Test]
        public void K5_SyntheticFixture_ManyLeaves_ForSizeDistributionAndMountTiming()
        {
            // 200 small textures under a 4 KB cap: enough archives to see the leaf size distribution and to time mounting (B3).
            var root = TempRoot("K5");
            try
            {
                AssetDatabase.StartAssetEditing();
                for (int i = 0; i < 200; ++i) WritePng($"{root}/t{i:D3}.png", 16, seed: i);
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.Refresh();

            var p = Profile("Packed3", "");
            p.SourceRoot = root;
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            const long cap = 4 * 1024;
            ContentArchivePacker.TestLimitBytes = cap;
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            var index = ContentArchives.ReadIndex(p.OutputPath);

            var multi = new List<long>(); int single = 0, big = 0;
            foreach (var a in index.Archives)
            {
                if (a.File.Contains(".manifest-")) continue;   // limit-exempt by design
                if (a.File.Contains(".big-")) { ++big; continue; }
                if (a.Files.Length == 1) ++single; else multi.Add(a.InputBytes);
            }
            multi.Sort();
            Debug.Log($"[ContentLink.Tests] K5: {index.Archives.Length} archives from {AllFiles(index).Count} files under a {cap} B cap — {multi.Count} multi-file leaves"
                      + (multi.Count > 0 ? $" (input bytes min {multi[0]}, median {multi[multi.Count / 2]}, max {multi[multi.Count - 1]}, median/cap {(double)multi[multi.Count / 2] / cap:F2})" : "")
                      + $", {single} single-file leaves, {big} oversized");
            Assert.That(index.Archives.Length, Is.GreaterThanOrEqualTo(50), "the fixture must yield at least 50 archives for the mount timing (B3)");
            foreach (var a in index.Archives) if (a.Files.Length > 1 && !a.File.Contains(".manifest-")) Assert.That(a.InputBytes, Is.LessThanOrEqualTo(cap), a.File);
        }

        [Test]
        public void K6_CustomPartitioner_GroupsAsItLikes_AndTheOutputRegistersLikeAnyOther()
        {
            var p = Profile("Packed6", "Icons", "Prefabs");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            p.Partitioner = ContentBuildWindowModel.NameOf(typeof(ByExtensionPartitioner));
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            var index = ContentArchives.ReadIndex(p.OutputPath);
            Assert.That(index.Archives[0].File, Does.StartWith("Packed6.ext-manifest-"));
            var labels = new List<string>();
            foreach (var a in index.Archives) { Assert.That(a.File, Does.StartWith("Packed6.ext-")); labels.Add(a.File.Split('-')[1]); }
            Assert.That(labels, Does.Contain("cf"), "every .cf in one archive");
            Debug.Log($"[ContentLink.Tests] K6: custom partition → {string.Join(", ", labels)}");

            p.Partitioner = "Nope.Partitioner";
            LogAssert.Expect(LogType.Error, new Regex("build of 'Packed6' failed"));
            var bad = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(bad.Succeeded, Is.False);
            Assert.That(bad.Errors, Has.Some.Contains("partitioner 'Nope.Partitioner' was not found"));
            p.Partitioner = ContentBuildWindowModel.NameOf(typeof(ByExtensionPartitioner));
            ContentBuildRunner.Build(p, showProgress: false);   // leave the custom output for B4
        }

        [Test]
        public void K7_SameInputInAnotherOutputFolder_PacksTheSameBytes()
        {
            // ArchiveAndCompress is given absolute paths; the archives must not depend on them.
            var p = Profile("Packed2b", "Icons", "Prefabs");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Compression = ContentBuildSettings.CompressionKind.LZ4;
            p.CleanBuildCache = true;
            ContentArchivePacker.TestLimitBytes = 4 * 1024;   // as K2
            var reference = $"{ContentBuildRunnerTests.OutputFolder}/Packed2";
            if (!File.Exists(Path.Combine(reference, ContentArchives.IndexFile))) Assert.Ignore("Runner/Packed2 is missing — K2 runs first");
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            var a = ContentArchives.ReadIndex(reference); var b = ContentArchives.ReadIndex(p.OutputPath);
            int same = 0, differ = 0;
            foreach (var x in b.Archives)
            {
                var name = x.File.Replace("Packed2b.", "Packed2.");
                if (!File.Exists(Path.Combine(reference, name))) continue;   // a name is profile-prefixed; the list hash part must match
                if (Sha256(Path.Combine(reference, name)) == Sha256(Path.Combine(p.OutputPath, x.File))) ++same; else ++differ;
            }
            // The manifest (BuildName) and the catalogs (asset names) differ between the two profiles, so their archives have other names and are skipped; every archive that shares its name must share its bytes.
            Debug.Log($"[ContentLink.Tests] K7: {same} archives byte-identical across output folders, {differ} differ, {b.Archives.Length - same - differ} not comparable (of {b.Archives.Length}; reference {a.Archives.Length})");
            Assert.That(differ, Is.EqualTo(0), "an archive with the same file list must be byte-identical wherever it was built");
            Assert.That(same, Is.GreaterThan(0), "at least the archives without catalogs must match");
            Directory.Delete(p.OutputPath, true);
            if (File.Exists(p.OutputPath + ".meta")) File.Delete(p.OutputPath + ".meta");
        }

        [Test]
        public void K3_PostProcessors_RunInTheListedOrder_AndAMissingTypeOrAnExceptionFailsTheBuild()
        {
            var p = Profile("Post", "");
            p.PostProcessors = new[] { typeof(RecordB).AssemblyQualifiedName, typeof(RecordA).FullName };
            var r = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join("\n", r.Errors));
            Assert.That(PostProcessCalls, Is.EqualTo(new[] { $"B:{r.Files.Length}:ok", $"A:{r.Files.Length}:ok" }), "listed order, each seeing the finished result");

            p.PostProcessors = new[] { "Nope.Missing" };
            LogAssert.Expect(LogType.Error, new Regex("build of 'Post' failed"));
            var rm = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(rm.Succeeded, Is.False);
            Assert.That(rm.Errors, Has.Some.Contains("'Nope.Missing' was not found"));
            Assert.That(File.Exists(Path.Combine(p.OutputPath, ContentBuildManifest.HashFile)), Is.True, "the output stays for inspection");

            p.PostProcessors = new[] { typeof(Throwing).FullName };
            LogAssert.Expect(LogType.Error, new Regex("build of 'Post' failed"));
            var rt = ContentBuildRunner.Build(p, showProgress: false);
            Assert.That(rt.Succeeded, Is.False);
            Assert.That(rt.Errors, Has.Some.Contains("threw InvalidOperationException: boom"));
        }

        static void DeleteOutput(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            else if (Directory.Exists(folder)) { Directory.Delete(folder, true); if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta"); }
        }

        [Test]
        public void K9_AnArchiveNameAUrlDoesNotCarry_FailsThePack_AndNothingIsPacked()
        {
            var p = Profile("SpacedNames", "Icons");
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            p.Partitioner = ContentBuildWindowModel.NameOf(typeof(SpacedNamePartitioner));
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("build of 'SpacedNames' failed"));
                var r = ContentBuildRunner.Build(p, showProgress: false);
                int archives = Directory.Exists(p.OutputPath) ? Directory.GetFiles(p.OutputPath, "*" + ArchiveNaming.Extension).Length : 0;
                bool index = File.Exists(Path.Combine(p.OutputPath, ContentArchives.IndexFile));
                Debug.Log($"[ContentLink.Tests] K9: succeeded={r.Succeeded} archives={archives} index={index} errors={string.Join(" | ", r.Errors)}");
                Assert.That(r.Succeeded, Is.False, "an archive name with a space must fail the build");
                Assert.That(r.Errors, Has.Some.Contains("does not carry as is"));
                Assert.That(archives, Is.EqualTo(0), "no archive is written");
                Assert.That(index, Is.False, "no index is written");
            }
            finally { DeleteOutput(p.OutputPath); }
        }

        [Test]
        public void K10_APackedProfileNamedWithACharacterAnArchiveCannotCarry_IsRefusedBeforeAnythingIsWritten()
        {
            var p = Profile("Stage#1", "Icons");
            p.OutputPath = $"{ContentBuildRunnerTests.OutputFolder}/K10";
            p.Archive = ContentBuildSettings.ArchiveMode.Packed;
            DeleteOutput(p.OutputPath);
            try
            {
                LogAssert.Expect(LogType.Error, new Regex("build of 'Stage#1' failed"));
                var r = ContentBuildRunner.Build(p, showProgress: false);
                Debug.Log($"[ContentLink.Tests] K10: succeeded={r.Succeeded} output={Directory.Exists(p.OutputPath)} errors={string.Join(" | ", r.Errors)}");
                Assert.That(r.Succeeded, Is.False, "a packed profile named after a URL character must not build");
                Assert.That(r.Errors, Has.Some.Contains("archive name cannot carry"));
                Assert.That(Directory.Exists(p.OutputPath), Is.False, "nothing is written");
            }
            finally { DeleteOutput(p.OutputPath); }
        }
    }
}
