# Bannerlord.ReferenceAssemblies

Reference assemblies for compiling mods and tools against Mount & Blade II: Bannerlord, its dedicated
server and its Modding Kit. The NuGet packages contain assembly metadata with the executable code removed.
See the .NET documentation on [reference assemblies](https://docs.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies).

## Package names

| | Release (`v1.2.3`) | Early access (`e1.2.3`) |
| --- | --- | --- |
| Game, all modules | `Bannerlord.ReferenceAssemblies` | `Bannerlord.ReferenceAssemblies.EarlyAccess` |
| Game, engine only | `Bannerlord.ReferenceAssemblies.Core` | `Bannerlord.ReferenceAssemblies.Core.EarlyAccess` |
| Game, one module | `Bannerlord.ReferenceAssemblies.SandBox` | `Bannerlord.ReferenceAssemblies.SandBox.EarlyAccess` |
| Dedicated server, everything | `Bannerlord.ReferenceAssemblies.Server` | `Bannerlord.ReferenceAssemblies.Server.EarlyAccess` |
| Dedicated server, engine only | `Bannerlord.ReferenceAssemblies.Server.Core` | `Bannerlord.ReferenceAssemblies.Server.Core.EarlyAccess` |
| Modding Kit, everything | `Bannerlord.ReferenceAssemblies.ModdingKit` | `Bannerlord.ReferenceAssemblies.ModdingKit.EarlyAccess` |
| Modding Kit, engine only | `Bannerlord.ReferenceAssemblies.ModdingKit.Core` | `Bannerlord.ReferenceAssemblies.ModdingKit.Core.EarlyAccess` |
| Game GUI, without DLC | `Bannerlord.ReferenceAssemblies.GUI.v3` | `Bannerlord.ReferenceAssemblies.GUI.v3.EarlyAccess` |
| Game GUI, one DLC module | `Bannerlord.ReferenceAssemblies.GUI.v3.NavalDLC` | `Bannerlord.ReferenceAssemblies.GUI.v3.NavalDLC.EarlyAccess` |
| Game GUI, every version with its DLC | `Bannerlord.ReferenceAssemblies.GUI.v3.All` | — |

Choose the package family for the application you target. Use the all-modules package for the game,
or select Core and individual module packages as needed; SandBox is shown above as an example.
The server packages cover both Windows and Linux. Use the Modding Kit packages for editor tools and
mods targeting `Win64_Shipping_wEditor`.

## Package versions

Match the package version to the build you target. The version combines the game version and changeset:
`v1.3.7` with changeset `102919` becomes `1.3.7.102919`.

- Early access versions use the `.EarlyAccess` package names.
- Builds that appeared only on the beta branch use a `-beta` suffix, such as `1.5.2.121216-beta`.
- Older builds without a changeset use the Steam build id as the fourth version component, such as `1.0.1.4842596`.

DLC packages follow the game package version. Their `moduleVersion:` tag records the DLC's own version.

## GUI packages

The GUI packages carry the game's UI for analyzers such as `Bannerlord.UIExtenderEx.Analyzers`, which check a
mod's prefab patches against every game version it supports. They add nothing to compilation or output, and
are marked as development dependencies. They carry none of the game's files, only data written from them, all
of it JSON. Each package contains:

- `gui/<Module>/GUI/Prefabs/**/*.json`: each prefab as a tree of `{ "n", "a", "c" }` nodes (the element name, its
  attributes and its children, in document order). It is the document the game loads, without comments and
  whitespace-only text, so an XPath gives the same answer on it as in game. The deepest prefabs nest to about 72
  JSON levels, past the default `MaxDepth` of 64 in System.Text.Json;
- `gui/prefabs.json`: the index of the trees, with each prefab's module, file, root tag, tags and parameters;
- `gui/brushes.json`: the brushes by name, with their base brush, font, layers, styles, animations and sounds, and
  the sprites these name; no visual values;
- `gui/sprites.json`: the sprite categories, with `alwaysLoad`, and every sprite a `Sprite` value can name, with its
  category;
- `gui/manifest.json`: the modules, with their ids, versions, types and dependencies, in load order;
- `gui/movies.json`: the movies the game loads and the ViewModel each is loaded with, traced from the
  assemblies' method bodies;
- `gui/spriteCategories.json`: the sprite categories each screen class loads, those loaded at start, and those
  loaded in every mission;
- `gui/fonts.json`: the fonts the game loads, by name;
- `gui/uiSounds.json`: the sound names a brush can give as `Audio`;
- `gui/types.json`: the widget classes with the events they raise and what they announce, the ViewModels, and
  the objects widget properties hold, with their members. A widget announces a change through
  `OnPropertyChanged(value, name)`, and the loader writes the value to the ViewModel property bound to that name:
  `announcements` lists each name with the types announced under it, which need not be the property's own (the
  alignments are announced as `System.String` from v1.1.0 on). Each ViewModel property carries the accessibility of
  its `getter` and `setter`, null when there is none, since the loader uses public accessors only;
- `build/<PackageId>.props`, which lists every file, `gui/**/*.json`, as `BannerlordGameGuiData` items.

The base package holds every module of the game itself. Each DLC module gets a package of its own, because a
DLC is optional: an analyzer checks a patch without it, and with it when the mod references the DLC package.
The packages are versioned exactly like the reference packages, so a mod restores the base and the DLC package
of the same build with the same `$(GameVersion).*`. Only the game app has GUI packages.

The `v3` in the id is the format of the contents, the same number as `formatVersion` in the JSON files. A published
package cannot be changed, so a format a consumer has to read differently, or new data every build must carry, is
published under new ids for every build, old ones included, and a consumer references the format it reads. Format 1,
`GUI.v1`, carried the game's prefab, brush and sprite data XML byte for byte; its packages are unlisted. Format 2,
`GUI.v2`, carried the same files as format 3 without the announcements and the ViewModel accessors.

## GUI package for every version

A project restores one version of a package id, so the per-build GUI packages show an analyzer only the game
version the project compiles against. `Bannerlord.ReferenceAssemblies.GUI.v3.All` holds every release version at
once, so an analyzer can check a mod against all the versions it supports in one build.

It holds the `GUI.v3` package of the newest build of each release version (`v…`), with that build's DLC packages;
a version still in beta is there as its beta. Early access versions are not. The data is format 3, value for value
as the per-build packages carry it, with every distinct record stored once:

- `gui/bundle.json`: every game version and its packages with their builds, and which table lines each of
  their files is made of;
- `gui/records.<file>.<property>.jsonl`: the records of one array of one format 3 file, such as
  `records.types.widgets.jsonl`, one per line;
- `gui/nodes.jsonl`: every prefab node, one per line, with its child elements as line numbers;
- `build/Bannerlord.ReferenceAssemblies.GUI.v3.All.props`, which lists `gui/*.json` and `gui/*.jsonl` as
  `BannerlordGameGuiData` items, with `Package="Bannerlord.ReferenceAssemblies.GUI.v3.All"`.

A consumer rebuilds a version's format 3 files from these, and reads them as it reads a per-build package. The
package holds every game version, so its version is the date it was packed and a run number: `YYYY.M.D.R`, such
as `2026.9.28.57`. Take the newest with `Version="*"`. Its `contentHash:` tag changes only when its content does.

## Supported builds

Packages are generated from Steam builds. The game packages cover the Windows build distributed through
Steam, GOG and Epic. Xbox, Game Pass and Microsoft Store builds are outside this project's scope.

The [Steam build registries](builds/steam) record known builds and their versions. Some historical beta
builds cannot currently be downloaded from Steam, so reference assemblies may be missing for them.

The [GOG build registry](builds/gog) records GOG's public builds of the game since March 2021, for
information only: packages are never made from GOG builds. Each build's version and changeset come from
its GOG label until they have been checked against the build's own files. Then the entry is marked
`verified`, plus `labelMismatch` when the files disagree with the label (the files' values are kept), or
`unverifiable` when GOG no longer serves the files. [`builds/links.json`](builds/links.json) lists which
Steam and GOG builds are the same build, by matching version and changeset. Each link says whether its
GOG side is `verified`.

## Generating packages

Building the tool requires the .NET 10 SDK. Downloading assemblies requires a Steam account that owns
the selected app. Set `STEAM_LOGIN` and `STEAM_PASSWORD`, or pass `--steamLogin` and `--steamPassword`.

Build and run these Bash examples from the repository root:

```sh
dotnet build src/Bannerlord.ReferenceAssemblies -c Release
TOOL=src/Bannerlord.ReferenceAssemblies/bin/Release/net10.0/Bannerlord.ReferenceAssemblies
```

Every command accepts `--app game`, `--app server` or `--app moddingkit`.
The default registry is `builds/steam/<appId>.json`; use `--registry` to select another file.
Packages are written to `final/` next to the executable. Use `--workDir` to change the output root.

### Update build metadata

Record current Steam builds and read missing version information by downloading a few small files:

```sh
$TOOL update --app game --fillVersions 25
```

Use `--buildId` to read specific builds, or `--retryUnavailable` to retry builds Steam previously refused.

### Generate reference assemblies

Download assemblies and create packages for builds not yet marked as published:

```sh
$TOOL generate --app server --maxBuilds 4
```

Use `--buildId` to regenerate specific builds, or `--dryRun` to list pending builds without downloading.
Pass `--checkFeed` to check NuGet for published packages before selecting builds.

Pass `--gui` to pack the GUI packages of each game build as well. Their build ids are written to
`final/generated-gui-builds.txt`. To pack only the GUI packages of builds that lack them, such as a build whose
GUI packing failed after its reference packages were published, or every build after a new GUI format, use
`--guiOnly`:

```sh
$TOOL generate --app game --guiOnly --maxBuilds 20
```

Each DLC is downloaded to a folder of its own, `depots/<buildId>.dlc/<dlcAppId>/`, and then copied over the
game folder, as Steam installs it.

### Bundle the GUI packages

Pack `GUI.v3.All` from the GUI packages the feed already has, for the builds the registry marks published:

```sh
$TOOL bundle-gui --app game --revision 57
$TOOL bundle-gui --app game --revision 1 --prerelease beta
```

It needs no Steam login and writes nothing to the registry. A package packed in the same run is taken from
`final/`, and the others are downloaded. Every file of every package must rebuild from the bundle as it was, or
nothing is packed. It is packed into `final-bundle/` only when its content differs from the newest one on the
feed. Without `--revision` its version ends in 0, for inspection only. `--prerelease` makes a package to test
with locally, and skips the comparison with the feed.

### Record GOG builds

Record the game's public GOG builds and rebuild the Steam/GOG links. No GOG login is needed, and the
command never takes `--app`:

```sh
$TOOL gog
$TOOL gog --fromGogDb
```

GOG lists only its five newest public builds, so the command has to run regularly to see each one.
`--fromGogDb` also adds the older builds from [GOGDB](https://www.gogdb.org/product/1564781494). It is
needed only once.

Checking labels against the builds' files needs a GOG account that owns the game, so the workflow does
not do it. Run the command by hand with `GOG_REFRESH_TOKEN` set, or with `--gogRefreshToken`, to a GOG
Galaxy refresh token. The token is tried first; when it is missing, GOG rejects it, or the account does
not own the game, the check is skipped and the rest of the command runs as usual. `--verify` limits how
many builds are checked in one run (25 by default). Each check downloads a few small files.

### Record published packages

After publishing, mark the builds in the registry, or refresh their publication status from NuGet:

```sh
$TOOL mark-published --app game --buildId 21112791
$TOOL mark-published --app game --gui --buildId 21112791
$TOOL mark-published --app game --fromFeed
```

The GUI packages have a published marker of their own; `--gui` sets it. `--fromFeed` refreshes both.

### Report current versions

Print the current stable and beta versions from the registry, and whether both are published:

```sh
$TOOL versions --app game
```

Stable is the build on the `public` branch; beta is the build on the `beta` branch, or stable when there
is none. The result is also written to `final/versions.json` and, in a workflow, to `GITHUB_OUTPUT`.

## Automation

[Update Build Registry](.github/workflows/update-builds.yml) checks for builds every three hours and
requests [generation](.github/workflows/generate-references.yml) when packages are missing. It also
records new GOG builds and refreshes the Steam/GOG links.
[Verify Feed](.github/workflows/verify-feed.yml) checks the registries against NuGet nightly.
Whenever the generate workflow publishes GUI packages, it packs and publishes `GUI.v3.All` as well. Run it
by hand with `pack` set to `references`, `gui` or `bundle` to do only that part; `all` is the default.

Once the packages for both the stable and the beta build are published, the update and generate
workflows send the current versions to [BUTR/.github](https://github.com/BUTR/.github) as a
`game_versions` dispatch. Its sync workflow sets the `GAME_VERSION_STABLE` and `GAME_VERSION_BETA`
organisation variables and notifies the mod repositories.
