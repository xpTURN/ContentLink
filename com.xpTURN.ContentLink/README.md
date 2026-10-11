# xpTURN.ContentLink
[![Unity 6.6+](https://img.shields.io/badge/unity-6.6%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Apache License Version 2.0](https://img.shields.io/badge/License-Apache-brightgreen.svg)](https://github.com/xpTURN/Klotho/blob/main/LICENSE)

ContentLink is a complete content workflow for Unity 6.6, built on the engine's **content directories**. A content
directory is a folder of built assets that a player registers and loads from at run time. ContentLink takes an asset
from your project folder to the screen. You refer to it **by name**, build it, ship it inside the player or from a
CDN, and load it from a script or an inspector field. When whatever loaded it goes away, it is released by itself.

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;

    async void Start()
    {
        // Assets/Content/Icons/sword.png — loaded by name, released when this GameObject is destroyed
        image.sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");
    }
}
```

> **Refer to it by name, from the editor to a CDN — and forget about releasing it.** An asset lives exactly as long
> as its owner — the GameObject, the scene, the app — and `Release()` is only ever an optimization, never a
> requirement.

---

## Contents

1. [What you get](#what-you-get)
2. [Install](#install)
3. [How it fits together](#how-it-fits-together)
4. [Tutorial: your first content directory](#tutorial-your-first-content-directory)
5. [Setting up catalogs](#setting-up-catalogs)
6. [Building](#building)
7. [Loading](#loading)
8. [Releasing](#releasing)
9. [Downloading content from a CDN](#downloading-content-from-a-cdn)
10. [Localized content](#localized-content)
11. [Editor simulation](#editor-simulation)
12. [Diagnostics](#diagnostics)
13. [Coming from Addressables or Resources](#coming-from-addressables-or-resources)
14. [Good to know](#good-to-know)
15. [License](#license)

---

## What you get

| Step | What ContentLink gives you |
|---|---|
| **Name** | Every asset under a folder (`Assets/Content`) gets a name from its path: `Icons/sword`, `Prefabs/Cat`, `Scenes/Arena` |
| **Pick** | Inspector fields that hold a name (`ContentLinkSprite icon;`) or a GUID (`ContentRef<Sprite>`), with drag & drop and a status color |
| **Work** | In the editor, Play Mode loads straight from the project — nothing to build while you work |
| **Build** | A build window with profiles, catalogs, compression and size-split archives; a command line for CI |
| **Ship** | Inside the player (`StreamingAssets`), or downloaded from a CDN — only the archives that changed, resumed and MD5-checked |
| **Load** | Sprites, prefabs, audio, any asset type, prefab instances and scenes — async or sync, by name |
| **Release** | Automatic: every load belongs to a **scope** (a GameObject, a scene, the app, or one you create) and goes when it ends |
| **Inspect** | A scope tree dump, live reference snapshots, and a warning naming any scope you forgot to end |

The only dependency is Unity 6000.6.

## Install

*Window > Package Manager > + > Add package from git URL*:

```
https://github.com/xpTURN/ContentLink.git?path=/com.xpTURN.ContentLink
```

Or, from a local checkout, add it to `Packages/manifest.json`:

```json
"com.xpturn.contentlink": "file:../../ContentLink/com.xpTURN.ContentLink"
```

`samples/LinkSample` in this repository is a small Unity project set up exactly like the tutorial below: textures, a
sprite atlas, prefabs, scenes and audio under `Assets/Content`, and a bootstrap script that registers and loads them.

## How it fits together

```
 In the editor                                   At run time (a player, or Play Mode)
 ─────────────────────────────────────────────   ─────────────────────────────────────────────────────
 Assets/Content/Icons/sword.png                  1. register the folder once at startup
        │                                            ContentService.RegisterDirectory(".../MainContent")
        │  profile "Main"                        2. load by name into a scope
        ▼                                            this.Scope().LoadAsync<Sprite>("Icons/sword[sword]")
 catalog  "Icons/sword" → engine id              3. the scope ends → the asset is released
        │
        │  Build
        ▼
 StreamingAssets/MainContent/  (the content directory: built assets + the catalog)
```

| Term | What it is |
|---|---|
| **Source root** | The folder whose assets are built. `Assets/Content` by default. |
| **Name** | What you load by: the asset's path under the source root, without the extension — `Icons/sword`. |
| **Catalog** | A `ContentCatalog` asset listing every name and the engine id behind it. The window writes it; you never edit it. The build puts it into the content directory. |
| **Profile** | The build settings for one content directory: source root, output folder, catalogs, archive format. |
| **Content directory** | The build's output folder. At run time you register it, then load from it by name. |
| **Scope** | The owner of what you load. When the scope ends, everything it loaded is released. |
| **Link / Reference** | A serialized field that holds a name (link) or an asset GUID (reference). Set it in the inspector. |

## Tutorial: your first content directory

This walks through one profile, one catalog and one build: content shipped inside the player.

### Step 1 — Put assets under the source root

Create the folder `Assets/Content` and put your content in it, in any sub-folders you like:

```
Assets/Content/Icons/sword.png        (Texture Type: Sprite)
Assets/Content/Prefabs/Cat.prefab
Assets/Content/Audio/click.wav
Assets/Content/Scenes/Arena.unity
```

Each file gets a name — its path under `Assets/Content`, without the extension:

| File | Name | Loads as |
|---|---|---|
| `Icons/sword.png` | `Icons/sword` | the `Texture2D` |
| — its sprite `sword` | `Icons/sword[sword]` | the `Sprite` |
| `Prefabs/Cat.prefab` | `Prefabs/Cat` | the prefab (`GameObject`) |
| `Audio/click.wav` | `Audio/click` | the `AudioClip` |
| `Scenes/Arena.unity` | `Scenes/Arena` | a scene, loaded with `LoadSceneAsync` |

Assets these files use — a material's textures, a prefab's meshes — are built with them automatically. Only what you
load by name has to be under the root.

### Step 2 — Open the build window

*Window > ContentLink > Build Window*. The first time, the window holds one profile, `Main`:

| Field | Value | Meaning |
|---|---|---|
| Name | `Main` | The profile's name |
| Name style | `PathWithoutExtension` | Names without the extension (`Icons/sword`) — see [Name style](#name-style) |
| Source root | `Assets/Content` | The folder whose assets are built |
| Output path | `Assets/StreamingAssets/MainContent` | Where the build is written — `StreamingAssets` ships inside the player |
| Categories | one row: *(whole root)* | The whole root goes into one catalog, created at `Assets/ContentCatalog.asset` |
| Archive | `None` | Plain files — see [Archive formats](#archive-formats) |
| Compression | `Uncompressed` | Applies to archives only |
| Target | `NoTarget` | Build for whichever platform is active |

The settings are saved in `ProjectSettings/ContentLinkBuild.asset`. Commit that file.

### Step 3 — Create the catalog

Press **Update catalogs** at the bottom of the window. It scans `Assets/Content` and writes
`Assets/ContentCatalog.asset`. Select that asset in the Project window: the inspector lists every name.

- Commit the catalog asset. Inspector fields read it to show whether a name is valid.
- You never edit it. Every **Update catalogs** and every build rewrites it.
- Keep it **outside** the source root — a catalog under the root is refused.

You don't need to press **Update catalogs** before every build: a build updates the catalogs itself. Press it when
you add or move assets and want the inspector to see the new names right away.

### Step 4 — Register the directory and load

Register the content directory once, when the app starts, before anything loads. A `Boot` component in your first
scene is enough:

```csharp
using System.IO;
using UnityEngine;
using xpTURN.ContentLink;

public class Boot : MonoBehaviour
{
    void Awake()
    {
        var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
        ContentService.RegisterDirectory(path);   // Archive = None or Engine
        // Archive = Packed:  await ContentService.RegisterPackedDirectoryAsync(path);  (in an async method)
    }
}
```

Then load by name. `this.Scope()` is this GameObject's scope: the sprite is released when the GameObject is destroyed.

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

Or let the inspector pick the asset — drop `Assets/Content/Icons/sword.png` on the field:

```csharp
public class ItemView : MonoBehaviour
{
    public Image image;
    public ContentLinkSprite icon;   // the inspector stores "Icons/sword" + sprite "sword"

    async void Start() => image.sprite = await icon.LoadAsync(this);
}
```

Press Play. It works **without a build**: in the editor, registering a profile's output folder serves that profile from
the project ([editor simulation](#editor-simulation)). Add, edit or delete an asset and press Play again — you see it.

### Step 5 — Build

Press **Build 'Main'**. The window shows the result under *Last build*, and `Assets/StreamingAssets/MainContent` now
holds the content directory: the engine's files and the catalog.

Then build the player as usual (*File > Build Profiles*). `StreamingAssets` goes into the player, and `Boot` finds the
folder there.

- **Build content before every player build** in which content changed. The player carries what the last content
  build wrote.
- **A content directory is built for one platform** — the one active in the editor. Switch the platform, then build the
  content again.

### Step 6 — Check the build in the editor (optional)

Turn off *Window > ContentLink > Editor Simulation* and press Play: the editor now loads the built folder, as a player
does. Turn it back on to go back to working without builds.

That is the whole cycle: put assets under the root, load them by name, build before you ship.

## Setting up catalogs

### Names

- A name is the asset's path under the source root, without the extension: `Assets/Content/Icons/sword.png` →
  `Icons/sword`.
- A **sprite** is `<file>[<sprite>]`: `Icons/sword[sword]`. A sprite sheet `sheet.png` gives `Icons/sheet[run_01]`,
  `Icons/sheet[run_02]`, …
- A **sprite atlas** (Sprite Atlas V2) under the root also names every sprite it packs through the atlas:
  `Atlas/UI[icon]`. The sprite's own name and the atlas's name load the same sprite.
- A **scene** is named like an asset (`Scenes/Arena`) and loaded with `LoadSceneAsync`.
- Names are **unique within a profile**. Two assets that would get one name — `sword.png` and `sword.prefab` in the
  same folder — fail the build, naming both files.
- Names are case-sensitive and keep the file's spelling. Hangul, kana and accented names work the same on every
  machine.

### Name style

A profile's **Name style** decides whether names keep the extension:

| Name style | `Icons/sword.png` | `sword.png` and `sword.prefab` side by side |
|---|---|---|
| `PathWithoutExtension` (default) | `Icons/sword` | refused — both are `Icons/sword` |
| `PathWithExtension` | `Icons/sword.png` | allowed — `Icons/sword.png` and `Icons/sword.prefab` |

**Pick it early.** Changing it renames every name of the root at the next catalog update: links, code and data that
hold the old names stop loading. `ContentRef` fields hold a GUID and are not affected. The window shows a note while
the profile and its catalogs disagree.

### Categories: several catalogs in one directory

By default the whole root goes into one catalog. Large projects can split the root by sub-folder, each sub-folder
with a catalog file of its own — smaller files, and people working in different folders don't touch the same catalog.

In the build window, under **Categories**:

1. Remove the *(whole root)* row with **−**, or keep it for a single catalog.
2. Type a sub-folder of the root in **Add category** — `Characters` — and press **Add**. Repeat for `UI` and `Levels`.
3. Leave the **Catalog** column empty: each catalog is created on the first build or catalog update, at
   `Assets/ContentCatalogs/<Profile>.<Category>.asset` (the window shows the path). Or pick an existing
   `ContentCatalog` (*Assets > Create > xpTURN > ContentLink > Content Catalog*).

```
Profile "Main"                 source root Assets/Content
  Category  Characters    →    Assets/ContentCatalogs/Main.Characters.asset
  Category  UI            →    Assets/ContentCatalogs/Main.UI.asset
  Category  Levels        →    Assets/ContentCatalogs/Main.Levels.asset
```

- **Categories do not change names.** `Assets/Content/UI/logo.png` is `UI/logo` whichever catalog holds it.
- All catalogs of a profile go into **one** content directory: registering it once makes every name loadable.
- Categories may not overlap: the *(whole root)* row and a sub-folder row cannot be used together, nor `UI` and
  `UI/Icons`.
- Assets under the root that no category covers are **not built** — the build logs a warning with their count.

### Several profiles: separate content directories

Each profile builds one content directory, registered on its own. Use several when content ships or downloads
separately — a base game and a DLC pack, or a [locale](#localized-content).

| Profile | Source root | Output path | Archive |
|---|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` | `Packed` |
| `Dlc1` | `Assets/Dlc1` | `Build/Content/Dlc1` | `Packed` |

To add one, type its name in **New profile** and press **Add**. A new profile starts with an output path of
`Build/Content/<Name>`, `Packed` archives with LZ4 compression, and one catalog at
`Assets/ContentCatalogs/<Name>.asset`. **Set active** chooses the profile the *Window > ContentLink* menu items build.

- Keep source roots **side by side**, not one inside another.
- An asset under one root that references an asset under another root carries a **copy** of it into its own
  directory. Load shared assets by name instead of referencing them across roots.

## Building

### The build window

| Field | What to put there |
|---|---|
| **Content version** | Optional. A label stamped into every packed build — used to pair a base with its locales when downloading |
| **Name** | The profile's name. `Packed` profiles: letters, digits, `.`, `-` and `_` only (it names the archives) |
| **Name style** | See [Name style](#name-style) |
| **Source root** | The folder to build (**…** picks one) |
| **Output path** | Where to write the build (**…** picks one) — see [Where to put the output](#where-to-put-the-output) |
| **Target** | `NoTarget` builds for the active platform. Another value refuses to build on any other platform |
| **Categories** | Sub-folders and their catalogs — see [Categories](#categories-several-catalogs-in-one-directory) |
| **Archive** | `None` · `Engine` · `Packed` — see [Archive formats](#archive-formats) |
| **Max archive MB** | `Packed` only: about how much content goes into one archive (default 20) |
| **Compression** | `Uncompressed` · `LZ4` · `LZMA` — archives only. `LZ4` is a good default |
| **Clean build cache** | Rebuild everything, ignoring the engine's build cache |
| **Disable type trees** | Smaller output that loads **only on the exact same Unity version** |
| **Fail when errors are logged** | On by default: an error logged during the build fails it |
| **Partitioner** | `Packed` only: how files are grouped into archives (leave the default) |
| **Post-processors** | Steps that run after the build — see [After the build](#after-the-build) |

At the bottom: **Update catalogs** (write the catalogs only), **Build '\<profile\>'** and **Build all**. Problems are
listed above those buttons, before anything is built; a category's problem is shown under its row.

### Archive formats

| Archive | What the output looks like | Register with | Use it when |
|---|---|---|---|
| `None` | the engine's plain files | `ContentService.RegisterDirectory(path)` | content ships in the player and you want the simplest setup |
| `Engine` | one `content0.archive` | `ContentService.RegisterDirectory(path)` | one compressed file in the player |
| `Packed` | a few archives of about *Max archive MB* each, plus `archives.json` and `archives.hash` | `await ContentService.RegisterPackedDirectoryAsync(path)` | content is downloaded, or compressed but split for patching |

`Packed` builds are **deterministic**: the same input packs into the same archives, byte for byte, and a small change
touches only a few archives — the rest keep their names and bytes, so a patch downloads little. *Max archive MB*
counts content before compression; with LZ4 the files on disk come out around half of it.

### Where to put the output

| Output path | What happens |
|---|---|
| Under `Assets/StreamingAssets/` | The folder ships inside the player. Register `Application.streamingAssetsPath/<folder>` |
| Outside `Assets/` (e.g. `Build/Content/Main`) | Not imported, not in the player — upload it to a server ([CDN](#downloading-content-from-a-cdn)) or copy it yourself |

The output folder must be empty or hold a previous content build: a folder with anything else in it is left alone and
the build fails. A build that fails before the engine runs leaves the previous output in place.

### Example setups

**Everything inside the app** — the simplest:

| Field | Value |
|---|---|
| Output path | `Assets/StreamingAssets/MainContent` |
| Archive | `None` (or `Packed` + `LZ4` for smaller files) |

```csharp
var path = Path.Combine(Application.streamingAssetsPath, "MainContent");
ContentService.RegisterDirectory(path);                      // Archive = None
// await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

**Downloaded from a CDN** — updates without a new app build:

| Field | Value |
|---|---|
| Output path | `Build/Content/Main` |
| Archive | `Packed`, Compression `LZ4`, Max archive MB `20` |

Upload the output folder after each build, and download it at startup — see
[Downloading content from a CDN](#downloading-content-from-a-cdn).

**A base game plus downloadable packs** — one profile for each, as in
[Several profiles](#several-profiles-separate-content-directories).

### Common build errors

| The message | Fix |
|---|---|
| `source root '…' is not a folder` | Create the folder, or fix *Source root* |
| `duplicate name '…': … / …` | Rename one of the two files, or switch the profile to `PathWithExtension` |
| `'…' is a ContentCatalog under the source root` | Move the catalog asset out of the root |
| `categories '…' and '…' overlap` | Remove the *(whole root)* row, or the nested sub-folder |
| `output folder '…' is neither empty nor a previous content build` | Point *Output path* at an empty folder, or empty it |
| `… asset(s) under '…' belong to no category` (warning) | Add a category for that folder — those assets were not built |
| `'…' is a V1 sprite atlas under the source root` | Move the atlas to Sprite Atlas V2 — see [Good to know](#good-to-know) |
| `profile is for … but the active build target is …` | Switch the platform, or set *Target* to `NoTarget` |

### Building from the command line

For CI, build a profile in batch mode:

```
Unity -batchmode -nographics -quit -projectPath <project> -buildTarget Android -logFile build.log \
      -executeMethod xpTURN.ContentLink.Editor.ContentBuildCLI.Build \
      -contentlink.profile Main
```

| Option (`-contentlink.<key> <value>`) | Value |
|---|---|
| `profile` | a profile name, or `all` for every profile. Omitted: the active profile |
| `output`, `source` | override the paths for this run |
| `archive` | `None` · `Engine` · `Packed` |
| `compression` | `Uncompressed` · `LZ4` · `LZMA` |
| `maxArchiveMB` | a whole number ≥ 1 |
| `contentVersion` | the content version stamped into this run's packed builds |

Overrides apply to the run only: the settings file is never changed. A failed build, an unknown option or profile
exits with code **1**. Close the project in other editors first: batch mode needs the project lock.

`xpTURN.ContentLink.Editor.ContentBuildCLI.UpdateCatalogs` takes the same options and only writes the catalogs — useful
when CI needs current catalogs before a player build. In this repository, `tools/build-content.sh` wraps both calls.

From an editor script, `ContentBuildRunner.Build(profile)` and `ContentBuildRunner.BuildAll()` do the same and return a
`ContentBuildResult`.

### After the build

A post-processor runs after every build of the profiles it is added to — the place to upload or copy the output.
Put it in an Editor assembly:

```csharp
using xpTURN.ContentLink.Editor;

public sealed class UploadStep : IContentBuildPostProcess
{
    public void OnBuilt(ContentBuildResult r)
    {
        // r.ProfileName, r.Target, r.OutputPath, r.Files, r.Archives, r.Milliseconds
        UnityEngine.Debug.Log($"{r.ProfileName}: {r.Files.Length} files in {r.OutputPath}");
    }
}
```

Then add it under **Post-processors** in the build window; several run in the order listed there. A step that throws
fails the build and leaves the output in place.

## Loading

### Register first

Register every content directory once at startup, **before the first load**:

```csharp
int dir = ContentService.RegisterDirectory(path);                      // Archive = None or Engine
// int dir = await ContentService.RegisterPackedDirectoryAsync(path);  // Archive = Packed
```

Both return a directory id (`-1` and one logged error when the folder cannot be used). Registering the same folder
twice is refused. `ContentService.UnregisterDirectory(dir)` takes it away again — refused while one of its assets
is still held.

### Scopes

Every load goes into a **scope**. When the scope ends, what it loaded is released.

| Scope | Get it with | Ends when |
|---|---|---|
| GameObject | `this.Scope()` / `gameObject.Scope()` | the GameObject is destroyed |
| Scene | `ContentScope.ForScene(scene)` | the scene unloads |
| Explicit | `parent.CreateChild("Stage3")` | you call `Dispose()` — or its parent ends |
| Root | `ContentScope.Root` | the app quits |

```csharp
// GameObject — the everyday case
var sprite = await this.Scope().LoadAsync<Sprite>("Icons/sword[sword]");

// Scene — lives as long as the scene
var sky = await ContentScope.ForScene(gameObject.scene).LoadAsync<Material>("Materials/Sky");

// Explicit — a stage, a screen, a popup: you decide when it ends
var stage = ContentScope.Root.CreateChild("Stage3");
await stage.LoadAsync<AudioClip>("Audio/boss");
await stage.LoadAsync<GameObject>("Prefabs/Boss");
// ... the stage plays ...
stage.Dispose();   // everything the stage loaded goes back

// Root — for the whole app
var font = await ContentScope.Root.LoadAsync<Font>("Fonts/Main");
```

On any scope:

```csharp
var mat  = scope.Load<Material>("Materials/Steel");   // synchronous
bool has = scope.Contains("Icons/sword");             // already loaded here: loading again is free
scope.Release("Icons/sword");                         // optional: give it back early
```

- Loading the same name twice into one scope holds it once — loading it every frame is fine.
- Many scopes can load the same name: the asset is loaded once and released when the last scope lets go.
- To hand an asset to another scope, use `source.ShareTo(target, name)` or `target.Adopt(asset)`.

### Inspector fields: links

A **link** holds a name. Drop an asset under the source root on it in the inspector:

```csharp
public ContentLinkSprite icon;              // also ContentLinkGameObject, ContentLinkTexture2D
public ContentLink<AudioClip> music;        // any asset type

image.sprite = await icon.LoadAsync(this);  // into this GameObject's scope
var clip = music.Load(this);                // synchronous

// switch the item: give the old one back, load the new one
icon.Release();
icon.SetName("Icons/shield", "shield");
image.sprite = await icon.LoadAsync(this);
```

The field's color tells its state: plain — fine; **amber** — not in a catalog yet (press **Update catalogs**);
**red** — the name loads something the field cannot hold.

### Inspector fields: references by GUID

A **reference** holds the asset's GUID instead of its name: moving or renaming the file keeps it working.

```csharp
public ContentRef<Sprite> icon;
public ContentSceneRef level;

image.sprite = await this.Scope().LoadAsync(icon);
var scene = await this.Scope().LoadSceneAsync(level);
```

A reference and a name of the same asset load the same object: `Release(icon)` also releases what
`LoadAsync<Sprite>("Icons/sword")` loaded.

### Prefab instances

```csharp
var cat = await this.Scope().InstantiateAsync("Prefabs/Cat", parent);
```

The prefab stays loaded as long as the instance lives — destroy the instance and it is released. Clones made with
`Object.Instantiate(cat)` keep it loaded too, so no spawner class is needed.

### Scenes

```csharp
// owned by a scope: unloaded when the scope ends (additive)
var arena = await this.Scope().LoadSceneAsync("Scenes/Arena");
this.Scope().ReleaseScene(arena);   // optional: unload it early

// from an inspector field
public ContentSceneLink level;
var scene = await level.LoadAsync(this);

// not owned, any mode — unload it yourself with SceneManager.UnloadSceneAsync
var menu = await ContentService.LoadSceneAsync("Scenes/Menu", LoadSceneMode.Single);
```

### Resources folders

A name that starts with `res:` loads from a `Resources` folder in the player — through the same scopes and fields, so a
project can move off `Resources.Load` one call at a time:

```csharp
var boot  = this.Scope().Load<TextAsset>("res:Config/boot");                 // Resources/Config/boot.txt
var logo  = await this.Scope().LoadAsync<Sprite>("res:UI/logo[logo]");       // a sprite: [its name]
var enemy = await this.Scope().InstantiateAsync("res:Prefabs/Enemy");
```

Prefabs and the assets they use leave memory only with `ContentService.SweepResources()` — call it where a hitch is
fine, such as a loading screen.

### Raw handles

For code that manages lifetime itself (pools, streaming):

```csharp
AssetHandle<Texture2D> h = await ContentService.AcquireAsync<Texture2D>("Icons/sword");
var tex = h.Asset;
h.Dispose();   // give it back — or hand it to a scope: scope.Attach(h)
```

## Releasing

Most code never calls `Release`:

| Situation | What to do |
|---|---|
| A component, popup, stage or scene's content | Nothing — load into the matching scope |
| A stage or screen with many assets | One explicit scope; `Dispose()` it when the stage ends |
| A scrolling list that recycles cells | A child scope per cell; dispose it when the cell is reused |
| Swapping equipment or a skin | `Release(name)` or `link.Release()` before loading the new one |
| Handing an asset to another system | `ShareTo(target, name)` or `target.Adopt(asset)` |

**How it works.** When a scope ends — the GameObject destroyed, the scene unloaded, `Dispose()` called, the app
quitting — it lets go of everything it loaded. An asset nobody holds any more is released **once per frame, at the end
of the frame**, after all your code has run — never in the middle of it. Letting go and loading again within one frame
does not reload anything.

**Forgotten scopes.** An explicit scope that is still open when its scene unloads, or when the app quits, is ended for
you and logged as a warning with its path (`Root/Field/ForgottenPopup`). Set `ContentService.CaptureStackTrace = true`
to see where it was created.

## Downloading content from a CDN

A `Packed` profile's output can be downloaded into a cache folder and registered from there.

**1. Build and upload.** Use a `Packed` profile with an output path outside `Assets/`. After each build, upload the
whole output folder — the archives, `archives.json` and `archives.hash` — to one URL per platform and app version.
Archive names carry a hash of their content and can be cached forever; give `archives.json` and `archives.hash` a
short cache time on the CDN.

**2. Check, download, register** at startup:

```csharp
var options = new ContentRemoteOptions
{
    BuiltInDirectory = Path.Combine(Application.streamingAssetsPath, "MainContent"),   // optional: a copy the app ships
    RetryCount = 3,
    Timeout = 30,
};
var remote = await ContentRemote.CheckAsync("Main", $"https://cdn.example.com/content/{platform}/Main", options);

if (remote.BytesToDownload > 0)
    ShowDownloadSize(remote.BytesToDownload);

if (await remote.DownloadAsync())   // poll remote.Status for progress
{
    await ContentService.RegisterPackedDirectoryAsync(remote.CacheDirectory);
    remote.CleanCache();            // delete archives the new build no longer uses
}
else
    Debug.LogError(remote.Error);
```

- Only archives the cache lacks are downloaded. An interrupted download resumes; a file whose MD5 does not match is
  never used.
- Offline, the check falls back to what the cache last downloaded, then to `BuiltInDirectory`. `remote.Source` says
  which one was used and `remote.Error` why.
- An archive the app already ships (`BuiltInDirectory`) is copied instead of downloaded.
- Register the cache folder **or** the built-in folder of a profile, never both: the engine refuses a second directory
  of the same build.
- A download while the folder is registered is safe; it takes effect the next time the folder is registered.
- In the editor with the simulation on, the check answers from the project without any request. Turn the simulation
  off to try real downloads.

| Option | Default | Meaning |
|---|---|---|
| `CacheRoot` | `persistentDataPath/ContentLink` | where caches go: `<CacheRoot>/<profile>` |
| `BuiltInDirectory` | none | a packed build shipped with the app |
| `Timeout` | `0` (none) | seconds without a new byte before a request is retried |
| `RetryCount` | `0` | retries per request |
| `VerifyCachedFiles` | `false` | also check the MD5 of files already cached |
| `WebRequestOverride`, `TransformUrl` | none | adjust every request (headers) or URL (signing) |

## Localized content

A locale is a second profile that holds only the files a language changes, at the same paths. Registered after the
base, its files answer the names they share; every other name stays the base's.

| Profile | Source root | Output path |
|---|---|---|
| `Main` | `Assets/Content` | `Assets/StreamingAssets/MainContent` |
| `Main.ko` | `Assets/Content.ko` | `Assets/StreamingAssets/MainContent.ko` |

`Assets/Content.ko/Icons/flag.png` replaces `Assets/Content/Icons/flag.png` — both are `Icons/flag`. Keep the locale
root **beside** the base root, and give both profiles the same name style.

```csharp
static string Dir(string name) => Path.Combine(Application.streamingAssetsPath, name);

ContentService.RegisterDirectory(Dir("MainContent"));      // the base first
ContentService.RegisterDirectory(Dir("MainContent.ko"));   // then the locale, before the first load

var flag = await this.Scope().LoadAsync<Sprite>("Icons/flag");   // the Korean flag
```

**Switching languages while running.** Load what a locale changes into one scope, so a switch can let go of all of it:

```csharp
ContentScope localized = ContentScope.Root.CreateChild("Localized");
int locale = -1;

bool SwitchLocale(string code)   // "ko", "ja", … — null for the base alone
{
    localized.Dispose();                                    // let go of the old locale's assets
    if (locale >= 0 && !ContentService.UnregisterDirectory(locale))
        return false;                                       // still held somewhere — the error says what
    locale = code == null ? -1 : ContentService.RegisterDirectory(Dir("MainContent." + code));
    localized = ContentScope.Root.CreateChild("Localized");
    return true;                                            // load again: names now answer in the new language
}
```

With Unity's Localization package, let it translate strings and call `SwitchLocale` from
`LocalizationSettings.SelectedLocaleChanged`.

## Editor simulation

*Window > ContentLink > Editor Simulation* (on by default). In Play Mode, registering a profile's output folder serves
that profile **from the project**: new, edited and deleted assets are what you see, and nothing has to be built. The
folder does not even have to exist.

- Registration is still required, as in a player.
- Nothing is written to disk.
- Released assets stay in memory in the editor — check memory and release behaviour with the simulation off.
- Archives are not used. Turn the simulation off to test a real build, its archives and downloads.
- When Play Mode ends, the console lists names you loaded that no catalog has yet: build before you ship them.

## Diagnostics

- *Window > ContentLink > Dump Scope Tree* (Play Mode), or `ContentScope.DumpTree()`:

  ```
  Root [Root] 0 (peak 0)
    Lobby [Scene] 0 (peak 0)
      Hold [Explicit] 1 (peak 1) : Icons/sword
      Boss [GameObject] 1 (peak 1) : Icons/shield
  ```

- `ContentService.Snapshot(list)` — every reference held right now: its name, the frame it was taken, and where (with
  `ContentService.CaptureStackTrace = true`)
- `ContentService.LiveAssetCount` — how many assets are held
- `ContentScope.DefaultWarnThreshold` — warn when one scope holds more than this many assets
- The build window shows the last build; the console has one `[ContentLink] built …` line per build

## Coming from Addressables or Resources

| Addressables | ContentLink |
|---|---|
| groups and bundles | profiles and categories; `Packed` archives |
| `Addressables.LoadAssetAsync` + `Release` | `scope.LoadAsync`; releasing is automatic |
| `AssetReference` / `AssetReferenceSprite` field | `ContentRef<T>` — change the field's type, the stored GUID carries over |
| `ref.LoadAssetAsync()` + `ref.ReleaseAsset()` | `this.Scope().LoadAsync(ref)` |
| `InstantiateAsync` + `ReleaseInstance` | `scope.InstantiateAsync` — destroy the instance |
| *Use Asset Database* play mode | editor simulation |
| `CheckForCatalogUpdates` / `UpdateCatalogs` | `ContentRemote.CheckAsync` |
| `GetDownloadSizeAsync` / `DownloadDependenciesAsync` | `remote.BytesToDownload` / `remote.DownloadAsync()` |
| `ClearDependencyCacheAsync` | `ContentRemote.ClearCache(profile)` / `remote.CleanCache()` |

For a `ContentRef<T>` taken over from an `AssetReference`, the asset must lie under a content root (moving it there
keeps its GUID).

| Resources | ContentLink |
|---|---|
| `Resources.Load<TextAsset>("Config/boot")` | `scope.Load<TextAsset>("res:Config/boot")` |
| `Resources.Load<Sprite>("UI/logo")` | `scope.Load<Sprite>("res:UI/logo[logo]")` |
| `Resources.UnloadAsset(asset)` | nothing — the scope releases it |
| `Resources.UnloadUnusedAssets()` | `ContentService.SweepResources()` |

## Good to know

- **Main thread only.** Load, register and cancel from the main thread.
- **One platform per build.** A content directory built for one platform may not load on another.
- **Sprite atlases** need *Project Settings > Editor > Sprite Packer > Mode* set to Sprite Atlas V2. Legacy (V1)
  `.spriteatlas` files are refused under a content root — move them to V2.
- **Only sprites** can be loaded as sub-assets. A mesh inside a model file cannot be loaded on its own.
- **Scenes are not shared.** Loading a scene twice gives two instances, each unloaded on its own.
- **Inactive clones.** A clone created inactive holds nothing until it first wakes. Call `clone.Scope()` right after
  `Instantiate` if the original may be destroyed first.
- **A reference and a name share a slot.** Releasing one releases the other; use a link or a child scope when two
  fields must be released separately.
- **Keep content roots and `Resources` folders apart.** A file in both is built twice, under two names.
- **Don't stop Play Mode while a content scene is still loading** — the 6000.6 editor can crash in that case, with or
  without ContentLink.
- **In the editor `Resources.UnloadAsset` unloads nothing.** Check the release of `res:` assets in a player.

## License

Apache License 2.0
