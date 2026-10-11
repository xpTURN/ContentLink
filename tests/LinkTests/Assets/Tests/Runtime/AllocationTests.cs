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
        readonly string[] m_ThreeKeys = { "Icons/single", "Audio/tone", "Materials/Plain" };   // TestContent/A
        readonly string m_FreshA = "Audio/tone";        // not loaded by Warm: a first AcquireAsync opens the slot and starts the engine load
        readonly string m_FreshB = "Materials/Plain";
        readonly string m_ResTexture = "res:ContentLinkTests/logo";   // Assets/Tests/Resources/ContentLinkTests
        readonly string m_ResSheet = "res:ContentLinkTests/sheet";
        readonly string m_ResSub = "s0";
        readonly string m_ResMissing = "res:ContentLinkTests/nope";
        readonly string m_ResS0 = "res:ContentLinkTests/sheet[s0]";
        readonly string m_ResS1 = "res:ContentLinkTests/sheet[s1]";
        readonly System.Threading.CancellationTokenSource m_Cts = new System.Threading.CancellationTokenSource();

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

        [Test, Timeout(10000)]
        public void G1g_ResourcesNames_HitAndFormatQuestions_DoNotAllocate()
        {
            // T25: a res: name on a live slot takes the same dictionary hit as a catalog name, and the questions look
            // at the name's format only — a path no Resources folder has included.
            var link = new ContentLink<Sprite>(m_ResSheet, m_ResSub);
            Assert.That(m_Scope.Load<Texture2D>(m_ResTexture), Is.Not.Null);
            Assert.That(link.Load(m_Scope), Is.Not.Null);
            try
            {
                AssertNoAlloc(() => { m_Scope.Load<Texture2D>(m_ResTexture); });
                AssertNoAlloc(() => { m_Scope.Contains(m_ResTexture); });
                AssertNoAlloc(() => { ContentService.Contains(m_ResMissing); });
                AssertNoAlloc(() => { var _ = link.Asset; });
                AssertNoAlloc(() => { link.RuntimeKeyIsValid(); });
            }
            finally { link.Release(); }
        }

        [Test, Timeout(10000)]
        public void G1h_ResourcesRelease_DoesNotAllocate()
        {
            // The release of res: slots runs inside Collect — the sheet's texture waits while its sprites hold
            // the path, then the path's last slot lets everything go — and allocates nothing.
            AssetHandle<Texture2D> tex = default; AssetHandle<Sprite> s0 = default, s1 = default;
            System.Action open = () =>
            {
                tex = ContentService.Acquire<Texture2D>(m_ResSheet); s0 = ContentService.Acquire<Sprite>(m_ResS0); s1 = ContentService.Acquire<Sprite>(m_ResS1);
                tex.Dispose(); s0.Dispose(); s1.Dispose();
            };
            int live = ContentService.LiveAssetCount;
            open(); ContentService.Collect();   // JIT and literals, the open and the release
            open();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live + 3), "sanity: three res: slots wait for the Collect");
            Assert.That(() => { ContentService.Collect(); }, Is.Not.AllocatingGCMemory());
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live), "the Collect released them");
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

        [Test, Timeout(10000)]
        public void G3_GameObjectScopeReleaseThenLoad_DoesNotAllocate()
        {
            // A GameObject scope mirrors its keys into the owner's serialized array (what clones inherit). The array is
            // written in place and never shrinks, so a Release followed by a Load again — a pooled cell swapping its
            // sprite — allocates nothing. m_Scope holds the same key, so the slot stays live in between.
            var scope = m_Owner.Scope();
            Assert.That(scope.Load<Texture2D>(m_Texture), Is.Not.Null);
            AssertNoAlloc(() => { scope.Release(m_Texture); scope.Load<Texture2D>(m_Texture); });
            Assert.That(scope.Contains(m_Texture), Is.True);
        }

        [Test, Timeout(10000)]
        public void G3b_CloneOfAKeyedOwner_TakesEveryKey_Recorded()
        {
            // Instantiate copies the owner's key array and the clone's Awake takes its own leases, syncing the array once
            // after the loop — never into the array it is walking. Asserts the clone holds every key; records the
            // GC.Alloc blocks of the Instantiate (the engine's clone included), never fails on the count.
            var original = new GameObject("G3b");
            var scope = original.Scope();
            foreach (var key in m_ThreeKeys) Assert.That(scope.Load<Object>(key), Is.Not.Null);
            var first = Object.Instantiate(original);   // JIT and literals first
            GameObject clone = null;
            int blocks = AllocBlocks(() => clone = Object.Instantiate(original));
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks per Instantiate of an owner with {m_ThreeKeys.Length} keys — {blocks} (engine clone included)");
            foreach (var key in m_ThreeKeys) Assert.That(clone.Scope().Contains(key), Is.True, key);
            Assert.That(clone.Scope().Count, Is.EqualTo(m_ThreeKeys.Length));
            Object.Destroy(clone); Object.Destroy(first); Object.Destroy(original);
        }

        [Test, Timeout(10000)]
        public void G3d_FirstScopeOfATouchedInactiveClone_TakesEveryKey_Recorded()
        {
            // A clone created inactive takes the original's keys when its scope opens — at the first Scope() when that
            // comes before its Awake. Asserts the clone holds every key; records the GC.Alloc blocks of that first Scope() (the clone's
            // scope and its leases), never fails on the count.
            var original = new GameObject("G3d");
            var scope = original.Scope();
            foreach (var key in m_ThreeKeys) Assert.That(scope.Load<Object>(key), Is.Not.Null);
            original.SetActive(false);
            var first = Object.Instantiate(original); first.Scope();   // JIT and literals first
            var clone = Object.Instantiate(original);
            ContentScope cloneScope = null;
            int blocks = AllocBlocks(() => cloneScope = clone.Scope());
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks per first Scope() of an inactive clone of an owner with {m_ThreeKeys.Length} keys — {blocks}");
            foreach (var key in m_ThreeKeys) Assert.That(cloneScope.Contains(key), Is.True, key);
            Assert.That(cloneScope.Count, Is.EqualTo(m_ThreeKeys.Length));
            clone.SetActive(true); first.SetActive(true);   // a never-activated clone gets no OnDestroy: activated, it releases its keys when destroyed
            Object.Destroy(clone); Object.Destroy(first); Object.Destroy(original);
        }

        static bool HasScope(GameObject go)   // the owner's scope, without creating one (the Scope getter would)
            => typeof(ContentScopeOwner).GetField("m_Scope", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(go.GetComponent<ContentScopeOwner>()) != null;

        [Test, Timeout(10000)]
        public void G3c_CloneOfAnEmptiedOwner_MakesNoScope_AndDoesNotAllocate()
        {
            // The owner's key array never shrinks, so an owner that released every key still carries an array, which a
            // clone receives as empty strings. The clone's Awake sees no key at the front and returns before making a
            // scope: no scope, no child in the scene scope, no allocation (it was a scope and its collections — six
            // blocks — per clone). Clones of an inactive original come out inactive, so the activation measured is the
            // clone's own first Awake; AssertNoAlloc cannot be used here because Awake runs only once per object.
            var original = new GameObject("G3c");
            var scope = original.Scope();
            foreach (var key in m_ThreeKeys) Assert.That(scope.Load<Object>(key), Is.Not.Null);
            original.SetActive(false);
            var keyed = Object.Instantiate(original);
            keyed.SetActive(true);
            Assert.That(keyed.Scope().Count, Is.EqualTo(m_ThreeKeys.Length), "control: a clone activated this way takes the keys in Awake");
            foreach (var key in m_ThreeKeys) Assert.That(scope.Release(key), Is.True);
            var sceneScope = ContentScope.ForScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            int children = sceneScope.Children.Count;
            var first = Object.Instantiate(original);
            first.SetActive(true);   // JIT first
            var clone = Object.Instantiate(original);
            Assert.That(() => clone.SetActive(true), Is.Not.AllocatingGCMemory());
            Assert.That(clone.activeSelf, Is.True);
            Assert.That(HasScope(first) || HasScope(clone), Is.False, "the clone of an emptied owner makes no scope in Awake");
            Assert.That(sceneScope.Children.Count, Is.EqualTo(children), "the scene scope gains no child");
            Object.Destroy(clone); Object.Destroy(first); Object.Destroy(keyed); Object.Destroy(original);
        }

        [Test, Timeout(10000)]
        public void G4_AcquireById_OnALiveSlot_DoesNotAllocate()
        {
            // The id path looks its slot up by the id itself; the "#…" key text is made once when the slot opens.
            Assert.That(ContentService.TryGetLoadableObjectId(m_Texture, out var id), Is.True);
            var keep = ContentService.Acquire<Texture2D>(id);   // opens the id slot and keeps it live
            Assert.That(keep.IsValid, Is.True);
            try { AssertNoAlloc(() => { var h = ContentService.Acquire<Texture2D>(id); h.Dispose(); }); }
            finally { keep.Dispose(); }
        }

        [Test, Timeout(10000)]
        public void G4b_AcquireById_OnALiveSlot_Recorded()
        {
            Assert.That(ContentService.TryGetLoadableObjectId(m_Texture, out var id), Is.True);
            var keep = ContentService.Acquire<Texture2D>(id);
            System.Action acquire = () => { var h = ContentService.Acquire<Texture2D>(id); h.Dispose(); };
            acquire();
            int blocks = AllocBlocks(acquire);
            keep.Dispose();
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks per Acquire by id on a live slot — {blocks}");
        }

        [Test, Timeout(10000)]
        public async System.Threading.Tasks.Task G5_SecondWaiterOnALoadingSlot_Recorded()
        {
            // The first AcquireAsync opens the slot and starts the engine load; a second one in the same frame joins as a
            // waiter while the slot is still Loading. Records that waiter's GC.Alloc blocks with a default token and with a
            // cancellable one (what a scope passes); never fails on the count.
            var firstA = ContentService.AcquireAsync<Object>(m_FreshA);
            bool loadingA = !firstA.GetAwaiter().IsCompleted;
            Awaitable<AssetHandle<Object>> secondA = null;
            int blocksA = AllocBlocks(() => secondA = ContentService.AcquireAsync<Object>(m_FreshA));
            var token = m_Cts.Token;
            var firstB = ContentService.AcquireAsync<Object>(m_FreshB, token);
            bool loadingB = !firstB.GetAwaiter().IsCompleted;
            Awaitable<AssetHandle<Object>> secondB = null;
            int blocksB = AllocBlocks(() => secondB = ContentService.AcquireAsync<Object>(m_FreshB, token));
            var a1 = await firstA; var a2 = await secondA; var b1 = await firstB; var b2 = await secondB;
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks per AcquireAsync joining a Loading slot — default token={blocksA} (was loading={loadingA}) cancellable token={blocksB} (was loading={loadingB})");
            Assert.That(a1.IsValid && a2.IsValid && b1.IsValid && b2.IsValid, Is.True);
            a1.Dispose(); a2.Dispose(); b1.Dispose(); b2.Dispose();
        }

        [Test, Timeout(10000)]
        public async System.Threading.Tasks.Task G6_FirstScopeOfANewScene_AndOfAGameObject_Recorded()
        {
            // The first ForScene of a scene creates its scope (name read from the engine); the first go.Scope() adds the
            // owner component and creates the GameObject scope. Records both (engine work included); never fails on the count.
            var warmScene = UnityEngine.SceneManagement.SceneManager.CreateScene("G6w");   // JIT and literals first
            ContentScope.ForScene(warmScene); var warm = new GameObject("G6w"); warm.Scope();
            var scene = UnityEngine.SceneManagement.SceneManager.CreateScene("G6");
            ContentScope sceneScope = null;
            int sceneBlocks = AllocBlocks(() => sceneScope = ContentScope.ForScene(scene));
            var go = new GameObject("G6");
            ContentScope goScope = null;
            int goBlocks = AllocBlocks(() => goScope = go.Scope());
            Debug.Log($"[ContentLink.Tests] GC.Alloc blocks — first ForScene of a new scene={sceneBlocks} · first go.Scope()={goBlocks} (engine AddComponent included)");
            Assert.That(sceneScope.Kind == ContentScopeKind.Scene && goScope.Kind == ContentScopeKind.GameObject, Is.True);
            Object.Destroy(go); Object.Destroy(warm);   // the warm-up object lives in the active scene, not in warmScene: nothing else would remove it
            await UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(scene);
            await UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(warmScene);
            Assert.That(ContentScope.DumpTree(), Does.Not.Contain("G6w"), "the warm-up scene and object leave no scope behind");
        }
    }
}
