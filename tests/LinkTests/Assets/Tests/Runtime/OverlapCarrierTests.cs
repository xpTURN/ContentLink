using System.Collections;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Test content I's root holds I.Sub's — I names TestContent/I/Sub/nest.png "Sub/nest", I.Sub names it "nest": one
    /// asset in two directories. The engine gives it from the one registered later, whichever name opened it (measured on 6000.6, editor and
    /// player), so that directory may not go while the asset is held.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("OverlapCarrier")]
    public class OverlapCarrierTests
    {
        int m_Sub = -1, m_I = -1;

        [SetUp]
        public void Before()
        {
            TestUtil.RestoreStatics();
            TestUtil.SetSimulation(false);   // only the built directories may answer
        }

        [UnityTearDown]
        public IEnumerator After()
        {
            if (m_I >= 0 && !ContentService.UnregisterDirectory(m_I)) ContentService.UnregisterDirectory(m_I, force: true);
            if (m_Sub >= 0 && !ContentService.UnregisterDirectory(m_Sub)) ContentService.UnregisterDirectory(m_Sub, force: true);
            m_I = m_Sub = -1;
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i)
            {
                ContentService.Collect();
                yield return null;
            }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "live assets remain after the test");
        }

        void Sub() => m_Sub = ContentService.RegisterDirectory(TestContent.Dir("I.Sub"));
        void Outer() => m_I = ContentService.RegisterDirectory(TestContent.Dir("I"));
        static async Task Frames(int n) { for (int i = 0; i < n; ++i) await TestUtil.NextFrame(); }

        /// <summary>O1: I registered after I.Sub gives "nest" — I may not go while it is held; once released it goes, and the name loads from I.Sub.</summary>
        [Test, Timeout(20000)]
        public async Task O1_TheDirectoryThatGaveIt_IsRefusedWhileItIsHeld()
        {
            Sub(); Outer();
            var held = ContentService.Acquire<Texture2D>("nest");
            var obj = held.Asset;
            Assert.That(obj, Is.Not.Null, "nest");
            var other = ContentService.Acquire<Texture2D>("Sub/nest");
            bool one = ReferenceEquals(other.Asset, obj);
            other.Dispose();
            string error = null;
            void Catch(string message, string stack, LogType type) { if (type == LogType.Error) error = message; }
            LogAssert.Expect(LogType.Error, new Regex("ContentLinkTests.I' still has 1 live asset"));
            Application.logMessageReceived += Catch;
            bool refused = !ContentService.UnregisterDirectory(m_I);
            Application.logMessageReceived -= Catch;
            await Frames(3);
            bool alive = obj != null && ReferenceEquals(held.Asset, obj);
            held.Dispose();
            ContentService.Collect();
            await Frames(2);
            bool gone = ContentService.UnregisterDirectory(m_I);
            if (gone) m_I = -1;
            var again = ContentService.Acquire<Texture2D>("nest");
            bool back = again.Asset != null;
            again.Dispose();
            Assert.That(one, Is.True, "I's name 'Sub/nest' gives the same object");
            Assert.That(refused, Is.True, "refused while held");
            StringAssert.Contains("nest refs=1 status=Loaded — a name of '", error, "the asset, and the directory whose name opened it");
            StringAssert.Contains("I.Sub', loaded from this directory", error);
            Assert.That(alive, Is.True, "the held object lives");
            Assert.That(gone, Is.True, "goes once released");
            Assert.That(back, Is.True, "the name loads from I.Sub again");
        }

        /// <summary>O2: the directory whose name opened it still refuses.</summary>
        [Test, Timeout(20000)]
        public async Task O2_TheDirectoryWhoseNameOpenedIt_StillRefuses()
        {
            Sub(); Outer();
            var held = ContentService.Acquire<Texture2D>("nest");
            LogAssert.Expect(LogType.Error, new Regex("I\\.Sub' still has 1 live asset"));
            bool refused = !ContentService.UnregisterDirectory(m_Sub);
            held.Dispose();
            await Frames(2);
            Assert.That(refused, Is.True);
        }

        /// <summary>
        /// O3: opened while I.Sub alone carried it, the object is I.Sub's — I, registered afterwards, gives I's name a copy of its own and
        /// goes while "nest" is held; "nest" asked again is the same object.
        /// </summary>
        [Test, Timeout(20000)]
        public async Task O3_OpenedBeforeTheLaterDirectory_TheLaterOneGoes()
        {
            Sub();
            var held = ContentService.Acquire<Texture2D>("nest");
            var obj = held.Asset;
            Outer();
            var other = ContentService.Acquire<Texture2D>("Sub/nest");
            bool two = other.Asset != null && obj != null && !ReferenceEquals(other.Asset, obj);   // a new load takes the later directory's copy (measured)
            other.Dispose();
            var again = ContentService.Acquire<Texture2D>("nest");
            bool same = ReferenceEquals(again.Asset, obj);
            again.Dispose();
            ContentService.Collect();
            await Frames(2);
            bool gone = ContentService.UnregisterDirectory(m_I);
            if (gone) m_I = -1;
            await Frames(3);
            bool alive = obj != null && ReferenceEquals(held.Asset, obj);
            held.Dispose();
            Assert.That(two, Is.True, "I's name, opened after I was registered, is I's own copy");
            Assert.That(same, Is.True, "'nest' asked again is the object it was opened with");
            Assert.That(gone, Is.True, "I goes");
            Assert.That(alive, Is.True, "the held object is I.Sub's: it lives");
        }

        /// <summary>
        /// O6: registered the other way round — I, then I.Sub. I.Sub gives both names now: holding I's "Sub/nest" refuses I.Sub, naming I;
        /// holding "nest", I goes and the object lives — it would die if the engine gave the directory registered first.
        /// </summary>
        [Test, Timeout(20000)]
        public async Task O6_TheOtherOrder_TheInnerDirectoryGivesIt()
        {
            Outer(); Sub();
            var byOuter = ContentService.Acquire<Texture2D>("Sub/nest");
            string error = null;
            void Catch(string message, string stack, LogType type) { if (type == LogType.Error) error = message; }
            LogAssert.Expect(LogType.Error, new Regex("ContentLinkTests.I\\.Sub' still has 1 live asset"));
            Application.logMessageReceived += Catch;
            bool subRefused = !ContentService.UnregisterDirectory(m_Sub);
            Application.logMessageReceived -= Catch;
            byOuter.Dispose();
            ContentService.Collect();
            await Frames(2);
            var held = ContentService.Acquire<Texture2D>("nest");
            var obj = held.Asset;
            bool gone = ContentService.UnregisterDirectory(m_I);
            if (gone) m_I = -1;
            await Frames(3);
            bool alive = obj != null && ReferenceEquals(held.Asset, obj);
            held.Dispose();
            Assert.That(subRefused, Is.True, "I.Sub, which gives I's name, is refused");
            StringAssert.Contains("Sub/nest refs=1 status=Loaded — a name of '", error);
            StringAssert.Contains("ContentLinkTests/I', loaded from this directory", error);
            Assert.That(gone, Is.True, "I — registered first — gives nothing that is held: it goes");
            Assert.That(alive, Is.True, "the object 'nest' holds is I.Sub's: it lives");
        }

        /// <summary>O4: forced, I takes the lease with it — the handle is invalid, not valid over an unloaded object.</summary>
        [Test, Timeout(20000)]
        public async Task O4_Forced_TheLeaseGoesWithIt()
        {
            Sub(); Outer();
            var held = ContentService.Acquire<Texture2D>("nest");
            LogAssert.Expect(LogType.Warning, new Regex("force released"));
            bool gone = ContentService.UnregisterDirectory(m_I, force: true);
            if (gone) m_I = -1;
            await Frames(3);
            Assert.That(gone, Is.True, "forced");
            Assert.That(held.IsValid, Is.False, "the lease went with the directory");
        }

        /// <summary>O5: an object unloaded under its holder — destroyed here — is named as such by a load through a scope.</summary>
        [Test, Timeout(20000)]
        public async Task O5_AnObjectUnloadedUnderItsHolder_IsNamedAsSuch()
        {
            Sub();
            var held = ContentService.Acquire<Texture2D>("nest");
            Object.DestroyImmediate(held.Asset, true);
            var scope = ContentScope.Root.CreateChild("O5");
            LogAssert.Expect(LogType.Error, new Regex("'nest' was unloaded while held"));
            LogAssert.Expect(LogType.Error, new Regex("Deletion of Content File owned object detected"));   // the engine's own complaint, after (measured on 6000.6)
            var got = await scope.LoadAsync<Texture2D>("nest");
            scope.Dispose();
            held.Dispose();
            ContentService.Collect();
            await Frames(2);
            var fresh = ContentService.Acquire<Texture2D>("nest");
            bool back = fresh.Asset != null;
            fresh.Dispose();
            Assert.That(got, Is.Null);
            Assert.That(back, Is.True, "released, it loads again");
        }
    }
}
