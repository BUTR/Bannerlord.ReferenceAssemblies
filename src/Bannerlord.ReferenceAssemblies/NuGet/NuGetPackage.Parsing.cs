using System.Text.RegularExpressions;

namespace Bannerlord.ReferenceAssemblies
{
    internal readonly partial struct NuGetPackage
    {
        private static readonly Regex RxSteamAppIdEmbedding = new(@"appId:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Regex RxSteamBuildIdEmbedding = new(@"buildId:(\d+)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static uint? ParseAppIdEmbedding(string stringWithVersion)
        {
            var match = RxSteamAppIdEmbedding.Match(stringWithVersion);
            return match.Success ? uint.Parse(match.Groups[1].Value) : null;
        }

        public static uint? ParseBuildIdEmbedding(string stringWithVersion)
        {
            var match = RxSteamBuildIdEmbedding.Match(stringWithVersion);
            return match.Success ? uint.Parse(match.Groups[1].Value) : null;
        }
    }
}