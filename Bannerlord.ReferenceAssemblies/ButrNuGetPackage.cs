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

        public readonly uint AppId;

        public readonly uint DepotId;

        public readonly uint BuildId;

        private ButrNuGetPackage(string name, NuGetVersion pkgVersion, uint appId, uint depotId, uint buildId)
        {
            Name = name;
            PkgVersion = pkgVersion;
            AppId = appId;
            DepotId = depotId;
            BuildId = buildId;
        }

        public ButrNuGetPackage(string name, VersionInfo version)
            : this(
                name,
                version.Version,
                ParseAppIdEmbedding(version.PackageSearchMetadata.Tags)
                ?? throw new NotImplementedException($"Missing Steam App Id Tag for {name}"),
                ParseDepotIdEmbedding(version.PackageSearchMetadata.Tags)
                ?? throw new NotImplementedException($"Missing Steam Depot Id Tag for {name}"),
                ParseBuildIdEmbedding(version.PackageSearchMetadata.Tags)
                ?? throw new NotImplementedException($"Missing Steam Build Id Tag for {name}")
            ) { }

        public override string ToString()
            => $"{Name} {PkgVersion} ({AppId}, {DepotId}, {BuildId})";
    }

}