using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Against the real engine: an in-flight cancellation's exception carries the token that cancelled, and a
    /// directory with a scene load in flight is refused rather than unregistered — unregistering under a scene load crashes the
    /// editor (measured on 6000.6). Directories A (assets) and C (scenes) stay registered for the fixture.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    [Category("Lifetime")]
    public class CancellationContractTests
    {
        int m_DirA = -1, m_DirC = -1;

        [OneTimeSetUp]
        public void Register()
        {
            m_DirA = ContentService.RegisterDirectory(TestContent.Dir("A"));
            m_DirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
        }

        [OneTimeTearDown]
        public void Unregister()
        {
            bool okC = m_DirC < 0 || ContentService.UnregisterDirectory(m_DirC);
            bool okA = m_DirA < 0 || ContentService.UnregisterDirectory(m_DirA);
            if (!okC && m_DirC >= 0) ContentService.UnregisterDirectory(m_DirC, force: true);
            if (!okA && m_DirA >= 0) ContentService.UnregisterDirectory(m_DirA, force: true);
            m_DirA = m_DirC = -1;
            Assert.That(okA && okC, Is.True, "a directory still had live assets or scenes at fixture end");
        }

        /// <summary>Waits until nothing is held or tracked, and the engine has ended what the cancelled loads left running.</summary>
        static async Task Settle(int frames = 60)
        {
            for (int i = 0; i < frames; ++i) { ContentService.Collect(); await Awaitable.NextFrameAsync(); }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0), "nothing held");
            Assert.That(ContentService.LoadedSceneCount, Is.EqualTo(0), "nothing tracked");
        }

        static string Which(CancellationToken got, CancellationToken want) => got == want ? "the cancelling token" : got == CancellationToken.None ? "no token" : "another token";

        /// <summary>The exception an awaited load ends with — null when it ends without one.</summary>
        static async Task<System.OperationCanceledException> Cancellation<T>(Awaitable<T> load)
        {
            try { await load; return null; }
            catch (System.OperationCanceledException e) { return e; }
        }

        /// <summary>
        /// T66: the OperationCanceledException of a load cancelled while it waits carries the token that cancelled —
        /// the caller's, or the scope's when the scope ends — as one cancelled before the call does.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T66_AnInFlightCancellation_CarriesTheTokenThatCancelled()
        {
            var cts = new CancellationTokenSource();
            var raw = ContentService.AcquireAsync<AudioClip>("Audio/tone", cts.Token);
            Assume.That(raw.GetAwaiter().IsCompleted, Is.False, "the load is in flight");
            cts.Cancel();
            var e1 = await Cancellation(raw);
            Assert.That(e1, Is.Not.Null, "a raw load: cancelled");
            Assert.That(Which(e1.CancellationToken, cts.Token), Is.EqualTo("the cancelling token"), "a raw load: the caller's token");

            var scope = ContentScope.Root.CreateChild("T66");
            var scopeToken = scope.DisposedToken;   // read before Dispose; after it the property answers the same token (checked below)
            var scoped = scope.LoadAsync<AudioClip>("Audio/tone");
            scope.Dispose();
            var e2 = await Cancellation(scoped);
            Assert.That(e2, Is.Not.Null, "a scope load: cancelled");
            Assert.That(Which(e2.CancellationToken, scopeToken), Is.EqualTo("the cancelling token"), "a scope load its scope's end cancels: the scope's token");
            Assert.That(Which(e2.CancellationToken, scope.DisposedToken), Is.EqualTo("the cancelling token"), "… and the scope's DisposedToken read after its end is that token");

            var cts3 = new CancellationTokenSource();
            var scene = ContentService.LoadSceneAsync("Scenes/One", LoadSceneMode.Additive, cts3.Token);
            Assume.That(scene.GetAwaiter().IsCompleted, Is.False, "the scene load is in flight");
            cts3.Cancel();
            var e3 = await Cancellation(scene);
            Assert.That(e3, Is.Not.Null, "a scene load: cancelled");
            Assert.That(Which(e3.CancellationToken, cts3.Token), Is.EqualTo("the cancelling token"), "a scene load: the caller's token");

            await Settle();   // the engine finishes the cancelled loads; the scene's late instance is handed back
        }

        /// <summary>
        /// T68: a scene load in flight — its directory is refused, forced or not, rather than
        /// unregistered under the load (which crashes the editor — measured on 6000.6); once the scene has arrived and is unloaded,
        /// the directory unregisters. A cancelled scene load keeps the directory until the engine's load ends.
        /// </summary>
        [Test, Timeout(30000)]
        public async Task T68_ASceneLoadInFlight_ItsDirectoryIsRefused()
        {
            var pending = ContentService.LoadSceneAsync("Scenes/One", LoadSceneMode.Additive);
            Assume.That(pending.GetAwaiter().IsCompleted, Is.False, "the scene load is in flight");
            LogAssert.Expect(LogType.Error, new Regex("has scene load\\(s\\) in flight"));
            Assert.That(ContentService.UnregisterDirectory(m_DirC, force: true), Is.False, "refused while the scene loads");

            var scene = await pending;
            Assert.That(scene.IsValid(), Is.True, "the load arrives");
            var op = SceneManager.UnloadSceneAsync(scene);
            while (op != null && !op.isDone) await Awaitable.NextFrameAsync();

            var cts = new CancellationTokenSource();
            var cancelled = ContentService.LoadSceneAsync("Scenes/One", LoadSceneMode.Additive, cts.Token);
            cts.Cancel();
            Assert.That(await Cancellation(cancelled), Is.Not.Null, "the caller is answered at once");
            LogAssert.Expect(LogType.Error, new Regex("has scene load\\(s\\) in flight"));
            Assert.That(ContentService.UnregisterDirectory(m_DirC), Is.False, "a cancelled load still holds the directory while the engine loads");

            await Settle();
            Assert.That(ContentService.UnregisterDirectory(m_DirC), Is.True, "unregistered once the engine's load ended and its instance was handed back");
            m_DirC = ContentService.RegisterDirectory(TestContent.Dir("C"));
        }
    }
}
