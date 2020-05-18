using System;
using System.Collections.Generic;

namespace Bannerlord.ReferenceAssemblies
{

    internal struct SteamAppBranch
    {

        public static readonly IReadOnlyDictionary<char, string> VersionPrefixToName
            = new SortedList<char, string>
            {
                {'a', "Alpha"},
                {'b', "Beta"},
                {'e', "EarlyAccess"},
                {'d', "Development"},
                {'v', null},
            };

        public string SpecialVersionType
            => VersionPrefixToName.TryGetValue(Prefix, out var name)
                ? name
                : throw new NotImplementedException("Probably an error.");

        public char Prefix => Version?[0] ?? 'i';

        public string Version { get; set; }

        public string Name { get; set; }

        public uint BuildId { get; set; }

    }

}