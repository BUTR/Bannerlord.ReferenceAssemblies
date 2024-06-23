using CommandLine;

using System.Collections.Generic;

namespace Bannerlord.ReferenceAssemblies.Options;

internal class GenerateOptions
{
    [Option("steamLogin", Required = true)]
    public string SteamLogin { get; set; } = default!;

    [Option("steamPassword", Required = true)]
    public string SteamPassword { get; set; } = default!;

    [Option("steamAppId", Required = true)]
    public uint SteamAppId { get; set; } = default!;

    [Option("steamOS", Required = true)]
    public string SteamOS { get; set; } = default!;

    [Option("steamOSArch", Required = true)]
    public string SteamOSArch { get; set; } = default!;

    [Option("steamDepotId", Required = true)]
    public IEnumerable<uint> SteamDepotId { get; set; } = default!;



    [Option("feedUrl", Required = true)]
    public string FeedUrl { get; set; } = default!;

    [Option("feedUser", Required = false)]
    public string FeedUser { get; set; } = default!;

    [Option("feedPassword", Required = false)]
    public string FeedPassword { get; set; } = default!;
}