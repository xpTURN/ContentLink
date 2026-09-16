using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build.Content;
using UnityEditor.Build.Reporting;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Probes of the engine's build pipeline. Each calls BuildPipeline.BuildContentDirectory directly (not through the
    /// build runner) on TestContent/A, and leaves outputs that the PlayMode probes register:
    ///   Probe/Flat           plain build                                  → packed by hand into Probe/Packed
    ///   Probe/EngineArchive  UseArchive + LZ4                             → does it register as a directory?
    ///   Probe/Dup            two root catalogs sharing a name             → which one the runtime serves
    /// The rest are measured here: determinism, option effects, output cleanup.
    /// Outputs live under StreamingAssets so the player run can read them too; the folder is gitignored.
    /// </summary>
    [Category("Build")]
    public class BuildProbeTests
    {
        public const string RootA = "Assets/TestContent/A";
        public const string ProbeFolder = "Assets/StreamingAssets/ContentLinkTests/Probe";
        const string k_Generated = "Assets/TestContent/Generated";

        string m_SourceRoot, m_CatalogPath, m_OutputPath;
        readonly List<string> m_TempAssets = new();

        [SetUp]
        public void SaveStatics()
        {
            m_SourceRoot = ContentDirectoryBuilder.SourceRoot;
            m_CatalogPath = ContentDirectoryBuilder.CatalogPath;
            m_OutputPath = ContentDirectoryBuilder.OutputPath;
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            Directory.CreateDirectory(ProbeFolder);
        }

        [TearDown]
        public void RestoreStatics()
        {
            ContentDirectoryBuilder.SourceRoot = m_SourceRoot;
            ContentDirectoryBuilder.CatalogPath = m_CatalogPath;
            ContentDirectoryBuilder.OutputPath = m_OutputPath;
            foreach (var path in m_TempAssets) AssetDatabase.DeleteAsset(path);
            m_TempAssets.Clear();
            ContentLinkEditorUtility.Invalidate();
        }

        // ───────────── helpers ─────────────

        /// <summary>A fresh catalog of <paramref name="root"/> at a temp path, remembered for cleanup.</summary>
        string Catalog(string root, string tag)
        {
            var path = $"{k_Generated}/Probe_{tag}_{Guid.NewGuid():N}.asset";
            var scanned = new List<ContentDirectoryBuilder.Scanned>();
            Assert.That(ContentDirectoryBuilder.Scan(root, scanned), Is.True, $"scan of {root}");
            ContentDirectoryBuilder.Write(ContentDirectoryBuilder.LoadOrCreateCatalog(path), root, scanned);
            m_TempAssets.Add(path);
            return path;
        }

        static BuildReport Build(string output, string[] catalogs, BuildCompression compression, BuildContentOptions options, string name = null)
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
            var meta = output + ".meta";
            if (File.Exists(meta)) File.Delete(meta);
            var p = new BuildContentDirectoryParameters
            {
                outputPath = output,
                rootAssetPaths = catalogs,
                compression = compression,
                options = options,
            };
            if (name != null) p.name = name;
            var report = BuildPipeline.BuildContentDirectory(p);
            Assert.That(report, Is.Not.Null);
            Assert.That(report.summary.result, Is.EqualTo(BuildResult.Succeeded), $"build into {output}");
            return report;
        }

        static string[] Files(string dir)
        {
            var list = new List<string>();
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                if (!f.EndsWith(".meta", StringComparison.Ordinal)) list.Add(f.Replace('\\', '/'));
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        static string Manifest(string dir)
        {
            var hash = File.ReadAllText(Path.Combine(dir, "BuildManifestHash.txt")).Trim();
            return File.ReadAllText(Path.Combine(dir, hash + ".json"));
        }

        static string Sha(string file)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file)));
        }

        static long TotalBytes(string dir)
        {
            long n = 0;
            foreach (var f in Files(dir)) n += new FileInfo(f).Length;
            return n;
        }

        static string Layout(string dir)
        {
            var parts = new List<string>();
            foreach (var f in Files(dir)) parts.Add($"{Path.GetFileName(f)}({new FileInfo(f).Length})");
            return string.Join(" ", parts);
        }

        // ───────────── determinism ─────────────

        [Test]
        public void P1a_TwoCleanBuilds_AreByteIdentical()
        {
            // The same output folder twice: BuildName defaults to the folder name, so two folders would differ in the
            // manifest by construction. The first result is copied aside before the second build replaces it.
            var catalog = Catalog(RootA, "det");
            var a = $"{ProbeFolder}/Det.first";
            var b = $"{ProbeFolder}/Det";
            Build(b, new[] { catalog }, BuildCompression.Uncompressed, BuildContentOptions.CleanBuildCache);
            if (Directory.Exists(a)) Directory.Delete(a, true);
            Directory.CreateDirectory(a);
            foreach (var f in Files(b)) File.Copy(f, Path.Combine(a, Path.GetFileName(f)));
            Build(b, new[] { catalog }, BuildCompression.Uncompressed, BuildContentOptions.CleanBuildCache);

            var fa = Files(a);
            var fb = Files(b);
            var namesA = Array.ConvertAll(fa, Path.GetFileName);
            var namesB = Array.ConvertAll(fb, Path.GetFileName);
            Assert.That(namesB, Is.EqualTo(namesA), "the two builds do not even produce the same file names");

            int differing = 0;
            for (int i = 0; i < fa.Length; ++i)
                if (Sha(fa[i]) != Sha(fb[i])) { ++differing; Debug.Log($"[ContentLink.Tests] differs: {namesA[i]}"); }
            Debug.Log($"[ContentLink.Tests] clean builds: {fa.Length} files, {differing} differ byte-wise");
            Assert.That(differing, Is.EqualTo(0),
                "two clean builds of the same input differ — the engine's bytes are not reproducible, so determinism can only be promised for our own artifacts");
        }

        // ───────────── what the options do ─────────────

        [Test]
        public void P1b_Options_AreHonoured_AndArchiveLayoutIsRecorded()
        {
            var catalog = Catalog(RootA, "opt");
            var flat = $"{ProbeFolder}/Flat";
            var lz4NoArchive = $"{ProbeFolder}/Lz4NoArchive";
            var engine = $"{ProbeFolder}/EngineArchive";
            var lzma = $"{ProbeFolder}/LzmaArchive";
            var noTree = $"{ProbeFolder}/NoTypeTree";

            var t0 = DateTime.UtcNow;
            Build(flat, new[] { catalog }, BuildCompression.Uncompressed, BuildContentOptions.None, "FlatName");   // does name reach BuildName?
            var tFlat = (DateTime.UtcNow - t0).TotalMilliseconds;
            t0 = DateTime.UtcNow;
            Build(lz4NoArchive, new[] { catalog }, BuildCompression.LZ4, BuildContentOptions.None);
            var tLz4 = (DateTime.UtcNow - t0).TotalMilliseconds;
            t0 = DateTime.UtcNow;
            Build(engine, new[] { catalog }, BuildCompression.LZ4, BuildContentOptions.UseArchive, "EngineArchive");
            var tEngine = (DateTime.UtcNow - t0).TotalMilliseconds;
            t0 = DateTime.UtcNow;
            Build(lzma, new[] { catalog }, BuildCompression.LZMA, BuildContentOptions.UseArchive);
            var tLzma = (DateTime.UtcNow - t0).TotalMilliseconds;
            // Measured: "Unable to use flags DisableWriteTypeTree unless SerializeUnityVersion is also specified." —
            // the option is only valid together with SerializeUnityVersion, so the runner must couple them.
            Build(noTree, new[] { catalog }, BuildCompression.Uncompressed, BuildContentOptions.DisableWriteTypeTree | BuildContentOptions.SerializeUnityVersion);

            Debug.Log($"[ContentLink.Tests] flat: {TotalBytes(flat)} B / {Files(flat).Length} files ({tFlat:F0} ms)");
            Debug.Log($"[ContentLink.Tests] LZ4 without UseArchive: {TotalBytes(lz4NoArchive)} B / {Files(lz4NoArchive).Length} files ({tLz4:F0} ms) — layout: {Layout(lz4NoArchive)}");
            Debug.Log($"[ContentLink.Tests] UseArchive + LZ4: {TotalBytes(engine)} B / {Files(engine).Length} files ({tEngine:F0} ms) — layout: {Layout(engine)}");
            Debug.Log($"[ContentLink.Tests] UseArchive + LZMA: {TotalBytes(lzma)} B / {Files(lzma).Length} files ({tLzma:F0} ms) — layout: {Layout(lzma)}");
            Debug.Log($"[ContentLink.Tests] DisableWriteTypeTree needs SerializeUnityVersion (engine refuses the flag alone); with both: {TotalBytes(noTree)} B");

            var manifestFlat = Manifest(flat);
            var manifestNoTree = Manifest(noTree);
            Debug.Log($"[ContentLink.Tests] BuiltWithTypeTrees flat={Regex.Match(manifestFlat, "\"BuiltWithTypeTrees\":(\\w+)").Groups[1].Value} noTree={Regex.Match(manifestNoTree, "\"BuiltWithTypeTrees\":(\\w+)").Groups[1].Value}");
            Assert.That(manifestNoTree, Does.Contain("\"BuiltWithTypeTrees\":false"), "DisableWriteTypeTree is not reflected in the manifest — options are not honoured");

            // The archive builds keep their manifest inside content0.archive — only the hash file is loose —
            // so BuildName is read from the flat build, which was given an explicit name.
            var buildName = Regex.Match(manifestFlat, "\"BuildName\":\"([^\"]*)\"").Groups[1].Value;
            Debug.Log($"[ContentLink.Tests] name=FlatName → manifest BuildName='{buildName}' (folder is 'Flat')");
            Debug.Log($"[ContentLink.Tests] engine archive output has no loose manifest: {!File.Exists(Path.Combine(engine, File.ReadAllText(Path.Combine(engine, "BuildManifestHash.txt")).Trim() + ".json"))}");
            Debug.Log($"[ContentLink.Tests] LZ4 without UseArchive equals flat: names={string.Join(",", Array.ConvertAll(Files(lz4NoArchive), Path.GetFileName)) == string.Join(",", Array.ConvertAll(Files(flat), Path.GetFileName))} bytes={TotalBytes(lz4NoArchive) == TotalBytes(flat)}");
        }

        // ───────────── preparation: pack the flat output by hand ─────────────

        [Test]
        public void P1c_PackFlatOutputIntoTwoArchives()
        {
            var catalog = Catalog(RootA, "pack");
            var flat = $"{ProbeFolder}/Flat";
            Build(flat, new[] { catalog }, BuildCompression.Uncompressed, BuildContentOptions.None);

            var packed = $"{ProbeFolder}/Packed";
            if (Directory.Exists(packed)) Directory.Delete(packed, true);
            Directory.CreateDirectory(packed);

            // Sorted by name, split in two — a hand-made shape (the packer's own rules are ContentArchivePacker's
            // business). The manifest and the hash file go into the first archive, and also stay loose next to the
            // archives for whoever needs to inspect them.
            var files = Files(flat);
            var half = (files.Length + 1) / 2;
            var groups = new[] { new List<ResourceFile>(), new List<ResourceFile>() };
            for (int i = 0; i < files.Length; ++i)
            {
                var f = files[i];
                groups[i < half ? 0 : 1].Add(new ResourceFile
                {
                    fileName = Path.GetFullPath(f),
                    fileAlias = Path.GetFileName(f),
                    serializedFile = f.EndsWith(".cf", StringComparison.Ordinal),
                });
            }
            for (int k = 0; k < 2; ++k)
            {
                var archive = $"{packed}/a.{k}.archive";
                uint crc = ContentBuildInterface.ArchiveAndCompress(groups[k].ToArray(), archive, BuildCompression.LZ4);
                Assert.That(crc, Is.Not.EqualTo(0u), $"ArchiveAndCompress failed for {archive}");
                Assert.That(File.Exists(archive), Is.True);
                Debug.Log($"[ContentLink.Tests] {Path.GetFileName(archive)}: {groups[k].Count} files, {new FileInfo(archive).Length} B, crc={crc:X8}");
            }
            foreach (var f in files)
                if (f.EndsWith(".json", StringComparison.Ordinal) || f.EndsWith("BuildManifestHash.txt", StringComparison.Ordinal))
                    File.Copy(f, Path.Combine(packed, Path.GetFileName(f)), true);
            AssetDatabase.Refresh();
        }

        // ───────────── preparation: two root catalogs, one name ─────────────

        [Test]
        public void P1d_TwoCatalogsSharingAName_Build()
        {
            var scanned = new List<ContentDirectoryBuilder.Scanned>();
            Assert.That(ContentDirectoryBuilder.Scan(RootA, scanned), Is.True);

            var first = $"{k_Generated}/Probe_dup1_{Guid.NewGuid():N}.asset";
            ContentDirectoryBuilder.Write(ContentDirectoryBuilder.LoadOrCreateCatalog(first), RootA, scanned);
            m_TempAssets.Add(first);

            // The second catalog calls the sheet texture "Icons/single" — the same name as the single texture in the first.
            var second = $"{k_Generated}/Probe_dup2_{Guid.NewGuid():N}.asset";
            var renamed = new List<ContentDirectoryBuilder.Scanned>();
            foreach (var s in scanned)
                if (!s.IsScene && s.Entry.name == "Icons/sheet")
                    renamed.Add(new ContentDirectoryBuilder.Scanned { Path = s.Path, Entry = new ContentCatalog.Entry { name = "Icons/single", id = s.Entry.id } });
            Assert.That(renamed, Has.Count.EqualTo(1));
            ContentDirectoryBuilder.Write(ContentDirectoryBuilder.LoadOrCreateCatalog(second), RootA, renamed);
            m_TempAssets.Add(second);

            Build($"{ProbeFolder}/Dup", new[] { first, second }, BuildCompression.Uncompressed, BuildContentOptions.None);
            Debug.Log("[ContentLink.Tests] built Probe/Dup with two root catalogs both naming 'Icons/single'");
        }

        // ───────────── does a rebuild clean the output folder? ─────────────

        [Test]
        public void P1e_RebuildAfterDeletingAnAsset_OutputFileCount()
        {
            var root = $"{k_Generated}/ProbeRoot";
            if (!AssetDatabase.IsValidFolder(root)) AssetDatabase.CreateFolder(k_Generated, "ProbeRoot");
            m_TempAssets.Add(root);
            AssetDatabase.CopyAsset($"{RootA}/Icons/single.png", $"{root}/one.png");
            AssetDatabase.CopyAsset($"{RootA}/Icons/sheet.png", $"{root}/two.png");
            AssetDatabase.Refresh();

            var output = $"{ProbeFolder}/Cleanup";
            Build(output, new[] { Catalog(root, "clean1") }, BuildCompression.Uncompressed, BuildContentOptions.None);
            int before = Files(output).Length;

            AssetDatabase.DeleteAsset($"{root}/two.png");
            AssetDatabase.Refresh();
            // Same output folder, no manual cleaning: whatever is left over is the engine's doing.
            var p = new BuildContentDirectoryParameters { outputPath = output, rootAssetPaths = new[] { Catalog(root, "clean2") } };
            var report = BuildPipeline.BuildContentDirectory(p);
            Assert.That(report.summary.result, Is.EqualTo(BuildResult.Succeeded));
            int after = Files(output).Length;
            var manifestFiles = Regex.Matches(Manifest(output), "\"IsBuiltIn\":false").Count;
            Debug.Log($"[ContentLink.Tests] files before={before} after deleting one asset and rebuilding into the same folder={after} (manifest lists {manifestFiles} non-builtin serialized files)");
        }
    }
}
