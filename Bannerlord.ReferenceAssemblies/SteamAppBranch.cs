namespace Bannerlord.ReferenceAssemblies
{

    internal struct SteamAppBranch
    {

        public char Prefix => Version?[0] ?? 'd';

        public string Version { get; set; }

        public string Name { get; set; }

        public uint BuildId { get; set; }

    }

}