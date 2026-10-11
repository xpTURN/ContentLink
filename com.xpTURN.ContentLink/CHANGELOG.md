# Changelog

## [0.3.0] - 2026-10-04

Downloads: `ContentRemote` keeps a `Packed` directory from a CDN in a cache — resumed, checked against MD5, with the
shipped build and the last cache as fallbacks — on version 2 of the packed index, so packed folders built by 0.2.x
have to be rebuilt (a packed profile's name now takes only ASCII letters, digits, `.`, `-` and `_`). Also `Resources`
as a second source (`res:` names), names that keep their extension, sprites named through their atlas, and locales as
overlay directories (README).

### Loading content
- **Resources as a second source.** A name that starts with `res:` loads from a `Resources` folder built into the
  player — `res:UI/logo` (the file's main asset), `res:UI/sheet[s0]` (a sprite by name, split at the last `[`), the link
  `("res:UI/sheet", "s0")` — through the same scopes, links, `Release`, `ShareTo`, clones and `Collect` as a catalog
  name. When the last reference goes, `Collect()` calls `Resources.UnloadAsset`; prefabs and components only drop the
  reference. `ContentService.SweepResources()` releases the zero-reference slots at once and runs
  `Resources.UnloadUnusedAssets`. The engine hands one object to every caller of a path, so an object another `res:`
  name still holds stays (a name in another case, a nested `Resources` folder's two paths), and a texture waits while a
  sprite of its file is held, to go with the path's last slot — a sweep in between keeps it waiting. A path in Hangul,
  kana or an accented script is found in either Unicode form, as the player keeps it (on macOS the folders decomposed).
  `ContentService.UnloadResourcesOnRelease = false` leaves the unload to the sweep while code that calls
  `Resources.Load` directly remains. `Contains` and
  `RuntimeKeyIsValid` check a `res:` name's form only; `ContentRef` and scenes do not reach `Resources`. New:
  `ContentService.ResourcesPrefix`, `ResourcesDirectory` (the `LeaseInfo.Dir` of a `res:` asset — it never blocks
  `UnregisterDirectory`), `SweepResources()`, `UnloadResourcesOnRelease`. In the editor a `res:` name — in Edit and
  Play Mode — loads what a player's `Resources` holds: a file in a `Resources` folder under an `Editor` folder fails as
  in a player, and a path it shares with a player's file loads the player's.
- **Localized content** (README): a locale is a profile whose source root mirrors the base root, registered after the
  base — the most recently registered directory answers a name both carry. The section covers setting up the profiles
  (a sibling root, the same paths and name style), registering them (a region after its language), the switch at run
  time with one scope and `UnregisterDirectory`, the limits (direct references, a name opened before the locale, a
  reference to the locale's own asset, a base asset referenced from a locale asset is carried and loaded twice) and
  pairing it with Unity Localization's `SelectedLocaleChanged`.
- **Remote content (CDN)** (README). `ContentRemote` keeps a `Packed` directory from a server in a cache folder
  (`<CacheRoot>/<profile>`, under `persistentDataPath` by default) that registers like any packed directory.
  `CheckAsync(profile, baseUrl, options)` asks for `archives.hash` first — one request when the cache holds that index
  whole — then `archives.json`, checked against the hash, and refuses before any download an index for another profile,
  one this player cannot load, or one whose archive names leave its folder, name a Windows device or come twice — a name
  never reaches the disk outside the cache folder. Unreachable or refused, it falls back to the cache's last commit,
  then the build the app ships (`BuiltInDirectory`, copied into the cache), else `Source = None`; `Error` says why.
  `DownloadAsync()` copies the shipped archives of the same name and MD5, downloads the rest to `.part` files that
  resume with HTTP Range and get their names only when size and MD5 match, and commits the index last; a failure — a
  file the cache cannot take too — keeps the previous commit and returns `false`; a cancel keeps the `.part` files, and
  calling `DownloadAsync` again plans again from the cache. A download fails, committing nothing, when the cache has
  lost an archive since the check. A committed `archives.json` that cannot be read counts as none (logged).
  Ending Play Mode stops the downloads it started and frees their folders for the next session (domain reload off).
  `BytesToDownload` (a whole `.part` counts nothing — only its check is left; it grows when a file has to come again
  whole: a server that ignores Range, a file that fails its MD5, a failed built-in copy), `ContentVersion`, and
  `Status` — a struct, polled without allocating; within a call its `DownloadedBytes` never goes back and ends at
  `TotalBytes`.
  `CleanCache()` deletes the archives the committed index no longer lists, never a mounted one;
  `ContentRemote.ClearCache(profile)` deletes a profile's cache folder (refused while registered or being registered).
  `CheckAsync` throws `ArgumentException`, before
  any request, for a name a packed profile cannot have; `ClearCache` and `CacheDirectoryOf`, for one that is no plain
  file name (`..`, `a:b`, `CON`). `ContentRemoteOptions` takes Addressables' names and defaults — `Timeout` (seconds
  without a new byte; the request is aborted and retried), `RetryCount`, `RedirectLimit`, `WebRequestOverride`,
  `TransformUrl`, `VerifyCachedFiles` — and `ContentRemote.MaxConcurrentWebRequests` counts over every download. The
  package declares the built-in `com.unity.modules.unitywebrequest` module.
- `RegisterPackedDirectoryAsync` returns `-1`, mounting nothing, for an index this player cannot load: another platform;
  built without type trees by another engine or catalog layout; built by a newer engine. An index it cannot use is one
  error: the reason, not a second line saying the folder has no index.
- **Fixed:** a packed index that named a file outside its folder (`../x.archive`, an absolute path) or one archive
  twice was followed: the size check, the mount and the registration all reached that file. Such an index is refused
  wherever it is read — a folder, a download, the cache's commit, the built-in copy — and
  `ContentArchives.ParseIndex` / `ReadIndex` return null for it (logged).
- **Fixed:** registering a packed folder that was already registered mounted its archives again, and when the engine
  refused the registration the first one's new loads all failed (the assets it held lived on). The second registration
  now throws the same `InvalidOperationException` before it mounts anything.
- **Fixed:** two `RegisterPackedDirectoryAsync` calls for one folder at once both mounted its archives, and the one the
  engine refused unmounted the files the other was using — its new loads failed. The second now throws the same
  `InvalidOperationException` before it mounts anything; while a folder is being registered, `ContentRemote.ClearCache`
  refuses it and a download leaves its archives alone.
- **Fixed:** a packed registration whose mount failed or gave up left the archives still mounting — and the failed one —
  mounted for the rest of the session; a retry mounted them twice. Every archive it started is unmounted again, whatever
  its state.
- **Fixed:** `ContentArchives.UnmountLeftovers()` — the reset at the start of a session — counted a failed mount it left
  listed. It unmounts every mount of ours it finds, a failed one too, and returns how many.
- The package declares the built-in `com.unity.modules.jsonserialize` module too: its runtime already used it
  (`JsonUtility` reads `archives.json`) without declaring it.
- **Fixed:** a load cancelled while it waited — by the caller's token or by its scope's end — threw an
  `OperationCanceledException` whose `CancellationToken` was `None`, scene loads too; 0.2.1 promised the token that
  cancelled. It now carries the caller's token or the scope's (the caller's when both are cancelled), as a caller's token
  cancelled before the call always did, and the scope's `DisposedToken` read after its end is that token too (it was
  another cancelled token).
- **Fixed:** a cancelled token now wins over a held key and over a miss on every load, as it already did for an asset no
  catalog carries: a scope's `LoadAsync` of a key it held returned the asset, and a scene load of a name or reference no
  catalog carries — through a scope, a `ContentSceneLink` or `ContentService` — logged an error and returned an invalid
  scene (in the editor, a name threw instead). Both throw `OperationCanceledException` with that token.
- **Fixed:** `ContentScope.ShareTo(target, null)` threw `ArgumentNullException` from a dictionary; it returns false, as
  `Release(null)` and `Contains(null)` do. `ContentSceneLink.LoadAsync(owner)` with a null owner threw
  `NullReferenceException` from inside it (a destroyed one, the engine's exception for a destroyed object); both throw
  `ArgumentNullException` for `owner` now, as with `ContentLink<T>`.
- **Fixed:** unregistering a directory while a scene load started through ContentLink was still in flight from it crashed
  Unity — the editor and a player alike, a cancelled load's too until the engine had finished it. `UnregisterDirectory`
  refuses it now, forced or not, with an error naming the loads, as for an open scene. New:
  `ContentService.HasSceneLoadsInFlight(dir)` — false once it no longer refuses for that. Ending Play Mode with an asset
  load in flight no longer reports it as a leaked raw handle nor refuses its directory with an error: the directory is
  left registered, like one with an open scene, and the next Play session releases it (a player's quit lets loads finish
  first).
- **Fixed:** a clone asked for its scope before its owner woke — created inactive and touched (`clone.Scope()`, or
  `ContentLink<T>.LoadAsync(clone)`), or active, by an earlier component's `Awake` or `OnEnable` — never took the
  original's references: the scope came back empty, the owner's `Awake` skipped the inheritance, and the clone's own
  loads overwrote the inherited key list. When the original was destroyed first, its assets unloaded under the clone.
  The first `Scope()` now takes them, as `Awake` does.
- **Fixed:** an explicit `Dispose()` of a GameObject's scope left the keys it had held in the list its clones inherit: a
  clone made afterwards loaded them again. The list now empties with the scope — a `Scope()` asked while that Dispose's
  cancellation runs gets a new empty scope too — and the next `Scope()` starts empty as before.
- **Fixed:** `Scope()` asked while its GameObject was being destroyed — by a load's cancellation running inside
  `OnDestroy`, or by another component's `OnDestroy` — made a new scope that nothing disposed until its scene unloaded.
  It now hands back the ended scope: loads into it throw `ObjectDisposedException`. The same goes for an object that
  never had a scope, where `Scope()` threw `NullReferenceException`: Unity adds no component to an object being
  destroyed.
- **Fixed:** a callback on a scope's `DisposedToken` that threw stopped the scope's end halfway: it stayed
  half-disposed, holding its assets and listed under its parent for the rest of the session, and `Dispose()` threw the
  `AggregateException`. The scope now ends completely and the exception is logged.
- **Fixed:** a clone that took over a key while another load of it was still in flight answered its own `Load` /
  `LoadAsync` of that key with null and a type-mismatch error. It now waits for that load.
- **Fixed:** a name, a link or a reference in another Unicode form loads what its composed (NFC) form names — a catalog
  an earlier version built on macOS holds names as that editor spelled them — and a scope holds it under the composed
  name, one key whichever form loaded it.
- **Fixed:** an asset opened by one directory's name and given by another — registered later, it carries the same asset
  under its own name (two profiles whose roots nest, an atlas that packs another profile's sprite) — let that other
  directory be unregistered under it: the engine unloaded the object while its handle stayed valid, and the name
  answered a destroyed object until every reference was let go. `UnregisterDirectory` refuses that directory too now,
  naming the asset and the directory whose name opened it; forced, the asset's leases go with it. A load through a
  scope or a link of an object unloaded under its holder says so instead of reporting another type.

### Serialized links
- The inspector names an asset in a `Resources` folder `res:…` (a sprite: `("res:UI/sheet", "s0")`; by the innermost
  `Resources` folder, in a package too) and refuses what a player would not load, with the reason: a `Resources` folder
  under an `Editor` folder, a path several files share, a scene, a file whose name ends with `]`, a sprite whose name
  holds `[`. A `res:` name's state follows what a player's `Resources.Load` gives — for a path a player's file shares
  with an editor-only one, the player's file — with no catalog in the project too. A reference (`ContentRef`) refuses a
  `Resources` asset and says to link it by name.
- **Fixed:** the inspector follows a profile edit — the name a picked asset gets, whether a name or a reference resolves,
  and the state it shows. Moving a profile's source root in the build window, overriding `ContentDirectoryBuilder`'s
  static view from a script, a command-line override and an undo made while the build window was closed all left the
  answers from before the change until an asset was next created or deleted.
- **Fixed:** a catalog rewritten in place is seen at once — *Update Content Catalog*, the command line's catalogs-only
  update, a script, or a version-control update followed by a refresh. New names stayed amber and old ones valid until
  the next project change: Unity raises `projectChanged` neither for saving an existing asset nor for refreshing a
  changed one when only its content changed.
- **Fixed:** the inspector follows a prefab saved with or without the component a `ContentRef<T>` field holds — in
  the editor, by a script, or by a version-control update and a refresh. The reference stayed red after the prefab
  gained `T`, or valid after it lost it (a load fails the type), until the next project change: saving a prefab, or
  refreshing one whose components changed, raises no `projectChanged`.
- **Fixed:** a name no catalog had yet that ends with `]` — a file `foo[1].png`, a scene `L[1].unity` — showed no
  asset in the inspector right after the pick, or beside `foo.png` with a sprite `1` that sprite: the editor's path
  search split every such name at its last `[`. It now answers as the builder names files — the file of that name or
  the sprite, and nothing when one root holds both or two files share a name (`a.png` and `a.mat`), which the build
  refuses and the inspector showed one of. Picking an asset whose name the build would refuse is refused, with the
  files named, as a `res:` path several files share is. The editor simulation finds such a file made during Play
  too, and serves neither of two files of one name made then.
- **Changed:** the tooltip of a name no catalog has names both cases — a new asset (update the catalogs) and an old
  name: the asset was renamed or moved, or its root's name style changed (pick the asset again).
- **Sprites through an atlas.** A reference holding an atlas GUID and a sprite name — what Addressables writes for an
  atlas sprite — resolves and shows the atlas's name for the sprite; a sprite the atlas does not pack is red, a variant
  or V1 atlas amber with the reason, and the atlas itself in a sprite field says to pick a sprite it packs. An atlas
  dropped on a sprite field (a link or a reference) opens a menu of the sprites it packs and writes the atlas's name. A
  sprite whose texture lies outside every content root is named through the atlas (a reference takes the atlas GUID);
  a sprite under a root keeps its own name. Saving an atlas updates all this at once — it raises no `projectChanged`.
  In Play Mode the editor simulation serves an atlas's name the build lacks yet, by name, reference and link — and
  follows each save, as the inspector does (a sprite already loaded stays until it is released).

### Building content
- A profile's **name style** (`NameStyle`): `PathWithExtension` keeps the extension in every name of its root —
  `Icons/sword.png`, `Icons/sword.png[sword]`, `Scenes/Arena.unity` — so a `sword.png` and a `sword.prefab` can sit
  side by side. The default, `PathWithoutExtension`, names as before, and settings and catalogs written before read
  as it. A catalog records the style it was written with (`ContentCatalog.NameStyle`); profiles on one root must agree;
  the build window has the field and says when a profile and its catalogs disagree, a catalog update that changes the
  style logs a warning, and the command line takes `-contentlink.nameStyle`. `ContentDirectoryBuilder.NameStyle` joins
  the static view. The editor's path fallback, the inspector and the editor simulation name with the root's style.
- **Fixed:** the build window's errors and notes follow the project as well as the settings: a source root folder
  created after the profile kept its "not a folder" error until a setting was edited.
- The builder refuses a name that starts with `res:` (a folder so named at the top of a source root): it would read as
  a `Resources` name.
- **Sprite atlases.** A Sprite Atlas V2 under a source root names every sprite it packs once more, through the atlas —
  `Atlas/UI[icon]`, the link `("Atlas/UI", "icon")` — with the sprite's own id, the atlas GUID and the sprite name. The
  name loads the sprite itself, bound to the atlas page by the build; a texture outside the root is pulled in and named
  through the atlas only. A variant atlas names nothing and a sprite packed by two atlases is named in each (both with
  a warning); two sprites of one name in an atlas fail the build as a duplicate, naming both textures. The atlas is
  expanded as it is at the scan.
- The builder refuses a build that would load sprites without a texture, with the reason: an atlas under the root with
  the Sprite Packer off (it would be built empty), an atlas packing a catalogued sprite under a V1 packer mode, and —
  under a V2 mode — a V1 atlas (`.spriteatlas`) under the root, held by an asset under it, or packing a sprite the
  content uses: one the catalog names or one a prefab, scene or asset under the root references, wherever its texture
  lies. Content directories carry no V1 atlas pages, so such a sprite loaded with a null texture before.
- **Archive names** are ASCII letters, digits, `.`, `-` and `_`, end with `.archive` and are no Windows device name
  (`CON`, `NUL.x.archive`) — what a URL, a CDN key and every file system carry as is. The packer refuses another name
  from any partitioner (a `#` would be cut off as a URL fragment, a space or `%` would reach the CDN as another key)
  and the reader refuses an index that lists one. New: `ContentArchives.ArchiveExtension`, `IsPlainFileName`,
  `IsPortableName`, `IsArchiveName`; `ArchiveNaming.Extension` is the same constant.
- **Profile names** that are no file name on every platform are refused in every mode — a control character, a
  trailing dot or space, a Windows device name (`CON`, `aux.ko`), besides `/ \ : * ? " < > |` as before — and a
  `Packed` profile's name takes only ASCII letters, digits, `.`, `-` and `_`: it goes into the archive names, the
  remote folder and the cache folder. The build window and the command line refuse such a profile before anything is
  written, its catalogs-only update too.
- **Fixed:** two categories that resolved to one catalog asset — `A/B` and `A.B` by their default paths, a category
  added under the name a renamed one had, one asset picked twice, the same path in another case — wrote it in turn,
  and the one written last replaced the other's names: a catalogs-only update reported success, and a build failed in
  the engine ("specified more than once") only after the catalog was rewritten and the output folder cleared. The
  build window, the command line and a catalogs-only update now refuse it before anything is written, naming both
  categories; so is a category without a catalog, which the build refused only when it ran.
- **Changed:** a catalog that another profile writes from another source root or sub-folder is refused in both
  profiles: each build or catalog update replaced the other's names, which the inspector and the editor simulation
  read. Give each profile its own catalog; a twin category — same source root, same sub-folder — may share one.
- **Fixed:** a `ContentCatalog` asset under a source root — another profile's, a copy, one made by hand — was taken as
  content: the build carried every asset it names into the directory, and with both directories registered,
  unregistering the later one unloaded assets held through the other's names. A build, a catalogs-only update and the
  static `ContentDirectoryBuilder.UpdateCatalog()` refuse it now — the static view, which catalogued its own catalog
  under its root, before making it; the inspector neither picks it nor resolves a name to it, and the editor simulation
  does not serve it (one a catalog already names keeps that name). A refused scan's build error no longer blames
  duplicate names whatever the reason.
- **Fixed:** content that holds another catalog, or the `LoadableObjectId` / `LoadableSceneId` of an asset or scene
  another catalog names — itself or through an asset it references — made the build carry it with no name: with both
  directories registered, the later one served it and unloaded it, a whole scene too, when it went. The build (and the
  static `Build()`, which now returns null after the engine ran too) fails after the engine runs, one error per
  catalog that names what it carries; a load target no catalog names is a warning. `BuildAll` (the window's and the
  command line's all) updates every profile's catalogs before it builds any. An `Engine` archive keeps its manifest
  inside and is not checked — its summary says so. A catalog saved under another extension (`.preset`, `.mat` …) now
  drops the editor's caches when it is saved again.
- **Packed index version 2.** `archives.json` records each archive's MD5 (`Md5`, of the file's bytes) and the build's
  target, engine version, type trees, catalog layout (`ContentCatalog.Layout`) and content version; `archives.hash`
  beside it holds the MD5 of its bytes — what a remote check asks for first.
- **Changed:** a packed folder built before (index version 1 — 0.2.x) is refused with a "rebuild" error, by a
  registration and by a remote check: rebuild it with this package.
- **Content version**: one string stamped into every packed index — *Content version* in the build window (a project
  setting, undoable), or `-contentlink.contentVersion` for one command-line run, accepted with `profile all` and in place
  of the project's value. A downloaded directory reports it (`ContentRemote.ContentVersion`), so an app can keep a
  locale of another build out.
- **Fixed:** `tools/build-content.sh` (the repository's wrapper) stopped before starting Unity under macOS's default
  bash 3.2 when no `-contentlink.<key>` option was given — the call that builds the active profile — and a `-p`, `-t`
  or `-l` without its value stopped the same way; both exited 1, the code of a failed build. An option given in place
  of a value was taken as the value (`-l -c` built instead of updating the catalogs; `-contentlink.output -c` built
  into a folder named `-c`), and a log it could not read as a file (`-l -`, Unity's log to the console) made it wait
  on its input. The call without options now runs; a missing or empty value, or an option in its place, is refused
  with exit 2; `-l -` works.
- **Fixed:** a profile's source root or category spelled in another case than its folder (`Assets/content` for
  `Assets/Content`, a category `icons` for `Icons/`) passed validation — the editor finds a folder in any case — but
  a build matched the category as written and left its assets out with one warning, and under such a root nothing a
  catalog did not hold yet was named (a new pick, a reference, the path fallback) while the editor simulation served
  nothing. Validation now refuses both, naming the spelling in the project; a catalog update or build refuses such a
  category by the scanned assets wherever the editor does not find the folder, and the editor simulation refuses
  such a root. A root that differed only in case built the right names before and is refused too. The build window
  and `-contentlink.source` store a root or category typed in another case as the project spells it, and a pick or
  reference under such a root says so. A source root written with doubled separators (`Assets//Content`, whose names
  each lost a character) is refused like one with a trailing separator.
- **Fixed:** the command line took an option given in place of a value as the value —
  `-contentlink.output -contentlink.profile Two` built the active profile into a folder named `-contentlink.profile` and
  exited 0 — and any number for `archive` and `compression`: `-contentlink.archive 7` built flat and uncompressed, as
  did such a value in the settings file. Both are refused now: a value is never a `-contentlink.` option, in any case
  (`-rc1` is still a value), and a mode takes only the numbers it has; validation refuses such a mode too.
  `postProcessors` keeps a type named with its assembly (`Namespace.Type, Assembly`, the form the build window stores)
  as one name: the comma split it in two, and the build failed after the post-processors before it had run; a pair that
  also reads as two post-processors is refused with both ways out. A post-processor that is not found is refused
  before anything is written, by every build and catalogs-only update, as a missing partitioner was. A profile named
  `all` in any case, which `-contentlink.profile all` could never build alone, is refused. Overrides are the run's: the
  profile is put back after it, so an open build window no longer shows them or saves them with its next edit.
- **Fixed:** a build cut short — a clear that could not remove an entry another program held open (Windows), or the
  editor closed or a job cancelled while the engine ran — left an output folder the next build refused as "neither
  empty nor a previous content build": the clear removed the previous build's marker first, and the engine writes the
  new one last. The marker now stays until the engine writes it again — one it could not write, read-only, goes as
  before — and a clear that cannot remove an entry names it. `ContentDirectoryBuilder.Build()` (the static
  single-catalog build) guards its output folder as a profile build does and removes what an archived build leaves
  before it builds: it built into any folder — where the engine removes the files of its own kinds (`.cf`, `.archive`,
  `.resS`, `.resource`) and any file named by a hash, another tool's too — and over a packed build it left
  `archives.json` and `archives.hash` naming archives that were gone, and under `Assets/` each archive's `.meta`. Other
  files in a folder it built before stay, as they always did.
- **Fixed:** names are composed (NFC). On macOS the editor spells a folder in Hangul, kana with a voiced mark or an
  accented letter decomposed and a file as it is on disk: a category typed composed — as an IME types it — left its
  assets out with one warning, a source root typed composed cut the names wrong or named nothing, and names differed
  from the ones typed and from machine to machine. Names are made and compared composed now — the inspector, the
  catalogs, the build and the simulation — and the next catalog update rewrites the names an earlier version wrote.

### Editor simulation
- **Fixed:** two profiles that share a name — a locale overlay, a patch directory. Once both roots were known (the
  other profile built once was enough, registered or not) the simulation dropped the name with a "duplicate name"
  warning, and before that it could serve the other profile's file without a word (the longer root first). It now
  answers with the most recently registered directory, as built directories do; two files of one name within a
  profile are still refused. A reference to a file whose name another profile shares — an asset, a sprite or a scene —
  resolves only while its own profile is served, as in a player, whether or not the other profile was ever built. A file
  named `main[sub]` beside a `main` with the sprite `sub` (or an atlas packing it) is a duplicate, as the builder says:
  neither resolves, with one warning; across profiles the registration order picks.
- **Fixed:** a scene's name asked through the asset API: `Contains` answered true and `LoadAsync` returned the
  `SceneAsset` in the editor; now `Contains` is false and a load logs the player's hint (use `LoadSceneAsync`).
- **Fixed:** loading one scene twice at once, or again right after a cancelled load, failed one of the loads (both, when
  a registration came between them) and left an instance untracked; each load now gets its own instance, as built scenes
  do.
- **Fixed:** turning the simulation off during Play with no profile's directory served failed a scene load in flight and
  emptied the exit summary; it now only stops answering new names.
- **Fixed:** `UnregisterDirectory` let a directory served from the project go while a scene of its files was open or
  still loading, a cancelled load's included; it refuses now, forced or not, as for a built scene.
- `ContentRemote.CheckAsync` for a profile the simulation serves answers `Source = Simulated` without a request; its
  `CacheDirectory` is the profile's output folder, so boot code written for downloads registers the project in Play Mode.

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
