using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// res: names against the real engine — Assets/Tests/Resources/ContentLinkTests, built into every player.
    /// The editor's Resources.UnloadAsset unloads nothing (measured on 6000.6), so the release checks run in a player only
    /// (tools/run-all-tests.sh --player) and are ignored in the editor.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Resources")]
    public class ResourcesSourceTests
    {
        const string k_Logo = "res:ContentLinkTests/logo";
        const string k_Sheet = "res:ContentLinkTests/sheet";

        static void PlayerOnly()
        {
            if (Application.isEditor) Assert.Ignore("the editor's Resources.UnloadAsset unloads nothing (IMP014 E7): the player run checks this");
        }

        static bool InMemory<T>(string name) where T : Object => Resources.FindObjectsOfTypeAll<T>().Any(o => o.name == name);

        [SetUp] public void Before() => TestUtil.RestoreStatics();
        [TearDown] public void After() { TestUtil.RestoreStatics(); ContentService.Collect(); }

        /// <summary>T21: a texture, a sprite by name and by link, a TextAsset and an instantiated prefab — each under its key, a slot each.</summary>
        [Test]
        public async Task T21_RealResources_ThroughScopesNamesAndLinks()
        {
            var scope = ContentScope.Root.CreateChild("T21");
            var link = new ContentLink<Sprite>(k_Sheet, "s1");
            GameObject hero = null;
            try
            {
                int live = ContentService.LiveAssetCount;
                Assert.That(await scope.LoadAsync<Texture2D>(k_Logo), Is.Not.Null);
                var s0 = scope.Load<Sprite>("res:ContentLinkTests/sheet[s0]");
                Assert.That(s0 != null ? s0.name : null, Is.EqualTo("s0"));
                var s1 = await link.LoadAsync(scope);
                Assert.That(s1 != null ? s1.name : null, Is.EqualTo("s1"));
                Assert.That(scope.Load<Sprite>("res:ContentLinkTests/sheet[s1]"), Is.SameAs(s1), "the link and the whole name are one slot");
                Assert.That(scope.Load<TextAsset>("res:ContentLinkTests/boot"), Is.Not.Null);
                hero = await scope.InstantiateAsync("res:ContentLinkTests/hero");
                Assert.That(hero, Is.Not.Null);
                Assert.That(scope.Contains(k_Logo) && scope.Contains("res:ContentLinkTests/sheet[s0]") && scope.Contains("res:ContentLinkTests/boot"), Is.True, "each name under its key");
                Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live + 5), "logo · s0 · s1 · boot · hero");
            }
            finally
            {
                if (hero != null) Object.Destroy(hero);   // the instance holds the prefab's reference
                link.Release(); scope.Dispose();
            }
        }

        /// <summary>
        /// T22: a release unloads in a player and the next load is a new object · one object under a nested folder's two paths stays while
        /// either name holds it (a name in another case is caught by the same-path rule first, so it would not show the same-object guard)
        /// · a texture waits for its sprite · UnloadResourcesOnRelease off keeps a reference taken with Resources.Load.
        /// </summary>
        [Test]
        public void T22_Release_UnloadsInAPlayer_SameObject_SamePath_TheSwitch()
        {
            PlayerOnly();
            var h = ContentService.Acquire<Texture2D>(k_Logo); var obj = h.Asset;
            h.Dispose(); ContentService.Collect();
            Assert.That(obj == null, Is.True, "unloaded at Collect");
            var again = ContentService.Acquire<Texture2D>(k_Logo);
            Assert.That(again.Asset != null && !ReferenceEquals(again.Asset, obj), Is.True, "a new object after");
            again.Dispose(); ContentService.Collect();

            var outer = ContentService.Acquire<TextAsset>("res:ContentLinkTests/Outer/Resources/ContentLinkTests/nested");
            var inner = ContentService.Acquire<TextAsset>("res:ContentLinkTests/nested");
            var held = outer.Asset;
            Assert.That(held != null && ReferenceEquals(held, inner.Asset), Is.True, "sanity: one object under two names");
            outer.Dispose(); ContentService.Collect();
            Assert.That(inner.Asset != null, Is.True, "D2: the other name keeps the object alive");
            inner.Dispose(); ContentService.Collect();
            Assert.That(held == null, Is.True, "D2: unloaded with the last name");

            var tex = ContentService.Acquire<Texture2D>(k_Sheet); var spr = ContentService.Acquire<Sprite>("res:ContentLinkTests/sheet[s0]");
            var t = tex.Asset; var sp = spr.Asset;
            tex.Dispose(); ContentService.Collect();
            Assert.That(t != null && ReferenceEquals(sp.texture, t), Is.True, "D2 ⓓ: the texture waits while its sprite is held");
            spr.Dispose(); ContentService.Collect();
            Assert.That(t == null && sp == null, Is.True, "D2 ⓓ: both go with the path's last slot");

            ContentService.UnloadResourcesOnRelease = false;
            var mine = ContentService.Acquire<Texture2D>(k_Logo); var legacy = Resources.Load<Texture2D>("ContentLinkTests/logo");
            Assert.That(ReferenceEquals(mine.Asset, legacy), Is.True, "sanity: the engine hands both the same object");
            mine.Dispose(); ContentService.Collect();
            Assert.That(legacy != null, Is.True, "D3 off: a reference taken with Resources.Load lives");
        }

        /// <summary>T23: a prefab is not unloaded on release (the engine refuses GameObjects) — SweepResources reclaims it.</summary>
        [Test]
        public async Task T23_APrefab_LeavesWithTheSweep()
        {
            PlayerOnly();
            var hero = ContentService.Acquire<GameObject>("res:ContentLinkTests/hero");
            Assert.That(hero.IsValid, Is.True);
            hero.Dispose(); ContentService.Collect();
            Assert.That(InMemory<GameObject>("hero"), Is.True, "a prefab stays after its release");
            await ContentService.SweepResources();
            Assert.That(InMemory<GameObject>("hero"), Is.False, "the sweep reclaims it");
        }

        /// <summary>T24: a live res: asset belongs to no content directory — it never blocks UnregisterDirectory.</summary>
        [Test]
        public void T24_ALiveResAsset_DoesNotBlockADirectory()
        {
            int dirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
            var held = ContentService.Acquire<Texture2D>(k_Logo);
            bool unregistered = false;
            try
            {
                Assert.That(held.IsValid, Is.True);
                unregistered = ContentService.UnregisterDirectory(dirA);
                Assert.That(unregistered, Is.True, "a live res: slot blocked the directory's unregistration");
            }
            finally
            {
                held.Dispose();
                if (!unregistered) ContentService.UnregisterDirectory(dirA, force: true);
            }
        }

        /// <summary>
        /// A Resources folder under an Editor folder is not built — Play Mode in the editor fails its name as a
        /// player does — and a path such a file shares with a player's file loads the player's (the editor's Resources gives either).
        /// </summary>
        [Test]
        public async Task T26_AnEditorOnlyFile_FailsAsInAPlayer_APathItSharesLoadsThePlayersFile()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("'res:ContentLinkTests/edonly' is not in a Resources folder of this build"));
            var edonly = ContentService.Acquire<TextAsset>("res:ContentLinkTests/edonly");
            var pair = ContentService.Acquire<TextAsset>("res:ContentLinkTests/pair");
            AssetHandle<TextAsset> later = default;
            try
            {
                Assert.That(edonly.IsValid, Is.False, "an editor-only file loaded");
                Assert.That(pair.IsValid ? pair.Asset.text : "nothing", Is.EqualTo("player"), "sync");
                pair.Dispose(); pair = default; ContentService.Collect();
                later = await ContentService.AcquireAsync<TextAsset>("res:ContentLinkTests/pair");
                Assert.That(later.IsValid ? later.Asset.text : "nothing", Is.EqualTo("player"), "async");
            }
            finally { edonly.Dispose(); pair.Dispose(); later.Dispose(); }
        }

        /// <summary>
        /// T28: a sweep while the sprite holds the path keeps the texture's deferral — both go with the
        /// path's last slot; with the switch off that slot only lets go, and the sweep takes the texture.
        /// </summary>
        [Test]
        public async Task T28_ASweepKeepsADeferral_AndTheSwitchOffLeavesItToTheSweep()
        {
            PlayerOnly();
            var tex = ContentService.Acquire<Texture2D>(k_Sheet); var spr = ContentService.Acquire<Sprite>("res:ContentLinkTests/sheet[s0]");
            var t = tex.Asset; var sp = spr.Asset;
            tex.Dispose(); ContentService.Collect();
            await ContentService.SweepResources();
            Assert.That(t != null, Is.True, "sanity: the sprite holds the texture through the sweep");
            spr.Dispose(); ContentService.Collect();
            Assert.That(t == null && sp == null, Is.True, "both go with the path's last slot — the sweep between kept the deferral");

            tex = ContentService.Acquire<Texture2D>(k_Sheet); spr = ContentService.Acquire<Sprite>("res:ContentLinkTests/sheet[s0]");
            t = tex.Asset;
            tex.Dispose(); ContentService.Collect();
            ContentService.UnloadResourcesOnRelease = false;
            spr.Dispose(); ContentService.Collect();
            Assert.That(t != null, Is.True, "the switch off unloads nothing");
            await ContentService.SweepResources();
            Assert.That(t == null, Is.True, "the path's last slot let go of the deferred texture, so the sweep takes it");
        }
    }
}
