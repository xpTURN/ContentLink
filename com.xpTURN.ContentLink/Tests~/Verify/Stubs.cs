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
            bool fail = FakeEngine.FailNextSceneLoad; FakeEngine.FailNextSceneLoad = false;
            FakeEngine.PendingLoads.Add(() =>
            {
                FakeEngine.Transitioning--;
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
        public string name = "";
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
            var go = new GameObject(src.activeSelf) { name = src.name + "(Clone)", scene = SceneManager.Active };
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
            if (o is GameObject go) foreach (var c in go.components) { if (c.awoken) Msg(c, "OnDestroy"); c.destroyed = true; }
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
        public Scene scene;
        public bool activeSelf { get; private set; } = true;
        public GameObject() { All.Add(this); }
        public GameObject(bool active) { activeSelf = active; All.Add(this); }
        internal void AwakeAll() { foreach (var c in components.ToArray()) if (!c.awoken) { c.awoken = true; Msg(c, "Awake"); } }
        public void SetActive(bool v) { activeSelf = v; if (v) AwakeAll(); }
        public T AddComponent<T>() where T : Component, new() { var c = new T { gameObject = this }; components.Add(c); if (activeSelf) { c.awoken = true; Msg(c, "Awake"); } return c; }
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
    public static class Time { public static int frameCount; }
    public static class Application { public static event Action quitting; public static void Quit() => quitting?.Invoke(); public static string dataPath = "/fake/Assets"; }
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
        public static Awaitable FromAsyncOperation(AsyncOperation op, CancellationToken cancellationToken = default)
        {
            var a = new Awaitable();
            if (op.isDone) { a.TryComplete(null); return a; }
            CancellationTokenRegistration reg = default;
            if (cancellationToken.CanBeCanceled) reg = cancellationToken.Register(() => a.TryComplete(new OperationCanceledException(cancellationToken)));
            op.completed += _ => { reg.Dispose(); a.TryComplete(null); };
            return a;
        }
        /// <summary>Only advances the frame counter and completes immediately; the mount stub uses that counter to go from InProgress to Complete.</summary>
        public static Awaitable NextFrameAsync()
        {
            Unity.Loading.FakeEngine.Frame++;
            var a = new Awaitable(); a.TryComplete(null); return a;
        }
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
}

namespace UnityEngine.PlayerLoop { public struct PostLateUpdate { } }

namespace UnityEngine.U2D { public class SpriteAtlas : UnityEngine.Object { } }

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
    [Serializable] public struct LoadableObjectId
    {
        public int value;
        public bool IsValid => value != 0;
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
        /// <summary>Matches the real editor: in Play Mode an id that is in no directory still loads. A player build does not do that.</summary>
        public static bool EditorServesAnyId;
        public static bool FailNextSceneLoad;   // the next engine scene load completes without producing a scene
        public static readonly List<Action> PendingLoads = new();
        public static int LoadCalls;
        // Archives: namespaces, mounts and packing records
        public static readonly HashSet<string> Namespaces = new();
        public static readonly List<Mount> Mounts = new();
        public static readonly HashSet<string> FailMount = new();          // archives with these file names fail to mount
        public static int MountFrames = 1;                                  // frames until a mount reports Complete
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
        public static bool IsLoaded(UnityEngine.SceneManagement.SceneHandle h) { foreach (var s in LoadedScenes) if (s.handle == h) return true; return false; }
        // Fake editor project: path ↔ asset ↔ LoadableObjectId
        public static readonly Dictionary<string, Object> MainAssetAt = new();
        public static readonly Dictionary<string, List<Object>> SubAssetsAt = new();
        public static readonly Dictionary<Object, string> PathOf = new();
        public static readonly HashSet<Object> IsSub = new();
        public static readonly HashSet<string> Folders = new();
        public static readonly Dictionary<Object, int> ObjectIdOf = new();
        public static readonly Dictionary<int, Object> ObjectById = new();
        public static readonly Dictionary<string, int> SceneIdOfPath = new();
        public static readonly Dictionary<int, Object> SceneAssetById = new();
        // GUID table: a path listed here has its own GUID that survives MoveProjectAsset; any other path is its own GUID (the stub convention)
        public static readonly Dictionary<string, string> GuidOfPath = new();
        public static readonly Dictionary<string, string> PathOfGuid = new();
        public static void SetGuid(string path, string guid) { GuidOfPath[path] = guid; PathOfGuid[guid] = path; }
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
            MainAssetAt.Clear(); SubAssetsAt.Clear(); PathOf.Clear(); IsSub.Clear(); Folders.Clear();
            ObjectIdOf.Clear(); ObjectById.Clear(); SceneIdOfPath.Clear(); SceneAssetById.Clear();
            GuidOfPath.Clear(); PathOfGuid.Clear();
        }
        public static int RefOf(int id) => Refs.TryGetValue(id, out var r) ? r : 0;
        public static void CompleteLoads() { var p = new List<Action>(PendingLoads); PendingLoads.Clear(); foreach (var a in p) a(); }
    }

    public class Loadable<T> where T : Object
    {
        readonly LoadableObjectId _id; LoadableStatus _status; Awaitable<T> _op; bool _holdsRef;
        public Loadable(LoadableObjectId id) { _id = id; }
        public LoadableObjectId LoadableObjectId => _id;
        public LoadableStatus Status => _status;
        public T Target => _status == LoadableStatus.Loaded ? FakeEngine.Assets[_id.value] as T : null;
        void Resolve()
        {
            if (_status != LoadableStatus.Loading) return;
            bool served = FakeEngine.Registered.Contains(FakeEngine.IdDir.TryGetValue(_id.value, out var d) ? d : "");
            if (!served && !FakeEngine.EditorServesAnyId) _status = LoadableStatus.Failed;
            else _status = FakeEngine.Assets.ContainsKey(_id.value) ? LoadableStatus.Loaded : LoadableStatus.Failed;
            _op?.TryComplete(Target, null);
        }
        void Begin()
        {
            if (_status != LoadableStatus.None) return;
            _status = LoadableStatus.Loading; FakeEngine.LoadCalls++;
            if (!_holdsRef) { _holdsRef = true; FakeEngine.Refs[_id.value] = FakeEngine.RefOf(_id.value) + 1; }   // at most one reference per instance
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
            _status = LoadableStatus.None; _op = null;
        }
        public override string ToString() => _id.ToString();
    }

    public static class ContentLoadManager
    {
        public static ContentDirectoryHandle RegisterContentDirectory(string path) { FakeEngine.Registered.Add(path); return new ContentDirectoryHandle { path = path }; }
        public static T[] GetRootAssets<T>(ContentDirectoryHandle h) where T : Object
        { var l = new List<T>(); foreach (var o in FakeEngine.Dirs[h.path]) if (o is T t) l.Add(t); return l.ToArray(); }
        public static ContentDirectoryHandle[] GetContentDirectories()
        { var l = new List<ContentDirectoryHandle>(); foreach (var p in FakeEngine.Registered) l.Add(new ContentDirectoryHandle { path = p }); return l.ToArray(); }
        public static void UnregisterContentDirectory(ContentDirectoryHandle h)
        {
            foreach (var kv in FakeEngine.IdDir) if (kv.Value == h.path && FakeEngine.RefOf(kv.Key) > 0) throw new InvalidOperationException($"engine: unregister with live Loadable {kv.Key}");
            foreach (var s in FakeEngine.LoadedScenes) if (FakeEngine.SceneDir.TryGetValue(s.loadableSceneId.guid, out var d) && d == h.path) throw new InvalidOperationException($"engine: unregister with open scene {s.name}");
            FakeEngine.Registered.Remove(h.path);
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
    public enum BuildTarget { NoTarget = -2, StandaloneOSX = 2, StandaloneWindows64 = 19, StandaloneLinux64 = 24 }
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
        public static string[] FindAssets(string f, string[] folders)
        {
            var hits = new List<string>();
            bool byType = f != null && f.StartsWith("t:");
            var term = byType ? f.Substring(2) : f;
            foreach (var kv in FakeEngine.MainAssetAt)
            {
                if (folders != null)
                {
                    bool inFolder = false;
                    foreach (var folder in folders) if (kv.Key.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal)) { inFolder = true; break; }   // a trailing separator finds the same assets (measured on 6000.6)
                    if (!inFolder) continue;
                }
                if (byType) { if (Matches(kv.Value.GetType(), term)) hits.Add(AssetPathToGUID(kv.Key)); }
                else if (string.IsNullOrEmpty(term) || kv.Key.Contains(term)) hits.Add(AssetPathToGUID(kv.Key));
            }
            return hits.ToArray();
        }
        static bool Matches(Type t, string name) { for (var x = t; x != null; x = x.BaseType) if (x.Name == name) return true; return false; }
        public static string GUIDToAssetPath(string g) => g != null && FakeEngine.PathOfGuid.TryGetValue(g, out var p) ? p : g;
        public static string AssetPathToGUID(string p) => p != null && FakeEngine.GuidOfPath.TryGetValue(p, out var g) ? g : p;
        public static bool IsValidFolder(string p) { if (p == null) return false; p = p.TrimEnd('/'); return p == "Assets" || FakeEngine.Folders.Contains(p); }   // Assets always exists; a trailing separator is accepted (measured on 6000.6)
        public static string GetAssetPath(Object o) => o != null && FakeEngine.PathOf.TryGetValue(o, out var p) ? p : "";
        public static bool IsSubAsset(Object o) => o != null && FakeEngine.IsSub.Contains(o);
        public static Object LoadMainAssetAtPath(string p) => p != null && FakeEngine.MainAssetAt.TryGetValue(p, out var o) ? o : null;
        public static Object[] LoadAllAssetRepresentationsAtPath(string p)
            => p != null && FakeEngine.SubAssetsAt.TryGetValue(p, out var l) ? l.ToArray() : new Object[0];
        public static void SaveAssets() { }
    }
    public static class EditorUtility
    {
        public static void SetDirty(Object o) { }
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
        public static Build.Reporting.BuildReport BuildContentDirectory(BuildContentDirectoryParameters p)
        {
            LastParameters = p;
            if (ThrowNextBuild) { ThrowNextBuild = false; throw new InvalidOperationException("engine: boom"); }
            var result = Build.Reporting.BuildResult.Succeeded;
            if (FailNextBuild) { FailNextBuild = false; result = Build.Reporting.BuildResult.Failed; }
            else if (!string.IsNullOrEmpty(p.outputPath))
            {
                System.IO.Directory.CreateDirectory(p.outputPath);
                if ((p.options & BuildContentOptions.UseArchive) != 0 || p.compression.Kind != UnityEngine.CompressionKindForStub.None)
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(p.outputPath, "content0.archive"), "archive");
                    System.IO.File.WriteAllText(System.IO.Path.Combine(p.outputPath, "BuildManifestHash.txt"), "deadbeef");
                }
                else
                {
                    var roots = new List<string>(); var loadables = new List<string>(); var files = new List<string>();
                    files.Add("{\"Index\":0,\"ID\":\"Library/unity default resources\",\"IsBuiltIn\":true,\"SerializedFileDependencies\":[]}");
                    int next = 1;
                    foreach (var root in p.rootAssetPaths ?? new string[0])
                    {
                        roots.Add("\"" + AssetDatabase.AssetPathToGUID(root) + "\"");
                        if (FakeEngine.MainAssetAt.TryGetValue(root, out var o) && o is xpTURN.ContentLink.ContentCatalog cat)
                            foreach (var e in cat.Entries)
                            {
                                var hash = Unity.Loading.LoadableObjectId.OidHex(e.id.value);
                                loadables.Add("{\"ObjectIdHash\":\"" + hash + "\",\"SerializedFile\":" + next + ",\"Identifier\":" + e.id.value + "}");
                                files.Add("{\"Index\":" + next + ",\"ID\":\"" + hash + ".cfid\",\"IsBuiltIn\":false,\"SerializedFileDependencies\":[0]" + (ManifestWithoutContentHash ? "" : ",\"ContentHash\":\"d" + hash.Substring(1) + "\"") + "}");
                                System.IO.File.WriteAllText(System.IO.Path.Combine(p.outputPath, "d" + hash.Substring(1) + ".cf"), "cf");   // matches the real engine: the on-disk name differs from the cfid
                                next++;
                            }
                    }
                    var json = "{\"Version\":1,\"BuildInfo\":{\"BuildName\":\"" + (p.name ?? "") + "\",\"BuiltWithTypeTrees\":" + ((p.options & BuildContentOptions.DisableWriteTypeTree) == 0 ? "true" : "false") + "},"
                             + "\"RootAssets\":[" + string.Join(",", roots) + "],\"Loadables\":[" + string.Join(",", loadables) + "],\"LoadableScenes\":[],\"SerializedFiles\":[" + string.Join(",", files) + "]}";
                    System.IO.File.WriteAllText(System.IO.Path.Combine(p.outputPath, "abc123.json"), json);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(p.outputPath, "BuildManifestHash.txt"), "abc123");
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
        public ArchiveStatus Status => id == 0 || M.Failed || !M.Mounted ? ArchiveStatus.Failed : FakeEngine.Frame >= M.ReadyFrame ? ArchiveStatus.Complete : ArchiveStatus.InProgress;
        public Unity.Jobs.JobHandle Unmount()
        {
            if (id == 0 || !M.Mounted) throw new InvalidOperationException("engine: unmount of an archive that is not mounted");
            M.Mounted = false; return default;
        }
        public string GetMountPath() => "ns:/" + M.Namespace + "/" + M.Prefix + "/";   // real-engine format: ns:/clprobe/clp/
    }
    public static class ArchiveFileInterface
    {
        public static ArchiveHandle MountAsync(Unity.Content.ContentNamespace ns, string filePath, string prefix)
        {
            if (!ns.IsValid) throw new ArgumentException("engine: invalid namespace");
            FakeEngine.Mounts.Add(new FakeEngine.Mount
            {
                Namespace = ns.GetName(), Path = filePath, Prefix = prefix, Mounted = true, ReadyFrame = FakeEngine.Frame + FakeEngine.MountFrames,
                Failed = !System.IO.File.Exists(filePath) || FakeEngine.FailMount.Contains(System.IO.Path.GetFileName(filePath)),
            });
            return new ArchiveHandle { id = FakeEngine.Mounts.Count };
        }
        public static ArchiveHandle[] GetMountedArchives(Unity.Content.ContentNamespace ns)
        {
            var l = new List<ArchiveHandle>();
            for (int i = 0; i < FakeEngine.Mounts.Count; ++i)
            {
                var m = FakeEngine.Mounts[i];
                if (m.Namespace == ns.GetName() && m.Mounted && !m.Failed) l.Add(new ArchiveHandle { id = i + 1 });
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
