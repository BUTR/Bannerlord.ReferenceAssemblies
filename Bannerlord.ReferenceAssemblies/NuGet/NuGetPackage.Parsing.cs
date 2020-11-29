using System.Text.RegularExpressions;

namespace Bannerlord.ReferenceAssemblies
{
    internal readonly partial struct NuGetPackage
    {
        private static readonly Regex RxSteamAppIdEmbedding
            = new Regex(@"appId:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex RxSteamDepotIdEmbedding
            = new Regex(@"depotId:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex RxSteamBuildIdEmbedding
            = new Regex(@"buildId:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly Regex RxAppVersionEmbedding
            = new Regex(@"([abevd])(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static uint? ParseAppIdEmbedding(string stringWithVersion)
        {
            var match = RxSteamAppIdEmbedding.Match(stringWithVersion);
            return match.Success ? (uint?) uint.Parse(match.Groups[1].Value) : null;
        }

        public static uint? ParseDepotIdEmbedding(string stringWithVersion)
        {
            var match = RxSteamDepotIdEmbedding.Match(stringWithVersion);
            return match.Success ? (uint?) uint.Parse(match.Groups[1].Value) : null;
        }

        public static uint? ParseBuildIdEmbedding(string stringWithVersion)
        {
            var match = RxSteamBuildIdEmbedding.Match(stringWithVersion);
            return match.Success ? (uint?) uint.Parse(match.Groups[1].Value) : null;
        }

        public static (char Prefix, int Major, int Minor, int Revision, int ChangeSet)? ParseAppVersionIntoParts(string stringWithVersion)
        {
            var match = RxSteamBuildIdEmbedding.Match(stringWithVersion);
            if (!match.Success)
                return null;

            return (
                match.Groups[1].Value[0],
                int.Parse(match.Groups[1].Value),
                int.Parse(match.Groups[2].Value),
                int.Parse(match.Groups[3].Value),
                match.Groups.Count >= 5
                 ? int.Parse(match.Groups[4].Value)
                 : 0
            );
        }
    }
}