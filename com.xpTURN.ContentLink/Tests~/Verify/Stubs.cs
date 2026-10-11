// ── Unity API stubs (following the documented signatures) + a fake engine that tracks reference counts ──
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace UnityEngine.Events { public delegate void UnityAction<T0>(T0 arg0); public delegate void UnityAction<T0, T1>(T0 arg0, T1 arg1); }

namespace UnityEngine.SceneManagement
{
    using Unity.Loading;
    public struct SceneHandle
    {
        ulong _raw;
        public ulong GetRawData() => _raw;
        public static SceneHandle FromRawData(ulong raw) => new SceneHandle { _raw = raw };
        public static bool operator ==(SceneHandle a, SceneHandle b) => a._raw == b._raw;
        public static bool operator !=(SceneHandle a, SceneHandle b) => a._raw != b._raw;
        public override bool Equals(object o) => o is SceneHandle h && h._raw == _raw;
        public override int GetHashCode() => _raw.GetHashCode();
        public override string ToString() => _raw.ToString();
        [Obsolete("Implicit conversion from SceneHandle to int is deprecated. Use SceneHandle.GetRawData() instead", true)]
        public static implicit operator int(SceneHandle h) => (int)h._raw;
        [Obsolete("Implicit conversion from int to SceneHandle is deprecated. Use SceneHandle.FromRawData(ulong) instead", true)]
        public static implicit operator SceneHandle(int v) => new SceneHandle { _raw = (ulong)v };
    }
    public enum LoadSceneMode { Single, Additive }
    public struct LoadSceneParameters
    {
        public LoadSceneMode loadSceneMode { get; set; }
        public LoadSceneParameters(LoadSceneMode mode) { loadSceneMode = mode; }
    }
    public struct Scene
    {
        public SceneHandle handle; public string name; public string path; public LoadableSceneId loadableSceneId;
        public bool IsValid() => handle.GetRawData() != 0;
        public bool isLoaded => FakeEngine.IsLoaded(handle) && !FakeEngine.Unloading.Contains(handle.GetRawData());   // matches the real engine: false as soon as an unload starts
    }
    public static class SceneManager
    {
        public static event UnityEngine.Events.UnityAction<Scene> sceneUnloaded;
        public static event UnityEngine.Events.UnityAction<Scene, LoadSceneMode> sceneLoaded;
        public static Scene Active;
        public static readonly Scene DontDestroyOnLoadScene = new Scene { handle = SceneHandle.FromRawData(ulong.MaxValue), name = "DontDestroyOnLoad" };
        static ulong s_NextHandle = 1000;
        public static int loadedSceneCount => FakeEngine.LoadedScenes.Count - FakeEngine.Unloading.Count;   // matches the real engine: drops as soon as an unload starts
        public static int sceneCount => FakeEngine.LoadedScenes.Count + FakeEngine.Transitioning;        // includes scenes that are still unloading
        public static Scene GetSceneAt(int i) => FakeEngine.LoadedScenes[i];
        public static Scene GetSceneByLoadableSceneId(LoadableSceneId id)
        { foreach (var s in FakeEngine.LoadedScenes) if (s.loadableSceneId == id) return s; return default; }
        public static void MoveGameObjectToScene(GameObject go, Scene s) => go.scene = s;
        // Test helper: registers a bootstrap scene as loaded.
        public static void Open(Scene s) { if (!FakeEngine.IsLoaded(s.handle)) FakeEngine.LoadedScenes.Add(s); }
        // A scene opened by editor asset path (no build): it belongs to no directory and has no LoadableSceneId, only a path.
        public static Scene NewLiveScene(string path)
        {
            int slash = path.LastIndexOf('/');
            var leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = leaf.LastIndexOf('.');
            return new Scene { handle = SceneHandle.FromRawData(s_NextHandle++), name = dot > 0 ? leaf.Substring(0, dot) : leaf, path = path };
        }
        public static void RaiseSceneLoaded(Scene s, LoadSceneMode mode) => sceneLoaded?.Invoke(s, mode);
        // Test helper: destroys the scene's objects (OnDestroy only for those that were ever active), then raises sceneUnloaded.
        public static void UnloadScene(Scene s)
        {
            foreach (var go in GameObject.All.ToArray()) if (!go.destroyed && go.scene.handle == s.handle) Object.Destroy(go);
            FakeEngine.LoadedScenes.RemoveAll(x => x.handle == s.handle);
            sceneUnloaded?.Invoke(s);
        }
        public static AsyncOperation LoadSceneAsync(LoadableSceneId id, LoadSceneParameters p)
        {
            if (!id.IsValid || !FakeEngine.SceneDir.TryGetValue(id.guid, out var dir) || !FakeEngine.Registered.Contains(dir))
            { Debug.LogError($"engine: scene {id} is not in any registered content directory"); return null; }
            var op = new AsyncOperation(); FakeEngine.Transitioning++;
            FakeEngine.ScenesInFlight[id.guid] = FakeEngine.ScenesInFlight.TryGetValue(id.guid, out var flying) ? flying + 1 : 1;
            bool fail = FakeEngine.FailNextSceneLoad; FakeEngine.FailNextSceneLoad = false;
            FakeEngine.PendingLoads.Add(() =>
            {
                FakeEngine.Transitioning--;
                if (--FakeEngine.ScenesInFlight[id.guid] == 0) FakeEngine.ScenesInFlight.Remove(id.guid);
                if (fail) { op.Complete(); return; }   // completed, but no scene arrived
                if (p.loadSceneMode == LoadSceneMode.Single) foreach (var s in FakeEngine.LoadedScenes.ToArray()) UnloadScene(s);
                var scene = new Scene { handle = SceneHandle.FromRawData(s_NextHandle++), name = FakeEngine.SceneNames.TryGetValue(id.guid, out var n) ? n : "Scene", path = $"built://{id.guid}", loadableSceneId = id };
                FakeEngine.LoadedScenes.Add(scene);
                sceneLoaded?.Invoke(scene, p.loadSceneMode);
                op.Complete();
            });
            return op;
        }
        public static AsyncOperation UnloadSceneAsync(Scene s)
        {
            if (!s.IsValid()) throw new ArgumentException("Scene to unload is invalid");                   // matches the real engine
            if (!FakeEngine.IsLoaded(s.handle)) { Debug.LogError("engine: UnloadSceneAsync on a scene that is not loaded"); return null; }
            if (FakeEngine.Unloading.Contains(s.handle.GetRawData())) return null;                            // matches the real engine: a second unload silently returns null
            if (loadedSceneCount == 1) { Debug.LogError("engine: cannot unload the last loaded scene"); return null; }
            var op = new AsyncOperation(); FakeEngine.Unloading.Add(s.handle.GetRawData());
            FakeEngine.PendingLoads.Add(() => { FakeEngine.Unloading.Remove(s.handle.GetRawData()); UnloadScene(s); op.Complete(); });
            return op;
        }
    }
}

namespace UnityEngine
{
    using System.Reflection;
    using UnityEngine.SceneManagement;
    public class UnityException : Exception { public UnityException(string m) : base(m) { } }

    public class Object
    {
        internal bool destroyed;
        string m_Name = "";
        public static long NameReads;   // test helper: every read of a name — the engine crosses into native code and allocates the string each time
        public string name { get { ++NameReads; return m_Name; } set => m_Name = value; }
        internal string RawName => m_Name;   // the engine's own copy of a name (a clone, a reload) is made natively: not a read
        static bool IsNull(Object o) => ReferenceEquals(o, null) || o.destroyed;
        public static bool operator ==(Object a, Object b) { bool an = IsNull(a), bn = IsNull(b); if (an || bn) return an && bn; return ReferenceEquals(a, b); }
        public static bool operator !=(Object a, Object b) => !(a == b);
        public override bool Equals(object o) => o is Object ob && this == ob;
        public override int GetHashCode() => base.GetHashCode();
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal static void Msg(Component c, string m) => c.GetType().GetMethod(m, F)?.Invoke(c, null);

        // Instantiate: copies serialized fields and, if active, runs Awake. Approximates Unity's cloning rules.
        public static GameObject Instantiate(GameObject src, Transform parent, bool worldPositionStays)
        {
            var go = new GameObject(src.activeSelf) { name = src.RawName + "(Clone)", scene = SceneManager.Active };
            foreach (var c in src.components)
            {
                var n = (Component)Activator.CreateInstance(c.GetType()); n.gameObject = go;
                foreach (var f in c.GetType().GetFields(F))
                    if (!f.IsNotSerialized && (f.IsPublic || f.GetCustomAttribute<SerializeField>() != null) && f.DeclaringType != typeof(Component))
                    { var v = f.GetValue(c); f.SetValue(n, v is Array a ? a.Clone() : v); }
                go.components.Add(n);
            }
            if (go.activeSelf) go.AwakeAll();
            return go;
        }
        public static GameObject Instantiate(GameObject src) => Instantiate(src, null, false);
        public static void Destroy(Object o)
        {
            if (o is GameObject go)
            {
                go.destroying = true;
                var comps = go.components.ToArray();
                foreach (var c in comps) if (c.awoken) Msg(c, "OnDestroy");   // the engine: a sibling's OnDestroy still sees the others, not null (measured on 6000.6)
                foreach (var c in comps) c.destroyed = true;
            }
            o.destroyed = true;
        }
        public static void DontDestroyOnLoad(Object o) { if (o is GameObject go) go.scene = SceneManager.DontDestroyOnLoadScene; }
    }
    public class Component : Object { public GameObject gameObject; internal bool awoken; }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
    public class Transform : Component { }
    public sealed class Sprite : Object { }
    public class Texture2D : Object { }
    public class ScriptableObject : Object { public static T CreateInstance<T>() where T : ScriptableObject, new() => new T(); }
    public enum CompressionKindForStub { None, LZ4, LZMA }
    /// <summary>Shell of the engine presets. Kind tells the tests which preset was passed in.</summary>
    [Serializable] public struct BuildCompression
    {
        public CompressionKindForStub Kind;
        public static readonly BuildCompression Uncompressed = new BuildCompression { Kind = CompressionKindForStub.None };
        public static readonly BuildCompression LZ4 = new BuildCompression { Kind = CompressionKindForStub.LZ4 };
        public static readonly BuildCompression LZMA = new BuildCompression { Kind = CompressionKindForStub.LZMA };
        public static readonly BuildCompression LZ4Runtime = new BuildCompression { Kind = CompressionKindForStub.LZ4 };
        public static readonly BuildCompression UncompressedRuntime = Uncompressed;
    }
    /// <summary>Really parses JSON, so the harness can verify the defensive manifest parsing.</summary>
    public static class JsonUtility
    {
        static readonly System.Text.Json.JsonSerializerOptions s_Options = new() { IncludeFields = true };
        static readonly System.Text.Json.JsonSerializerOptions s_Pretty = new() { IncludeFields = true, WriteIndented = true };
        public static T FromJson<T>(string json) => System.Text.Json.JsonSerializer.Deserialize<T>(json, s_Options);
        public static string ToJson(object obj, bool prettyPrint = false) => System.Text.Json.JsonSerializer.Serialize(obj, obj.GetType(), prettyPrint ? s_Pretty : s_Options);
    }
    /// <summary>GUID reference. The stub holds the object directly but exposes only the real surface (asset, isSet, isBroken).</summary>
    [Serializable] public struct LazyLoadReference<T> where T : Object
    {
        Object m_Asset;
        public T asset { get => m_Asset as T; set => m_Asset = value; }
        public bool isSet => m_Asset != null;
        public bool isBroken => false;
    }
    public sealed class GameObject : Object
    {
        public static readonly List<GameObject> All = new();
        internal List<Component> components = new();
        internal bool destroying;   // Object.Destroy has begun: its OnDestroy calls run
        public Scene scene;
        public bool activeSelf { get; private set; } = true;
        public GameObject() { All.Add(this); }
        public GameObject(bool active) { activeSelf = active; All.Add(this); }
        internal void AwakeAll() { foreach (var c in components.ToArray()) if (!c.awoken) { c.awoken = true; Msg(c, "Awake"); } }
        public void SetActive(bool v) { activeSelf = v; if (v) AwakeAll(); }
        public T AddComponent<T>() where T : Component, new() { if (destroying) return null;   // the engine: an object being destroyed adds no component — null, no log (measured on 6000.6)
            var c = new T { gameObject = this }; components.Add(c); if (activeSelf) { c.awoken = true; Msg(c, "Awake"); } return c; }
        public T GetComponent<T>() where T : Component { foreach (var c in components) if (c is T t) return t; return null; }
        public Component GetComponent(Type t) { foreach (var c in components) if (t.IsInstanceOfType(c)) return c; return null; }
    }
    public static class Debug
    {
        public static int Errors, Warnings, Logs;
        public static string LastError = "", LastWarning = "", LastLog = "";
        public static bool Verbose = true;
        public static void Log(object m) { Logs++; LastLog = m.ToString(); if (Verbose) Console.WriteLine("  [log] " + m); }
        public static void LogWarning(object m) { Warnings++; LastWarning = m.ToString(); if (Verbose) Console.WriteLine("  [warn] " + m.ToString().Split('\n')[0]); }
        public static void LogError(object m) { Errors++; LastError = m.ToString(); if (Verbose) Console.WriteLine("  [error] " + m.ToString().Split('\n')[0]); }
        public static void LogException(Exception e) { Errors++; Console.WriteLine("  [exception] " + e.Message); }
    }
    public static class Time
    {
        public static int frameCount;
        /// <summary>Real API: wall-clock seconds. The stub ties it to the fake engine's frames (FakeWeb.SecondsPerFrame each), plus a settable jump.</summary>
        public static double realtimeSinceStartupAsDouble => Unity.Loading.FakeEngine.Frame * Unity.Loading.FakeWeb.SecondsPerFrame + Unity.Loading.FakeWeb.ClockOffset;
    }
    public enum RuntimePlatform { OSXEditor = 0, OSXPlayer = 1, WindowsPlayer = 2, WindowsEditor = 7, IPhonePlayer = 8, Android = 11, LinuxPlayer = 13, LinuxEditor = 16, WebGLPlayer = 17, PS5 = 44 }
    public static class Application
    {
        public static event Action quitting; public static void Quit() => quitting?.Invoke(); public static string dataPath = "/fake/Assets";
        public static string unityVersion = "6000.6.0f1";
        public static RuntimePlatform platform = RuntimePlatform.OSXEditor;
        public static bool isEditor = true;
        public static string persistentDataPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clverify-persistent");
        public static string temporaryCachePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clverify-cache");
        public static string streamingAssetsPath = "/fake/Assets/StreamingAssets";
    }
    public enum RuntimeInitializeLoadType { AfterSceneLoad, BeforeSceneLoad, SubsystemRegistration }
    [AttributeUsage(AttributeTargets.Method)] public class RuntimeInitializeOnLoadMethodAttribute : Attribute { public RuntimeInitializeOnLoadMethodAttribute(RuntimeInitializeLoadType t) { } }
    [AttributeUsage(AttributeTargets.Field)] public class SerializeField : Attribute { }
    public class CreateAssetMenuAttribute : Attribute { public string menuName; public string fileName; }
    public class DisallowMultipleComponent : Attribute { }
    public class HideInInspector : Attribute { }
    public class AddComponentMenu : Attribute { public AddComponentMenu(string s) { } }

    // Awaitable<T>: single-threaded with synchronous continuations (approximates Unity's main-thread behaviour)
    [AsyncMethodBuilder(typeof(AwaitableBuilder<>))]
    public class Awaitable<T> : INotifyCompletion
    {
        bool _done; T _result; Exception _ex; Action _cont;
        public Awaitable<T> GetAwaiter() => this;
        public bool IsCompleted => _done;
        public T GetResult() { if (_ex != null) throw _ex; return _result; }
        public void OnCompleted(Action c) { if (_done) c(); else _cont += c; }
        internal bool TryComplete(T r, Exception ex) { if (_done) return false; _done = true; _result = r; _ex = ex; var c = _cont; _cont = null; c?.Invoke(); return true; }
    }
    public struct AwaitableBuilder<T>
    {
        Awaitable<T> _a;
        public static AwaitableBuilder<T> Create() => new AwaitableBuilder<T> { _a = new Awaitable<T>() };
        public Awaitable<T> Task => _a;
        public void Start<TSM>(ref TSM sm) where TSM : IAsyncStateMachine => sm.MoveNext();
        public void SetStateMachine(IAsyncStateMachine sm) { }
        public void SetResult(T r) => _a.TryComplete(r, null);
        public void SetException(Exception e) => _a.TryComplete(default, e);
        public void AwaitOnCompleted<TA, TSM>(ref TA aw, ref TSM sm) where TA : INotifyCompletion where TSM : IAsyncStateMachine { IAsyncStateMachine b = sm; aw.OnCompleted(b.MoveNext); }
        public void AwaitUnsafeOnCompleted<TA, TSM>(ref TA aw, ref TSM sm) where TA : ICriticalNotifyCompletion where TSM : IAsyncStateMachine { IAsyncStateMachine b = sm; aw.OnCompleted(b.MoveNext); }
    }
    // Non-generic Awaitable (for async Awaitable methods and FromAsyncOperation)
    [AsyncMethodBuilder(typeof(AwaitableBuilder))]
    public class Awaitable : INotifyCompletion
    {
        bool _done; Exception _ex; Action _cont;
        public Awaitable GetAwaiter() => this;
        public bool IsCompleted => _done;
        public void GetResult() { if (_ex != null) throw _ex; }
        public void OnCompleted(Action c) { if (_done) c(); else _cont += c; }
        internal bool TryComplete(Exception ex) { if (_done) return false; _done = true; _ex = ex; var c = _cont; _cont = null; c?.Invoke(); return true; }
        /// <summary>
        /// Measured (6000.6 — the editor, Mono and IL2CPP players): a token cancelled before the call throws here — the
        /// operation done or not — and the operation goes on; a cancel while the operation flies aborts a web request, whose awaiter gets
        /// OperationCanceledException when the aborted request completes, and is ignored by the other operations measured — a scene load or
        /// unload, a Resources load, UnloadUnusedAssets: they complete and the awaiter resumes normally (no other kind was measured); a cancel
        /// after it is done changes nothing.
        /// </summary>
        public static Awaitable FromAsyncOperation(AsyncOperation op, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = new Awaitable();
            if (op.isDone) { a.TryComplete(null); return a; }
            var request = (op as UnityEngine.Networking.UnityWebRequestAsyncOperation)?.webRequest;
            bool cancelled = false;
            CancellationTokenRegistration reg = default;
            if (request != null && cancellationToken.CanBeCanceled) reg = cancellationToken.Register(() => { cancelled = true; request.Abort(); });
            op.completed += _ => { reg.Dispose(); a.TryComplete(cancelled ? new OperationCanceledException(cancellationToken) : null); };
            return a;
        }
        /// <summary>Only advances the frame counter and completes immediately; the mount stub uses that counter to go from InProgress to Complete.</summary>
        public static Awaitable NextFrameAsync()
        {
            Unity.Loading.FakeEngine.Frame++;
            Unity.Loading.FakeWeb.Step();
            var a = new Awaitable(); a.TryComplete(null); return a;
        }
        /// <summary>Real API: resumes on a thread-pool thread. The stub stays on the caller's thread (the harness is single-threaded) and counts the hops.</summary>
        public static Awaitable BackgroundThreadAsync() { Unity.Loading.FakeWeb.BackgroundHops++; var a = new Awaitable(); a.TryComplete(null); return a; }
        public static Awaitable MainThreadAsync() { var a = new Awaitable(); a.TryComplete(null); return a; }
    }
    public struct AwaitableBuilder
    {
        Awaitable _a;
        public static AwaitableBuilder Create() => new AwaitableBuilder { _a = new Awaitable() };
        public Awaitable Task => _a;
        public void Start<TSM>(ref TSM sm) where TSM : IAsyncStateMachine => sm.MoveNext();
        public void SetStateMachine(IAsyncStateMachine sm) { }
        public void SetResult() => _a.TryComplete(null);
        public void SetException(Exception e) => _a.TryComplete(e);
        public void AwaitOnCompleted<TA, TSM>(ref TA aw, ref TSM sm) where TA : INotifyCompletion where TSM : IAsyncStateMachine { IAsyncStateMachine b = sm; aw.OnCompleted(b.MoveNext); }
        public void AwaitUnsafeOnCompleted<TA, TSM>(ref TA aw, ref TSM sm) where TA : ICriticalNotifyCompletion where TSM : IAsyncStateMachine { IAsyncStateMachine b = sm; aw.OnCompleted(b.MoveNext); }
    }
    public class AsyncOperation
    {
        public bool isDone { get; private set; }
        Action<AsyncOperation> _completed;
        public event Action<AsyncOperation> completed { add { if (isDone) value(this); else _completed += value; } remove { _completed -= value; } }
        internal void Complete() { isDone = true; var c = _completed; _completed = null; c?.Invoke(this); }
    }
    public static class AsyncOperationAwaitableExtensions { public static Awaitable GetAwaiter(this AsyncOperation op) => Awaitable.FromAsyncOperation(op).GetAwaiter(); }
    public class AwaitableCompletionSource<T>
    {
        public Awaitable<T> Awaitable { get; } = new Awaitable<T>();
        public bool TrySetResult(T v) => Awaitable.TryComplete(v, null);
        public bool TrySetCanceled() => Awaitable.TryComplete(default, new OperationCanceledException());
    }

    /// <summary>An asynchronous Resources load. Reading <see cref="asset"/> before it is done loads at once (measured).</summary>
    public class ResourceRequest : AsyncOperation
    {
        internal string Path; internal Type Type; Object m_Asset; bool m_Resolved;
        public Object asset { get { if (!m_Resolved) { m_Asset = Resources.Find(Path, Type); m_Resolved = true; } return m_Asset; } }
    }

    /// <summary>
    /// The engine's Resources over the fake project: every file under a folder named Resources, by the path below that folder
    /// without the extension — below each such folder when they nest. Lookups ignore case; an untyped load of a path that
    /// several files share gives the GameObject first; an empty or missing path is null with no log; GameObject and
    /// Component refuse UnloadAsset with the engine's error; an unloaded object stays dead and the next load gives a new one.
    /// Editor-only folders (an Editor segment) answer only while <see cref="Unity.Loading.FakeEngine.ResourcesSeeEditorOnly"/> (the editor).
    /// </summary>
    public static class Resources
    {
        public static Object Load(string path) => Load(path, typeof(Object));
        public static T Load<T>(string path) where T : Object => Load(path, typeof(T)) as T;
        public static Object Load(string path, Type type) { Unity.Loading.FakeEngine.ResourcesLoads++; return Find(path, type); }
        public static T[] LoadAll<T>(string path) where T : Object
        {
            Unity.Loading.FakeEngine.ResourcesLoadAlls++;
            var list = new List<T>();
            foreach (var o in Unity.Loading.FakeEngine.ResourcesAt(path)) if (o is T t) list.Add(t);
            return list.ToArray();
        }
        public static Object[] LoadAll(string path) => LoadAll<Object>(path);
        public static ResourceRequest LoadAsync(string path) => LoadAsync(path, typeof(Object));
        public static ResourceRequest LoadAsync(string path, Type type)
        {
            Unity.Loading.FakeEngine.ResourcesAsyncLoads++;
            var request = new ResourceRequest { Path = path, Type = type };
            Unity.Loading.FakeEngine.PendingLoads.Add(() => { _ = request.asset; request.Complete(); });   // one frame, loaded or not (measured)
            return request;
        }
        public static void UnloadAsset(Object assetToUnload)
        {
            if (ReferenceEquals(assetToUnload, null)) return;
            if (assetToUnload is GameObject || assetToUnload is Component)
            {
                Debug.LogError("UnloadAsset may only be used on individual assets and can not be used on GameObject's / Components / AssetBundles or GameManagers");
                return;
            }
            Unity.Loading.FakeEngine.ResourcesUnloaded.Add(assetToUnload);
            if (!Unity.Loading.FakeEngine.ResourcesSeeEditorOnly) assetToUnload.destroyed = true;   // the editor unloads nothing (measured)
        }
        public static AsyncOperation UnloadUnusedAssets()
        {
            Unity.Loading.FakeEngine.ResourcesSweeps++;
            var op = new AsyncOperation();
            Unity.Loading.FakeEngine.PendingLoads.Add(op.Complete);
            return op;
        }
        internal static Object Find(string path, Type type)
        {
            Object pick = null;
            foreach (var o in Unity.Loading.FakeEngine.ResourcesAt(path))
            {
                if (!type.IsInstanceOfType(o)) continue;
                if (type == typeof(Object) && Unity.Loading.FakeEngine.IsSub.Contains(o)) continue;   // untyped: main assets only
                if (pick == null || (o is GameObject && !(pick is GameObject))) pick = o;
                if (type != typeof(Object)) break;   // typed: the first that fits
            }
            return pick;
        }
    }
}

namespace UnityEngine.PlayerLoop { public struct PostLateUpdate { } }

namespace UnityEngine.U2D
{
    /// <summary>
    /// What the builder and the editor read. GetPackables answers the objects as they were added (a sprite, a texture,
    /// a folder — measured on 6000.6), a variant answers none; CanBindTo and spriteCount answer the packed sprites, and none
    /// while the atlas is not packed (SpriteAtlasV2Build before a build — measured).
    /// </summary>
    public class SpriteAtlas : UnityEngine.Object
    {
        public UnityEngine.Object[] Packables = new UnityEngine.Object[0];   // stub: what GetPackables returns
        public readonly System.Collections.Generic.List<Sprite> Bound = new();   // stub: what the engine packed
        public bool Packed = true;                                           // stub: false = not packed yet
        public bool isVariant { get; set; }
        public int spriteCount => Packed ? Bound.Count : 0;
        public bool CanBindTo(Sprite sprite) => Packed && Bound.Contains(sprite);
    }
}
namespace UnityEditor.U2D
{
    public static class SpriteAtlasExtensions
    {
        public static UnityEngine.Object[] GetPackables(this UnityEngine.U2D.SpriteAtlas atlas) => atlas.isVariant ? new UnityEngine.Object[0] : atlas.Packables;
    }
}

namespace UnityEngine.Serialization
{
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
    public class FormerlySerializedAsAttribute : Attribute { public FormerlySerializedAsAttribute(string oldName) { this.oldName = oldName; } public string oldName { get; } }
}

namespace UnityEngine.LowLevel
{
    public struct PlayerLoopSystem { public Type type; public PlayerLoopSystem[] subSystemList; public UpdateFunction updateDelegate; public delegate void UpdateFunction(); }
    public static class PlayerLoop
    {
        static PlayerLoopSystem s_Loop = new PlayerLoopSystem { subSystemList = new[] { new PlayerLoopSystem { type = typeof(UnityEngine.PlayerLoop.PostLateUpdate), subSystemList = new PlayerLoopSystem[0] } } };
        public static PlayerLoopSystem GetCurrentPlayerLoop() => s_Loop;
        public static void SetPlayerLoop(PlayerLoopSystem l) => s_Loop = l;
        public static void RunPostLateUpdate() { foreach (var s in s_Loop.subSystemList[0].subSystemList) s.updateDelegate?.Invoke(); }
    }
}

namespace Unity.Loading
{
    using UnityEngine;
    [Serializable] public struct LoadableObjectId : IEquatable<LoadableObjectId>
    {
        public int value;
        public bool IsValid => value != 0;
        public bool Equals(LoadableObjectId o) => value == o.value;
        public override bool Equals(object o) => o is LoadableObjectId x && x.value == value;
        public override int GetHashCode() => value;
        public static bool operator ==(LoadableObjectId a, LoadableObjectId b) => a.value == b.value;
        public static bool operator !=(LoadableObjectId a, LoadableObjectId b) => a.value != b.value;
        public static string OidHex(int v) => v.ToString("x32");
        // Real-engine format: { oid-10631ae2bdc563cb89b073d5a49ccfdc, guid: 12445bf6…, fileID: 2800000, type: 3 }
        public override string ToString() => "{ oid-" + OidHex(value) + ", guid: " + OidHex(value) + ", fileID: " + value + ", type: 3 }";
    }
    [Serializable] public struct LoadableSceneId : IEquatable<LoadableSceneId>
    {
        public int guid;   // The real API keeps the GUID private; the public surface is only IsValid, Equals, GetHashCode, ToString and ==/!=
        public bool IsValid => guid != 0;
        public bool Equals(LoadableSceneId o) => guid == o.guid;
        public override bool Equals(object o) => o is LoadableSceneId x && x.guid == guid;
        public override int GetHashCode() => guid;
        public override string ToString() => "LSID:" + guid;
        public static bool operator ==(LoadableSceneId a, LoadableSceneId b) => a.guid == b.guid;
        public static bool operator !=(LoadableSceneId a, LoadableSceneId b) => a.guid != b.guid;
    }
    public enum LoadableStatus { None, Loading, Loaded, Failed }
    public struct ContentDirectoryHandle { internal string path; }

    public static class FakeEngine
    {
        public static readonly Dictionary<int, Object> Assets = new();       // id → asset (a missing id fails to load)
        public static readonly Dictionary<int, int> Refs = new();            // engine reference counts
        public static readonly Dictionary<int, string> IdDir = new();        // id → owning directory
        public static readonly Dictionary<string, ScriptableObject[]> Dirs = new();
        public static readonly HashSet<string> Registered = new();
        /// <summary>
        /// Ids another directory carries as a load target too — two profiles whose roots nest, a catalog under a root. The engine serves an id
        /// from the most recently registered directory that carries it, chosen when the load starts; a load its owner did not serve counts
        /// against the directory that served it (measured on 6000.6).
        /// </summary>
        public static readonly Dictionary<int, List<string>> AlsoIn = new();
        public static readonly List<string> RegisterOrder = new();                 // registered directories, the most recent last
        public static readonly Dictionary<(int, string), int> RefsAway = new();    // live loads of an id that a directory other than its owner serves
        /// <summary>The directory a load of the id starting now is served from: the most recently registered one that carries it — its owner when no other does.</summary>
        public static string ServingDir(int id)
        {
            IdDir.TryGetValue(id, out var owner);
            if (!AlsoIn.TryGetValue(id, out var also) || also.Count == 0) return owner;
            string best = owner != null && Registered.Contains(owner) ? owner : null;
            int at = best != null ? RegisterOrder.IndexOf(best) : -1;
            foreach (var d in also) { int i = RegisterOrder.IndexOf(d); if (Registered.Contains(d) && i > at) { best = d; at = i; } }
            return best ?? owner;
        }
        public static int AwayRefs(int id) { int n = 0; foreach (var kv in RefsAway) if (kv.Key.Item1 == id) n += kv.Value; return n; }
        /// <summary>Matches the real editor: in Play Mode an id that is in no directory still loads. A player build does not do that.</summary>
        public static bool EditorServesAnyId;
        public static bool FailNextSceneLoad;   // the next engine scene load completes without producing a scene
        public static readonly List<Action> PendingLoads = new();
        public static int LoadCalls;
        // Resources: counters, and whether editor-only Resources folders answer (the editor: yes · a player: no)
        public static int ResourcesLoads, ResourcesAsyncLoads, ResourcesLoadAlls, ResourcesSweeps;
        public static readonly List<Object> ResourcesUnloaded = new();
        public static bool ResourcesSeeEditorOnly;
        /// <summary>Every live object a Resources path names, files in registration order (main asset, then its sub-assets).</summary>
        public static List<Object> ResourcesAt(string path)
        {
            var found = new List<Object>();
            if (string.IsNullOrEmpty(path)) return found;
            foreach (var kv in new List<KeyValuePair<string, Object>>(MainAssetAt))
            {
                if (!ResourcesPathsOf(kv.Key).Contains(path.ToLowerInvariant())) continue;
                found.Add(Revive(kv.Key, kv.Value, -1));
                if (SubAssetsAt.TryGetValue(kv.Key, out var subs))
                    for (int i = 0; i < subs.Count; ++i) found.Add(Revive(kv.Key, subs[i], i));
            }
            return found;
        }
        /// <summary>The Resources paths a project file answers to, lower case: below each Resources folder it lies under.</summary>
        static List<string> ResourcesPathsOf(string assetPath)
        {
            var paths = new List<string>();
            var parts = assetPath.Split('/');
            bool editorOnly = false;
            for (int i = 0; i < parts.Length - 1; ++i)
            {
                if (string.Equals(parts[i], "Editor", StringComparison.OrdinalIgnoreCase)) editorOnly = true;
                if (!string.Equals(parts[i], "Resources", StringComparison.OrdinalIgnoreCase)) continue;   // the folder name in any case (measured: a lowercase resources folder is built)
                if (editorOnly && !ResourcesSeeEditorOnly) continue;
                var rel = string.Join("/", parts, i + 1, parts.Length - i - 1);
                int dot = rel.LastIndexOf('.');
                if (dot > rel.LastIndexOf('/')) rel = rel.Substring(0, dot);
                paths.Add(rel.ToLowerInvariant());
            }
            return paths;
        }
        /// <summary>An object UnloadAsset killed is replaced by a new instance on the next load; the old reference stays dead.</summary>
        static Object Revive(string assetPath, Object o, int sub)
        {
            if (!o.destroyed) return o;
            var fresh = (Object)Activator.CreateInstance(o.GetType());
            fresh.name = o.RawName;
            PathOf[fresh] = assetPath;
            if (sub < 0) MainAssetAt[assetPath] = fresh; else { SubAssetsAt[assetPath][sub] = fresh; IsSub.Add(fresh); }
            ObjectIdOf[fresh] = ++s_NextReviveId; ObjectById[s_NextReviveId] = fresh;
            return fresh;
        }
        static int s_NextReviveId = 500000;
        // Archives: namespaces, mounts and packing records
        public static readonly HashSet<string> Namespaces = new();
        public static readonly List<Mount> Mounts = new();
        public static readonly HashSet<string> FailMount = new();          // archives with these file names fail to mount
        public static int MountFrames = 1;                                  // frames a mount takes once its turn comes: archives mount one after another, in the order they were started — a single line across namespaces (measured)
        public static readonly Dictionary<string, int> MountFramesOf = new(StringComparer.Ordinal);   // by file name: that archive takes this many frames instead
        public static bool ThrowOnLoad;                                     // the next engine load throws after it has started (status Loading, reference taken)
        public static int Frame;
        public static readonly List<(string path, string[] aliases, UnityEngine.CompressionKindForStub compression)> Packed = new();
        public sealed class Mount { public string Namespace, Path, Prefix; public int ReadyFrame; public bool Failed, Mounted; }
        // Scenes
        public static readonly Dictionary<int, string> SceneDir = new();       // scene guid → owning directory
        public static readonly Dictionary<int, string> SceneNames = new();
        public static readonly List<UnityEngine.SceneManagement.Scene> LoadedScenes = new();
        public static readonly HashSet<ulong> Unloading = new();
        public static int Transitioning;
        /// <summary>
        /// Exceptions that escaped an async void method (the package's StartLoad · StartResourcesLoad). With no context the runtime rethrows
        /// them on the thread pool, where they end the process at some later point — in a later section, or after the run printed ALL PASS
        /// and exited 0 (measured). <see cref="RecordingContext"/> records them instead; the run fails on any.
        /// </summary>
        public static readonly List<Exception> AsyncVoidExceptions = new();
        /// <summary>Test.Main installs it: an async void method posts its escaping exception here (nothing else posts — no Task awaits).</summary>
        public sealed class RecordingContext : SynchronizationContext
        {
            public override void Post(SendOrPostCallback d, object state) { try { d(state); } catch (Exception e) { AsyncVoidExceptions.Add(e); } }
        }
        /// <summary>Scene loads started and not yet arrived, by scene guid. Unregistering the directory of one crashes the real editor (measured on 6000.6): the stub's engine throws.</summary>
        public static readonly Dictionary<int, int> ScenesInFlight = new();
        public static bool IsLoaded(UnityEngine.SceneManagement.SceneHandle h) { foreach (var s in LoadedScenes) if (s.handle == h) return true; return false; }
        // Fake editor project: path ↔ asset ↔ LoadableObjectId. Path lookups ignore case and answer with the path as registered,
        // like the editor's (measured on 6000.6)
        /// <summary>Project paths as the editor compares them: in any case and either Unicode form (measured on 6000.6).</summary>
        public sealed class ProjectPathComparer : IEqualityComparer<string>
        {
            public static readonly ProjectPathComparer Instance = new();
            internal static string Key(string s) => s?.Normalize(System.Text.NormalizationForm.FormC);
            public bool Equals(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);
            public int GetHashCode(string s) => StringComparer.OrdinalIgnoreCase.GetHashCode(Key(s));
        }
        public static readonly Dictionary<string, Object> MainAssetAt = new(ProjectPathComparer.Instance);
        public static readonly Dictionary<string, List<Object>> SubAssetsAt = new(ProjectPathComparer.Instance);
        public static readonly Dictionary<Object, string> PathOf = new();
        public static readonly HashSet<Object> IsSub = new();
        public static readonly HashSet<string> Folders = new(ProjectPathComparer.Instance);
        public static readonly Dictionary<Object, int> ObjectIdOf = new();
        public static readonly Dictionary<int, Object> ObjectById = new();
        public static readonly Dictionary<string, int> SceneIdOfPath = new();
        /// <summary>
        /// What an asset or a scene holds by id — a LoadableObjectId field, a LoadableSceneId field, a catalog it holds (its names): a flat
        /// build of a catalog that names it carries those as load targets too, with no name (measured on 6000.6).
        /// </summary>
        public static readonly Dictionary<string, List<int>> HeldIds = new();
        public static readonly Dictionary<string, List<string>> HeldScenes = new();   // asset path → scene paths
        public static readonly Dictionary<int, Object> SceneAssetById = new();
        // GUID table: a path listed here has its own GUID that survives MoveProjectAsset; any other path is its own GUID (the stub convention)
        public static readonly Dictionary<string, string> GuidOfPath = new(ProjectPathComparer.Instance);   // a path is found in any case (measured on 6000.6)
        public static readonly Dictionary<string, string> PathOfGuid = new();
        public static void SetGuid(string path, string guid) { GuidOfPath[path] = guid; PathOfGuid[guid] = path; }
        // what an object references (a prefab its sprite, a scene asset what its scene holds): EditorUtility.CollectDependencies follows it,
        // and AssetDatabase.GetDependencies by the files
        public static readonly Dictionary<Object, Object[]> Uses = new();
        // what only the objects link (a packed sprite → its atlas — measured on 6000.6): CollectDependencies follows it, GetDependencies does not
        public static readonly Dictionary<Object, Object[]> Links = new();
        // folders and files deleted this session: the default lookup still answers their GUID, and the GUID its path (measured on 6000.6)
        public static readonly Dictionary<string, string> DeletedGuidOfPath = new(ProjectPathComparer.Instance);
        public static void SetDeleted(string path, string guid) { DeletedGuidOfPath[path] = guid; PathOfGuid[guid] = path; }
        /// <summary>Moves a project asset to another path the way the editor does: same objects, same GUID, new path.</summary>
        public static void MoveProjectAsset(string from, string to)
        {
            var main = MainAssetAt[from]; MainAssetAt.Remove(from); MainAssetAt[to] = main; PathOf[main] = to;
            if (SubAssetsAt.TryGetValue(from, out var subs)) { SubAssetsAt.Remove(from); SubAssetsAt[to] = subs; foreach (var s in subs) PathOf[s] = to; }
            if (SceneIdOfPath.TryGetValue(from, out var sceneId)) { SceneIdOfPath.Remove(from); SceneIdOfPath[to] = sceneId; }
            if (GuidOfPath.TryGetValue(from, out var guid)) { GuidOfPath.Remove(from); SetGuid(to, guid); }
            int slash = to.LastIndexOf('/');
            while (slash > 0) { Folders.Add(to.Substring(0, slash)); slash = to.LastIndexOf('/', slash - 1); }
        }
        static int s_NextEditorId = 9000;
        /// <summary>Registers one main asset and its sub-assets in the fake project.</summary>
        public static void AddProjectAsset(string path, Object main, params Object[] subs)
        {
            MainAssetAt[path] = main; PathOf[main] = path;
            ObjectIdOf[main] = ++s_NextEditorId; ObjectById[s_NextEditorId] = main;
            var list = new List<Object>();
            foreach (var sub in subs)
            {
                list.Add(sub); PathOf[sub] = path; IsSub.Add(sub);
                ObjectIdOf[sub] = ++s_NextEditorId; ObjectById[s_NextEditorId] = sub;
            }
            SubAssetsAt[path] = list;
            int slash = path.LastIndexOf('/');
            while (slash > 0) { Folders.Add(path.Substring(0, slash)); slash = path.LastIndexOf('/', slash - 1); }
            if (main is UnityEditor.SceneAsset) { SceneIdOfPath[path] = ObjectIdOf[main]; SceneAssetById[ObjectIdOf[main]] = main; }
        }
        public static void ClearProject()
        {
            ResourcesLoads = ResourcesAsyncLoads = ResourcesLoadAlls = ResourcesSweeps = 0; ResourcesUnloaded.Clear();
            MainAssetAt.Clear(); SubAssetsAt.Clear(); PathOf.Clear(); IsSub.Clear(); Folders.Clear();
            ObjectIdOf.Clear(); ObjectById.Clear(); SceneIdOfPath.Clear(); SceneAssetById.Clear();
            GuidOfPath.Clear(); PathOfGuid.Clear(); DeletedGuidOfPath.Clear(); Uses.Clear(); Links.Clear(); HeldIds.Clear(); HeldScenes.Clear();
        }
        public static int RefOf(int id) => Refs.TryGetValue(id, out var r) ? r : 0;
        /// <summary>
        /// An import batch as the engine reports it: every AssetPostprocessor's OnPostprocessAllAssets, with the paths. It raises no
        /// projectChanged — saving an existing asset or refreshing one changed on disk raises none, an import that adds, removes or
        /// renames a sub-asset or replaces the main object does (measured on 6000.6): a test raises it where the engine would.
        /// </summary>
        public static void Import(string[] imported, string[] deleted = null, string[] moved = null, string[] movedFrom = null)
        {
            var args = new object[] { imported ?? new string[0], deleted ?? new string[0], moved ?? new string[0], movedFrom ?? new string[0] };
            foreach (var type in typeof(FakeEngine).Assembly.GetTypes())
                if (type.IsSubclassOf(typeof(UnityEditor.AssetPostprocessor)))
                    type.GetMethod("OnPostprocessAllAssets", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.Invoke(null, args);
        }
        public static void CompleteLoads() { var p = new List<Action>(PendingLoads); PendingLoads.Clear(); foreach (var a in p) a(); }
    }

    public class Loadable<T> where T : Object
    {
        readonly LoadableObjectId _id; LoadableStatus _status; Awaitable<T> _op; bool _holdsRef; string _away;   // _away: the directory that serves it when not its owner
        public Loadable(LoadableObjectId id) { _id = id; }
        public LoadableObjectId LoadableObjectId => _id;
        public LoadableStatus Status => _status;
        public T Target => _status == LoadableStatus.Loaded ? FakeEngine.Assets[_id.value] as T : null;
        void Resolve()
        {
            if (_status != LoadableStatus.Loading) return;
            bool served = FakeEngine.Registered.Contains(_away ?? (FakeEngine.IdDir.TryGetValue(_id.value, out var d) ? d : ""));
            if (!served && !FakeEngine.EditorServesAnyId) _status = LoadableStatus.Failed;
            else _status = FakeEngine.Assets.ContainsKey(_id.value) ? LoadableStatus.Loaded : LoadableStatus.Failed;
            _op?.TryComplete(Target, null);
        }
        void Begin()
        {
            if (_status != LoadableStatus.None) return;
            _status = LoadableStatus.Loading; FakeEngine.LoadCalls++;
            if (!_holdsRef)   // at most one reference per instance
            {
                _holdsRef = true; FakeEngine.Refs[_id.value] = FakeEngine.RefOf(_id.value) + 1;
                var from = FakeEngine.ServingDir(_id.value);
                if (from != null && FakeEngine.IdDir.TryGetValue(_id.value, out var own) && from != own)
                {
                    _away = from;
                    FakeEngine.RefsAway[(_id.value, from)] = (FakeEngine.RefsAway.TryGetValue((_id.value, from), out var n) ? n : 0) + 1;
                }
            }
        }
        void ThrowIfArmed() { if (!FakeEngine.ThrowOnLoad) return; FakeEngine.ThrowOnLoad = false; throw new InvalidOperationException("engine: load blew up"); }
        public T Load() { Begin(); ThrowIfArmed(); Resolve(); return Target; }
        public Awaitable<T> LoadAsync()
        {
            if (_op != null) return _op;
            Begin(); ThrowIfArmed(); _op = new Awaitable<T>();
            FakeEngine.PendingLoads.Add(Resolve);
            return _op;
        }
        public void Release()
        {
            if (!_holdsRef) return;
            _holdsRef = false; FakeEngine.Refs[_id.value] = FakeEngine.RefOf(_id.value) - 1;
            if (_away != null) { FakeEngine.RefsAway[(_id.value, _away)] -= 1; _away = null; }
            _status = LoadableStatus.None; _op = null;
        }
        public override string ToString() => _id.ToString();
    }

    public static class ContentLoadManager
    {
        public static ContentDirectoryHandle RegisterContentDirectory(string path) { if (!FakeEngine.Registered.Add(path)) throw new InvalidOperationException("Content directory is already registered: " + path); FakeEngine.RegisterOrder.Remove(path); FakeEngine.RegisterOrder.Add(path); return new ContentDirectoryHandle { path = path }; }
        public static T[] GetRootAssets<T>(ContentDirectoryHandle h) where T : Object
        { var l = new List<T>(); foreach (var o in FakeEngine.Dirs[h.path]) if (o is T t) l.Add(t); return l.ToArray(); }
        public static ContentDirectoryHandle[] GetContentDirectories()
        { var l = new List<ContentDirectoryHandle>(); foreach (var p in FakeEngine.Registered) l.Add(new ContentDirectoryHandle { path = p }); return l.ToArray(); }
        public static void UnregisterContentDirectory(ContentDirectoryHandle h)
        {
            foreach (var kv in FakeEngine.IdDir) if (kv.Value == h.path && FakeEngine.RefOf(kv.Key) - FakeEngine.AwayRefs(kv.Key) > 0) throw new InvalidOperationException($"engine: unregister with live Loadable {kv.Key}");
            foreach (var kv in FakeEngine.RefsAway) if (kv.Key.Item2 == h.path && kv.Value > 0) throw new InvalidOperationException($"engine: unregister with live Loadable {kv.Key.Item1} it serves for another directory — the real engine unloads it under its holder");
            foreach (var s in FakeEngine.LoadedScenes) if (FakeEngine.SceneDir.TryGetValue(s.loadableSceneId.guid, out var d) && d == h.path) throw new InvalidOperationException($"engine: unregister with open scene {s.name}");
            foreach (var kv in FakeEngine.ScenesInFlight) if (FakeEngine.SceneDir.TryGetValue(kv.Key, out var d) && d == h.path) throw new InvalidOperationException($"engine: unregister with a scene load in flight {kv.Key} — the real editor crashes");
            FakeEngine.Registered.Remove(h.path); FakeEngine.RegisterOrder.Remove(h.path);
        }
    }
}

namespace UnityEditor
{
    using UnityEngine;
    using Unity.Loading;
    public class MenuItem : Attribute { public MenuItem(string s) { } public MenuItem(string s, bool validate) { } }
    // Singleton backed by a ProjectSettings file. The real one reads and writes the FilePath file; here it is in-memory and only counts saves.
    public class FilePathAttribute : Attribute
    {
        public enum Location { PreferencesFolder, ProjectFolder }
        public FilePathAttribute(string relativePath, Location location) { }
    }
    public class ScriptableSingleton<T> : UnityEngine.ScriptableObject where T : UnityEngine.ScriptableObject, new()
    {
        static T s_Instance;
        public static T instance => s_Instance ??= new T();
        public static int SaveCount;                          // test helper
        protected void Save(bool saveAsText) { SaveCount++; }
        public static void ResetForTests() { s_Instance = null; SaveCount = 0; }
    }
    public enum BuildTarget { NoTarget = -2, StandaloneOSX = 2, StandaloneWindows = 5, iOS = 9, Android = 13, StandaloneWindows64 = 19, StandaloneLinux64 = 24 }
    public static class EditorUserBuildSettings { public static BuildTarget activeBuildTarget = BuildTarget.StandaloneOSX; }
    public static class Menu { public static void SetChecked(string item, bool value) { } }
    public enum PlayModeStateChange { EnteredEditMode, ExitingEditMode, EnteredPlayMode, ExitingPlayMode }
    public static class EditorPrefs
    {
        static readonly Dictionary<string, bool> s_Bools = new();
        public static bool GetBool(string key, bool def = false) => s_Bools.TryGetValue(key, out var v) ? v : def;
        public static void SetBool(string key, bool value) => s_Bools[key] = value;
    }
    public class SceneAsset : UnityEngine.Object { }
    public class DefaultAsset : UnityEngine.Object { }   // a folder's main asset
    public enum SpritePackerMode { Disabled = 0, BuildTimeOnly = 1, AlwaysOn = 2, BuildTimeOnlyAtlas = 3, AlwaysOnAtlas = 4, SpriteAtlasV2 = 5, SpriteAtlasV2Build = 6 }
    public static class EditorSettings { public static SpritePackerMode spritePackerMode = SpritePackerMode.SpriteAtlasV2; }   // the test projects' setting
    public static class EditorApplication
    {
        public static bool isPlaying;
        public static int ExitCode = -1;                                  // test helper: the code Exit was called with, or -1 if it was never called
        public static void Exit(int code) { ExitCode = code; }
        public static event Action projectChanged;
        public static void RaiseProjectChanged() => projectChanged?.Invoke();   // test helper
        public static event Action<PlayModeStateChange> playModeStateChanged;
        public static void RaisePlayModeStateChanged(PlayModeStateChange c) => playModeStateChanged?.Invoke(c);   // test helper
    }
    [AttributeUsage(AttributeTargets.Method)] public class InitializeOnLoadMethodAttribute : Attribute { }
    /// <summary>The engine calls a subclass's static OnPostprocessAllAssets after every import batch — FakeEngine.Import does here.</summary>
    public class AssetPostprocessor { }
    public enum AssetPathToGUIDOptions { IncludeRecentlyDeletedAssets, OnlyExistingAssets }
    public static class AssetDatabase
    {
        public static int RefreshCalls;                       // test helper
        public static void Refresh() { RefreshCalls++; }
        // guid == path (stub convention)
        public static T LoadAssetAtPath<T>(string p) where T : Object
            => p != null && FakeEngine.MainAssetAt.TryGetValue(p, out var o) ? o as T : null;
        /// <summary>Matches 6000.6 (measured): the parent folder must exist — an error is logged and a UnityException thrown otherwise.</summary>
        public static void CreateAsset(Object o, string p)
        {
            int slash = p.LastIndexOf('/');
            var parent = slash > 0 ? p.Substring(0, slash) : "";
            if (!IsValidFolder(parent))
            {
                Debug.LogError("Parent directory must exist before creating asset at " + p + ".");
                throw new UnityException("Creating asset at path " + p + " failed.");
            }
            FakeEngine.MainAssetAt[p] = o; FakeEngine.PathOf[o] = p;
        }
        public static void ImportAsset(string path) { if (path != null && System.IO.Directory.Exists(path)) FakeEngine.Folders.Add(path.Replace('\\', '/').TrimEnd('/')); }   // a folder on disk becomes a known folder
        public static string CreateFolder(string parent, string name)
        {
            if (!IsValidFolder(parent)) { Debug.LogError("Parent directory must exist before creating folder " + parent + "/" + name); return ""; }
            var path = parent + "/" + name; FakeEngine.Folders.Add(path); return path;   // guid == path (stub convention)
        }
        public static string[] FindAssets(string f) => FindAssets(f, null);
        public static int FindCalls;   // the searches by name a path search makes — what it costs
        public static int TypeFindCalls;   // the searches by type ("t:…")
        public static string[] FindAssets(string f, string[] folders)
        {
            if (!string.IsNullOrEmpty(f) && !f.StartsWith("t:", StringComparison.Ordinal)) FindCalls++;
            if (f != null && f.StartsWith("t:", StringComparison.Ordinal)) TypeFindCalls++;
            var hits = new List<string>();
            bool byType = f != null && f.StartsWith("t:");
            var term = byType ? f.Substring(2) : f;
            foreach (var kv in FakeEngine.MainAssetAt)
            {
                if (folders != null)
                {
                    bool inFolder = false;
                    foreach (var folder in folders) if (FakeEngine.ProjectPathComparer.Key(kv.Key).StartsWith(FakeEngine.ProjectPathComparer.Key(folder.TrimEnd('/')) + "/", StringComparison.OrdinalIgnoreCase)) { inFolder = true; break; }   // a trailing separator finds the same assets (measured on 6000.6)
                    if (!inFolder) continue;
                }
                if (byType) { if (Matches(kv.Value.GetType(), term)) hits.Add(AssetPathToGUID(kv.Key)); }
                // the term is a case-insensitive part of the file name without its extension: a term with an extension or a
                // path piece finds nothing (measured on 6000.6)
                else if (string.IsNullOrEmpty(term) || System.IO.Path.GetFileNameWithoutExtension(kv.Key).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(AssetPathToGUID(kv.Key));
            }
            return hits.ToArray();
        }
        static bool Matches(Type t, string name) { for (var x = t; x != null; x = x.BaseType) if (x.Name == name) return true; return false; }
        public static string GUIDToAssetPath(string g) => g != null && FakeEngine.PathOfGuid.TryGetValue(g, out var p) ? p : g;
        // A path is found in any case and in either Unicode form, and answered with the spelling it was stored with (the disk's —
        // measured on 6000.6); one with no GUID set stands for its own GUID. A path the project does not hold has an empty
        // GUID — but one deleted this session answers its old GUID unless existing assets are asked for (both measured on 6000.6).
        public static string AssetPathToGUID(string p) => AssetPathToGUID(p, AssetPathToGUIDOptions.IncludeRecentlyDeletedAssets);
        public static string AssetPathToGUID(string p, AssetPathToGUIDOptions options)
        {
            if (p == null) return null;
            if (string.Equals(p, "Assets", StringComparison.OrdinalIgnoreCase)) return "Assets";
            foreach (var q in new[] { p, p.Normalize(System.Text.NormalizationForm.FormC), p.Normalize(System.Text.NormalizationForm.FormD) })
            {
                if (FakeEngine.GuidOfPath.TryGetValue(q, out var g)) return g;
                if (FakeEngine.Folders.TryGetValue(q, out var folder)) return folder;
                if (FakeEngine.MainAssetAt.TryGetValue(q, out var o) && FakeEngine.PathOf.TryGetValue(o, out var stored)) return stored;
            }
            if (options == AssetPathToGUIDOptions.IncludeRecentlyDeletedAssets && FakeEngine.DeletedGuidOfPath.TryGetValue(p, out var gone)) return gone;
            return "";
        }
        public static bool IsValidFolder(string p) { if (p == null) return false; p = p.TrimEnd('/'); return string.Equals(p, "Assets", StringComparison.OrdinalIgnoreCase) || FakeEngine.Folders.Contains(p) || FakeEngine.Folders.Contains(p.Normalize(System.Text.NormalizationForm.FormC)) || FakeEngine.Folders.Contains(p.Normalize(System.Text.NormalizationForm.FormD)); }   // Assets always exists; a trailing separator is accepted, a path in either Unicode form found (measured on 6000.6)
        public static string GetAssetPath(Object o) => o != null && FakeEngine.PathOf.TryGetValue(o, out var p) ? p : "";
        public static bool IsSubAsset(Object o) => o != null && FakeEngine.IsSub.Contains(o);
        public static Object LoadMainAssetAtPath(string p) => p != null && FakeEngine.MainAssetAt.TryGetValue(p, out var o) ? o : null;
        public static Type GetMainAssetTypeAtPath(string p) => p != null && FakeEngine.MainAssetAt.TryGetValue(p, out var o) ? o.GetType() : null;
        public static int DependencyCalls;   // test helper
        /// <summary>The file and the files its objects reference (followed when recursive), each once — not what only the objects link (measured on 6000.6).</summary>
        public static string[] GetDependencies(string path, bool recursive)
        {
            ++DependencyCalls;
            var files = new List<string> { path };
            var seen = new HashSet<Object>();
            void Walk(Object o, bool own)
            {
                if (o == null || !seen.Add(o)) return;
                if (FakeEngine.PathOf.TryGetValue(o, out var p) && !files.Contains(p)) files.Add(p);
                if ((own || recursive) && FakeEngine.Uses.TryGetValue(o, out var uses)) foreach (var u in uses) Walk(u, false);
            }
            if (FakeEngine.MainAssetAt.TryGetValue(path, out var main)) Walk(main, true);
            if (FakeEngine.SubAssetsAt.TryGetValue(path, out var subs)) foreach (var s in subs) Walk(s, true);
            return files.ToArray();
        }
        public static Object[] LoadAllAssetRepresentationsAtPath(string p)
            => p != null && FakeEngine.SubAssetsAt.TryGetValue(p, out var l) ? l.ToArray() : new Object[0];
        public static void SaveAssets() { }
    }
    public static class EditorUtility
    {
        public static int DirtyCountForTests;   // test helper: what moves when an undo reverts an object no window listens for (measured 0 → 1 on 6000.6)
        public static int GetDirtyCount(Object o) => DirtyCountForTests;
        public static void SetDirty(Object o) { }
        public static int CollectCalls;   // test helper: a collection loads what it reaches
        /// <summary>The objects and what they reference or link, followed, each once (the order is not the engine's).</summary>
        public static Object[] CollectDependencies(Object[] roots)
        {
            ++CollectCalls;
            var list = new List<Object>();
            var seen = new HashSet<Object>();
            void Walk(Object o)
            {
                if (o == null || !seen.Add(o)) return;
                list.Add(o);
                if (FakeEngine.Uses.TryGetValue(o, out var uses)) foreach (var u in uses) Walk(u);
                if (FakeEngine.Links.TryGetValue(o, out var links)) foreach (var l in links) Walk(l);
            }
            foreach (var root in roots) Walk(root);
            return list.ToArray();
        }
        public static int ProgressCalls;
        public static void DisplayProgressBar(string title, string info, float t) => ProgressCalls++;
        public static void ClearProgressBar() { }
    }
    public class BuildContentDirectoryParameters
    {
        public string name; public string outputPath; public string[] rootAssetPaths;
        public UnityEngine.BuildCompression compression = UnityEngine.BuildCompression.Uncompressed;
        public BuildContentOptions options;
    }
    [Flags] public enum BuildContentOptions
    {
        None = 0, CleanBuildCache = 1, DisableWriteTypeTree = 2, FailBuildWhenErrorsLogged = 4,
        SerializeUnityVersion = 8, SkipExportToOutputPath = 16, UseArchive = 32,
    }
    /// <summary>Records the parameters it received and, like the real engine, writes a manifest plus .cf files to the output folder (flat layout, real-engine format).</summary>
    public static class BuildPipeline
    {
        public static BuildContentDirectoryParameters LastParameters;
        public static bool FailNextBuild;
        public static bool ThrowNextBuild;                   // the next BuildContentDirectory throws
        public static bool ManifestWithoutContentHash;       // the fake manifest omits ContentHash (older shape)
        public static int ManifestVersion = 1;               // the fake manifest's version: another is a shape the package does not know
        public static readonly string[] EngineKinds = { ".cf", ".archive", ".resS", ".resource" };
        public static Build.Reporting.BuildReport BuildContentDirectory(BuildContentDirectoryParameters p)
        {
            LastParameters = p;
            if (ThrowNextBuild) { ThrowNextBuild = false; throw new InvalidOperationException("engine: boom"); }
            string twice = null;
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the engine refuses an asset given twice, a path in another case too (measured on 6000.6)
            foreach (var root in p.rootAssetPaths ?? new string[0])
                if (!seenRoots.Add(root)) { twice = root; break; }
            var result = Build.Reporting.BuildResult.Succeeded;
            if (FailNextBuild) { FailNextBuild = false; result = Build.Reporting.BuildResult.Failed; }
            else if (twice != null) { UnityEngine.Debug.LogError($"Asset \"{twice}\" has been specified more than once."); result = Build.Reporting.BuildResult.Failed; }
            else if (!string.IsNullOrEmpty(p.outputPath))
            {
                System.IO.Directory.CreateDirectory(p.outputPath);
                foreach (var f in System.IO.Directory.GetFiles(p.outputPath))   // what the engine removes by itself, in any folder (measured on 6000.6): the top level's files of its kinds, any case, and those named by a hash — 32 hex digits, any case, before the first dot or the end
                    if (Array.Exists(EngineKinds, k => string.Equals(k, System.IO.Path.GetExtension(f), StringComparison.OrdinalIgnoreCase)) || System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(f), "^[0-9a-f]{32}(\\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        System.IO.File.Delete(f);
                string failed = null;
                void Write(string path, string text)   // the engine writes in place and fails the build on a file it cannot write — a read-only marker (measured)
                {
                    if (failed != null) return;
                    try { System.IO.File.WriteAllText(path, text); }
                    catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { failed = path; }
                }
                if ((p.options & BuildContentOptions.UseArchive) != 0 || p.compression.Kind != UnityEngine.CompressionKindForStub.None)
                {
                    Write(System.IO.Path.Combine(p.outputPath, "content0.archive"), "archive");
                    Write(System.IO.Path.Combine(p.outputPath, "BuildManifestHash.txt"), "deadbeef");
                }
                else
                {
                    var roots = new List<string>(); var loadables = new List<string>(); var files = new List<string>();
                    var scenes = new List<string>(); var carried = new HashSet<string>(); var held = new List<string>();   // held: asset and scene paths whose holdings the build carries
                    files.Add("{\"Index\":0,\"ID\":\"Library/unity default resources\",\"IsBuiltIn\":true,\"SerializedFileDependencies\":[]}");
                    int next = 1, rootIndex = 0;
                    foreach (var root in p.rootAssetPaths ?? new string[0])
                    {
                        FakeEngine.MainAssetAt.TryGetValue(root, out var rootObject);   // a root catalog is a load target too, keyed by its own id's hash (measured)
                        var rootHash = Unity.Loading.LoadableObjectId.OidHex(rootObject != null && FakeEngine.ObjectIdOf.TryGetValue(rootObject, out var rootId) ? rootId : -(++rootIndex));
                        roots.Add("\"" + rootHash + "\"");
                        if (carried.Add(rootHash)) loadables.Add("{\"ObjectIdHash\":\"" + rootHash + "\",\"SerializedFile\":0,\"Identifier\":0}");   // file 0: the checks count the entries' files only
                        if (rootObject is xpTURN.ContentLink.ContentCatalog sceneCat)
                            foreach (var se in sceneCat.Scenes)
                            {
                                var scenePath = AssetDatabase.GUIDToAssetPath(se.guid);
                                if (carried.Add("scene:" + se.guid)) scenes.Add("{\"GUID\":\"" + se.guid + "\",\"Path\":\"" + scenePath + "\",\"SerializedFile\":0}");
                                held.Add(scenePath);
                            }
                        if (FakeEngine.MainAssetAt.TryGetValue(root, out var o) && o is xpTURN.ContentLink.ContentCatalog cat)
                            foreach (var e in cat.Entries)
                            {
                                if (!string.IsNullOrEmpty(e.guid)) held.Add(AssetDatabase.GUIDToAssetPath(e.guid));
                                carried.Add(Unity.Loading.LoadableObjectId.OidHex(e.id.value));
                                var hash = Unity.Loading.LoadableObjectId.OidHex(e.id.value);
                                loadables.Add("{\"ObjectIdHash\":\"" + hash + "\",\"SerializedFile\":" + next + ",\"Identifier\":" + e.id.value + "}");
                                files.Add("{\"Index\":" + next + ",\"ID\":\"" + hash + ".cfid\",\"IsBuiltIn\":false,\"SerializedFileDependencies\":[0]" + (ManifestWithoutContentHash ? "" : ",\"ContentHash\":\"d" + hash.Substring(1) + "\"") + "}");
                                Write(System.IO.Path.Combine(p.outputPath, "d" + hash.Substring(1) + ".cf"), "cf");   // matches the real engine: the on-disk name differs from the cfid
                                next++;
                            }
                    }
                    foreach (var path in held)
                    {
                        if (FakeEngine.HeldIds.TryGetValue(path, out var ids))
                            foreach (var id in ids)
                            {
                                var hash = Unity.Loading.LoadableObjectId.OidHex(id);
                                if (carried.Add(hash)) loadables.Add("{\"ObjectIdHash\":\"" + hash + "\",\"SerializedFile\":0,\"Identifier\":" + id + "}");
                            }
                        if (FakeEngine.HeldScenes.TryGetValue(path, out var heldScenes))
                            foreach (var scenePath in heldScenes)
                            {
                                var guid = AssetDatabase.AssetPathToGUID(scenePath);
                                if (carried.Add("scene:" + guid)) scenes.Add("{\"GUID\":\"" + guid + "\",\"Path\":\"" + scenePath + "\",\"SerializedFile\":0}");
                            }
                    }
                    var json = "{\"Version\":" + ManifestVersion + ",\"BuildInfo\":{\"BuildName\":\"" + (p.name ?? "") + "\",\"BuiltWithTypeTrees\":" + ((p.options & BuildContentOptions.DisableWriteTypeTree) == 0 ? "true" : "false") + "},"
                             + "\"RootAssets\":[" + string.Join(",", roots) + "],\"Loadables\":[" + string.Join(",", loadables) + "],\"LoadableScenes\":[" + string.Join(",", scenes) + "],\"SerializedFiles\":[" + string.Join(",", files) + "]}";
                    Write(System.IO.Path.Combine(p.outputPath, "abc123.json"), json);
                    Write(System.IO.Path.Combine(p.outputPath, "BuildManifestHash.txt"), "abc123");
                }
                if (failed != null)
                {
                    UnityEngine.Debug.LogError($"Failed to write {System.IO.Path.GetFullPath(failed)}");
                    UnityEngine.Debug.LogError("Content Build failed");
                    result = Build.Reporting.BuildResult.Failed;
                }
            }
            return new Build.Reporting.BuildReport { summary = new Build.Reporting.BuildSummary { result = result } };
        }
    }
    public static class SessionState
    {
        static readonly Dictionary<string, object> s_Values = new();
        public static void SetString(string k, string v) => s_Values[k] = v;
        public static string GetString(string k, string d) => s_Values.TryGetValue(k, out var v) ? (string)v : d;
        public static void SetBool(string k, bool v) => s_Values[k] = v;
        public static bool GetBool(string k, bool d) => s_Values.TryGetValue(k, out var v) ? (bool)v : d;
        public static void SetInt(string k, int v) => s_Values[k] = v;
        public static int GetInt(string k, int d) => s_Values.TryGetValue(k, out var v) ? (int)v : d;
        public static void SetFloat(string k, float v) => s_Values[k] = v;
        public static float GetFloat(string k, float d) => s_Values.TryGetValue(k, out var v) ? (float)v : d;
    }
    public static class LoadableObjectIdEditorUtility
    {
        public static LoadableObjectId CreateLoadableObjectId(Object o)
            => o != null && FakeEngine.ObjectIdOf.TryGetValue(o, out var v) ? new LoadableObjectId { value = v } : default;
        public static Object LoadableObjectIdToObject(LoadableObjectId id)
            => FakeEngine.ObjectById.TryGetValue(id.value, out var o) ? o : null;
    }
    public static class LoadableSceneIdEditorUtility
    {
        public static LoadableSceneId CreateLoadableSceneId(string scenePath)
            => scenePath != null && FakeEngine.SceneIdOfPath.TryGetValue(scenePath, out var v) ? new LoadableSceneId { guid = v } : default;
        public static SceneAsset LoadableSceneIdToScene(LoadableSceneId id)
            => FakeEngine.SceneAssetById.TryGetValue(id.guid, out var o) ? o as SceneAsset : null;
    }
}

namespace UnityEditor.SceneManagement
{
    using Unity.Loading;
    using UnityEngine;
    using UnityEngine.SceneManagement;
    /// <summary>Editor path that opens a scene by asset path without a build. Mirrors the real API's location.</summary>
    public static class EditorSceneManager
    {
        public static AsyncOperation LoadSceneAsyncInPlayMode(string path, LoadSceneParameters p)
        {
            if (string.IsNullOrEmpty(path) || !FakeEngine.MainAssetAt.ContainsKey(path)) return null;
            var op = new AsyncOperation();
            FakeEngine.PendingLoads.Add(() =>
            {
                if (p.loadSceneMode == LoadSceneMode.Single)
                    foreach (var s in FakeEngine.LoadedScenes.ToArray()) UnityEngine.SceneManagement.SceneManager.UnloadScene(s);
                var scene = UnityEngine.SceneManagement.SceneManager.NewLiveScene(path);
                FakeEngine.LoadedScenes.Add(scene);
                UnityEngine.SceneManagement.SceneManager.RaiseSceneLoaded(scene, p.loadSceneMode);
                op.Complete();
            });
            return op;
        }
    }
}

namespace UnityEditor.Build.Reporting
{
    public enum BuildResult { Unknown, Succeeded, Failed, Cancelled }
    public struct BuildSummary { public BuildResult result; }
    public class BuildReport { public BuildSummary summary; }
}

// ───────────── archive packing and mounting (real surface confirmed against the 6000.6 XML docs / decompiled assemblies) ─────────────
namespace Unity.Jobs { public struct JobHandle { public void Complete() { } } }

namespace Unity.Content
{
    /// <summary>Real API: a struct with IsValid, GetName, Delete, GetOrCreateNamespace (alphanumeric, at most 16 chars) and GetAll. Default is omitted.</summary>
    public struct ContentNamespace
    {
        internal string name;
        public bool IsValid => name != null && Unity.Loading.FakeEngine.Namespaces.Contains(name);
        public string GetName() => name;
        public void Delete() => Unity.Loading.FakeEngine.Namespaces.Remove(name);
        public static ContentNamespace GetOrCreateNamespace(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 16) throw new ArgumentException("engine: namespace name must be 1..16 characters: " + name);
            foreach (var c in name) if (!char.IsLetterOrDigit(c)) throw new ArgumentException("engine: namespace name must be alphanumeric: " + name);
            Unity.Loading.FakeEngine.Namespaces.Add(name);
            return new ContentNamespace { name = name };
        }
        public static ContentNamespace[] GetAll()
        {
            var l = new List<ContentNamespace>();
            foreach (var n in Unity.Loading.FakeEngine.Namespaces) l.Add(new ContentNamespace { name = n });
            return l.ToArray();
        }
    }
}

namespace Unity.IO.Archive
{
    using Unity.Loading;
    public enum ArchiveStatus { InProgress, Complete, Failed }
    /// <summary>Real API: a struct handle whose state lives in the engine, so every copy sees the same thing. default is an invalid handle.</summary>
    public struct ArchiveHandle
    {
        internal int id;   // starts at 1
        FakeEngine.Mount M => FakeEngine.Mounts[id - 1];
        public ArchiveStatus Status => id == 0 ? ArchiveStatus.Failed : !M.Mounted ? throw new InvalidOperationException("The archive has already been unmounted.") : FakeEngine.Frame < M.ReadyFrame ? ArchiveStatus.InProgress : M.Failed ? ArchiveStatus.Failed : ArchiveStatus.Complete;   // a mount that fails says so when its turn comes (measured)
        public Unity.Jobs.JobHandle Unmount()
        {
            if (id == 0) throw new InvalidOperationException("engine: unmount of an archive that is not mounted");
            if (!M.Mounted) throw new InvalidOperationException("The archive has already been unmounted.");   // measured
            M.Mounted = false; return default;
        }
        public string GetMountPath() => "ns:/" + M.Namespace + "/" + M.Prefix + "/";   // real-engine format: ns:/clprobe/clp/
    }
    public static class ArchiveFileInterface
    {
        public static ArchiveHandle MountAsync(Unity.Content.ContentNamespace ns, string filePath, string prefix)
        {
            if (!ns.IsValid) throw new ArgumentException("engine: invalid namespace");
            // The engine mounts one archive after another, across namespaces (measured): this one's turn comes after every mount still in
            // progress. An unmounted one leaves the line, but the ones after it keep their frames — the engine has them done by the time
            // Unmount().Complete() returns: only the timing differs.
            int start = FakeEngine.Frame;
            foreach (var m in FakeEngine.Mounts) if (m.Mounted && m.ReadyFrame > start) start = m.ReadyFrame;
            var file = System.IO.Path.GetFileName(filePath);
            FakeEngine.Mounts.Add(new FakeEngine.Mount
            {
                Namespace = ns.GetName(), Path = filePath, Prefix = prefix, Mounted = true,
                ReadyFrame = start + (FakeEngine.MountFramesOf.TryGetValue(file, out var frames) ? frames : FakeEngine.MountFrames),
                Failed = !System.IO.File.Exists(filePath) || FakeEngine.FailMount.Contains(file),
            });
            return new ArchiveHandle { id = FakeEngine.Mounts.Count };
        }
        public static ArchiveHandle[] GetMountedArchives(Unity.Content.ContentNamespace ns)
        {
            var l = new List<ArchiveHandle>();
            for (int i = 0; i < FakeEngine.Mounts.Count; ++i)
            {
                var m = FakeEngine.Mounts[i];
                if (m.Namespace == ns.GetName() && m.Mounted) l.Add(new ArchiveHandle { id = i + 1 });   // a failed mount stays listed, as in the engine (measured)
            }
            return l.ToArray();
        }
    }
}

namespace UnityEditor.Build.Content
{
    public struct ResourceFile { public string fileName; public string fileAlias; public bool serializedFile; }
    public static class ContentBuildInterface
    {
        /// <summary>A fake archive is the concatenation of aliases and contents. The CRC is an FNV hash of the content, so the determinism check compares bytes.</summary>
        public static uint ArchiveAndCompress(ResourceFile[] files, string outputPath, UnityEngine.BuildCompression compression)
        {
            using var w = new System.IO.MemoryStream();
            foreach (var f in files)
            {
                var alias = System.Text.Encoding.UTF8.GetBytes(f.fileAlias + (f.serializedFile ? "|sf\n" : "\n"));
                w.Write(alias, 0, alias.Length);
                var bytes = System.IO.File.ReadAllBytes(f.fileName);
                w.Write(bytes, 0, bytes.Length);
            }
            var data = w.ToArray();
            System.IO.File.WriteAllBytes(outputPath, data);
            uint crc = 2166136261; foreach (var b in data) crc = (crc ^ b) * 16777619;
            var aliases = new string[files.Length]; for (int i = 0; i < files.Length; ++i) aliases[i] = files[i].fileAlias;
            Unity.Loading.FakeEngine.Packed.Add((outputPath, aliases, compression.Kind));
            return crc == 0 ? 1u : crc;
        }
    }
}

// ───────────── editor surface used by the build window model ─────────────
namespace UnityEditor
{
    public static class Undo
    {
        public static int RecordCalls; public static string LastName;   // test helper
        public static void RecordObject(UnityEngine.Object o, string name) { RecordCalls++; LastName = name; }
        public static event Action undoRedoPerformed;
        public static void RaiseUndoRedo() => undoRedoPerformed?.Invoke();   // test helper
    }
    /// <summary>The real API returns a cached, unordered TypeCollection. The stub scans the loaded assemblies, excluding T itself.</summary>
    public static class TypeCache
    {
        public static List<Type> GetTypesDerivedFrom<T>()
        {
            var l = new List<Type>();
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types) if (t != null && t != typeof(T) && typeof(T).IsAssignableFrom(t)) l.Add(t);
            }
            return l;
        }
    }
}

// ───────────── UnityWebRequest over a fake server ─────────────
namespace UnityEngine.Networking
{
    using Unity.Loading;
    public class DownloadHandler : IDisposable
    {
        public virtual byte[] data => null;
        public string text => data == null ? null : System.Text.Encoding.UTF8.GetString(data);
        public virtual void Dispose() { }
        internal virtual void Receive(byte[] bytes, int offset, int count) { }
        internal virtual void End(bool failed) { }   // failed: an error or an abort ended the request
    }
    public sealed class DownloadHandlerBuffer : DownloadHandler
    {
        readonly System.IO.MemoryStream m_Data = new();
        public override byte[] data => m_Data.ToArray();
        internal override void Receive(byte[] bytes, int offset, int count) => m_Data.Write(bytes, offset, count);
    }
    /// <summary>Measured: the file is made when the handler is made — a missing folder too — and a file it cannot make throws there; append keeps what is there; received bytes stay when the request fails unless removeFileOnAbort, which deletes the file on any failure — an error when it ends the request, an Abort or Dispose there and then.</summary>
    public sealed class DownloadHandlerFile : DownloadHandler
    {
        readonly string m_Path; readonly bool m_Append; System.IO.FileStream m_File;
        public bool removeFileOnAbort;
        public DownloadHandlerFile(string path) : this(path, false) { }
        public DownloadHandlerFile(string path, bool append)
        {
            m_Path = path; m_Append = append;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                m_File = new System.IO.FileStream(path, append ? System.IO.FileMode.Append : System.IO.FileMode.Create, System.IO.FileAccess.Write);
            }
            catch (Exception) { throw new ArgumentException("Failed to create file " + path); }
        }
        internal override void Receive(byte[] bytes, int offset, int count) => m_File.Write(bytes, offset, count);
        internal override void End(bool failed)
        {
            if (m_File != null) { m_File.Dispose(); m_File = null; }
            if (failed && removeFileOnAbort && System.IO.File.Exists(m_Path)) System.IO.File.Delete(m_Path);
        }
        public override void Dispose() { if (m_File != null) { m_File.Dispose(); m_File = null; } }
    }
    public class UploadHandler { }
    public class UnityWebRequestAsyncOperation : AsyncOperation
    {
        public UnityWebRequest webRequest { get; internal set; }
        internal void Done() => Complete();
    }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }
        public const string kHttpVerbGET = "GET";
        public UnityWebRequest(string url, string method, DownloadHandler downloadHandler, UploadHandler uploadHandler) { this.url = url; this.method = method; this.downloadHandler = downloadHandler; }
        public static UnityWebRequest Get(string url) => new UnityWebRequest(url, kHttpVerbGET, new DownloadHandlerBuffer(), null);
        public string url; public string method; public DownloadHandler downloadHandler;
        public int redirectLimit = 32; public int timeout;
        public Result result { get; internal set; }
        public string error { get; internal set; }
        public long responseCode { get; internal set; }
        public ulong downloadedBytes { get; internal set; }
        public bool isDone { get; internal set; }
        public bool Disposed { get; private set; }   // stub only
        internal readonly Dictionary<string, string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> ResponseHeaders = new(StringComparer.OrdinalIgnoreCase);
        internal UnityWebRequestAsyncOperation Op;
        public void SetRequestHeader(string name, string value) => RequestHeaders[name] = value;
        public string GetRequestHeader(string name) => RequestHeaders.TryGetValue(name, out var v) ? v : null;
        public string GetResponseHeader(string name) => ResponseHeaders.TryGetValue(name, out var v) ? v : null;
        public UnityWebRequestAsyncOperation SendWebRequest() => FakeWeb.Start(this);
        public void Abort() => FakeWeb.Abort(this);
        public void Dispose() { if (Disposed) return; Disposed = true; FakeWeb.Forget(this); downloadHandler?.Dispose(); }
    }
}

namespace Unity.Loading
{
    using UnityEngine;
    using UnityEngine.Networking;
    /// <summary>
    /// The server behind the request stubs, stepped once per frame (Awaitable.NextFrameAsync). A file is served by URL; file:// reads the disk and
    /// ignores Range (measured). Behaviours follow the engine as measured: a dropped connection is ConnectionError "Unknown Error" with
    /// the status code already set and the received bytes kept; a Range the server ignores answers 200 with the whole body. No request is done
    /// inside SendWebRequest, Abort or Dispose (measured): an error, an abort and a dispose end in the next frame — the stub's
    /// one choice of when; the engine takes one frame to a few hundred — and an abort wins over an error still to come.
    /// </summary>
    public static class FakeWeb
    {
        public sealed class File
        {
            public byte[] Data;
            public int Status;              // non-zero: answer every request with this HTTP status
            public bool IgnoreRange;
            public int CutAfter = -1;       // drop the connection after this many body bytes — for the next CutTimes responses
            public int CutTimes = 1;
            public int StallAfter = -1;     // one shot: deliver nothing for StallFrames frames after this many body bytes
            public int StallFrames;
            public int FailTimes;           // the next N requests fail before any byte (ConnectionError, FailError)
            public string FailError = "Cannot connect to destination host";
            public int CorruptTimes;        // the next N responses flip one byte of the body
            public int BytesPerFrame;       // 0 = FakeWeb.BytesPerFrame
            public string CacheControl;
        }
        public static readonly Dictionary<string, File> Files = new(StringComparer.Ordinal);
        public static readonly List<string> Log = new();
        public static int BytesPerFrame = 1 << 20;
        public static double SecondsPerFrame = 0.016, ClockOffset;
        public static int InFlight, MaxInFlight, BackgroundHops;   // InFlight: the requests a caller still waits on — an aborted or disposed one leaves at once, as ContentRemote counts its own (stub only: the engine holds the connection to the end)
        public static Action OnStep;   // test hook: runs after every frame of the transfers
        public static long SentBytes;   // test helper: every body byte delivered — what a download really received
        public static bool CodeWithFirstByte;   // the response code stays 0 until the first body byte arrives, as the engine's does before the headers
        public static int Steps, StepLimit = 200000;   // a watchdog: the harness is synchronous, so a loop that never ends would hang it
        sealed class Transfer { public long Code; public UnityWebRequest Request; public byte[] Body; public int Sent; public int CutAfter = -1; public int StallAfter = -1; public int StallLeft; public int Rate; public (UnityWebRequest.Result Result, long Code, string Error)? End; public bool Aborted; }
        static readonly List<Transfer> s_Active = new();

        public static void Reset() { Files.Clear(); Log.Clear(); s_Active.Clear(); BytesPerFrame = 1 << 20; SecondsPerFrame = 0.016; ClockOffset = 0; InFlight = MaxInFlight = BackgroundHops = 0; OnStep = null; Steps = 0; SentBytes = 0; CodeWithFirstByte = false; }
        public static File Serve(string url, byte[] data) { var f = new File { Data = data }; Files[url] = f; return f; }
        public static int Count(string url) { int n = 0; foreach (var l in Log) if (l.StartsWith("GET " + url + " ", StringComparison.Ordinal)) ++n; return n; }
        public static int CountContaining(string part) { int n = 0; foreach (var l in Log) if (l.Contains(part)) ++n; return n; }

        internal static UnityWebRequestAsyncOperation Start(UnityWebRequest r)
        {
            var op = new UnityWebRequestAsyncOperation { webRequest = r };
            r.Op = op;
            string range = r.GetRequestHeader("Range");
            Log.Add($"GET {r.url} range={range ?? "-"} cc={r.GetRequestHeader("Cache-Control") ?? "-"}");
            ++InFlight; if (InFlight > MaxInFlight) MaxInFlight = InFlight;
            var t = new Transfer { Request = r };
            s_Active.Add(t);
            if (r.url.StartsWith("file://", StringComparison.Ordinal))
            {
                var path = new Uri(r.url).LocalPath;
                if (!System.IO.File.Exists(path)) { EndNextFrame(t, UnityWebRequest.Result.ProtocolError, 404, "HTTP/1.1 404 Not Found"); return op; }
                t.Body = System.IO.File.ReadAllBytes(path); r.responseCode = 200; t.Rate = BytesPerFrame;
                return op;
            }
            if (!Files.TryGetValue(r.url, out var f)) { EndNextFrame(t, UnityWebRequest.Result.ProtocolError, 404, "HTTP/1.1 404 Not Found"); return op; }
            if (f.FailTimes > 0) { --f.FailTimes; EndNextFrame(t, UnityWebRequest.Result.ConnectionError, 0, f.FailError); return op; }
            if (f.Status != 0) { EndNextFrame(t, UnityWebRequest.Result.ProtocolError, f.Status, $"HTTP/1.1 {f.Status} Error"); return op; }
            var body = f.Data;
            if (f.CorruptTimes > 0) { --f.CorruptTimes; body = (byte[])body.Clone(); body[body.Length / 2] ^= 0xFF; }
            long start = 0;
            if (range != null && !f.IgnoreRange && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                start = long.Parse(range.Substring(6, range.IndexOf('-') - 6));
                if (start >= body.Length) { EndNextFrame(t, UnityWebRequest.Result.ProtocolError, 416, "HTTP/1.1 416 Range Not Satisfiable"); return op; }
                r.responseCode = 206;
                r.ResponseHeaders["Content-Range"] = $"bytes {start}-{body.Length - 1}/{body.Length}";
            }
            else r.responseCode = 200;
            if (CodeWithFirstByte) { t.Code = r.responseCode; r.responseCode = 0; }
            r.ResponseHeaders["Accept-Ranges"] = f.IgnoreRange ? "none" : "bytes";
            if (f.CacheControl != null) r.ResponseHeaders["Cache-Control"] = f.CacheControl;
            t.Body = new byte[body.Length - start];
            Array.Copy(body, start, t.Body, 0, t.Body.Length);
            t.CutAfter = f.CutAfter; if (f.CutAfter >= 0 && --f.CutTimes <= 0) f.CutAfter = -1;
            t.StallAfter = f.StallAfter; t.StallLeft = f.StallFrames; f.StallAfter = -1;
            t.Rate = f.BytesPerFrame > 0 ? f.BytesPerFrame : BytesPerFrame;
            return op;
        }

        /// <summary>One frame of every transfer in flight.</summary>
        public static void Step()
        {
            if (++Steps > StepLimit) throw new TimeoutException($"FakeWeb: {StepLimit} frames and the transfers have not ended — a wait that never finishes");
            foreach (var t in new List<Transfer>(s_Active))
            {
                var r = t.Request;
                if (r.isDone) continue;
                if (t.End is { } end) { Finish(t, end.Result, end.Code, end.Error); continue; }
                if (t.Body == null) continue;
                if (t.StallAfter >= 0 && t.Sent >= t.StallAfter && t.StallLeft > 0) { --t.StallLeft; continue; }
                int n = Math.Min(t.Rate, t.Body.Length - t.Sent);
                if (t.StallAfter >= 0 && t.Sent < t.StallAfter) n = Math.Min(n, t.StallAfter - t.Sent);
                if (t.CutAfter >= 0) n = Math.Min(n, Math.Max(0, t.CutAfter - t.Sent));
                if (n > 0) { r.downloadHandler?.Receive(t.Body, t.Sent, n); t.Sent += n; r.downloadedBytes = (ulong)t.Sent; SentBytes += n; if (t.Code != 0) { r.responseCode = t.Code; t.Code = 0; } }
                if (t.CutAfter >= 0 && t.Sent >= t.CutAfter) { Finish(t, UnityWebRequest.Result.ConnectionError, r.responseCode, "Unknown Error"); continue; }
                if (t.Sent >= t.Body.Length) Finish(t, UnityWebRequest.Result.Success, r.responseCode, null);
            }
            OnStep?.Invoke();
        }

        static void Finish(Transfer t, UnityWebRequest.Result result, long code, string error)
        {
            var r = t.Request;
            if (r.isDone) return;
            r.result = result; r.responseCode = code; r.error = error; r.isDone = true;
            if (r.downloadHandler != null && !t.Aborted) r.downloadHandler.End(failed: result != UnityWebRequest.Result.Success);
            s_Active.Remove(t);
            if (!t.Aborted) --InFlight;
            r.Op.Done();
        }

        /// <summary>Measured: an error, an abort or a dispose is known in a later frame — never inside SendWebRequest, Abort or Dispose.</summary>
        static void EndNextFrame(Transfer t, UnityWebRequest.Result result, long code, string error) => t.End = (result, code, error);

        /// <summary>
        /// Measured: Abort and Dispose stop the transfer and close the handler's file there — removeFileOnAbort deletes it there, so a new
        /// handler on the path is safe at once — while the request is done, and completed fires, in a later frame: aborted, also when an error
        /// was still to come (a request cut in the frame of its send ends "Request aborted", whatever the server would have answered).
        /// The engine's responseCode is 0 when no header had come; the stub keeps the one it set when the transfer started.
        /// </summary>
        internal static void Abort(UnityWebRequest r)
        {
            foreach (var t in s_Active)
                if (t.Request == r)
                {
                    if (t.Aborted) return;
                    t.Aborted = true; --InFlight;
                    r.downloadHandler?.End(failed: true);
                    EndNextFrame(t, UnityWebRequest.Result.ConnectionError, r.responseCode, "Request aborted");
                    return;
                }
        }

        internal static void Forget(UnityWebRequest r) => Abort(r);
    }
}
