using System.Diagnostics;
using System.Globalization;

namespace Bannerlord.ReferenceAssemblies;

internal static class Log
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static void Info(string message) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{Clock.Elapsed.TotalSeconds,8:F1}] {message}"));
}
