using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Bannerlord.ReferenceAssemblies
{
    public static class TemplateHelpers
    {
        private static readonly Regex RxDoubleBraceVariable = new(@"\{\{([^}]+)\}\}", RegexOptions.CultureInvariant);

        public static string ApplyTemplate(string template, IReadOnlyDictionary<string, string> repl) =>
            RxDoubleBraceVariable.Replace(template, match => repl[match.Groups[1].Value]);
    }
}