using NuGet.Versioning;

namespace Bannerlord.ReferenceAssemblies
{
    internal readonly partial struct NuGetPackage
    {
        public static NuGetPackage? Get(string name, NuGetVersion version, string tags)
        {
            var appId = ParseAppIdEmbedding(tags);
            var depotId = ParseDepotIdEmbedding(tags);
            var buildId = ParseBuildIdEmbedding(tags);

            if (appId == null || depotId == null || buildId == null)
                return null;

            return new NuGetPackage(name, version, appId.Value, depotId.Value, buildId.Value);
        }

        public readonly string Name;
        public readonly NuGetVersion PkgVersion;

        public readonly uint AppId;
        public readonly uint DepotId;
        public readonly uint BuildId;

        private NuGetPackage(string name, NuGetVersion pkgVersion, uint appId, uint depotId, uint buildId)
        {
            Name = name;
            PkgVersion = pkgVersion;
            AppId = appId;
            DepotId = depotId;
            BuildId = buildId;
        }

        public override string ToString() => $"{Name} {PkgVersion} ({AppId}, {DepotId}, {BuildId})";
    }
}