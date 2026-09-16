#if !UNITY_EDITOR
using NUnit.Framework;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// Player stand-in for SimulationProjectTruthTests, which lives in the editor-only EditorPlay assembly because
    /// it needs build profiles. Reported as skipped here so "editor only" does not read like "gone".
    /// </summary>
    [Category("Simulation")]
    public class SimulationProjectTruthTests
    {
        [Test]
        public void EditorOnly() => Assert.Ignore("editor only: profile-driven simulation tests run in the editor PlayMode suite.");
    }
}
#endif
