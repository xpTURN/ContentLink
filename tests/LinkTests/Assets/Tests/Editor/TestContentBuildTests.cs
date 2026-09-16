using NUnit.Framework;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// EditMode step that builds the test content directories and leaves them in place for the PlayMode tests.
    /// Run this (or the menu 'Window/ContentLink/Tests/Build Test Content') before the PlayMode suite.
    /// </summary>
    [Category("TestContent")]
    public class TestContentBuildTests
    {
        [Test]
        public void S0_BuildAll_BuildsAllDirectories()
        {
            TestContentBuild.BuildAll();   // throws on catalog or build failure
            foreach (var dir in TestContentBuild.Directories)
                Assert.That(TestContentBuild.IsBuilt(dir), Is.True, $"output missing for '{dir}'");
        }
    }
}
