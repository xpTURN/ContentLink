using System.Reflection;
using UnityEngine;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>
    /// UTF 1.8.0 with Enter Play Mode Options (domain reload off): PlayerTestAssemblyProvider caches the test assemblies
    /// in a static list, and its SubsystemRegistration reset only Clear()s it while the loader returns early on non-null,
    /// so from the second Play Mode session in a domain the runner loads zero tests and the run ends at once.
    /// Putting the field back to null makes the provider reload. Editor only; harmless if UTF changes and the field is gone.
    /// </summary>
    static class UtfNoDomainReloadWorkaround
    {
#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetPlayerTestAssemblyProviderCache()
        {
            var type = typeof(UnityEngine.TestTools.LogAssert).Assembly.GetType("UnityEngine.TestTools.Utils.PlayerTestAssemblyProvider");
            var field = type?.GetField("m_LoadedAssemblies", BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null)
            {
                Debug.LogWarning("[ContentLink.Tests] PlayerTestAssemblyProvider.m_LoadedAssemblies not found — the no-domain-reload workaround is inactive. If Play Mode runs report zero tests, enable domain reload in Enter Play Mode Options.");
                return;
            }
            field.SetValue(null, null);
        }
#endif
    }
}
