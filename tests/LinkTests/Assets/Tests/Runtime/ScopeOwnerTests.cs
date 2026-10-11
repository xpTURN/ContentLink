using System;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Against the real engine: the scope a GameObject's owner opens — a clone touched before its Awake takes its keys
    /// at the touch, an explicit Dispose empties the key list, and while the object is destroyed Scope() is the ended scope (an ended one too
    /// for an object that never had a scope).
    /// Directory A stays registered for the fixture.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Lifetime")]
    public class ScopeOwnerTests
    {
        int m_DirA = -1;
        readonly System.Collections.Generic.List<GameObject> m_Objects = new();

        [OneTimeSetUp]
        public void Register() => m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));

        [OneTimeTearDown]
        public void Unregister()
        {
            bool ok = m_DirA < 0 || ContentService.UnregisterDirectory(m_DirA);
            if (!ok) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = -1;
            Assert.That(ok, Is.True, "directory A still had live assets at fixture end");
        }

        static async Task Frames(int n) { for (int i = 0; i < n; ++i) { ContentService.Collect(); await Awaitable.NextFrameAsync(); } }

        static async Task Settle(int frames = 60)   // the engine finishes a cancelled load before its slot can go
        {
            for (int i = 0; i < frames && ContentService.LiveAssetCount > 0; ++i) { ContentService.Collect(); await Awaitable.NextFrameAsync(); }
        }

        GameObject Track(GameObject go) { m_Objects.Add(go); return go; }

        [UnityTearDown]
        public System.Collections.IEnumerator DestroyLeftovers()
        {
            foreach (var go in m_Objects) if (go != null) { go.SetActive(true); Object.Destroy(go); }   // activated first: a never-activated object gets no OnDestroy
            m_Objects.Clear();
            for (int i = 0; i < 60 && ContentService.LiveAssetCount > 0; ++i) { ContentService.Collect(); yield return null; }
        }

        static bool HasScope(GameObject go)   // the owner's scope, without opening one (the Scope getter would)
            => typeof(ContentScopeOwner).GetField("m_Scope", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(go.GetComponent<ContentScopeOwner>()) != null;

        /// <summary>
        /// T70: a clone created inactive and touched before its Awake takes the original's keys at the touch — the original's
        /// texture stays loaded after the original is destroyed, so what the clone copied from it still points at a live texture; its Awake
        /// takes no second reference.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T70_ACloneTouchedBeforeItsAwake_HoldsTheOriginalsAssets()
        {
            var original = Track(new GameObject("T70"));
            var holder = original.AddComponent<TextureHolder>();
            holder.texture = original.Scope().Load<Texture2D>("Icons/single");
            var parent = Track(new GameObject("T70Parent")); parent.SetActive(false);
            var clone = Object.Instantiate(original, parent.transform);   // inactive: its Awake waits for the activation
            clone.Scope();                                                 // touched first — the pooled clone's initialisation
            Object.Destroy(original);
            await Frames(5);
            var copied = clone.GetComponent<TextureHolder>();
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "the clone holds the texture the original loaded");
            Assert.That(copied.texture != null, Is.True, "what the clone copied from the original still points at a live texture");
            parent.SetActive(true);
            await Frames(2);
            Assert.That(clone.Scope().Contains("Icons/single") && clone.Scope().Count == 1, Is.True, "its Awake took no second reference");
            Object.Destroy(parent);
            await Frames(3);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "released when the clone is destroyed");
        }

        /// <summary>
        /// T71: while the object is destroyed Scope() hands back the ended scope — to a load's cancellation that runs
        /// inside the owner's OnDestroy and to a sibling's OnDestroy — so nothing is left behind that no OnDestroy would dispose.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T71_TheScopeAskedWhileItsObjectIsDestroyed_IsTheEndedOne()
        {
            var go = Track(new GameObject("T71"));
            var scope = go.Scope();
            var hook = go.AddComponent<DestroyHook>();   // after the owner: its OnDestroy runs later
            var pending = scope.LoadAsync<Texture2D>("Icons/single");
            Assume.That(pending.GetAwaiter().IsCompleted, Is.False, "the load is in flight");
            ContentScope fromCancel = null, fromSibling = null;
            async void Observe()
            {
                try { await pending; } catch (OperationCanceledException) { }
                fromCancel = go.Scope();
            }
            Observe();
            hook.whenDestroyed = () => fromSibling = go.Scope();
            Object.Destroy(go);
            await Frames(3);
            await Settle();
            Assert.That(ReferenceEquals(fromCancel, scope) && scope.IsDisposed, Is.True, "a load's cancellation inside OnDestroy gets the ended scope");
            Assert.That(ReferenceEquals(fromSibling, scope), Is.True, "a sibling's OnDestroy gets the ended scope");
            Assert.That(ContentScope.DumpTree(), Does.Not.Contain("T71"), "no scope is left behind");
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
        }

        /// <summary>T72: after an explicit Dispose a clone made from the object inherits nothing, and the next Scope() starts empty.</summary>
        [Test, Timeout(30000)]
        public async Task T72_AnExplicitDispose_ClonesMadeAfterwardInheritNothing()
        {
            var go = Track(new GameObject("T72"));
            var scope = go.Scope();
            Assert.That(scope.Load<Texture2D>("Icons/single"), Is.Not.Null);
            scope.Dispose();
            await Frames(3);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
            var clone = Track(Object.Instantiate(go));
            Assert.That(HasScope(clone) || ContentService.LiveAssetCount != 0, Is.False, "the clone inherits nothing and opens no scope");
            var next = go.Scope();
            Assert.That(next.Count, Is.EqualTo(0), "the next Scope() starts empty");
            Object.Destroy(go); Object.Destroy(clone);
            await Frames(2);
        }

        /// <summary>
        /// T74 (the README's caveat — an engine fact): a clone created inactive and left untouched holds nothing until its Awake. If the
        /// original goes first its texture unloads; the Awake loads the key again — a new object — and what the clone copied from the original
        /// still points at the old one.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T74_AnUntouchedInactiveClone_ReloadsANewObject_ItsCopiedReferenceStaysMissing()
        {
            var original = Track(new GameObject("T74"));
            var holder = original.AddComponent<TextureHolder>();
            holder.texture = original.Scope().Load<Texture2D>("Icons/single");
            var parent = Track(new GameObject("T74Parent")); parent.SetActive(false);
            var clone = Object.Instantiate(original, parent.transform);
            var copied = clone.GetComponent<TextureHolder>();
            Object.Destroy(original);
            await Frames(5);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "untouched, the inactive clone held nothing");
            Assert.That(copied.texture == null, Is.True, "the original's texture unloaded");
            parent.SetActive(true);                                        // Awake: the original is gone — the key is loaded again
            await Frames(2);
            var reloaded = clone.Scope().Load<Texture2D>("Icons/single");
            Assert.That(reloaded != null && ContentService.LiveAssetCount == 1, Is.True, "the clone's scope holds the key again");
            Assert.That(copied.texture == null, Is.True, "a new object: what the clone copied still points at the old one");
            Object.Destroy(parent);
            await Frames(3);
        }

        /// <summary>
        /// T76: an object that never had a scope — Scope() asked while it is destroyed is an ended scope too, not a NullReferenceException
        /// from inside Scope(): the engine adds no component to an object being destroyed.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T76_AnObjectThatNeverHadAScope_AskedWhileDestroyed_GetsAnEndedOne()
        {
            var go = Track(new GameObject("T76"));
            var hook = go.AddComponent<DestroyHook>();
            ContentScope seen = null; string error = "none", load = "-";
            hook.whenDestroyed = () =>
            {
                try { seen = go.Scope(); } catch (Exception e) { error = e.GetType().Name; return; }
                try { seen.Load<Texture2D>("Icons/single"); load = "loaded"; } catch (ObjectDisposedException) { load = "ODE"; }
            };
            Object.Destroy(go);
            await Frames(2);
            Assert.That(error, Is.EqualTo("none"), "Scope() answers, it does not throw");
            Assert.That(seen != null && seen.IsDisposed, Is.True, "an ended scope");
            Assert.That(load, Is.EqualTo("ODE"), "a load into it is ObjectDisposedException");
            Assert.That(ContentScope.DumpTree(), Does.Not.Contain("T76"), "nothing is left behind");
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
        }

        /// <summary>
        /// T77: an active clone whose earlier component's Awake asks for the scope before the owner's own Awake
        /// (the owner is added after the user's components) still takes the original's keys — the original's texture stays loaded after it goes.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T77_AnEarlierComponentsAwakeAsksForTheScope_TheCloneStillTakesTheOriginalsAssets()
        {
            var original = Track(new GameObject("T77"));
            original.AddComponent<AwakeHook>();   // before the owner: Scope() adds the owner after it
            var holder = original.AddComponent<TextureHolder>();
            holder.texture = original.Scope().Load<Texture2D>("Icons/single");
            ContentScope seen = null;
            AwakeHook.whenAwake = go => seen = go.Scope();
            GameObject clone;
            try { clone = Track(Object.Instantiate(original)); } finally { AwakeHook.whenAwake = null; }
            Assert.That(seen, Is.Not.Null, "the clone's earlier component asked for the scope in its Awake");
            Object.Destroy(original);
            await Frames(5);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(1), "the clone holds the texture the original loaded");
            Assert.That(clone.GetComponent<TextureHolder>().texture != null, Is.True, "what the clone copied still points at a live texture");
            Object.Destroy(clone);
            await Frames(3);
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
        }

        /// <summary>
        /// T78: an explicit Dispose whose own cancellation asks for the scope gets a new empty one — what the Dispose dropped stays
        /// dropped, a clone made afterwards inherits nothing, and that new scope stays the object's.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T78_AnExplicitDisposeWhoseCancellationAsksForTheScope_KeepsWhatItDroppedDropped()
        {
            var go = Track(new GameObject("T78"));
            var scope = go.Scope();
            Assert.That(scope.Load<Texture2D>("Icons/single"), Is.Not.Null);
            var pending = scope.LoadAsync<Object>("Audio/tone");
            Assume.That(pending.GetAwaiter().IsCompleted, Is.False, "the load is in flight");
            ContentScope seen = null; bool emptyThen = false;
            async void Observe()
            {
                try { await pending; } catch (OperationCanceledException) { }
                seen = go.Scope(); emptyThen = !seen.IsDisposed && seen.Count == 0;
            }
            Observe();
            scope.Dispose();
            await Frames(3);
            await Settle();
            Assert.That(seen != null && emptyThen, Is.True, "the cancellation got a new empty scope");
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "nothing the Dispose dropped is held again");
            var clone = Track(Object.Instantiate(go));
            Assert.That(HasScope(clone), Is.False, "a clone made afterwards inherits nothing");
            Assert.That(ReferenceEquals(go.Scope(), seen), Is.True, "the scope the cancellation got stays the object's scope");
        }
    }
}
