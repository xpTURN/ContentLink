using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Loading;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Build half of the catalog-size measurement (it records the numbers and never fails on them): two directories whose only root is a 10 000-entry
    /// catalog, one with GUIDs and one without, so the bytes a GUID adds per entry can be read off the build output and
    /// the PlayMode half (ContentRefMeasureTests) can time the first reference lookup. Every entry points at the same
    /// texture — the catalog is what is measured. The catalog assets are deleted after the build so they do not join
    /// the project catalogs every other editor test indexes.
    /// </summary>
    [Category("Measure")]
    public class ContentRefMeasureTests
    {
        public const int Entries = 10000;
        const string k_Generated = "Assets/TestContent/Generated";
        const string k_Output = "Assets/StreamingAssets/ContentLinkTests/Runner";

        [Test]
        public void U4_TenThousandEntryCatalogs_WithAndWithoutGuids_Recorded()
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TestContent/A/Icons/single.png");
            Assert.That(texture, Is.Not.Null);
            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(texture);

            long withGuids = Build("Synth10k", id, true);
            long withoutGuids = Build("Synth10kNoGuid", id, false);
            Debug.Log($"[ContentLink.Tests] {Entries} catalog entries — built files {withGuids} B with GUIDs, {withoutGuids} B without → {(withGuids - withoutGuids) / (double)Entries:F1} B per entry");
        }

        static long Build(string name, LoadableObjectId id, bool guids)
        {
            if (!AssetDatabase.IsValidFolder(k_Generated)) AssetDatabase.CreateFolder("Assets/TestContent", "Generated");
            var catalogPath = $"{k_Generated}/{name}.asset";
            try
            {
                var catalog = ContentDirectoryBuilder.LoadOrCreateCatalog(catalogPath);
                var entries = new ContentCatalog.Entry[Entries];
                for (int i = 0; i < Entries; ++i)
                    entries[i] = new ContentCatalog.Entry { name = "Synth/item" + i.ToString("D5"), id = id, guid = guids ? i.ToString("x32") : "", sub = "" };
                catalog.EditorSetEntries(entries);
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();

                var output = $"{k_Output}/{name}";
                var report = BuildPipeline.BuildContentDirectory(new BuildContentDirectoryParameters { outputPath = output, rootAssetPaths = new[] { catalogPath } });
                Assert.That(report.summary.result, Is.EqualTo(BuildResult.Succeeded), $"{name}: {report.summary.totalErrors} error(s)");
                return Directory.GetFiles(output, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".meta")).Sum(f => new FileInfo(f).Length);
            }
            finally
            {
                AssetDatabase.DeleteAsset(catalogPath);
                ContentLinkEditorUtility.Invalidate();
            }
        }
    }
}
