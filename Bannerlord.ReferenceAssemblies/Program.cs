using PCLExt.FileStorage;
using PCLExt.FileStorage.Extensions;
using PCLExt.FileStorage.Folders;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;

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
            public string Name { get; set; }
            public uint BuildId { get; set; }
        }

        private static IFolder GetModuleFolder(this IFolder folder, string module, bool isCore = true) =>
            isCore ? folder : folder.CreateFolder("Modules", CreationCollisionOption.OpenIfExists).CreateFolder(module, CreationCollisionOption.OpenIfExists);
        private static IList<IFile> GetModuleFiles(this IFolder folder, bool isCore = true) =>
            isCore ? folder.GetFiles("TaleWorlds.*") : folder.GetFiles();

        private static readonly string packageName = "Bannerlord.ReferenceAssemblies";
        private static readonly string appid = "261550";
        private static readonly string os = "windows";
        private static readonly string osarch = "64";
        private static readonly string filelist = "Assemblies.txt";
        private static readonly string depot = "261551";
        private static string login;
        private static string pass;
        private static string gtoken;
        private static readonly IFolder ExecutableFolder = new ApplicationRootFolder();
        public static void Main(string[] args)
        {
            login = args[1];
            pass = args[3];
            gtoken = args[5];

            Console.WriteLine("Checking branches...");
            var branches = GetAllBranches();
            foreach (var branchInfo in branches)
                DownloadBranch(new Branch() { Name = branchInfo.Name, BuildId = branchInfo.BuildIds.First()} );

            Console.WriteLine("Generating references...");
            foreach (var branchInfo in branches)
            {
                var branch = new Branch()
                {
                    Name = branchInfo.Name,
                    BuildId = branchInfo.BuildIds.First()
                };

                var rootFolder = ExecutableFolder
                    .GetFolder("depots")
                    .GetFolder(depot)
                    .GetFolder(branch.BuildId.ToString());

                GenerateReference(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                    GenerateReference(branch, module.Name, module);
            }

            Console.WriteLine("Generating packages...");
            foreach (var branchInfo in branches)
            {
                var branch = new Branch()
                {
                    Name = branchInfo.Name,
                    BuildId = branchInfo.BuildIds.First()
                };

                var rootFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(depot)
                    .GetFolder(branch.BuildId.ToString());

                GenerateNuget(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                    GenerateNuget(branch, module.Name, module);
            }

            PublishNuget();
        }

        private static List<BranchInfo> GetAllBranches() => new List<BranchInfo>()
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
        private static void DownloadBranch(Branch branch)
        {
            var folder = ExecutableFolder
                    .CreateFolder("depots", CreationCollisionOption.OpenIfExists)
                    .CreateFolder(depot, CreationCollisionOption.OpenIfExists)
                    .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

            var file = ExecutableFolder.GetFile(filelist);
            var args = $"-app {appid} -depot {depot} -beta {branch.BuildId} -os {os} -osarch {osarch} -username {login} -password {pass} -filelist {file.Path} -dir {folder.Path}".Split(' ');
            var type = Type.GetType("DepotDownloader.Program, DepotDownloader");
            var mainMethod = type.GetMethod("Main", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            mainMethod.Invoke(null, new object[] { args });
        }

        private static void GenerateReference(Branch branch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);

            var outputFolder = ExecutableFolder
                .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                .CreateFolder(depot, CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .GetModuleFolder(moduleName, isCore)
                .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
            {
                var args = $"-f|-o|{Path.Combine(outputFolder.Path, file.Name)}|{file.Path}".Split('|');
                ReferenceAssemblyGenerator.CLI.Program.Main(args);
            }
        }

        private static string GenerateNuspec(Branch branch, string moduleName) =>
            $@"&lt;?xml version=&quot;1.0&quot; encoding=&quot;utf-8&quot;?&gt;
&lt;package&gt;
    &lt;metadata minClientVersion=&quot;3.3&quot;&gt;
        &lt;id&gt;{packageName}.{moduleName}&lt;/id&gt;
        &lt;version&gt;{branch.Name}&lt;/version&gt;
        &lt;title&gt;Bannerlord Game Reference Assemblies&lt;/title&gt;
        &lt;authors&gt;The Mount &amp;amp; Blade Development Community&lt;/authors&gt;
        &lt;description&gt;Contains stripped metadata-only libraries for building against Mount &amp;amp; Blade II: Bannerlord.&lt;/description&gt;
        &lt;tags&gt;bannerlord game reference assemblies&lt;/tags&gt;
        &lt;developmentDependency&gt;true&lt;/developmentDependency&gt;
        &lt;requireLicenseAcceptance&gt;false&lt;/requireLicenseAcceptance&gt;
        &lt;repository type=&quot;git&quot; url=&quot;https://github.com/Bannerlord-Unofficial-Modding-Community/Bannerlord.ReferenceAssemblies.git&quot;  /&gt;
    &lt;/metadata&gt;
    &lt;files&gt;
        &lt;file src=&quot;ref\*.dll&quot; target=&quot;ref&quot;/&gt;
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
                .CreateFolder(depot, CreationCollisionOption.OpenIfExists)
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
            Process.Start("dotnet", $"tool install gpr -g --version 0.1.13-beta").WaitForExit();

            foreach (var file in ExecutableFolder.GetFolder("final").GetFiles("*.nupkg"))
                Process.Start("gpr", $"push {file.Path} -k {gtoken}").WaitForExit();
        }
    }
}
