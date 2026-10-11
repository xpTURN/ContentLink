using System;
using System.IO;
using UnityEngine;

namespace xpTURN.ContentLink.Samples
{
    /// <summary>
    /// Registers the Main profile's content directory and spawns the showcases from it by name, side by side.
    /// In the editor the simulation serves that directory from the project, so Play needs no build; a player mounts the
    /// packed build under StreamingAssets (build the Main profile first).
    /// </summary>
    public sealed class ShowcaseBootstrap : MonoBehaviour
    {
        [SerializeField] string m_Directory = "MainContent";   // the Main profile's output folder, under StreamingAssets
        [SerializeField] string[] m_Showcases = { "Prefabs/LinkShowcase.prefab", "Prefabs/RefShowcase.prefab" };
        [SerializeField] float m_Spacing = 9f;

        async void Start()
        {
            if (!ContentService.Contains(m_Showcases[0]))   // registered once: a reloaded scene, or a game that registered the folder first, finds the names served
            {
                int dir = await ContentService.RegisterPackedDirectoryAsync(Path.Combine(Application.streamingAssetsPath, m_Directory));
                if (dir < 0) return;   // ContentService logged why
                if (this == null) return;   // destroyed while the archives mounted: the registration is not tied to this object's scope, so nothing cancels it
            }

            for (int i = 0; i < m_Showcases.Length; ++i)
            {
                GameObject showcase;
                try { showcase = await this.Scope().InstantiateAsync(m_Showcases[i]); }
                catch (OperationCanceledException) { return; }   // the bootstrap was destroyed while spawning
                if (showcase != null)
                    showcase.transform.position = new Vector3((i - (m_Showcases.Length - 1) * 0.5f) * m_Spacing, 0f, 0f);
            }
        }
    }
}
