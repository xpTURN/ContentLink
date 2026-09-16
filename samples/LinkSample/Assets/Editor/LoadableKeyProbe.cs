using System;
using System.Collections.Generic;
using Unity.Loading;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

static class LoadableKeyProbe
{
    [MenuItem("Assets/ContentLink Probe/Compare Loadable Key Strings")]
    static void Run()
    {
        var m1 = typeof(Loadable<Object>).GetMethod("ToString", Type.EmptyTypes);
        var m2 = typeof(LoadableObjectId).GetMethod("ToString", Type.EmptyTypes);
        Debug.Log($"Loadable.ToString declared by: {m1.DeclaringType}\n" +
                  $"LoadableObjectId.ToString declared by: {m2.DeclaringType}\n" +
                  $"assembly: {typeof(Loadable<Object>).Assembly.GetLoadedAssemblyPath()}");

        var distinct = new HashSet<string>();
        foreach (var o in Selection.objects)
        {
            var id = LoadableObjectIdEditorUtility.CreateLoadableObjectId(o);
            var loadable = new Loadable<Object>(id);
            string a = loadable.ToString(), b = id.ToString();
            distinct.Add(a);
            Debug.Log($"{(a == b ? "SAME" : "DIFF")} {o.name} ({o.GetType().Name})\n" +
                      $"  Loadable        : {a}\n  LoadableObjectId: {b}\n  Status after ctor: {loadable.Status}");
        }
        Debug.Log($"{Selection.objects.Length} objects → {distinct.Count} distinct Loadable strings");
    }
}
