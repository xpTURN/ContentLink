using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// A build carries as load targets what its catalogs name and, with no name, whatever its content
    /// holds the id of — a catalog it holds brings every name of it (measured on 6000.6). One that another catalog names fails the build;
    /// one that no catalog names is a warning. A catalog saved under another extension drops the editor's caches when it is saved again.
    /// </summary>
    [Category("Build")]
    public class LoadTargetCheckTests
    {
        const string k_Fx = "Assets/TestContent/Generated/T714";
        const string k_Out = "Assets/StreamingAssets/ContentLinkTests/LoadTargets";
        static readonly Type s_Holder = Type.GetType("xpTURN.ContentLink.Tests.LoadTargetHolder, xpTURN.ContentLink.Tests", true);   // the runtime test assembly: no reference from here
        string m_Snapshot;
        ContentCatalog m_Other;
        Texture2D m_Big, m_Free;

        [SetUp]
        public void Before()
        {
            m_Snapshot = EditorJsonUtility.ToJson(ContentBuildSettings.instance);
            AssetDatabase.DeleteAsset(k_Fx);
            if (Directory.Exists(k_Out)) { Directory.Delete(k_Out, true); File.Delete(k_Out + ".meta"); }
            foreach (var f in new[] { "O/Scenes", "Lib", "C", "Cat" }) Folder($"{k_Fx}/{f}");
            Png($"{k_Fx}/O/big.png", 64, 1); Png($"{k_Fx}/Lib/free.png", 16, 2);
            File.WriteAllText($"{k_Fx}/O/Scenes/os.unity", "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"); AssetDatabase.ImportAsset($"{k_Fx}/O/Scenes/os.unity");
            m_Other = Catalog($"{k_Fx}/Cat/O.asset", $"{k_Fx}/O");   // another profile's: big and the scene
            m_Big = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Fx}/O/big.png");
            m_Free = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Fx}/Lib/free.png");
        }

        [TearDown]
        public void After()
        {
            EditorJsonUtility.FromJsonOverwrite(m_Snapshot, ContentBuildSettings.instance);
            ContentBuildSettings.instance.MarkDirty();
            ContentDirectoryBuilder.ClearOverrides();
            AssetDatabase.DeleteAsset(k_Fx);
            if (Directory.Exists(k_Out)) { Directory.Delete(k_Out, true); File.Delete(k_Out + ".meta"); }
            AssetDatabase.Refresh();
            ContentLinkEditorUtility.Invalidate();
        }

        static void Folder(string path) { if (AssetDatabase.IsValidFolder(path)) return; int i = path.LastIndexOf('/'); Folder(path.Substring(0, i)); AssetDatabase.CreateFolder(path.Substring(0, i), path.Substring(i + 1)); }
        static void Png(string path, int size, int seed)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false); var rnd = new System.Random(seed); var px = new Color32[size * size];
            for (int i = 0; i < px.Length; ++i) px[i] = new Color32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
            tex.SetPixels32(px); tex.Apply(); File.WriteAllBytes(path, tex.EncodeToPNG()); Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
        static ContentCatalog Catalog(string path, string root)
        {
            var catalog = ContentDirectoryBuilder.LoadOrCreateCatalog(path);
            var items = new List<ContentDirectoryBuilder.Scanned>();
            Assert.That(ContentDirectoryBuilder.Scan(root, ContentNameStyle.PathWithoutExtension, items), Is.True);
            ContentDirectoryBuilder.Write(catalog, root, ContentNameStyle.PathWithoutExtension, items);
            return catalog;
        }
        void Holder(Action<ScriptableObject> fill)
        {
            var h = ScriptableObject.CreateInstance(s_Holder); fill(h);
            AssetDatabase.CreateAsset(h, $"{k_Fx}/C/h.asset"); AssetDatabase.SaveAssets();
        }
        static void Set(Object o, string field, object value) => o.GetType().GetField(field).SetValue(o, value);
        static ContentBuildSettings.Profile Profile(string name, ContentBuildSettings.ArchiveMode mode = ContentBuildSettings.ArchiveMode.None)
        {
            var settings = ContentBuildSettings.instance; settings.Remove(name);
            var p = settings.Add(name);
            p.SourceRoot = $"{k_Fx}/C"; p.OutputPath = $"{k_Out}/{name}"; p.Archive = mode;
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "", CreateAt = $"{k_Fx}/Cat/{name}.asset" } };
            return p;
        }
        static readonly Regex k_Failed = new("build of 'T714.*' failed");

        [Test]
        public void L1_ContentThatHoldsACatalog_FailsTheBuild_NamingWhatItCarries()
        {
            Holder(h => Set(h, "catalog", m_Other));
            LogAssert.Expect(LogType.Error, k_Failed);
            var r = ContentBuildRunner.Build(Profile("T714a"), showProgress: false);
            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.Errors, Has.Count.EqualTo(1));
            StringAssert.Contains($"this build carries 1 asset(s) and 1 scene(s) that '{k_Fx}/Cat/O.asset' names", r.Errors[0]);
            StringAssert.Contains($"— scene 'Scenes/os' ({k_Fx}/O/Scenes/os.unity), 'big' ({k_Fx}/O/big.png): ", r.Errors[0]);
            Assert.That(File.Exists(r.ManifestPath) && Directory.GetFiles(r.OutputPath, "*.cf").Length > 0, Is.True, "the output stays for inspection");
        }

        [Test]
        public void L2_AnIdAnotherCatalogNames_FailsAPackedBuildBeforeItIsPacked()
        {
            Holder(h => Set(h, "id", LoadableObjectIdEditorUtility.CreateLoadableObjectId(m_Big)));
            LogAssert.Expect(LogType.Error, k_Failed);
            var r = ContentBuildRunner.Build(Profile("T714b", ContentBuildSettings.ArchiveMode.Packed), showProgress: false);
            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.Errors, Has.Count.EqualTo(1));
            StringAssert.Contains($"this build carries 1 asset(s) that '{k_Fx}/Cat/O.asset' names", r.Errors[0]);
            StringAssert.Contains($"— 'big' ({k_Fx}/O/big.png): ", r.Errors[0]);
            Assert.That(r.Archives, Is.Empty);
            Assert.That(File.Exists($"{k_Out}/T714b/{ContentArchives.IndexFile}"), Is.False);
        }

        [Test]
        public void L3_ASceneIdAnotherCatalogNames_FailsTheBuild()
        {
            Holder(h => Set(h, "sceneId", LoadableSceneIdEditorUtility.CreateLoadableSceneId($"{k_Fx}/O/Scenes/os.unity")));
            LogAssert.Expect(LogType.Error, k_Failed);
            var r = ContentBuildRunner.Build(Profile("T714c"), showProgress: false);
            Assert.That(r.Succeeded, Is.False);
            Assert.That(r.Errors, Has.Count.EqualTo(1));
            StringAssert.Contains($"this build carries 1 scene(s) that '{k_Fx}/Cat/O.asset' names", r.Errors[0]);
        }

        [Test]
        public void L4_AnIdNoCatalogNames_BuildsWithAWarning()
        {
            Holder(h => Set(h, "id", LoadableObjectIdEditorUtility.CreateLoadableObjectId(m_Free)));
            LogAssert.Expect(LogType.Warning, new Regex(@"holds the LoadableObjectId or LoadableSceneId of 1 load target\(s\) no catalog of this project names as it is saved now — oid-[0-9a-f]{32}: "));
            var r = ContentBuildRunner.Build(Profile("T714d"), showProgress: false);
            Assert.That(r.Succeeded, Is.True, string.Join(" | ", r.Errors));
        }

        [Test]
        public void L5_AReferenceOrAnIdTheProfileNames_BuildsQuietly()
        {
            Png($"{k_Fx}/C/own.png", 16, 3);
            File.WriteAllText($"{k_Fx}/C/stage.unity", "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n"); AssetDatabase.ImportAsset($"{k_Fx}/C/stage.unity");   // the profile's own scene: named, not reported
            var own = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Fx}/C/own.png");
            Holder(h => { Set(h, "reference", new ContentRef<Texture2D>(AssetDatabase.AssetPathToGUID($"{k_Fx}/O/big.png"))); Set(h, "id", LoadableObjectIdEditorUtility.CreateLoadableObjectId(own)); });
            var warnings = new List<string>();
            void Listen(string message, string stack, LogType type) { if (type == LogType.Warning) warnings.Add(message); }
            Application.logMessageReceived += Listen;
            ContentBuildResult r;
            try { r = ContentBuildRunner.Build(Profile("T714e"), showProgress: false); }
            finally { Application.logMessageReceived -= Listen; }
            Assert.That(r.Succeeded, Is.True, string.Join(" | ", r.Errors));
            Assert.That(r.Manifest.LoadableScenes, Has.Length.EqualTo(1), "the profile's own scene is a load target");
            Assert.That(warnings, Is.Empty);
        }

        [Test]
        public void L6_AnEngineArchive_IsNotChecked()
        {
            Holder(h => Set(h, "id", LoadableObjectIdEditorUtility.CreateLoadableObjectId(m_Big)));
            var logs = new List<string>();
            void Listen(string message, string stack, LogType type) { if (type == LogType.Log) logs.Add(message); }
            Application.logMessageReceived += Listen;
            ContentBuildResult r;
            try { r = ContentBuildRunner.Build(Profile("T714f", ContentBuildSettings.ArchiveMode.Engine), showProgress: false); }
            finally { Application.logMessageReceived -= Listen; }
            Assert.That(r.Succeeded, Is.True, "its manifest is inside the archive");
            Assert.That(r.Manifest, Is.Null);
            Assert.That(logs.Exists(l => l.Contains("built 'T714f'") && l.Contains(", Engine — load targets not checked: the manifest is inside the archive)")), Is.True, "the summary says so");
        }

        [Test]
        public void L7_TheStaticBuild_RefusesItToo()
        {
            Holder(h => Set(h, "id", LoadableObjectIdEditorUtility.CreateLoadableObjectId(m_Big)));
            ContentDirectoryBuilder.SourceRoot = $"{k_Fx}/C";
            ContentDirectoryBuilder.CatalogPath = $"{k_Fx}/Cat/static.asset";
            ContentDirectoryBuilder.OutputPath = $"{k_Out}/static";
            LogAssert.Expect(LogType.Error, new Regex($"this build carries 1 asset\\(s\\) that '{k_Fx}/Cat/O.asset' names"));
            Assert.That(ContentDirectoryBuilder.Build(), Is.Null);
        }

        [TestCase("preset")]
        [TestCase("anim")]
        public void W1_ACatalogSavedUnderAnotherExtension_DropsTheEditorsCaches(string extension)
        {
            var generation = typeof(ContentLinkEditorUtility).GetProperty("Generation", BindingFlags.NonPublic | BindingFlags.Static);
            Folder($"{k_Fx}/W"); Folder($"{k_Fx}/W2"); Png($"{k_Fx}/W2/w.png", 8, 4);
            var catalog = ScriptableObject.CreateInstance<ContentCatalog>();
            catalog.EditorSetSourceRoot($"{k_Fx}/W");
            AssetDatabase.CreateAsset(catalog, $"{k_Fx}/Cat/W.{extension}"); AssetDatabase.SaveAssets();
            ContentLinkEditorUtility.Invalidate();
            var w = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Fx}/W2/w.png");
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(w, out _, out _), Is.False, "W2 is no root yet");
            int before = (int)generation.GetValue(null);
            catalog.EditorSetSourceRoot($"{k_Fx}/W2");   // saved by another tool: no Write, no Invalidate
            EditorUtility.SetDirty(catalog); AssetDatabase.SaveAssets();
            Assert.That((int)generation.GetValue(null), Is.GreaterThan(before));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(w, out var name, out var why), Is.True, why);
            Assert.That(name, Is.EqualTo("w"));
        }
    }
}
