using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>Records the import batches while armed — what ContentCatalogImportWatcher receives.</summary>
    public sealed class ImportSignalRecorder : AssetPostprocessor
    {
        public static bool Armed;
        public static readonly List<string> Imported = new();
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (Armed) Imported.AddRange(importedAssets);
        }
    }

    /// <summary>
    /// T65: the engine's signals the editor caches stand on (measured on 6000.6). A refresh that renames a sub-asset or replaces
    /// the main object raises projectChanged — the names and the cached objects follow it; a prefab saved with a component added or
    /// removed raises none, and only the import report names it — the statuses follow that (ContentCatalogImportWatcher). Should the
    /// first stop, the names would need the import report for every asset; should the second start, nothing breaks — so only what
    /// the design needs is pinned.
    /// </summary>
    [Category("EditorUtility")]
    public class ImportSignalTests
    {
        const string k_Root = "Assets/TestContent/Generated/T65";
        int m_Changed;
        void OnChanged() => m_Changed++;

        [SetUp]
        public void Before()
        {
            AssetDatabase.DeleteAsset(k_Root);
            AssetDatabase.CreateFolder("Assets/TestContent/Generated", "T65");
            m_Changed = 0;
            EditorApplication.projectChanged += OnChanged;
        }

        [TearDown]
        public void After()
        {
            EditorApplication.projectChanged -= OnChanged;
            ImportSignalRecorder.Armed = false;
            ImportSignalRecorder.Imported.Clear();
            AssetDatabase.DeleteAsset(k_Root);
        }

        [Test]
        public void T65_ASubAssetRenamedOrTheMainObjectReplacedRaisesProjectChanged_APrefabSavedIsReportedByTheImport()
        {
            var sheet = $"{k_Root}/sheet.png";
            Assert.That(AssetDatabase.CopyAsset("Assets/TestContent/F/Icons/fsheet.png", sheet), Is.True);
            AssetDatabase.Refresh();
            int quiet = m_Changed;
            AssetDatabase.Refresh();
            Assert.That(m_Changed, Is.EqualTo(quiet), "control: a refresh with nothing changed raises nothing — what follows is the change's own");
            var meta = File.ReadAllText(sheet + ".meta");
            Assert.That(meta, Does.Contain("name: fs0\n"), "sanity: the slice as fsheet.png.meta stores it");
            File.WriteAllText(sheet + ".meta", meta.Replace("name: fs0\n", "name: hero\n").Replace("      fs0: ", "      hero: "));
            int before = m_Changed;
            AssetDatabase.Refresh();
            var names = AssetDatabase.LoadAllAssetRepresentationsAtPath(sheet).OfType<Sprite>().Select(s => s.name).ToList();
            Assert.That(names, Does.Contain("hero").And.Not.Contain("fs0"), "sanity: the refresh renamed the slice");
            Assert.That(m_Changed, Is.GreaterThan(before), "a slice renamed on disk and refreshed raises projectChanged — the caches' names rest on it");

            var replaced = $"{k_Root}/q.prefab";
            Assert.That(AssetDatabase.CopyAsset("Assets/TestContent/A/Prefabs/Plain.prefab", replaced), Is.True);
            File.WriteAllText(replaced, File.ReadAllText("Assets/TestContent/A/Prefabs/Linked.prefab"));   // another prefab's file ids: the main object is replaced
            before = m_Changed;
            AssetDatabase.Refresh();
            Assert.That(m_Changed, Is.GreaterThan(before), "a prefab replaced on disk raises projectChanged — the cached objects rest on it");

            var prefab = $"{k_Root}/p.prefab";
            Assert.That(AssetDatabase.CopyAsset("Assets/TestContent/A/Prefabs/Plain.prefab", prefab), Is.True);
            var root = PrefabUtility.LoadPrefabContents(prefab);
            try
            {
                root.AddComponent<BoxCollider>();
                ImportSignalRecorder.Imported.Clear();
                ImportSignalRecorder.Armed = true;
                PrefabUtility.SaveAsPrefabAsset(root, prefab);
                ImportSignalRecorder.Armed = false;
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            Assert.That(ImportSignalRecorder.Imported, Does.Contain(prefab), "a prefab saved is reported by the import — the statuses rest on it");
        }
    }
}
