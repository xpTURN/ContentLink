using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace xpTURN.ContentLink.Tests.Editor
{
    /// <summary>
    /// Play Mode ends while loads are in flight. They are started at ExitingPlayMode, which comes before
    /// Application.quitting (measured), so they are in flight when ContentService.Shutdown runs. This project enters Play Mode without
    /// a domain reload: the directory Shutdown leaves registered must be released when the next session starts. Asset loads only: the
    /// engine does not start a scene load at ExitingPlayMode, and one still in flight from Play crashes the 6000.6 editor in its own exit,
    /// before Application.quitting — with or without ContentLink (measured).
    /// </summary>
    [Category("Lifetime")]
    public class PlayModeEndTests
    {
        static readonly List<string> s_Reported = new List<string>();
        static void OnLog(string message, string stack, LogType type)
        {
            if (type != LogType.Log && message.StartsWith("[ContentLink]", StringComparison.Ordinal)) s_Reported.Add($"{type}: {message.Split('\n')[0]}");
        }

        static void StartLoadsAsPlayModeEnds(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            _ = ContentService.AcquireAsync<AudioClip>("Audio/tone");                                       // a raw load: its caller has no handle yet
            _ = ContentScope.ForScene(SceneManager.GetActiveScene()).LoadAsync<Texture2D>("Icons/single");   // a scope load
        }

        /// <summary>T67: nothing is reported for the loads in flight — no leaked handle, no refused directory — and the next session registers the directory again.</summary>
        [UnityTest, Timeout(240000)]
        public IEnumerator T67_PlayModeEndsWithLoadsInFlight_NothingReported_TheNextSessionRegistersAgain()
        {
            var dirA = Path.Combine(Application.streamingAssetsPath, "ContentLinkTests", "A");
            if (!Directory.Exists(dirA)) Assert.Ignore("content directory A is missing — run the PlayMode tests once (TestContentBuild)");
            s_Reported.Clear();
            Application.logMessageReceived += OnLog;
            try
            {
                yield return new EnterPlayMode();
                Assert.That(ContentService.RegisterDirectory(dirA), Is.GreaterThanOrEqualTo(0));
                EditorApplication.playModeStateChanged += StartLoadsAsPlayModeEnds;
                yield return new ExitPlayMode();
                EditorApplication.playModeStateChanged -= StartLoadsAsPlayModeEnds;
                var reportedAtEnd = new List<string>(s_Reported);

                yield return new EnterPlayMode();
                string again;
                try
                {
                    int dir = ContentService.RegisterDirectory(dirA);
                    var h = ContentService.Acquire<Texture2D>("Icons/single");
                    again = h.IsValid ? "registered, loads" : "registered, no load";
                    h.Dispose();
                    ContentService.Collect();
                    if (!ContentService.UnregisterDirectory(dir)) again += ", not unregistered";
                }
                catch (Exception e) { again = $"{e.GetType().Name}: {e.Message}"; }
                yield return new ExitPlayMode();

                Assert.That(reportedAtEnd, Is.Empty, "the end of Play Mode reports nothing for loads still in flight");
                Assert.That(again, Is.EqualTo("registered, loads"), "the next session registers the directory again and loads from it");
            }
            finally
            {
                Application.logMessageReceived -= OnLog;
                EditorApplication.playModeStateChanged -= StartLoadsAsPlayModeEnds;
            }
        }
    }
}
