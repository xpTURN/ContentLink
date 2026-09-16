# xpTURN.ContentLink

Scope-owned asset loading for **Unity 6.6 content directories**.

`AssetLink` made Addressables usable with names and leak detection, but correctness still depended on every
caller pairing load with release. ContentLink removes that dependency: **every load is owned by a scope, and every
scope ends automatically.** A forgotten release is bounded by a lifetime instead of leaking until the app quits.

> Correctness never depends on `Release()`. `Release()` only lowers peak memory.

## Requirements

- Unity 6000.6 or later (`Unity.Loading` content directory API)
- No other dependencies

## Install

Window > Package Manager > `+` > Add package from git URL, then enter the git URL of this package folder.

## Quick start

```csharp
using xpTURN.ContentLink;

// 1. Build: Window > ContentLink > Build Window (profiles), or Build Content Directory for the active one
//    Assets/Content/Icons/sword.png -> "Icons/sword" (the Texture2D),  its sprites -> "Icons/sword[sword]"
//    Assets/Content/Scenes/Arena.unity -> scene "Scenes/Arena"

// 2. Register at startup — a flat or engine-archive build:
ContentService.RegisterDirectory(Path.Combine(Application.streamingAssetsPath, "MainContent"));
//    a Packed build (the default profile setting):
await ContentService.RegisterPackedDirectoryAsync(Path.Combine(Application.streamingAssetsPath, "MainContent"));

// 3. Load into a scope. There is nothing to release.
public class ItemView : MonoBehaviour
{
    public Image image;
    async void Start() => image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
}
```

## Building content

Content is built per **profile**: a source root, an output folder, one or more **categories** (sub-folders of the
root that each get their own `ContentCatalog`, so one directory can carry several catalogs), archive and
compression options, engine options and post-processors. Profiles live in `ProjectSettings/ContentLinkBuild.asset`
and are edited in *Window > ContentLink > Build Window*; *Build Content Directory* / *Update Content
Catalog* act on the active profile. A project without settings gets a `Main` profile from the legacy
`ContentDirectoryBuilder` defaults, so nothing changes until you open the window. A new profile's catalogs go to
`Assets/ContentCatalogs/<profile>.asset`; the folder is created on the first build.

Names are relative to the profile's root — `Assets/Content/Icons/sword.png` is `Icons/sword` whichever category
holds it — and a name that two categories would both carry is refused before the build. Overlapping categories, a
catalog placed under its own root, a profile name with a path character, a source root written with a trailing
separator, and an output folder inside the root (or containing it) are refused too. The output folder is checked
before anything is written: only an empty folder or a previous content build is accepted, anything else is left alone
and the build fails; the clear itself happens right before the engine runs, so a build that fails earlier leaves the
previous output usable.

| Archive | What is written | Register with |
|---|---|---|
| `None` | the engine's flat files (`*.cf`, `*.resS`, the manifest) | `ContentService.RegisterDirectory(path)` |
| `Engine` | one `content0.archive` (the engine's `UseArchive`) | `ContentService.RegisterDirectory(path)` |
| `Packed` (default) | archives of about `MaxArchiveMB` each plus `archives.json` | `await ContentService.RegisterPackedDirectoryAsync(path)` |

`Packed` is for distribution: the flat build is split by a hash-prefix trie — the manifest in a small archive of
its own, oversized files alone, the rest bucketed by the bits of their key's MD5 (a serialized file's manifest ID,
which survives content changes; a `.resS`'s name) and split until a bucket fits the limit — so an edited asset
stays in its archive and every other archive keeps its name and bytes. Names are the
bit path plus a hash of the file list (`Main.0110-a3f9c2e1.archive`); the same input always packs the same way,
byte for byte. The runtime mounts every archive under one namespace and registers the mount path like a flat
directory; unregistering unmounts. `RegisterPackedDirectoryAsync` returns `-1` with an error, mounting nothing, when
the folder has no `archives.json`, when the index lists no archives, or when an archive's size on disk differs from
the index (a download cut short); an archive that does not mount throws, naming it. Same-length corruption is caught
only by comparing the index's `Crc`, which is the caller's job — the engine blocks on a corrupted content file rather
than failing, so verify downloads before registering them. The limit counts input bytes — with LZ4 the archives on
disk come out around half of it. Compression (`Uncompressed` / `LZ4` / `LZMA`) belongs to archives; a flat build is always uncompressed.
`DisableWriteTypeTree` makes a smaller output that loads only on the exact engine version. To group archives your
own way, implement `IContentArchivePartitioner` (Editor assembly) and name it in the profile's `Partitioner`; the
packer validates the answer and checks it does not depend on the input order. `SequentialFillPartitioner` is the
built-in plain fill.

Post-processors implement `IContentBuildPostProcess` and run after the build in the order the profile lists them;
the window finds them with `TypeCache`. `ContentBuildResult` carries the output file list, the archives, the parsed
manifest, the names that were built and, for each, its catalog and serialized file on disk (`Loadables`). A missing
type or an exception fails the build and leaves the output for inspection — as does any build stage that throws: the
result carries the reason, and the window, the log and the command line all show it.

Content directories are built for the editor's active build target and may not load on another one; the window and
the build log say which target a build was made for.

Command line (`tools/build-content.sh` wraps this; the project must not be open in another editor instance — batch
mode needs the project lock):

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget StandaloneOSX -logFile <log> \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.compression LZ4
```

`-contentlink.profile all` (any case) builds every profile and stops at the first failure. `source`, `output`, `archive`,
`maxArchiveMB`, `compression`, `cleanBuildCache`, `disableWriteTypeTree`, `failBuildWhenErrorsLogged` and
`postProcessors` override the named profile in memory only — the settings file is never written from the command
line. A failed build, an unknown key or an unknown profile exits with code 1.

`-executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` takes the same arguments but only rewrites
the profile's catalogs — no build, no output folder — the command-line twin of *Update Content Catalog*
(`tools/build-content.sh -c`). With `profile all` every profile is updated, stopping at the first failure; a `source`
override is what the catalogs are named against.

## Scopes

| Scope | Created by | Ends on | Parent |
|---|---|---|---|
| Root | automatic | `Application.quitting` | — |
| Scene | `ContentScope.ForScene(scene)` | `SceneManager.sceneUnloaded` | Root |
| GameObject | `gameObject.Scope()` / `component.Scope()` | `OnDestroy` | its scene |
| Explicit | `parent.CreateChild("Stage")` | your `Dispose()`, else the parent | any |

- **Leak signal.** An explicit scope reclaimed by a scene unload or by quit logs a warning with its path
  (`Root/Field/ForgottenPopup`). Enable `ContentService.CaptureStackTrace` to include where it was created.
  Ending with a disposed parent or a destroyed owner GameObject is a normal lifetime and does not warn.
- **Never-activated objects** never get `OnDestroy`; their scope is reclaimed when the scene unloads.
- **Forced unregistration.** After `UnregisterDirectory(dir, force: true)` a scope's `Count`, `DumpTree` and
  `Release(key)` see only live references; a reference the force killed is dropped quietly, never reported twice.
- **Moved objects** (`MoveGameObjectToScene`, `DontDestroyOnLoad`) keep their assets: when the original scene
  unloads, their scope is handed over to the scene they live in now.
- **Dedupe.** A key is held at most once per scope. Loading the same key repeatedly (even every frame) never grows it.
- Scopes release assets; they never destroy GameObjects.

## Serialized references (AssetLink style)

```csharp
public ContentLinkSprite icon;                           // name set in the inspector or via SetName()

image.sprite = await icon.LoadAsync(this);                // owner is required (null throws ArgumentNullException)

// Equipment swap: return the old one early (optional)
icon.Release();
icon.SetName(table[itemId].IconName);
image.sprite = await icon.LoadAsync(this);
```

`ContentLinkGameObject`, `ContentLinkSprite`, `ContentLinkTexture2D` are provided; derive from `ContentLink<T>` for others.
`ContentSceneLink` is the same thing for a scene (see [Scenes](#scenes)).

**In the inspector.** A link is drawn as an object field over its catalog name. Dropping an asset in writes the name
the build would give it, and the name line stays editable for data-driven values. The colour says what the catalogs
know: plain for an empty link, amber for a name no catalog has yet (run *Update Content Catalog*), red when the name
resolves to something the field cannot hold. An asset that no catalog knows and that lives outside `SourceRoot` is
refused, with the reason in the console; the name is left as it was. Links you derive yourself are drawn too — no
extra attribute is needed.

## Instances

```csharp
var go = await scope.InstantiateAsync("Prefabs/Cat", parent);
```

The prefab reference belongs to the **instance**, not to `scope` (which only cancels the request if it ends first).
Destroying the instance returns it. Clones made with `Object.Instantiate(instance)` inherit the reference in `Awake`,
so a clone keeps its textures even if the original is destroyed first. No spawner class is needed: a reference is a
table slot, so one per instance is cheap.

## Scenes

Scenes in the content folder become scene entries of the catalog (`.unity` → `"Scenes/Arena"`) and are built into the
directory with their dependencies. A scene is not a `Loadable`: it has no reference count, and its lifetime is the
scene instance itself. ContentLink resolves the name, tracks every loaded content scene (whoever loaded it) so a
directory cannot be unregistered while one is open, and lets a scope own a scene.

```csharp
// Owned: unloaded when the scope ends (additive only)
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
scope.ReleaseScene(arena);                                 // optional early unload

// Serialized, owner required — same rules as ContentLink<T>
public ContentSceneLink level;
var scene = await level.LoadAsync(this);
await level.UnloadAsync();                                 // optional; safe at any time

// Unowned, any mode (Single closes every other scene). Unload with SceneManager.UnloadSceneAsync.
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

- A scene loaded through `ContentService.LoadSceneAsync` belongs to nobody; use a scope or `ContentSceneLink` for a
  bounded lifetime. Single-mode scenes cannot be owned (they close the owner too).
- A scope owns the instance its own load produced: a scene you opened yourself through `SceneManager` stays yours
  even when a scope loads the same scene, and a failed scope load never claims it.
- When an owning scope ends while its scene is the last loaded scene, the scene stays and a warning is logged; at
  application quit nothing is unloaded.
- `ContentService.UnregisterDirectory` refuses (even with `force`) while a scene from that directory is open.
  Unload the scene first. `ContentService.LoadedSceneCount` / `SnapshotScenes(list)` show what is open.
- Cancellation stops the wait; an additive scene is unloaded again once the engine load completes, a Single-mode
  scene stays. A Single-mode load also destroys the caller: pass `destroyCancellationToken` when awaiting from a
  MonoBehaviour.
- Editor, domain reload disabled: a directory left registered at quit (a scene was still open) is released when
  the next Play session starts, so it can be registered again.

## When to call Release

| Situation | What to do |
|---|---|
| Component, popup, stage, scene content | Nothing. Load into the matching scope. |
| Stage / screen with many assets | One explicit scope, `Dispose()` when the stage ends. |
| Scrolling list with recycled cells | A child scope per cell; dispose it when the cell is rebound. |
| Equipment / skin swap | `Release(key)` or `ContentLink.Release()` before loading the new one. |
| Handing an asset to another system | `ShareTo(target, key)` or `target.Adopt(asset)`. Never pass a raw asset across scopes. |

Set `ContentScope.DefaultWarnThreshold` (or `scope.WarnThreshold`) to be warned when a scope accumulates too
much: the failure mode of scopes is over-retention, not leaks.

## Deterministic release

When the last reference goes away the asset is queued, and `Loadable.Release()` is called only in
`ContentService.Collect()` at the end of the frame (PostLateUpdate). Releasing and reacquiring within one frame
does not reload. `ContentService.ReleaseDelayFrames` keeps zero-reference assets a few frames longer.

`ContentService.UnregisterDirectory` refuses while assets from that directory are still held or a scene from it is
still open, because the engine requires every `Loadable` to be released and every scene to be unloaded first.

## Editor simulation

In the editor the project stands in for the content directories: a registration of a build profile's output folder
in Play Mode is not passed to the engine — its names come from the profile's source root as it is on disk, filtered
by the profile's categories, and every asset loads from the project. Edits, additions and deletions are what Play
Mode sees, and nothing has to be built while you work; the output folder need not exist. Toggle:
*Window > ContentLink > Editor Simulation* (on by default, per project; read when a directory is
registered). Off, the editor registers the built directories for real, exactly as a player does.

Any other registered path (a downloaded copy, an ad-hoc build) is real; under its root a name no catalog carries yet
is still resolved from the project. Project-served assets are not unloaded on release (the editor holds them), and
archives are not exercised — check both with the toggle off. Holding a project-served asset never blocks
unregistering a directory. Each served registration logs one line; leaving Play Mode logs the names the simulation
resolved — loaded, or only checked with `RuntimeKeyIsValid` / `Contains` — that the profile's catalogs do not carry
yet, whatever the toggle says at that moment, and `ContentService.SnapshotSimulated(list)` lists every project-served
name while playing. `TryGetLoadableObjectId` answers those names too.

## Diagnostics

- `Window > ContentLink > Dump Scope Tree` (Play Mode), or `ContentScope.DumpTree()`
  (children are listed in the order the scope holds them, not creation order — a removal moves the last sibling into the slot)
- `ContentService.Snapshot(list)` for raw references

```
Root [Root] 0 (peak 0)
  Lobby [Scene] 0 (peak 0)
    Hold [Explicit] 1 (peak 1) : Icons/sword
    Boss [GameObject] 1 (peak 1) : Icons/shield
```

## Coming from AssetLink

| AssetLink | ContentLink |
|---|---|
| `AssetLink.LoadAssetAsync()` + `ReleaseAsset()` | `ContentLink.LoadAsync(owner)`; `Release()` optional |
| `AssetLinkSpawner.SpawnAsync()` + `DoAutoRelease` | `scope.InstantiateAsync()` |
| `AddressablesTracker.ReportUnreferencedHandles()` (GC based) | scope warnings at scene unload / quit (deterministic) |
| Auto Regist Folder Pattern | `ContentDirectoryBuilder` |
| `AssetRef` (GUID) | `ContentService.TryGetLoadableObjectId(name, out id)` → `AcquireAsync<T>(id)` + `scope.Attach(handle)` |

## Notes and limitations

- Main thread only. Cancel tokens from the main thread.
- The engine load cannot be cancelled. A cancelled request returns its reference immediately; the load finishes
  and is released at the next `Collect()`.
- A load the engine throws out of is reported like any other failure — an invalid handle, logged — on the
  synchronous and the asynchronous path alike; nothing stays held.
- Play Mode content served from the project (editor simulation) is not reference counted by Unity. Test release
  behaviour with the simulation off, against a built and registered content directory.
- Clones created inactive take their references on first activation.
- Scenes are not reference counted. Loading the same scene twice additively gives two instances; each is tracked and
  unloaded separately. There is no `ShareTo` / `Adopt` for scenes.

## Verified against

`Tests~/Verify` compiles the package against stubs of the documented Unity 6.6 API and a fake engine that enforces
Loadable reference rules, and runs the lifecycle scenarios (`dotnet run` with the .NET 8 SDK).
The Unity Test Runner suite in `tests/LinkTests` (EditMode builder tests, PlayMode lifetime / scene / regression /
allocation tests) runs against built content directories in Unity 6000.6 — editor Play Mode and a standalone player.

## License

Apache License 2.0
