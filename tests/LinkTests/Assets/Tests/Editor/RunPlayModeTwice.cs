using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Runs the PlayMode suite twice in the same editor session (Enter Play Mode Options: domain reload off)
    /// and compares the two summaries. The second run is the one that exercises statics surviving a Play session
    /// (ContentService/ContentScope ResetStatics, the UTF cache workaround).
    /// </summary>
    public static class RunPlayModeTwice
    {
        const string Menu = "Window/ContentLink/Tests/Run PlayMode Twice";
        static TestRunnerApi s_Api;
        static Callbacks s_Callbacks;

        sealed class Callbacks : ICallbacks
        {
            int m_Run;
            string m_First;

            public void RunStarted(ITestAdaptor tests) { }
            public void TestStarted(ITestAdaptor test) { }
            public void TestFinished(ITestResultAdaptor result) { }

            public void RunFinished(ITestResultAdaptor result)
            {
                string summary = $"passed={result.PassCount} failed={result.FailCount} skipped={result.SkipCount} inconclusive={result.InconclusiveCount}";
                if (++m_Run == 1)
                {
                    m_First = summary;
                    Debug.Log($"[ContentLink.Tests] run 1: {summary} — run 2 starts once run 1 has fully finished (play mode exited, runner idle)");
                    EditorApplication.update += StartSecondRunWhenIdle;   // RunFinished fires before UTF's own cleanup tasks
                    return;
                }
                bool identical = summary == m_First && result.FailCount == 0;
                Debug.Log($"[ContentLink.Tests] run 2: {summary} — {(identical ? "IDENTICAL to run 1, no domain reload in between" : $"DIFFERENT from run 1 ({m_First})")}");
                s_Api.UnregisterCallbacks(this);
                s_Callbacks = null;
            }
        }

        [MenuItem(Menu)]
        static void Run()
        {
            if (s_Callbacks != null) { Debug.LogWarning("[ContentLink.Tests] already running"); return; }
            s_Api ??= ScriptableObject.CreateInstance<TestRunnerApi>();
            s_Callbacks = new Callbacks();
            s_Api.RegisterCallbacks(s_Callbacks);
            Execute();
        }

        static int s_IdleFrames;

        static void StartSecondRunWhenIdle()
        {
            // TestRunnerApi.IsRunActive() is internal in UTF 1.8: treat "not playing, not compiling, and UTF's bootstrap
            // scene (Assets/InitTestScene*.unity) gone" as the run being fully over, then wait a few more frames.
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || BootstrapSceneExists()) { s_IdleFrames = 0; return; }
            if (++s_IdleFrames < 10) return;
            EditorApplication.update -= StartSecondRunWhenIdle;
            s_IdleFrames = 0;
            Execute();
        }

        static bool BootstrapSceneExists()
        {
            foreach (var guid in AssetDatabase.FindAssets("InitTestScene t:Scene", new[] { "Assets" }))
                if (System.IO.Path.GetFileName(AssetDatabase.GUIDToAssetPath(guid)).StartsWith("InitTestScene")) return true;
            return false;
        }

        static void Execute()
        {
            s_Api.Execute(new ExecutionSettings(new Filter { testMode = TestMode.PlayMode }));
        }
    }
}
