using System;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace Bannerlord.ReferenceAssemblies
{

    [PublicAPI]
    internal readonly partial struct ButrNuGetPackage
    {

        public readonly string Name;

        public readonly NuGetVersion PkgVersion;

        public readonly string AppVersion;

        public readonly uint BuildId;

        private ButrNuGetPackage(string name, NuGetVersion pkgVersion, string appVersion, uint buildId)
        {
            Name = name;
            PkgVersion = pkgVersion;
            AppVersion = appVersion;
            BuildId = buildId;
        }

        public ButrNuGetPackage(string name, VersionInfo version)
            : this(
                name,
                version.Version,
                ParseAppVersion(version.PackageSearchMetadata.Tags)
                ?? throw new NotImplementedException($"Missing App Version Tag for {name}"),
                ParseBuildIdEmbedding(version.PackageSearchMetadata.Tags)
                ?? throw new NotImplementedException($"Missing Steam Build Id Tag for {name}")
            ) { }

    }

}