using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;

namespace Bannerlord.ReferenceAssemblies
{

    internal class ButrNugetContext
    {

        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

        private readonly string _githubToken;

        public ButrNugetContext(string githubToken)
            => _githubToken = githubToken;

        public void Publish()
            => ExecutableFolder.GetFolder("final").GetFiles("*.nupkg")
                .AsParallel()
                .WithDegreeOfParallelism(8)
                .Select(file => Process.Start("gpr", $"push {file.Path} -k {_githubToken}"))
                .ForAll(proc => proc.WaitForExit());

        public IReadOnlyDictionary<string, IReadOnlyList<string>> GetVersions(string userOrOrg)
        {
            if (ProcessHelpers.Run("dotnet", $"gpr list {userOrOrg} -k {_githubToken}", out var output) != 0)
                Console.WriteLine();
            var lines = output.Split(new[] {'\r', '\n'}, StringSplitOptions.RemoveEmptyEntries);
            var returnVal = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var line in lines)
            {
                if (line.StartsWith("http") || line.StartsWith("[PRIVATE REPOSITORIES]"))
                    continue;

                var line1 = line.Trim().Split('(', ')');
                var versions = line1[2].Trim().TrimStart('[').TrimEnd(']').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                returnVal.Add(line1[0].Trim(), versions.Select(v => v.Trim(',')).ToList());
            }

            return returnVal;
        }

    }

}