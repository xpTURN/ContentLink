using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>The command-line entry point: its argument parsing, the error paths of a run and what a run leaves behind. One test builds (C6).</summary>
    [Category("Build")]
    public class ContentBuildCLITests
    {
        const string k_File = "ProjectSettings/ContentLinkBuild.asset";
        string m_Snapshot;

        [SetUp]
        public void Snapshot() => m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);

        [TearDown]
        public void Restore()
        {
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            ContentLinkEditorUtility.Invalidate();
        }

        [Test]
        public void C1_ProfileAll_IgnoresCase_LikeTheKeys()
        {
            // The keys are matched ignoring case; the special value 'all' must be too, or "-contentlink.profile All"
            // looks for a profile called 'All'.
            Assert.That(ContentBuildCLI.Parse(new[] { "-contentlink.profile", "All" }, out var options, out var error), Is.True, error);
            Assert.That(options.Profile, Is.EqualTo(ContentBuildCLI.All));
            Assert.That(ContentBuildCLI.Parse(new[] { "-CONTENTLINK.PROFILE", "main" }, out options, out error), Is.True, error);
            Assert.That(options.Profile, Is.EqualTo("main"), "an ordinary profile name keeps its case");
        }

        /// <summary>T27: the name style — its name in any case; a value it does not have is refused with the ones it has.</summary>
        [Test]
        public void C2_NameStyle_IsParsedAndApplied_AndAnUnknownValueIsRefused()
        {
            Assert.That(ContentBuildCLI.Parse(new[] { "-contentlink.NAMESTYLE", "pathwithextension" }, out var options, out var error), Is.True, error);
            var profile = new ContentBuildSettings.Profile();
            Assert.That(ContentBuildCLI.Apply(profile, options.Overrides, out error), Is.True, error);
            Assert.That(profile.NameStyle, Is.EqualTo(ContentNameStyle.PathWithExtension));
            Assert.That(ContentBuildCLI.Apply(profile, new Dictionary<string, string> { ["nameStyle"] = "2" }, out error), Is.False);
            Assert.That(error, Does.Contain("PathWithoutExtension | PathWithExtension"));
        }

        /// <summary>A value left out — the next option of this command line is not taken in its place; a value that only begins with '-' stays one.</summary>
        [Test]
        public void C3_AnOptionInPlaceOfAValue_IsRefused()
        {
            Assert.That(ContentBuildCLI.Parse(new[] { "-contentlink.output", "-contentlink.profile", "Main" }, out _, out var error), Is.False);
            Assert.That(error, Is.EqualTo("-contentlink.output needs a value, not the option -contentlink.profile"));
            Assert.That(ContentBuildCLI.Parse(new[] { "-contentlink.output", "-CONTENTLINK.Profile", "Main" }, out _, out error), Is.False, "in any case, as the keys are matched");
            Assert.That(ContentBuildCLI.Parse(new[] { "-contentlink.contentVersion", "-rc1" }, out var options, out error), Is.True, error);
            Assert.That(options.ContentVersion, Is.EqualTo("-rc1"), "a value that begins with '-' is a value (tools/build-content.sh passes it — IMP018 Plan-F14 B1f)");
        }

        /// <summary>A mode by a number it does not have is refused; a post-processor named with its assembly — as the window stores it — is one name.</summary>
        [Test]
        public void C4_ModesByNumber_AndPostProcessorsWithTheirAssembly()
        {
            var profile = new ContentBuildSettings.Profile();
            Assert.That(ContentBuildCLI.Apply(profile, new Dictionary<string, string> { ["archive"] = "7" }, out var error), Is.False);
            Assert.That(error, Does.Contain("None | Engine | Packed"));
            Assert.That(ContentBuildCLI.Apply(profile, new Dictionary<string, string> { ["compression"] = "9" }, out error), Is.False);
            Assert.That(error, Does.Contain("Uncompressed | LZ4 | LZMA"));
            Assert.That(ContentBuildCLI.Apply(profile, new Dictionary<string, string> { ["archive"] = "1" }, out error), Is.True, error);
            Assert.That(profile.Archive, Is.EqualTo(ContentBuildSettings.ArchiveMode.Engine), "a number the mode has is still taken");

            var a = ContentBuildWindowModel.NameOf(typeof(ContentArchivePackerTests.RecordA));
            var b = typeof(ContentArchivePackerTests.RecordB).FullName;
            Assert.That(ContentBuildCLI.Apply(profile, new Dictionary<string, string> { ["postProcessors"] = $"{a}, {b}" }, out error), Is.True, error);
            Assert.That(profile.PostProcessors, Is.EqualTo(new[] { a, b }), "'Namespace.Type, Assembly' is one name; a full name beside it is another");
        }

        /// <summary>The error paths of a run in the real editor — each exits 1 with one error and changes nothing: no profile, no settings file, no output.</summary>
        [Test]
        public void C5_ARefusedRun_ExitsOne_AndChangesNothing()
        {
            bool existed = File.Exists(k_File);
            var stamp = existed ? File.GetLastWriteTimeUtc(k_File) : default;
            var settings = ContentBuildSettings.instance;
            var active = settings.ActiveName;   // a project without settings gets its Main here, before the snapshot
            var before = EditorJsonUtility.ToJson(settings);
            string[][] runs =
            {
                new[] { "-contentlink.colour", "red" },
                new[] { "-contentlink.profile", "Nope" },
                new[] { "-contentlink.profile", "all", "-contentlink.output", "Build/Never" },
                new[] { "-contentlink.output", "Build/Never", "-contentlink.archive", "zip" },
                new[] { "-contentlink.output", "-contentlink.profile", active },
            };
            foreach (var args in runs)
            {
                LogAssert.Expect(LogType.Error, new Regex(@"^\[ContentLink CLI\] FAILED · "));
                Assert.That(ContentBuildCLI.Run(args), Is.EqualTo(1), string.Join(" ", args));
            }
            Assert.That(EditorJsonUtility.ToJson(settings), Is.EqualTo(before), "the profiles are as they were — a value applied before the refused one too");
            Assert.That(File.Exists(k_File), Is.EqualTo(existed), "no settings file");
            if (existed) Assert.That(File.GetLastWriteTimeUtc(k_File), Is.EqualTo(stamp), "no settings written");
            Assert.That(Directory.Exists("Build/Never") || Directory.Exists("-contentlink.profile"), Is.False, "nothing built");
        }

        /// <summary>A run's overrides are the run's — the build takes them and the profile is put back after it (one real build, outside Assets/).</summary>
        [Test]
        public void C6_ARunsOverrides_AreGoneAfterIt()
        {
            const string output = "Build/ContentLinkTests/CliOverrides";
            var catalog = $"Assets/TestContent/Generated/CliOverrides_{System.Guid.NewGuid():N}.asset";
            var p = ContentBuildSettings.instance.Add("CliOverrides");
            p.SourceRoot = ContentBuildRunnerTests.RootA;
            p.Archive = ContentBuildSettings.ArchiveMode.None;
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = catalog } };
            var outputBefore = p.OutputPath;
            try
            {
                Assert.That(ContentBuildCLI.Run(new[] { "-contentlink.profile", p.Name, "-contentlink.output", output, "-contentlink.archive", "Packed", "-contentlink.maxArchiveMB", "3" }), Is.EqualTo(0));
                Assert.That(File.Exists(Path.Combine(output, ContentArchives.IndexFile)), Is.True, "the build took the overrides");
                Assert.That(p.OutputPath, Is.EqualTo(outputBefore), "the output is the profile's again");
                Assert.That(p.Archive, Is.EqualTo(ContentBuildSettings.ArchiveMode.None));
                Assert.That(p.MaxArchiveMB, Is.EqualTo(20));
            }
            finally
            {
                if (Directory.Exists(output)) Directory.Delete(output, true);
                AssetDatabase.DeleteAsset(catalog);
            }
        }
    }
}
