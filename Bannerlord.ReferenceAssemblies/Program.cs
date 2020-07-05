using Bannerlord.ReferenceAssemblies.Options;

using CommandLine;

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    /// <summary>
    /// 1. Check NuGet feed to get all generated versions
    /// 2. Check Steam for game branches
    /// 3. Compare NuGet list with the current branches
    /// 4. Download all new versions from Steam with out file filter
    /// 5. Generate reference assemblies from th downloaded files
    /// 6. Generate nuspec and csproj files
    /// 7. Use dotnet pack to generate the nupgk
    /// 8. Upload packages to feed
    ///
    /// PCLExt was used because it's good for prototyping IMO
    /// Ideally, should be replaced by scripts in the future
    /// </summary>
    public static class Program
    {
        public static async Task Main(string[] args)
        {
            var ctrlC = new CancellationTokenSource();
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                if (eventArgs.SpecialKey != ConsoleSpecialKey.ControlC)
                    return;

                ctrlC.Cancel();
                eventArgs.Cancel = true;
            };

            Trace.Listeners.Add(new CustomTraceListener(Console.Out));


            var result = Parser.Default.ParseArguments<GenerateOptions>(args);
            await result.WithParsedAsync<GenerateOptions>(o => new Tool(o).ExecuteAsync(ctrlC.Token));
            result.WithNotParsed(e => Console.Error.WriteLine(e.ToString()));
        }
    }
}