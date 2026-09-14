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

## Supported builds

Packages are generated from Steam builds. The game packages cover the Windows build distributed through
Steam, GOG and Epic. Xbox, Game Pass and Microsoft Store builds are outside this project's scope.

The [build registries](builds) record known builds and their versions. Some historical beta builds
cannot currently be downloaded from Steam, so reference assemblies may be missing for them.

## Generating packages

Building the tool requires the .NET 10 SDK. Downloading assemblies requires a Steam account that owns
the selected app. Set `STEAM_LOGIN` and `STEAM_PASSWORD`, or pass `--steamLogin` and `--steamPassword`.

Build and run these Bash examples from the repository root:

```sh
dotnet build src/Bannerlord.ReferenceAssemblies -c Release
TOOL=src/Bannerlord.ReferenceAssemblies/bin/Release/net10.0/Bannerlord.ReferenceAssemblies
```

Every command accepts `--app game`, `--app server` or `--app moddingkit`.
The default registry is `builds/<appId>.json`; use `--registry` to select another file.
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

### Record published packages

After publishing, mark the builds in the registry, or refresh their publication status from NuGet:

```sh
$TOOL mark-published --app game --buildId 21112791
$TOOL mark-published --app game --fromFeed
```

### Report current versions

Print the current stable and beta versions from the registry, and whether both are published:

```sh
$TOOL versions --app game
```

Stable is the build on the `public` branch; beta is the build on the `beta` branch, or stable when there
is none. The result is also written to `final/versions.json` and, in a workflow, to `GITHUB_OUTPUT`.

## Automation

[Update Build Registry](.github/workflows/update-builds.yml) checks for builds every three hours and
requests [generation](.github/workflows/generate-references.yml) when packages are missing.
[Verify Feed](.github/workflows/verify-feed.yml) checks the registries against NuGet nightly.

Once the packages for both the stable and the beta build are published, the update and generate
workflows send the current versions to [BUTR/.github](https://github.com/BUTR/.github) as a
`game_versions` dispatch. Its sync workflow sets the `GAME_VERSION_STABLE` and `GAME_VERSION_BETA`
organisation variables and notifies the mod repositories.
