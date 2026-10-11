using System;
using UnityEditor;

namespace xpTURN.ContentLink.Editor
{
    /// <summary>
    /// An asset whose content alone changed — saved by anything, or changed on disk and refreshed (a version-control update) —
    /// is imported again with no EditorApplication.projectChanged (measured on 6000.6): a catalog or an atlas drops the editor
    /// utility's caches, and the editor simulation's answers with them; any other asset, the reference statuses — the one
    /// answer read off an asset's content (a prefab's components).
    /// </summary>
    sealed class ContentCatalogImportWatcher : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var path in importedAssets)
                if (ContentLinkEditorUtility.IsAtlasPath(path)
                    || AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(ContentCatalog))   // by its type: the engine imports a catalog saved as .mat, .preset … too (measured)
                {
                    ContentLinkEditorUtility.Invalidate();
                    return;
                }
            // An atlas saved, removed or moved changes the sprites it names — saving one raises no projectChanged (measured)
            foreach (var path in deletedAssets) if (ContentLinkEditorUtility.IsAtlasPath(path)) { ContentLinkEditorUtility.Invalidate(); return; }
            foreach (var path in movedAssets) if (ContentLinkEditorUtility.IsAtlasPath(path)) { ContentLinkEditorUtility.Invalidate(); return; }
            if (importedAssets.Length > 0) ContentLinkEditorUtility.InvalidateStatuses();
        }
    }
}
