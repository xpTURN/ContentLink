# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

**English** | [한국어](README.ko-KR.md) | [日本語](README.ja-JP.md) | [简体中文](README.zh-CN.md)

ContentLink loads game content — sprites, prefabs, scenes, audio — **by name** from Unity 6.6's built-in
**content directories**, and takes over the one job every loading system leaves to you: deciding when a loaded
asset may be released.

**The problem.** With `Resources`, AssetBundles or Addressables every load has to be paired with a release, by
hand, in every script. Miss one and memory grows until the app quits; release too early and a texture goes
missing. The tools that find the culprit work after the fact, by guesswork in the profiler.

**What ContentLink does.** Every load belongs to a **scope** — the GameObject that asked for it, its scene, the
app, or one you create for a stage or a popup. When the scope ends (the object is destroyed, the scene unloads,
the app quits, you dispose it) everything it holds is released. You load, and you are done: a release you forget
costs a little peak memory, never correctness. The same idea covers serialized references (`ContentLinkSprite
icon;` with an inspector drawer), instantiated prefabs and content scenes.

**What else is in the box.** A build pipeline for the content directories themselves — profiles saved in
`ProjectSettings`, a build window, deterministic size-split archives for download pipelines, post-processing hooks
and a command line — and an editor mode in which Play Mode loads straight from the project, so nothing has to be
built while you work. The only dependency is Unity 6000.6.

> **Load it, and forget about releasing it.** An asset lives exactly as long as its owner — the GameObject, the
> scene, the app — and `Release()` is only ever an optimization, never a requirement.

---

## Contents

1. [What you get](#what-you-get)
2. [Requirements and install](#requirements-and-install)
3. [Five ideas](#five-ideas)
4. [Quick start](#quick-start)
5. [Names](#names)
6. [Building content](#building-content)
7. [Loading content](#loading-content)
8. [When to call Release](#when-to-call-release)
9. [How release works](#how-release-works)
10. [Editor simulation](#editor-simulation)
11. [Diagnostics](#diagnostics)
12. [Coming from AssetLink or Addressables](#coming-from-assetlink-or-addressables)
13. [Notes and limitations](#notes-and-limitations)
14. [Repository layout and tests](#repository-layout-and-tests)
15. [License](#license)

---

## What you get

| Area | Feature |
|---|---|
| Loading | `scope.LoadAsync<T>("Icons/sword")` — the asset belongs to the scope; nothing to release |
| Scopes | Root / Scene / GameObject / Explicit scopes that end automatically; dedupe per scope; leak warnings with a path |
| Serialized references | `ContentLink<T>` and `ContentSceneLink` fields with an inspector drawer that fills in and validates the name |
| Instances | `scope.InstantiateAsync("Prefabs/Cat")` — the prefab reference lives with the instance and its clones |
| Scenes | Content scenes owned by a scope or a link; unowned loads in any mode; open scenes tracked |
| Deterministic release | Engine releases happen at one point per frame (`Collect()`), never in the middle of your code |
| Build profiles | Source root, output, several categories (catalogs) per directory, archive mode, compression, engine options — saved in `ProjectSettings` |
| Build window | *Window > ContentLink > Build Window*: edit profiles with undo, see validation messages, build one or all |
| Packed archives | The build is split into archives of about `MaxArchiveMB` each, deterministically; the runtime mounts them back into one directory |
| Post-processing | `IContentBuildPostProcess` steps run after a build in the order you list them |
| Command line | `ContentBuildCLI.Build` via `-executeMethod`, with overrides, `profile all`, exit codes; `tools/build-content.sh` |
| Editor simulation | In Play Mode a profile's directory is served from the project: new, edited and deleted assets are what you see, and no build is needed — nothing is written |
| Diagnostics | Scope tree dump, reference snapshots, last-build summary |

## Requirements and install

- Unity **6000.6** or later (the `Unity.Loading` content directory API).
- No other dependencies.

**From git** — *Window > Package Manager > + > Add package from git URL*:

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

**From a local checkout** — in `Packages/manifest.json`:

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

`samples/LinkSample` in this repository is a Unity project wired up exactly like that.

## Five ideas

| Idea | What it is |
|---|---|
| **Content directory** | A folder the engine builds from your assets (`BuildPipeline.BuildContentDirectory`). At runtime you register it and load from it. It is built for one build target. |
| **Catalog** | A `ContentCatalog` asset the build puts into the directory: the list of names and the engine ids they map to. One directory can carry several catalogs (one per *category*). |
| **Name** | What you load by: the asset's path relative to the source root, without extension — `Icons/sword`. Sub-assets are `Icons/sword[sword]`, scenes `Scenes/Arena`. |
| **Scope** | The owner of everything you load. Scopes form a tree (Root → Scene → GameObject → Explicit) and end automatically. |
| **Link** | A serialized field holding a name (`ContentLinkSprite icon;`). Set it in the inspector, load it with an owner. |

## Quick start

**1. Put assets under a source root** — by default `Assets/Content`:

```
Assets/Content/Icons/sword.png        →  "Icons/sword"            (the Texture2D)
                                         "Icons/sword[sword]"     (its Sprite sub-asset)
Assets/Content/Prefabs/Cat.prefab     →  "Prefabs/Cat"
Assets/Content/Scenes/Arena.unity     →  "Scenes/Arena"
```

**2. Build** — *Window > ContentLink > Build Window*. The first time, the window shows a `Main` profile:
source root `Assets/Content`, one category covering the whole root, output `Assets/StreamingAssets/MainContent`,
archive `None` (flat files). Press **Build 'Main'**. (*Window > ContentLink > Build Content Directory*
builds the active profile without opening the window.)

`StreamingAssets` ships inside the player, so the code below finds the folder. For a download pipeline, set
`Archive` to `Packed`, output outside `Assets/` and ship the archives yourself — see [Building content](#building-content).

**3. Register the directory once at startup**, before anything loads:

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    async void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");

        // Archive = None (flat files, the migrated Main profile) or Engine (one content0.archive):
        ContentService.RegisterDirectory(path);

        // Archive = Packed (the default for new profiles): a few archives + archives.json
        // await ContentService.RegisterPackedDirectoryAsync(path);
    }
}
```

**4. Load into a scope.** There is nothing to release: the sprite goes away with the GameObject.

```csharp
using UnityEngine;
using UnityEngine.UI;
using xpTURN.ContentLink;

public class ItemView : MonoBehaviour
{
    public Image image;
    async void Start() => image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
}
```

**5. Or use a serialized link** and pick the asset in the inspector:

```csharp
public ContentLinkSprite icon;                       // drop Assets/Content/Icons/sword.png on it → "Icons/sword[sword]"
async void Start() => image.sprite = await icon.LoadAsync(this);   // this = this GameObject's scope (same as step 4); any scope can be passed instead
```

In the editor you can skip step 2 altogether: with [editor simulation](#editor-simulation) (on by default) a
registration of a profile's output folder is served from the project in Play Mode — new, edited and deleted assets
are what you see, and the folder does not even have to exist. Build when you ship.

## Names

- A name is the asset path **relative to the profile's source root**, without the extension: `Icons/sword`.
  Categories do not change names — `Icons/sword` is `Icons/sword` whichever catalog holds it.
- **Sub-assets** are `<main>[<sub>]`: the sprite `sword` inside `sword.png` is `Icons/sword[sword]`; a sprite
  sheet `sheet.png` gives `Icons/sheet[run_01]`, `Icons/sheet[run_02]`, …
- **Scenes** are named like assets: `Scenes/Arena`. Assets and scenes share one name space.
- Names are **unique per profile**. Two assets that would get the same name (two categories overlapping, or the same
  name in two folders) fail the build with both paths in the console.
- The inspector drawer shows a name's state: plain for empty, **amber** for a name no catalog has yet (build, or run
  *Update Content Catalog*), **red** when the name resolves to something the field cannot hold.

## Building content

### Profiles

A **profile** describes one content directory. Profiles are saved in `ProjectSettings/ContentLinkBuild.asset`
(`ContentBuildSettings`) and edited in the build window. One profile is **active**: the menu items and the legacy
static API act on it.

| Field | Default (new profile) | Meaning |
|---|---|---|
| `Name` | — | Unique; the active profile and the command line pick by it |
| `SourceRoot` | `Assets/Content` | The folder names are relative to |
| `OutputPath` | `Build/Content/<Name>` | Where the directory is written. Outside `Assets/` by default — see below |
| `Catalogs` | one category, whole root | Each category = a sub-folder of the root + the catalog asset it fills |
| `Archive` | `Packed` | `None` (flat) · `Engine` (the engine's single archive) · `Packed` (size-split archives) |
| `MaxArchiveMB` | `20` | `Packed`: the size an archive is closed at, give or take one file |
| `Compression` | `LZ4` | `Uncompressed` · `LZ4` · `LZMA` — applies to archives; a flat build is always uncompressed |
| `CleanBuildCache` | off | Engine option: ignore the build cache |
| `DisableWriteTypeTree` | off | Smaller output that loads **only on the exact engine version** (`SerializeUnityVersion` is added automatically) |
| `FailBuildWhenErrorsLogged` | on | An error logged during the build fails it |
| `Target` | `NoTarget` | `NoTarget` = whatever build target is active; anything else refuses to build on another target |
| `PostProcessors` | none | Type names of `IContentBuildPostProcess` implementations, in the order they run |

**Categories.** Add a sub-folder (`Icons`, `Prefabs`, `Levels/Forest`, or `""` for the whole root) and the build puts
that folder's assets into a catalog of their own; all catalogs of a profile go into **one** directory, so the
runtime sees one name space. Overlapping categories, a catalog placed under its own root, and duplicate names are
refused before anything is written; so are a profile name with a path character (`/ \ : * ? " < > |` — the name
goes into archive names and default paths), a source root written with a trailing separator, and an output folder
inside the source root or containing it. A new category's catalog asset is created on the first build at
`Assets/ContentCatalogs/<Profile>.<Category>.asset` (the folder too, if it is missing); you can also point the field
at an existing catalog.

**Output folder.** The folder is checked before anything is written: only an empty folder or a previous content
build (it has a `BuildManifestHash.txt`) is accepted; a folder with anything else in it is left alone and the build
fails. The clear itself happens right before the engine runs, so a build that fails earlier — validation, the
catalogs — leaves the previous output usable.
An output under `Assets/` (for example `StreamingAssets`) is imported by Unity and refreshed after the build;
an output outside `Assets/` is not, which is what you want for content you distribute separately.

**Build target.** Content directories are built for the editor's **active build target** and may not load on
another one. The window, the log and the last-build summary always say which target a build was made for.

**Coming from the static builder.** Projects that set `ContentDirectoryBuilder.SourceRoot` / `CatalogPath` /
`OutputPath` from a script keep working: a project without settings gets a `Main` profile made from those defaults
(`Assets/Content` → `Assets/StreamingAssets/MainContent`, flat, uncompressed), and the static properties remain as a
view of the active profile. Setting them overrides the profile for the editor session only — the window says so.

### Archive modes and registration

| `Archive` | What is written | Register with |
|---|---|---|
| `None` | the engine's flat files (`*.cf`, `*.resS`, the manifest) | `ContentService.RegisterDirectory(path)` |
| `Engine` | one `content0.archive` (the engine's `UseArchive`) | `ContentService.RegisterDirectory(path)` |
| `Packed` | `<Profile>.manifest-<hash>.archive`, `<Profile>.<bits>-<hash>.archive`, … plus `archives.json` | `await ContentService.RegisterPackedDirectoryAsync(path)` |

`Packed` is for distribution: a single archive is a bad download unit, and the engine has no size option, so the
package packs the flat build itself. The default rule is a **hash-prefix trie**: the manifest and its hash file get a
small archive of their own; a file larger than the limit gets one of its own; every other file is bucketed by the
bits of the MD5 of its key — for a serialized file its manifest ID, which the engine derives from the asset's
identity and keeps when the content changes, for a `.resS`/`.resource` its name — and a bucket that does not fit
the limit is split on the next bit. An edited asset therefore stays in its archive, an added or removed one
touches one archive (and a neighbour when a bucket splits or merges) — every other archive keeps its name and
its bytes, which is what a patch pipeline wants. (A `.resS` is keyed by name, so an edited texture also moves its
pixel data from one archive to another.) Archives are named by their bit path
plus a hash of the file list (`Main.0110-a3f9c2e1.archive`), so one name never means two different contents.
**The same input always packs the same way, byte for byte.** Expect about 1.5× as many archives as a plain fill
would make (leaves run around 0.6× the limit); raise `MaxArchiveMB` if you want them nearer the limit. The limit
counts *input* bytes — with LZ4 the archives on disk come out around half of it. `archives.json` lists every archive
with its size, input size, CRC and the files inside: a patcher fetches it first, downloads the names it does not
have and deletes the ones that went away. The CRC is the value the engine reports for the archive's content — it
is not the CRC32 of the file on disk (measured), so verify a download by its size or your own hash.

**Custom partitioning.** Implement `IContentArchivePartitioner` in an Editor assembly and pick it in the profile
(`Partitioner`; on the command line `-contentlink.partitioner <type name>`, `trie` or `sequential`). It receives
the files with their sizes, the manifest files, the parsed manifest and the built loadables with their catalog and
file on disk, and returns the archives — name plus files. The built-in `SequentialFillPartitioner` is the plain
sorted fill, for fewer and fuller archives at the price of locality. The packer checks any answer (every file
exactly once, no empty archive, plain unique names) and calls the partitioner twice with the files in a different
order, failing the build when the answers differ. Put a content hash in your names, as the built-ins do, so a
cached download never goes stale. Switching partitioners or changing `MaxArchiveMB` re-partitions — a one-time
larger download.

At runtime `RegisterPackedDirectoryAsync` mounts every archive under one namespace and registers the mount path
like a flat directory; `UnregisterDirectory` unmounts them again. It returns `-1` (with an error), mounting nothing,
when the folder has no `archives.json`, when the index lists no archives, or when an archive's size on disk differs
from the index; it throws naming the archive when one does not mount.

Content that reached the device by download deserves a check before it is registered. `archives.json` records each
archive's `Bytes` and `Crc`: `RegisterPackedDirectoryAsync` refuses an archive whose size on disk differs from the
index before it mounts anything, but same-length corruption is caught only by comparing the `Crc`, which is the
caller's job — the engine blocks on a corrupted content file rather than failing, so verify first.

### Post-processors

```csharp
using xpTURN.ContentLink.Editor;

public sealed class UploadStep : IContentBuildPostProcess
{
    public void OnBuilt(ContentBuildResult r)
    {
        // r.ProfileName, r.Target, r.OutputPath, r.Files (sorted), r.Archives, r.Manifest, r.BuiltNames, r.Milliseconds
    }
}
```

Pick implementations in the window (it finds them with `TypeCache`) and order them there; the order is saved with
the profile and is the order they run. A type that cannot be found or a step that throws **fails the build** and
leaves the output in place for inspection. Any other stage that throws is reported the same way: the result carries
the reason, and the window, the log and the command line all show it. Steps run after packing, so `r.Archives` is
final.

### Menu items and scripting

| Where | What |
|---|---|
| *Window > ContentLink > Build Window* | Edit profiles, build one or all, see messages and the last result |
| *… > Build Content Directory* | Build the active profile |
| *… > Update Content Catalog* | Rewrite the active profile's catalogs without building (links turn from amber to plain) |
| `ContentBuildRunner.Build(profile)` / `BuildAll()` | The same from code; returns `ContentBuildResult` |

### Command line

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget StandaloneOSX -logFile <log> \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main -contentlink.output Build/Content/Main -contentlink.compression LZ4
```

`tools/build-content.sh` wraps that call (checks the project lock first, passes the exit code through, prints the
`[ContentLink …]` lines of the log):

```
tools/build-content.sh -p samples/LinkSample -contentlink.profile Main
tools/build-content.sh -p <project> -t Win64 -contentlink.profile all      # -t passes -buildTarget (Win64, OSXUniversal, Linux64, Android, iOS, …)
```

| Key (`-contentlink.<key> <value>`) | Value |
|---|---|
| `profile` | a profile name, or `all` (any case; every profile, stops at the first failure). Omitted = the active profile |
| `source`, `output` | paths |
| `archive` | `None` · `Engine` · `Packed` |
| `maxArchiveMB` | whole number ≥ 1 |
| `compression` | `Uncompressed` · `LZ4` · `LZMA` |
| `cleanBuildCache`, `disableWriteTypeTree`, `failBuildWhenErrorsLogged` | `true` · `false` |
| `postProcessors` | comma-separated type names |

Overrides change the named profile **in memory only** — the settings file is never written from the command line
(`profile all` accepts no overrides). A failed build, an unknown key or an unknown profile exits with code **1**.
The project must not be open in another editor instance: batch mode needs the project lock.

**Catalogs only.** `-executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` takes the same arguments
but only rewrites the profile's catalogs — no build, no output folder — the command-line twin of *Update Content
Catalog* (`tools/build-content.sh -c -contentlink.profile Main`). Use it when CI only needs the catalog assets current
(link validation before a player build) or when another pipeline builds the directories. Every build already rewrites
the catalogs, so there is nothing extra to switch on there. `profile all` updates every profile and stops at the first
failure; a `source` override is what the catalogs are named against. Exit codes as above.

## Loading content

### Scopes

| Scope | Created by | Ends on | Parent |
|---|---|---|---|
| Root | automatic (`ContentScope.Root`) | `Application.quitting` | — |
| Scene | `ContentScope.ForScene(scene)` | `SceneManager.sceneUnloaded` | Root |
| GameObject | `gameObject.Scope()` / `component.Scope()` | `OnDestroy` | its scene |
| Explicit | `parent.CreateChild("Stage")` | your `Dispose()`, else the parent | any |

**GameObject scope** — the everyday case. Assets go away with the object:

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        var scope = this.Scope();                                   // this GameObject's scope (gameObject.Scope() works too)
        image.sprite = await scope.LoadAsync<Sprite>("Icons/sword[sword]");
    }                                                               // released in OnDestroy — nothing else to write
}
```

**Scene scope** — assets that should live exactly as long as the scene:

```csharp
var scope  = ContentScope.ForScene(gameObject.scene);               // one per scene, created on first use
var skybox = await scope.LoadAsync<Material>("Materials/Sky");      // released when the scene unloads
```

**Explicit scope** — a stage, a screen, a popup: you decide when it ends, and the parent ends it at the latest:

```csharp
var stage = ContentScope.Root.CreateChild("Stage3");                // or this.Scope().CreateChild("Popup")
await stage.LoadAsync<AudioClip>("Wav/descent");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... the stage plays ...
stage.Dispose();                                                    // everything it held goes back
```

**Root scope** — for the lifetime of the app:

```csharp
var atlas = await ContentScope.Root.LoadAsync<Texture2D>("UI/Atlas");   // released at Application.quitting
```

**On every scope:**

```csharp
var mat   = scope.Load<Material>("Materials/Steel");                // sync (blocks on the engine load)
bool held = scope.Contains("Icons/sword");                          // true: loading it again is free
scope.Release("Icons/sword");                                       // optional early return
var token = scope.DisposedToken;                                    // cancelled when the scope ends — pass it to your own awaits
```

- **Dedupe.** A key is held at most once per scope. Loading the same key repeatedly (even every frame) never grows it.
- **Leak signal.** An explicit scope reclaimed by a scene unload or by quit logs a warning with its path
  (`Root/Field/ForgottenPopup`). `ContentService.CaptureStackTrace = true` adds where it was created. Ending with a
  disposed parent or a destroyed owner GameObject is a normal lifetime and does not warn.
- **Over-retention warning.** `ContentScope.DefaultWarnThreshold` (or `scope.WarnThreshold`) warns when a scope holds
  more than that many assets — the failure mode of scopes is holding too much, not leaking.
- **Never-activated objects** never get `OnDestroy`; their scope is reclaimed when the scene unloads.
- **Moved objects** (`MoveGameObjectToScene`, `DontDestroyOnLoad`) keep their assets: when the original scene
  unloads, their scope is handed over to the scene they live in now.
- **Across scopes.** `source.ShareTo(target, key)` gives `target` its own reference; `target.Adopt(asset)` takes an
  asset you already hold. Never hand a raw asset to another system without one of these.
- Scopes release assets; they never destroy GameObjects.

### Serialized links

```csharp
public ContentLinkSprite icon;                               // ContentLinkGameObject, ContentLinkTexture2D too
public class ContentLinkAudio : ContentLink<AudioClip> { }   // derive for other types; the drawer works for these as well

image.sprite = await icon.LoadAsync(this);                   // owner required (null throws ArgumentNullException) — a Component/GameObject means its GameObject scope, a ContentScope means that scope
image.sprite = await icon.LoadAsync(stage);                  // e.g. into an explicit scope
var clip = music.Load(this);                                 // sync

// Equipment swap: return the old one early (optional)
icon.Release();
icon.SetName(table[itemId].IconName);
image.sprite = await icon.LoadAsync(this);

icon.IsValid; icon.Asset; icon.IsLoading; icon.Scope; icon.RuntimeKeyIsValid();
```

### Instances

```csharp
var go = await scope.InstantiateAsync("Prefabs/Cat", parent);
```

The prefab reference belongs to the **instance**, not to `scope` (which only cancels the request if it ends first).
Destroying the instance returns it. Clones made with `Object.Instantiate(instance)` inherit the reference in `Awake`,
so a clone keeps its textures even if the original is destroyed first. No spawner class is needed: a reference is a
table slot, so one per instance is cheap.

### Scenes

Scenes under the source root become scene entries (`Scenes/Arena`) and are built into the directory with their
dependencies. A scene is not reference counted: its lifetime is the scene instance. ContentLink resolves the name,
tracks every loaded content scene (whoever loaded it) so a directory cannot be unregistered while one is open, and
lets a scope own a scene.

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

- Single-mode scenes cannot be owned (they close the owner too). A Single-mode load also destroys the caller: pass
  `destroyCancellationToken` when awaiting from a MonoBehaviour.
- When an owning scope ends while its scene is the last loaded scene, the scene stays and a warning is logged; at
  application quit nothing is unloaded.
- Cancellation stops the wait; an additive scene is unloaded again once the engine load completes.
- A scope owns the instance its own load produced: a scene you opened yourself through `SceneManager` stays yours
  even when a scope loads the same scene, and a failed scope load never claims it.
- `ContentService.LoadedSceneCount` / `SnapshotScenes(list)` show what is open.

### Raw handles

For code that manages lifetime itself (pools, streaming systems):

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");   // or Acquire<T> (sync)
h.Asset; h.IsValid; h.Status; h.Key;
var h2 = h.Share();          // a second reference
h.Dispose();                 // return it — or hand it to a scope instead:
scope.Attach(h2);            // the scope now owns it

if (ContentService.TryGetLoadableObjectId("Icons/sword", out var id))   // engine id, for id-based loads
    h = await ContentService.AcquireAsync<Texture2D>(id);
```

`ContentService.Contains(name)` / `ContainsScene(name)` tell whether a registered directory carries a name.

## When to call Release

| Situation | What to do |
|---|---|
| Component, popup, stage, scene content | Nothing. Load into the matching scope. |
| Stage / screen with many assets | One explicit scope, `Dispose()` when the stage ends. |
| Scrolling list with recycled cells | A child scope per cell; dispose it when the cell is rebound. |
| Equipment / skin swap | `Release(key)` or `ContentLink.Release()` before loading the new one. |
| Handing an asset to another system | `ShareTo(target, key)` or `target.Adopt(asset)`. Never pass a raw asset across scopes. |

## How release works

A loaded asset goes back to the engine in three steps: **the scope notices its own end** → **it lets go of every
reference it held** → **assets whose count reached zero are returned once, at the end of the frame.**

### 1. How a scope knows it has ended

| Scope | The signal | What happens inside |
|---|---|---|
| **GameObject** | the object's `OnDestroy` | The first `gameObject.Scope()` call adds a hidden `ContentScopeOwner` component; its `OnDestroy` disposes the scope. An object that was never activated never gets `OnDestroy`, so its scope is reclaimed as a child by the **scene scope** when the scene unloads. |
| **Scene** | `SceneManager.sceneUnloaded` | `ContentScope.ForScene(scene)` creates the scope keyed by the scene handle. On `sceneUnloaded` that scope is disposed. Child GameObject scopes whose object **moved to another scene** (`MoveGameObjectToScene` · `DontDestroyOnLoad` — the owner's `scene` is no longer this one) are not released but re-parented under the scene they live in now. |
| **Root** | `Application.quitting` | `ContentService.Shutdown()` disposes Root, and the whole tree cascades. Calling `Dispose()` on Root yourself only logs an error. |
| **Explicit** | your `Dispose()` | Without it, the parent's end cascades into it — and when that parent is a scene or Root, a **leak warning** is logged with the path and the creating stack. Ending through an explicit parent or a destroyed owner GameObject is a normal lifetime and does not warn. |

Clones (`Object.Instantiate`) inherit the key list that `ContentScopeOwner` serializes and take **their own references**
in `Awake` (shared while the original still holds them, reloaded if it is already gone). A clone created inactive has
its `Awake` deferred and takes them on first activation.

### 2. What a scope does when it ends — in order

1. `IsDisposed = true`, `DisposedToken` is cancelled — loads still in flight for that scope are cancelled.
2. Child scopes are ended in reverse order (with the hand-over rule above).
3. Every keyed reference (`LoadAsync`/`Load`) and every `Attach`ed handle is released → each asset's count drops.
4. Scenes the scope owns are unloaded (not at application quit — the engine tears them down itself).
5. An explicit scope reclaimed by a scene or Root logs the warning.

### 3. After the count reaches zero

There is one slot per asset (the same name from any scope is the same slot), and the slot carries the count. When
it reaches zero the slot is **not freed on the spot**: it is queued with the frame number. The actual release is
done by `ContentService.Collect()`, which the package inserts at the end of `PostLateUpdate` in the player loop —
**once per frame**, after all of your code has run.

For every queued slot `Collect()` does one of:

- someone acquired it again meanwhile and the count is no longer zero → it just leaves the queue (**release and
  re-acquire within one frame never reloads**);
- it is still loading → it is left alone until the load finishes;
- fewer than `ReleaseDelayFrames` frames have passed since it hit zero → it is looked at again next frame;
- otherwise → the engine's `Loadable.Release()` is called and the slot is cleared. The slot's generation number
  moves on, so a load that completes late cannot touch the empty slot.

With `ContentService.AutoCollect = false` the player-loop hook does nothing and you call `Collect()` yourself.

### Directories and shutdown

`ContentService.UnregisterDirectory(dir)` refuses while assets from that directory are still held or a scene from it
is open, because the engine requires every `Loadable` released and every scene unloaded first; `force: true`
invalidates the remaining asset references (never open scenes), and the scopes that held them then see only live
references — `Count`, `DumpTree` and `Release(key)` drop a killed reference quietly. A packed directory unmounts its
archives when it is unregistered. `ContentService.Shutdown()` (also run at `Application.quitting`) disposes the scope tree, reports
leftover raw handles and unregisters everything.

## Editor simulation

In the editor the project stands in for the content directories. When `RegisterDirectory` (or
`RegisterPackedDirectoryAsync`) is called in Play Mode with a path that is some **profile's output folder**, nothing
is registered with the engine: the names come from the profile's source root as it is on disk right now, filtered
by the profile's categories, and each asset is loaded from the project. Edit a texture and press Play — you see the
edit. Delete a file — the load fails. Add a file — it loads. Clone the repository and press Play — no build needed.

```
Window > ContentLink > Editor Simulation     toggle (on by default, per project)
```

The equivalent of Addressables' *Use Asset Database* play mode: an authoring convenience, not a substitute for the
build. Turn it off to check that a build would behave the same — the editor then registers the built directories
for real, exactly as a player does.

- **Nothing is written.** The index (name → path) is in memory, built when a directory is registered or Play is
  entered, and dropped at Play exit.
- **Only a profile's output folder is served this way.** Any other path (a downloaded copy, an ad-hoc build) is
  registered for real; under such a directory's root, a name no catalog carries yet is still resolved from the
  project (the original simulation), and a name in two places is answered by the real catalog.
- **Registration is still required.** A forgotten `RegisterDirectory` fails in the editor as it would in a player;
  registering the same folder twice is refused, and a profile whose source root does not exist throws.
- **The toggle is read when a directory is registered.** Turning it off mid-Play keeps directories already served
  from the project working until the next Play; only names under real directories stop resolving. The Play-exit
  summary and the reset run whatever the toggle says at that moment.
- **Released assets are not unloaded.** Reference counting is unchanged, but the editor keeps the object alive, so
  memory readings during authoring are higher than a build's. Confirm unload behaviour with the toggle off. Holding a
  project-served asset never blocks unregistering a directory.
- **Edits during Play show in place** (a reimport refreshes the loaded object); a deleted asset's object is
  destroyed under any handle that still holds it, and a new load of it fails with an error.
- **Archives are not exercised.** `Packed` and `Engine` outputs are served from the project like any other; mount
  and archive behaviour needs the toggle off.
- **Simulated scenes** open through `EditorSceneManager.LoadSceneAsyncInPlayMode`; the scope that asked for one
  still unloads it, but they belong to no directory.
- **Duplicate names are refused**, the way the builder refuses them, so a name cannot work only in the editor.

Each such registration logs one line saying which profile and root serve it. Leaving Play Mode logs one line naming
the names the simulation resolved — loaded, or only checked with `RuntimeKeyIsValid` / `Contains` — that the
profile's catalogs do not carry yet: the links that need a build before they ship;
`ContentService.SnapshotSimulated(list)` reports every project-served name while playing.
`ContentService.TryGetLoadableObjectId(name, out id)` answers project-served names too.

## Diagnostics

- *Window > ContentLink > Dump Scope Tree* (Play Mode), or `ContentScope.DumpTree()`:

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```
  Children are listed in the order the scope holds them, which is not creation order: removing a scope moves the last
  sibling into its slot, so that removal costs the same whatever the tree's size.

- `ContentService.Snapshot(list)` — every live reference (with a stack trace when `CaptureStackTrace` is on)
- `ContentService.SnapshotScenes(list)` / `LoadedSceneCount` — open content scenes
- `ContentService.SnapshotSimulated(list)` — names the editor simulation resolved
- `ContentService.LiveAssetCount` — assets currently held
- The build window shows the last build of the editor session (profile, target, files, time, errors); the log has
  one `[ContentLink] built …` line per build

## Coming from AssetLink or Addressables

| Before | ContentLink |
|---|---|
| `AssetLink.LoadAssetAsync()` + `ReleaseAsset()` | `ContentLink.LoadAsync(owner)`; `Release()` optional |
| `AssetLinkSpawner.SpawnAsync()` + `DoAutoRelease` | `scope.InstantiateAsync()` |
| `AddressablesTracker.ReportUnreferencedHandles()` (GC based) | scope warnings at scene unload / quit (deterministic) |
| Auto Regist Folder Pattern | build profiles and categories |
| `AssetRef` (GUID) | `ContentService.TryGetLoadableObjectId(name, out id)` → `AcquireAsync<T>(id)` + `scope.Attach(handle)` |
| *Use Asset Database* play mode | editor simulation |
| Addressables groups / bundles | profiles / categories, `Packed` archives |

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
- A content directory is built for one build target and may not load on another.
- `Packed` archive sizes are a limit on input bytes, not on the compressed file. The hash-prefix trie makes about
  1.5× as many archives as a plain fill; an archive hovering at the limit may split and merge between builds; and
  changing `MaxArchiveMB` or the partitioner re-partitions the output.
- Building needs the project lock: close the project in other editor instances before a command-line build.

## Repository layout and tests

| Path | What |
|---|---|
| `com.xpTURN.ContentLink/` | The package (`Runtime`, `Editor`, `Tests~/Verify`, `README`, `CHANGELOG`) |
| `samples/LinkSample/` | A Unity 6.6 project using the package from `file:` — textures, a sprite atlas, prefabs, scenes and audio under `Assets/Content` |
| `tests/LinkTests/` | The Unity Test Runner project: EditMode (builder, settings, packer, window model) and PlayMode (lifetime, scenes, packed registration, allocation) suites |
| `tools/run-all-tests.sh` | Runs everything: the dotnet harness, then EditMode, then PlayMode (`--player` adds a standalone player run) |
| `tools/build-content.sh` | Command-line content build |
| `docs/IMP/` | Design and implementation records (Korean) |
| `.github/` | CI (`workflows/check.yml`): the dotnet harness, the `.meta` sidecar check and the version-manifest check on every push and pull request; the Unity suites need a licensed editor and stay local |

`Tests~/Verify` compiles the package against stubs of the documented Unity 6.6 API and a fake engine that enforces
the Loadable reference rules, and runs the lifecycle, build and packing scenarios (`dotnet run` with the .NET 8 SDK).
The Unity suites run against content directories built by the tests themselves, in the editor and in a standalone
player. Verified with Unity 6000.6.0f1.

```
tools/run-all-tests.sh                 # harness + EditMode + PlayMode
tools/run-all-tests.sh --dotnet-only   # harness only (seconds)
tools/run-all-tests.sh --player        # also PlayMode in a StandaloneOSX player
```

## License

Apache License 2.0
