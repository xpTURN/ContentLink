using System.Collections.Generic;
using UnityEngine;
using UnityEngine.LowLevel;

namespace xpTURN.ContentLink
{
    /// <summary>Runs ContentService.Collect() at the end of PostLateUpdate so engine releases happen at a fixed point.</summary>
    static class ContentPlayerLoop
    {
        struct ContentCollect { }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Install()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            for (int i = 0; i < subs.Length; ++i)
            {
                if (subs[i].type != typeof(UnityEngine.PlayerLoop.PostLateUpdate)) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList ?? new PlayerLoopSystem[0]);
                list.RemoveAll(s => s.type == typeof(ContentCollect));
                list.Add(new PlayerLoopSystem { type = typeof(ContentCollect), updateDelegate = OnUpdate });
                subs[i].subSystemList = list.ToArray();
                break;
            }
            PlayerLoop.SetPlayerLoop(root);
        }

        static void OnUpdate()
        {
            if (ContentService.AutoCollect) ContentService.Collect();
        }
    }
}
