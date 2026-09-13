# Bannerlord.ReferenceAssemblies

Generates reference assemblies for Mount & Blade II: Bannerlord, its dedicated server and its Modding Kit, for every
build Steam has published: the public history back to launch, and the tip of every beta branch while it
is still being served. Core binaries come from `bin/<platform>`, module binaries from
`Modules/<module>/bin/<platform>`.

## About Reference Assemblies
https://docs.microsoft.com/en-us/dotnet/standard/assembly/reference-assemblies

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

The game keeps the ids it has always had; every mod out there references them. The dedicated server and the
Modding Kit are sibling families one segment down, and the release kind stays at the end as before. Early access and
release are separate ids because their version numbers restarted: `e1.9.0` would otherwise sort above
`v1.0.0`.

The package version is the game version without its letter, then the changeset: `1.3.7.102919`. A build
that only ever appeared in the beta branch is a prerelease, `1.5.2.121216-beta`. Builds that predate
changesets use the Steam build id instead; see below.

Each package carries two tags the tool reads back, `buildId:<steam build id>` and `appId:<steam app id>`,
and a `moduleVersion:` tag with the version its own manifest declares: `moduleVersion:v1.4.8` on the
official modules, Core and the meta package, and the DLC's own line on a DLC, such as `moduleVersion:v1.2.8`
on War Sails. That is a label only. The DLC ships with every game build and has never moved on its own,
so its package is versioned by the game like every other.

## The build registry

Steam can serve historical depot manifests, but it only *tells* you about the manifests of the branches
that exist right now. When TaleWorlds points a branch at a new build, the previous manifest disappears
from the app info and nothing can ask for it again unless the id was written down.

`builds/<appId>.json` is that record, one file each for the game (261550), the dedicated server (1863440)
and the Modding Kit (1393600). One entry per build:

```json
{
  "buildId": 20982519,
  "date": "2025-11-28T18:04:00Z",
  "title": "Patch Notes - WS v1.0.3 / BL v1.3.7",
  "branches": ["public", "v1.3.7"],
  "version": "v1.3.7",
  "changeSet": 102919,
  "publishedVersion": "1.3.7.102919",
  "manifests": {
    "261551": "9122089984960997812",
    "261552": "2208448863323996203",
    "2927200": "3696023466000144570"
  }
}
```

`manifests` maps depot id to manifest id, including the War Sails DLC depot for builds that have it.
Manifest ids are stored as strings so that consumers without 64-bit integers keep every digit.
`publishedVersion` is what the feed carries for the build; see [Two records, one check](#two-records-one-check).
`moduleVersions` records what each module's own manifest declares, for the `moduleVersion:` tag above; the
update verb reads it along with the game version.

`contentUnavailable` marks a build whose manifest Steam refused. A manifest that was never public stops
being served once its branch moves on, so the flag is mostly permanent, but refusals have been seen to
lift, and `update --retryUnavailable` asks again. A scheduled generate run leaves such builds out, so a
refusal cannot take a slot from a build Steam would serve; `generate --buildId` still tries them.

`versionUnreadable` marks a build whose files came down complete but named no version: either they carry
none, or the build has no library assembly under the expected folder at all, as with one Modding Kit build
from 2020. Nothing about the build changes with time, so it is not retried; `update --buildId` asks again.

### How a build is named

No Steam metadata carries the game version, so a build has to be downloaded at least in part before it
can be named. Three places inside a build carry a version and they do not always agree:

- `bin/<platform>/Version.xml`, in later game builds; the dedicated server never ships one;
- a `Version.xml` embedded in `TaleWorlds.Library.dll` as a virtual file attribute;
- the official module manifests, `Modules/*/SubModule.xml`. A DLC such as War Sails has a version line of
  its own there and names the game version in `RequiredBaseVersion`, which is what is read for it.

The highest of them wins. That matters: through the launch patches `TaleWorlds.Library` and the Native
module both sat at `e1.0.0` while SandBox, StoryMode and CustomBattle followed every patch, so a build
the patch notes call `e1.0.11` reports `e1.0.0` from the assembly alone. The same lag shows up later,
for instance in build 4911497, whose assembly says `e1.1.0` while its modules say `e1.1.1`. A module
version is only preferred when it shares the assembly's release prefix, which leaves the pre-launch
`b0.8.7` build alone even though its modules were already stamped `e1.0.0`.

The changeset is the fourth part of a version string when it has one, and otherwise the
`ApplicationVersion.DefaultChangeSet` constant in the assembly.

### Versions without a changeset

`changeSet` is null for the twenty-odd builds of late March and April 2020. That is not a failed read.
Those builds predate `ApplicationVersion.DefaultChangeSet`, and nothing else in them records a revision
either: no `Version.xml` on disk and placeholder `1.0.0.0` file versions on every executable.

Writing that as `0` would blur different builds together, so the registry records null and the package
version falls back to the Steam build id as its fourth part, giving `1.0.1.4842596` and so on. The build
id is the only value that tells those builds apart, it rises over time the way a changeset does, and
every package already carries it as a tag.

## Verbs

Every verb takes `--app game`, `--app server` or `--app moddingkit`; that one switch selects the Steam app and depots, the
platform folder, the package prefix and the registry file. Steam credentials come from `--steamLogin`
and `--steamPassword`, or from the `STEAM_LOGIN` and `STEAM_PASSWORD` environment variables.

The examples run the built executable from the repository root, so that `builds/` is found; pass
`--registry` to point elsewhere. Downloads, stripped assemblies and packages land next to the executable
under `depots/`, `ref/` and `final/`, or under `--workDir`.

```sh
dotnet build src/Bannerlord.ReferenceAssemblies -c Release
TOOL=src/Bannerlord.ReferenceAssemblies/bin/Release/net10.0/Bannerlord.ReferenceAssemblies
```

### update

Asks Steam which branches exist right now, records their build ids and depot manifests, then reads the
version of builds that do not have one yet by downloading a few small files.

```sh
$TOOL update --app game --fillVersions 25
```

Pass `--buildId` to read specific builds instead of the newest ones still missing a version, and
`--retryUnavailable` to ask again for builds Steam refused before.

Runs every three hours in [Update Build Registry](.github/workflows/update-builds.yml) for every app and
commits the result. That cadence is what keeps hotfix builds from being lost: a branch that is overwritten
between two runs takes its manifest id out of the live app info.

### generate

Takes the builds the registry says the feed does not carry yet, downloads them by their recorded manifest
ids, strips the assemblies and packs them.

```sh
$TOOL generate --app server --maxBuilds 4
```

It writes the ids it packed to `final/generated-builds.txt`, which the workflow hands to `mark-published`
once the push has actually succeeded. Pass `--buildId` to regenerate specific builds regardless of what the
feed already has, and `--dryRun` to only list what is outstanding, which contacts nothing.

The War Sails DLC is a depot of its own app and its build follows the game build by up to an hour. A
registry run in that gap records the previous DLC manifest against the new game build, and update replaces
it once the DLC lands, as long as the build has not been published. So that it is not published in between,
a build younger than two hours whose DLC manifest an older build already carries is left for a later run;
every game build so far has come with a DLC manifest of its own. Once the two hours have passed the build
is packed as recorded.

Builds are taken in the order that loses the least if a run is cut short. The current tip of each branch
comes first, because a build that was never public stops being served once its branch moves on, and the
public backlog follows newest first, because it can be fetched at any time. Builds that would pack to a
version the feed already has are skipped, as are several builds reporting one and the same version and
changeset: only one of those can be published, so the public one is kept. A build that fails to download
is reported and the run continues.

Stripping and packing happen in-process, with
[BepInEx.AssemblyPublicizer](https://www.nuget.org/packages/BepInEx.AssemblyPublicizer) and
`NuGet.Packaging`; nothing else needs to be installed. Each assembly goes under `ref/<framework>` for the
framework it declares, and netstandard assemblies are offered under `net472` as well, as they always were.

### mark-published

Records in the registry which builds the feed already carries, so the other two verbs never need NuGet.

```sh
$TOOL mark-published --app game --buildId 21112791
$TOOL mark-published --app game --fromFeed
```

`--fromFeed` reads NuGet and rewrites every marker, which is how an existing feed is recorded for the
first time and how drift is repaired. `generate --checkFeed` does the same reconcile inline before
choosing.

## How the jobs fit together

1. [Update Build Registry](.github/workflows/update-builds.yml), every three hours. Asks Steam what exists
   for each app, records it, reads the version of anything new, and commits. It then works
   out from the registries alone whether any build is outstanding, and if so asks for generation. It has to
   ask explicitly: a push made with the default token starts no other workflow.
2. [GenerateReferences](.github/workflows/generate-references.yml). Packs what the registries say is
   missing, one app after another, pushes, records what was published, and commits.
3. [Verify Feed](.github/workflows/verify-feed.yml), nightly. Rewrites the published markers from NuGet
   for every app and commits any difference.

The two Steam jobs share a concurrency group, because a second login with the same account replaces the
first. `generate --dryRun` is what step 1 uses to decide; it writes the outstanding ids to
`final/pending-builds.txt`.

## Two records, one check

Steam says which builds exist; NuGet says which of them already have reference assemblies. Asking both on
every run is slow and makes the scheduled job depend on the feed being reachable, so the answer from NuGet
is written back into the registry as `publishedVersion` on each build. The three-hourly update never
touches the feed, and generate reads `publishedVersion` rather than querying it.

Because it stores the version rather than a flag, a build whose version is later corrected stops counting
as published and is packaged again under its corrected version.

Keeping that record honest is its own job. [Verify Feed](.github/workflows/verify-feed.yml) runs nightly,
rewrites every marker from the feed and commits any difference, so a push that half succeeded, a package
unlisted, or a version corrected after publishing all surface as a change there. Nothing depends on it
being right: a drifted marker only means a build is packaged twice or skipped once.

Only packages tagged with the app being checked are counted, and each package id is looked up exactly
rather than searched for. A search returns everyone else's packages whose names merely start the same
way, and theirs carry the same `buildId` tags. The app tag is also what keeps the game and the server
apart: both can report the same version.

## Dedicated server

The dedicated server is Steam app 1863440 with depot 1863441. Its layout differs from the game's:

- it ships Windows and Linux builds side by side, in `bin/Win64_Shipping_Server` and
  `bin/Linux64_Shipping_Server`. Their managed assemblies are the same bytes; only native libraries and
  the starters differ. One package therefore serves both platforms. The tool reads both folders, packs
  the union, and reports any assembly that ever differs between them;
- every managed assembly is in those folders, including the multiplayer and custom-server ones. The
  `Modules/` folders hold data and manifests only, so the server family has a Core package and a meta
  package and nothing in between;
- it has no `Version.xml` anywhere; the version is read from the module manifests and the changeset from
  the assembly;
- the server starters and the web panel are .NET (Core) applications. Their managed assemblies go under
  `ref/net6.0`, and their native apphost executables are skipped.

Its history on Steam starts in September 2022 at `e1.8.1`, so the `.EarlyAccess` line holds three builds
and everything after `v1.0.0` is a release.

## Modding Kit

The Modding Kit is Steam app 1393600 with depot 1393601: the game compiled with the editor switched on,
under `bin/Win64_Shipping_wEditor` and `Modules/*/bin/Win64_Shipping_wEditor`, released in lockstep with
the game and sharing its version and changeset. Compared for `v1.4.8` (changeset 119303), 57 of its 78
shared assemblies have the same metadata as the game's and 21 carry extra editor-only members, several
hundred in `TaleWorlds.MountAndBlade` and `TaleWorlds.CampaignSystem` alone. It ships Native, SandBox,
SandBoxCore, StoryMode and CustomBattle, and tools the game lacks such as the sprite sheet generator, but
no Multiplayer, BirthAndDeath, FastMode or NavalDLC. A mod that ships a `Win64_Shipping_wEditor` build,
or an editor tool, compiles against these packages.

## Other platforms

Steam, GOG and Epic ship the same Windows build, `bin/Win64_Shipping_Client`, so the Steam pipeline covers
all three stores.

The Xbox (Game Pass and Microsoft Store) build is out of scope. It is a separate compilation with its own
platform assemblies, it is not on Steam, and it is delivered as an encrypted XVC container that only
Gaming Services on a licensed machine can open, so there is no clean way to fetch its binaries alone.
Steam is the only source of reference assemblies.

## Requirements

- .NET 10 SDK.
- A Steam account that owns the app. Historical manifests are only served to owners.
