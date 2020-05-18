using System.Collections.Generic;
using System.Diagnostics;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Extensions;

namespace Bannerlord.ReferenceAssemblies
{

    public static partial class Program
    {

        private static void GeneratePackages(IEnumerable<Branch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var rootFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(steamDepotId.ToString())
                    .GetFolder(branch.BuildId.ToString());

                GenerateNuget(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                    GenerateNuget(branch, module.Name, module);
            }
        }

        private static string GenerateNuspec(Branch branch, string moduleName)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"moduleName", moduleName},
                    {"branchVersion", PackageName},
                    {"appId", steamAppId.ToString()},
                    {"depotId", steamDepotId.ToString()},
                    {"buildId", branch.BuildId.ToString()},
                });

        private static string GenerateCsproj(Branch branch, string moduleName)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"moduleName", moduleName},
                });

        private static void GenerateNuget(Branch branch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);
            var name = isCore ? "Core" : moduleName;

            var outputFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(name, CreationCollisionOption.OpenIfExists);
            var refFolder = outputFolder.CreateFolder("ref", CreationCollisionOption.OpenIfExists);

            outputFolder.CreateFile($"{PackageName}.{name}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateNuspec(branch, name));
            outputFolder.CreateFile($"{PackageName}.{name}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateCsproj(branch, name));

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
                file.Copy(refFolder.CreateFile(file.Name, CreationCollisionOption.ReplaceExisting));

            var outputFolder1 = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{outputFolder1.Path}\"", outputFolder.Path);
            Process.Start(new ProcessStartInfo("dotnet", $"pack -o {outputFolder1.Path}")
            {
                WorkingDirectory = outputFolder.Path
            })!.WaitForExit();
        }

    }

}