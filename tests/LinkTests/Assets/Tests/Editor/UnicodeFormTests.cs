using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Unicode forms against the real editor. On macOS the AssetDatabase spells a path's folders decomposed (NFD) and its file,
    /// and an object's name, as on disk (measured on 6000.6); names are made and compared composed (NFC) — what an IME types and git keeps.
    /// The fixture is made as git checks a project out on macOS: folders and files composed on disk.
    /// </summary>
    public class UnicodeFormTests
    {
        const string Root = "Assets/U12";
        const string Catalog = "Assets/U12Gen/Catalog.asset";
        static string C(string s) => s.Normalize(NormalizationForm.FormC);
        static string D(string s) => s.Normalize(NormalizationForm.FormD);
        static T Load<T>(string path) where T : Object => AssetDatabase.LoadAssetAtPath<T>(path);   // either form finds it (measured)

        static Texture2D Pic => Load<Texture2D>(Root + "/" + C("사진/장미") + ".png");   // not a name test content H has: its catalog would answer first
        static Sprite PicSprite => AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(Pic)).OfType<Sprite>().First();
        static Texture2D Tree => Load<Texture2D>(Root + "/" + C("나무") + ".png");
        static TextAsset Line => Load<TextAsset>(Root + "/Resources/" + C("글자/한줄") + ".txt");

        [OneTimeSetUp]
        public void Fixture()
        {
            var png = File.ReadAllBytes("Assets/Tests/Resources/ContentLinkTests/logo.png");
            Directory.CreateDirectory(Root + "/" + C("사진"));                                    // as git writes it: composed
            File.WriteAllBytes(Root + "/" + C("사진") + "/" + C("장미") + ".png", png);
            File.WriteAllBytes(Root + "/" + D("나무") + ".png", png);                             // a file whose name is decomposed on disk
            Directory.CreateDirectory(Root + "/Resources/" + C("글자"));
            File.WriteAllText(Root + "/Resources/" + C("글자") + "/" + C("한줄") + ".txt", "hangul");
            foreach (var folder in new[] { C("데이터"), C("データ"), C("café") })   // Hangul, kana with a voiced mark, accented Latin — each decomposes under NFD
            {
                Directory.CreateDirectory(Root + "/Scripts/" + folder);
                File.WriteAllBytes(Root + "/Scripts/" + folder + "/a.png", png);
            }
            Directory.CreateDirectory("Assets/U12Gen");
            AssetDatabase.Refresh();
            foreach (var t in new Object[] { Pic, Tree })
            {
                var imp = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(t));
                imp.textureType = TextureImporterType.Sprite; imp.spriteImportMode = SpriteImportMode.Single; imp.SaveAndReimport();
            }
        }

        [OneTimeTearDown]
        public void Clean()
        {
            ContentDirectoryBuilder.ClearOverrides();
            AssetDatabase.DeleteAsset(Root); AssetDatabase.DeleteAsset("Assets/U12Gen");
            ContentLinkEditorUtility.Invalidate();
        }

        [TearDown]
        public void After()
        {
            ContentDirectoryBuilder.ClearOverrides();
            AssetDatabase.DeleteAsset(Catalog);
            ContentLinkEditorUtility.Invalidate();
        }

        static void Static(string root)
        {
            ContentDirectoryBuilder.SourceRoot = root;
            ContentDirectoryBuilder.CatalogPath = Catalog;
            ContentDirectoryBuilder.OutputPath = "Assets/StreamingAssets/U12Content";
            ContentDirectoryBuilder.NameStyle = ContentNameStyle.PathWithoutExtension;
            ContentLinkEditorUtility.Invalidate();
        }

        static string[] Names() => Load<ContentCatalog>(Catalog).Entries.Select(e => e.name).ToArray();

        /// <summary>U1: what the rest stands on — the editor spells the folder decomposed and the file as on disk (macOS).</summary>
        [Test]
        public void U1_TheEditorSpellsTheFoldersDecomposed()
        {
            if (Application.platform != RuntimePlatform.OSXEditor) Assert.Ignore("measured on macOS: another editor may spell the path as on disk");
            Assert.That(AssetDatabase.GetAssetPath(Pic), Is.EqualTo(D(Root + "/사진") + "/" + C("장미") + ".png"));
            Assert.That(Tree.name, Is.EqualTo(D("나무")), "an object is named as its file is spelled");
            foreach (var folder in new[] { "데이터", "データ", "café" })
            {
                var path = AssetDatabase.GetAssetPath(Load<Texture2D>(Root + "/Scripts/" + C(folder) + "/a.png"));
                Assert.That(path, Is.EqualTo(Root + "/Scripts/" + D(folder) + "/a.png"), $"'{folder}' — not Hangul only: the editor normalizes the folder (NFD)");
            }
        }

        /// <summary>U2: the picker names composed — a file in a decomposed folder, a file and a sprite spelled decomposed, a file in a decomposed folder under Resources (under the root: a root name).</summary>
        [Test]
        public void U2_NamesAreComposed()
        {
            Static(Root);
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Pic, out var pic, out var why), Is.True, why);
            Assert.That(pic, Is.EqualTo(C("사진/장미")));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Tree, out var tree, out why), Is.True, why);
            Assert.That(tree, Is.EqualTo(C("나무")));
            Assert.That(ContentLinkEditorUtility.TryGetLinkForAsset(PicSprite, out var main, out var sub, out why), Is.True, why);
            Assert.That((main, sub), Is.EqualTo((C("사진/장미"), C("장미"))));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Line, out var line, out why), Is.True, why);
            Assert.That(line, Is.EqualTo(C("Resources/글자/한줄")));
        }

        /// <summary>U3: a root typed composed over the decomposed folder names what it holds — the catalog update too, not cut at the composed length.</summary>
        [Test]
        public void U3_AComposedRootNamesWhatItHolds()
        {
            Static(C(Root + "/사진"));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Pic, out var name, out var why), Is.True, why);
            Assert.That(name, Is.EqualTo(C("장미")));
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
            Assert.That(Names(), Is.EquivalentTo(new[] { C("장미"), C("장미[장미]") }));
        }

        /// <summary>U4: a category typed composed catalogues the decomposed folder's assets — none left out with a warning.</summary>
        [Test]
        public void U4_AComposedCategoryCataloguesItsAssets()
        {
            var settings = ContentBuildSettings.instance;
            var profile = settings.Add("U12");
            try
            {
                profile.SourceRoot = Root; profile.OutputPath = "Assets/StreamingAssets/U12Profile";
                profile.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = C("사진"), CreateAt = Catalog } };
                ContentLinkEditorUtility.Invalidate();
                Assert.That(settings.Validate(profile), Is.Empty);
                var errors = new System.Collections.Generic.List<string>();
                Assert.That(ContentBuildRunner.UpdateCatalogs(profile, errors), Is.True, string.Join(" | ", errors));
                Assert.That(Names(), Is.EquivalentTo(new[] { C("사진/장미"), C("사진/장미[장미]") }));
            }
            finally { settings.Remove("U12"); }
        }

        /// <summary>U5: a name in either form resolves — with no catalog (the path search runs in both forms) and with one.</summary>
        [Test]
        public void U5_EitherFormResolves()
        {
            Static(Root);
            Assert.That(ContentLinkEditorUtility.ResolveAsset(C("사진/장미")), Is.SameAs(Pic));
            Assert.That(ContentLinkEditorUtility.ResolveAsset(D("사진") + "/" + C("장미")), Is.SameAs(Pic), "the spelling an earlier version made");
            Assert.That(ContentLinkEditorUtility.ResolveAsset(C("나무")), Is.SameAs(Tree), "a file spelled decomposed");
            Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
            Assert.That(ContentLinkEditorUtility.Validate(C("사진/장미"), typeof(Texture2D)), Is.EqualTo(NameStatus.Ok));
            Assert.That(ContentLinkEditorUtility.Validate(D("사진") + "/" + C("장미"), typeof(Texture2D)), Is.EqualTo(NameStatus.Ok));
            Assert.That(Names().All(n => n.IsNormalized(NormalizationForm.FormC)), Is.True, string.Join(", ", Names()));
        }

        /// <summary>U6: a res: name is composed, and in either form it is the player's file in the editor — the spelling the editor's Resources finds.</summary>
        [Test]
        public void U6_ResInEitherForm()
        {
            Static(C(Root + "/사진"));   // the Resources folder outside the root: a res: name
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Line, out var line, out var why), Is.True, why);
            Assert.That(line, Is.EqualTo(C("res:글자/한줄")));
            Assert.That(ContentLinkEditorUtility.ResolveAsset(C("res:글자/한줄")), Is.SameAs(Line));
            Assert.That(ContentLinkEditorUtility.ResolveAsset("res:" + D("글자") + "/" + C("한줄")), Is.SameAs(Line));
            Assert.That(ContentLinkEditorUtility.Validate(C("res:글자/한줄"), typeof(TextAsset)), Is.EqualTo(NameStatus.Ok));
        }

        /// <summary>U7: a PathWithExtension root — a path is found in either form, so each file counts once: a file in a decomposed folder, a file spelled decomposed and a sprite are named and found.</summary>
        [Test]
        public void U7_PathWithExtension()
        {
            Static(Root);
            ContentDirectoryBuilder.NameStyle = ContentNameStyle.PathWithExtension;
            ContentLinkEditorUtility.Invalidate();
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Pic, out var pic, out var why), Is.True, why);
            Assert.That(pic, Is.EqualTo(C("사진/장미.png")));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(Tree, out var tree, out why), Is.True, why);
            Assert.That(tree, Is.EqualTo(C("나무.png")));
            Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(PicSprite, out var sprite, out why), Is.True, why);
            Assert.That(sprite, Is.EqualTo(C("사진/장미.png[장미]")));
            Assert.That(ContentLinkEditorUtility.ResolveAsset(C("사진/장미.png")), Is.SameAs(Pic));
            Assert.That(ContentLinkEditorUtility.ResolveAsset(D("사진") + "/" + C("장미.png")), Is.SameAs(Pic), "the spelling an earlier version made");
            Assert.That(ContentLinkEditorUtility.ResolveAsset(C("나무.png")), Is.SameAs(Tree));
            Assert.That(ContentLinkEditorUtility.ResolveAsset(C("사진/장미.png[장미]")), Is.SameAs(PicSprite));
        }

        /// <summary>U8: one name, two files in two forms — a .txt spelled composed, a .bytes spelled decomposed: neither answers, the picker refuses both, the build refuses the name.</summary>
        [Test]
        public void U8_TwoFormsOfOneName_AreRefused()
        {
            string nfcPath = Root + "/" + C("칼") + ".txt", nfdPath = Root + "/" + D("칼") + ".bytes";
            File.WriteAllText(nfcPath, "nfc"); File.WriteAllText(nfdPath, "nfd");
            AssetDatabase.Refresh();
            try
            {
                Static(Root);
                var nfc = Load<TextAsset>(nfcPath); var nfd = Load<TextAsset>(nfdPath);
                Assert.That(nfc != null && nfd != null, Is.True, "both imported");
                Assert.That(ContentLinkEditorUtility.ResolveAsset(C("칼")), Is.Null, "a name two files answer answers neither");
                Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(nfc, out _, out var why), Is.False);
                StringAssert.Contains("names 2 files", why);
                Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(nfd, out _, out why), Is.False);
                StringAssert.Contains("names 2 files", why);
                LogAssert.Expect(LogType.Error, new Regex("duplicate name"));
                Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.False, "the build refuses a name two files share");
            }
            finally { AssetDatabase.DeleteAsset(nfcPath); AssetDatabase.DeleteAsset(nfdPath); }
        }

        /// <summary>U9: a file name spelled in neither form throughout — part of it decomposed — is refused with that reason (a search by name finds it in neither form) until a catalog update names it, composed.</summary>
        [Test]
        public void U9_ANameInNeitherForm()
        {
            var path = Root + "/" + D("검") + C("방패") + ".txt";
            File.WriteAllText(path, "mixed");
            AssetDatabase.Refresh();
            try
            {
                Static(Root);
                var mixed = Load<TextAsset>(path);
                Assert.That(mixed, Is.Not.Null, "imported");
                Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(mixed, out _, out var why), Is.False);
                StringAssert.Contains("spelled in neither Unicode form throughout", why);
                Assert.That(ContentDirectoryBuilder.UpdateCatalog(), Is.True);
                Assert.That(Names(), Does.Contain(C("검방패")));
                Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(mixed, out var name, out why), Is.True, why);
                Assert.That(name, Is.EqualTo(C("검방패")));
                Assert.That(ContentLinkEditorUtility.ResolveAsset(C("검방패")), Is.SameAs(mixed));
            }
            finally { AssetDatabase.DeleteAsset(path); }
        }

        /// <summary>U10: a Resources file no spelling of its composed path reaches — part of its name decomposed, a CJK compatibility ideograph — is named in its own spelling, the folders composed, and the editor answers and validates that name.</summary>
        [Test]
        public void U10_ResInAFilesOwnSpelling()
        {
            string mixedPath = Root + "/Resources/" + C("글자") + "/" + D("검") + C("방패") + ".txt", compatPath = Root + "/Resources/" + C("글자") + "/\uF914.txt";
            File.WriteAllText(mixedPath, "mixed"); File.WriteAllText(compatPath, "compat");
            AssetDatabase.Refresh();
            try
            {
                Static(C(Root + "/사진"));   // the Resources folder outside the root: res: names
                var mixed = Load<TextAsset>(mixedPath); var compat = Load<TextAsset>(compatPath);
                Assert.That(mixed != null && compat != null, Is.True, "both imported");
                Assert.That(AssetDatabase.GetAssetPath(compat), Does.EndWith("/\uF914.txt"), "the file kept as written");
                Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(mixed, out var mixedName, out var why), Is.True, why);
                Assert.That(mixedName, Is.EqualTo("res:" + C("글자") + "/" + D("검") + C("방패")));
                Assert.That(ContentLinkEditorUtility.TryGetNameForAsset(compat, out var compatName, out why), Is.True, why);
                Assert.That(compatName, Is.EqualTo("res:" + C("글자") + "/\uF914"));
                Assert.That(ContentLinkEditorUtility.ResolveAsset(mixedName), Is.SameAs(mixed));
                Assert.That(ContentLinkEditorUtility.ResolveAsset(compatName), Is.SameAs(compat));
                Assert.That(ContentLinkEditorUtility.Validate(mixedName, typeof(TextAsset)), Is.EqualTo(NameStatus.Ok));
            }
            finally { AssetDatabase.DeleteAsset(mixedPath); AssetDatabase.DeleteAsset(compatPath); }
        }
    }
}
