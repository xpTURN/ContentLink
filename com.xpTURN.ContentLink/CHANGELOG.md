# Changelog

## [0.2.1] - 2026-09-20

Allocation-free hot paths in scopes — key changes on a GameObject scope, loads by id, waits on a load in flight,
loads with a caller's token, scene-scope creation — and linear scope filling. No API changes; `PeakCount` and the
`WarnThreshold` warning count entries (below).

### Loading and scopes
- Changed: a GameObject scope mirrors its keys into the owner's serialized array (what clones inherit) in place, and the
  array never shrinks, so a `Release(key)` followed by a `Load` — a pooled cell swapping its sprite — allocates nothing
  (it was three allocations per key change). A clone's `Awake` takes its leases first and syncs the array once
  afterwards, never into the array it is walking.
- Changed: a load by `LoadableObjectId` (`Acquire`, `AcquireAsync`) finds its slot by the id itself; the `#…` key
  text is made once when the slot opens. A hit allocated nine blocks per call before (the id's `ToString`), now none.
- Changed: a load that waits for an engine load already in flight registers its cancellation with a static delegate
  instead of a closure (two allocations fewer per waiter), and a scene scope reads the scene's name once instead of
  twice when it is created (one string fewer).
- Changed: adding to a scope no longer walks every entry it holds, so filling a scope with n assets is linear instead
  of quadratic (5000 assets: 88 ms → 4 ms on the test engine). `PeakCount` and the `WarnThreshold` warning now count
  the scope's entries — the same as its live count except after a forced unregistration, whose dead entries count
  until they are dropped. `Count` is unchanged and still counts live references only.
- Changed: a scope load given a caller's cancellation token no longer links it to the scope's own token through a
  `CancellationTokenSource` per load; both tokens are registered directly. Such a load allocates the same as one with
  no token (it was three allocations more). The `OperationCanceledException` now carries the token that cancelled —
  the caller's or the scope's — instead of a linked one.

## [0.2.0] - 2026-09-18

GUID references that survive moves and renames (`ContentRef<T>`, `ContentSceneRef`), and links that keep a sprite's
name apart from its main name.

### Serialized links
- `ContentRef<T>` and `ContentSceneRef` hold an asset's GUID (and a sprite's name) instead of its path, so moving or
  renaming the file does not break them. The fields have the names of Addressables' `AssetReference`
  (`m_AssetGUID`, `m_SubObjectName`), so changing an `AssetReferenceSprite` field to `ContentRef<Sprite>` keeps its data.
- A reference holds a key only — no load state, copies are harmless — and loads through a scope:
  `this.Scope().LoadAsync(icon)`, `Load`, `InstantiateAsync` (a component type is fine: the prefab is instantiated),
  `Release`, `Contains`, `ShareTo`, `LoadSceneAsync(level)`. `ContentService` has `TryGetName` / `TryGetSceneName`,
  `Contains` / `ContainsScene`, `Acquire` / `AcquireAsync` and `LoadSceneAsync` for references.
- A reference resolves to the catalog name the build recorded for its GUID and then loads by name, so a reference and
  a name load of the same asset are one slot and one scope key. `RuntimeKeyIsValid()` means "a registered catalog can
  load it", as for `ContentLink`; `IsSet` says whether a value is set. Lookups allocate nothing, hit or miss; a load
  that cannot resolve logs the reference and why (not set, a scene where an asset was expected or the reverse, a
  sprite name the asset does not have).
- `ContentLink<T>` stores a sprite as its main name and its sprite name (`m_Name`, `m_SubObjectName` — the field names
  of `ContentRef`): `("Icons/sheet", "s0")` loads the catalog name `Icons/sheet[s0]`, the same slot and scope key as a
  name or a reference load. `SetName(name, subObjectName)`, the constructor and `SubObjectName` take it, and the load
  and rename refusals name the sprite. A whole name with no sprite name still loads as it is, so a data-driven
  `SetName(table.IconName)` keeps working.
- **Changed:** `ContentLink<T>.Name` of a sprite link is the main name (`Icons/sheet`, not `Icons/sheet[s0]`), and
  `SetName(name)` clears the sprite name: copy a link with `SetName(a.Name, a.SubObjectName)`.
- `ContentService.TryGetName(name, sub, out catalogName)` and `Contains(name, sub)` answer whether a (main name,
  sprite name) pair can be loaded. After a pair's first lookup they allocate nothing — a miss too, in a player.
- Reference drawer: pick an asset to store its GUID; a read-only, copyable line shows the name it resolves to, red for
  a GUID with no asset, a renamed slice or the wrong type, amber for a file that is not built yet, lies outside the
  content roots, or is a sprite atlas. Click the link icon beside that line to switch between the name and the GUID.
  Values carried over from Addressables are shown, never rejected.
- Link drawer: picking a sprite writes both fields; the name line shows them in the catalog's form `Icons/sheet[s0]`,
  and a name typed there is split against the catalogs (a name no catalog has stays whole).

### Building content
- Catalog entries record the asset's GUID and a sprite's sub-asset name (`guid`, `sub`), and scene entries the scene's
  GUID. Rebuild content directories to resolve references against them.

### Editor simulation
- The simulation answers references too, from the same folder scan, and follows a file moved during Play. With
  nested content roots a reference takes the name under the root that is served.
- **Fixed:** a name the folder scan cannot see (a sprite `name[sub]`, a file added during Play) was filed under the
  deepest content root holding the file, so with nested profile roots it could be refused even though its own root
  was served.

## [0.1.0] - 2026-09-16

First release of ContentLink for Unity 6.6 content directories. Everything below is new.

### Loading and scopes
- Every load is owned by a **scope**: `scope.LoadAsync<T>("Icons/sword")` returns the asset, and the scope releases
  it when it ends. Scopes form a tree (Root → Scene → GameObject → Explicit) and end on their own: a GameObject
  scope ends when the object is destroyed, a scene scope when the scene unloads, an explicit scope when you
  `Dispose()` it. Get a scope with `gameObject.Scope()` / `component.Scope()`, or `scope.CreateChild(name)`.
- Loading the same name twice in one scope hands back the same asset and keeps a single reference, so calling
  `Load` from `Update` does not pile up. `ShareTo` / `Adopt` / `Attach` move or copy a reference between scopes.
- `scope.InstantiateAsync("Prefabs/Cat")` instantiates a prefab from a content directory; the prefab reference
  lives with the instance and follows its clones.
- Objects moved to another scene or marked `DontDestroyOnLoad` keep their assets: the reference is handed over
  to the destination scope.
- Engine releases happen at one point per frame (`ContentService.Collect`, run automatically), never in the middle
  of your code. `ReleaseDelayFrames` can hold released assets a few frames longer.
- Removing a scope from its parent is O(1) whatever the tree's size: each child keeps its index in the parent's
  list and a removal moves the last sibling into its slot, so `Children` and `DumpTree` list children in the order
  the scope holds them, not in creation order.
- Raw handles for code that wants to manage lifetime itself: `ContentService.Acquire<T>` / `AcquireAsync<T>` by
  name or by `LoadableObjectId`, `AssetHandle<T>.Share()` / `Dispose()`.
  `ContentService.TryGetLoadableObjectId(name, out id)` turns a catalog name into the id these take.
- A load the engine throws out of is reported like any other failure — the exception is logged, the handle is
  invalid, nothing stays held — on the synchronous and the asynchronous path alike.
- A `ContentLink.LoadAsync` is cancelled by the scope's own token when the scope ends; a cancellable caller token
  is linked to it, and a default token costs no allocation.
- Content directories: `ContentService.RegisterDirectory(path)` / `UnregisterDirectory(dir)`. Unregistering refuses
  while assets from that directory are still held and while one of its scenes is open (unload the scene first).
  `force: true` invalidates the remaining asset references (not while a load is still in flight), and the scopes
  that held them then see only live ones: `Count`, `DumpTree` and `Release(key)` drop a killed reference quietly.

### Serialized links
- `ContentLink<T>` (and the ready-made `ContentLinkSprite`, `ContentLinkTexture2D`, `ContentLinkGameObject`) holds a
  catalog name in a serialized field. Load it with an owner: `icon.LoadAsync(this)` — a Component or GameObject
  means its GameObject scope, a `ContentScope` means that scope, and a null owner throws `ArgumentNullException`.
  `Release()` is optional.
- `ContentSceneLink` does the same for a scene: `LoadAsync(owner)` loads it additively under the owner's scope,
  `UnloadAsync()` unloads it early.
- Inspector drawer for both (your own `ContentLink<T>` subclasses included): drop an asset on the field to fill in
  its catalog name, or type the name, and see at a glance whether the catalogs know it and whether the asset
  type matches.

### Scenes
- Scenes under the source root become scene entries in the catalog and load by name:
  `ContentService.LoadSceneAsync("Scenes/Arena", mode)` for a scene you manage yourself,
  `scope.LoadSceneAsync(name)` for an additive scene the scope unloads when it ends.
- Every content scene that is open is tracked (`LoadedSceneCount`, `SnapshotScenes`), and `ContainsScene` /
  `TryGetLoadableSceneId` answer questions about scene names.
- A scope owns only the scene instance its own load produced: a scene you opened yourself through `SceneManager`
  is tracked but never claimed, so a scope never unloads it.

### Building content
- **Build profiles** live in `ProjectSettings/ContentLinkBuild.asset` (`ContentBuildSettings`). A profile is one
  content directory: a source folder, an output folder, one or more categories (each written to its own catalog),
  archive mode, compression, engine options and post-processors. A project without settings gets a `Main`
  profile with the classic `ContentDirectoryBuilder` defaults, and the static `ContentDirectoryBuilder.SourceRoot`
  / `CatalogPath` / `OutputPath` keep working as a view of the active profile.
- **Build window** (*Window > ContentLink > Build Window*): edit profiles with undo, add categories, pick
  post-processors, read the validation messages, see the active build target and the last result, and build one
  profile or all of them. A new category's catalog is created on the first build, its folder included. The menu
  items *Build Content Directory* and *Update Content Catalog* build or refresh the active profile.
- **Archive modes**. `Packed` (the default) splits the build into archives of up to `MaxArchiveMB` with a
  hash-prefix trie — the manifest in a small archive of its own, oversized files alone, the rest bucketed by the
  bits of their key's MD5 (a serialized file's manifest ID, which survives content changes; a `.resS`'s name) — so
  an edited asset stays in its archive and the others keep their names and bytes; names carry the bit path and a
  hash of the file list, and the same input always packs the same way. An `archives.json` index lists the
  archives; at runtime `ContentService.RegisterPackedDirectoryAsync(path)` mounts them back into one directory and
  `UnregisterDirectory` unmounts. `Engine` uses the engine's single archive and `None` leaves the files flat.
  If the build manifest is missing or names no file, serialized files are keyed by name and the build warns, since
  an edit then moves data between archives.
- `RegisterPackedDirectoryAsync` returns `-1` with one error and mounts nothing when the folder has no
  `archives.json`, when the index lists no archives, or when an archive's size on disk differs from the index (a
  download cut short); it throws naming the archive when one does not mount. Same-length corruption is caught
  only by comparing the index's `Crc`, which is the caller's job — the engine blocks on a corrupted content file
  rather than failing, so verify downloads first. In the editor, entering Play unmounts the archives a previous
  Play left mounted, and only those in ContentLink's own namespaces.
- **Custom partitioning**: implement `IContentArchivePartitioner` to decide which files share an archive and what
  it is called, and pick it in the profile (`Partitioner`, CLI `-contentlink.partitioner`). The packer validates
  the answer (every file exactly once, no empty archive, names free of path characters on every platform) and
  rejects one that depends on the input order. `SequentialFillPartitioner` (the plain sorted fill) is built in as
  an alternative.
- **Post-processors**: implement `IContentBuildPostProcess` to run your own step after a build (upload, checksum,
  …). They run in the order listed in the profile; a missing type or an exception fails the build.
- The build result (`ContentBuildResult`) carries the output files, the parsed build manifest (with each
  serialized file's `ContentHash`, its name on disk) and the names that were built, each with its catalog and file
  (`Loadables`). A build stage that throws is a failed build with the reason in the result and the summary, for
  the window, the command line and code alike. Building into a folder under `Assets/` refreshes the AssetDatabase
  afterwards.
- Validation catches the usual mistakes before a build: an empty or missing source root, a profile name with a
  path character (`/ \ : * ? " < > |` — the name goes into archive file names and default paths), a catalog placed
  inside its own source root, overlapping categories, two profiles writing to the same output folder, a profile
  meant for another build target, a catalog that would have to be created outside `Assets/`, a source root written
  with a trailing separator (the window and the command line store typed paths normalised), an output folder inside
  the source root or containing it, a catalog inside the output folder. The build also refuses to clear an output
  folder that is neither empty nor a previous content build, and clears it only once the catalogs are written, so a
  failure before the engine runs leaves the previous build in place.

### Command line
- `ContentBuildCLI.Build` for `-executeMethod`: builds the active profile, a named one
  (`-contentlink.profile Name`) or all of them (`-contentlink.profile all`, any case). Any profile field can be
  overridden for that run with `-contentlink.<field> <value>` without touching the saved settings. Exit code 1 on
  failure.
- `ContentBuildCLI.UpdateCatalogs`: same arguments, but only rewrites the catalogs and builds nothing.
- `tools/build-content.sh` wraps both (`-c` for catalogs only).

### Editor simulation
- In Play Mode the project can stand in for a built content directory, so you can iterate without rebuilding.
  When a build profile's output folder is registered, nothing is registered with the engine: names come from the
  profile's source folder (filtered by its categories) and assets load straight from the project, so new, edited
  and deleted assets are what you see. The output folder does not even have to exist.
- Any other registered directory behaves like a real build, except that a name no catalog carries yet is also
  resolved from the project, so a freshly added asset can be linked and loaded before the next build. Scenes
  included. Nothing is written to a catalog by the simulation. An asset served this way belongs to no directory:
  holding it never blocks unregistering one, and `force` never releases it.
- Toggle: *Window > ContentLink > Editor Simulation*. It is read when a directory is registered; turning
  it off mid-Play keeps directories already served from the project working until the next Play, and the Play-exit
  summary and reset run whatever the toggle says at that moment. Each served registration logs one line, and the
  summary at Play exit lists the names that the profile's catalogs do not carry yet, so you know what still needs
  a build.
- The simulation is editor-only: in a player build every hook it uses is null and only built directories answer.

### Diagnostics
- `ContentScope.DumpTree()` (also *Window > ContentLink > Dump Scope Tree*) prints every scope with its
  live references; `WarnThreshold` / `DefaultWarnThreshold` warn when a scope holds more than expected.
- `ContentService.Snapshot` / `SnapshotScenes` / `SnapshotSimulated` list live assets, open content scenes and
  names resolved by the simulation; `CaptureStackTrace` records where each load came from.
- A scope that was not disposed is reclaimed by the scene-unload cascade with a leak warning that names the
  scope's path.
