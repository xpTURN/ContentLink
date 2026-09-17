using System.Collections;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Debug = UnityEngine.Debug;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Timing half of the catalog-size measurement (it records the numbers and never fails on them): the reference index is
    /// built on the first reference lookup after a registration, so that lookup pays for the whole catalog. Timed here next to the
    /// registration itself, which builds the name index over the same 10 000 entries — if the two are of a size, a
    /// separate warm-up call is not worth having.
    /// </summary>
    [Category("Measure")]
    public class ContentRefMeasureTests
    {
        const int k_Entries = 10000;   // ContentRefMeasureTests.Entries in the editor assembly

        static string Output(string name) => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "Runner", name);

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            TestUtil.RestoreStatics();
            for (int i = 0; i < 10 && ContentService.LiveAssetCount != 0; ++i) { ContentService.Collect(); yield return null; }
            Assert.That(ContentService.LiveAssetCount, Is.EqualTo(0));
        }

        [Test, Timeout(60000)]
        public void U4_FirstReferenceLookup_AgainstTheRegistration_Recorded()
        {
            if (!Directory.Exists(Output("Synth10k")))
                Assert.Ignore("Runner/Synth10k is missing — run the EditMode test ContentRefMeasureTests.U4 first");
            TestUtil.RestoreStatics();   // simulation off: only the catalog answers

            var first = new ContentRef<Texture2D>(0.ToString("x32"));
            var last = new ContentRef<Texture2D>((k_Entries - 1).ToString("x32"));
            var watch = Stopwatch.StartNew();
            int dir = ContentService.RegisterDirectory(Output("Synth10k"));
            double registerMs = watch.Elapsed.TotalMilliseconds;
            try
            {
                watch.Restart();
                bool found = ContentService.TryGetName(first, out var name);
                double firstMs = watch.Elapsed.TotalMilliseconds;

                watch.Restart();
                for (int i = 0; i < 1000; ++i) ContentService.TryGetName(last, out _);
                double laterUs = watch.Elapsed.TotalMilliseconds;   // per 1000 lookups = µs per lookup

                Debug.Log($"[ContentLink.Tests] {k_Entries} entries — RegisterDirectory (catalog load + name index) {registerMs:F2} ms · first reference lookup (reference index) {firstMs:F2} ms · later lookups {laterUs:F3} µs each");
                Assert.That(found, Is.True, "the synthetic catalog's first GUID did not resolve");
                Assert.That(name, Is.EqualTo("Synth/item00000"));
            }
            finally { ContentService.UnregisterDirectory(dir); }
        }
    }
}
