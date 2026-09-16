using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>What one profile build produced. Handed to post-processors and kept as the last result (SessionState).</summary>
    public sealed class ContentBuildResult
    {
        public string ProfileName = "";
        public BuildTarget Target;
        public string OutputPath = "";
        public bool Succeeded;
        public readonly List<string> Errors = new();
        public double Milliseconds;

        /// <summary>The engine's report; null when validation stopped the build before the engine ran.</summary>
        public BuildReport Report;

        /// <summary>The manifest file inside the output, or "" when it was not found (archive builds keep it inside the archive).</summary>
        public string ManifestPath = "";
        public ContentBuildManifest Manifest;

        /// <summary>Every file in the output, relative to <see cref="OutputPath"/>, sorted ordinal — the order post-processors see.</summary>
        public string[] Files = Array.Empty<string>();

        /// <summary>Loadable names whose ids the manifest lists — every catalog entry the engine actually built. Sorted ordinal.</summary>
        public readonly List<string> BuiltNames = new();

        /// <summary>
        /// The same entries with their catalog and, where the manifest's ContentHash says, the serialized file on
        /// disk that holds them (a flat build's "&lt;hash&gt;.cf"; null when the manifest did not say). What a
        /// custom <see cref="IContentArchivePartitioner"/> groups by. Sorted by catalog, then name.
        /// </summary>
        public readonly List<LoadableInfo> Loadables = new();

        /// <summary>Archives written by the packer (Packed) or the engine (Engine), relative to <see cref="OutputPath"/>.</summary>
        public string[] Archives = Array.Empty<string>();
    }

    /// <summary>
    /// A custom step after a profile has been built. Implementations are listed by type name in the
    /// profile and run in that order; an exception fails the build but leaves the output for inspection.
    /// </summary>
    public interface IContentBuildPostProcess
    {
        void OnBuilt(ContentBuildResult result);
    }
}
