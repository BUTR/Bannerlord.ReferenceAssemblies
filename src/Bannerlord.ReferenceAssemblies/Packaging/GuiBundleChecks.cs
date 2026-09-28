using System.Text;
using System.Text.Json.Nodes;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// The checks every GUI.v2.All runs before it is packed, on the bytes it will carry. Unlike <see cref="GuiChecks"/>,
/// which only logs what the game's own files get wrong, any failure here stops the run: it means the bundle
/// does not say what the per-build packages say.
/// </summary>
internal static class GuiBundleChecks
{
    /// <summary>The largest .nupkg the bundle may be. About twice the first measured size; see gui-packages-v2-all.md.</summary>
    public const long SizeBudget = 4L * 1024 * 1024;

    public static void Run(GuiBundleContent content, IReadOnlyList<GuiBundleSource> sources, string basePackageId)
    {
        var failures = new List<string>();
        var reader = new GuiBundleReader(content.Files);

        if (reader.FormatVersion != GuiBundle.FormatVersion)
            failures.Add($"formatVersion is {reader.FormatVersion}, not {GuiBundle.FormatVersion}.");
        if (reader.ContentHash != content.ContentHash || HashOf(content.Files) != content.ContentHash)
            failures.Add($"contentHash {reader.ContentHash} is not the hash of the files.");

        // Scope: exactly the packages chosen, in the order that numbers the lines.
        var expected = GuiBundle.Order(sources, basePackageId).SelectMany(x => x)
            .Select(x => $"{x.Build.Version} {x.PackageId} {x.PackageVersion} {x.Build.BuildId}").ToList();
        var actual = reader.Packages
            .Select(x => $"{x.GameVersion} {x.Package["packageId"]} {x.Package["packageVersion"]} {x.Package["buildId"]}").ToList();
        if (!expected.SequenceEqual(actual))
            failures.Add($"The bundle lists {actual.Count} package(s), not the {expected.Count} chosen: first difference at {FirstDifference(expected, actual)}.");

        // Every child must be an earlier line; Nodes() throws otherwise.
        reader.Nodes();

        // Round trip: every file of every package rebuilds to what that package carries.
        var byPackage = sources.ToDictionary(x => (x.PackageId, x.PackageVersion));
        var files = 0;
        foreach (var (gameVersion, package) in reader.Packages)
        {
            var key = (package["packageId"]!.GetValue<string>(), package["packageVersion"]!.GetValue<string>());
            if (!byPackage.TryGetValue(key, out var source))
                continue;
            var rebuilt = reader.Rebuild(package);
            var original = source.ReadFiles();
            foreach (var path in original.Keys.Except(rebuilt.Keys).Order(StringComparer.Ordinal))
                failures.Add($"{key.Item1} {key.Item2}: gui/{path} is not rebuilt.");
            foreach (var path in rebuilt.Keys.Except(original.Keys).Order(StringComparer.Ordinal))
                failures.Add($"{key.Item1} {key.Item2}: gui/{path} is rebuilt but not in the package.");
            foreach (var (path, bytes) in original.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (!rebuilt.TryGetValue(path, out var text))
                    continue;
                files++;
                var normalized = GuiBundle.Normalize(bytes);
                if (normalized != text)
                    failures.Add($"{key.Item1} {key.Item2} ({gameVersion}): gui/{path} rebuilds differently at character {FirstDifference(normalized, text)}.");
            }
        }

        // Nothing unused, and nothing twice.
        foreach (var table in reader.Tables)
        {
            var lines = reader.Lines(table);
            var reached = reader.Reached.GetValueOrDefault(table) ?? new HashSet<int>();
            if (reached.Count != lines.Length)
                failures.Add($"{table}: {lines.Length - reached.Count} of {lines.Length} line(s) are used by no package.");
            if (lines.Distinct(StringComparer.Ordinal).Count() != lines.Length)
                failures.Add($"{table}: holds a line twice.");
        }

        Log.Info($"  bundle checks: {actual.Count} package(s), {files} file(s) rebuilt, {failures.Count} failure(s)");
        if (failures.Count > 0)
        {
            foreach (var failure in failures.Take(50))
                Log.Info($"    {failure}");
            throw new InvalidDataException($"The GUI bundle failed {failures.Count} check(s); the first: {failures[0]}");
        }
    }

    /// <summary>Fails when the packed bundle is over <see cref="SizeBudget"/>: dedupe was probably lost somewhere.</summary>
    public static void CheckSize(string nupkg, long budget = SizeBudget)
    {
        var size = new FileInfo(nupkg).Length;
        if (size > budget)
            throw new InvalidDataException($"{Path.GetFileName(nupkg)} is {size / 1024} KiB, over the budget of {budget / 1024} KiB. Some record probably differs in every build now.");
    }

    /// <summary>The hash of the files, with bundle.json as it was hashed: without its contentHash.</summary>
    private static string HashOf(IReadOnlyDictionary<string, byte[]> files)
    {
        var index = (JsonObject) GuiBundle.Parse(files[GuiBundle.IndexFile])!;
        index.Remove("contentHash");
        var hashed = files.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        hashed[GuiBundle.IndexFile] = Encoding.UTF8.GetBytes(index.ToJsonString(GuiBundle.Json) + "\n");
        return GuiBundle.Hash(hashed);
    }

    private static int FirstDifference(string a, string b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i])
            i++;
        return i;
    }

    private static string FirstDifference(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
            if (i >= expected.Count || i >= actual.Count || expected[i] != actual[i])
                return $"{i}: expected {(i < expected.Count ? expected[i] : "nothing")}, found {(i < actual.Count ? actual[i] : "nothing")}";
        return "none";
    }
}
