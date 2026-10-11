using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.U2D;
using xpTURN.ContentLink.Editor;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Sprite atlases on the real engine — what the builder names, refuses and warns about, and what a dropped atlas
    /// offers. The fixture is TestContent/F: Atlas/FAtlas packs Icons/fa, Icons/fsheet (fs0 · fs1) and TestShared/Atlas/fout,
    /// a texture outside the root. What a test makes lies in F/SimTemp (gitignored) and is deleted at both ends.
    /// </summary>
    [Category("Atlas")]
    public class AtlasSpriteEditorTests
    {
        const string k_Root = "Assets/TestContent/F";
        const string k_Atlas = k_Root + "/Atlas/FAtlas.spriteatlasv2";
        const string k_Temp = k_Root + "/SimTemp";

        SpritePackerMode m_Mode;

        [SetUp]
        public void Before()
        {
            m_Mode = EditorSettings.spritePackerMode;
            AssetDatabase.DeleteAsset(k_Temp);
            ContentDirectoryBuilder.SourceRoot = k_Root;   // the root is known to the editor even before the test content is built
            ContentLinkEditorUtility.Invalidate();
        }

        [TearDown]
        public void After()
        {
            if (EditorSettings.spritePackerMode != m_Mode) EditorSettings.spritePackerMode = m_Mode;
            AssetDatabase.DeleteAsset(k_Temp);
            ContentDirectoryBuilder.ClearOverrides();
            ContentLinkEditorUtility.Invalidate();
        }

        static List<ContentDirectoryBuilder.Scanned> Scan(out bool ok)
        {
            var items = new List<ContentDirectoryBuilder.Scanned>();
            ok = ContentDirectoryBuilder.Scan(k_Root, ContentNameStyle.PathWithoutExtension, items);
            return items;
        }

        /// <summary>A 16-pixel sprite texture at a path (imported as one).</summary>
        static Texture2D SpritePng(string path)
        {
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false);
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        static bool TryEntry(List<ContentDirectoryBuilder.Scanned> items, string name, out ContentCatalog.Entry entry)
        {
            foreach (var i in items)
                if (!i.IsScene && i.Entry.name == name) { entry = i.Entry; return true; }
            entry = default;
            return false;
        }

        static Sprite SpriteAt(string path, string name) => AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<Sprite>().First(s => s.name == name);

        static void MakeAtlas(string path, params Object[] packables)
        {
            if (!AssetDatabase.IsValidFolder(k_Temp)) AssetDatabase.CreateFolder(k_Root, "SimTemp");
            var atlas = new SpriteAtlasAsset();
            atlas.Add(packables);
            SpriteAtlasAsset.Save(atlas, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        // The drop helpers are internal: the drawer is their only caller
        static object Utility(string method, params object[] args)
            => typeof(ContentLinkEditorUtility).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        static string Choices(Object dropped, System.Type field) => string.Join(",", ((Sprite[])Utility("AtlasChoices", dropped, field)).Select(s => s.name));

        /// <summary>T15: the atlas's name for each sprite it packs — the sprite's own id, the atlas GUID and the sprite name; a texture outside the root gets that name only; a variant names nothing and says so.</summary>
        [Test]
        public void AS15_TheAtlasNamesTheSpritesItPacks()
        {
            var items = Scan(out bool ok);
            var names = items.Where(i => !i.IsScene && i.Entry.name.StartsWith("Atlas/FAtlas[")).Select(i => i.Entry.name).OrderBy(n => n, System.StringComparer.Ordinal);
            Assert.That(ok, Is.True);
            Assert.That(names, Is.EqualTo(new[] { "Atlas/FAtlas[fa]", "Atlas/FAtlas[fout]", "Atlas/FAtlas[fs0]", "Atlas/FAtlas[fs1]" }));
            bool hasOwn = TryEntry(items, "Icons/fa[fa]", out var own);
            bool hasAlias = TryEntry(items, "Atlas/FAtlas[fa]", out var alias);
            Assert.That(hasOwn && hasAlias, Is.True);
            Assert.That(alias.id, Is.EqualTo(own.id), "the atlas's name carries the sprite's own id");
            Assert.That(alias.guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(k_Atlas)));
            Assert.That(alias.sub, Is.EqualTo("fa"));
            Assert.That(items.Any(i => !i.IsScene && i.Entry.name.EndsWith("[fout]") && i.Entry.name != "Atlas/FAtlas[fout]"), Is.False,
                "a texture outside the root has the atlas's name only");

            var variant = new SpriteAtlasAsset();
            variant.SetIsVariant(true);
            variant.SetMasterAtlas(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(k_Atlas));
            if (!AssetDatabase.IsValidFolder(k_Temp)) AssetDatabase.CreateFolder(k_Root, "SimTemp");
            SpriteAtlasAsset.Save(variant, $"{k_Temp}/FVariant.spriteatlasv2");
            AssetDatabase.ImportAsset($"{k_Temp}/FVariant.spriteatlasv2", ImportAssetOptions.ForceSynchronousImport);
            LogAssert.Expect(LogType.Warning, new Regex("is a variant atlas: it names no sprites"));
            items = Scan(out ok);
            Assert.That(ok, Is.True);
            Assert.That(items.Any(i => !i.IsScene && i.Entry.name.StartsWith("SimTemp/FVariant[")), Is.False, "a variant names no sprite");
        }

        /// <summary>T17 (the part a batch run can reach — a drop itself is checked by hand): what an atlas dropped on a field offers, and what a pick writes.</summary>
        [Test]
        public void AS17_ADroppedAtlasOffersWhatItPacks()
        {
            var atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(k_Atlas);
            Assert.That(Choices(atlas, typeof(Sprite)), Is.EqualTo("fa,fs0,fs1,fout"));
            Assert.That(Choices(atlas, typeof(Object)), Is.Empty, "an Object field takes the atlas itself");
            Assert.That(Choices(atlas, typeof(Texture2D)), Is.Empty);
            Assert.That(Choices(SpriteAt($"{k_Root}/Icons/fa.png", "fa"), typeof(Sprite)), Is.Empty, "a sprite is no atlas");

            var fa = SpriteAt($"{k_Root}/Icons/fa.png", "fa");
            var linkArgs = new object[] { atlas, fa, null, null };
            var refArgs = new object[] { atlas, fa, null, null };
            Assert.That((bool)Utility("TryGetLinkForAtlasSprite", linkArgs), Is.True);
            Assert.That((bool)Utility("TryGetRefForAtlasSprite", refArgs), Is.True);
            Assert.That(linkArgs[2], Is.EqualTo("Atlas/FAtlas")); Assert.That(linkArgs[3], Is.EqualTo("fa"));
            Assert.That(refArgs[2], Is.EqualTo(AssetDatabase.AssetPathToGUID(k_Atlas))); Assert.That(refArgs[3], Is.EqualTo("fa"));
        }

        /// <summary>T21: the packer mode and a V1 atlas, on the real engine — a build that would load sprites without a texture is refused.</summary>
        [Test]
        public void AS21_ThePackerModeAndAV1Atlas_RefuseTheBuild()
        {
            // Switching the packer mode, and a V1 atlas in a batch run, make the engine log on its own: the refusals are read from the log
            LogAssert.ignoreFailingMessages = true;
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type) { if (type == LogType.Error && message.StartsWith("[ContentLink]")) errors.Add(message); }
            Application.logMessageReceived += OnLog;
            try
            {
                EditorSettings.spritePackerMode = SpritePackerMode.AlwaysOnAtlas;
                Scan(out bool v1Mode);
                EditorSettings.spritePackerMode = SpritePackerMode.Disabled;
                Scan(out bool disabled);
                EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2Build;
                Scan(out bool v2Build);
                EditorSettings.spritePackerMode = SpritePackerMode.SpriteAtlasV2;
                Assert.That(v1Mode, Is.False, "mode 4: the V2 atlas is not packed — its sprites would load without a texture");
                Assert.That(disabled, Is.False, "the packer off: the atlas would be built empty");
                Assert.That(v2Build, Is.True, "SpriteAtlasV2Build: the content build packs the atlas itself");
                Assert.That(errors.Any(e => Regex.IsMatch(e, "the Sprite Packer mode is AlwaysOnAtlas: .*FAtlas.*without a texture")), Is.True, string.Join(" | ", errors));
                Assert.That(errors.Any(e => e.Contains("the Sprite Packer mode is Disabled and 'Assets/TestContent/F' holds sprite atlases")), Is.True, string.Join(" | ", errors));

                if (!AssetDatabase.IsValidFolder("Assets/TestShared/SimTemp21")) AssetDatabase.CreateFolder("Assets/TestShared", "SimTemp21");   // outside the root: one under it is refused as such (AS24)
                var v1 = new SpriteAtlas();
                v1.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Root}/Icons/fa.png") });
                AssetDatabase.CreateAsset(v1, "Assets/TestShared/SimTemp21/V1Temp.spriteatlas");
                errors.Clear();
                Scan(out bool withV1);
                Assert.That(withV1, Is.False, "a V1 atlas that packs a sprite the catalog names refuses the build");
                Assert.That(errors.Any(e => Regex.IsMatch(e, "V1Temp.spriteatlas' packs 1 sprite.*V1 sprite atlas")), Is.True, string.Join(" | ", errors));
            }
            finally { Application.logMessageReceived -= OnLog; AssetDatabase.DeleteAsset("Assets/TestShared/SimTemp21"); }
        }

        /// <summary>
        /// A V1 atlas packing sprites outside the root that a prefab or a scene under the root uses refuses the build: built, those sprites
        /// draw without a texture in a player (measured on 6000.6 — the editor draws them with the project's page). A material that holds
        /// the packed texture itself is not refused: a player shows that texture.
        /// </summary>
        [Test]
        public void AS23_AV1AtlasPackingASpriteTheContentUses_RefusesTheBuild()
        {
            LogAssert.ignoreFailingMessages = true;   // a V1 atlas in a V2 project makes the engine log on its own: the refusals are read from the log
            const string outside = "Assets/TestShared/SimTemp77";
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type) { if (type == LogType.Error && message.StartsWith("[ContentLink]")) errors.Add(message); }
            try
            {
                if (!AssetDatabase.IsValidFolder(outside)) AssetDatabase.CreateFolder("Assets/TestShared", "SimTemp77");
                if (!AssetDatabase.IsValidFolder(k_Temp)) AssetDatabase.CreateFolder(k_Root, "SimTemp");
                var drawn = SpritePng($"{outside}/drawn.png");
                var held = SpritePng($"{outside}/held.png");
                var v1 = new SpriteAtlas();
                v1.Add(new Object[] { drawn, held });
                AssetDatabase.CreateAsset(v1, $"{outside}/V1Uses.spriteatlas");
                var material = new Material(Shader.Find("Sprites/Default")) { mainTexture = held };
                AssetDatabase.CreateAsset(material, $"{k_Temp}/Holds.mat");
                Application.logMessageReceived += OnLog;

                Scan(out bool withMaterial);
                Assert.That(withMaterial, Is.True, "a material that holds a texture a V1 atlas packs refused the build: " + string.Join(" | ", errors));

                var go = new GameObject("Draws");
                go.AddComponent<SpriteRenderer>().sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{outside}/drawn.png");
                PrefabUtility.SaveAsPrefabAsset(go, $"{k_Temp}/Draws.prefab");
                Object.DestroyImmediate(go);
                errors.Clear();
                Scan(out bool withPrefab);
                Assert.That(withPrefab, Is.False, "a prefab under the root that draws a V1-packed sprite went through");
                Assert.That(errors.Any(e => e.Contains("V1Uses.spriteatlas' packs 1 sprite(s) the content uses") && e.Contains($"which '{k_Temp}/Draws.prefab' uses")), Is.True, string.Join(" | ", errors));
                AssetDatabase.DeleteAsset($"{k_Temp}/Draws.prefab");

                var scenePath = $"{k_Temp}/Draws.unity";
                File.WriteAllText(scenePath, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");   // an empty scene file: NewScene cannot add one beside the batch run's untitled scene
                AssetDatabase.ImportAsset(scenePath);
                var active = SceneManager.GetActiveScene();
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                SceneManager.SetActiveScene(scene);
                new GameObject("Draws").AddComponent<SpriteRenderer>().sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{outside}/drawn.png");
                SceneManager.SetActiveScene(active);
                EditorSceneManager.SaveScene(scene);
                EditorSceneManager.CloseScene(scene, true);
                errors.Clear();
                Scan(out bool withScene);
                Assert.That(withScene, Is.False, "a scene under the root that draws a V1-packed sprite went through");
                Assert.That(errors.Any(e => e.Contains($"which '{scenePath}' uses")), Is.True, string.Join(" | ", errors));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                AssetDatabase.DeleteAsset(outside);
            }
        }

        /// <summary>
        /// A V1 atlas under the root that packs a texture outside it nobody uses is refused: loaded by its name, it would give sprites
        /// without a texture (measured on 6000.6 — content directories carry no V1 pages).
        /// </summary>
        [Test]
        public void AS24_AV1AtlasUnderTheRoot_RefusesTheBuild()
        {
            LogAssert.ignoreFailingMessages = true;   // a V1 atlas in a V2 project makes the engine log on its own: the refusal is read from the log
            const string outside = "Assets/TestShared/SimTemp77";
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type) { if (type == LogType.Error && message.StartsWith("[ContentLink]")) errors.Add(message); }
            try
            {
                if (!AssetDatabase.IsValidFolder(outside)) AssetDatabase.CreateFolder("Assets/TestShared", "SimTemp77");
                if (!AssetDatabase.IsValidFolder(k_Temp)) AssetDatabase.CreateFolder(k_Root, "SimTemp");
                var v1 = new SpriteAtlas();
                v1.Add(new Object[] { SpritePng($"{outside}/alone.png") });
                AssetDatabase.CreateAsset(v1, $"{k_Temp}/Legacy.spriteatlas");
                Application.logMessageReceived += OnLog;
                Scan(out bool ok);
                Assert.That(ok, Is.False, "a V1 atlas under the root went through");
                Assert.That(errors.Count(e => e.Contains($"'{k_Temp}/Legacy.spriteatlas' is a V1 sprite atlas under the source root")), Is.EqualTo(1), string.Join(" | ", errors));
                Assert.That(errors.Count, Is.EqualTo(1), "one refusal for the atlas, not one more for what it packs: " + string.Join(" | ", errors));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                AssetDatabase.DeleteAsset(outside);
            }
        }

        /// <summary>
        /// An asset under the root that holds a V1 atlas itself, to give sprites at run time. The atlas packs a texture, so no sprite of it
        /// is used — but a build carries it without its pages: GetSprite gives a sprite without a texture in a player (measured on 6000.6).
        /// The asset's files name the atlas; the scan refuses it, naming the asset.
        /// </summary>
        [Test]
        public void AS25_AV1AtlasTheContentHolds_RefusesTheBuild()
        {
            LogAssert.ignoreFailingMessages = true;   // a V1 atlas in a V2 project makes the engine log on its own: the refusal is read from the log
            const string outside = "Assets/TestShared/SimTemp77";
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type) { if (type == LogType.Error && message.StartsWith("[ContentLink]")) errors.Add(message); }
            try
            {
                if (!AssetDatabase.IsValidFolder(outside)) AssetDatabase.CreateFolder("Assets/TestShared", "SimTemp77");
                if (!AssetDatabase.IsValidFolder(k_Temp)) AssetDatabase.CreateFolder(k_Root, "SimTemp");
                var v1 = new SpriteAtlas();
                v1.Add(new Object[] { SpritePng($"{outside}/given.png") });
                AssetDatabase.CreateAsset(v1, $"{outside}/Given.spriteatlas");
                var holder = ScriptableObject.CreateInstance<AtlasHolderFixture>();
                holder.atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>($"{outside}/Given.spriteatlas");
                AssetDatabase.CreateAsset(holder, $"{k_Temp}/Holds.asset");
                Application.logMessageReceived += OnLog;
                Scan(out bool ok);
                Assert.That(ok, Is.False, "an asset under the root that holds a V1 atlas went through");
                Assert.That(errors.Count(e => e.Contains($"'{outside}/Given.spriteatlas' is a V1 sprite atlas that '{k_Temp}/Holds.asset' holds")), Is.EqualTo(1), string.Join(" | ", errors));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                AssetDatabase.DeleteAsset(outside);
            }
        }

        /// <summary>
        /// A V1 atlas that packs sprites themselves, a prefab under the root drawing one of them. A packed sprite links to
        /// its atlas, and the atlas to every sprite it packs, so the objects alone would count them all as the prefab's (measured on 6000.6);
        /// the prefab's files name the one it draws. The refusal counts that one and names it.
        /// </summary>
        [Test]
        public void AS26_AV1AtlasPackingSprites_NamesTheOneDrawn()
        {
            LogAssert.ignoreFailingMessages = true;   // a V1 atlas in a V2 project makes the engine log on its own: the refusal is read from the log
            const string outside = "Assets/TestShared/SimTemp77";
            var errors = new List<string>();
            void OnLog(string message, string stack, LogType type) { if (type == LogType.Error && message.StartsWith("[ContentLink]")) errors.Add(message); }
            try
            {
                if (!AssetDatabase.IsValidFolder(outside)) AssetDatabase.CreateFolder("Assets/TestShared", "SimTemp77");
                if (!AssetDatabase.IsValidFolder(k_Temp)) AssetDatabase.CreateFolder(k_Root, "SimTemp");
                SpritePng($"{outside}/t.png");
                SpritePng($"{outside}/s.png");
                var v1 = new SpriteAtlas();
                v1.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Sprite>($"{outside}/t.png"), AssetDatabase.LoadAssetAtPath<Sprite>($"{outside}/s.png") });
                AssetDatabase.CreateAsset(v1, $"{outside}/V1Sprites.spriteatlas");
                SpriteAtlasUtility.PackAtlases(new[] { v1 }, EditorUserBuildSettings.activeBuildTarget);
                var go = new GameObject("Draws");
                go.AddComponent<SpriteRenderer>().sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{outside}/s.png");
                var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{k_Temp}/Draws.prefab");
                Object.DestroyImmediate(go);
                Assert.That(EditorUtility.CollectDependencies(new Object[] { prefab }).Any(o => o is Sprite && o.name == "t"), Is.True,
                    "the engine no longer links a packed sprite to its atlas — this test no longer sees what it is about");
                Application.logMessageReceived += OnLog;
                Scan(out bool ok);
                Assert.That(ok, Is.False, "a prefab under the root that draws a V1-packed sprite went through");
                Assert.That(errors.Any(e => e.Contains($"'{outside}/V1Sprites.spriteatlas' packs 1 sprite(s) the content uses ('{outside}/s.png' s first, which '{k_Temp}/Draws.prefab' uses)")), Is.True, string.Join(" | ", errors));
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                AssetDatabase.DeleteAsset(outside);
            }
        }

        /// <summary>T22: an atlas saved with one texture less — the next scan has no name for it, and a drop offers it no more, with no projectChanged in between.</summary>
        [Test]
        public void AS22_AnAtlasSaved_TheBuilderAndTheInspectorFollow()
        {
            var faTexture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Root}/Icons/fa.png");
            var path = $"{k_Temp}/T22Atlas.spriteatlasv2";
            MakeAtlas(path, faTexture, AssetDatabase.LoadAssetAtPath<Texture2D>($"{k_Root}/Icons/fsheet.png"));
            ContentLinkEditorUtility.Invalidate();
            Choices(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path), typeof(Sprite));   // the first reads may settle the caches' stamp
            var generation = typeof(ContentLinkEditorUtility).GetProperty("Generation", BindingFlags.Static | BindingFlags.NonPublic);
            int before = (int)generation.GetValue(null);
            Assert.That(Choices(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path), typeof(Sprite)), Is.EqualTo("fa,fs0,fs1"));
            Assert.That((int)generation.GetValue(null), Is.EqualTo(before), "the caches hold between reads — or the check below would pass with nothing dropping them");

            var asset = SpriteAtlasAsset.Load(path);
            asset.Remove(new Object[] { faTexture });
            SpriteAtlasAsset.Save(asset, path);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            Assert.That(Choices(AssetDatabase.LoadAssetAtPath<SpriteAtlas>(path), typeof(Sprite)), Is.EqualTo("fs0,fs1"), "the inspector follows the saved atlas");
            var items = Scan(out bool ok);
            Assert.That(ok, Is.True);
            Assert.That(TryEntry(items, "SimTemp/T22Atlas[fa]", out _), Is.False, "the builder names what the atlas packs now");
            Assert.That(TryEntry(items, "SimTemp/T22Atlas[fs0]", out _), Is.True);
        }
    }
}
