using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.U2D;
using Object = UnityEngine.Object;
#endif

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Sprites named through an atlas, loaded from TestContent/F — Atlas/FAtlas packs Icons/fa, Icons/fsheet (fs0 · fs1)
    /// and TestShared/Atlas/fout, a texture outside the root. The page checks run in a player only: the editor binds a sprite
    /// loaded from a content directory to the project's atlas page too, so a page missing from the build would look bound there.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Atlas")]
    public class AtlasSpriteTests
    {
        const string k_AtlasGuid = "b7b667753b499478d96e56b695260450";   // TestContent/F/Atlas/FAtlas.spriteatlasv2

        int m_Dir = -1;

        static void PlayerOnly()
        {
            if (Application.isEditor) Assert.Ignore("the editor binds a sprite from a content directory to the project's atlas page too (IMP015 E14): the player run checks this");
        }

        static int Pages() => Resources.FindObjectsOfTypeAll<Texture2D>().Count(t => t.name.StartsWith("sactx", StringComparison.Ordinal) && t.name.Contains("FAtlas"));
        static bool OwnTexture() => Resources.FindObjectsOfTypeAll<Texture2D>().Any(t => t.name == "fa" || t.name == "fsheet" || t.name == "fout");
        static async Task Frames(int n) { for (int i = 0; i < n; ++i) await TestUtil.NextFrame(); }

        [SetUp]
        public void Before()
        {
            TestUtil.RestoreStatics();
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("F"));
        }

        [TearDown]
        public void After()
        {
            ContentService.Collect();
            if (m_Dir >= 0 && !ContentService.UnregisterDirectory(m_Dir)) ContentService.UnregisterDirectory(m_Dir, force: true);
            m_Dir = -1;
            TestUtil.RestoreStatics();
        }

        /// <summary>T19: the sprite's own name and the atlas's name hold one object — each lets go on its own, and the object goes with the last.</summary>
        [Test]
        public async Task AS19_OwnNameAndAtlasName_HoldOneObject()
        {
            var own = ContentService.Acquire<Sprite>("Icons/fa[fa]");
            var alias = ContentService.Acquire<Sprite>("Atlas/FAtlas[fa]");
            var sprite = alias.Asset;
            Assert.That(sprite, Is.Not.Null);
            Assert.That(own.Asset, Is.SameAs(sprite), "one id, one object");

            own.Dispose(); ContentService.Collect(); await Frames(3);
            Assert.That(alias.Asset != null && sprite != null, Is.True, "the atlas's name keeps it when the own name lets go");

            alias.Dispose(); ContentService.Collect(); await Frames(3);
            Assert.That(sprite == null, Is.True, "unloaded with the last name");
        }

        /// <summary>
        /// T18: in a player the atlas's name loads the original sprite bound to the atlas page, with no texture of its own in
        /// memory; a reference (atlas GUID, sprite) and a link (atlas, sprite) reach the same page, a texture outside the root too;
        /// the page goes with the last name.
        /// </summary>
        [Test]
        public async Task AS18_TheAtlasName_LoadsTheSpriteOnThePage()
        {
            PlayerOnly();
            var alias = ContentService.Acquire<Sprite>("Atlas/FAtlas[fa]");
            var byRef = ContentService.Acquire(new ContentRef<Sprite>(k_AtlasGuid, "fs0"));
            var link = new ContentLink<Sprite>("Atlas/FAtlas", "fout");
            var outside = await link.LoadAsync(ContentScope.Root);
            var sprite = alias.Asset;

            Assert.That(sprite != null && sprite.packed, Is.True, "packed");
            Assert.That(sprite.texture != null && sprite.texture.name.StartsWith("sactx", StringComparison.Ordinal), Is.True, $"on the atlas page: {(sprite.texture != null ? sprite.texture.name : "null")}");
            Assert.That(OwnTexture(), Is.False, "no texture of its own: the build carries the page only");
            Assert.That(byRef.Asset != null && byRef.Asset.name == "fs0" && byRef.Asset.texture == sprite.texture, Is.True, "the reference (atlas GUID, fs0), on the same page");
            Assert.That(outside != null && outside.packed && outside.texture == sprite.texture, Is.True, "the link (atlas, fout) — a texture outside the root, on the same page");
            Assert.That(Pages(), Is.EqualTo(1));

            alias.Dispose(); byRef.Dispose(); link.Release(); ContentService.Collect(); await Frames(5);
            Assert.That(Pages(), Is.EqualTo(0), "the page goes with the last name");
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// T20: in the editor, an atlas's name the build lacks — an atlas made after the build, under F's root — is served from
    /// the project by name, by reference (atlas GUID, sprite) and by link. The atlas lies in F/SimTemp (gitignored) and is deleted
    /// at both ends, like the other simulation fixtures. Editor-only: the fixture needs the AssetDatabase.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Atlas")]
    public class AtlasSimulationTests
    {
        const string k_Temp = "Assets/TestContent/F/SimTemp";
        const string k_Atlas = k_Temp + "/LateAtlas.spriteatlasv2";

        int m_Dir = -1;

        [OneTimeSetUp]
        public void CreateFixture()
        {
            AssetDatabase.DeleteAsset(k_Temp);
            AssetDatabase.CreateFolder("Assets/TestContent/F", "SimTemp");
            var atlas = new SpriteAtlasAsset();
            atlas.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TestContent/F/Icons/fa.png") });
            SpriteAtlasAsset.Save(atlas, k_Atlas);
            AssetDatabase.ImportAsset(k_Atlas, ImportAssetOptions.ForceSynchronousImport);
        }

        [OneTimeTearDown]
        public void DropFixture() => AssetDatabase.DeleteAsset(k_Temp);

        [SetUp]
        public void Before()
        {
            TestUtil.SetSimulation(true);
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("F"));
        }

        [TearDown]
        public void After()
        {
            ContentService.Collect();
            if (m_Dir >= 0 && !ContentService.UnregisterDirectory(m_Dir)) ContentService.UnregisterDirectory(m_Dir, force: true);
            m_Dir = -1;
            TestUtil.RestoreStatics();
        }

        [Test]
        public async Task AS20_AnAtlasNameTheBuildLacks_IsServedFromTheProject()
        {
            var byName = ContentService.Acquire<Sprite>("SimTemp/LateAtlas[fa]");
            var byRef = ContentService.Acquire(new ContentRef<Sprite>(AssetDatabase.AssetPathToGUID(k_Atlas), "fa"));
            var link = new ContentLink<Sprite>("SimTemp/LateAtlas", "fa");
            var viaLink = await link.LoadAsync(ContentScope.Root);
            try
            {
                Assert.That(byName.IsValid && byName.Asset != null && byName.Asset.name == "fa", Is.True, "the name 'SimTemp/LateAtlas[fa]'");
                Assert.That(byRef.Asset, Is.SameAs(byName.Asset), "the reference (atlas GUID, fa)");
                Assert.That(viaLink, Is.SameAs(byName.Asset), "the link ('SimTemp/LateAtlas', 'fa')");
                Assert.That(byName.Asset.packed, Is.True, "the packer is on: the project's sprite is on the atlas page");
            }
            finally
            {
                byName.Dispose(); byRef.Dispose(); link.Release();
            }
        }
    }
#endif
}
