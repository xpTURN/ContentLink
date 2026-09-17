using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Per-frame paths must not allocate: idle Collect, a sync Load cache hit, ContentLink.Asset, and the lookups.
    ///
    /// Measurement rule: never put a string literal inside the measured delegate. Mono materialises a
    /// literal (ldstr) the first time a method runs, and that counts as one GC.Alloc block — the package path itself
    /// is clean. Keys are therefore held in fields and every delegate is executed once before it is measured.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Allocation")]
    public class AllocationTests
    {
        readonly string m_Texture = "Icons/single";
        readonly string m_SpriteMain = "Icons/single";
        readonly string m_SpriteSub = "single";
        readonly string m_MissingSub = "nope";
        readonly string m_Missing = "Icons/nope";
        readonly ContentRef<Texture2D> m_TextureRef = new ContentRef<Texture2D>("2db49f3267d3454694e93fa3d8354b29");   // TestContent/A/Icons/single.png
        readonly ContentRef<Texture2D> m_MissingRef = new ContentRef<Texture2D>("00000000000000000000000000000000");

        int m_DirA = -1;
        ContentScope m_Scope;
        GameObject m_Owner;
        LinkHolder m_Holder;

        [OneTimeSetUp]
        public void RegisterA()
        {
            Assert.That(System.IO.Directory.Exists(TestContent.Dir("A")), Is.True, "run the EditMode test TestContentBuildTests first");
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
        }

        [OneTimeTearDown]
        public void UnregisterA()
        {
            if (m_DirA < 0) return;
            bool ok = ContentService.UnregisterDirectory(m_DirA);
            if (!ok) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = -1;
            Assert.That(ok, Is.True, "directory A still had live assets at fixture end (leak)");
        }

        [UnitySetUp]
        public IEnumerator Warm()
        {
            TestUtil.RestoreStatics();
            m_Scope = ContentScope.Root.CreateChild("G1");
            Assert.That(m_Scope.Load<Texture2D>(m_Texture), Is.Not.Null);
            m_Owner = new GameObject("G1");
            m_Holder = m_Owner.AddComponent<LinkHolder>();
            m_Holder.icon.SetName(m_SpriteMain, m_SpriteSub);
            Assert.That(m_Holder.icon.Load(m_Holder), Is.Not.Null);
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            m_Scope?.Dispose();
            if (m_Owner != null) Object.Destroy(m_Owner);
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i) { ContentService.Collect(); yield return null; }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
        }

        /// <summary>Runs the delegate once (JIT, literals) and then asserts it allocates nothing.</summary>
        static void AssertNoAlloc(TestDelegate action)
        {
            action();
            Assert.That(action, Is.Not.AllocatingGCMemory());
        }

        [Test, Timeout(10000)]
        public void G1a_IdleCollect_DoesNotAllocate()
        {
            AssertNoAlloc(() => { ContentService.Collect(); });
        }

        [Test, Timeout(10000)]
        public void G1b_SyncLoadCacheHit_AndScopeContains_DoNotAllocate()
        {
            AssertNoAlloc(() => { m_Scope.Load<Texture2D>(m_Texture); });
            AssertNoAlloc(() => { m_Scope.Contains(m_Texture); });
            AssertNoAlloc(() => { m_Scope.Contains(m_Missing); });
        }

        [Test, Timeout(10000)]
        public void G1c_LinkAssetAndServiceLookups_DoNotAllocate()
        {
            AssertNoAlloc(() => { var _ = m_Holder.icon.Asset; });
            AssertNoAlloc(() => { var _ = m_Holder.icon.IsValid; });
            AssertNoAlloc(() => { ContentService.TryGetLoadableObjectId(m_Texture, out _); });
            AssertNoAlloc(() => { ContentService.Contains(m_Texture); });
            AssertNoAlloc(() => { ContentService.Contains(m_Missing); });
        }

        [Test, Timeout(10000)]
        public void G1e_ReferenceLookups_AndAReferenceCacheHit_DoNotAllocate()
        {
            // a reference resolves to its catalog name through a dictionary, so the per-frame paths stay clean —
            // including a miss, and a scope cache hit (the set-up holds Icons/single, the name m_TextureRef resolves to).
            AssertNoAlloc(() => { ContentService.TryGetName(m_TextureRef, out _); });
            AssertNoAlloc(() => { ContentService.Contains(m_TextureRef); });
            AssertNoAlloc(() => { ContentService.Contains(m_MissingRef); });
            AssertNoAlloc(() => { m_Scope.Contains(m_TextureRef); });
            AssertNoAlloc(() => { m_Scope.Load(m_TextureRef); });
        }

        [Test, Timeout(10000)]
        public void G1f_LinkSpriteNameLookups_HitAndMiss_DoNotAllocate()
        {
            // a (name, sprite) pair is composed once, and a miss is remembered while no editor fallback is installed
            // (RestoreStatics removes it, so this is the player's contract).
            AssertNoAlloc(() => { ContentService.Contains(m_SpriteMain, m_SpriteSub); });
            AssertNoAlloc(() => { ContentService.TryGetName(m_SpriteMain, m_SpriteSub, out _); });
            AssertNoAlloc(() => { ContentService.Contains(m_SpriteMain, m_MissingSub); });
            AssertNoAlloc(() => { m_Holder.icon.RuntimeKeyIsValid(); });
        }

        // ───────────── the measurement artifact, kept as a guard for future test authors (records, never fails) ─────────────

        static int AllocBlocks(System.Action action)
        {
            var recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            try { action(); }
            finally { recorder.enabled = false; }
            return recorder.sampleBlockCount;
        }

        [Test, Timeout(10000)]
        public void G1d_StringLiteralInsideDelegate_Recorded()
        {
            string captured = m_Texture;
            int literal = AllocBlocks(() => ContentService.Contains("Icons/single"));   // ldstr inside the measured delegate
            int viaField = AllocBlocks(() => ContentService.Contains(m_Texture));
            int viaLocal = AllocBlocks(() => ContentService.Contains(captured));
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks — literal-in-delegate={literal} via-field={viaField} via-local={viaLocal} (the literal counts, the package path does not)");
            Assert.That(viaField, Is.EqualTo(0));
            Assert.That(viaLocal, Is.EqualTo(0));
        }

        [Test, Timeout(10000)]
        public void G2_LinkLoadAsync_DefaultTokenVersusCancellableToken_Recorded()
        {
            // The scope's own token already cancels a load when the scope ends, so a default token should not pay
            // for a linked CancellationTokenSource on every load. Records the GC.Alloc blocks of both paths on a live
            // slot (the async path completes synchronously there); never fails.
            var holder = m_Holder; var cts = new System.Threading.CancellationTokenSource();
            System.Action viaDefault = () => { holder.icon.Release(); holder.icon.LoadAsync(holder); };
            System.Action viaToken = () => { holder.icon.Release(); holder.icon.LoadAsync(holder, cts.Token); };
            viaDefault(); viaToken();   // JIT and literals first
            int withDefault = AllocBlocks(viaDefault);
            int withToken = AllocBlocks(viaToken);
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks per ContentLink.LoadAsync on a live slot — default token={withDefault} cancellable token={withToken}");
            Assert.That(holder.icon.IsValid, Is.True);
        }
    }
}
