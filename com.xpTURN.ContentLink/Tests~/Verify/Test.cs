using System;
using System.Reflection;
using System.Threading;
using Unity.Loading;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.SceneManagement;
using xpTURN.ContentLink;
using xpTURN.ContentLink.Editor;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

class IconView : MonoBehaviour { public ContentLinkSprite icon = new ContentLinkSprite(); }
class SiblingKiller : MonoBehaviour { public GameObject victim; void OnDestroy() { if (victim != null) Object.Destroy(victim); } }
class SceneHolder : MonoBehaviour { public ContentSceneLink level = new ContentSceneLink(); }

// Drawer fixture stand-in: the field shapes that AssetTypeOf has to unwrap
class LinkFields
{
    public ContentLinkSprite one = new ContentLinkSprite();
    public ContentLinkSprite[] many = new ContentLinkSprite[0];
    public System.Collections.Generic.List<ContentLinkGameObject> list = new();
    public ContentLink<Texture2D> direct = new ContentLink<Texture2D>();
    public ContentSceneLink scene = new ContentSceneLink();
    public int notALink;
}

static class Test
{
    static int fails;
    static void Check(bool c, string what) { Console.WriteLine((c ? "  PASS " : "  FAIL ") + what); if (!c) fails++; }
    static void Tick() { Time.frameCount++; PlayerLoop.RunPostLateUpdate(); }
    static void Init(Type t, string m) => t.GetMethod(m, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
    static int Ref(int id) => FakeEngine.RefOf(id);
    static Scene NewScene(int h, string n) => new Scene { handle = SceneHandle.FromRawData((ulong)h), name = n };
    static GameObject NewGO(string name, Scene s, bool active = true) => new GameObject(active) { name = name, scene = s };
    static T Run<T>(Awaitable<T> a) { FakeEngine.CompleteLoads(); return a.GetResult(); }
    static void RunV(Awaitable a) { FakeEngine.CompleteLoads(); a.GetResult(); }
    static void RunVNoComplete(Awaitable a) { if (!a.IsCompleted) throw new Exception("expected a synchronously completed Awaitable"); a.GetResult(); }
    static LoadableSceneId SceneId(int guid) => new LoadableSceneId { guid = guid };
    static ContentCatalog SceneCatalog(string dir, params (string name, int guid)[] scenes)
    {
        var cat = ScriptableObject.CreateInstance<ContentCatalog>();
        var list = new System.Collections.Generic.List<ContentCatalog.SceneEntry>();
        foreach (var (name, guid) in scenes) list.Add(new() { name = name, id = SceneId(guid) });
        cat.EditorSetScenes(list.ToArray());
        FakeEngine.Dirs[dir] = new ScriptableObject[] { cat };
        return cat;
    }
    static int Warn(Action a) { int w = Debug.Warnings; a(); return Debug.Warnings - w; }

    const int CAT = 1, SWORD = 2, SHIELD = 3, ICON0 = 100;
    const int SC_ONE = 1001, SC_TWO = 1002, SC_ARENA = 1003, SC_LOOSE = 1099;

    // Post-processor test doubles: record the call order and the shape of the result they were given
    static readonly System.Collections.Generic.List<string> PpCalls = new();
    class PpA : IContentBuildPostProcess { public void OnBuilt(ContentBuildResult r) => PpCalls.Add("A:" + r.Files.Length + (r.Succeeded ? ":ok" : ":fail")); }
    class PpB : IContentBuildPostProcess { public void OnBuilt(ContentBuildResult r) => PpCalls.Add("B:" + r.Files.Length + (r.Succeeded ? ":ok" : ":fail")); }
    class PpThrow : IContentBuildPostProcess { public void OnBuilt(ContentBuildResult r) => throw new InvalidOperationException("boom"); }

    // Partitioners that break one rule each — what the packer's validator and purity check must catch
    static bool IsManifest(ArchivePartitionInput i, string f) { foreach (var m in i.ManifestFiles) if (m == f) return true; return false; }
    static System.Collections.Generic.List<string> Content(ArchivePartitionInput i) { var l = new System.Collections.Generic.List<string>(); foreach (var f in i.Files) if (!IsManifest(i, f)) l.Add(f); l.Sort(StringComparer.Ordinal); return l; }
    static ArchiveGroup Grp(string name, params string[] files) => new ArchiveGroup(name, files);
    static ArchiveGroup Grp(string name, System.Collections.Generic.List<string> files) => new ArchiveGroup(name, files);
    class PartDrop : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) { var c = Content(i); c.RemoveAt(0); return new[] { Grp("T.m.archive", i.ManifestFiles[0], i.ManifestFiles[1]), Grp("T.c.archive", c) }; } }
    class PartDup : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) { var c = Content(i); return new[] { Grp("T.m.archive", i.ManifestFiles[0], i.ManifestFiles[1]), Grp("T.c.archive", c), Grp("T.d.archive", c) }; } }
    class PartEmpty : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) => new[] { Grp("T.m.archive", i.ManifestFiles[0], i.ManifestFiles[1]), Grp("T.c.archive", Content(i)), Grp("T.e.archive") }; }
    class PartCase : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) => new[] { Grp("T.X.archive", i.ManifestFiles[0], i.ManifestFiles[1]), Grp("t.x.archive", Content(i)) }; }
    class PartSlash : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) => new[] { Grp("sub/m.archive", i.ManifestFiles[0], i.ManifestFiles[1]), Grp("T.c.archive", Content(i)) }; }
    class PartOrder : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) { var c = new System.Collections.Generic.List<string>(); foreach (var f in i.Files) if (!IsManifest(i, f)) c.Add(f); var first = new[] { c[0] }; c.RemoveAt(0); return new[] { Grp("T.m.archive", i.ManifestFiles[0], i.ManifestFiles[1]), Grp("T.first.archive", first), Grp("T.rest.archive", c) }; } }
    class PartSplitManifest : IContentArchivePartitioner { public System.Collections.Generic.IReadOnlyList<ArchiveGroup> Partition(ArchivePartitionInput i) { var c = Content(i); c.Insert(0, i.ManifestFiles[1]); return new[] { Grp("T.h.archive", i.ManifestFiles[0]), Grp("T.c.archive", c) }; } }
    static bool SameBytes(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; ++i) if (a[i] != b[i]) return false; return true; }
    static bool Has(System.Collections.Generic.IReadOnlyList<Type> list, Type t) { foreach (var x in list) if (x == t) return true; return false; }
    static bool Any(System.Collections.Generic.IReadOnlyList<string> list, string part) { foreach (var x in list) if (x.Contains(part)) return true; return false; }

    static void Main()
    {
        var asm = typeof(ContentService).Assembly;
        Init(typeof(ContentService), "ResetStatics");
        Init(typeof(ContentScope), "ResetStatics");
        Init(asm.GetType("xpTURN.ContentLink.ContentPlayerLoop"), "Install");

        var catPrefab = new GameObject { name = "Cat" };
        FakeEngine.Assets[CAT] = catPrefab;
        FakeEngine.Assets[SWORD] = new Sprite { name = "sword" };
        FakeEngine.Assets[SHIELD] = new Sprite { name = "shield" };
        var entries = new System.Collections.Generic.List<ContentCatalog.Entry> {
            new() { name = "Prefabs/Cat", id = new LoadableObjectId { value = CAT } },
            new() { name = "Icons/sword", id = new LoadableObjectId { value = SWORD } },
            new() { name = "Icons/shield", id = new LoadableObjectId { value = SHIELD } },
        };
        for (int i = 0; i < 20; ++i)
        {
            FakeEngine.Assets[ICON0 + i] = new Sprite { name = "item" + i };
            entries.Add(new() { name = "Items/" + i, id = new LoadableObjectId { value = ICON0 + i } });
        }
        foreach (var kv in FakeEngine.Assets) FakeEngine.IdDir[kv.Key] = "Main";
        var catalog = ScriptableObject.CreateInstance<ContentCatalog>();
        catalog.EditorSetEntries(entries.ToArray());
        // Scene fixtures: One and Two are in the catalog; Loose is only in the directory (not indexed)
        FakeEngine.SceneDir[SC_ONE] = "Main"; FakeEngine.SceneNames[SC_ONE] = "One";
        FakeEngine.SceneDir[SC_TWO] = "Main"; FakeEngine.SceneNames[SC_TWO] = "Two";
        FakeEngine.SceneDir[SC_LOOSE] = "Main"; FakeEngine.SceneNames[SC_LOOSE] = "Loose";
        FakeEngine.SceneDir[SC_ARENA] = "Arena"; FakeEngine.SceneNames[SC_ARENA] = "Arena";
        catalog.EditorSetScenes(new ContentCatalog.SceneEntry[] { new() { name = "Scenes/One", id = SceneId(SC_ONE) }, new() { name = "Scenes/Two", id = SceneId(SC_TWO) } });
        FakeEngine.Dirs["Main"] = new ScriptableObject[] { catalog };
        int dir = ContentService.RegisterDirectory("Main");

        var sceneA = NewScene(1, "Lobby"); var sceneB = NewScene(2, "Field");
        SceneManager.Active = sceneA;

        Console.WriteLine("1) component usage: no Release code needed");
        var view = NewGO("View", sceneA).AddComponent<IconView>();
        var spr = Run(view.Scope().LoadAsync<Sprite>("Icons/sword"));
        Check(spr != null && spr.name == "sword" && Ref(SWORD) == 1, "load");
        Object.Destroy(view.gameObject); Tick();
        Check(Ref(SWORD) == 0, "GameObject destroyed → released at end of frame");

        Console.WriteLine("1b) the engine throws from a load (F-11 · injected: the stub's next load throws after it has started)");
        {
            const int X11 = SWORD + 900;
            FakeEngine.Assets[X11] = new Sprite { name = "x11" }; FakeEngine.IdDir[X11] = "F11";
            var cat11 = ScriptableObject.CreateInstance<ContentCatalog>();
            cat11.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "F11/x", id = new LoadableObjectId { value = X11 } } });
            FakeEngine.Dirs["F11"] = new ScriptableObject[] { cat11 };
            int dir11 = ContentService.RegisterDirectory("F11");
            // recovery for the current code, whose leak cannot be undone through the API: a session reset, both directories back, the fake engine's leaked reference dropped
            void Recover11()
            {
                if (ContentService.LiveAssetCount == 0 && Ref(X11) == 0 && FakeEngine.Registered.Contains("F11")) return;
                Init(typeof(ContentService), "ResetStatics");
                Debug.Verbose = false; ContentService.RegisterDirectory("Main"); dir11 = ContentService.RegisterDirectory("F11"); Debug.Verbose = true;
                FakeEngine.Refs[X11] = 0;
            }

            // raw sync acquire
            FakeEngine.ThrowOnLoad = true; Debug.Verbose = false; int errs11 = Debug.Errors; string ex11 = ""; AssetHandle<Sprite> hs11 = default;
            try { hs11 = ContentService.Acquire<Sprite>("F11/x"); } catch (Exception e) { ex11 = e.GetType().Name; }
            FakeEngine.ThrowOnLoad = false; Debug.Verbose = true;
            Check(ex11 == "" && !hs11.IsValid, $"① sync Acquire: the exception is caught and the handle is invalid — like the async path (actual: exception={(ex11 == "" ? "none" : ex11)} valid={hs11.IsValid})");
            Check(Debug.Errors == errs11 + 2 && Debug.LastError.Contains("failed to load 'F11/x'"), $"② the exception is logged and the load is reported failed (actual: errors=+{Debug.Errors - errs11} last=\"{Debug.LastError}\")");
            Tick();
            Check(ContentService.LiveAssetCount == 0 && Ref(X11) == 0, $"③ nothing stays held: no live slot, the engine reference returned (actual: live={ContentService.LiveAssetCount} ref={Ref(X11)})");
            Debug.Verbose = false; bool unreg11 = ContentService.UnregisterDirectory(dir11, force: true); Debug.Verbose = true;
            Check(unreg11, $"④ the directory unregisters after the failure, force or not — nothing is stuck in Loading (actual: force-unregister={unreg11} last=\"{Debug.LastError}\")");
            if (unreg11) dir11 = ContentService.RegisterDirectory("F11"); else Recover11();

            // scope sync load
            var scope11 = ContentScope.Root.CreateChild("F11");
            FakeEngine.ThrowOnLoad = true; Debug.Verbose = false; string exScope11 = ""; Sprite got11 = null;
            try { got11 = scope11.Load<Sprite>("F11/x"); } catch (Exception e) { exScope11 = e.GetType().Name; }
            FakeEngine.ThrowOnLoad = false; Debug.Verbose = true; Tick();
            Check(exScope11 == "" && got11 == null && scope11.Count == 0 && ContentService.LiveAssetCount == 0, $"⑤ scope.Load: null, nothing held, no exception (actual: exception={(exScope11 == "" ? "none" : exScope11)} count={scope11.Count} live={ContentService.LiveAssetCount})");
            scope11.Dispose(); Tick(); Recover11();

            // ContentLink sync load: not stuck, SetName and a reload work afterwards
            var v11 = NewGO("F11View", sceneA).AddComponent<IconView>();
            v11.icon.SetName("F11/x");
            FakeEngine.ThrowOnLoad = true; Debug.Verbose = false; string exLink11 = ""; Sprite lg11 = null;
            try { lg11 = v11.icon.Load(v11); } catch (Exception e) { exLink11 = e.GetType().Name; }
            FakeEngine.ThrowOnLoad = false;
            bool notStuck11 = !v11.icon.IsLoading && !v11.icon.IsValid;
            int eRename = Debug.Errors; v11.icon.SetName("Icons/sword"); bool renameOk11 = Debug.Errors == eRename;
            Sprite reload11 = null; try { reload11 = v11.icon.Load(v11); } catch (Exception) { }
            Debug.Verbose = true;
            Check(exLink11 == "" && lg11 == null && notStuck11 && renameOk11 && reload11 != null && reload11.name == "sword", $"⑥ ContentLink.Load: null, not stuck, SetName and a reload work (actual: exception={(exLink11 == "" ? "none" : exLink11)} loading={v11.icon.IsLoading} renameRejected={!renameOk11} reload={(reload11 != null ? reload11.name : "null")})");
            v11.icon.Release(); Object.Destroy(v11.gameObject); Tick(); Recover11();

            // async (regression): the same injection is caught by StartLoad — null, nothing held
            var scopeA11 = ContentScope.Root.CreateChild("F11Async");
            FakeEngine.ThrowOnLoad = true; Debug.Verbose = false; string exAsync11 = ""; Sprite gotA11 = null;
            try { gotA11 = Run(scopeA11.LoadAsync<Sprite>("F11/x")); } catch (Exception e) { exAsync11 = e.GetType().Name; }
            FakeEngine.ThrowOnLoad = false; Debug.Verbose = true; Tick();
            Check(exAsync11 == "" && gotA11 == null && ContentService.LiveAssetCount == 0 && Ref(X11) == 0, $"⑦ async: the same exception is caught, the load fails cleanly, nothing held (as before) (actual: exception={(exAsync11 == "" ? "none" : exAsync11)} live={ContentService.LiveAssetCount})");
            scopeA11.Dispose(); Tick(); Recover11();

            Debug.Verbose = false; ContentService.UnregisterDirectory(dir11, force: true); Debug.Verbose = true;
            FakeEngine.Dirs.Remove("F11"); FakeEngine.Assets.Remove(X11); FakeEngine.IdDir.Remove(X11); FakeEngine.Refs.Remove(X11); FakeEngine.ThrowOnLoad = false;
        }

        Console.WriteLine("2) key dedupe within a scope");
        var s2 = ContentScope.ForScene(sceneA).CreateChild("Dedupe");
        var p1 = s2.LoadAsync<Sprite>("Icons/sword"); var p2 = s2.LoadAsync<Sprite>("Icons/sword");
        FakeEngine.CompleteLoads(); var r1 = p1.GetResult(); var r2 = p2.GetResult();
        Check(r1 != null && r2 != null && ReferenceEquals(r1, r2), $"two concurrent loads both get the same asset (r1={(r1 != null ? r1.name : "null")} r2={(r2 != null ? r2.name : "null")})");
        {   // synchronous Load while an async load is in progress
            var sx = ContentScope.ForScene(sceneA).CreateChild("DedupeSync");
            var pa = sx.LoadAsync<Sprite>("Icons/shield");
            var rs = sx.Load<Sprite>("Icons/shield");
            FakeEngine.CompleteLoads(); var ra = pa.GetResult();
            Check(rs != null && ra != null, $"sync Load during an async load: both get the asset (sync={(rs != null ? rs.name : "null")} async={(ra != null ? ra.name : "null")})");
            sx.Dispose(); Tick();
        }
        for (int i = 0; i < 100; ++i) s2.Load<Sprite>("Icons/sword");   // the mistake of loading repeatedly from Update
        Check(s2.Count == 1 && Ref(SWORD) == 1, $"concurrent/repeated loads still hold a single lease (count={s2.Count})");
        s2.Dispose(); Tick();
        Check(Ref(SWORD) == 0, "released by Dispose");

        Console.WriteLine("3) explicit scope: a proper Dispose gives no warning / a missed one is reclaimed by the scene-unload cascade with a warning");
        var stage = ContentScope.ForScene(sceneB).CreateChild("Stage");
        stage.Load<Sprite>("Icons/sword"); stage.Load<Sprite>("Icons/shield");
        Check(Warn(() => stage.Dispose()) == 0, "Dispose → no warning");
        var forgotten = ContentScope.ForScene(sceneB).CreateChild("ForgottenPopup");
        forgotten.Load<Sprite>("Icons/shield");
        Debug.Verbose = true;
        int w3 = Warn(() => SceneManager.UnloadScene(sceneB)); Tick();
        Check(w3 == 1 && forgotten.IsDisposed && Ref(SHIELD) == 0, "missed Dispose → reclaimed on scene unload + one leak warning");

        Console.WriteLine("3b) the scope tree's child list: removal in any order keeps it consistent (O-1 · Plan-O1-ChildIndex)");
        {
            var fIdx3 = typeof(ContentScope).GetField("m_IndexInParent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var fKids3 = typeof(ContentScope).GetField("m_Children", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            // ⑤ the index invariant, read by reflection: every listed child sits where its index says (skipped while the field does not exist yet)
            bool Invariant3(ContentScope parent, out string why)
            {
                why = "";
                if (fIdx3 == null) { why = "(no index field yet: skipped)"; return true; }
                var kids = (System.Collections.Generic.List<ContentScope>)fKids3.GetValue(parent);
                for (int i = 0; i < kids.Count; ++i)
                {
                    if ((int)fIdx3.GetValue(kids[i]) != i) { why = $"child {i} says {(int)fIdx3.GetValue(kids[i])}"; return false; }
                    if (!ReferenceEquals(kids[i].Parent, parent)) { why = $"child {i} has another parent"; return false; }
                }
                return true;
            }
            var rnd3 = new Random(11);
            var parent3 = ContentScope.Root.CreateChild("O1");
            var kids3 = new System.Collections.Generic.List<ContentScope>(); for (int i = 0; i < 1000; ++i) kids3.Add(parent3.CreateChild("k" + i));
            var alive3 = new System.Collections.Generic.HashSet<ContentScope>(kids3);
            for (int n = 0; n < 300; ++n) { var victim = kids3[rnd3.Next(kids3.Count)]; if (!alive3.Contains(victim)) continue; victim.Dispose(); alive3.Remove(victim); }
            bool set3 = parent3.Children.Count == alive3.Count; foreach (var c in parent3.Children) set3 &= alive3.Contains(c) && !c.IsDisposed;
            Check(set3, $"① after 300 random middle removals Children is exactly the live set (actual: count={parent3.Children.Count} live={alive3.Count})");
            var rootKids3 = (System.Collections.Generic.List<ContentScope>)fKids3.GetValue(ContentScope.Root);
            int at3 = fIdx3 == null ? rootKids3.IndexOf(parent3) : (int)fIdx3.GetValue(parent3);
            Check(at3 >= 0 && at3 < rootKids3.Count && ReferenceEquals(rootKids3[at3], parent3), $"② the parent sits where Root's list says (actual: at={at3} rootKids={rootKids3.Count})");
            Debug.Verbose = false; parent3.Dispose(); Debug.Verbose = true;
            bool allGone3 = true; foreach (var c in kids3) allGone3 &= c.IsDisposed;
            bool invRoot3 = Invariant3(ContentScope.Root, out var whyR3);
            Check(allGone3 && parent3.Children.Count == 0 && (fIdx3 == null || (int)fIdx3.GetValue(parent3) == -1) && invRoot3, $"③ the cascade disposes every remaining child; the parent leaves Root; Root stays consistent (actual: {whyR3})");

            // ④ / ⑤ the scene handoff (the §5 move scenario) keeps both lists consistent
            var sc3a = NewScene(31, "O1A"); var sc3b = NewScene(32, "O1B");
            var goA3 = NewGO("A", sc3a); goA3.Scope().Load<Sprite>("Icons/sword");
            var goB3 = NewGO("B", sc3a); var lb3 = goB3.Scope().Load<Sprite>("Icons/shield");
            var goC3 = NewGO("C", sc3a); goC3.Scope().Load<Sprite>("Icons/sword");
            var scopeA3 = goA3.Scope().Parent;
            SceneManager.MoveGameObjectToScene(goB3, sc3b);
            SceneManager.UnloadScene(sc3a); Tick();
            var scopeB3 = goB3.Scope().Parent;
            bool invB3 = Invariant3(scopeB3, out var whyB3);
            Check(lb3 != null && scopeB3.Name == "O1B" && scopeB3.Children.Count == 1 && ReferenceEquals(scopeB3.Children[0], goB3.Scope()) && invB3 && scopeA3.IsDisposed && Ref(SHIELD) == 1 && Ref(SWORD) == 0,
                  $"④ handoff: the moved scope is the destination scene's only child, the old scene scope is gone, its assets released, the mover's kept (actual: {whyB3} kids={scopeB3.Children.Count} shield={Ref(SHIELD)} sword={Ref(SWORD)})");
            SceneManager.UnloadScene(sc3b); Tick();
            bool invRoot3b = Invariant3(ContentScope.Root, out var whyR3b);
            Check(Ref(SHIELD) == 0 && invRoot3b, $"⑤ the destination unload releases the mover; the index invariant holds on Root (actual: shield={Ref(SHIELD)} {whyR3b})");

            // ⑥ OnDestroy that destroys siblings during a scene unload
            var sc3c = NewScene(33, "O1C");
            var objs3 = new GameObject[200];
            for (int i = 0; i < 200; ++i) { objs3[i] = NewGO("o" + i, sc3c); objs3[i].Scope().Load<Sprite>("Icons/sword"); }
            for (int i = 0; i < 200; i += 3) objs3[i].AddComponent<SiblingKiller>().victim = objs3[(i + 7) % 200];
            var scopeC3 = objs3[0].Scope().Parent;
            Debug.Verbose = false; int errs3 = Debug.Errors; int warns3 = Debug.Warnings;
            SceneManager.UnloadScene(sc3c); Tick();
            Debug.Verbose = true;
            bool invRoot3c = Invariant3(ContentScope.Root, out var whyR3c);
            Check(scopeC3.IsDisposed && Ref(SWORD) == 0 && Debug.Errors == errs3 && Debug.Warnings == warns3 && invRoot3c, $"⑥ OnDestroy destroying siblings during the unload: everything disposed, assets released, no log, Root consistent (actual: ref={Ref(SWORD)} errors=+{Debug.Errors - errs3} warnings=+{Debug.Warnings - warns3} {whyR3c})");

            // ⑦ / ⑧ a consumer that casts Children back to List and mutates it: still no exception, and the parent's cascade clears what was left (D5)
            var parentX3 = ContentScope.Root.CreateChild("O1X");
            var kidsX3 = new System.Collections.Generic.List<ContentScope>(); for (int i = 0; i < 50; ++i) kidsX3.Add(parentX3.CreateChild("x" + i));
            var raw3 = (System.Collections.Generic.List<ContentScope>)parentX3.Children;
            raw3.RemoveAt(10); raw3.Insert(3, kidsX3[40]);
            string exX3 = "";
            Debug.Verbose = false; int warnsX3 = Debug.Warnings;
            try
            {
                var order3 = new System.Collections.Generic.List<ContentScope>(kidsX3); for (int i = order3.Count - 1; i > 0; --i) { int j = rnd3.Next(i + 1); (order3[i], order3[j]) = (order3[j], order3[i]); }
                foreach (var c in order3) c.Dispose();
            }
            catch (Exception e) { exX3 = e.GetType().Name + ": " + e.Message; }
            Debug.Verbose = true;
            bool allDisposedX3 = true; foreach (var c in kidsX3) allDisposedX3 &= c.IsDisposed;
            Check(exX3 == "" && allDisposedX3 && parentX3.Children.Count <= 1, $"⑦ a Children list changed behind the scope's back: disposing every child in random order throws nothing and disposes all (actual: ex={exX3} left={parentX3.Children.Count} warnings=+{Debug.Warnings - warnsX3})");
            Debug.Verbose = false; parentX3.Dispose(); Debug.Verbose = true;
            Check(parentX3.IsDisposed && parentX3.Children.Count == 0, $"⑧ the parent's own cascade clears whatever was left behind (actual: left={parentX3.Children.Count})");
        }

        Console.WriteLine("4) object that was never activated (no OnDestroy)");
        var inactive = NewGO("PooledTemplate", sceneB, active: false);
        inactive.Scope().Load<Sprite>("Icons/sword");
        Object.Destroy(inactive); Tick();
        Check(Ref(SWORD) == 1, "no OnDestroy arrives, so it is still held (bounded over-holding)");
        SceneManager.UnloadScene(sceneB); Tick();
        Check(Ref(SWORD) == 0, "the scene-unload cascade reclaims it");

        Console.WriteLine("5) moving to another scene / DontDestroyOnLoad");
        var mover = NewGO("Mover", sceneA); mover.Scope().Load<Sprite>("Icons/sword");
        var ddol = NewGO("Hud", sceneA); ddol.Scope().Load<Sprite>("Icons/shield");
        SceneManager.MoveGameObjectToScene(mover, sceneB);
        Object.DontDestroyOnLoad(ddol);
        SceneManager.UnloadScene(sceneA); Tick();
        Check(Ref(SWORD) == 1 && Ref(SHIELD) == 1, "assets of surviving objects are kept when the original scene unloads (no pink materials)");
        Check(mover.Scope().Parent.Name == "Field" && ddol.Scope().Parent.Name == "DontDestroyOnLoad", "re-parented to the current scene's scope");
        SceneManager.UnloadScene(sceneB); Tick();
        Check(Ref(SWORD) == 0 && Ref(SHIELD) == 1, "released when the destination scene unloads; DDOL is kept");
        Object.Destroy(ddol); Tick();
        Check(Ref(SHIELD) == 0, "released when the DDOL object is destroyed");

        Console.WriteLine("6) scope disposed while loading");
        var popup = ContentScope.ForScene(sceneA).CreateChild("Popup");
        var pl = popup.LoadAsync<Sprite>("Icons/sword");
        popup.Dispose();
        bool oce = false; try { pl.GetResult(); } catch (OperationCanceledException) { oce = true; }
        Check(oce, "OperationCanceledException");
        FakeEngine.CompleteLoads(); Tick();
        Check(Ref(SWORD) == 0, "released once the engine load completes, no leak");

        Console.WriteLine("7) ContentLink: Release is optional");
        var v7 = NewGO("Equip", sceneA).AddComponent<IconView>();
        v7.icon.SetName("Icons/sword");
        Run(v7.icon.LoadAsync(v7));
        v7.icon.Release(); v7.icon.SetName("Icons/shield"); Run(v7.icon.LoadAsync(v7));   // swapping equipment: early release
        Tick();
        Check(Ref(SWORD) == 0 && Ref(SHIELD) == 1, "the previous asset is returned immediately on swap");
        Object.Destroy(v7.gameObject); Tick();
        Check(Ref(SHIELD) == 0 && !v7.icon.IsValid, "reclaimed when the owner is destroyed even without Release");
        Debug.Verbose = false;
        Check(Warn(() => v7.icon.Release()) == 0, "calling Release after the scope is gone is safe");
        Debug.Verbose = true;

        Console.WriteLine("7b) ContentLink: swap while loading (the README order)");
        foreach (bool newerFirst in new[] { false, true })
        {
            string tag = newerFirst ? "new load completes first" : "old load completes first";
            var v = NewGO("EquipFast", sceneA).AddComponent<IconView>();
            v.icon.SetName("Icons/sword");
            var first = v.icon.LoadAsync(v);
            Debug.Verbose = false; int e0 = Debug.Errors;
            v.icon.Release(); v.icon.SetName("Icons/shield");
            var second = v.icon.LoadAsync(v);
            int errs = Debug.Errors - e0; Debug.Verbose = true;
            Check(errs == 0 && v.icon.Name == "Icons/shield" && v.icon.IsLoading, $"[{tag}] Release → SetName → LoadAsync while loading is not rejected (errors={errs} name={v.icon.Name} loading={v.icon.IsLoading})");
            var pending = new System.Collections.Generic.List<Action>(FakeEngine.PendingLoads); FakeEngine.PendingLoads.Clear();
            if (newerFirst) pending.Reverse();
            bool midOk = true;
            for (int i = 0; i < pending.Count; ++i)
            {
                pending[i]();
                if (i == 0 && !newerFirst) midOk = v.icon.IsLoading && !v.icon.IsValid;   // only the old load has finished: the new one is still in progress
            }
            var oldR = first.GetResult(); var newR = second.GetResult();
            Tick();
            Check(pending.Count == 2 && midOk, $"[{tag}] completion of the old load does not touch the new load's state (pending={pending.Count} mid={midOk})");
            Check(oldR == null && newR != null && newR.name == "shield" && ReferenceEquals(v.icon.Asset, newR), $"[{tag}] old call returns null, new call returns the new asset (first={(oldR != null ? oldR.name : "null")} second={(newR != null ? newR.name : "null")} asset={(v.icon.Asset != null ? v.icon.Asset.name : "null")})");
            Check(Ref(SWORD) == 0 && Ref(SHIELD) == 1, $"[{tag}] old asset returned, only the new one held (ref sword={Ref(SWORD)} shield={Ref(SHIELD)})");
            Object.Destroy(v.gameObject); Tick();
            Check(Ref(SHIELD) == 0 && !v.icon.IsValid, $"[{tag}] reclaimed when the owner is destroyed");
        }

        Console.WriteLine("7c) ContentLink: Release alone while loading / reload of the same name");
        {
            var v = NewGO("ReleaseOnly", sceneA).AddComponent<IconView>();
            v.icon.SetName("Icons/sword");
            var p = v.icon.LoadAsync(v);
            v.icon.Release();
            bool loadingAfterRelease = v.icon.IsLoading;
            FakeEngine.CompleteLoads(); var r = p.GetResult(); Tick();
            Check(r == null && !v.icon.IsValid && Ref(SWORD) == 0, $"Release alone: result is null, nothing held (r={(r != null ? r.name : "null")} valid={v.icon.IsValid} ref={Ref(SWORD)} loadingAfterRelease={loadingAfterRelease})");
            Object.Destroy(v.gameObject); Tick();
        }
        {
            var v = NewGO("SameName", sceneA).AddComponent<IconView>();
            v.icon.SetName("Icons/sword");
            var p1x = v.icon.LoadAsync(v);
            Debug.Verbose = false; int e0 = Debug.Errors;
            v.icon.Release();
            var p2x = v.icon.LoadAsync(v);
            int errs = Debug.Errors - e0; Debug.Verbose = true;
            FakeEngine.CompleteLoads(); var a = p1x.GetResult(); var b = p2x.GetResult(); Tick();
            Check(errs == 0 && a == null && b != null && v.icon.IsValid && Ref(SWORD) == 1, $"same-name reload: only the new call gets the asset, ref count 1 (errors={errs} a={(a != null ? a.name : "null")} b={(b != null ? b.name : "null")} ref={Ref(SWORD)})");
            Object.Destroy(v.gameObject); Tick();
            Check(Ref(SWORD) == 0, "same-name reload: reclaimed when the owner is destroyed");
        }

        Console.WriteLine("8) detecting and handling over-holding in a scroll list");
        var screen = ContentScope.ForScene(sceneA).CreateChild("Inventory");
        screen.WarnThreshold = 8;
        int w8 = Warn(() => { for (int i = 0; i < 20; ++i) screen.Load<Sprite>("Items/" + i); });
        Check(w8 == 1 && screen.Count == 20, "piling loads into one scope without cell reuse warns about over-holding (once)");
        screen.Dispose(); Tick();
        var screen2 = ContentScope.ForScene(sceneA).CreateChild("Inventory2");
        var cells = new ContentScope[6];
        for (int i = 0; i < 20; ++i)   // reuse 6 cells: each cell swaps its child scope
        {
            ref var cell = ref cells[i % 6];
            cell?.Dispose();
            cell = screen2.CreateChild("Cell" + (i % 6));
            cell.Load<Sprite>("Items/" + i);
            Tick();
        }
        int live = 0; for (int i = 0; i < 20; ++i) live += Ref(ICON0 + i);
        Check(live == 6, $"swapping cell scopes caps concurrent holds at 6 (live={live})");
        Check(Warn(() => screen2.Dispose()) == 0, "parent Dispose also cleans up the cell scopes (normal path → no warning)");
        Tick(); live = 0; for (int i = 0; i < 20; ++i) live += Ref(ICON0 + i);
        Check(live == 0, "all cell assets released");
        var owner = NewGO("PopupOwner", sceneA); var child = owner.Scope().CreateChild("Popup"); child.Load<Sprite>("Icons/sword");
        Check(Warn(() => Object.Destroy(owner)) == 0 && child.IsDisposed, "a child scope ended by destroying its owning GameObject also gives no warning");
        Tick();

        Console.WriteLine("9) transfer between scopes: ShareTo / Adopt");
        Tick();
        var st = ContentScope.ForScene(sceneA).CreateChild("Stage2");
        var ui = ContentScope.ForScene(sceneA).CreateChild("Hud2");
        var sword = st.Load<Sprite>("Icons/sword");
        st.ShareTo(ui, "Icons/sword");
        var shield = st.Load<Sprite>("Icons/shield");
        Check(ui.Adopt(shield), "Adopt(asset object)");
        st.Dispose(); Tick();
        Check(Ref(SWORD) == 1 && Ref(SHIELD) == 1, "the receiver still holds after the source scope is disposed");
        ui.Dispose(); Tick();
        Check(Ref(SWORD) == 0 && Ref(SHIELD) == 0, "released when the receiver is disposed");

        Console.WriteLine("10) InstantiateAsync + inheritance by instance clones");
        var req = ContentScope.ForScene(sceneA).CreateChild("Spawner");
        var inst = Run(req.InstantiateAsync("Prefabs/Cat"));
        req.Dispose(); Tick();
        Check(inst != null && Ref(CAT) == 1, "the instance keeps holding the prefab after the requesting scope ends");
        var clone = Object.Instantiate(inst);
        Object.Destroy(inst); Tick();
        Check(Ref(CAT) == 1 && clone.Scope().Contains("Prefabs/Cat"), "an Object.Instantiate clone inherits the lease");
        Object.Destroy(clone); Tick();
        Check(Ref(CAT) == 0, "released when the clone is destroyed");

        Console.WriteLine("10b) loading by id and directory unregistration");
        {
            const int OTHER = 50;
            FakeEngine.Assets[OTHER] = new Sprite { name = "other" }; FakeEngine.IdDir[OTHER] = "Other";
            var otherCat = ScriptableObject.CreateInstance<ContentCatalog>();
            otherCat.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Other/x", id = new LoadableObjectId { value = OTHER } } });
            FakeEngine.Dirs["Other"] = new ScriptableObject[] { otherCat };

            var byId = ContentScope.ForScene(sceneA).CreateChild("ById");
            var hid = Run(ContentService.AcquireAsync<Sprite>(new LoadableObjectId { value = SWORD }));
            byId.Attach(hid);
            Debug.Verbose = false;
            bool ownRefused; try { ownRefused = !ContentService.UnregisterDirectory(dir); } catch (InvalidOperationException) { ownRefused = false; }
            Check(ownRefused && FakeEngine.Registered.Contains("Main") && hid.IsValid, "an asset loaded by id blocks unregistering its own directory");
            int dirOther = ContentService.RegisterDirectory("Other");
            Check(ContentService.UnregisterDirectory(dirOther) && hid.IsValid && Ref(SWORD) == 1, "an asset loaded by id does not block unregistering an unrelated directory");
            dirOther = ContentService.RegisterDirectory("Other");
            byId.Load<Sprite>("Other/x");
            bool forced = ContentService.UnregisterDirectory(dirOther, force: true);
            Check(forced && Ref(OTHER) == 0 && hid.IsValid && Ref(SWORD) == 1, $"force releases only that directory's assets; the id-loaded asset stays (ok={forced} ref(other)={Ref(OTHER)} valid={hid.IsValid} ref(sword)={Ref(SWORD)})");
            Check(ContentService.TryGetLoadableObjectId("Icons/sword", out var swordId) && swordId.value == SWORD
                  && !ContentService.TryGetLoadableObjectId("Nope/x", out _) && !ContentService.TryGetLoadableObjectId("Other/x", out _),
                  "name → LoadableObjectId lookup: a registered name matches; an unknown name or one from an unregistered directory is false");
            Debug.Verbose = true;
            byId.Dispose(); Tick();
            Check(Ref(SWORD) == 0 && !FakeEngine.Registered.Contains("Other"), "id asset released when the scope ends; Other stays unregistered");
        }

        Console.WriteLine("10c) builder-rule catalog: path name = main texture, [sub] = sprite");
        {
            const int TEX = 60, TEXSPR = 61;
            FakeEngine.Assets[TEX] = new Texture2D { name = "tex" }; FakeEngine.IdDir[TEX] = "Atlas";
            FakeEngine.Assets[TEXSPR] = new Sprite { name = "tex" }; FakeEngine.IdDir[TEXSPR] = "Atlas";
            var atlasCat = ScriptableObject.CreateInstance<ContentCatalog>();
            atlasCat.EditorSetEntries(new ContentCatalog.Entry[] {
                new() { name = "Atlas/tex", id = new LoadableObjectId { value = TEX } },
                new() { name = "Atlas/tex[tex]", id = new LoadableObjectId { value = TEXSPR } },
            });
            FakeEngine.Dirs["Atlas"] = new ScriptableObject[] { atlasCat };
            int dirAtlas = ContentService.RegisterDirectory("Atlas");

            var ok = ContentScope.ForScene(sceneA).CreateChild("AtlasOk");
            var atlasSpr = Run(ok.LoadAsync<Sprite>("Atlas/tex[tex]"));
            var atlasTex = Run(ok.LoadAsync<Texture2D>("Atlas/tex"));
            Check(atlasSpr != null && atlasTex != null, "[sub] name loads as Sprite, path name as Texture2D");
            ok.Dispose(); Tick();

            Debug.Verbose = false;
            var bad = ContentScope.ForScene(sceneA).CreateChild("AtlasBad");
            int e0 = Debug.Errors;
            var wrong = Run(bad.LoadAsync<Sprite>("Atlas/tex"));
            int e1 = Debug.Errors - e0;
            int countAfter = bad.Count;
            Tick();
            Check(wrong == null && e1 == 1 && countAfter == 0 && Ref(TEX) == 0, $"type mismatch: null + 1 error, and the scope holds nothing (errors={e1} count={countAfter} ref={Ref(TEX)})");
            var wrong2 = Run(bad.LoadAsync<Sprite>("Atlas/tex"));
            int e2 = Debug.Errors - e0;
            Tick();
            Check(wrong2 == null && e2 == 2, $"retrying the same wrong call is not silent either (errors={e2})");
            bad.Dispose(); Tick();

            var held = ContentScope.ForScene(sceneA).CreateChild("AtlasHeld");
            Run(held.LoadAsync<Texture2D>("Atlas/tex"));
            int e3 = Debug.Errors;
            var asSprite = Run(held.LoadAsync<Sprite>("Atlas/tex"));
            int e4 = Debug.Errors - e3;
            Check(asSprite == null && e4 == 1 && held.Count == 1 && Ref(TEX) == 1, $"requesting a held key as another type: 1 error, existing hold kept (errors={e4} count={held.Count} ref={Ref(TEX)})");
            held.Dispose(); Tick();

            var atlasView = NewGO("AtlasLink", sceneA).AddComponent<IconView>();
            atlasView.icon.SetName("Atlas/tex");
            int e5 = Debug.Errors;
            var linkAsset = Run(atlasView.icon.LoadAsync(atlasView));
            int e6 = Debug.Errors - e5;
            Tick();
            Check(linkAsset == null && e6 == 1 && !atlasView.icon.IsValid && Ref(TEX) == 0, $"texture name in a ContentLinkSprite: null + 1 error, nothing held (errors={e6} valid={atlasView.icon.IsValid} ref={Ref(TEX)})");
            Object.Destroy(atlasView.gameObject); Tick();
            Debug.Verbose = true;

            Check(ContentService.UnregisterDirectory(dirAtlas), "Atlas directory cleanup");
        }

        Console.WriteLine("10d) a scope's entries after a forced unregistration killed their leases (F-6)");
        {
            const int SIXA = 70, SIXB = 71;
            FakeEngine.Assets[SIXA] = new Sprite { name = "sixa" }; FakeEngine.IdDir[SIXA] = "Six";
            FakeEngine.Assets[SIXB] = new Sprite { name = "sixb" }; FakeEngine.IdDir[SIXB] = "Six";
            var sixCat = ScriptableObject.CreateInstance<ContentCatalog>();
            sixCat.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Six/a", id = new LoadableObjectId { value = SIXA } }, new() { name = "Six/b", id = new LoadableObjectId { value = SIXB } } });
            FakeEngine.Dirs["Six"] = new ScriptableObject[] { sixCat };
            int dirSix = ContentService.RegisterDirectory("Six");
            var six = ContentScope.ForScene(sceneA).CreateChild("Six");
            six.Load<Sprite>("Six/a");
            var hb = ContentService.Acquire<Sprite>("Six/b"); six.Attach(hb);
            Check(six.Count == 2 && six.Contains("Six/a") && hb.IsValid, "precondition: one keyed and one attached lease, Count 2");
            Debug.Verbose = false;
            bool forced = ContentService.UnregisterDirectory(dirSix, force: true);
            Debug.Verbose = true;
            Check(forced && !hb.IsValid, "force unregister kills both leases");
            Check(!six.Contains("Six/a"), "① Contains is false for the dead key (as before)");
            Check(six.Count == 0, $"② Count counts live leases only (actual: {six.Count})");
            var dump = ContentScope.DumpTree();
            Check(!dump.Contains("Six/a"), "③ DumpTree does not list a dead key");
            int w0 = Debug.Warnings; bool released = six.Release("Six/a"); int warns = Debug.Warnings - w0;
            Check(!released && warns == 0, $"④ Release of a dead key returns false without a warning (actual: returned={released} warnings=+{warns})");
            bool keyGone = true; foreach (var k in six.Keys) if (k == "Six/a") keyGone = false;
            Check(keyGone && six.Count == 0, $"⑤ after Release the dead key is gone and Count is 0 (actual: keyGone={keyGone} count={six.Count})");
            dirSix = ContentService.RegisterDirectory("Six");
            var again = six.Load<Sprite>("Six/a");
            Check(again != null && six.Contains("Six/a") && six.Count == 1, $"⑥ after re-registration the key loads again and Count is 1 — the dead attached lease is not counted (actual: count={six.Count})");
            int w1 = Debug.Warnings; six.Dispose(); Tick();
            Check(Debug.Warnings == w1 && Ref(SIXA) == 0, "⑦ disposing a scope with dead entries warns nothing and releases the live one (as before)");
            Check(ContentService.UnregisterDirectory(dirSix), "cleanup: Six unregisters");
        }

        Console.WriteLine("11) scene catalog index");
        SceneManager.Open(sceneA);
        Check(ContentService.ContainsScene("Scenes/One") && ContentService.TryGetLoadableSceneId("Scenes/Two", out var twoId) && twoId == SceneId(SC_TWO)
              && !ContentService.TryGetLoadableSceneId("Scenes/Nope", out _) && !ContentService.ContainsScene(null), "ContainsScene · TryGetLoadableSceneId");
        Check(!ContentService.Contains("Scenes/One") && !ContentService.TryGetLoadableObjectId("Scenes/One", out _), "scene names are not in the asset index");
        {
            var s11 = ContentScope.ForScene(sceneA).CreateChild("SceneAsAsset");
            Debug.Verbose = false; int e0 = Debug.Errors;
            var wrongType = Run(s11.LoadAsync<Sprite>("Scenes/One"));
            int e1 = Debug.Errors - e0; Debug.Verbose = true;
            Check(wrongType == null && e1 == 1 && Debug.LastError.Contains("LoadSceneAsync") && s11.Count == 0, $"scene name passed to the asset API → null + 1 hint error (errors={e1})");
            s11.Dispose(); Tick();
            Debug.Verbose = false; e0 = Debug.Errors;
            var nope = Run(ContentService.LoadSceneAsync("Scenes/Nope"));
            Debug.Verbose = true;
            Check(!nope.IsValid() && Debug.Errors - e0 == 1 && ContentService.LoadedSceneCount == 0, "unknown scene name → invalid Scene + 1 error");
        }

        Console.WriteLine("12) load → track → unload, and Single mode");
        {
            var one = Run(ContentService.LoadSceneAsync("Scenes/One"));
            Check(one.IsValid() && one.isLoaded && one.loadableSceneId == SceneId(SC_ONE) && ContentService.LoadedSceneCount == 1, "Additive load → 1 tracked");
            var oneScope = ContentScope.ForScene(one);
            oneScope.CreateChild("InOne").Load<Sprite>("Icons/sword");
            var unloadOp = SceneManager.UnloadSceneAsync(one);
            Check(unloadOp != null && ContentService.LoadedSceneCount == 1, "unload started — still tracked until it completes");
            Debug.Verbose = false; int w = Warn(() => { FakeEngine.CompleteLoads(); Tick(); }); Debug.Verbose = true;
            Check(ContentService.LoadedSceneCount == 0 && oneScope.IsDisposed && Ref(SWORD) == 0 && w == 1, $"sceneUnloaded → untracked, scene scope cascades (1 leak warning for InOne) (w={w})");

            one = Run(ContentService.LoadSceneAsync("Scenes/One"));
            var snap = new System.Collections.Generic.List<ContentService.SceneInfo>();
            ContentService.SnapshotScenes(snap);
            Check(snap.Count == 1 && snap[0].Key == "Scenes/One" && snap[0].Dir == dir && snap[0].Scene.handle == one.handle, "SnapshotScenes");
            var two = Run(ContentService.LoadSceneAsync("Scenes/Two", LoadSceneMode.Single));
            Check(two.IsValid() && !one.isLoaded && ContentService.LoadedSceneCount == 1 && SceneManager.loadedSceneCount == 1, "content scenes (and the bootstrap scene) closed by a Single load are untracked too");
            SceneManager.Open(sceneA);
            SceneManager.UnloadSceneAsync(two); FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0 && SceneManager.loadedSceneCount == 1, "cleanup");
        }

        Console.WriteLine("12b) which instance a scope's scene load claims (F-7)");
        {
            Scene Newest12() => FakeEngine.LoadedScenes[FakeEngine.LoadedScenes.Count - 1];
            SceneManager.LoadSceneAsync(SceneId(SC_ONE), new LoadSceneParameters(LoadSceneMode.Additive)); FakeEngine.CompleteLoads();
            var usersScene = Newest12();   // the user opened the scene through the engine, before any scope asked for it
            // recovery for the current code, whose scope loads unload the user's instance: leave only the user's instance open
            void Normalize12()
            {
                foreach (var sc in FakeEngine.LoadedScenes.ToArray()) if (sc.loadableSceneId == SceneId(SC_ONE) && sc.handle != usersScene.handle) SceneManager.UnloadSceneAsync(sc);
                FakeEngine.CompleteLoads(); Tick();
                if (!FakeEngine.IsLoaded(usersScene.handle)) { SceneManager.LoadSceneAsync(SceneId(SC_ONE), new LoadSceneParameters(LoadSceneMode.Additive)); FakeEngine.CompleteLoads(); usersScene = Newest12(); }
            }
            Check(ContentService.LoadedSceneCount == 1, "① the engine-loaded instance is tracked — 'whoever loaded it'");

            var ownerScope = ContentScope.Root.CreateChild("Owner");
            var ownedScene = Run(ownerScope.LoadSceneAsync("Scenes/One"));
            var ownScene = Newest12();
            Check(ownedScene.IsValid() && ownedScene.handle == ownScene.handle && ownScene.handle != usersScene.handle, $"② a scope's load claims the instance it produced, not the user's (actual: claimed={ownedScene.handle} produced={ownScene.handle} user's={usersScene.handle})");
            ownerScope.Dispose(); FakeEngine.CompleteLoads();
            Check(FakeEngine.IsLoaded(usersScene.handle) && !FakeEngine.IsLoaded(ownScene.handle), $"③ disposing the scope unloads its own instance and leaves the user's (actual: user's={FakeEngine.IsLoaded(usersScene.handle)} own={FakeEngine.IsLoaded(ownScene.handle)})");
            Normalize12();

            var scopeA12 = ContentScope.Root.CreateChild("A12"); var scopeB12 = ContentScope.Root.CreateChild("B12");
            var loadA12 = scopeA12.LoadSceneAsync("Scenes/One"); var loadB12 = scopeB12.LoadSceneAsync("Scenes/One");
            FakeEngine.CompleteLoads();
            var gotA12 = loadA12.GetResult(); var gotB12 = loadB12.GetResult();
            Check(gotA12.IsValid() && gotB12.IsValid() && gotA12.handle != gotB12.handle && gotA12.handle != usersScene.handle && gotB12.handle != usersScene.handle, "④ two concurrent scope loads get two distinct instances, neither the user's");
            scopeA12.Dispose(); FakeEngine.CompleteLoads();
            bool onlyA = !FakeEngine.IsLoaded(gotA12.handle) && FakeEngine.IsLoaded(gotB12.handle) && FakeEngine.IsLoaded(usersScene.handle);
            scopeB12.Dispose(); FakeEngine.CompleteLoads();
            Check(onlyA && !FakeEngine.IsLoaded(gotB12.handle) && FakeEngine.IsLoaded(usersScene.handle), $"④b disposing each scope unloads its own instance only (actual: after A={onlyA} B gone={!FakeEngine.IsLoaded(gotB12.handle)} user's={FakeEngine.IsLoaded(usersScene.handle)})");
            Normalize12();

            FakeEngine.FailNextSceneLoad = true;
            var failScope = ContentScope.Root.CreateChild("Failer");
            Debug.Verbose = false; int errs12 = Debug.Errors;
            var failScene = Run(failScope.LoadSceneAsync("Scenes/One"));
            Debug.Verbose = true;
            failScope.Dispose(); FakeEngine.CompleteLoads();
            Check(!failScene.IsValid() && Debug.Errors == errs12 + 1 && FakeEngine.IsLoaded(usersScene.handle), $"⑤ a scope load the engine fails reports failure and never claims the user's instance (actual: valid={failScene.IsValid()} errors=+{Debug.Errors - errs12} user's loaded={FakeEngine.IsLoaded(usersScene.handle)})");
            Normalize12();

            var cancelScope = ContentScope.Root.CreateChild("Canceller");
            var cancelLoad = cancelScope.LoadSceneAsync("Scenes/One");
            cancelScope.Dispose();
            bool wasCancelled = false; try { FakeEngine.CompleteLoads(); cancelLoad.GetResult(); } catch (OperationCanceledException) { wasCancelled = true; }
            FakeEngine.CompleteLoads();
            Check(wasCancelled && FakeEngine.IsLoaded(usersScene.handle) && ContentService.LoadedSceneCount == 1, $"⑥ a scope that ends mid-load unloads the instance it produced, not the user's (actual: cancelled={wasCancelled} user's loaded={FakeEngine.IsLoaded(usersScene.handle)} tracked={ContentService.LoadedSceneCount})");
            Normalize12();

            var snap12 = new System.Collections.Generic.List<ContentService.SceneInfo>(); ContentService.SnapshotScenes(snap12);
            Debug.Verbose = false; bool refused12 = !ContentService.UnregisterDirectory(dir); Debug.Verbose = true;
            SceneManager.UnloadSceneAsync(usersScene); FakeEngine.CompleteLoads(); Tick();
            Check(snap12.Count == 1 && snap12[0].Scene.handle == usersScene.handle && refused12 && ContentService.LoadedSceneCount == 0, "⑦ the user's instance is tracked, refuses unregistration and is theirs to unload (as before)");
        }

        Console.WriteLine("13) unregistering a directory with an open scene is refused (every directory that holds the scene)");
        {
            SceneCatalog("Arena", ("Levels/Arena", SC_ARENA));
            SceneCatalog("Arena2", ("Levels/Arena", SC_ARENA));
            int dirArena = ContentService.RegisterDirectory("Arena");
            int dirArena2 = ContentService.RegisterDirectory("Arena2");
            var arena = Run(ContentService.LoadSceneAsync("Levels/Arena"));
            Check(arena.IsValid() && ContentService.LoadedSceneCount == 1, "Arena load");
            Debug.Verbose = false; int e0 = Debug.Errors;
            bool refuse1 = ContentService.UnregisterDirectory(dirArena);
            bool refuse2 = ContentService.UnregisterDirectory(dirArena2, force: true);
            int errs = Debug.Errors - e0; Debug.Verbose = true;
            Check(!refuse1 && !refuse2 && errs == 2 && FakeEngine.Registered.Contains("Arena") && FakeEngine.Registered.Contains("Arena2") && Debug.LastError.Contains("Levels/Arena"),
                  $"open scene → both holding directories refused (force included), scene names listed (r1={refuse1} r2={refuse2} errors={errs})");
            SceneManager.UnloadSceneAsync(arena); FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.UnregisterDirectory(dirArena) && ContentService.UnregisterDirectory(dirArena2) && ContentService.LoadedSceneCount == 0, "unregistration succeeds after unload");
        }

        Console.WriteLine("14) cancellation, and UnloadAsync while loading");
        {
            var cts = new CancellationTokenSource();
            var pending = ContentService.LoadSceneAsync("Scenes/One", LoadSceneMode.Additive, cts.Token);
            cts.Cancel();
            bool cancelled = false; try { pending.GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && ContentService.LoadedSceneCount == 0, "cancel → OperationCanceledException");
            FakeEngine.CompleteLoads();
            Check(ContentService.LoadedSceneCount == 1 && FakeEngine.Unloading.Count == 1, "engine load completes → rollback (unload) starts");
            var again = ContentService.LoadSceneAsync("Scenes/One");   // must not claim the instance that is unloading
            FakeEngine.CompleteLoads(); var againScene = again.GetResult(); Tick();
            Check(againScene.IsValid() && againScene.isLoaded && ContentService.LoadedSceneCount == 1 && FakeEngine.Unloading.Count == 0, "cancelled scene reclaimed; a concurrent reload gets a fresh instance");
            SceneManager.UnloadSceneAsync(againScene); FakeEngine.CompleteLoads(); Tick();

            cts = new CancellationTokenSource();
            pending = ContentService.LoadSceneAsync("Scenes/Two", LoadSceneMode.Single, cts.Token);
            Debug.Verbose = false; int w = Warn(() => cts.Cancel()); Debug.Verbose = true;   // the cancellation continuation runs synchronously inside Cancel()
            cancelled = false; try { pending.GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            FakeEngine.CompleteLoads();
            Check(cancelled && w == 1 && Debug.LastWarning.Contains("Single") && ContentService.LoadedSceneCount == 1 && SceneManager.loadedSceneCount == 1, $"Single cancel: 1 warning, scene kept (w={w})");
            SceneManager.Open(sceneA);
            SceneManager.UnloadSceneAsync(SceneManager.GetSceneByLoadableSceneId(SceneId(SC_TWO))); FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0, "cleanup");

            var holder = NewGO("Holder", sceneA).AddComponent<SceneHolder>();
            holder.level.SetName("Scenes/One");
            var lp = holder.level.LoadAsync(holder);
            bool loadingBefore = holder.level.IsLoading;
            RunVNoComplete(holder.level.UnloadAsync());
            Check(loadingBefore && !holder.level.IsLoading && ContentService.LoadedSceneCount == 0, "UnloadAsync while loading → the wait is abandoned");
            FakeEngine.CompleteLoads(); var lr = lp.GetResult();
            Check(!lr.IsValid() && !holder.level.IsLoaded && ContentService.LoadedSceneCount == 1 && FakeEngine.Unloading.Count == 1, "old load result is invalid; unload starts after completion");
            FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0 && holder.gameObject.Scope().SceneCount == 0, "reclaimed; owned list is empty");
            Object.Destroy(holder.gameObject); Tick();
        }

        Console.WriteLine("15) direct loads are accounted too; unindexed scenes; link rules");
        {
            var direct = SceneManager.LoadSceneAsync(SceneId(SC_ONE), new LoadSceneParameters(LoadSceneMode.Additive));
            FakeEngine.CompleteLoads();
            Check(direct != null && direct.isDone && ContentService.LoadedSceneCount == 1, "a direct SceneManager load is tracked too");
            Debug.Verbose = false;
            Check(!ContentService.UnregisterDirectory(dir) && FakeEngine.Registered.Contains("Main"), "a directly loaded scene also blocks unregistration");
            Debug.Verbose = true;
            var oneDirect = SceneManager.GetSceneByLoadableSceneId(SceneId(SC_ONE));
            SceneManager.UnloadSceneAsync(oneDirect); FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0, "direct unload → untracked");

            SceneManager.LoadSceneAsync(SceneId(SC_LOOSE), new LoadSceneParameters(LoadSceneMode.Additive)); FakeEngine.CompleteLoads();
            Check(ContentService.LoadedSceneCount == 0 && SceneManager.loadedSceneCount == 2, "an unindexed scene is not tracked");
            SceneManager.UnloadSceneAsync(SceneManager.GetSceneByLoadableSceneId(SceneId(SC_LOOSE))); FakeEngine.CompleteLoads(); Tick();
            Debug.Verbose = false; int e0 = Debug.Errors;
            var byId = Run(ContentService.LoadSceneAsync(SceneId(SC_LOOSE), new LoadSceneParameters(LoadSceneMode.Additive)));
            Debug.Verbose = true;
            Check(!byId.IsValid() && Debug.Errors - e0 == 1 && ContentService.LoadedSceneCount == 0, "LoadSceneAsync with an unindexed id → invalid + 1 error, no engine load started");

            var holder = NewGO("Holder15", sceneA).AddComponent<SceneHolder>();
            holder.level.SetName("Scenes/One");
            var first = Run(holder.level.LoadAsync(holder));
            Check(first.IsValid() && holder.level.IsLoaded && holder.level.Scope == holder.gameObject.Scope() && holder.gameObject.Scope().SceneCount == 1, "link load → registered in the owning scope");
            Debug.Verbose = false; e0 = Debug.Errors;
            var again = Run(holder.level.LoadAsync(holder));
            holder.level.SetName("Scenes/Two");
            int errs = Debug.Errors - e0; Debug.Verbose = true;
            Check(errs == 2 && holder.level.Name == "Scenes/One" && again.handle == first.handle && ContentService.LoadedSceneCount == 1, $"double load and SetName while loaded are refused (errors={errs})");
            RunV(holder.level.UnloadAsync()); Tick();
            Check(!holder.level.IsLoaded && holder.level.Scope == null && ContentService.LoadedSceneCount == 0 && holder.gameObject.Scope().SceneCount == 0, "UnloadAsync → unloaded and ownership released");
            bool argNull = false; try { holder.level.LoadAsync((ContentScope)null).GetResult(); } catch (ArgumentNullException) { argNull = true; }
            Check(argNull && !holder.level.IsLoading, "owner null → ArgumentNullException (at the await point, like ContentLink<T>)");
            Object.Destroy(holder.gameObject); Tick();
        }

        Console.WriteLine("16) scope-owned scenes");
        {
            var stage16 = ContentScope.ForScene(sceneA).CreateChild("Stage3");
            var owned = Run(stage16.LoadSceneAsync("Scenes/One"));
            Check(owned.IsValid() && stage16.SceneCount == 1 && stage16.Count == 0 && ContentScope.DumpTree().Contains("scenes: Scenes/One"), "scope-owned load; the dump shows the scene");
            var inOwned = ContentScope.ForScene(owned);
            Check(Warn(() => stage16.Dispose()) == 0 && FakeEngine.Unloading.Count == 1 && stage16.SceneCount == 0, "Dispose → unload starts");
            FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0 && inOwned.IsDisposed, "unload completes; scene scope cleaned up");

            var go16 = NewGO("LevelOwner", sceneA);
            Run(go16.Scope().LoadSceneAsync("Scenes/Two"));
            Object.Destroy(go16); FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0, "owning object destroyed → unload");

            stage16 = ContentScope.ForScene(sceneA).CreateChild("Stage4");
            owned = Run(stage16.LoadSceneAsync("Scenes/One"));
            Check(stage16.ReleaseScene(owned) && stage16.SceneCount == 0 && !stage16.ReleaseScene(owned) && FakeEngine.Unloading.Count == 1, "ReleaseScene unloads early; a second call returns false");
            FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0, "reclaimed");
            stage16.Dispose();

            stage16 = ContentScope.Root.CreateChild("Stage5");
            owned = Run(stage16.LoadSceneAsync("Scenes/One"));
            SceneManager.UnloadScene(sceneA);   // test setup: make One the last loaded scene
            Debug.Verbose = false; int w = Warn(() => stage16.Dispose()); Debug.Verbose = true;
            Check(w == 1 && Debug.LastWarning.Contains("last loaded") && owned.isLoaded && ContentService.LoadedSceneCount == 1 && FakeEngine.Unloading.Count == 0, $"the last scene is kept, with a warning (w={w})");
            SceneManager.Open(sceneA);
            SceneManager.UnloadSceneAsync(owned); FakeEngine.CompleteLoads(); Tick();

            stage16 = ContentScope.Root.CreateChild("Stage6");
            var p16 = stage16.LoadSceneAsync("Scenes/One");
            stage16.Dispose();
            bool cancelled = false; try { p16.GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            FakeEngine.CompleteLoads();
            Check(cancelled && ContentService.LoadedSceneCount == 1 && FakeEngine.Unloading.Count == 1, "scope disposed while loading → cancelled; unload starts after completion");
            FakeEngine.CompleteLoads(); Tick();
            Check(ContentService.LoadedSceneCount == 0 && SceneManager.loadedSceneCount == 1, "reclaimed");
        }

        Console.WriteLine("21) editor utility: naming rules, field types, catalog lookup");
        {
            const string ROOT = "Assets/Content";
            Init(typeof(ContentLinkEditorUtility), "Install");   // subscribes to projectChanged
            ContentLinkEditorUtility.Invalidate();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");   // the fake project has the folder the scenarios keep their catalogs in — CreateAsset needs the parent folder, as the engine does

            // Naming rules: uses the same function as the builder
            Check(ContentLinkEditorUtility.ToName(ROOT, "Assets/Content/x.png") == "x"
                  && ContentLinkEditorUtility.ToName(ROOT, "Assets/Content/Icons/sword.png") == "Icons/sword"
                  && ContentLinkEditorUtility.ToName(ROOT, "Assets/Content/A/B/c.mat") == "A/B/c",
                  "ToName: directly under root, in a sub-folder, nested");
            var subSprite = new Sprite { name = "sword" };
            Check(ContentLinkEditorUtility.ToName(ROOT, "Assets/Content/Icons/sword.png", subSprite) == "Icons/sword[sword]", "ToName: sub-sprite");
            Check(ContentLinkEditorUtility.IsUnder(ROOT, "Assets/Content/x.png") && !ContentLinkEditorUtility.IsUnder(ROOT, "Assets/Other/x.png")
                  && !ContentLinkEditorUtility.IsUnder(ROOT, "Assets/ContentX/x.png"), "IsUnder: a folder that merely shares the prefix is outside");

            // Field types: arrays and Lists must be unwrapped
            Type F(string n) => ContentLinkEditorUtility.AssetTypeOf(typeof(LinkFields).GetField(n));
            Check(F("one") == typeof(Sprite) && F("many") == typeof(Sprite) && F("list") == typeof(GameObject)
                  && F("direct") == typeof(Texture2D) && F("scene") == null && F("notALink") == null,
                  $"AssetTypeOf: single, array, List, direct generic, scene link, non-link (many={F("many")?.Name ?? "null"} list={F("list")?.Name ?? "null"})");
            Check(ContentLinkEditorUtility.ObjectFieldTypeFor(typeof(Sprite)) == typeof(Sprite)
                  && ContentLinkEditorUtility.ObjectFieldTypeFor(typeof(IconView)) == typeof(GameObject),
                  "ObjectFieldTypeFor: a component T maps to GameObject");

            // No catalog → NoCatalog
            Check(ContentLinkEditorUtility.Validate("Icons/sword", typeof(Sprite)) == NameStatus.NoCatalog
                  && ContentLinkEditorUtility.Validate("", typeof(Sprite)) == NameStatus.Empty,
                  "Validate: no catalog; empty name");

            // Fake project + catalog
            var swordTex = new Texture2D { name = "sword" };
            var swordSprite = new Sprite { name = "sword" };
            var catPrefab21 = new GameObject { name = "Cat" }; catPrefab21.AddComponent<IconView>();
            var outside = new Sprite { name = "elsewhere" };
            var sceneAsset = new UnityEditor.SceneAsset { name = "Arena" };
            FakeEngine.AddProjectAsset("Assets/Content/Icons/sword.png", swordTex, swordSprite);
            FakeEngine.AddProjectAsset("Assets/Content/Prefabs/Cat.catPrefab21", catPrefab21);
            FakeEngine.AddProjectAsset("Assets/Content/Scenes/Arena.unity", sceneAsset);
            FakeEngine.AddProjectAsset("Assets/Other/elsewhere.png", outside);

            var cat = ScriptableObject.CreateInstance<ContentCatalog>();
            cat.EditorSetEntries(new ContentCatalog.Entry[] {
                new() { name = "Icons/sword", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(swordTex) },
                new() { name = "Icons/sword[sword]", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(swordSprite) },
                new() { name = "Prefabs/Cat", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(catPrefab21) },
            });
            cat.EditorSetScenes(new ContentCatalog.SceneEntry[] {
                new() { name = "Scenes/Arena", id = UnityEditor.LoadableSceneIdEditorUtility.CreateLoadableSceneId("Assets/Content/Scenes/Arena.unity") },
            });
            FakeEngine.AddProjectAsset("Assets/Generated/Catalog.asset", cat);
            ContentLinkEditorUtility.Invalidate();
            Check(ContentLinkEditorUtility.LoadCatalogs().Length == 1, $"LoadCatalogs: finds catalogs via t:ContentCatalog (n={ContentLinkEditorUtility.LoadCatalogs().Length})");

            // Asset → name: the catalog reverse index comes first; outside the root is refused; non-sprite sub-assets are refused
            Check(ContentLinkEditorUtility.TryGetNameForAsset(swordSprite, out var n1, out _) && n1 == "Icons/sword[sword]", "asset → name: sub-sprite");
            Check(ContentLinkEditorUtility.TryGetNameForAsset(catPrefab21, out var n2, out _) && n2 == "Prefabs/Cat", "asset → name: prefab");
            Check(ContentLinkEditorUtility.TryGetNameForAsset(sceneAsset, out var n3, out _) && n3 == "Scenes/Arena", "asset → name: scene");
            Check(!ContentLinkEditorUtility.TryGetNameForAsset(outside, out _, out var why) && why.Contains("not under"), $"outside SourceRoot is refused, with a reason ({why})");
            var mesh = new Object { name = "body" };
            FakeEngine.AddProjectAsset("Assets/Content/Models/rig.fbx", new Object { name = "rig" }, mesh);
            ContentLinkEditorUtility.Invalidate();
            Check(!ContentLinkEditorUtility.TryGetNameForAsset(mesh, out _, out var why2) && why2.Contains("sub-asset"), $"a non-sprite sub-asset is refused ({why2})");

            // Name → asset: via the catalog, via path reconstruction, and negative cases
            Check(ReferenceEquals(ContentLinkEditorUtility.ResolveAsset("Icons/sword[sword]"), swordSprite)
                  && ReferenceEquals(ContentLinkEditorUtility.ResolveAsset("Prefabs/Cat"), catPrefab21)
                  && ReferenceEquals(ContentLinkEditorUtility.ResolveScene("Scenes/Arena"), sceneAsset), "name → asset: catalog");
            Check(ReferenceEquals(ContentLinkEditorUtility.ResolveAsset("Models/rig"), FakeEngine.MainAssetAt["Assets/Content/Models/rig.fbx"]),
                  "name → asset: found by path reconstruction even outside the catalog (so the drawer keeps showing the asset for a name that is not built yet)");
            Check(ContentLinkEditorUtility.ResolveAsset("Icons/nope") == null && ContentLinkEditorUtility.ResolveAsset("") == null, "unknown name → null");

            // Validate branches
            Check(ContentLinkEditorUtility.Validate("Icons/sword[sword]", typeof(Sprite)) == NameStatus.Ok
                  && ContentLinkEditorUtility.Validate("Icons/sword", typeof(Texture2D)) == NameStatus.Ok
                  && ContentLinkEditorUtility.Validate("Icons/sword", typeof(Sprite)) == NameStatus.TypeMismatch
                  && ContentLinkEditorUtility.Validate("Prefabs/Cat", typeof(IconView)) == NameStatus.Ok
                  && ContentLinkEditorUtility.Validate("Icons/nope", typeof(Sprite)) == NameStatus.NotInCatalog
                  && ContentLinkEditorUtility.Validate("Models/rig", typeof(Object)) == NameStatus.NotInCatalog
                  && ContentLinkEditorUtility.Validate("Scenes/Arena", null, scene: true) == NameStatus.Ok
                  && ContentLinkEditorUtility.Validate("Icons/sword", null, scene: true) == NameStatus.NotInCatalog,
                  "Validate: Ok, type mismatch, not in catalog, scenes checked separately");

            // Cache invalidation: is the projectChanged subscription really wired up (stub version of the editor check)
            cat.EditorSetEntries(new ContentCatalog.Entry[] {
                new() { name = "Icons/renamed", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(swordTex) },
            });
            bool staleBefore = ContentLinkEditorUtility.Validate("Icons/renamed", typeof(Texture2D)) == NameStatus.NotInCatalog;
            UnityEditor.EditorApplication.RaiseProjectChanged();
            bool freshAfter = ContentLinkEditorUtility.Validate("Icons/renamed", typeof(Texture2D)) == NameStatus.Ok;
            Check(staleBefore && freshAfter, $"projectChanged rebuilds the index (before={staleBefore} after={freshAfter})");

            // ── The catalog remembers its own source root, and path reconstruction uses that root ──
            {
                var keepRoot = ContentDirectoryBuilder.SourceRoot;
                var keepCatalog = ContentDirectoryBuilder.CatalogPath;

                ContentDirectoryBuilder.SourceRoot = "Assets/Content";
                ContentDirectoryBuilder.CatalogPath = "Assets/Generated/Catalog2.asset";
                Check(ContentDirectoryBuilder.UpdateCatalog(), "UpdateCatalog");
                var built = UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>("Assets/Generated/Catalog2.asset");
                Check(built != null && built.SourceRoot == "Assets/Content",
                      $"the builder records the source root in the catalog (root='{built?.SourceRoot}')");
                Check(ScriptableObject.CreateInstance<ContentCatalog>().SourceRoot == "",
                      "a catalog that was never written has an empty root (the state the simulation overlay later skips)");

                // When the catalog root differs from the static root, does a new asset under the catalog root still get a name?
                var late = new Texture2D { name = "late" };
                FakeEngine.AddProjectAsset("Assets/Content/Icons/late.png", late);
                ContentDirectoryBuilder.SourceRoot = "Assets/Other";
                ContentLinkEditorUtility.Invalidate();
                bool named = ContentLinkEditorUtility.TryGetNameForAsset(late, out var lateName, out var lateWhy);
                Check(named && lateName == "Icons/late",
                      $"even with the static root at Assets/Other, the name comes from the catalog root ({lateName ?? lateWhy})");
                Check(ReferenceEquals(ContentLinkEditorUtility.ResolveAsset("Icons/late"), late),
                      "name → asset path reconstruction also uses the catalog root");

                // When roots overlap, the deepest root wins
                ContentDirectoryBuilder.SourceRoot = "Assets/Content/Icons";
                ContentDirectoryBuilder.CatalogPath = "Assets/Generated/Catalog3.asset";
                Check(ContentDirectoryBuilder.UpdateCatalog(), "UpdateCatalog with a nested root");
                ContentLinkEditorUtility.Invalidate();
                var roots = ContentLinkEditorUtility.SourceRoots();
                Check(Array.IndexOf(roots, "Assets/Content/Icons") == 0 && Array.IndexOf(roots, "Assets/Content") > 0,
                      $"roots are ordered longest first ({string.Join(" · ", roots)})");
                Check(ContentLinkEditorUtility.RootFor("Assets/Content/Icons/late.png") == "Assets/Content/Icons",
                      "the deepest of the overlapping roots is chosen");
                Check(ContentLinkEditorUtility.RootFor("Assets/Other/elsewhere.png") == null,
                      "null when under no root");
                bool noEmptyRoot = true;
                foreach (var r in roots) if (r.Length == 0) noEmptyRoot = false;
                Check(noEmptyRoot, "an empty root (a catalog that was never written) is not listed");

                ContentDirectoryBuilder.SourceRoot = keepRoot;
                ContentDirectoryBuilder.CatalogPath = keepCatalog;
            }

            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("17) directory unregistration safeguard");
        var hold = ContentScope.ForScene(sceneA).CreateChild("Hold");
        hold.Load<Sprite>("Icons/sword");
        Debug.Verbose = false;
        Check(!ContentService.UnregisterDirectory(dir), "refused while assets are held");
        Debug.Verbose = true;

        Console.WriteLine("18) scope tree dump");
        var dd = NewGO("Boss", sceneA); dd.Scope().Load<Sprite>("Icons/shield");
        Console.Write(ContentScope.DumpTree().Replace("\n", "\n    ").Insert(0, "    "));
        Console.WriteLine();

        Console.WriteLine("19) quit (directories with an open scene are skipped)");
        int dirArenaQ = ContentService.RegisterDirectory("Arena");
        var stageQ = ContentScope.Root.CreateChild("StageQuit");
        var arenaQ = Run(stageQ.LoadSceneAsync("Levels/Arena"));
        Check(arenaQ.IsValid() && stageQ.SceneCount == 1, "before quit: a Root child scope owns the Arena scene");
        int w13 = Warn(() => Application.Quit());
        Check(w13 == 2, $"warnings for the explicit scopes that were never disposed (Hold, StageQuit) (w={w13})");
        Check(ContentService.LiveAssetCount == 0 && !FakeEngine.Registered.Contains("Main") && Ref(SWORD) == 0 && Ref(SHIELD) == 0, "everything released, Main directory unregistered (no engine exception)");
        Check(FakeEngine.Registered.Contains("Arena") && arenaQ.isLoaded && FakeEngine.Unloading.Count == 0 && ContentService.LoadedSceneCount == 0,
              $"Arena with its open scene is skipped; quit starts no unload; tracking is cleared (registered={FakeEngine.Registered.Contains("Arena")} loaded={arenaQ.isLoaded} unloading={FakeEngine.Unloading.Count} tracked={ContentService.LoadedSceneCount})");

        Console.WriteLine("20) next session start (leftover registrations are cleared)");
        FakeEngine.LoadedScenes.Clear();   // the editor has unloaded every scene on leaving Play Mode, but directory registrations survive
        Init(typeof(ContentService), "ResetStatics");
        Check(FakeEngine.Registered.Count == 0, $"ResetStatics unregisters the previous session's leftover registration (Arena) (registered={FakeEngine.Registered.Count})");
        int dirAgain = ContentService.RegisterDirectory("Arena");
        Check(dirAgain == 0 && FakeEngine.Registered.Contains("Arena") && ContentService.ContainsScene("Levels/Arena"), "re-registering the same path succeeds");
        Check(ContentService.UnregisterDirectory(dirAgain), "cleanup");

        Console.WriteLine("21) editor simulation overlay");
        {
            Init(typeof(ContentService), "ResetStatics");
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Drop();
            FakeEngine.EditorServesAnyId = true;   // like the real editor: an id that is in no directory still loads

            // Project: two assets under the root Assets/Sim. Only a is in the catalog; b is the "new asset not registered yet"
            var simA = new Texture2D { name = "a" };
            var simB = new Texture2D { name = "b" };
            FakeEngine.AddProjectAsset("Assets/Sim/Icons/a.png", simA);
            FakeEngine.AddProjectAsset("Assets/Sim/Icons/b.png", simB);
            FakeEngine.Assets[FakeEngine.ObjectIdOf[simA]] = simA;
            FakeEngine.Assets[FakeEngine.ObjectIdOf[simB]] = simB;
            var idA = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(simA);

            var projectCatalog = ScriptableObject.CreateInstance<ContentCatalog>();
            projectCatalog.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Icons/a", id = idA } });
            projectCatalog.EditorSetSourceRoot("Assets/Sim");
            FakeEngine.AddProjectAsset("Assets/Generated/SimCatalog.asset", projectCatalog);

            // What gets registered is the built copy: a different object from the project asset, sharing only the SourceRoot string
            var builtCatalog = ScriptableObject.CreateInstance<ContentCatalog>();
            builtCatalog.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Icons/a", id = idA } });
            builtCatalog.EditorSetSourceRoot("Assets/Sim");
            FakeEngine.Dirs["SimDir"] = new ScriptableObject[] { builtCatalog };
            FakeEngine.IdDir[idA.value] = "SimDir";

            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentLinkSimulation), "Install");

            // Build-up: on entering Play, name → path is indexed without loading anything
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.EnteredPlayMode);
            Check(ContentLinkSimulation.TryGetPath("Icons/b", out var simPath, out var simRoot)
                  && simPath == "Assets/Sim/Icons/b.png" && simRoot == "Assets/Sim",
                  $"the overlay picks up a new asset that is not in the catalog (path={simPath} root={simRoot})");

            // Nothing resolves before the directory is registered
            Check(!ContentService.Contains("Icons/b"), "with the directory unregistered, nothing resolves even if the overlay has it (a missing registration is not hidden)");

            int dirSim = ContentService.RegisterDirectory("SimDir");
            Check(!ReferenceEquals(projectCatalog, builtCatalog) && ContentService.IsSourceRootRegistered("Assets/Sim"),
                  "registration is judged by the SourceRoot string — the built copy and the project asset are different objects");
            Check(ContentService.Contains("Icons/b") && ContentService.Contains("Icons/a"),
                  "Contains is true for the new asset too (RuntimeKeyIsValid does not lie)");

            var hb = ContentService.Acquire<Texture2D>("Icons/b");
            Check(hb.IsValid && ReferenceEquals(hb.Asset, simB), "loads by name without a manual catalog update");
            var simulated = new System.Collections.Generic.List<string>();
            ContentService.SnapshotSimulated(simulated);
            Check(simulated.Count == 1 && simulated[0] == "Icons/b", $"diagnostics: only slots that came through simulation are reported (n={simulated.Count})");
            hb.Dispose(); Tick();

            // The catalog comes first: a built name does not go through simulation
            var ha = ContentService.Acquire<Texture2D>("Icons/a");
            ContentService.SnapshotSimulated(simulated);
            Check(ha.IsValid && simulated.Count == 0, "a name that is in the catalog is not marked as simulated");
            ha.Dispose(); Tick();

            // Scenes: a scene missing from the catalog still opens via the editor path, and the returned Scene is picked by the requested path
            var newScene = new UnityEditor.SceneAsset { name = "New" };
            FakeEngine.AddProjectAsset("Assets/Sim/Scenes/New.unity", newScene);
            ContentLinkEditorUtility.Invalidate();
            SceneManager.Open(NewScene(77, "Base"));   // the engine cannot unload the last remaining scene
            Check(ContentService.ContainsScene("Scenes/New"), "ContainsScene is true for a scene name missing from the catalog (filled in lazily)");
            int scenesBefore = ContentService.LoadedSceneCount;
            var simScene = Run(ContentService.LoadSceneAsync("Scenes/New", LoadSceneMode.Additive));
            Check(simScene.IsValid() && simScene.path == "Assets/Sim/Scenes/New.unity",
                  $"the scene opens and the returned Scene matches the requested path (path={simScene.path})");
            Check(!simScene.loadableSceneId.IsValid && ContentService.LoadedSceneCount == scenesBefore + 1,
                  "a simulated scene has no LoadableSceneId, but it is still tracked so a scope can unload it");
            var unloadOp = ContentService.TryUnloadScene(simScene);
            FakeEngine.CompleteLoads();
            Check(unloadOp != null && ContentService.LoadedSceneCount == scenesBefore, "a simulated scene can be unloaded (it is tracked, so scopes can unload it too)");
            SceneManager.UnloadScene(SceneManager.GetSceneAt(0));

            // Toggle: switched off, it behaves like a shipped build
            ContentLinkSimulation.Enabled = false;
            Debug.Verbose = false;
            int errBefore = Debug.Errors;
            var hOff = ContentService.Acquire<Texture2D>("Icons/b");
            Debug.Verbose = true;
            Check(!ContentService.Contains("Icons/b") && !hOff.IsValid && Debug.Errors == errBefore + 1,
                  "with the toggle off, Contains is false and the usual error is logged");
            ContentLinkSimulation.Enabled = true;
            var hb2 = ContentService.Acquire<Texture2D>("Icons/b");   // one resolution to count in the summary
            hb2.Dispose(); Tick();

            // Leaving Play: a one-line summary, then the overlay is dropped (nothing persists)
            int logBefore = Debug.Logs;
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.ExitingPlayMode);
            Check(Debug.Logs == logBefore + 1 && Debug.LastLog.Contains("Icons/b") && Debug.LastLog.Contains("Build the content directory"),
                  $"one summary line on leaving Play (logs=+{Debug.Logs - logBefore})");
            Check(!ContentLinkSimulation.TryGetPath("Icons/b", out _, out _), "the overlay is dropped on leaving Play — nothing is saved");
            Check(projectCatalog.Entries.Count == 1 && projectCatalog.SourceRoot == "Assets/Sim",
                  "the catalog asset is untouched (automatic refresh never edits authored assets)");

            // A catalog with no known root is skipped with a warning
            var rootless = ScriptableObject.CreateInstance<ContentCatalog>();
            FakeEngine.AddProjectAsset("Assets/Generated/Rootless.asset", rootless);
            ContentLinkEditorUtility.Invalidate();
            Debug.Verbose = false;
            int w14 = Warn(() => ContentLinkSimulation.Rebuild());
            Debug.Verbose = true;
            Check(w14 == 1, $"a catalog with no known root is skipped after one warning (w={w14})");

            // Two files with the same name are both dropped with a warning (that name would fail to build anyway)
            var dupTex = new Texture2D { name = "dup" };
            var dupMat = new Object { name = "dup" };   // a different file, differing only by extension
            FakeEngine.AddProjectAsset("Assets/Sim/Icons/dup.png", dupTex);
            FakeEngine.AddProjectAsset("Assets/Sim/Icons/dup.mat", dupMat);
            ContentLinkEditorUtility.Invalidate();
            Debug.Verbose = false;
            int w15 = Warn(() => ContentLinkSimulation.Rebuild());
            Debug.Verbose = true;
            Check(w15 == 2 && !ContentLinkSimulation.TryGetPath("Icons/dup", out _, out _),
                  $"a duplicate name warns and resolves to neither file (w={w15})");

            Check(ContentService.UnregisterDirectory(dirSim), "cleanup: unregister SimDir");
            ContentLinkSimulation.Drop();
            FakeEngine.EditorServesAnyId = false;
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("22) build profiles, static view, validator");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();

            // Without a settings file, Main takes the legacy static defaults: flat and uncompressed
            var settings = ContentBuildSettings.instance;
            var mainProfile = settings.Active;
            Check(mainProfile.Name == "Main" && mainProfile.SourceRoot == ContentDirectoryBuilder.DefaultSourceRoot
                  && mainProfile.OutputPath == ContentDirectoryBuilder.DefaultOutputPath
                  && mainProfile.Catalogs[0].ResolvePath() == ContentDirectoryBuilder.DefaultCatalogPath
                  && mainProfile.Archive == ContentBuildSettings.ArchiveMode.None,
                  "a project without settings gets a Main profile with the legacy static defaults (flat, uncompressed)");

            // Static view and session overrides
            Check(ContentDirectoryBuilder.SourceRoot == "Assets/Content" && ContentDirectoryBuilder.CatalogPath == "Assets/ContentCatalog.asset"
                  && ContentDirectoryBuilder.OutputPath == "Assets/StreamingAssets/MainContent", "the static getters read the active profile");
            ContentDirectoryBuilder.SourceRoot = "Assets/Elsewhere";
            Check(ContentDirectoryBuilder.SourceRoot == "Assets/Elsewhere" && settings.Active.SourceRoot == "Assets/Content", "the setters are session overrides — the profile is unchanged");
            ContentDirectoryBuilder.ClearOverrides();
            Check(ContentDirectoryBuilder.SourceRoot == "Assets/Content", "overrides cleared");

            // Defaults of a new profile, unique names, switching the active profile
            var extra = settings.Add("Extra");
            Check(extra != null && extra.OutputPath == "Build/Content/Extra" && extra.Archive == ContentBuildSettings.ArchiveMode.Packed
                  && extra.MaxArchiveMB == 20 && extra.Compression == ContentBuildSettings.CompressionKind.LZ4
                  && extra.FailBuildWhenErrorsLogged && extra.Target == UnityEditor.BuildTarget.NoTarget
                  && extra.Catalogs[0].ResolvePath() == "Assets/ContentCatalogs/Extra.asset",
                  "a new profile gets the shipping defaults (Packed, 20 MB, LZ4, fail on errors, catalog outside the root)");
            Check(settings.Add("Extra") == null, "profile names are unique");
            Check(settings.SetActive("Extra") && settings.Active.Name == "Extra" && ContentDirectoryBuilder.OutputPath == "Build/Content/Extra",
                  "switching the active profile shows in the static view");
            Check(settings.Remove("Extra") && settings.Active.Name == "Main", "removing the active profile falls back to the first one");

            // Validator (runs before any build)
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            var vp = settings.Add("V"); vp.SourceRoot = "Assets/Content";
            vp.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/1.asset" },
                                  new ContentBuildSettings.CatalogEntry { SubFolder = "Icons/Sub", CreateAt = "Assets/Cat/2.asset" } };
            var errs = settings.Validate(vp);
            Check(errs.Count == 1 && errs[0].Contains("overlap"), $"validate: overlapping sub-folders (n={errs.Count})");
            vp.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "", CreateAt = "Assets/Content/Catalog.asset" } };
            errs = settings.Validate(vp);
            Check(errs.Count == 1 && errs[0].Contains("catalogue itself"), $"validate: catalog inside the root (n={errs.Count})");
            vp.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "", CreateAt = "Assets/Cat/1.asset" } };
            Check(settings.Validate(vp).Count == 0, "validate: clean profile");
            var v2 = settings.Add("V2"); v2.Name = "V";
            Check(settings.Validate(vp).Exists(e => e.Contains("used 2 times")), "validate: duplicate name");
            v2.Name = "V2";
            vp.Target = UnityEditor.BuildTarget.StandaloneWindows64;
            Check(settings.Validate(vp).Exists(e => e.Contains("active build target")), "validate: target mismatch");
            vp.Target = UnityEditor.BuildTarget.NoTarget;
            vp.SourceRoot = "Assets/Nope";
            Check(settings.Validate(vp).Exists(e => e.Contains("not a folder")), "validate: missing root");

            // The root of a profile that was never built is still part of the drawer's union of roots
            var lateTex = new Texture2D { name = "late" };
            FakeEngine.AddProjectAsset("Assets/Other/late.png", lateTex);
            var other = settings.Add("Other"); other.SourceRoot = "Assets/Other";
            ContentLinkEditorUtility.Invalidate();
            Check(ContentLinkEditorUtility.TryGetNameForAsset(lateTex, out var lateName2, out var lateWhy2) && lateName2 == "late",
                  $"a never-built profile's root still names assets ({lateName2 ?? lateWhy2})");

            // Scan and Write are separate; the parameterless UpdateCatalog composes the two
            var scanned = new System.Collections.Generic.List<ContentDirectoryBuilder.Scanned>();
            Check(ContentDirectoryBuilder.Scan("Assets/Content", scanned) && scanned.Count == 1
                  && scanned[0].Path == "Assets/Content/Icons/a.png" && scanned[0].Entry.name == "Icons/a",
                  $"Scan: yields the path together with the root-relative name (n={scanned.Count})");
            var written = ContentDirectoryBuilder.LoadOrCreateCatalog("Assets/Cat/1.asset");
            ContentDirectoryBuilder.Write(written, "Assets/Content", scanned);
            Check(written.Entries.Count == 1 && written.SourceRoot == "Assets/Content", "Write: records entries and root in the catalog");

            // Saving happens only on an explicit call
            int saves = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;
            settings.SaveSettings();
            Check(UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves + 1, "SaveSettings calls Save(true) — there is no auto-save");

            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("22b) a source root with a trailing separator (F-3)");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-f3-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });

            // typed into the window: stored normalised
            var model = new ContentBuildWindowModel();
            model.AddProfile("Slash");
            model.Edit(x => { x.SourceRoot = "Assets/Content/"; x.OutputPath = "Build\\Content\\Slash\\"; }, "paths");
            var ps = settings.Find("Slash");
            Check(ps.SourceRoot == "Assets/Content" && ps.OutputPath == "Build/Content/Slash", $"① the window model stores typed paths without trailing separators or backslashes (actual: '{ps.SourceRoot}' · '{ps.OutputPath}')");

            // the CLI override: the same
            var pc = settings.Add("Cli");
            ContentBuildCLI.Apply(pc, new System.Collections.Generic.Dictionary<string, string> { ["source"] = "Assets/Content/", ["output"] = "Build/Content/Cli/" }, out _);
            Check(pc.SourceRoot == "Assets/Content" && pc.OutputPath == "Build/Content/Cli", $"② a CLI override is stored normalised too (actual: '{pc.SourceRoot}' · '{pc.OutputPath}')");

            // a hand-edited settings file: the value is already stored with the separator
            var ph = settings.Add("Hand"); ph.SourceRoot = "Assets/Content/"; ph.OutputPath = System.IO.Path.Combine(tmp, "hand"); ph.Archive = ContentBuildSettings.ArchiveMode.None;
            ph.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/hand.asset" } };
            var verrs = settings.Validate(ph);
            Check(verrs.Exists(e => e.Contains("separator")), $"③ validation names the trailing separator of a stored source root, not 'is not a folder' (actual: [{string.Join(" | ", verrs)}])");
            Debug.Verbose = false;
            var uerrs = new System.Collections.Generic.List<string>();
            bool uok = ContentBuildRunner.UpdateCatalogs(ph, uerrs);
            Debug.Verbose = true;
            var written = FakeEngine.MainAssetAt.TryGetValue("Assets/Cat/hand.asset", out var wo) ? wo as ContentCatalog : null;
            var names = new System.Collections.Generic.List<string>(); if (written != null) foreach (var e in written.Entries) names.Add(e.name);
            bool maimed = names.Exists(n => !n.StartsWith("Icons/", StringComparison.Ordinal));
            Check(!maimed, $"④ whatever the outcome, no catalog is written with a maimed name (actual: ok={uok} names=[{string.Join(", ", names)}] errors=[{string.Join(" | ", uerrs)}])");
            Check(!uok || (written != null && written.SourceRoot == "Assets/Content"), $"⑤ if the catalog stage runs, the catalog records the root without the separator (actual: ok={uok} root='{written?.SourceRoot}')");

            // regression: the simulation match still normalises on its own
            var m = ContentLinkSimulation.MatchProfile("Build/Content/Slash/");
            Check(m != null && m.SourceRoot == "Assets/Content", "⑥ MatchProfile keeps matching the normalised output and root");

            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("22c) the output path and the source root must not contain each other (F-5)");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-f5-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            var p = settings.Add("Nest"); p.SourceRoot = "Assets/Content"; p.Archive = ContentBuildSettings.ArchiveMode.None;
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/nest.asset" } };
            System.Collections.Generic.List<string> V(string output) { p.OutputPath = output; return settings.Validate(p); }
            string Show(System.Collections.Generic.List<string> e) => e.Count == 0 ? "none" : string.Join(" | ", e);

            var e1 = V("Assets/Content/Build");
            Check(e1.Exists(e => e.Contains("inside the source root")), $"① a relative output under the source root is refused (actual: {Show(e1)})");
            var e2 = V(System.IO.Path.GetFullPath("Assets/Content/Build"));
            Check(e2.Exists(e => e.Contains("inside the source root")), $"② the same output written as an absolute path is refused too (actual: {Show(e2)})");
            var e3 = V("Assets");
            Check(e3.Exists(e => e.Contains("contains the source root")), $"③ an output that contains the source root is refused (actual: {Show(e3)})");
            var e4 = V("Assets/Content");
            var e4b = V("./Assets/Content/");
            Check(e4.Count > 0 && e4b.Count > 0, $"④ an output equal to the source root is refused, however it is spelled (actual: {Show(e4)} / {Show(e4b)})");
            var e5 = V("Assets/ContentBuild");
            Check(e5.Count == 0, $"⑤ a sibling whose name merely starts with the root's is fine (actual: {Show(e5)})");
            var e6 = V("Build/Content/Nest"); var e6b = V(System.IO.Path.Combine(tmp, "nest"));
            Check(e6.Count == 0 && e6b.Count == 0, $"⑥ a normal output, relative or absolute, is fine (actual: {Show(e6)} / {Show(e6b)})");

            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("23) build runner");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-p2-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");   // keep Main from writing into the harness working folder

            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            FakeEngine.AddProjectAsset("Assets/Content/Icons/b.png", new Texture2D { name = "b" });
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", new Object { name = "m" });

            var two = settings.Add("Two");
            two.SourceRoot = "Assets/Content"; two.OutputPath = System.IO.Path.Combine(tmp, "two"); two.Archive = ContentBuildSettings.ArchiveMode.None;
            two.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/icons.asset" },
                                   new ContentBuildSettings.CatalogEntry { SubFolder = "Materials", CreateAt = "Assets/Cat/materials.asset" } };
            var r = ContentBuildRunner.Build(two, showProgress: false);
            Check(r.Succeeded, "runner: two categories in a single build (" + string.Join(" | ", r.Errors) + ")");
            var lp = UnityEditor.BuildPipeline.LastParameters;
            Check(lp.rootAssetPaths.Length == 2 && lp.rootAssetPaths[0] == "Assets/Cat/icons.asset" && lp.rootAssetPaths[1] == "Assets/Cat/materials.asset"
                  && lp.name == "Two" && lp.compression.Kind == CompressionKindForStub.None && lp.options == UnityEditor.BuildContentOptions.FailBuildWhenErrorsLogged,
                  "parameters: two catalogs (path order), name = profile, flat is uncompressed, options follow the profile");
            var icons = (ContentCatalog)FakeEngine.MainAssetAt["Assets/Cat/icons.asset"];
            var mats = (ContentCatalog)FakeEngine.MainAssetAt["Assets/Cat/materials.asset"];
            Check(icons.Entries.Count == 2 && icons.Entries[0].name == "Icons/a" && icons.Entries[1].name == "Icons/b" && icons.SourceRoot == "Assets/Content",
                  "split: the Icons catalog has two root-relative names, and its recorded root is the profile root");
            Check(mats.Entries.Count == 1 && mats.Entries[0].name == "Materials/m", "split: the Materials catalog has one");
            Check(two.Catalogs[0].Catalog.isSet && two.Catalogs[0].ResolvePath() == "Assets/Cat/icons.asset", "the first build creates the catalog and sets the reference");
            Check(r.Manifest != null && r.Manifest.RootAssets.Length == 2 && r.BuiltNames.Count == 3 && r.BuiltNames[0] == "Icons/a" && r.Manifest.ContentFileCount() == 3,
                  $"result: two manifest roots, three built names (sorted) — no file mapping, since the cfid is not the on-disk name (names={r.BuiltNames.Count})");
            Check(r.Files.Length == 5 && r.Archives.Length == 0 && string.CompareOrdinal(r.Files[0], r.Files[1]) < 0,
                  $"result: sorted file list (3 cf + json + hash = {r.Files.Length}), no archives");
            Check(UnityEditor.SessionState.GetString(ContentBuildRunner.SessionKey + ".profile", "") == "Two"
                  && UnityEditor.SessionState.GetBool(ContentBuildRunner.SessionKey + ".ok", false)
                  && UnityEditor.SessionState.GetInt(ContentBuildRunner.SessionKey + ".files", 0) == 5, "last result in SessionState (settings file unchanged)");

            // Archive-mode rules, a pure function
            var eng = settings.Add("Eng"); eng.Archive = ContentBuildSettings.ArchiveMode.Engine; eng.Compression = ContentBuildSettings.CompressionKind.LZMA;
            eng.DisableWriteTypeTree = true; eng.CleanBuildCache = true;
            var pe = ContentBuildRunner.Parameters(eng, new[] { "x" });
            Check((pe.options & UnityEditor.BuildContentOptions.UseArchive) != 0 && pe.compression.Kind == CompressionKindForStub.LZMA, "Engine mode: UseArchive + the profile's compression");
            Check((pe.options & UnityEditor.BuildContentOptions.DisableWriteTypeTree) != 0 && (pe.options & UnityEditor.BuildContentOptions.SerializeUnityVersion) != 0
                  && (pe.options & UnityEditor.BuildContentOptions.CleanBuildCache) != 0, "DisableWriteTypeTree brings SerializeUnityVersion with it");
            var pk = settings.Add("Pk"); pk.Compression = ContentBuildSettings.CompressionKind.LZ4;   // Archive defaults to Packed
            var pp = ContentBuildRunner.Parameters(pk, new[] { "x" });
            Check((pp.options & UnityEditor.BuildContentOptions.UseArchive) == 0 && pp.compression.Kind == CompressionKindForStub.None,
                  "Packed mode: the engine build is flat and uncompressed — the packer applies compression (passing compression to the engine would force an archive)");

            // Output folder safeguard
            var stray = System.IO.Path.Combine(tmp, "stray"); System.IO.Directory.CreateDirectory(stray);
            System.IO.File.WriteAllText(System.IO.Path.Combine(stray, "keep.txt"), "mine");
            var perr = new System.Collections.Generic.List<string>();
            Check(!ContentBuildRunner.PrepareOutput(stray, perr) && System.IO.File.Exists(System.IO.Path.Combine(stray, "keep.txt")) && perr[0].Contains("refusing"),
                  "neither a previous build nor an empty folder → refused and left untouched");
            Check(ContentBuildRunner.PrepareOutput(two.OutputPath, perr) && System.IO.Directory.GetFileSystemEntries(two.OutputPath).Length == 0, "a previous content build is emptied");

            // Warning for assets outside every category; failures; BuildAll stops at the first failure
            settings.Remove("Eng"); settings.Remove("Pk");
            var only = settings.Add("OnlyIcons"); only.SourceRoot = "Assets/Content"; only.OutputPath = System.IO.Path.Combine(tmp, "only"); only.Archive = ContentBuildSettings.ArchiveMode.None;
            only.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/only.asset" } };
            int w16 = Warn(() => ContentBuildRunner.Build(only, false));
            Check(w16 == 1, $"an asset outside every category (Materials/m) warns once (w={w16})");
            var bad = settings.Add("Bad"); bad.SourceRoot = "Assets/Nope";
            var after = settings.Add("After"); after.SourceRoot = "Assets/Content"; after.OutputPath = System.IO.Path.Combine(tmp, "after"); after.Archive = ContentBuildSettings.ArchiveMode.None;
            after.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/after.asset" } };
            Debug.Verbose = false;
            int errBefore2 = Debug.Errors;
            var all = ContentBuildRunner.BuildAll(false);
            Debug.Verbose = true;
            Check(all.Count == 4 && all[0].Succeeded && all[1].Succeeded && all[2].Succeeded && !all[3].Succeeded && all[3].ProfileName == "Bad" && Debug.Errors == errBefore2 + 1,
                  $"BuildAll: Main, Two and OnlyIcons succeed, then it stops at Bad — After is not built (n={all.Count})");
            UnityEditor.BuildPipeline.FailNextBuild = true;
            Debug.Verbose = false; var rf = ContentBuildRunner.Build(two, false); Debug.Verbose = true;
            Check(!rf.Succeeded && rf.Errors.Exists(e => e.Contains("Failed")) && !UnityEditor.SessionState.GetBool(ContentBuildRunner.SessionKey + ".ok", true),
                  "an engine failure is recorded as failed in the result and in SessionState");

            System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("23b) fresh profile with the default catalog path — the folder does not exist yet (F-1)");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject();   // deliberately without Assets/ContentCatalogs: a fresh project
            ContentLinkEditorUtility.Invalidate();
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-f1-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            string Errs(ContentBuildResult x) => x == null ? "" : string.Join(" | ", x.Errors);

            var fresh = settings.Add("Fresh");   // CreateAt = Assets/ContentCatalogs/Fresh.asset; the folder is not in the project
            fresh.SourceRoot = "Assets/Content"; fresh.OutputPath = System.IO.Path.Combine(tmp, "fresh"); fresh.Archive = ContentBuildSettings.ArchiveMode.None;
            var createAt = fresh.Catalogs[0].CreateAt;
            Check(createAt == "Assets/ContentCatalogs/Fresh.asset" && !UnityEditor.AssetDatabase.IsValidFolder("Assets/ContentCatalogs"), "precondition: the default catalog path's folder does not exist");

            Debug.Verbose = false;
            var verrs = settings.Validate(fresh);
            string ex = null; ContentBuildResult rf = null;
            try { rf = ContentBuildRunner.Build(fresh, showProgress: false); } catch (Exception e) { ex = e.GetType().Name + ": " + e.Message; }
            Debug.Verbose = true;
            Check(ex == null && rf != null && rf.Succeeded, $"① the first build of a fresh profile succeeds — no exception, no failure (actual: exception={ex ?? "none"} succeeded={rf?.Succeeded} errors={Errs(rf)})");
            var made = UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>(createAt);
            Check(UnityEditor.AssetDatabase.IsValidFolder("Assets/ContentCatalogs") && made != null, "② the catalog asset exists at the default path, in a folder the build made");
            Check(fresh.Catalogs[0].Catalog.isSet && fresh.Catalogs[0].ResolvePath() == createAt, "③ the profile now references the created catalog");
            Check(verrs.Count == 0, $"④ validation does not block a missing catalog folder — the build makes it (actual: {string.Join(" | ", verrs)})");

            // the catalogs-only entry point (menu · CLI -c), two missing segments
            var fresh2 = settings.Add("Fresh2"); fresh2.SourceRoot = "Assets/Content"; fresh2.OutputPath = System.IO.Path.Combine(tmp, "fresh2"); fresh2.Archive = ContentBuildSettings.ArchiveMode.None;
            fresh2.Catalogs[0].CreateAt = "Assets/ContentCatalogs/Deep/Fresh2.asset";
            var uerrs = new System.Collections.Generic.List<string>(); var upaths = new System.Collections.Generic.List<string>();
            string uex = null; bool uok = false;
            Debug.Verbose = false;
            try { uok = ContentBuildRunner.UpdateCatalogs(fresh2, uerrs, upaths); } catch (Exception e) { uex = e.GetType().Name; }
            Debug.Verbose = true;
            Check(uex == null && uok && UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>("Assets/ContentCatalogs/Deep/Fresh2.asset") != null,
                  $"⑤ UpdateCatalogs alone creates a two-level missing folder and the catalog (actual: exception={uex ?? "none"} ok={uok} errors={string.Join(" | ", uerrs)})");

            // a catalog path outside Assets/ can never be created: a reason, never an exception
            var outside = settings.Add("Outside"); outside.SourceRoot = "Assets/Content"; outside.OutputPath = System.IO.Path.Combine(tmp, "outside"); outside.Archive = ContentBuildSettings.ArchiveMode.None;
            outside.Catalogs[0].CreateAt = "Build/Cat/outside.asset";
            Debug.Verbose = false;
            string oex = null; ContentBuildResult ro = null;
            try { ro = ContentBuildRunner.Build(outside, showProgress: false); } catch (Exception e) { oex = e.GetType().Name; }
            Debug.Verbose = true;
            Check(oex == null && ro != null && !ro.Succeeded && ro.Errors.Count > 0, $"⑥ a catalog path outside Assets/ fails with a reason, not an exception (actual: exception={oex ?? "none"} succeeded={ro?.Succeeded} errors={Errs(ro)})");

            // regression guard: an existing catalog is reused, the reference survives
            Debug.Verbose = false;
            string aex = null; ContentBuildResult again = null;
            try { again = ContentBuildRunner.Build(fresh, showProgress: false); } catch (Exception e) { aex = e.GetType().Name; }
            Debug.Verbose = true;
            Check(aex == null && again != null && again.Succeeded && made != null && ReferenceEquals(made, UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>(createAt)) && fresh.Catalogs[0].ResolvePath() == createAt,
                  $"⑦ the second build reuses the catalog asset and keeps the reference (actual: exception={aex ?? "none"} succeeded={again?.Succeeded})");

            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("23c) a build that fails before the engine runs leaves the previous output alone (F-4)");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-f4-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            string Errs(ContentBuildResult x) => x == null ? "" : string.Join(" | ", x.Errors);
            string Hash(ContentBuildSettings.Profile x) => System.IO.Path.Combine(x.OutputPath, ContentBuildManifest.HashFile);

            var p = settings.Add("Keep"); p.SourceRoot = "Assets/Content"; p.OutputPath = System.IO.Path.Combine(tmp, "keep"); p.Archive = ContentBuildSettings.ArchiveMode.None;
            p.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/keep.asset" } };
            var k1 = ContentBuildRunner.Build(p, showProgress: false);
            int files1 = System.IO.Directory.GetFileSystemEntries(p.OutputPath).Length;
            Check(k1.Succeeded && System.IO.File.Exists(Hash(p)), $"① first build succeeds and leaves a manifest ({files1} files)");

            // a duplicate name appears: the catalog stage fails before the engine runs
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.jpg", new Texture2D { name = "a2" });   // 'Icons/a' twice
            Debug.Verbose = false;
            var k2 = ContentBuildRunner.Build(p, showProgress: false);
            Debug.Verbose = true;
            Check(!k2.Succeeded && k2.Errors.Exists(e => e.Contains("duplicate")) && System.IO.File.Exists(Hash(p)) && System.IO.Directory.GetFileSystemEntries(p.OutputPath).Length == files1,
                  $"② a catalog-stage failure leaves the previous output usable (actual: succeeded={k2.Succeeded} manifest={System.IO.File.Exists(Hash(p))} files={System.IO.Directory.GetFileSystemEntries(p.OutputPath).Length} errors={Errs(k2)})");

            // fixed: the next build still clears the previous build's files before the engine writes
            FakeEngine.MainAssetAt.Remove("Assets/Content/Icons/a.jpg");
            if (!System.IO.File.Exists(Hash(p))) { Debug.Verbose = false; ContentBuildRunner.Build(p, showProgress: false); Debug.Verbose = true; }   // recover: on current code ② wiped the output, so restore a previous build for ③ to clear
            System.IO.File.WriteAllText(System.IO.Path.Combine(p.OutputPath, "stale.cf"), "left over from the previous build");
            var k3 = ContentBuildRunner.Build(p, showProgress: false);
            Check(k3.Succeeded && !System.IO.File.Exists(System.IO.Path.Combine(p.OutputPath, "stale.cf")) && System.IO.File.Exists(Hash(p)),
                  $"③ once fixed, the rebuild clears the previous build's files first and succeeds (actual: succeeded={k3.Succeeded} stale={System.IO.File.Exists(System.IO.Path.Combine(p.OutputPath, "stale.cf"))})");

            // a stranger output folder: refused before anything is written — the catalog included
            var q = settings.Add("Stranger"); q.SourceRoot = "Assets/Content"; q.OutputPath = System.IO.Path.Combine(tmp, "stranger"); q.Archive = ContentBuildSettings.ArchiveMode.None;
            q.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/stranger.asset" } };
            System.IO.Directory.CreateDirectory(q.OutputPath); System.IO.File.WriteAllText(System.IO.Path.Combine(q.OutputPath, "keep.txt"), "mine");
            Debug.Verbose = false;
            var kq = ContentBuildRunner.Build(q, showProgress: false);
            Debug.Verbose = true;
            Check(!kq.Succeeded && kq.Errors.Exists(e => e.Contains("refusing")) && System.IO.File.Exists(System.IO.Path.Combine(q.OutputPath, "keep.txt")), "④ a stranger output folder is refused and left untouched");
            Check(!FakeEngine.MainAssetAt.ContainsKey("Assets/Cat/stranger.asset"), $"⑤ a refused output folder writes no catalog — the guard runs before anything is written (actual: catalog written={FakeEngine.MainAssetAt.ContainsKey("Assets/Cat/stranger.asset")})");

            // inside Assets/: a catalog-stage failure touches nothing, so nothing is refreshed
            var dataPath = Application.dataPath;
            Application.dataPath = System.IO.Path.Combine(tmp, "Assets");
            var ia = settings.Add("InAssets"); ia.SourceRoot = "Assets/Content"; ia.OutputPath = System.IO.Path.Combine(Application.dataPath, "Out"); ia.Archive = ContentBuildSettings.ArchiveMode.None;
            ia.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/inassets.asset" } };
            var ki = ContentBuildRunner.Build(ia, showProgress: false);
            int refreshes = UnityEditor.AssetDatabase.RefreshCalls;
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.jpg", new Texture2D { name = "a2" });
            Debug.Verbose = false;
            var ki2 = ContentBuildRunner.Build(ia, showProgress: false);
            Debug.Verbose = true;
            FakeEngine.MainAssetAt.Remove("Assets/Content/Icons/a.jpg");
            Application.dataPath = dataPath;
            Check(ki.Succeeded && !ki2.Succeeded && UnityEditor.AssetDatabase.RefreshCalls == refreshes && System.IO.File.Exists(Hash(ia)),
                  $"⑥ inside Assets/: a catalog-stage failure neither clears nor refreshes (actual: refreshes=+{UnityEditor.AssetDatabase.RefreshCalls - refreshes} manifest={System.IO.File.Exists(Hash(ia))})");

            // regression: a validation failure never touches the output; an engine failure comes after the clear
            p.SourceRoot = "Assets/Nope";
            Debug.Verbose = false; var kv = ContentBuildRunner.Build(p, showProgress: false); Debug.Verbose = true;
            p.SourceRoot = "Assets/Content";
            Check(!kv.Succeeded && System.IO.File.Exists(Hash(p)), "⑦ a validation failure leaves the output alone (as before)");
            UnityEditor.BuildPipeline.FailNextBuild = true;
            Debug.Verbose = false; var ke = ContentBuildRunner.Build(p, showProgress: false); Debug.Verbose = true;
            Check(!ke.Succeeded && ke.Errors.Exists(e => e.Contains("Failed")) && !System.IO.File.Exists(Hash(p)),
                  "⑧ an engine failure is reported as before, and the output had been cleared for it (the clear still precedes the engine)");
            UnityEditor.BuildPipeline.FailNextBuild = false;   // never leak into the next scenario

            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("24) packing, post-processing, packed directory registration");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-p3-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");

            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            FakeEngine.AddProjectAsset("Assets/Content/Icons/b.png", new Texture2D { name = "b" });
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", new Object { name = "m" });

            // Archive assignment: the hash-prefix trie, a pure function of (files, sizes, limit)
            {
                var sizes = new System.Collections.Generic.Dictionary<string, long> { ["c.cf"] = 3, ["a.cf"] = 4, ["b.cf"] = 5, ["big.resS"] = 20, ["BuildManifestHash.txt"] = 1, ["abc123.json"] = 2 };
                var manifestFiles = new[] { "BuildManifestHash.txt", "abc123.json" };
                ArchivePartitionInput In(long limit, params string[] files) => new ArchivePartitionInput { ProfileName = "T", LimitBytes = limit, Files = files, Sizes = sizes, ManifestFiles = manifestFiles };
                string Files(ArchiveGroup g) => string.Join(",", g.Files);
                var trie = new ContentArchivePacker.HashTriePartitioner();
                var g = trie.Partition(In(8, "c.cf", "big.resS", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"));
                // MD5 first bits: a.cf → 1, b.cf → 0, c.cf → 0; a+b+c = 12 > 8 splits once; {b,c} = 8 fits; big.resS > 8 goes alone
                Check(g.Count == 4 && g[0].Name == "T.manifest-" + ArchiveNaming.ListHash(new[] { "BuildManifestHash.txt", "abc123.json" }) + ".archive" && Files(g[0]) == "BuildManifestHash.txt,abc123.json"
                      && g[1].Name == "T.0-" + ArchiveNaming.ListHash(new[] { "b.cf", "c.cf" }) + ".archive" && Files(g[1]) == "b.cf,c.cf"
                      && g[2].Name == "T.1-" + ArchiveNaming.ListHash(new[] { "a.cf" }) + ".archive" && Files(g[2]) == "a.cf"
                      && g[3].Name == "T.big-big-" + ArchiveNaming.ListHash(new[] { "big.resS" }) + ".archive" && Files(g[3]) == "big.resS",
                      $"trie: manifest first (limit exempt), the rest split on the first MD5 bit until a bucket fits, an oversized file alone; names = label + list hash ({string.Join(" ", System.Linq.Enumerable.Select(g, x => x.Name))})");
                var shuffled = trie.Partition(In(8, "b.cf", "BuildManifestHash.txt", "a.cf", "big.resS", "abc123.json", "c.cf"));
                Check(ArchivePartitionValidator.Same(g, shuffled), "trie: the input order does not matter");
                sizes["d.cf"] = 3;
                var g2 = trie.Partition(In(8, "d.cf", "big.resS", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"));   // c.cf modified → renamed d.cf (bit 1)
                int changed = 0; foreach (var x in g2) { bool kept = false; foreach (var y in g) if (y.Name == x.Name) kept = true; if (!kept) ++changed; }
                Check(g2.Count == 4 && changed == 2 && Files(g2[1]) == "b.cf" && Files(g2[2]) == "a.cf,d.cf" && g2[0].Name == g[0].Name && g2[3].Name == g[3].Name,
                      $"locality: renaming one file touches its old leaf and its new leaf only — manifest and big archives keep their names (changed={changed})");
                var tiny = trie.Partition(In(2, "c.cf", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"));
                Check(tiny.Count == 4 && Files(tiny[0]) == "BuildManifestHash.txt,abc123.json" && tiny[1].Name.StartsWith("T.big-a-") && tiny[2].Name.StartsWith("T.big-b-") && tiny[3].Name.StartsWith("T.big-c-"),
                      "limit below everything: the manifest archive is exempt and stays whole; each other file is a big archive of its own");
                sizes["big.cf"] = 20;
                var clash = trie.Partition(In(8, "big.resS", "big.cf", "abc123.json", "BuildManifestHash.txt"));
                Check(clash.Count == 3 && clash[1].Name.StartsWith("T.big-big_cf-") && clash[2].Name.StartsWith("T.big-big_resS-"),
                      "two oversized files whose first eight characters clash get the whole alias as label");
                var only = trie.Partition(In(8, "abc123.json", "BuildManifestHash.txt"));
                Check(only.Count == 1 && Files(only[0]) == "BuildManifestHash.txt,abc123.json", "no content files: the manifest archive alone, no empty archive");
                var seq = new ContentArchivePacker.SequentialFillPartitioner().Partition(In(8, "c.cf", "big.resS", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"));
                Check(seq.Count == 4 && Files(seq[0]) == "BuildManifestHash.txt,abc123.json" && seq[1].Name.StartsWith("T.seq-") && Files(seq[1]) == "a.cf" && Files(seq[2]) == "b.cf,c.cf" && seq[3].Name.StartsWith("T.big-big-"),
                      "sequential fill: sorted, closed when the next file would exceed the limit, named by list hash");
                var errors = new System.Collections.Generic.List<string>(); var warnings = new System.Collections.Generic.List<string>();
                Check(ArchivePartitionValidator.Validate(In(8, "c.cf", "big.resS", "a.cf", "abc123.json", "b.cf", "BuildManifestHash.txt"), g, errors, warnings) && errors.Count == 0 && warnings.Count == 0, "validator: the trie's own answer passes clean");
            }

            // Packed build: flat build → assignment → ArchiveAndCompress → delete loose files → archives.json
            var pk2 = settings.Add("Pk2");
            pk2.SourceRoot = "Assets/Content"; pk2.OutputPath = System.IO.Path.Combine(tmp, "pk2");
            pk2.Archive = ContentBuildSettings.ArchiveMode.Packed; pk2.Compression = ContentBuildSettings.CompressionKind.LZ4; pk2.CleanBuildCache = true;
            pk2.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/icons.asset" },
                                   new ContentBuildSettings.CatalogEntry { SubFolder = "Materials", CreateAt = "Assets/Cat/materials.asset" } };
            ContentArchivePacker.TestLimitBytes = 100;   // stub output: hash 6 bytes, manifest a few hundred bytes (limit exempt), cf 2 bytes ×3 → [manifest] [all]
            var r = ContentBuildRunner.Build(pk2, showProgress: false);
            Check(r.Succeeded, "packed build succeeds (" + string.Join(" | ", r.Errors) + ")");
            Check(r.Archives.Length == 2 && r.Archives[0].StartsWith("Pk2.manifest-") && r.Archives[1].StartsWith("Pk2.all-") && r.Archives[1].EndsWith(".archive")
                  && string.Join(",", r.Files) == "BuildManifestHash.txt," + r.Archives[1] + "," + r.Archives[0] + ",archives.json",
                  $"output: the manifest archive + one 'all' archive + hash file + archives.json — loose files are gone (files={string.Join(",", r.Files)})");
            var index = ContentArchives.ReadIndex(pk2.OutputPath);
            Check(index != null && index.Version == 1 && index.Profile == "Pk2" && index.Compression == "LZ4" && index.MaxArchiveMB == 20 && index.Archives.Length == 2
                  && index.Archives[0].File == r.Archives[0] && index.Archives[0].Files.Length == 2 && index.Archives[0].Files[0] == "BuildManifestHash.txt" && index.Archives[0].Files[1] == "abc123.json" && index.Archives[0].InputBytes > 100
                  && index.Archives[1].Files.Length == 3 && index.Archives[1].InputBytes == 6
                  && index.Archives[0].Bytes > 0 && index.Archives[0].Crc.Length == 8,
                  "archives.json: the manifest and its hash file first in their own archive (over the limit, exempt), the three cf files in one archive, with size, input size and CRC recorded");
            Check(FakeEngine.Packed.Count == 2 && FakeEngine.Packed[0].compression == CompressionKindForStub.LZ4 && FakeEngine.Packed[0].path.EndsWith(r.Archives[0])
                  && FakeEngine.Packed[1].aliases.Length == 3 && FakeEngine.Packed[1].aliases[0].EndsWith(".cf"),
                  "the packer calls ArchiveAndCompress with the profile's compression and the aliases are relative names");
            Check(r.ManifestPath == "" && r.Manifest != null && r.BuiltNames.Count == 3, "result: the manifest path is empty (it is inside an archive) but the parsed manifest and built names remain");
            Check(r.Loadables.Count == 3 && r.Loadables[0].Catalog == "Assets/Cat/icons.asset" && r.Loadables[0].Name == "Icons/a" && r.Loadables[0].File != null && r.Loadables[0].File.EndsWith(".cf")
                  && r.Loadables[2].Catalog == "Assets/Cat/materials.asset" && r.Manifest.SerializedFiles[1].ContentHash + ".cf" == r.Loadables[0].File || r.Manifest.SerializedFiles[2].ContentHash + ".cf" == r.Loadables[0].File,
                  $"result.Loadables: every built entry with its catalog and, via the manifest's ContentHash, its file on disk ({r.Loadables.Count})");

            // Determinism: a second clean build produces the same list, bytes and archives.json
            var first = new System.Collections.Generic.Dictionary<string, byte[]>();
            foreach (var a in r.Archives) first[a] = System.IO.File.ReadAllBytes(System.IO.Path.Combine(pk2.OutputPath, a));
            var firstIndex = System.IO.File.ReadAllText(System.IO.Path.Combine(pk2.OutputPath, ContentArchives.IndexFile));
            var again = ContentBuildRunner.Build(pk2, showProgress: false);
            bool same = again.Succeeded && again.Archives.Length == r.Archives.Length;
            foreach (var a in again.Archives) same &= first.TryGetValue(a, out var bytes) && SameBytes(bytes, System.IO.File.ReadAllBytes(System.IO.Path.Combine(pk2.OutputPath, a)));
            Check(same && firstIndex == System.IO.File.ReadAllText(System.IO.Path.Combine(pk2.OutputPath, ContentArchives.IndexFile)),
                  "determinism: the second clean build's archive list, bytes and archives.json equal the first");

            // Runtime: archives.json → mount everything into one namespace → register the mount path as the directory → unregistering unmounts
            var ns = ContentArchives.NamespaceFor(pk2.OutputPath);
            var mountPath = "ns:/" + ns + "/" + ContentArchives.MountPrefix + "/";
            FakeEngine.Dirs[mountPath] = new ScriptableObject[] { (ContentCatalog)FakeEngine.MainAssetAt["Assets/Cat/icons.asset"], (ContentCatalog)FakeEngine.MainAssetAt["Assets/Cat/materials.asset"] };
            Check(ns.Length == 16 && ns.StartsWith("cl") && ContentArchives.IsOurNamespace(ns) && ContentArchives.NamespaceFor(tmp) != ns && !ContentArchives.IsOurNamespace("clprobe"),
                  "namespace: a 16-char alphanumeric name derived from the path — a different path gives a different name");
            int frame0 = FakeEngine.Frame;
            var reg = ContentService.RegisterPackedDirectoryAsync(pk2.OutputPath);
            Check(reg.IsCompleted && reg.GetAwaiter().GetResult() == 0 && FakeEngine.Frame > frame0, "registration waits frames while the mount is InProgress, then registers");
            var space = Unity.Content.ContentNamespace.GetOrCreateNamespace(ns);
            Check(ContentService.Contains("Icons/a") && ContentService.Contains("Materials/m") && FakeEngine.Registered.Contains(mountPath)
                  && Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(space).Length == 2, "after registration: both categories' names are indexed, the registered path is the mount path, both archives are mounted");
            Check(ContentService.UnregisterDirectory(0) && Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(space).Length == 0 && !FakeEngine.Registered.Contains(mountPath),
                  "unregistration: unregistering the directory unmounts the archives");

            // Mount failure: names the archive, unmounts what was mounted, registers nothing
            FakeEngine.FailMount.Add(index.Archives[1].File);
            string failMsg = "";
            try { ContentService.RegisterPackedDirectoryAsync(pk2.OutputPath).GetAwaiter().GetResult(); } catch (InvalidOperationException e) { failMsg = e.Message; }
            FakeEngine.FailMount.Clear();
            Check(failMsg.Contains("'" + index.Archives[1].File + "'") && failMsg.Contains("Failed") && Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(space).Length == 0 && !FakeEngine.Registered.Contains(mountPath),
                  $"mount failure: the exception names the archive, mounted ones are unmounted, nothing registered ({failMsg})");

            // Missing index or unknown version → -1 and an error log
            var bogus = System.IO.Path.Combine(tmp, "bogus"); System.IO.Directory.CreateDirectory(bogus);
            System.IO.File.WriteAllText(System.IO.Path.Combine(bogus, ContentArchives.IndexFile), "{\"Version\":7}");
            Debug.Verbose = false; int errBefore = Debug.Errors;
            int none = ContentService.RegisterPackedDirectoryAsync(settings.Active.OutputPath).GetAwaiter().GetResult();
            int ver = ContentService.RegisterPackedDirectoryAsync(bogus).GetAwaiter().GetResult();
            Debug.Verbose = true;
            Check(none == -1 && ver == -1 && Debug.Errors == errBefore + 3 && FakeEngine.Registered.Count == 0, $"missing archives.json or unknown version → -1 and an error log (errors=+{Debug.Errors - errBefore})");

            // Leftover cleanup: ResetStatics unmounts the previous session's archives after unregistering
            ContentService.RegisterPackedDirectoryAsync(pk2.OutputPath).GetAwaiter().GetResult();
            Check(Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(space).Length == 2 && FakeEngine.Registered.Contains(mountPath), "re-register: two mounted, registered");
            Init(typeof(ContentService), "ResetStatics");
            Check(Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(space).Length == 0 && !FakeEngine.Registered.Contains(mountPath),
                  "ResetStatics: releases the previous session's registrations and unmounts the archives in our namespace");

            // Partitioner selection and the packer's guards (D12): the profile names the type; the validator and the purity check refuse a bad answer
            Debug.Verbose = false;
            pk2.Partitioner = ContentBuildWindowModel.NameOf(typeof(ContentArchivePacker.SequentialFillPartitioner));
            var rs = ContentBuildRunner.Build(pk2, showProgress: false);
            var seqIndex = ContentArchives.ReadIndex(pk2.OutputPath);
            string Errs(ContentBuildResult x) => string.Join(" | ", x.Errors);
            Check(rs.Succeeded && seqIndex.Archives.Length == 2 && seqIndex.Archives[0].File.StartsWith("Pk2.manifest-") && seqIndex.Archives[1].File.StartsWith("Pk2.seq-") && seqIndex.Archives[1].Files.Length == 3,
                  $"a profile that names SequentialFillPartitioner packs sequentially ({string.Join(" ", System.Linq.Enumerable.Select(seqIndex.Archives, a => a.File))})");
            pk2.Partitioner = "Nope.Part";
            var rNope = ContentBuildRunner.Build(pk2, showProgress: false);
            pk2.Partitioner = typeof(object).AssemblyQualifiedName;
            var rObj = ContentBuildRunner.Build(pk2, showProgress: false);
            Check(!rNope.Succeeded && Errs(rNope).Contains("partitioner 'Nope.Part' was not found") && !rObj.Succeeded && Errs(rObj).Contains("does not implement IContentArchivePartitioner"),
                  $"an unknown partitioner type fails validation; a type without the interface fails the pack ({Errs(rNope)} / {Errs(rObj)})");
            var rejected = new System.Collections.Generic.List<string>();
            foreach (var (type, expect) in new[] { (typeof(PartDrop), "is not assigned to any archive"), (typeof(PartDup), "is assigned more than once"), (typeof(PartEmpty), "has no files"),
                                                   (typeof(PartCase), "is used twice"), (typeof(PartSlash), "is not a plain file name"), (typeof(PartOrder), "is not a pure function") })
            {
                pk2.Partitioner = type.AssemblyQualifiedName;
                var rx = ContentBuildRunner.Build(pk2, showProgress: false);
                if (rx.Succeeded || !Errs(rx).Contains(expect)) rejected.Add($"{type.Name}: {(rx.Succeeded ? "succeeded" : Errs(rx))}");
                else if (System.IO.Directory.GetFiles(pk2.OutputPath, "*.archive").Length != 0) rejected.Add($"{type.Name}: archives were written before the check");
            }
            Check(rejected.Count == 0, "validator: a dropped file, a duplicate, an empty archive, a case-only name clash, a path in a name and an order-dependent answer each fail the build before any archive is written" + (rejected.Count == 0 ? "" : " — " + string.Join("; ", rejected)));
            int warnBefore = Debug.Warnings;
            pk2.Partitioner = typeof(PartSplitManifest).AssemblyQualifiedName;
            var rSplit = ContentBuildRunner.Build(pk2, showProgress: false);
            Check(rSplit.Succeeded && Debug.Warnings == warnBefore + 2 && Debug.LastWarning.Contains("different archives") && rSplit.Archives.Length == 2,
                  $"the manifest and its hash file in different archives is allowed (they still mount into one folder) and warned about — as is the multi-file archive over the limit (warnings=+{Debug.Warnings - warnBefore}: {Errs(rSplit)})");
            pk2.Partitioner = "";
            Debug.Verbose = true;

            // Post-processors: run in configured order; a missing type, a type without the interface, or an exception fails the build (output stays)
            PpCalls.Clear();
            var post = settings.Add("Post"); post.SourceRoot = "Assets/Content"; post.OutputPath = System.IO.Path.Combine(tmp, "post"); post.Archive = ContentBuildSettings.ArchiveMode.None;
            post.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/post.asset" } };
            post.PostProcessors = new[] { typeof(PpB).AssemblyQualifiedName, typeof(PpA).FullName };
            var rp = ContentBuildRunner.Build(post, showProgress: false);
            Check(rp.Succeeded && string.Join(",", PpCalls) == "B:5:ok,A:5:ok", $"post-process: configured order (B then A), each sees the finished result (5 files, success) ({string.Join(",", PpCalls)})");
            Debug.Verbose = false;
            post.PostProcessors = new[] { "Nope.Missing" };
            var rm = ContentBuildRunner.Build(post, showProgress: false);
            post.PostProcessors = new[] { typeof(object).AssemblyQualifiedName };
            var ri = ContentBuildRunner.Build(post, showProgress: false);
            post.PostProcessors = new[] { typeof(PpThrow).FullName };
            var rt = ContentBuildRunner.Build(post, showProgress: false);
            Debug.Verbose = true;
            Check(!rm.Succeeded && rm.Errors.Exists(e => e.Contains("'Nope.Missing' was not found")) && System.IO.File.Exists(System.IO.Path.Combine(post.OutputPath, "BuildManifestHash.txt")),
                  "post-process: a missing type fails — output stays");
            Check(!ri.Succeeded && ri.Errors.Exists(e => e.Contains("does not implement")), "post-process: a type that does not implement the interface fails");
            Check(!rt.Succeeded && rt.Errors.Exists(e => e.Contains("threw InvalidOperationException: boom")) && !UnityEditor.SessionState.GetBool(ContentBuildRunner.SessionKey + ".ok", true),
                  "post-process: an exception is recorded as failure in the result and SessionState");

            ContentArchivePacker.TestLimitBytes = null;
            System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear(); FakeEngine.Packed.Clear(); FakeEngine.Dirs.Remove(mountPath);
            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("24b) the session reset leaves other systems' namespaces alone (F-9)");
        {
            FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear(); FakeEngine.MountFrames = 0;
            Init(typeof(ContentService), "ResetStatics");
            var tmp9 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-f9-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tmp9);
            var file9 = System.IO.Path.Combine(tmp9, "some.archive"); System.IO.File.WriteAllBytes(file9, new byte[] { 1, 2, 3 });

            // the shape: what NamespaceFor makes is ours; a name that merely is 16 characters starting with 'cl' is not
            var ours9 = ContentArchives.NamespaceFor(System.IO.Path.Combine(tmp9, "packed"));
            bool shape9 = true;
            foreach (var p9 in new[] { "Build/Content/Main", "C:\\Games\\x\\Content", "/srv/content/2026-09-16", "", "ns:/weird/" }) shape9 &= ContentArchives.IsOurNamespace(ContentArchives.NamespaceFor(p9));
            Check(shape9 && ContentArchives.IsOurNamespace(ours9) && ContentArchives.IsOurNamespace("cl0123456789abcd"), "① every name NamespaceFor makes is ours — 'cl' + 14 lowercase hex digits (as before)");
            Check(!ContentArchives.IsOurNamespace("clientAssetsNs01"), "② a 16-character 'cl…' name whose tail is not 14 hex digits is not ours");
            Check(!ContentArchives.IsOurNamespace("clABCDEF01234567"), "③ uppercase hex is not the shape NamespaceFor makes");
            Check(!ContentArchives.IsOurNamespace(null) && !ContentArchives.IsOurNamespace("cl0123456789abc") && !ContentArchives.IsOurNamespace("cl0123456789abcde") && !ContentArchives.IsOurNamespace("clprobe") && !ContentArchives.IsOurNamespace("xx0123456789abcd"),
                  "④ null, 15 or 17 characters, 'clprobe' and another prefix are not ours (as before)");

            // the reset: other systems' mounts survive — 'cl'-prefixed or not — and our own leftover is still unmounted and reported
            var foreign9 = Unity.Content.ContentNamespace.GetOrCreateNamespace("clientAssetsNs01");
            var other9 = Unity.Content.ContentNamespace.GetOrCreateNamespace("userArchives0001");
            var mine9 = Unity.Content.ContentNamespace.GetOrCreateNamespace(ours9);
            Unity.IO.Archive.ArchiveFileInterface.MountAsync(foreign9, file9, "u");
            Unity.IO.Archive.ArchiveFileInterface.MountAsync(other9, file9, "u");
            Unity.IO.Archive.ArchiveFileInterface.MountAsync(mine9, file9, ContentArchives.MountPrefix);
            int logs9 = Debug.Logs; Debug.Verbose = false;
            Init(typeof(ContentService), "ResetStatics");
            Debug.Verbose = true;
            string log9 = Debug.Logs > logs9 ? Debug.LastLog : "";
            int fLeft = Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(foreign9).Length, oLeft = Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(other9).Length, mLeft = Unity.IO.Archive.ArchiveFileInterface.GetMountedArchives(mine9).Length;
            Check(fLeft == 1 && oLeft == 1, $"⑤ the reset leaves other systems' mounts alone, 'cl'-prefixed or not (actual: clientAssetsNs01={fLeft} userArchives0001={oLeft})");
            Check(mLeft == 0 && log9.Contains("unmounted 1 archive(s)"), $"⑥ the reset still unmounts our own leftover and reports exactly it (actual: ours={mLeft} log=\"{log9}\")");

            FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear(); FakeEngine.MountFrames = 1;
            System.IO.Directory.Delete(tmp9, true);
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("24c) an index that lists no archives is refused, not thrown (F-10)");
        {
            FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear(); FakeEngine.Registered.Clear(); FakeEngine.MountFrames = 0;
            Init(typeof(ContentService), "ResetStatics");
            var tmp10 = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-f10-" + Guid.NewGuid().ToString("N"));
            var dir10 = System.IO.Path.Combine(tmp10, "packed"); System.IO.Directory.CreateDirectory(dir10);
            var index10 = System.IO.File.Exists(dir10) ? "" : System.IO.Path.Combine(dir10, ContentArchives.IndexFile);
            System.IO.File.WriteAllText(index10, "{\"Version\":1,\"Profile\":\"P\",\"Compression\":\"LZ4\",\"MaxArchiveMB\":20,\"Archives\":[]}");
            var ns10 = ContentArchives.NamespaceFor(dir10);
            int Register10(out string ex) { ex = ""; try { return ContentService.RegisterPackedDirectoryAsync(dir10).GetAwaiter().GetResult(); } catch (Exception e) { ex = e.GetType().Name; return -2; } }

            Debug.Verbose = false; int errs10 = Debug.Errors;
            int rc10 = Register10(out var ex10);
            Debug.Verbose = true;
            Check(rc10 == -1 && ex10 == "", $"① an index that lists no archives → -1, no exception (actual: rc={rc10} exception={(ex10 == "" ? "none" : ex10)})");
            Check(Debug.Errors == errs10 + 1 && Debug.LastError.Contains(ContentArchives.IndexFile) && Debug.LastError.Contains("lists no archives"), $"② exactly one error, naming the index and saying it lists no archives (actual: errors=+{Debug.Errors - errs10} last=\"{Debug.LastError}\")");
            Check(!FakeEngine.Namespaces.Contains(ns10) && FakeEngine.Mounts.Count == 0 && FakeEngine.Registered.Count == 0, $"③ nothing is left behind: no namespace, no mount, no registration (actual: namespace={FakeEngine.Namespaces.Contains(ns10)} mounts={FakeEngine.Mounts.Count} registered={FakeEngine.Registered.Count})");
            var read10 = ContentArchives.ReadIndex(dir10);
            Check(read10 != null && read10.Archives.Length == 0, "④ ReadIndex still hands the (empty) index to whoever inspects the folder — the gate is the registration, not the reader (as before)");

            // once the index lists an archive, the same directory registers — the refusal left nothing in the way
            var tex10 = new Texture2D { name = "x" }; FakeEngine.AddProjectAsset("Assets/Content/Icons/x.png", tex10); FakeEngine.Assets[FakeEngine.ObjectIdOf[tex10]] = tex10;
            var cat10 = ScriptableObject.CreateInstance<ContentCatalog>();
            cat10.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Icons/x", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(tex10) } });
            const string arch10 = "P.manifest-0000.archive"; System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir10, arch10), new byte[] { 1 });
            System.IO.File.WriteAllText(index10, JsonUtility.ToJson(new ContentArchives.Index { Profile = "P", Compression = "LZ4", MaxArchiveMB = 20, Archives = new[] { new ContentArchives.Archive { File = arch10, Bytes = 1, InputBytes = 1, Crc = "00000000", Files = new[] { "BuildManifestHash.txt" } } } }));
            var mount10 = "ns:/" + ns10 + "/" + ContentArchives.MountPrefix + "/";
            FakeEngine.Dirs[mount10] = new ScriptableObject[] { cat10 };
            int again10 = Register10(out var exAgain);
            Check(again10 >= 0 && exAgain == "" && ContentService.Contains("Icons/x") && FakeEngine.Registered.Contains(mount10), $"⑤ once the index lists an archive the same directory registers (actual: rc={again10} {exAgain})");
            if (again10 >= 0) ContentService.UnregisterDirectory(again10, force: true);
            FakeEngine.Namespaces.Clear(); FakeEngine.Mounts.Clear();   // the successful registration made the namespace legitimately; start ⑥ clean

            // a hand-edited index with no Archives key at all is the same case: the JSON reader keeps the empty default, not null
            System.IO.File.WriteAllText(index10, "{\"Version\":1,\"Profile\":\"P\"}");
            Debug.Verbose = false; int errsKey = Debug.Errors;
            int rcKey = Register10(out var exKey);
            Debug.Verbose = true;
            Check(rcKey == -1 && exKey == "" && Debug.Errors == errsKey + 1 && !FakeEngine.Namespaces.Contains(ns10), $"⑥ an index with no Archives key at all is the same case: -1, one error, nothing left behind (actual: rc={rcKey} exception={(exKey == "" ? "none" : exKey)} errors=+{Debug.Errors - errsKey})");

            FakeEngine.Dirs.Remove(mount10); FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear(); FakeEngine.Registered.Clear(); FakeEngine.MountFrames = 1;
            System.IO.Directory.Delete(tmp10, true);
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("25) build window model");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            FakeEngine.Folders.Add("Assets/ContentCatalogs");   // isolated from F-1: the window's default catalog folder exists in this project; 23b covers the missing-folder case
            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-p4-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", new Object { name = "m" });

            var model = new ContentBuildWindowModel();
            int saves = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount, undos = UnityEditor.Undo.RecordCalls;
            Check(model.SelectedName == settings.ActiveName && model.SelectedIsActive && model.Profiles.Count == 1, "selection starts at the active profile");
            Check(model.AddProfile("Win") == null && model.SelectedName == "Win" && !model.SelectedIsActive
                  && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves + 1 && UnityEditor.Undo.RecordCalls == undos + 1,
                  "add profile: selected, one Undo record, one save, active unchanged");
            Check(model.AddProfile("Win").Contains("exists") && model.AddProfile(" ").Contains("empty") && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves + 1,
                  "a rejected add neither records nor saves");
            model.Edit(x => { x.SourceRoot = "Assets/Content"; x.OutputPath = System.IO.Path.Combine(tmp, "win"); x.Archive = ContentBuildSettings.ArchiveMode.None; }, "paths");
            Check(UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves + 2 && UnityEditor.Undo.RecordCalls == undos + 2 && UnityEditor.Undo.LastName == "paths",
                  "every committed edit records Undo and saves");
            Check(model.Rename(settings.ActiveName).Contains("exists") && model.Rename("Win2") == null && model.SelectedName == "Win2" && settings.Find("Win2") != null && settings.Find("Win") == null,
                  "rename: must be unique, and the selection follows");
            model.SetSelectedActive();
            Check(settings.ActiveName == "Win2" && model.SelectedIsActive && model.Rename("Win3") == null && settings.ActiveName == "Win3",
                  "activate; renaming the active profile updates the active name too");

            // Categories → validation messages
            Check(ContentBuildWindowModel.DefaultCatalogPath(model.Selected, "Icons/Big") == "Assets/ContentCatalogs/Win3.Icons.Big.asset"
                  && ContentBuildWindowModel.DefaultCatalogPath(model.Selected, "") == "Assets/ContentCatalogs/Win3.asset", "a new catalog's default location is outside the root, named profile.category");
            Check(model.AddCategory("Icons", "Assets/Content/icons.asset").Contains("under the source root") && model.Selected.Catalogs.Length == 1, "a catalog inside the root is refused");
            Check(model.AddCategory("Icons") == null && model.Selected.Catalogs.Length == 2 && model.Selected.Catalogs[1].CreateAt == "Assets/ContentCatalogs/Win3.Icons.asset"
                  && model.Errors.Count == 1 && model.Errors[0].Contains("overlap") && !model.CanBuild,
                  $"adding a category that overlaps the whole-root one → validation message, cannot build ({string.Join(" | ", model.Errors)})");
            model.RemoveCategory(0);
            Check(model.Errors.Count == 0 && model.CanBuild && model.Selected.Catalogs.Length == 1 && model.AddCategory("Icons/").Contains("exists already"),
                  "removing the overlap makes it buildable; the same category cannot be added twice");

            // Notes shown in the window
            model.Edit(x => x.OutputPath = "Assets/StreamingAssets/Win", "out");
            Check(model.Notes.Count == 2 && Any(model.Notes, "inside Assets/") && Any(model.Notes, "uncompressed"), $"output inside Assets/ is allowed but noted (two notes together with the compression one, notes={model.Notes.Count})");
            ContentDirectoryBuilder.SourceRoot = "Assets/Other"; settings.MarkDirty();
            Check(ContentDirectoryBuilder.HasOverrides && model.Notes.Count == 3 && Any(model.Notes, "override"), $"a live static override is noted (notes={model.Notes.Count})");
            ContentDirectoryBuilder.ClearOverrides(); settings.MarkDirty();
            model.Edit(x => { x.OutputPath = System.IO.Path.Combine(tmp, "win"); x.Compression = ContentBuildSettings.CompressionKind.LZMA; }, "out");
            Check(!ContentDirectoryBuilder.HasOverrides && model.Notes.Count == 1 && model.Notes[0].Contains("uncompressed"), "clearing the override removes that note; a flat build notes that compression is ignored");

            // Post-processors
            var available = model.AvailablePostProcessors;
            Check(Has(available, typeof(PpA)) && Has(available, typeof(PpB)) && Has(available, typeof(PpThrow)) && !Has(available, typeof(IContentBuildPostProcess))
                  && string.CompareOrdinal(available[0].FullName, available[1].FullName) < 0, "implementations are found via TypeCache and listed by name");
            Check(model.AddPostProcessor(typeof(PpB)) == null && model.AddPostProcessor(typeof(PpA)) == null && model.AddPostProcessor(typeof(PpB)).Contains("listed already")
                  && model.Selected.PostProcessors.Length == 2 && ContentBuildRunner.ResolvePostProcessor(model.Selected.PostProcessors[0]) == typeof(PpB)
                  && model.Selected.PostProcessors[0] == typeof(PpB).FullName + ", " + typeof(PpB).Assembly.GetName().Name,
                  "added to settings as 'name, assembly'; duplicates refused; the runner resolves that name");
            model.MovePostProcessor(1, -1);
            Check(ContentBuildRunner.ResolvePostProcessor(model.Selected.PostProcessors[0]) == typeof(PpA) && ContentBuildRunner.ResolvePostProcessor(model.Selected.PostProcessors[1]) == typeof(PpB),
                  "reordering only affects the post-processor list");
            model.Edit(x => x.PostProcessors = new[] { x.PostProcessors[0], "Nope.Missing" }, "pp");
            Check(model.Errors.Count == 1 && model.Errors[0].Contains("Nope.Missing") && !model.CanBuild, "an unresolvable post-processor produces a message that blocks the build");
            model.RemovePostProcessor(1);
            Check(model.CanBuild && model.Selected.PostProcessors.Length == 1, "removing it unblocks the build");

            // Partitioner: both built-ins are offered; the profile stores 'name, assembly'; an unresolvable name blocks a Packed build
            var parts = model.AvailablePartitioners;
            Check(Has(parts, typeof(ContentArchivePacker.HashTriePartitioner)) && Has(parts, typeof(ContentArchivePacker.SequentialFillPartitioner)) && !Has(parts, typeof(IContentArchivePartitioner))
                  && model.Selected.Partitioner == "" && model.SelectedPartitioner == typeof(ContentArchivePacker.HashTriePartitioner),
                  "partitioners: the two built-ins are found; an empty name means the hash trie");
            model.SetPartitioner(typeof(ContentArchivePacker.SequentialFillPartitioner));
            Check(model.Selected.Partitioner == ContentBuildWindowModel.NameOf(typeof(ContentArchivePacker.SequentialFillPartitioner)) && model.SelectedPartitioner == typeof(ContentArchivePacker.SequentialFillPartitioner),
                  "SetPartitioner stores 'name, assembly' and resolves back");
            model.Edit(x => { x.Partitioner = "Nope.Part"; x.Archive = ContentBuildSettings.ArchiveMode.Packed; }, "part");
            Check(model.SelectedPartitioner == null && Any(model.Errors, "partitioner 'Nope.Part' was not found") && !model.CanBuild, "an unresolvable partitioner blocks a Packed build");
            model.Edit(x => { x.Partitioner = ""; x.Archive = ContentBuildSettings.ArchiveMode.None; }, "part");
            Check(model.CanBuild, "a flat build does not care about the partitioner");

            // Build reaches the runner; the window saves the reference the first build sets; the last result lives in SessionState
            PpCalls.Clear();
            int savesBeforeBuild = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;
            var r = model.Build(showProgress: false);
            Check(r != null && r.Succeeded && model.LastResult == r && UnityEditor.BuildPipeline.LastParameters.name == "Win3" && PpCalls.Count == 1 && PpCalls[0].StartsWith("A:"),
                  "the build call reaches the runner and post-processors run (" + string.Join(" | ", r?.Errors ?? new System.Collections.Generic.List<string>()) + ")");
            Check(model.Selected.Catalogs[0].Catalog.isSet && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == savesBeforeBuild + 1,
                  "the window saves the catalog reference the first build set — the CLI does not save");
            var last = ContentBuildWindowModel.ReadLastBuild();
            Check(last.Exists && last.Ok && last.Profile == "Win3" && last.Files == r.Files.Length && last.Target == "StandaloneOSX", "the last result is read from SessionState");
            savesBeforeBuild = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;
            Check(model.UpdateCatalogs(out var msg) && msg.Contains("updated 1 catalog") && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == savesBeforeBuild,
                  "catalog update: nothing to save when the reference is already set");
            // Refresh after build: only for output inside Assets/ (relative and absolute paths both recognised)
            int refreshes = UnityEditor.AssetDatabase.RefreshCalls;
            var dataPath = Application.dataPath;
            Application.dataPath = System.IO.Path.Combine(tmp, "Assets");
            model.Edit(x => x.OutputPath = System.IO.Path.Combine(Application.dataPath, "Out"), "out");
            var ra = model.Build(showProgress: false);
            Application.dataPath = dataPath;
            Check(ra.Succeeded && UnityEditor.AssetDatabase.RefreshCalls == refreshes + 1
                  && ContentBuildRunner.InsideAssets("Assets/StreamingAssets/X") && ContentBuildRunner.InsideAssets("Assets") && !ContentBuildRunner.InsideAssets("Build/Content/X") && !ContentBuildRunner.InsideAssets("AssetsX/y"),
                  $"building inside Assets/ calls AssetDatabase.Refresh once — the earlier build outside (tmp) called it 0 times (refreshes={UnityEditor.AssetDatabase.RefreshCalls - refreshes})");
            model.Edit(x => x.OutputPath = System.IO.Path.Combine(tmp, "win"), "out");

            int s0 = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;
            model.OnUndoRedo();
            Check(UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == s0 + 1, "the file follows after Undo/Redo");

            // Removal
            Check(model.RemoveSelected() == null && settings.Find("Win3") == null && model.SelectedName == settings.ActiveName && settings.ActiveName == settings.Profiles[0].Name,
                  "removing the active profile activates the first one and moves the selection there");
            Check(model.RemoveSelected().Contains("last profile") && settings.Profiles.Count == 1, "the last profile cannot be removed");

            System.IO.Directory.Delete(tmp, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("26) CLI entry point");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            var settings = ContentBuildSettings.instance;
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-p5-" + Guid.NewGuid().ToString("N"));
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", new Object { name = "m" });
            var two = settings.Add("Two");
            two.SourceRoot = "Assets/Content"; two.OutputPath = System.IO.Path.Combine(tmp, "two"); two.Archive = ContentBuildSettings.ArchiveMode.None;
            two.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/icons.asset" },
                                   new ContentBuildSettings.CatalogEntry { SubFolder = "Materials", CreateAt = "Assets/Cat/materials.asset" } };
            int saves = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;

            // Parsing: only our keys are picked up, the rest belong to Unity; an unknown key or a missing value is an error
            Check(ContentBuildCLI.Parse(new[] { "Unity", "-batchmode", "-buildTarget", "StandaloneOSX", "-contentlink.profile", "Two", "-contentlink.Output", "x", "-contentlink.maxarchivemb", "5" }, out var o1, out var e1)
                  && e1 == null && o1.Profile == "Two" && o1.Overrides.Count == 2 && o1.Overrides["output"] == "x" && o1.Overrides["maxArchiveMB"] == "5",
                  "parse: only -contentlink.<key> <value>; keys are case-insensitive; Unity arguments are ignored");
            Check(!ContentBuildCLI.Parse(new[] { "-contentlink.colour", "red" }, out _, out var e2) && e2.Contains("unknown option -contentlink.colour") && e2.Contains("profile, source, output"),
                  "an unknown key is an error (not silently skipped) and the known keys are listed");
            Check(!ContentBuildCLI.Parse(new[] { "-contentlink.profile" }, out _, out var e3) && e3.Contains("needs a value"), "a key without a value is an error");
            Check(ContentBuildCLI.Parse(new[] { "-contentlink.partitioner", "sequential" }, out var o4, out _) && o4.Overrides["partitioner"] == "sequential", "parse: the partitioner key");

            // Overrides stay in memory; the build goes through the runner; exit code
            Debug.Verbose = false;
            int rc = ContentBuildCLI.Run(new[] { "-contentlink.profile", "Two", "-contentlink.output", System.IO.Path.Combine(tmp, "cli"), "-contentlink.archive", "packed",
                                                 "-contentlink.compression", "LZ4", "-contentlink.maxArchiveMB", "5", "-contentlink.cleanBuildCache", "1", "-contentlink.partitioner", "sequential" });
            Debug.Verbose = true;
            var cliIndex = ContentArchives.ReadIndex(System.IO.Path.Combine(tmp, "cli"));
            Check(rc == 0 && UnityEditor.BuildPipeline.LastParameters.name == "Two" && two.OutputPath == System.IO.Path.Combine(tmp, "cli") && two.Archive == ContentBuildSettings.ArchiveMode.Packed
                  && two.MaxArchiveMB == 5 && two.CleanBuildCache && two.Partitioner == ContentBuildWindowModel.NameOf(typeof(ContentArchivePacker.SequentialFillPartitioner))
                  && cliIndex != null && cliIndex.Archives.Length == 2 && cliIndex.Archives[1].File.StartsWith("Two.seq-")
                  && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves && Debug.LastLog.StartsWith("[ContentLink CLI] OK · 'Two'"),
                  $"Run: overrides (output, Packed, LZ4, 5MB, clean, partitioner alias → type name) land on the in-memory profile; the runner builds sequentially; no save; one summary line (rc={rc})");
            two.Partitioner = "";
            Debug.Verbose = false;
            int rcNone = ContentBuildCLI.Run(new string[0]);
            Debug.Verbose = true;
            Check(rcNone == 0 && UnityEditor.BuildPipeline.LastParameters.name == "Main", "no arguments → the active profile");
            Debug.Verbose = false; int errs = Debug.Errors;
            int rcBadValue = ContentBuildCLI.Run(new[] { "-contentlink.archive", "zip" });
            int rcNoProfile = ContentBuildCLI.Run(new[] { "-contentlink.profile", "Nope" });
            int rcAllPlus = ContentBuildCLI.Run(new[] { "-contentlink.profile", "all", "-contentlink.output", "x" });
            int rcUnknown = ContentBuildCLI.Run(new[] { "-contentlink.colour", "red" });
            Debug.Verbose = true;
            Check(rcBadValue == 1 && rcNoProfile == 1 && rcAllPlus == 1 && rcUnknown == 1 && Debug.Errors == errs + 4
                  && Debug.LastError.Contains("unknown option"), "bad value, unknown profile, all + override, unknown key → exit code 1 and one error log each");
            two.Archive = ContentBuildSettings.ArchiveMode.None;
            Debug.Verbose = false;
            int rcAll = ContentBuildCLI.Run(new[] { "-contentlink.profile", "all" });
            var bad = settings.Add("Bad"); bad.SourceRoot = "Assets/Nope";
            var after = settings.Add("After"); after.SourceRoot = "Assets/Content"; after.OutputPath = System.IO.Path.Combine(tmp, "after"); after.Archive = ContentBuildSettings.ArchiveMode.None;
            errs = Debug.Errors;
            int rcAllBad = ContentBuildCLI.Run(new[] { "-contentlink.profile", "all" });
            Debug.Verbose = true;
            Check(rcAll == 0 && rcAllBad == 1 && Debug.LastError.Contains("build of 'Bad' failed") && Debug.LastError.Contains("2 of 4 built before it") && !System.IO.Directory.Exists(after.OutputPath),
                  $"all: builds everything and returns 0; stops at the failing profile and returns 1 (rcAll={rcAll} rcAllBad={rcAllBad})");
            settings.Remove("Bad"); settings.Remove("After");

            // Project without a settings file: asking for 'Main' by name must still find it (a real-editor regression: Find did not run the migration)
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            settings = ContentBuildSettings.instance;
            Debug.Verbose = false;
            int rcFresh = ContentBuildCLI.Run(new[] { "-contentlink.profile", "Main", "-contentlink.output", System.IO.Path.Combine(tmp, "fresh") });
            Debug.Verbose = true;
            Check(rcFresh == 0 && settings.Find("Main") != null && settings.SetActive("Main") && UnityEditor.BuildPipeline.LastParameters.name == "Main",
                  $"project without settings: -contentlink.profile Main finds the migrated Main and builds it (rc={rcFresh})");
            saves = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;
            settings.Active.OutputPath = System.IO.Path.Combine(tmp, "main");

            // Build(): reads the process arguments (none of our keys here → active profile); failure calls Exit(1), success does not call Exit (-quit ends with 0)
            UnityEditor.EditorApplication.ExitCode = -1;
            Debug.Verbose = false;
            ContentBuildCLI.Build();
            int exitOk = UnityEditor.EditorApplication.ExitCode;
            settings.Active.SourceRoot = "Assets/Nope";
            ContentBuildCLI.Build();
            Debug.Verbose = true;
            Check(exitOk == -1 && UnityEditor.EditorApplication.ExitCode == 1 && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves,
                  $"Build(): success does not Exit, failure calls EditorApplication.Exit(1), neither saves settings (ok={exitOk} fail={UnityEditor.EditorApplication.ExitCode})");

            System.IO.Directory.Delete(tmp, true);
            UnityEditor.EditorApplication.ExitCode = -1;
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("27) simulated directory — runtime side");
        {
            Init(typeof(ContentService), "ResetStatics");
            FakeEngine.Registered.Clear(); FakeEngine.Assets.Clear(); FakeEngine.IdDir.Clear(); FakeEngine.Refs.Clear();
            FakeEngine.EditorServesAnyId = true;   // editor Play Mode: an id that is in no directory is served from the Library
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-p6-" + Guid.NewGuid().ToString("N"));   // never created — the output folder does not have to exist
            var simA = System.IO.Path.Combine(tmp, "simA");

            // Two hand-installed fallbacks (the editor installs the real ones in check 28): profile SimA = root Assets/Content, category Icons only
            var overlay = new System.Collections.Generic.Dictionary<string, (string path, int id)>
            {
                ["Icons/a"] = ("Assets/Content/Icons/a.png", 7001),
                ["Icons/b"] = ("Assets/Content/Icons/b.png", 7002),
                ["Materials/m"] = ("Assets/Content/Materials/m.mat", 7003),
            };
            foreach (var kv in overlay) FakeEngine.Assets[kv.Value.id] = new Texture2D { name = kv.Key.Substring(kv.Key.IndexOf('/') + 1) };
            var realCatalog = ScriptableObject.CreateInstance<ContentCatalog>();
            realCatalog.EditorSetEntries(new[] { new ContentCatalog.Entry { name = "Dlc/x", id = new LoadableObjectId { value = 7100 } } });
            FakeEngine.Assets[7100] = new Texture2D { name = "x" }; FakeEngine.IdDir[7100] = "real"; FakeEngine.Dirs["real"] = new ScriptableObject[] { realCatalog };

            ContentService.DirectoryFallback = path => path.Replace('\\', '/').TrimEnd('/').EndsWith("/simA")
                ? new ContentService.SimulatedDirectory { Profile = "SimA", SourceRoot = "Assets/Content", Covered = new[] { "Assets/Content/Icons" } } : null;
            ContentService.NameFallback = name =>
            {
                if (!overlay.TryGetValue(name, out var e)) return default;
                return ContentService.TryGetSimulatedDir("Assets/Content", e.path, out var dir) ? new ContentService.SimulatedAsset(new LoadableObjectId { value = e.id }, dir) : default;
            };

            int warn = Debug.Warnings;
            int sim = ContentService.RegisterDirectory(simA);
            Check(sim == 0 && FakeEngine.Registered.Count == 0 && Debug.Warnings == warn && !System.IO.Directory.Exists(simA),
                  "when the fallback answers, nothing is registered with the engine; the output folder may be missing; no 'no catalog' warning");
            string dup = "";
            try { ContentService.RegisterDirectory(simA + "/"); } catch (InvalidOperationException e) { dup = e.Message; }
            Check(dup.Contains("already registered") && ContentService.UnregisterDirectory(1) == false, "re-registering the same path (differing only by trailing separator) throws; there is a single record");

            Check(ContentService.Contains("Icons/a") && !ContentService.Contains("Materials/m") && !ContentService.Contains("Nope"),
                  "coverage gate: only names under Covered (Icons) are answered — Materials outside the category is not");
            var ha = ContentService.Acquire<Texture2D>("Icons/a");
            var leases = new System.Collections.Generic.List<ContentService.LeaseInfo>(); ContentService.Snapshot(leases);
            Check(ha.IsValid && ha.Asset != null && ha.Asset.name == "a" && leases.Count == 1 && leases[0].Dir == sim,
                  $"simulated load: the asset arrives and the slot knows its directory (dir={leases[0].Dir})");
            Check(ContentService.TryGetLoadableObjectId("Icons/b", out var idB) && idB.value == 7002, "TryGetLoadableObjectId answers simulated names too");
            var hb = ContentService.Acquire<Texture2D>(idB);
            ContentService.Snapshot(leases);
            Check(hb.IsValid && leases.Count == 2 && leases.TrueForAll(l => l.Dir == sim), "a load re-acquired by that id also knows its directory (s_SimDirById)");

            Debug.Verbose = false; int errs = Debug.Errors;
            var hm = ContentService.Acquire<Texture2D>("Materials/m");
            Debug.Verbose = true;
            Check(!hm.IsValid && Debug.Errors == errs + 1 && Debug.LastError.Contains("is not under 'Assets/Content' (profile SimA) in the project"),
                  $"message for a name the simulated record cannot answer ({Debug.LastError})");
            overlay.Remove("Icons/b");   // deletion = it disappears from the scan
            Debug.Verbose = false; errs = Debug.Errors;
            bool gone = !ContentService.Contains("Icons/b");
            var hb2 = ContentService.Acquire<Texture2D>("Icons/b");   // a fresh lookup by name — the live slot is keyed by id, so it is not affected
            Debug.Verbose = true;
            Check(gone && !hb2.IsValid && Debug.Errors == errs + 1 && Debug.LastError.Contains("'Icons/b' is not under") && hb.IsValid && hb.Asset != null,
                  "deleted name: Contains is false, a new load fails with that message, the already-live slot (by id) is untouched");

            // Mixed with a real directory, simulated slots do not block unregistering the other one
            int real = ContentService.RegisterDirectory("real");
            Check(real == 1 && FakeEngine.Registered.Contains("real") && ContentService.Contains("Dlc/x"), "a path no profile knows is really registered");
            Check(ContentService.UnregisterDirectory(real), "simulated slots (holding Icons/a and b) do not block unregistering the real directory");

            Debug.Verbose = false;
            bool refused = !ContentService.UnregisterDirectory(sim);
            bool forced = ContentService.UnregisterDirectory(sim, force: true);
            Debug.Verbose = true;
            Check(refused && forced && !ContentService.Contains("Icons/a") && !ha.IsValid, "refused while held; force invalidates; after unregistration the names are gone too");

            // A null fallback means real registration; the Packed path also consults the fallback first
            ContentService.DirectoryFallback = null;
            FakeEngine.Dirs["real2"] = new ScriptableObject[] { realCatalog };
            int real2 = ContentService.RegisterDirectory("real2");
            Check(FakeEngine.Registered.Contains("real2") && real2 >= 0, "without a fallback (player build, or toggle off) it registers with the engine as before");
            ContentService.UnregisterDirectory(real2, force: true);
            ContentService.DirectoryFallback = path => path.Replace('\\', '/').TrimEnd('/').EndsWith("/simA")
                ? new ContentService.SimulatedDirectory { Profile = "SimA", SourceRoot = "Assets/Content", Covered = new[] { "Assets/Content" } } : null;
            var packed = ContentService.RegisterPackedDirectoryAsync(simA);
            Check(packed.IsCompleted && packed.GetAwaiter().GetResult() >= 0 && FakeEngine.Registered.Count == 0 && FakeEngine.Mounts.Count == 0 && ContentService.Contains("Materials/m"),
                  "Packed registration consults the fallback first too — no archives.json check, no mount; a whole-root category answers everything");

            // Two profiles with the same output path are rejected by validation
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            var settings = ContentBuildSettings.instance;
            var pa = settings.Add("A"); pa.OutputPath = "Build/Content/same";
            var pb = settings.Add("B"); pb.OutputPath = "./Build/Content/same/";
            Check(settings.Validate(pa).Exists(e => e.Contains("used by 2 profiles")) && ContentBuildSettings.SameFolder("Build/x", "./Build/x/") && !ContentBuildSettings.SameFolder("Build/x", "Build/y"),
                  "same output folder after normalisation is rejected — trailing separator and ./ do not matter");

            ContentService.DirectoryFallback = null; ContentService.NameFallback = null;
            FakeEngine.EditorServesAnyId = false;
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            Init(typeof(ContentService), "ResetStatics");
            FakeEngine.Dirs.Remove("real"); FakeEngine.Dirs.Remove("real2");
        }

        Console.WriteLine("27b) a name the simulation resolved under a REAL directory's root (NoDirectory) vs. unregistering directories (F-2)");
        {
            Init(typeof(ContentService), "ResetStatics"); Init(typeof(ContentScope), "ResetStatics");   // Root was disposed by the quit scenario
            FakeEngine.Registered.Clear(); FakeEngine.Assets.Clear(); FakeEngine.IdDir.Clear(); FakeEngine.Refs.Clear();
            FakeEngine.EditorServesAnyId = true;
            const int NEW = 7200, OTHER2 = 7201, ORPHAN = 7202;
            FakeEngine.Assets[NEW] = new Texture2D { name = "new" };                                          // not built: no IdDir
            FakeEngine.Assets[OTHER2] = new Texture2D { name = "x" }; FakeEngine.IdDir[OTHER2] = "other";
            FakeEngine.Assets[ORPHAN] = new Texture2D { name = "orphan" };                                    // an id no catalog carries, served by the editor
            var mainCat = ScriptableObject.CreateInstance<ContentCatalog>(); mainCat.EditorSetSourceRoot("Assets/Content");
            mainCat.EditorSetEntries(new[] { new ContentCatalog.Entry { name = "Icons/sword", id = new LoadableObjectId { value = SWORD } } });
            FakeEngine.Assets[SWORD] = new Sprite { name = "sword" }; FakeEngine.IdDir[SWORD] = "main";
            FakeEngine.Dirs["main"] = new ScriptableObject[] { mainCat };
            var otherCat = ScriptableObject.CreateInstance<ContentCatalog>();
            otherCat.EditorSetEntries(new[] { new ContentCatalog.Entry { name = "Other/x", id = new LoadableObjectId { value = OTHER2 } } });
            FakeEngine.Dirs["other"] = new ScriptableObject[] { otherCat };
            ContentService.NameFallback = name => name == "Icons/new" ? new ContentService.SimulatedAsset(new LoadableObjectId { value = NEW }) : default;   // under a real root: NoDirectory

            int main = ContentService.RegisterDirectory("main"); int other = ContentService.RegisterDirectory("other");
            var s = ContentScope.Root.CreateChild("F2");
            var got = s.Load<Texture2D>("Icons/new");
            var leases = new System.Collections.Generic.List<ContentService.LeaseInfo>(); ContentService.Snapshot(leases);
            var simNames = new System.Collections.Generic.List<string>(); ContentService.SnapshotSimulated(simNames);
            Check(got != null && leases.Count == 1 && leases[0].Dir == ContentService.NoDirectory && simNames.Contains("Icons/new"), "① the unbuilt name loads; its slot has no directory and is marked simulated");

            Debug.Verbose = false;
            bool okOther = ContentService.UnregisterDirectory(other);
            Debug.Verbose = true;
            Check(okOther && s.Contains("Icons/new") && Ref(NEW) == 1, $"② unregistering the unrelated 'other' is allowed and the simulated asset stays held (actual: ok={okOther} held={s.Contains("Icons/new")} ref={Ref(NEW)})");
            if (!okOther) { Debug.Verbose = false; ContentService.UnregisterDirectory(other, force: true); s.Load<Texture2D>("Icons/new"); Debug.Verbose = true; }   // recover: force killed the simulated slot on current code, reload it

            Debug.Verbose = false;
            bool okMain = ContentService.UnregisterDirectory(main);
            Debug.Verbose = true;
            Check(okMain && s.Contains("Icons/new") && Ref(NEW) == 1, $"③ the real directory whose root served the name is not blocked either — the asset comes from the project, not from it (actual: ok={okMain})");
            if (!okMain) { Debug.Verbose = false; ContentService.UnregisterDirectory(main, force: true); s.Load<Texture2D>("Icons/new"); Debug.Verbose = true; }
            main = ContentService.RegisterDirectory("main");

            other = ContentService.RegisterDirectory("other");
            s.Load<Texture2D>("Other/x");
            Debug.Verbose = false;
            bool forced = ContentService.UnregisterDirectory(other, force: true);
            Debug.Verbose = true;
            Check(forced && Ref(OTHER2) == 0 && s.Contains("Icons/new") && Ref(NEW) == 1, $"④ force on 'other' releases only its own asset; the simulated one is untouched (actual: forced={forced} other={Ref(OTHER2)} held={s.Contains("Icons/new")} ref={Ref(NEW)})");

            // regression guard (IMP001 F-2 ⓑ): an id no catalog carries is still 'unknown' — it blocks, and force frees it
            var hOrphan = ContentService.Acquire<Texture2D>(new LoadableObjectId { value = ORPHAN });
            Debug.Verbose = false;
            bool refusedMain = !ContentService.UnregisterDirectory(main);
            bool forcedMain = ContentService.UnregisterDirectory(main, force: true);
            Debug.Verbose = true;
            Check(hOrphan.IsValid == false && refusedMain && forcedMain && Ref(ORPHAN) == 0, $"⑤ an id from no catalog still blocks every directory and force frees it (unchanged policy) (actual: refused={refusedMain} forced={forcedMain} valid={hOrphan.IsValid})");
            main = ContentService.RegisterDirectory("main");

            // the id route of the same unbuilt name: TryGetLoadableObjectId → Acquire(id)
            Check(ContentService.TryGetLoadableObjectId("Icons/new", out var newId) && newId.value == NEW, "⑥ the unbuilt name resolves to its id");
            var hid = ContentService.Acquire<Texture2D>(newId);
            ContentService.SnapshotSimulated(simNames);
            Debug.Verbose = false;
            bool okOther2 = false; other = ContentService.RegisterDirectory("other"); okOther2 = ContentService.UnregisterDirectory(other);
            if (!okOther2) ContentService.UnregisterDirectory(other, force: true);
            Debug.Verbose = true;
            Check(hid.IsValid && okOther2 && hid.IsValid, $"⑦ the same asset acquired by id is simulated too: an unrelated directory still unregisters and the handle survives (actual: ok={okOther2} valid={hid.IsValid})");
            hid.Dispose();

            s.Dispose(); Tick();
            Check(Ref(NEW) == 0, "⑧ scope end releases the simulated asset");

            ContentService.NameFallback = null; FakeEngine.EditorServesAnyId = false;
            FakeEngine.Registered.Clear(); FakeEngine.Dirs.Remove("main"); FakeEngine.Dirs.Remove("other"); FakeEngine.Assets.Clear(); FakeEngine.IdDir.Clear(); FakeEngine.Refs.Clear();
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("28) simulated directory — editor matching, gate, toggle, summary");
        {
            Init(typeof(ContentService), "ResetStatics");
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat"); FakeEngine.Registered.Clear(); FakeEngine.Assets.Clear(); FakeEngine.IdDir.Clear();
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Drop();
            ContentLinkSimulation.Enabled = true;
            FakeEngine.EditorServesAnyId = true;
            Init(typeof(ContentLinkSimulation), "Install");   // installs the real fallbacks: OnRegister and Resolve

            var texA = new Texture2D { name = "a" }; var texB = new Texture2D { name = "b" }; var matM = new Object { name = "m" };
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", texA);
            FakeEngine.AddProjectAsset("Assets/Content/Icons/b.png", texB);
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", matM);
            foreach (var o in new Object[] { texA, texB, matM }) FakeEngine.Assets[FakeEngine.ObjectIdOf[o]] = o;
            // Profile SimA's catalog (a project asset) has only a; b is the "unbuilt name" that must show up in the summary
            var catA = ScriptableObject.CreateInstance<ContentCatalog>();
            catA.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Icons/a", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(texA) } });
            catA.EditorSetSourceRoot("Assets/Content");
            FakeEngine.AddProjectAsset("Assets/Cat/simA.asset", catA);
            var settings = ContentBuildSettings.instance;
            var simA = settings.Add("SimA"); simA.SourceRoot = "Assets/Content"; simA.OutputPath = "Build/Content/SimA";
            simA.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/simA.asset" } };
            ContentLinkEditorUtility.Invalidate();

            // Pure matching
            var m1 = ContentLinkSimulation.MatchProfile("Build/Content/SimA/");
            var m2 = ContentLinkSimulation.MatchProfile(System.IO.Path.GetFullPath("Build/Content/SimA"));
            Check(m1 != null && m1.Profile == "SimA" && m1.SourceRoot == "Assets/Content" && m1.Covered.Length == 1 && m1.Covered[0] == "Assets/Content/Icons"
                  && m2 != null && ContentLinkSimulation.MatchProfile("Build/Content/Other") == null && ContentLinkSimulation.MatchProfile(null) == null,
                  "matches the profile after normalising relative/absolute paths and trailing separators; Covered is the category folder; an unknown path is null");

            // In edit mode the fallback does not answer (the engine path is used as is)
            UnityEditor.EditorApplication.isPlaying = false;
            Check(ContentService.DirectoryFallback("Build/Content/SimA") == null, "outside Play Mode the fallback returns null");
            UnityEditor.EditorApplication.isPlaying = true;

            // A source root that is not a folder throws (no fallback to real registration)
            var broken = settings.Add("Broken"); broken.SourceRoot = "Assets/Nope"; broken.OutputPath = "Build/Content/Broken";
            string d9 = ""; try { ContentService.RegisterDirectory("Build/Content/Broken"); } catch (InvalidOperationException e) { d9 = e.Message; }
            Check(d9.Contains("source root 'Assets/Nope' is not a folder") && FakeEngine.Registered.Count == 0, "a profile with a missing root throws and nothing is registered with the engine");

            // Registration → not registered with the engine, the root is scanned immediately (before the Play-entry scan), one log line
            int logs = Debug.Logs;
            int simDir = ContentService.RegisterDirectory("Build/Content/SimA");
            Check(simDir == 0 && FakeEngine.Registered.Count == 0 && Debug.Logs == logs + 1 && Debug.LastLog.Contains("is served from the project — profile 'SimA'") && Debug.LastLog.Contains("Assets/Content/Icons"),
                  "the fallback answers, so the engine is not touched and one registration line is logged");
            Check(ContentService.Contains("Icons/a") && ContentService.Contains("Icons/b") && !ContentService.Contains("Materials/m"),
                  "even without the Play-entry scan, the root scanned at registration answers; outside Covered (Materials) does not");
            var hb = ContentService.Acquire<Texture2D>("Icons/b");
            var leases = new System.Collections.Generic.List<ContentService.LeaseInfo>(); ContentService.Snapshot(leases);
            Check(hb.IsValid && hb.Asset == texB && leases.Count == 1 && leases[0].Dir == simDir, "a name resolved through the coverage gate belongs to that record");

            // Turning the toggle off during Play does not break the record
            logs = Debug.Logs;
            ContentLinkSimulation.Enabled = false;
            Check(Debug.Logs == logs + 1 && Debug.LastLog.Contains("keep working until the next Play") && ContentService.Contains("Icons/a") && ContentLinkSimulation.TryGetPath("Icons/a", out _, out _),
                  "turning it off during Play logs one notice without dropping; the simulated record keeps answering");
            ContentLinkSimulation.Enabled = true;

            // Exit summary lists only names missing from the profile's catalog (b); a is in the catalog, so it is not noise
            var ha = ContentService.Acquire<Texture2D>("Icons/a");
            logs = Debug.Logs;
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.ExitingPlayMode);
            Check(Debug.Logs == logs + 1 && Debug.LastLog.Contains("resolved 1 name(s)") && Debug.LastLog.Contains("Icons/b") && !Debug.LastLog.Contains("Icons/a"),
                  $"the summary lists only names missing from that profile's catalog ({Debug.LastLog})");

            // Registering with the toggle off is a real registration: the fallback returns null
            ha.Dispose(); hb.Dispose(); Tick();
            ContentService.UnregisterDirectory(simDir, force: true);
            ContentLinkSimulation.Enabled = false;
            FakeEngine.Dirs["Build/Content/SimA"] = new ScriptableObject[] { catA };
            int real = ContentService.RegisterDirectory("Build/Content/SimA");
            Check(FakeEngine.Registered.Contains("Build/Content/SimA") && ContentService.Contains("Icons/a") && !ContentService.Contains("Icons/b"),
                  "with the toggle off it registers for real — only the catalog (a) answers, not the new file (b), same as a shipped build");
            ContentService.UnregisterDirectory(real, force: true);
            FakeEngine.Dirs.Remove("Build/Content/SimA");

            ContentLinkSimulation.Enabled = true;
            ContentLinkSimulation.Drop();
            UnityEditor.EditorApplication.isPlaying = false;
            FakeEngine.EditorServesAnyId = false;
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentService.DirectoryFallback = null; ContentService.NameFallback = null;
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("28b) the simulation toggle turned off during Play: exit summary and reset (F-8)");
        {
            Init(typeof(ContentService), "ResetStatics");
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat"); FakeEngine.Registered.Clear(); FakeEngine.Assets.Clear(); FakeEngine.IdDir.Clear();
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentLinkEditorUtility.Invalidate();
            ContentLinkSimulation.Drop();
            ContentLinkSimulation.Enabled = true;
            FakeEngine.EditorServesAnyId = true;
            Init(typeof(ContentLinkSimulation), "Install");
            var texA8 = new Texture2D { name = "a" }; var texB8 = new Texture2D { name = "b" };
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", texA8);
            FakeEngine.AddProjectAsset("Assets/Content/Icons/b.png", texB8);
            foreach (var o in new Object[] { texA8, texB8 }) FakeEngine.Assets[FakeEngine.ObjectIdOf[o]] = o;
            var catA8 = ScriptableObject.CreateInstance<ContentCatalog>();
            catA8.EditorSetEntries(new ContentCatalog.Entry[] { new() { name = "Icons/a", id = UnityEditor.LoadableObjectIdEditorUtility.CreateLoadableObjectId(texA8) } });
            catA8.EditorSetSourceRoot("Assets/Content");
            FakeEngine.AddProjectAsset("Assets/Cat/sim8.asset", catA8);
            var sim8 = ContentBuildSettings.instance.Add("Sim8"); sim8.SourceRoot = "Assets/Content"; sim8.OutputPath = "Build/Content/Sim8";
            sim8.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/sim8.asset" } };
            ContentLinkEditorUtility.Invalidate();
            string ExitLog() { int n = Debug.Logs; UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.ExitingPlayMode); return Debug.Logs > n ? Debug.LastLog.Split('\n')[0] : ""; }

            // Play 1: served from the project, the unbuilt b is loaded, the toggle goes off mid-Play, Play ends
            UnityEditor.EditorApplication.isPlaying = true;
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.EnteredPlayMode);
            Debug.Verbose = false;
            ContentService.RegisterDirectory("Build/Content/Sim8");
            var hb8 = ContentService.Acquire<Texture2D>("Icons/b");
            ContentLinkSimulation.Enabled = false;
            var exit1 = ExitLog();
            Debug.Verbose = true;
            Check(hb8.IsValid && exit1.Contains("Icons/b"), $"① Play 1 exit: the summary names the unbuilt 'Icons/b' loaded this Play, toggle or not (actual: \"{exit1}\")");
            bool overlayGone = !ContentLinkSimulation.TryGetPath("Icons/a", out _, out _);
            Check(overlayGone, $"② Play 1 exit: the overlay is dropped, toggle or not (actual: still answers={!overlayGone})");
            hb8.Dispose(); Tick();
            UnityEditor.EditorApplication.isPlaying = false;
            Init(typeof(ContentService), "ResetStatics");

            // Play 2: toggle back on, nothing registered, nothing loaded
            ContentLinkSimulation.Enabled = true;
            UnityEditor.EditorApplication.isPlaying = true;
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.EnteredPlayMode);
            Debug.Verbose = false; var exit2 = ExitLog(); Debug.Verbose = true;
            Check(exit2 == "", $"③ Play 2 exit: nothing was loaded, so no summary — nothing from Play 1 leaks in (actual: \"{exit2}\")");
            UnityEditor.EditorApplication.isPlaying = false;
            Init(typeof(ContentService), "ResetStatics");

            // Play 3: the toggle is off for the whole Play — a real registration, no summary, no state left behind
            ContentLinkSimulation.Enabled = false;
            UnityEditor.EditorApplication.isPlaying = true;
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.EnteredPlayMode);
            FakeEngine.Dirs["Build/Content/Sim8"] = new ScriptableObject[] { catA8 };
            Debug.Verbose = false;
            int real8 = ContentService.RegisterDirectory("Build/Content/Sim8");
            bool realReg = FakeEngine.Registered.Contains("Build/Content/Sim8");
            var exit3 = ExitLog();
            ContentService.UnregisterDirectory(real8, force: true);
            Debug.Verbose = true;
            Check(realReg && exit3 == "" && !ContentLinkSimulation.TryGetPath("Icons/a", out _, out _), $"④ Play 3 (toggle off throughout): a real registration, no summary, no overlay (actual: real={realReg} summary=\"{exit3}\")");
            UnityEditor.EditorApplication.isPlaying = false;
            FakeEngine.Dirs.Remove("Build/Content/Sim8");
            Init(typeof(ContentService), "ResetStatics");

            // outside Play: turning the toggle off drops at once (as before)
            ContentLinkSimulation.Enabled = true;
            ContentLinkSimulation.Rebuild();
            bool hadOverlay = ContentLinkSimulation.TryGetPath("Icons/a", out _, out _);
            ContentLinkSimulation.Enabled = false;
            Check(hadOverlay && !ContentLinkSimulation.TryGetPath("Icons/a", out _, out _), "⑤ outside Play the toggle going off drops the overlay at once (as before)");

            // Play 4: off then on again before the exit (the §28 order) still summarises
            ContentLinkSimulation.Enabled = true;
            UnityEditor.EditorApplication.isPlaying = true;
            UnityEditor.EditorApplication.RaisePlayModeStateChanged(UnityEditor.PlayModeStateChange.EnteredPlayMode);
            Debug.Verbose = false;
            ContentService.RegisterDirectory("Build/Content/Sim8");
            var hb8b = ContentService.Acquire<Texture2D>("Icons/b");
            ContentLinkSimulation.Enabled = false; ContentLinkSimulation.Enabled = true;
            var exit4 = ExitLog();
            Debug.Verbose = true;
            Check(exit4.Contains("Icons/b"), $"⑥ off then on again before the exit still summarises (as before) (actual: \"{exit4}\")");
            hb8b.Dispose(); Tick();

            ContentLinkSimulation.Drop(); ContentLinkSimulation.Enabled = true;
            UnityEditor.EditorApplication.isPlaying = false; FakeEngine.EditorServesAnyId = false;
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentService.DirectoryFallback = null; ContentService.NameFallback = null;
            ContentService.SceneContainsFallback = null; ContentService.SceneFallback = null; ContentService.SceneResultFallback = null;
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat"); ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine("29) CLI catalogs-only update — the UpdateCatalogs entry point");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-cat-" + Guid.NewGuid().ToString("N"));
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", new Object { name = "m" });
            UnityEditor.BuildPipeline.LastParameters = null;

            // Project without settings, by name, with an output override: the catalog is updated, no build runs, and neither the output folder nor a settings file is created
            int saves = UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount;
            Debug.Verbose = false;
            int rc = ContentBuildCLI.Run(new[] { "-contentlink.profile", "Main", "-contentlink.output", System.IO.Path.Combine(tmp, "main") }, catalogsOnly: true);
            Debug.Verbose = true;
            var settings = ContentBuildSettings.instance;
            var cat = UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>(ContentDirectoryBuilder.DefaultCatalogPath);
            Check(rc == 0 && cat != null && cat.Entries.Count == 2 && cat.SourceRoot == "Assets/Content"
                  && UnityEditor.BuildPipeline.LastParameters == null && !System.IO.Directory.Exists(tmp)
                  && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves
                  && Debug.LastLog == "[ContentLink CLI] OK · catalogs of 'Main' · 1 catalog(s)",
                  $"catalogsOnly: catalog updated with 2 entries, no engine build, no output folder, no save, one summary line (rc={rc} last={Debug.LastLog})");

            // A source override is written as the catalog's SourceRoot (same behaviour as build mode)
            FakeEngine.AddProjectAsset("Assets/Other/x.png", new Texture2D { name = "x" });
            Debug.Verbose = false;
            int rcSrc = ContentBuildCLI.Run(new[] { "-contentlink.source", "Assets/Other" }, catalogsOnly: true);
            Debug.Verbose = true;
            cat = UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>(ContentDirectoryBuilder.DefaultCatalogPath);
            Check(rcSrc == 0 && cat.Entries.Count == 1 && cat.SourceRoot == "Assets/Other" && settings.Active.SourceRoot == "Assets/Other",
                  $"source override: the catalog is rewritten with that root; only the in-memory profile changes (rc={rcSrc})");
            settings.Active.SourceRoot = "Assets/Content";

            // all: updates every profile, stops at the first failure, rejects overrides, unknown profile
            var two = settings.Add("Two"); two.SourceRoot = "Assets/Content"; two.OutputPath = System.IO.Path.Combine(tmp, "two");
            two.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/icons.asset" } };
            Debug.Verbose = false;
            int rcAll = ContentBuildCLI.Run(new[] { "-contentlink.profile", "all" }, catalogsOnly: true);
            string okAll = Debug.LastLog;
            var bad = settings.Add("Bad"); bad.SourceRoot = "Assets/Nope"; bad.OutputPath = System.IO.Path.Combine(tmp, "bad");
            var after = settings.Add("After"); after.SourceRoot = "Assets/Content"; after.OutputPath = System.IO.Path.Combine(tmp, "after");
            after.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Materials", CreateAt = "Assets/Cat/after.asset" } };
            int errs = Debug.Errors;
            int rcAllBad = ContentBuildCLI.Run(new[] { "-contentlink.profile", "all" }, catalogsOnly: true);
            string failAll = Debug.LastError;
            int rcAllPlus = ContentBuildCLI.Run(new[] { "-contentlink.profile", "all", "-contentlink.source", "x" }, catalogsOnly: true);
            int rcNoProfile = ContentBuildCLI.Run(new[] { "-contentlink.profile", "Nope" }, catalogsOnly: true);
            Debug.Verbose = true;
            var icons = UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>("Assets/Cat/icons.asset");
            Check(rcAll == 0 && okAll == "[ContentLink CLI] OK · catalogs of 2 profile(s)" && icons != null && icons.Entries.Count == 1
                  && rcAllBad == 1 && failAll.Contains("catalog update of 'Bad' failed") && failAll.Contains("2 of 4 updated before it")
                  && UnityEditor.AssetDatabase.LoadAssetAtPath<ContentCatalog>("Assets/Cat/after.asset") == null
                  && rcAllPlus == 1 && rcNoProfile == 1 && Debug.Errors == errs + 3 && UnityEditor.BuildPipeline.LastParameters == null,
                  $"all: both updated → 0; stops at Bad → 1 (After is not created); override or unknown profile → 1; no build ever runs (rcAll={rcAll} rcAllBad={rcAllBad})");
            settings.Remove("Bad"); settings.Remove("After"); settings.Remove("Two");

            // UpdateCatalogs(): process arguments (none of our keys → active profile); success does not Exit, failure calls Exit(1)
            UnityEditor.EditorApplication.ExitCode = -1;
            Debug.Verbose = false;
            ContentBuildCLI.UpdateCatalogs();
            int exitOk = UnityEditor.EditorApplication.ExitCode;
            settings.Active.SourceRoot = "Assets/Nope";
            ContentBuildCLI.UpdateCatalogs();
            Debug.Verbose = true;
            Check(exitOk == -1 && UnityEditor.EditorApplication.ExitCode == 1 && UnityEditor.BuildPipeline.LastParameters == null
                  && UnityEditor.ScriptableSingleton<ContentBuildSettings>.SaveCount == saves,
                  $"UpdateCatalogs(): success does not Exit, failure calls Exit(1), no build, no save (ok={exitOk} fail={UnityEditor.EditorApplication.ExitCode})");

            if (System.IO.Directory.Exists(tmp)) System.IO.Directory.Delete(tmp, true);
            UnityEditor.EditorApplication.ExitCode = -1;
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat");
            ContentLinkEditorUtility.Invalidate();
        }

        Console.WriteLine("OBS) observations O-2·3·4·6·7·8·9·10·11");
        {
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests();
            ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat"); FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear(); FakeEngine.Registered.Clear();
            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
            if (!FakeEngine.Assets.ContainsKey(SWORD)) FakeEngine.Assets[SWORD] = new Sprite { name = "sword" };     // an earlier section drops the fixture assets
            if (!FakeEngine.Assets.ContainsKey(SHIELD)) FakeEngine.Assets[SHIELD] = new Sprite { name = "shield" };
            FakeEngine.Dirs["Main"] = new ScriptableObject[] { catalog }; foreach (var kv in FakeEngine.Assets) FakeEngine.IdDir[kv.Key] = "Main";
            ContentService.RegisterDirectory("Main");
            var settingsO = ContentBuildSettings.instance;
            var tmpO = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clink-obs-" + Guid.NewGuid().ToString("N"));
            settingsO.Active.OutputPath = System.IO.Path.Combine(tmpO, "main");
            FakeEngine.AddProjectAsset("Assets/Content/Icons/a.png", new Texture2D { name = "a" });
            FakeEngine.AddProjectAsset("Assets/Content/Icons/b.png", new Texture2D { name = "b" });
            FakeEngine.AddProjectAsset("Assets/Content/Materials/m.mat", new Object { name = "m" });
            string ErrsO(ContentBuildResult x) => string.Join(" | ", x.Errors);

            // O-2: a packed build whose manifest archive alone is over a tiny limit — no warning for it
            var pkO = settingsO.Add("PkO"); pkO.SourceRoot = "Assets/Content"; pkO.OutputPath = System.IO.Path.Combine(tmpO, "pko");
            pkO.Archive = ContentBuildSettings.ArchiveMode.Packed; pkO.Compression = ContentBuildSettings.CompressionKind.LZ4; pkO.CleanBuildCache = true;
            pkO.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { SubFolder = "Icons", CreateAt = "Assets/Cat/icons_o.asset" }, new ContentBuildSettings.CatalogEntry { SubFolder = "Materials", CreateAt = "Assets/Cat/materials_o.asset" } };
            ContentArchivePacker.TestLimitBytes = 100;
            Debug.Verbose = false; int warnO2 = Debug.Warnings; var rO2 = ContentBuildRunner.Build(pkO, showProgress: false); Debug.Verbose = true;
            Check(rO2.Succeeded && Debug.Warnings == warnO2, $"O-2 ① the manifest archive alone over the limit is not warned about — it is exempt by design (actual: ok={rO2.Succeeded} warnings=+{Debug.Warnings - warnO2}: \"{Debug.LastWarning}\")");

            // O-6: a manifest that names no file by ContentHash (older shape) — the packer says the keys fell back to names
            UnityEditor.BuildPipeline.ManifestWithoutContentHash = true;
            Debug.Verbose = false; int warnO6 = Debug.Warnings; var rO6 = ContentBuildRunner.Build(pkO, showProgress: false); Debug.Verbose = true;
            UnityEditor.BuildPipeline.ManifestWithoutContentHash = false;
            Check(rO6.Succeeded && Debug.Warnings == warnO6 + 1 && Debug.LastWarning.Contains("keyed by name"), $"O-6 ① a manifest without ContentHash is warned about like a missing manifest (actual: ok={rO6.Succeeded} warnings=+{Debug.Warnings - warnO6}: \"{Debug.LastWarning}\")");
            ContentArchivePacker.TestLimitBytes = null;

            // O-11: a truncated archive is refused before anything is mounted
            var rO11 = ContentBuildRunner.Build(pkO, showProgress: false);
            var idxO11 = ContentArchives.ReadIndex(pkO.OutputPath);
            var archO11 = System.IO.Path.Combine(pkO.OutputPath, idxO11.Archives[1].File);
            System.IO.File.WriteAllBytes(archO11, new byte[] { 1, 2, 3 });
            var nsO11 = ContentArchives.NamespaceFor(pkO.OutputPath);
            FakeEngine.Dirs["ns:/" + nsO11 + "/" + ContentArchives.MountPrefix + "/"] = new ScriptableObject[] { (ContentCatalog)FakeEngine.MainAssetAt["Assets/Cat/icons_o.asset"], (ContentCatalog)FakeEngine.MainAssetAt["Assets/Cat/materials_o.asset"] };   // the fake engine serves the mount path
            Debug.Verbose = false; int errO11 = Debug.Errors; int rcO11 = -2; string exO11 = "";
            try { rcO11 = ContentService.RegisterPackedDirectoryAsync(pkO.OutputPath).GetAwaiter().GetResult(); } catch (Exception e) { exO11 = e.GetType().Name; }
            Debug.Verbose = true;
            Check(rcO11 == -1 && exO11 == "" && Debug.Errors == errO11 + 1 && Debug.LastError.Contains("bytes on disk") && FakeEngine.Mounts.Count == 0 && !FakeEngine.Namespaces.Contains(nsO11),
                  $"O-11 ① an archive whose size differs from the index is refused with -1 and one error, before any mount (actual: rc={rcO11} exception={(exO11 == "" ? "none" : exO11)} errors=+{Debug.Errors - errO11} mounts={FakeEngine.Mounts.Count} last=\"{Debug.LastError}\")");
            if (rcO11 >= 0) ContentService.UnregisterDirectory(rcO11, force: true);
            FakeEngine.Dirs.Remove("ns:/" + nsO11 + "/" + ContentArchives.MountPrefix + "/"); FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear();

            // O-10: an exception inside Build becomes a failed result with the reason — no exception escapes
            var thO = settingsO.Add("ThrowO"); thO.SourceRoot = "Assets/Content"; thO.OutputPath = System.IO.Path.Combine(tmpO, "throw"); thO.Archive = ContentBuildSettings.ArchiveMode.None;
            thO.Catalogs = new[] { new ContentBuildSettings.CatalogEntry { CreateAt = "Assets/Cat/throw_o.asset" } };
            UnityEditor.BuildPipeline.ThrowNextBuild = true;
            Debug.Verbose = false; ContentBuildResult rO10 = null; string exO10 = "";
            try { rO10 = ContentBuildRunner.Build(thO, showProgress: false); } catch (Exception e) { exO10 = e.GetType().Name; }
            string lastErrO10 = Debug.LastError; Debug.Verbose = true; UnityEditor.BuildPipeline.ThrowNextBuild = false;
            Check(exO10 == "" && rO10 != null && !rO10.Succeeded && rO10.Errors.Exists(x => x.Contains("boom")) && lastErrO10.Contains("boom"),
                  $"O-10 ① a stage that throws makes a failed result that carries the reason, and the summary shows it (actual: exception={(exO10 == "" ? "none" : exO10)} errors={(rO10 != null ? ErrsO(rO10) : "-")} summary=\"{lastErrO10.Split('\n')[0]}\")");

            // O-7: a profile name with a path character fails validation
            var badO7 = settingsO.Add("bad/name"); var okO7 = settingsO.Add("good-name_1");
            var vBad = badO7 != null ? settingsO.Validate(badO7) : new System.Collections.Generic.List<string> { "(Add refused)" };
            var vOk = settingsO.Validate(okO7);
            Check(badO7 != null && vBad.Exists(x => x.Contains("cannot be in a file name")) && !vOk.Exists(x => x.Contains("cannot be in a file name")),
                  $"O-7 ① 'bad/name' fails validation naming the rule; 'good-name_1' passes (actual: bad={string.Join(" | ", vBad)} good={string.Join(" | ", vOk)})");

            // O-8: an archive name with ':' is rejected on every platform
            {
                var sizesO = new System.Collections.Generic.Dictionary<string, long> { ["a.cf"] = 4, ["BuildManifestHash.txt"] = 1, ["abc123.json"] = 2 };
                var inO = new ArchivePartitionInput { ProfileName = "T", LimitBytes = 8, Files = new[] { "a.cf", "BuildManifestHash.txt", "abc123.json" }, Sizes = sizesO, ManifestFiles = new[] { "BuildManifestHash.txt", "abc123.json" } };
                var errsO8 = new System.Collections.Generic.List<string>(); var warnsO8 = new System.Collections.Generic.List<string>();
                bool okO8 = ArchivePartitionValidator.Validate(inO, new[] { Grp("T.m.archive", "BuildManifestHash.txt", "abc123.json"), Grp("T:x.archive", "a.cf") }, errsO8, warnsO8);
                var errsO8b = new System.Collections.Generic.List<string>();
                bool okO8b = ArchivePartitionValidator.Validate(inO, new[] { Grp("T.m.archive", "BuildManifestHash.txt", "abc123.json"), Grp("T?x.archive", "a.cf") }, errsO8b, null);
                Check(!okO8 && errsO8.Exists(x => x.Contains("T:x.archive")) && !okO8b && errsO8b.Exists(x => x.Contains("T?x.archive")), $"O-8 ① ':' and '?' in an archive name are rejected here on macOS too (actual: colon ok={okO8} question ok={okO8b})");
            }

            // O-9: the CLI's 'all' value ignores case like its keys
            Check(ContentBuildCLI.Parse(new[] { "-contentlink.profile", "All" }, out var optO9, out var errO9) && optO9.Profile == ContentBuildCLI.All, $"O-9 ① -contentlink.profile All means every profile (actual: profile='{optO9?.Profile}' error={errO9})");

            // O-3: a null owner is an ArgumentNullException, like the scope overload
            var linkO3 = new ContentLinkSprite(); linkO3.SetName("Icons/sword");
            string exO3a = "", exO3b = "", exO3c = "";
            try { linkO3.LoadAsync((Component)null); } catch (Exception e) { exO3a = e.GetType().Name; }
            try { linkO3.LoadAsync((GameObject)null); } catch (Exception e) { exO3b = e.GetType().Name; }
            try { linkO3.Load((Component)null); } catch (Exception e) { exO3c = e.GetType().Name; }
            Check(exO3a == "ArgumentNullException" && exO3b == "ArgumentNullException" && exO3c == "ArgumentNullException", $"O-3 ① a null owner throws ArgumentNullException on every owner overload (actual: {exO3a} / {exO3b} / {exO3c})");

            // O-4: a default token still cancels with the scope; a caller token still cancels; allocation per load measured
            var scO4 = ContentScope.Root.CreateChild("O4"); var linkO4 = new ContentLinkSprite(); linkO4.SetName("Icons/shield");
            var opO4 = linkO4.LoadAsync(scO4); scO4.Dispose();
            bool cancO4 = false; Sprite gotO4 = null; try { gotO4 = Run(opO4); } catch (OperationCanceledException) { cancO4 = true; }
            Tick();
            Check((cancO4 || gotO4 == null) && !linkO4.IsValid && Ref(SHIELD) == 0, $"O-4 ① a default token: the scope ending mid-load still cancels and nothing is held (as before) (actual: cancelled={cancO4} got={(gotO4 != null ? gotO4.name : "null")} ref={Ref(SHIELD)})");
            var scO4b = ContentScope.Root.CreateChild("O4b"); var linkO4b = new ContentLinkSprite(); linkO4b.SetName("Icons/shield");
            var ctsO4 = new System.Threading.CancellationTokenSource(); var opO4b = linkO4b.LoadAsync(scO4b, ctsO4.Token); ctsO4.Cancel();
            bool cancO4b = false; Sprite gotO4b = null; try { gotO4b = Run(opO4b); } catch (OperationCanceledException) { cancO4b = true; }
            Tick();
            Check((cancO4b || gotO4b == null) && !linkO4b.IsValid && Ref(SHIELD) == 0, $"O-4 ② a caller token still cancels and nothing is held (as before) (actual: cancelled={cancO4b} ref={Ref(SHIELD)})");
            scO4b.Dispose();
            var vO4 = NewGO("AllocView", sceneA).AddComponent<IconView>(); vO4.icon.SetName("Icons/sword");
            for (int i = 0; i < 50; ++i) { Run(vO4.icon.LoadAsync(vO4)); vO4.icon.Release(); }
            Tick(); long beforeO4 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; ++i) { Run(vO4.icon.LoadAsync(vO4)); vO4.icon.Release(); }
            long perLoadO4 = (GC.GetAllocatedBytesForCurrentThread() - beforeO4) / 2000;
            Console.WriteLine($"  O-4 allocation per LoadAsync with a default token ≈ {perLoadO4} B");
            Object.Destroy(vO4.gameObject); Tick();

            ContentArchivePacker.TestLimitBytes = null; UnityEditor.BuildPipeline.ThrowNextBuild = false; UnityEditor.BuildPipeline.ManifestWithoutContentHash = false;
            System.IO.Directory.Delete(tmpO, true);
            UnityEditor.ScriptableSingleton<ContentBuildSettings>.ResetForTests(); ContentDirectoryBuilder.ClearOverrides();
            FakeEngine.ClearProject(); FakeEngine.Folders.Add("Assets/Cat"); FakeEngine.Mounts.Clear(); FakeEngine.Namespaces.Clear();
            ContentLinkEditorUtility.Invalidate();
            Init(typeof(ContentService), "ResetStatics");
        }

        Console.WriteLine(fails == 0 ? "\nALL PASS" : $"\n{fails} FAIL");
    }
}
