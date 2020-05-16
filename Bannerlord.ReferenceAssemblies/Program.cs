using PCLExt.FileStorage;
using PCLExt.FileStorage.Extensions;
using PCLExt.FileStorage.Folders;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using DepotDownloader;
using SteamKit2;

namespace Bannerlord.ReferenceAssemblies
{
    public static class Program
    {
        private struct BranchInfo
        {
            public string Name { get; set; }
            public List<uint> BuildIds { get; set; }
        }
        private struct Branch
        {
            public string Version { get; set; }
            public string Name { get; set; }
            public uint BuildId { get; set; }
        }

        private static IFolder GetModuleFolder(this IFolder folder, string module, bool isCore = true) =>
            isCore ? folder : folder.CreateFolder("Modules", CreationCollisionOption.OpenIfExists).CreateFolder(module, CreationCollisionOption.OpenIfExists);
        private static IList<IFile> GetModuleFiles(this IFolder folder, bool isCore = true) =>
            isCore ? folder.GetFiles("TaleWorlds.*") : folder.GetFiles();

        private static readonly string packageName = "Bannerlord.ReferenceAssemblies";
        private static readonly uint appid = 261550;
        private static readonly string os = "windows";
        private static readonly string osarch = "64";
        private static readonly string filelist = "Assemblies.txt";
        private static readonly uint depot = 261551;
        private static string login;
        private static string pass;
        private static string gtoken;
        private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);
        public static void Main(string[] args)
        {
            login = args[1];
            pass = args[3];
            gtoken = args[5];

            Process.Start("dotnet", $"tool install gpr -g").WaitForExit();



            Console.WriteLine("Checking NuGet...");
            var packages = GetNugetVersions();
            Console.WriteLine("Checking branches...");
            var branches = GetAllBranches();

            var coreNugetVersions = packages.TryGetValue("Bannerlord.ReferenceAssemblies.Core", out var v) ? v : new List<string>();
            var toDownload = new List<(string, string, string)>();
            foreach (var (version, branch, buildId) in branches)
            {
                if (string.IsNullOrEmpty(version) || coreNugetVersions.Contains(version))
                    continue;
                toDownload.Add((version, branch, buildId));
            }

            foreach (var (version, name, buildId) in toDownload)
                DownloadBranch(new Branch() { Version = version, Name = name, BuildId = uint.TryParse(buildId, out var r) ? r : 0 } );
            ContentDownloader.ShutdownSteam3();

            Console.WriteLine("Generating references...");
            foreach (var (version, name, buildId) in toDownload)
            {
                var branch = new Branch()
                {
                    Version = version,
                    Name = name,
                    BuildId = uint.TryParse(buildId, out var r) ? r : 0
                };

                var rootFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(depot.ToString())
                    .GetFolder(branch.BuildId.ToString());

                GenerateReference(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                    GenerateReference(branch, module.Name, module);
            }

            Console.WriteLine("Generating packages...");
            foreach (var (version, name, buildId) in toDownload)
            {
                var branch = new Branch()
                {
                    Version = version,
                    Name = name,
                    BuildId = uint.TryParse(buildId, out var r) ? r : 0
                };

                var rootFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(depot.ToString())
                    .GetFolder(branch.BuildId.ToString());

                GenerateNuget(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                    GenerateNuget(branch, module.Name, module);
            }

            PublishNuget();
        }

        public static Dictionary<string, List<string>> GetNugetVersions()
        {
            Console.WriteLine("Starting GPR...");
            var process = new Process
            {
                StartInfo =
                {
                    FileName = "gpr",
                    Arguments = $"list -k {gtoken}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            var lines = new List<string>();
            process.Start();
            while (!process.StandardOutput.EndOfStream)
                lines.Add(process.StandardOutput.ReadLine());
            process.WaitForExit();
            var returnVal = new Dictionary<string, List<string>>();
            Console.WriteLine("Parsing GPR output...");
            foreach (var line in lines)
            {
                if (line.StartsWith("http") || line.StartsWith("[PRIVATE REPOSITORIES]"))
                    continue;
                var line1 = line.Trim().Split(new [] { '(', ')' });
                var versions = line1[2].Trim().TrimStart('[').TrimEnd(']').Split(' ', StringSplitOptions.RemoveEmptyEntries);
                returnVal.Add(line1[0], versions.ToList());
                Console.WriteLine($"Found package {line1[0]}, versions {string.Join(' ', versions)}");
            }

            return returnVal;
        }
        private static (string, string, string) ConvertVersion(string version, string buildId)
        {
            var letter = version[0];
            if (char.IsDigit(version[1]))
            {
                return ($"{version[1..]}.{buildId}-{letter}", version, buildId);
            }
            else
            {
                return ("", version, buildId);
            }
        }
        private static List<(string, string, string)> GetAllBranches()
        {
            AccountSettingsStore.LoadFromFile("account.config");
            DepotDownloader.Program.InitializeSteam(login, pass);
            ContentDownloader.steam3.RequestAppInfo(appid);
            var depots = ContentDownloader.GetSteam3AppSection(appid, EAppInfoSection.Depots);
            var branches = depots["branches"];
            return branches.Children.Select(c => ConvertVersion(c.Name, c["buildid"].Value)).ToList();
            /*
            return new List<BranchInfo>()
            {
                new BranchInfo()
                {
                    Name = "1.4.0.5028281-e",
                    BuildIds = new List<uint>()
                    {
                        5028281
                    }
                }
            };
            */
        }

        private static void DownloadBranch(Branch branch)
        {
            var folder = ExecutableFolder
                .CreateFolder("depots", CreationCollisionOption.OpenIfExists)
                .CreateFolder(depot.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

            ContentDownloader.Config.MaxDownloads = 4;
            ContentDownloader.Config.InstallDirectory = folder.Path;

            var fileList = ExecutableFolder.GetFile(filelist).Path;
            string[] files = null;
            try
            {
                string fileListData = File.ReadAllText(fileList);
                files = fileListData.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                ContentDownloader.Config.UsingFileList = true;
                ContentDownloader.Config.FilesToDownload = new List<string>();
                ContentDownloader.Config.FilesToDownloadRegex = new List<Regex>();

                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                foreach (var fileEntry in files)
                {
                    try
                    {
                        string fileEntryProcessed;
                        if (isWindows)
                        {
                            // On Windows, ensure that forward slashes can match either forward or backslashes in depot paths
                            fileEntryProcessed = fileEntry.Replace("/", "[\\\\|/]");
                        }
                        else
                        {
                            // On other systems, treat / normally
                            fileEntryProcessed = fileEntry;
                        }
                        Regex rgx = new Regex(fileEntryProcessed, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                        ContentDownloader.Config.FilesToDownloadRegex.Add(rgx);
                    }
                    catch
                    {
                        // For anything that can't be processed as a Regex, allow both forward and backward slashes to match
                        // on Windows
                        if (isWindows)
                        {
                            ContentDownloader.Config.FilesToDownload.Add(fileEntry.Replace("/", "\\"));
                        }
                        ContentDownloader.Config.FilesToDownload.Add(fileEntry);
                        continue;
                    }
                }

                Console.WriteLine("Using filelist: '{0}'.", fileList);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Warning: Unable to load filelist: {0}", ex.ToString());
            }

            ContentDownloader.DownloadAppAsync(appid, depot, ContentDownloader.INVALID_MANIFEST_ID, branch.Name, os,
                osarch, null, false, true).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static void GenerateReference(Branch branch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);

            var outputFolder = ExecutableFolder
                .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                .CreateFolder(depot.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .GetModuleFolder(moduleName, isCore)
                .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
            {
                var args = $"-f|--keep-non-public|-o|{Path.Combine(outputFolder.Path, file.Name)}|{file.Path}".Split('|');
                ReferenceAssemblyGenerator.CLI.Program.Main(args);
            }
        }

        private static string GenerateNuspec(Branch branch, string moduleName) =>
            $@"&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-8&quot;?&gt;
&lt;package&gt;
    &lt;metadata minClientVersion=&quot;3.3&quot;&gt;
        &lt;id&gt;{packageName}.{moduleName}&lt;/id&gt;
        &lt;version&gt;{branch.Version}&lt;/version&gt;
        &lt;title&gt;Bannerlord Game Reference Assemblies&lt;/title&gt;
        &lt;authors&gt;The Mount &amp;amp; Blade Development Community&lt;/authors&gt;
        &lt;description&gt;Contains stripped metadata-only libraries for building against Mount &amp;amp; Blade II: Bannerlord.&lt;/description&gt;
        &lt;tags&gt;bannerlord game reference assemblies&lt;/tags&gt;
        &lt;developmentDependency&gt;true&lt;/developmentDependency&gt;
        &lt;requireLicenseAcceptance&gt;false&lt;/requireLicenseAcceptance&gt;
        &lt;repository type=&quot;git&quot; url=&quot;https://github.com/Bannerlord-Unofficial-Modding-Community/Bannerlord.ReferenceAssemblies.git&quot;  /&gt;
    &lt;/metadata&gt;
    &lt;files&gt;
        &lt;file src=&quot;ref\net472\*.exe&quot; target=&quot;ref&quot;/&gt;
        &lt;file src=&quot;ref\net472\*.dll&quot; target=&quot;ref&quot;/&gt;
        &lt;file src=&quot;ref\netstandard2.0\*.exe&quot; target=&quot;ref&quot;/&gt;
        &lt;file src=&quot;ref\netstandard2.0\*.dll&quot; target=&quot;ref&quot;/&gt;
    &lt;/files&gt;
&lt;/package&gt;
";
        private static string GenerateCsproj(Branch branch, string moduleName) =>
            $@"&lt;Project Sdk=&quot;Microsoft.NET.Sdk&quot;&gt;
  
  &lt;PropertyGroup&gt;
    &lt;TargetFramework&gt;netstandard2.0&lt;/TargetFramework&gt;
    &lt;NuspecFile&gt;{packageName}.{moduleName}.nuspec&lt;/NuspecFile&gt;

    &lt;NoBuild&gt;true&lt;/NoBuild&gt;
    &lt;NoRestore&gt;true&lt;/NoRestore&gt;
  &lt;/PropertyGroup&gt;
&lt;/Project&gt;
";

        private static void GenerateNuget(Branch branch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);
            var name = isCore ? "Core" : moduleName;

            var outputFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(depot.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(name, CreationCollisionOption.OpenIfExists);
            var refFolder = outputFolder.CreateFolder("ref", CreationCollisionOption.OpenIfExists);

            outputFolder.CreateFile($"{packageName}.{name}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(HttpUtility.HtmlDecode(GenerateNuspec(branch, name)));
            outputFolder.CreateFile($"{packageName}.{name}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(HttpUtility.HtmlDecode(GenerateCsproj(branch, name)));

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
                file.Copy(refFolder.CreateFile(file.Name, CreationCollisionOption.ReplaceExisting));

            var outputFolder1 = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            Process.Start(new ProcessStartInfo("dotnet", $"pack -o {outputFolder1.Path}")
            {
                WorkingDirectory = outputFolder.Path
            })!.WaitForExit();
        }

        public static void PublishNuget()
        {
            foreach (var file in ExecutableFolder.GetFolder("final").GetFiles("*.nupkg"))
                Process.Start("gpr", $"push {file.Path} -k {gtoken}").WaitForExit();
        }
    }
}
