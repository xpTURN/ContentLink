using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// EditMode step that builds the test content directories and leaves them in place for the PlayMode tests.
    /// Run this (or the menu 'Window/ContentLink/Tests/Build Test Content') before the PlayMode suite.
    /// </summary>
    [Category("TestContent")]
    public class TestContentBuildTests
    {
        [Test]
        public void S0_BuildAll_BuildsAllDirectories()
        {
            TestContentBuild.BuildAll();   // throws on catalog or build failure
            foreach (var dir in TestContentBuild.Directories)
                Assert.That(TestContentBuild.IsBuilt(dir), Is.True, $"output missing for '{dir}'");
        }

        /// <summary>
        /// Directory D: two prefabs under the source root use a material and a texture outside it (Assets/TestShared).
        /// The catalog names the prefabs only, and the engine writes the dependencies into the directory anyway.
        /// </summary>
        [Test]
        public void S1_DirectoryD_NamesOnlyWhatIsUnderTheRoot_AndStillCarriesTheOutsideDependencies()
        {
            Assume.That(TestContentBuild.IsBuilt("D"), Is.True, "S0 builds D");

            var catalog = AssetDatabase.LoadAssetAtPath<ContentCatalog>($"{TestContentBuild.GeneratedFolder}/CatalogD.asset");
            Assert.That(catalog, Is.Not.Null, "catalog D");
            Assert.That(catalog.Entries.Select(e => e.name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray(),
                Is.EqualTo(new[] { "Prefabs/OutsideA", "Prefabs/OutsideB" }), "only assets under the source root get a name");

            var manifest = ContentBuildManifest.Read(TestContentBuild.OutputPath("D"), out _);
            Assert.That(manifest, Is.Not.Null, "build manifest of D");

            // Loadables = the named assets plus the root catalog itself (its hash is the one in RootAssets).
            var named = manifest.Loadables.Where(l => !manifest.RootAssets.Contains(l.ObjectIdHash)).ToArray();
            Assert.That(named.Length, Is.EqualTo(2), "named loadables = the two prefabs");

            // The dependency table: each prefab's serialized file depends on a file that holds no loadable at all —
            // written only because something references it — and both prefabs depend on the same one.
            var files = manifest.SerializedFiles.ToDictionary(f => f.Index);
            var loadableFiles = manifest.Loadables.Select(l => l.SerializedFile).ToHashSet();
            int[] Carried(ContentBuildManifest.LoadableData l) => files[l.SerializedFile].SerializedFileDependencies
                .Where(i => !files[i].IsBuiltIn && !loadableFiles.Contains(i)).OrderBy(i => i).ToArray();
            var a = Carried(named[0]);
            var b = Carried(named[1]);
            Debug.Log($"[ContentLink.Tests] directory D: loadables {manifest.Loadables.Length} · content files {manifest.ContentFileCount()} · prefab dependencies [{string.Join(",", a)}] / [{string.Join(",", b)}]");
            Assert.That(a, Is.Not.Empty, "the prefab's file must depend on a file written for the material outside the root");
            Assert.That(b, Is.EqualTo(a), "both prefabs depend on the same file — the dependency is written once");
        }
    }
}
