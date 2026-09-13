using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Works out which version of the game a downloaded build is.
///
/// Three places carry a version and they do not always agree:
///   - bin/&lt;platform&gt;/Version.xml on disk, in later builds;
///   - a Version.xml embedded in TaleWorlds.Library.dll as a VirtualFile attribute, in all builds;
///   - the official module manifests under Modules/*/SubModule.xml.
/// Through the launch patches TaleWorlds.Library and the Native module both sat at e1.0.0 while
/// SandBox, StoryMode and CustomBattle followed every patch, so the modules are the ones that say which
/// patch a build actually is. The highest version wins, and the changeset comes from the fourth part of
/// a version string when it has one, otherwise from the ApplicationVersion.DefaultChangeSet constant.
///
/// This replaces FetchBannerlordVersion, which reads only the assembly, only under the client's
/// platform folder, and reports a changeset of zero for builds that carry none.
/// </summary>
internal static class VersionReader
{
    private const string LibraryAssembly = "TaleWorlds.Library.dll";
    private static readonly Regex RxVersion = new(@"^[a-z]\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static (string Version, int? ChangeSet)? Read(string gameFolder)
    {
        if (!Directory.Exists(gameFolder))
            return null;

        var assembly = FindFile(gameFolder, LibraryAssembly);

        // Candidates in no particular order; the highest of them names the build.
        var candidates = new List<string>();
        if (ReadVersionXmlOnDisk(gameFolder) is { } onDisk)
            candidates.Add(onDisk);
        if (assembly is not null && ReadEmbeddedVersionXml(assembly) is { } embedded)
            candidates.Add(embedded);

        if (candidates.Count == 0 && ReadHighestModuleVersion(gameFolder) is null)
            return null;

        var version = candidates.OrderByDescending(x => x, VersionComparer).FirstOrDefault();
        version = version is null
            ? ReadHighestModuleVersion(gameFolder)
            : PreferHigherVersion(version, ReadHighestModuleVersion(gameFolder));

        if (version is null)
            return null;

        // A four-part version carries its own changeset; otherwise the assembly holds it, if at all.
        var changeSet = candidates.Select(TrailingChangeSet).OfType<int>().Cast<int?>().FirstOrDefault()
                        ?? (assembly is not null ? ReadChangeSet(assembly) : null);

        return (Trim(version), changeSet);
    }

    /// <summary>Reads bin/&lt;platform&gt;/Version.xml, whichever platform folder this build ships.</summary>
    private static string? ReadVersionXmlOnDisk(string gameFolder)
    {
        var bin = Path.Combine(gameFolder, "bin");
        if (!Directory.Exists(bin))
            return null;

        return Directory.EnumerateFiles(bin, "Version.xml", SearchOption.AllDirectories)
            .Select(ReadVersionXml)
            .OfType<string>()
            .OrderByDescending(x => x, VersionComparer)
            .FirstOrDefault();
    }

    private static string? ReadVersionXml(string path)
    {
        try
        {
            return FromVersionXml(XDocument.Load(path).Root);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Both shapes ever used: a Value on the root, or a Singleplayer element carrying it.</summary>
    private static string? FromVersionXml(XElement? root)
    {
        var value = (string?) root?.Attribute("Value")
                    ?? (string?) root?.Descendants("Singleplayer").FirstOrDefault()?.Attribute("Value");
        value = value?.Trim();
        return value is not null && RxVersion.IsMatch(value) ? value : null;
    }

    /// <summary>
    /// Reads the Version.xml that TaleWorlds.Library.dll carries as a VirtualFile attribute on
    /// VirtualFolders.&lt;platform&gt;.bin.Parameters.Version. The platform folder appears as a nested type
    /// name and differs between the client, the dedicated server and the editor, so the nesting is walked
    /// rather than named, but only a file under a bin/Parameters folder counts.
    /// </summary>
    private static string? ReadEmbeddedVersionXml(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return null;

            var reader = peReader.GetMetadataReader();
            var virtualFolders = reader.TypeDefinitions
                .Select(reader.GetTypeDefinition)
                .Where(x => reader.GetString(x.Name) == "VirtualFolders" && reader.GetString(x.Namespace) == "TaleWorlds.Library")
                .ToList();

            foreach (var type in virtualFolders)
            foreach (var xml in FindVirtualFiles(reader, type, "Version.xml", parent: null, depth: 0))
            {
                if (FromVersionXml(Parse(xml)) is { } version)
                    return version;
            }
        }
        catch (Exception)
        {
            // not a managed assembly, or a shape we do not know
        }

        return null;

        static XElement? Parse(string xml)
        {
            try { return XDocument.Parse(xml).Root; }
            catch (Exception) { return null; }
        }
    }

    /// <summary>
    /// Walks the nested types under VirtualFolders, each of which stands for a folder, looking for a
    /// virtual file of the given name inside a bin/Parameters folder.
    /// </summary>
    private static IEnumerable<string> FindVirtualFiles(MetadataReader reader, TypeDefinition type, string fileName, string? parent, int depth)
    {
        if (depth > 6)
            yield break;

        var name = reader.GetString(type.Name);
        if (name == "Parameters" && parent == "bin")
        {
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                foreach (var attributeHandle in field.GetCustomAttributes())
                {
                    if (ReadVirtualFileAttribute(reader, reader.GetCustomAttribute(attributeHandle)) is not { } file)
                        continue;
                    if (string.Equals(file.Name, fileName, StringComparison.OrdinalIgnoreCase))
                        yield return file.Content;
                }
            }
        }

        foreach (var nestedHandle in type.GetNestedTypes())
        foreach (var found in FindVirtualFiles(reader, reader.GetTypeDefinition(nestedHandle), fileName, name, depth + 1))
            yield return found;
    }

    /// <summary>A VirtualFileAttribute blob is the prolog then two strings: the file name and its content.</summary>
    private static (string Name, string Content)? ReadVirtualFileAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        if (attribute.Constructor.Kind != HandleKind.MethodDefinition)
            return null;

        var declaringType = reader.GetMethodDefinition((MethodDefinitionHandle) attribute.Constructor).GetDeclaringType();
        if (reader.GetString(reader.GetTypeDefinition(declaringType).Name) != "VirtualFileAttribute")
            return null;

        try
        {
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadByte() != 0x01 || blob.ReadByte() != 0x00)
                return null;

            var name = blob.ReadSerializedString();
            var content = blob.ReadSerializedString();
            return name is null || content is null ? null : (name, content);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the ApplicationVersion.DefaultChangeSet constant out of TaleWorlds.Library.dll.</summary>
    public static int? ReadChangeSet(string assemblyPath)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata)
                return null;

            var reader = peReader.GetMetadataReader();
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                if (reader.GetString(type.Name) != "ApplicationVersion" || reader.GetString(type.Namespace) != "TaleWorlds.Library")
                    continue;

                foreach (var fieldHandle in type.GetFields())
                {
                    var field = reader.GetFieldDefinition(fieldHandle);
                    if (reader.GetString(field.Name) != "DefaultChangeSet")
                        continue;

                    var constantHandle = field.GetDefaultValue();
                    if (constantHandle.IsNil)
                        return null;

                    var constant = reader.GetConstant(constantHandle);
                    if (constant.TypeCode != ConstantTypeCode.Int32)
                        return null;

                    return reader.GetBlobReader(constant.Value).ReadInt32();
                }
            }
        }
        catch (Exception)
        {
            // not a managed assembly, or a shape we do not know
        }

        return null;
    }

    /// <summary>
    /// Reads the game version a module manifest speaks for. The official modules carry it as
    /// &lt;Version value="v1.4.8"/&gt;. A DLC such as War Sails has a version line of its own there and
    /// names the game version in &lt;RequiredBaseVersion/&gt; instead, so that element wins when present;
    /// otherwise the DLC's own number would one day be mistaken for the game's.
    /// </summary>
    public static string? ReadModuleVersion(string subModuleXmlPath)
    {
        try
        {
            var root = XDocument.Load(subModuleXmlPath).Root;
            var value = (Attribute(root, "RequiredBaseVersion") ?? Attribute(root, "Version"))?.Trim();
            return value is not null && RxVersion.IsMatch(value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The version each module's own manifest declares, keyed by module folder, which is also the name of
    /// the module's package. This is the module's own line, so a DLC reports its own version here.
    /// </summary>
    public static Dictionary<string, string> ReadModuleVersions(string gameFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var modules = Path.Combine(gameFolder, "Modules");
        if (!Directory.Exists(modules))
            return result;

        foreach (var path in Directory.EnumerateFiles(modules, "SubModule.xml", SearchOption.AllDirectories))
        {
            try
            {
                var version = Attribute(XDocument.Load(path).Root, "Version")?.Trim();
                if (version is not null && RxVersion.IsMatch(version))
                    result[Path.GetFileName(Path.GetDirectoryName(path))!] = version;
            }
            catch (Exception)
            {
                // a manifest that does not parse names nothing
            }
        }
        return result;
    }

    private static string? Attribute(XElement? root, string element) =>
        root?.Elements(element).Select(x => (string?) x.Attribute("value")).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>The highest version declared by the official modules.</summary>
    public static string? ReadHighestModuleVersion(string gameFolder)
    {
        var modules = Path.Combine(gameFolder, "Modules");
        if (!Directory.Exists(modules))
            return null;

        return Directory.EnumerateFiles(modules, "SubModule.xml", SearchOption.AllDirectories)
            .Select(ReadModuleVersion)
            .OfType<string>()
            .OrderByDescending(x => x, VersionComparer)
            .FirstOrDefault();
    }

    /// <summary>
    /// Takes the module version only when it plainly supersedes the other: same release kind, and
    /// higher. A prefix change means a different line of releases, as with the pre-launch b0.8.7 build
    /// whose modules were already stamped e1.0.0, and those are left alone.
    /// </summary>
    public static string PreferHigherVersion(string version, string? moduleVersion) =>
        moduleVersion is { Length: > 0 }
        && version.Length > 0
        && version[0] == moduleVersion[0]
        && VersionComparer.Compare(moduleVersion, version) > 0
            ? moduleVersion
            : version;

    /// <summary>Finds a file anywhere under the folder, or null.</summary>
    public static string? FindFile(string folder, string fileName) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, fileName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    public static readonly IComparer<string> VersionComparer = Comparer<string>.Create(static (a, b) =>
    {
        var left = Numbers(a);
        var right = Numbers(b);
        for (var i = 0; i < left.Length; i++)
        {
            var comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
                return comparison;
        }
        return 0;
    });

    /// <summary>The version proper, without a changeset appended as a fourth part.</summary>
    private static string Trim(string version)
    {
        var parts = version.Split('.');
        return parts.Length > 3 ? string.Join(".", parts.Take(3)) : version;
    }

    private static int? TrailingChangeSet(string version)
    {
        var parts = version.Split('.');
        return parts.Length > 3 && int.TryParse(parts[3], out var value) ? value : null;
    }

    private static int[] Numbers(string version)
    {
        var parts = version.TrimStart('a', 'b', 'd', 'e', 'i', 'v').Split('.');
        var numbers = new int[4];
        for (var i = 0; i < numbers.Length && i < parts.Length; i++)
            numbers[i] = int.TryParse(parts[i], out var value) ? value : 0;
        return numbers;
    }
}
