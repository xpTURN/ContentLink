using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.TestTools;
using xpTURN.ContentLink.Editor;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests.EditorPlay
{
    /// <summary>
    /// What drops the editor utility's caches drops the simulation's answers — an atlas saved during Play raises no
    /// projectChanged (measured on 6000.6). F's built directory is registered with the simulation on, so a name its catalog lacks is
    /// answered from the project; the atlas lies in F/SimTemp (gitignored) and is deleted at both ends.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Simulation")]
    public class SimulationInvalidationTests
    {
        const string k_Temp = "Assets/TestContent/F/SimTemp";
        const string k_Atlas = k_Temp + "/SavedAtlas.spriteatlasv2";
        const string k_Fa = "Assets/TestContent/F/Icons/fa.png";
        const string k_Sheet = "Assets/TestContent/F/Icons/fsheet.png";

        int m_Dir = -1;
        bool m_EnabledWas;

        [SetUp]
        public void Before()
        {
            AssetDatabase.DeleteAsset(k_Temp);
            AssetDatabase.CreateFolder("Assets/TestContent/F", "SimTemp");
            var atlas = new SpriteAtlasAsset();
            atlas.Add(new Object[] { AssetDatabase.LoadAssetAtPath<Texture2D>(k_Fa) });
            SpriteAtlasAsset.Save(atlas, k_Atlas);
            AssetDatabase.ImportAsset(k_Atlas, ImportAssetOptions.ForceSynchronousImport);
            m_EnabledWas = ContentLinkSimulation.Enabled;
            ContentLinkSimulation.Enabled = true;
            TestUtil.SetSimulation(true);
            ContentLinkSimulation.Drop();   // the overlay is made on the next name with the atlas in it — as at Play entry
            m_Dir = ContentService.RegisterDirectory(TestContent.Dir("F"));
        }

        [TearDown]
        public void After()
        {
            ContentService.Collect();
            if (m_Dir >= 0 && !ContentService.UnregisterDirectory(m_Dir)) ContentService.UnregisterDirectory(m_Dir, force: true);
            m_Dir = -1;
            TestUtil.RestoreStatics();
            ContentLinkSimulation.Drop();
            ContentLinkSimulation.Enabled = m_EnabledWas;
            AssetDatabase.DeleteAsset(k_Temp);
            ContentLinkEditorUtility.Invalidate();
        }

        static void Save(Object add, Object remove)
        {
            var asset = SpriteAtlasAsset.Load(k_Atlas);
            if (add != null) asset.Add(new[] { add });
            if (remove != null) asset.Remove(new[] { remove });
            SpriteAtlasAsset.Save(asset, k_Atlas);
            AssetDatabase.ImportAsset(k_Atlas, ImportAssetOptions.ForceSynchronousImport);
        }

        /// <summary>T63: an atlas saved during Play with one more texture serves its sprites at once — by name, by reference and by a load — as the inspector already shows.</summary>
        [Test, Timeout(30000)]
        public async Task T63_AnAtlasSavedWithATextureMore_ItsSpritesAnswer()
        {
            var guid = AssetDatabase.AssetPathToGUID(k_Atlas);
            Assert.That(ContentService.Contains("SimTemp/SavedAtlas[fa]"), Is.True, "sanity: it packs fa");
            Assert.That(ContentService.Contains("SimTemp/SavedAtlas[fs0]"), Is.False, "sanity: not fsheet yet — the miss is asked first");
            Assert.That(ContentLinkSimulation.TryGetPath("SimTemp/SavedAtlas", out var overlaid, out _) && overlaid == k_Atlas, Is.True,
                "sanity: the atlas is in the overlay — off it a name takes Discover, which reads the utility, and F-37 would not show");

            Save(AssetDatabase.LoadAssetAtPath<Texture2D>(k_Sheet), null);

            Assert.That(ContentLinkEditorUtility.ResolveRefAsset(guid, "fs0"), Is.Not.Null, "sanity: the inspector follows the saved atlas (IMP015 T22)");
            Assert.That(ContentService.Contains("SimTemp/SavedAtlas[fs0]"), Is.True, "the name of a sprite the atlas packs now");
            Assert.That(ContentService.Contains(new ContentRef<Sprite>(guid, "fs0")), Is.True, "… the reference (atlas GUID, fs0)");
            var handle = await ContentService.AcquireAsync<Sprite>("SimTemp/SavedAtlas[fs0]");
            try { Assert.That(handle.IsValid && handle.Asset != null && handle.Asset.name == "fs0", Is.True, "… and a load"); }
            finally { if (handle.IsValid) handle.Dispose(); }
        }

        /// <summary>T64 (the other way): a texture taken out of the atlas during Play — its sprite stops answering, as in a build.</summary>
        [Test, Timeout(30000)]
        public void T64_AnAtlasSavedWithoutATexture_ItsSpriteStopsAnswering()
        {
            Assert.That(ContentService.Contains("SimTemp/SavedAtlas[fa]"), Is.True, "sanity: it packs fa — the answer is asked first");
            Save(AssetDatabase.LoadAssetAtPath<Texture2D>(k_Sheet), AssetDatabase.LoadAssetAtPath<Texture2D>(k_Fa));
            Assert.That(ContentService.Contains("SimTemp/SavedAtlas[fa]"), Is.False, "fa is no longer packed");
        }
    }
}
