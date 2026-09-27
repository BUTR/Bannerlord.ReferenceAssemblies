using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Licenses;
using NuGet.Versioning;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>What every package of this repository carries, the reference packages and the GUI ones alike.</summary>
internal static class NuGetPackages
{
    private const string RepositoryUrl = "https://github.com/BUTR/Bannerlord.ReferenceAssemblies.git";

    public static PackageBuilder New(string id, string version, string title, string description, IEnumerable<string> tags)
    {
        var builder = new PackageBuilder
        {
            Id = id,
            Version = NuGetVersion.Parse(version),
            Title = title,
            Description = description,
            Repository = new RepositoryMetadata("git", RepositoryUrl, branch: null!, commit: null!),
            LicenseMetadata = new LicenseMetadata(LicenseType.Expression, "MIT", NuGetLicenseExpression.Parse("MIT"), [], LicenseMetadata.CurrentVersion),
            MinClientVersion = new Version(3, 3),
        };
        builder.Authors.Add("BUTR");
        builder.Owners.Add("BUTR");
        foreach (var tag in tags)
            builder.Tags.Add(tag);
        return builder;
    }

    /// <summary>Saves the package into the folder as &lt;id&gt;.&lt;version&gt;.nupkg and returns its path.</summary>
    public static string Save(PackageBuilder builder, string folder)
    {
        var path = Path.Combine(folder, $"{builder.Id}.{builder.Version!.ToNormalizedString()}.nupkg");
        using (var stream = File.Create(path))
            builder.Save(stream);
        return path;
    }
}
