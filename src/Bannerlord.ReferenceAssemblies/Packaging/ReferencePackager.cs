using BepInEx.AssemblyPublicizer;

using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Licenses;
using NuGet.Versioning;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>What a set of packages is called and where its assemblies come from. See <see cref="App.ForBuild"/>.</summary>
internal sealed record PackageSpec(
    string Prefix,
    string Suffix,
    string Version,
    string Title,
    string Description,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> BinFolders,
    string GameVersion,
    IReadOnlyDictionary<string, string> ModuleVersions)
{
    /// <summary>Package id of the meta package (module null), of Core, or of a module.</summary>
    public string PackageId(string? module) => module is null ? $"{Prefix}{Suffix}" : $"{Prefix}.{module}{Suffix}";

    /// <summary>
    /// The moduleVersion tag of a package: a module's own manifest version, which for a DLC such as War
    /// Sails is its own line (v1.2.8) rather than the game's; the game version for Core and the meta
    /// package. The package version itself is always the game's, because every module ships with every
    /// game build.
    /// </summary>
    public string ModuleVersionTag(string? module) =>
        $"moduleVersion:{(module is not null && ModuleVersions.TryGetValue(module, out var own) ? own : GameVersion)}";
}

/// <summary>
/// Turns a game folder into packages: one Core package with the engine assemblies, one package per
/// official module that ships assemblies, and a meta package that depends on them all. Method bodies are
/// stripped so that the assemblies compile against but cannot run, and each package offers them under
/// ref/&lt;framework&gt; for the framework they were built for.
/// </summary>
internal sealed class ReferencePackager(Paths paths)
{
    private const string RepositoryUrl = "https://github.com/BUTR/Bannerlord.ReferenceAssemblies.git";

    /// <summary>The same as assembly-publicizer --strip-only.</summary>
    private static readonly AssemblyPublicizerOptions StripOnly = new()
    {
        Target = PublicizeTarget.None,
        Strip = true,
        IncludeOriginalAttributesAttribute = false,
    };

    /// <summary>Packs the game folder (the one holding bin/ and Modules/) and returns the package paths.</summary>
    public IReadOnlyList<string> Pack(string source, string refRoot, PackageSpec spec)
    {
        if (Directory.Exists(refRoot))
            Directory.Delete(refRoot, true);
        Directory.CreateDirectory(paths.Final);

        var packages = new List<string>();
        var names = new List<string>();

        // Core: the engine assemblies next to the executable. The dedicated server keeps its module
        // assemblies there too, so anything named after a module present counts as well.
        var modules = Path.Combine(source, "Modules");
        var moduleNames = Directory.Exists(modules) ? Directory.EnumerateDirectories(modules).Select(Path.GetFileName).OfType<string>().ToList() : [];
        var core = CollectAssemblies(Path.Combine(source, "bin"), spec.BinFolders, x => IsEngineAssembly(x, moduleNames));
        if (core.Count == 0)
            throw new InvalidDataException($"No engine assemblies under {Path.Combine(source, "bin")} for {string.Join(", ", spec.BinFolders)}.");
        names.Add("Core");
        packages.Add(PackAssemblies(spec, "Core", Strip(core, Path.Combine(refRoot, "Core"))));

        // One package per module that ships assemblies of its own.
        var moduleDirs = Directory.Exists(modules) ? Directory.EnumerateDirectories(modules) : [];
        foreach (var moduleDir in moduleDirs.Order(StringComparer.OrdinalIgnoreCase))
        {
            var files = CollectAssemblies(Path.Combine(moduleDir, "bin"), spec.BinFolders, x => x.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (files.Count == 0)
                continue;

            var name = Path.GetFileName(moduleDir);
            names.Add(name);
            packages.Add(PackAssemblies(spec, name, Strip(files, Path.Combine(refRoot, name))));
        }

        packages.Add(PackMeta(spec, names));
        return packages;
    }

    /// <summary>
    /// The assemblies under the given platform folders of a bin/ directory, one file per name. Several
    /// platform folders are expected to carry the same managed assemblies, and so far the dedicated
    /// server's Windows and Linux folders do, byte for byte. A file only one platform ships is still
    /// packed; a file that differs between platforms is reported and the first platform's copy is kept,
    /// because publishing one platform's API as the other's would be worse than a noisy log.
    /// </summary>
    private static List<string> CollectAssemblies(string bin, IReadOnlyList<string> binFolders, Func<string, bool> take)
    {
        var chosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in binFolders.Select(x => Path.Combine(bin, x)).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder).Where(take).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(file);
                if (!chosen.TryGetValue(name, out var first))
                    chosen[name] = file;
                else if (!SameContent(first, file))
                    Log.Info($"  WARNING: {name} differs between {Path.GetFileName(Path.GetDirectoryName(first))} and {Path.GetFileName(folder)}; packing the former");
            }
        }
        return chosen.Values.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool SameContent(string a, string b)
    {
        if (new FileInfo(a).Length != new FileInfo(b).Length)
            return false;
        using var left = File.OpenRead(a);
        using var right = File.OpenRead(b);
        return SHA256.HashData(left).AsSpan().SequenceEqual(SHA256.HashData(right));
    }

    /// <summary>
    /// The game's own assemblies and managed executables: TaleWorlds.*, anything named after one of the
    /// modules present (SandBox.View.dll and the like, where a build keeps them in the engine folder), and
    /// executables. Third-party libraries such as Newtonsoft.Json are left out. The dedicated server also
    /// ships native .NET apphost executables, which have no metadata and are skipped.
    /// </summary>
    private static bool IsEngineAssembly(string path, IReadOnlyList<string> moduleNames)
    {
        var name = Path.GetFileName(path);
        var isDll = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var candidate = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || isDll && name.StartsWith("TaleWorlds.", StringComparison.OrdinalIgnoreCase)
                        || isDll && moduleNames.Any(m => name.Equals($"{m}.dll", StringComparison.OrdinalIgnoreCase) || name.StartsWith($"{m}.", StringComparison.OrdinalIgnoreCase));
        return candidate && Frameworks(path) is not null;
    }

    /// <summary>Strips the assemblies into &lt;output&gt;/&lt;tfm&gt;/ and returns (source, target-in-package) pairs.</summary>
    private static List<(string Source, string Target)> Strip(IEnumerable<string> assemblies, string output)
    {
        var files = new List<(string, string)>();
        foreach (var assembly in assemblies)
        {
            if (Frameworks(assembly) is not { } frameworks)
            {
                Log.Info($"  {Path.GetFileName(assembly)} is not a managed assembly; skipped");
                continue;
            }

            var name = Path.GetFileName(assembly);
            string? stripped = null;
            foreach (var framework in frameworks)
            {
                var target = Path.Combine(output, framework, name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (stripped is null)
                    AssemblyPublicizer.Publicize(assembly, target, StripOnly);
                else
                    File.Copy(stripped, target, true);
                stripped = target;
                files.Add((target, $"ref/{framework}/{name}"));
            }
        }
        return files;
    }

    /// <summary>
    /// The ref/ folders an assembly goes under, or null for a file that is not a managed assembly. The
    /// TargetFramework attribute decides; a netstandard assembly is offered under net472 as well, as the
    /// packages always have. Without the attribute, the core library it references is the next best sign.
    /// </summary>
    private static IReadOnlyList<string>? Frameworks(string assemblyPath)
    {
        FileStream stream;
        try
        {
            stream = File.OpenRead(assemblyPath);
        }
        catch (UnauthorizedAccessException)
        {
            // A file we may not read cannot be packed; it is reported by the caller as skipped.
            return null;
        }

        using var _ = stream;
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            return null;
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
            return null;

        var framework = TargetFramework(reader) is { } declared
            ? NuGetFramework.Parse(declared)
            : reader.AssemblyReferences.Select(x => reader.GetString(reader.GetAssemblyReference(x).Name)).Contains("netstandard")
                ? NuGetFramework.Parse("netstandard2.0")
                : NuGetFramework.Parse("net472");

        return framework.Framework == ".NETStandard"
            ? ["netstandard2.0", "net472"]
            : [framework.GetShortFolderName()];
    }

    /// <summary>Reads [assembly: TargetFramework("...")], whose blob is the prolog followed by one string.</summary>
    private static string? TargetFramework(MetadataReader reader)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var parent = reader.GetMemberReference((MemberReferenceHandle) attribute.Constructor).Parent;
            if (parent.Kind != HandleKind.TypeReference || reader.GetString(reader.GetTypeReference((TypeReferenceHandle) parent).Name) != "TargetFrameworkAttribute")
                continue;

            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 0x0001)
                return null;
            return blob.ReadSerializedString();
        }
        return null;
    }

    private string PackAssemblies(PackageSpec spec, string name, IEnumerable<(string Source, string Target)> files)
    {
        var builder = NewPackage(spec, spec.PackageId(name), spec.ModuleVersionTag(name));
        foreach (var (source, target) in files)
            builder.Files.Add(new PhysicalPackageFile { SourcePath = source, TargetPath = target });
        return Save(builder);
    }

    private string PackMeta(PackageSpec spec, IEnumerable<string> names)
    {
        var builder = NewPackage(spec, spec.PackageId(null), spec.ModuleVersionTag(null));
        var dependencies = names.Select(name => new PackageDependency(spec.PackageId(name), VersionRange.Parse(spec.Version)));
        builder.DependencyGroups.Add(new PackageDependencyGroup(NuGetFramework.AnyFramework, dependencies));
        return Save(builder);
    }

    private static PackageBuilder NewPackage(PackageSpec spec, string id, string moduleVersionTag)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(spec.Version),
            Title = spec.Title,
            Description = spec.Description,
            Repository = new RepositoryMetadata("git", RepositoryUrl, branch: null!, commit: null!),
            LicenseMetadata = new LicenseMetadata(LicenseType.Expression, "MIT", NuGetLicenseExpression.Parse("MIT"), [], LicenseMetadata.CurrentVersion),
            MinClientVersion = new Version(3, 3),
        };
        builder.Authors.Add("BUTR");
        builder.Owners.Add("BUTR");
        // The feed check reads the buildId and appId tags back; see NuGetFeed.
        foreach (var tag in spec.Tags.Append(moduleVersionTag))
            builder.Tags.Add(tag);
        return builder;
    }

    private string Save(PackageBuilder builder)
    {
        var path = Path.Combine(paths.Final, $"{builder.Id}.{builder.Version!.ToNormalizedString()}.nupkg");
        using var stream = File.Create(path);
        builder.Save(stream);
        Log.Info($"  {Path.GetFileName(path)}");
        return path;
    }
}
