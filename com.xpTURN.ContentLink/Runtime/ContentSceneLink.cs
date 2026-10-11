using System;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace xpTURN.ContentLink
{
    /// <summary>
    /// Name-based serialized reference to a scene in a content directory, loaded additively and owned by a scope:
    /// the scene is unloaded when the owner scope ends. Single-mode loads go through
    /// <see cref="ContentService.LoadSceneAsync(string, LoadSceneParameters, CancellationToken)"/> instead.
    ///
    ///     public ContentSceneLink level;
    ///     var scene = await level.LoadAsync(this);   // owner = this GameObject
    ///
    /// UnloadAsync() is optional. One ContentSceneLink holds at most one scene instance.
    /// </summary>
    [Serializable]
    public sealed class ContentSceneLink
    {
        [SerializeField] string m_Name = "";

        [NonSerialized] ContentScope m_Scope;
        [NonSerialized] Scene m_Scene;
        [NonSerialized] bool m_Loading;
        [NonSerialized] int m_LoadVersion;

        public ContentSceneLink() { }
        public ContentSceneLink(string name) { m_Name = name; }

        public string Name => m_Name;
        public bool IsLoading => m_Loading;
        public bool IsLoaded => m_Scene.IsValid() && m_Scene.isLoaded;
        public Scene Scene => m_Scene;
        public ContentScope Scope => IsLoaded ? m_Scope : null;

        public bool RuntimeKeyIsValid() => ContentService.ContainsScene(m_Name);

        /// <summary>Data-driven remapping. Unload first if loaded.</summary>
        public void SetName(string name)
        {
            if (m_Loading || IsLoaded)
            {
                Debug.LogError($"[ContentLink] Unload scene '{m_Name}' before changing the name.");
                return;
            }
            m_Name = name;
        }

        public Awaitable<Scene> LoadAsync(Component owner, CancellationToken ct = default) => LoadAsync(owner != null ? owner.Scope() : throw new ArgumentNullException(nameof(owner)), ct);

        public async Awaitable<Scene> LoadAsync(ContentScope scope, CancellationToken ct = default)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope), "ContentSceneLink needs an owner scope.");
            if (scope.IsDisposed) throw new ObjectDisposedException($"ContentScope '{scope.Path}'");
            if (m_Loading || IsLoaded)
            {
                Debug.LogError($"[ContentLink] scene '{m_Name}' is already loaded. Call UnloadAsync() first.");
                return m_Scene;
            }
            m_Loading = true;
            int version = m_LoadVersion;

            Scene scene;
            try { scene = await scope.LoadSceneAsync(m_Name, ct); }
            finally { if (version == m_LoadVersion) m_Loading = false; }

            if (version != m_LoadVersion)
            {
                if (scene.IsValid()) scope.ReleaseScene(scene);   // unloaded while loading: hand it back
                return default;
            }
            if (!scene.IsValid()) return default;
            m_Scope = scope;
            m_Scene = scene;
            return scene;
        }

        /// <summary>
        /// Unloads the scene. Safe to call at any time: while loading it stops the wait and unloads once the engine
        /// load completes; after the owner scope ended it is a no-op.
        /// </summary>
        public async Awaitable UnloadAsync()
        {
            if (m_Loading) { m_Loading = false; m_LoadVersion++; return; }
            var scene = m_Scene;
            var scope = m_Scope;
            m_Scene = default;
            m_Scope = null;
            if (!scene.IsValid() || !scene.isLoaded) return;
            var op = scope != null && !scope.IsDisposed ? scope.ReleaseSceneOp(scene) : ContentService.TryUnloadScene(scene);
            if (op != null) await op;
        }

        public override string ToString() => $"ContentSceneLink({m_Name})";
    }
}
