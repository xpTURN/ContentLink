using System.IO;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Unicode forms against the real engine — the editor's Play Mode and the players. Test content H (Assets/TestContent/H,
    /// built with the others) and Assets/Tests/Resources/ContentLinkTests/한글 hold Hangul folders and files as git checks them out on macOS:
    /// composed on disk. The macOS editor spells their folders decomposed, so a build there carries the folders decomposed (measured on
    /// 6000.6) — Resources finds a path only in that spelling; names are composed.
    /// </summary>
    [PrebuildSetup(TestContent.BuildHook)]
    public class UnicodeFormLoadTests
    {
        static string C(string s) => s.Normalize(NormalizationForm.FormC);
        static string D(string s) => s.Normalize(NormalizationForm.FormD);

        [SetUp] public void Before() => TestUtil.RestoreStatics();
        [TearDown] public void After() { TestUtil.RestoreStatics(); ContentService.Collect(); }

        /// <summary>L1: a catalog built here — a name typed composed, the mixed spelling an earlier version made, all decomposed: found, one slot.</summary>
        [Test]
        public void L1_TheBuiltCatalog_EitherForm_OneSlot()
        {
            int dir = ContentService.RegisterDirectory(TestContent.Dir("H"));
            try
            {
                Assert.That(ContentService.Contains(C("그림/파일")), Is.True, "typed composed");
                Assert.That(ContentService.Contains(D("그림") + "/" + C("파일")), Is.True, "the spelling an earlier version built");
                Assert.That(ContentService.Contains(D("그림/파일")), Is.True, "all decomposed");
                Assert.That(ContentService.Contains(C("그림/파일[파일]")), Is.True, "its sprite");
                int live = ContentService.LiveAssetCount;
                var a = ContentService.Acquire<Texture2D>(D("그림") + "/" + C("파일"));
                var b = ContentService.Acquire<Texture2D>(C("그림/파일"));
                try
                {
                    Assert.That(a.IsValid && b.IsValid, Is.True);
                    Assert.That(b.Asset, Is.SameAs(a.Asset));
                    Assert.That(ContentService.LiveAssetCount, Is.EqualTo(live + 1), "one slot whichever form asked first");
                    Assert.That(a.Key, Is.EqualTo(C("그림/파일")));
                }
                finally { a.Dispose(); b.Dispose(); }
            }
            finally { ContentService.Collect(); ContentService.UnregisterDirectory(dir); }
        }

        /// <summary>L2: res: — a composed path loads the file the build spells its way, sync and async; the stored spelling is the same slot.</summary>
        [Test]
        public async Task L2_Res_ComposedPath()
        {
            var sync = ContentService.Acquire<TextAsset>(C("res:ContentLinkTests/한글/글자"));
            var async = await ContentService.AcquireAsync<TextAsset>("res:ContentLinkTests/" + D("한글") + "/" + C("글자"));
            try
            {
                Assert.That(sync.IsValid, Is.True, "sync, composed");
                Assert.That(async.IsValid, Is.True, "async, as the editor spells it");
                Assert.That(async.Asset, Is.SameAs(sync.Asset));
            }
            finally { sync.Dispose(); async.Dispose(); }
        }

        /// <summary>L3: res: async from a fresh slot — the composed path first, then the build's spelling.</summary>
        [Test]
        public async Task L3_Res_ComposedPath_AsyncFirst()
        {
            var h = await ContentService.AcquireAsync<TextAsset>(C("res:ContentLinkTests/한글/글자"));
            try { Assert.That(h.IsValid && h.Asset != null, Is.True); }
            finally { h.Dispose(); }
        }

        /// <summary>L4: the lookups by another form allocate nothing once the form is seen — the redirect remembers it.</summary>
        [Test]
        public void L4_TheRedirectAllocatesNothing()
        {
            int dir = ContentService.RegisterDirectory(TestContent.Dir("H"));
            try
            {
                var mixed = D("그림") + "/" + C("파일");
                var composed = C("그림/파일");
                ContentService.Contains(mixed); ContentService.Contains(composed);
                Assert.That(() => { var s = new string('x', 64); }, Is.AllocatingGCMemory(), "the measure sees an allocation (control)");
                Assert.That(() => { ContentService.Contains(mixed); ContentService.Contains(composed); }, Is.Not.AllocatingGCMemory());
            }
            finally { ContentService.UnregisterDirectory(dir); }
        }
    }

    /// <summary>
    /// res: by a name in a file's own spelling — files the test writes before the build (<see cref="UnicodeFormFiles"/>):
    /// git keeps a file name composed, and these are the names no spelling of the composed path reaches.
    /// </summary>
    [PrebuildSetup(typeof(UnicodeFormFiles)), PostBuildCleanup(typeof(UnicodeFormFiles))]
    public class UnicodeFormResTests
    {
        static string C(string s) => s.Normalize(NormalizationForm.FormC);
        static string D(string s) => s.Normalize(NormalizationForm.FormD);

        [SetUp] public void Before() => TestUtil.RestoreStatics();
        [TearDown] public void After() { TestUtil.RestoreStatics(); ContentService.Collect(); }

        /// <summary>L5: res: by a name in a file's own spelling — part of it decomposed, a CJK compatibility ideograph — which no spelling of the composed path reaches: its file as given under the build's folders, sync and async.</summary>
        [Test]
        public async Task L5_Res_AFilesOwnSpelling()
        {
            var mixed = ContentService.Acquire<TextAsset>("res:ContentLinkTests/" + C("한글") + "/" + D("검") + C("방패"));
            var compat = await ContentService.AcquireAsync<TextAsset>("res:ContentLinkTests/" + C("한글") + "/\uF914");
            try
            {
                Assert.That(mixed.IsValid && mixed.Asset != null && mixed.Asset.text == "mixed", Is.True, "sync, part of the name decomposed");
                Assert.That(compat.IsValid && compat.Asset != null && compat.Asset.text == "compat", Is.True, "async, a compatibility ideograph");
            }
            finally { mixed.Dispose(); compat.Dispose(); }
        }
    }

    /// <summary>The Resources files L5 loads, written before the build: git keeps a file name composed, so they are made here, as the editor spells them.</summary>
    public class UnicodeFormFiles : IPrebuildSetup, IPostBuildCleanup
    {
        static readonly string Folder = "Assets/Tests/Resources/ContentLinkTests/" + "한글".Normalize(NormalizationForm.FormC) + "/";
        static readonly string[] Files = { "검".Normalize(NormalizationForm.FormD) + "방패".Normalize(NormalizationForm.FormC) + ".txt", "\uF914.txt" };

        public void Setup()
        {
#if UNITY_EDITOR
            File.WriteAllText(Folder + Files[0], "mixed");
            File.WriteAllText(Folder + Files[1], "compat");
            UnityEditor.AssetDatabase.Refresh();
#endif
        }

        public void Cleanup()
        {
#if UNITY_EDITOR
            foreach (var file in Files) UnityEditor.AssetDatabase.DeleteAsset(Folder + file);
#endif
        }
    }
}
