using System;
using System.IO;
using UnityEngine;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// The manifest BuildContentDirectory writes next to its files: BuildManifestHash.txt names it, and it lists the
    /// serialized files (with their dependencies) and which one holds each loadable. The format is undocumented —
    /// this is what version 1 looks like on 6000.6 — so reading is defensive: a shape we do not recognise leaves
    /// the result without a mapping and never fails the build.
    /// </summary>
    [Serializable]
    public sealed class ContentBuildManifest
    {
        public const int KnownVersion = 1;
        public const string HashFile = "BuildManifestHash.txt";

        [Serializable] public sealed class BuildInfoData { public string BuildName; public bool BuiltWithTypeTrees; }
        [Serializable] public sealed class LoadableData { public string ObjectIdHash; public int SerializedFile; public long Identifier; }
        [Serializable] public sealed class LoadableSceneData { public string GUID; public string Path; public int SerializedFile; }
        [Serializable] public sealed class SerializedFileData { public int Index; public string ID; public bool IsBuiltIn; public int[] SerializedFileDependencies; public string ContentHash; }

        public int Version;
        public BuildInfoData BuildInfo;
        public string[] RootAssets;
        public LoadableData[] Loadables;
        public LoadableSceneData[] LoadableScenes;
        public SerializedFileData[] SerializedFiles;

        /// <summary>Reads the manifest of a build output. Null (with the reason logged) when it is not there or not a shape we know.</summary>
        public static ContentBuildManifest Read(string outputPath, out string manifestPath)
        {
            manifestPath = "";
            var hashFile = Path.Combine(outputPath, HashFile);
            if (!File.Exists(hashFile)) return null;

            var hash = File.ReadAllText(hashFile).Trim();
            var path = Path.Combine(outputPath, hash + ".json");
            if (!File.Exists(path)) return null;   // archive builds keep the manifest inside the archive

            ContentBuildManifest manifest;
            try { manifest = JsonUtility.FromJson<ContentBuildManifest>(File.ReadAllText(path)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[ContentLink] could not parse the build manifest '{path}': {e.Message}. The result has no loadable → file mapping.");
                return null;
            }
            if (manifest == null || manifest.Version != KnownVersion || manifest.SerializedFiles == null)
            {
                Debug.LogWarning($"[ContentLink] build manifest '{path}' is version {manifest?.Version} — this package knows version {KnownVersion}. The result has no loadable → file mapping.");
                return null;
            }
            manifestPath = path.Replace('\\', '/');
            return manifest;
        }

        /// <summary>Serialized files that are not engine built-ins — the ones this build wrote, root catalogs included.</summary>
        public int ContentFileCount()
        {
            int n = 0;
            if (SerializedFiles != null) foreach (var f in SerializedFiles) if (!f.IsBuiltIn) ++n;
            return n;
        }

        // Note: SerializedFiles[i].ID ("<hash>.cfid") is the name other serialized files reference this one by, not
        // its name on disk — none of them matches a "<hash>.cf" in the output folder (measured on 6000.6).
        // SerializedFiles[i].ContentHash is the name on disk: "<ContentHash>.cf" (measured on 6000.6; built-in
        // entries have none). .resS and .resource files are not listed at all.

        /// <summary>The serialized file's name on disk ("&lt;ContentHash&gt;.cf"), or null when the manifest did not say (built-ins, older manifests).</summary>
        public static string FileOf(SerializedFileData file)
            => file == null || string.IsNullOrEmpty(file.ContentHash) ? null : file.ContentHash + ".cf";
    }
}
