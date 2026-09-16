using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>MatchProfile is a pure function of the settings, so its path rules are checked here without Play Mode.</summary>
    [Category("Simulation")]
    public class SimulationMatchTests
    {
        string m_Snapshot;

        [SetUp] public void Snapshot() => m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);

        [TearDown]
        public void Restore()
        {
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
        }

        [Test]
        public void M1_StreamingAssetsPath_MatchesTheRelativeOutputPath_AndCoversTheCategories()
        {
            var p = ContentBuildSettings.instance.Add("MatchX");
            p.SourceRoot = "Assets/TestContent/A";
            p.OutputPath = "Assets/StreamingAssets/ContentLinkTests/MatchX";
            p.Catalogs = new[]
            {
                new ContentBuildSettings.CatalogEntry { SubFolder = "Icons" },
                new ContentBuildSettings.CatalogEntry { SubFolder = "Prefabs/" },
            };

            var d = ContentLinkSimulation.MatchProfile(Application.streamingAssetsPath + "/ContentLinkTests/MatchX");
            Assert.That(d, Is.Not.Null, "the runtime's absolute streamingAssetsPath must match the profile's project-relative output");
            Assert.That(d.Profile, Is.EqualTo("MatchX"));
            Assert.That(d.SourceRoot, Is.EqualTo("Assets/TestContent/A"));
            Assert.That(d.Covered, Is.EqualTo(new[] { "Assets/TestContent/A/Icons", "Assets/TestContent/A/Prefabs" }), "covered folders are the categories as absolute-to-project paths, normalised");

            Assert.That(ContentLinkSimulation.MatchProfile("Assets/StreamingAssets/ContentLinkTests/MatchX/"), Is.Not.Null, "trailing separator");
            Assert.That(ContentLinkSimulation.MatchProfile("./Assets/StreamingAssets/ContentLinkTests/MatchX"), Is.Not.Null, "relative prefix");
            Assert.That(ContentLinkSimulation.MatchProfile("Assets/StreamingAssets/ContentLinkTests/Other"), Is.Null, "no profile has that output");
            Assert.That(ContentLinkSimulation.MatchProfile(""), Is.Null);
        }

        [Test]
        public void M2_AWholeRootCategory_CoversTheRootItself()
        {
            var p = ContentBuildSettings.instance.Add("MatchAll");
            p.SourceRoot = "Assets/TestContent/A";
            p.OutputPath = "Build/Content/MatchAll";
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "" } };

            var d = ContentLinkSimulation.MatchProfile("Build/Content/MatchAll");
            Assert.That(d, Is.Not.Null);
            Assert.That(d.Covered, Is.EqualTo(new[] { "Assets/TestContent/A" }));
        }
    }
}
