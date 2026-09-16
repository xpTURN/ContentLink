using NUnit.Framework;
using xpTURN.ContentLink.Editor;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>The command-line entry point's argument parsing — pure, no build.</summary>
    [Category("Build")]
    public class ContentBuildCLITests
    {
        [Test]
        public void C1_ProfileAll_IgnoresCase_LikeTheKeys()
        {
            // The keys are matched ignoring case; the special value 'all' must be too, or "-contentlink.profile All"
            // looks for a profile called 'All'.
            Assert.That(ContentBuildCLI.Parse(new[] { "-contentlink.profile", "All" }, out var options, out var error), Is.True, error);
            Assert.That(options.Profile, Is.EqualTo(ContentBuildCLI.All));
            Assert.That(ContentBuildCLI.Parse(new[] { "-CONTENTLINK.PROFILE", "main" }, out options, out error), Is.True, error);
            Assert.That(options.Profile, Is.EqualTo("main"), "an ordinary profile name keeps its case");
        }
    }
}
