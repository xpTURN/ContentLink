using System;
using System.IO;
using Unity.Loading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace xpTURN.ContentLink.Tests
{
    /// <summary>Where the prebuild hook (editor assembly: TestContentBuild) puts the built content directories.</summary>
    public static class TestContent
    {
        public const string BuildHook = "xpTURN.ContentLink.Tests.Editor.TestContentBuild";

        public static string Root => Path.Combine(Application.streamingAssetsPath, "ContentLinkTests");
        public static string Dir(string name) => Path.Combine(Root, name);
    }

    public static class TestEnv
    {
        /// <summary>
        /// True where Loadable.Release actually unloads the object, so "obj == null" can be asserted.
        /// Measured by SmokeTests.S3b: true in the editor as well — a registered content directory is served from the built
        /// files in Play Mode, not from the AssetDatabase. Kept as a property for platforms that might behave differently.
        /// </summary>
        public static bool ReleaseObservable => true;
    }

    public static class TestUtil
    {
        public static Awaitable NextFrame() => Awaitable.NextFrameAsync();

        /// <summary>Polls the condition once per frame; true as soon as it holds, false after <paramref name="frames"/> frames.</summary>
        public static async Awaitable<bool> WaitUntil(Func<bool> condition, int frames = 10)
        {
            for (int i = 0; i < frames; ++i)
            {
                if (condition()) return true;
                await Awaitable.NextFrameAsync();
            }
            return condition();
        }

        // The editor installs these at domain load; they are parked here while the simulation is off.
        static Func<string, ContentService.SimulatedAsset> s_Name;
        static Func<string, ContentService.SimulatedDirectory> s_Directory;   // simulated directories, served from the project
        static Func<string, bool> s_SceneContains;
        static Func<string, LoadSceneParameters, AsyncOperation> s_SceneStart;
        static Func<string, Scene> s_SceneResult;
        static Func<string, string, bool, string> s_Ref;                      // ContentRef: GUID → name
        static Action<System.Collections.Generic.List<string>> s_SceneLoads;   // the simulated scene loads in flight (UnregisterDirectory)

        /// <summary>
        /// Turns the editor simulation off or on for a test. Runtime-only on purpose: this assembly has no
        /// editor platform restriction, so it cannot read EditorPrefs — nulling the seam is the switch.
        /// Every other suite assumes only built directories answer, so RestoreStatics turns it off.
        /// </summary>
        public static void SetSimulation(bool on)
        {
            if (ContentService.NameFallback != null) s_Name = ContentService.NameFallback;
            if (ContentService.DirectoryFallback != null) s_Directory = ContentService.DirectoryFallback;
            if (ContentService.SceneContainsFallback != null) s_SceneContains = ContentService.SceneContainsFallback;
            if (ContentService.SceneFallback != null) s_SceneStart = ContentService.SceneFallback;
            if (ContentService.SceneResultFallback != null) s_SceneResult = ContentService.SceneResultFallback;
            if (ContentService.RefFallback != null) s_Ref = ContentService.RefFallback;
            if (ContentService.SceneLoadsInFlightFallback != null) s_SceneLoads = ContentService.SceneLoadsInFlightFallback;

            ContentService.NameFallback = on ? s_Name : null;
            ContentService.RefFallback = on ? s_Ref : null;
            ContentService.DirectoryFallback = on ? s_Directory : null;
            ContentService.SceneContainsFallback = on ? s_SceneContains : null;
            ContentService.SceneFallback = on ? s_SceneStart : null;
            ContentService.SceneResultFallback = on ? s_SceneResult : null;
            ContentService.SceneLoadsInFlightFallback = on ? s_SceneLoads : null;
        }

        /// <summary>Puts every static ContentLink setting back to its default.</summary>
        public static void RestoreStatics()
        {
            ContentService.AutoCollect = true;
            ContentService.ReleaseDelayFrames = 0;
            ContentService.CaptureStackTrace = false;
            ContentService.UnloadResourcesOnRelease = true;
            ContentScope.DefaultWarnThreshold = 0;
            SetSimulation(false);
        }
    }
}
