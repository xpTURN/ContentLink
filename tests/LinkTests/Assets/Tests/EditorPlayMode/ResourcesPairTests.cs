using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace xpTURN.ContentLink.Tests.EditorPlay
{
    /// <summary>
    /// A path a player's file shares with a file in a Resources folder under an Editor folder. The
    /// editor's own Resources gives either — for pairs made during a session, nothing a project controls decides which — while a
    /// player has only its own: Play Mode loads the player's file, sync and async. Pairs that were on disk when the editor opened
    /// come out the player's way, so this one makes them during Play (a fixture would test nothing — measured).
    /// </summary>
    [Category("Resources")]
    public class ResourcesPairTests
    {
        const string k_Root = "Assets/Tests/F16PlayPairs";

        static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            Folder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }

        static void Make(string path, string kind)
        {
            Folder(path.Substring(0, path.LastIndexOf('/')));
            if (kind == "prefab") { var go = new GameObject(Path.GetFileNameWithoutExtension(path)); PrefabUtility.SaveAsPrefabAsset(go, path); Object.DestroyImmediate(go); return; }
            if (kind == "txt") File.WriteAllText(path, path);
            else { var t = new Texture2D(4, 4); File.WriteAllBytes(path, t.EncodeToPNG()); Object.DestroyImmediate(t); }
            AssetDatabase.ImportAsset(path);
        }

        [UnityTest]
        public IEnumerator P1_PairsMadeDuringPlay_LoadThePlayersFile()
        {
            TestUtil.RestoreStatics();
            var kinds = new[] { "txt", "png", "prefab" };
            var cases = new List<(string name, string player)>();
            try
            {
                int i = 0;
                foreach (var pk in kinds) foreach (var ek in kinds) foreach (bool playerMadeFirst in new[] { true, false })
                {
                    string player = $"{k_Root}/c{i}/A/Resources/F16P/x{i}.{pk}", editor = $"{k_Root}/c{i}/Z/Editor/Resources/F16P/x{i}.{ek}";
                    if (playerMadeFirst) { Make(player, pk); Make(editor, ek); } else { Make(editor, ek); Make(player, pk); }
                    cases.Add(($"res:F16P/x{i}", player));
                    ++i;
                }
                yield return null;
                int editorFirst = 0;
                foreach (var (name, player) in cases)
                {
                    if (AssetDatabase.GetAssetPath(Resources.Load(name.Substring(ContentService.ResourcesPrefix.Length))) != player) ++editorFirst;
                    var h = ContentService.Acquire<Object>(name);
                    string sync = h.IsValid ? AssetDatabase.GetAssetPath(h.Asset) : "nothing";
                    h.Dispose(); ContentService.Collect();
                    var a = ContentService.AcquireAsync<Object>(name);
                    while (!a.GetAwaiter().IsCompleted) yield return null;
                    var ha = a.GetAwaiter().GetResult();
                    string async = ha.IsValid ? AssetDatabase.GetAssetPath(ha.Asset) : "nothing";
                    ha.Dispose(); ContentService.Collect();
                    Assert.That(sync, Is.EqualTo(player), name + " (sync)");
                    Assert.That(async, Is.EqualTo(player), name + " (async)");
                }
                Assert.That(editorFirst, Is.GreaterThan(0), "the engine gave the player's file for every path: nothing above was tested");
                Debug.Log($"[ContentLink.Tests] P1: the engine gave the editor-only file for {editorFirst} of {cases.Count} paths");
            }
            finally { AssetDatabase.DeleteAsset(k_Root); }
        }
    }
}
