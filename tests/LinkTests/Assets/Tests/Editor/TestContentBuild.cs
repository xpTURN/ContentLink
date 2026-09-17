using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Builds the test content directories (TestContent/A, B, C, D) into StreamingAssets.
    ///
    /// The build is an EditMode step (menu or <see cref="TestContentBuildTests"/>) rather than part of every PlayMode run:
    /// it takes seconds, triggers a player script compilation, and the output can stay in place between runs.
    /// The runtime test assembly references this class by name for validation only:
    /// [PrebuildSetup("xpTURN.ContentLink.Tests.Editor.TestContentBuild")].
    /// </summary>
    public sealed class TestContentBuild : IPrebuildSetup
    {
        public const string SourceRootBase = "Assets/TestContent";
        public const string GeneratedFolder = "Assets/TestContent/Generated";
        public const string OutputFolder = "Assets/StreamingAssets/ContentLinkTests";
        public const string BuildMenu = "Window/ContentLink/Tests/Build Test Content";
        public static readonly string[] Directories = { "A", "B", "C", "D" };   // C = scenes · D = prefabs with dependencies outside the source root

        public static string OutputPath(string dir) => $"{OutputFolder}/{dir}";
        public static bool IsBuilt(string dir) => Directory.Exists(OutputPath(dir));

        /// <summary>Validation only — the content must already be built (see <see cref="BuildAll"/>).</summary>
        public void Setup()
        {
            foreach (var dir in Directories)
                if (!IsBuilt(dir))
                    throw new InvalidOperationException($"[ContentLink.Tests] test content '{dir}' is not built. Run '{BuildMenu}' or the EditMode test TestContentBuildTests first.");
        }

        [MenuItem(BuildMenu)]
        public static void BuildAll()
        {
            var target = EditorUserBuildSettings.activeBuildTarget;
            if (target != BuildTarget.StandaloneOSX && target != BuildTarget.StandaloneWindows64 && target != BuildTarget.StandaloneLinux64)
                Debug.LogWarning($"[ContentLink.Tests] active build target is {target}; content directories are built per target and may not load in the editor.");

            EnsureFolder("Assets/TestContent", "Generated");
            EnsureFolder("Assets", "StreamingAssets");
            EnsureFolder("Assets/StreamingAssets", "ContentLinkTests");

            string sourceRoot = ContentDirectoryBuilder.SourceRoot;
            string catalogPath = ContentDirectoryBuilder.CatalogPath;
            string outputPath = ContentDirectoryBuilder.OutputPath;
            try
            {
                foreach (var dir in Directories)
                {
                    ContentDirectoryBuilder.SourceRoot = $"{SourceRootBase}/{dir}";
                    ContentDirectoryBuilder.CatalogPath = $"{GeneratedFolder}/Catalog{dir}.asset";
                    ContentDirectoryBuilder.OutputPath = OutputPath(dir);

                    var report = ContentDirectoryBuilder.Build();
                    if (report == null)
                        throw new InvalidOperationException($"[ContentLink.Tests] catalog update failed for '{dir}' (see the errors above).");
                    if (report.summary.result != BuildResult.Succeeded)
                        throw new InvalidOperationException($"[ContentLink.Tests] content build failed for '{dir}': {report.summary.result}, {report.summary.totalErrors} error(s).");
                    Debug.Log($"[ContentLink.Tests] built '{dir}' -> {ContentDirectoryBuilder.OutputPath} ({report.summary.result}, target {target})");
                }
            }
            finally
            {
                ContentDirectoryBuilder.SourceRoot = sourceRoot;
                ContentDirectoryBuilder.CatalogPath = catalogPath;
                ContentDirectoryBuilder.OutputPath = outputPath;
            }
            AssetDatabase.Refresh();
        }

        [MenuItem("Window/ContentLink/Tests/Delete Test Content")]
        public static void DeleteAll()
        {
            AssetDatabase.DeleteAsset(OutputFolder);
            AssetDatabase.DeleteAsset(GeneratedFolder);
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}")) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
