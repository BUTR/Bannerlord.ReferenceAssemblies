using CommandLine;

namespace Bannerlord.ReferenceAssemblies;

/// <summary>
/// Three verbs over one build registry per app (builds/&lt;appId&gt;.json):
///
/// update         - asks Steam which branches exist right now, records their build ids and depot
///                  manifests, then reads the version of builds whose version is still unknown.
/// generate       - downloads the builds the registry says are not on the feed yet, strips the
///                  assemblies and packs them.
/// mark-published - records which builds the feed carries, so the other two never have to ask NuGet.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var ctrlC = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            if (eventArgs.SpecialKey != ConsoleSpecialKey.ControlC)
                return;
            ctrlC.Cancel();
            eventArgs.Cancel = true;
        };

        var exitCode = 0;
        try
        {
            await Parser.Default.ParseArguments<UpdateOptions, GenerateOptions, MarkPublishedOptions>(args).MapResult(
                (UpdateOptions o) => UpdateCommand.RunAsync(o, ctrlC.Token),
                (GenerateOptions o) => GenerateCommand.RunAsync(o, ctrlC.Token),
                (MarkPublishedOptions o) => MarkPublishedCommand.RunAsync(o, ctrlC.Token),
                _ =>
                {
                    exitCode = 1;
                    return Task.CompletedTask;
                });
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            exitCode = 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        // DepotDownloader keeps background threads alive after the Steam session ends.
        Environment.Exit(exitCode);
        return exitCode;
    }
}
