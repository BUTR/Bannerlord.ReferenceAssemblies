using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    {
      foreach (var file in ExecutableFolder.GetFolder("final").GetFiles("*.nupkg"))
        Process.Start("gpr", $"push {file.Path} -k {_githubToken}")!.WaitForExit();
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetVersions()
    {
      var process = new Process
      {
        StartInfo =
        {
          FileName = "gpr",
          Arguments = $"list bannerlord-unofficial-modding-community  -k {_githubToken}",
          UseShellExecute = false,
          RedirectStandardOutput = true,
        }
      };
      var lines = new List<string>();
      process.Start();
      while (!process.StandardOutput.EndOfStream)
        lines.Add(process.StandardOutput.ReadLine());
      process.WaitForExit();
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