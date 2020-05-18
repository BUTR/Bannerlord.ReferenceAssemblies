using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;
using PCLExt.FileStorage;
using PCLExt.FileStorage.Extensions;

namespace Bannerlord.ReferenceAssemblies
{

    public static partial class Program
    {

        private static void GeneratePackages(IEnumerable<SteamAppBranch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var rootFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(steamDepotId.ToString())
                    .GetFolder(branch.BuildId.ToString());

                var deps = new List<string> {"Core"};
                GenerateNupkg(branch, "", rootFolder);
                foreach (var module in rootFolder.GetFolder("Modules").GetFolders())
                {
                    deps.Add(module.Name);
                    GenerateNupkg(branch, module.Name, module);
                }

                GenerateMetaNupkg(branch, rootFolder, deps);
            }
        }

        private static string GenerateNuspec(SteamAppBranch steamAppBranch, string moduleName)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("package-nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"moduleName", moduleName},
                    {"appVersion", steamAppBranch.Version},
                    {"buildId", steamAppBranch.BuildId.ToString()},
                    {"versionPrefix", steamAppBranch.Prefix.ToString()},
                    {"packageVersion", steamAppBranch.Version.Substring(1)}
                });

        private static string GenerateCsproj(SteamAppBranch steamAppBranch, string moduleName)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("package-csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"moduleName", moduleName},
                    {"versionPrefix", steamAppBranch.Prefix.ToString()},
                    {"packageVersion", steamAppBranch.Version.Substring(1)}
                });

        private static string GenerateMetaNuspec(SteamAppBranch steamAppBranch, IEnumerable<string> deps)
        {
            var versionPrefix = steamAppBranch.Prefix.ToString();
            var packageVersion = steamAppBranch.Version.Substring(1);
            var dependenciesXml = deps.Select(dep
                => new XElement("dependency",
                    new XAttribute("id", $"{PackageName}.{dep}.{versionPrefix}"),
                    new XAttribute("version", packageVersion))).ToString();
            return TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("metapackage-nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"appVersion", steamAppBranch.Version},
                    {"buildId", steamAppBranch.BuildId.ToString()},
                    {"packageVersion", packageVersion},
                    {"versionPrefix", versionPrefix},
                    {"dependenciesXml", dependenciesXml}
                });
        }

        private static string GenerateMetaCsproj(SteamAppBranch steamAppBranch)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("metapackage-csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"versionPrefix", steamAppBranch.Prefix.ToString()}
                });

        private static void GenerateNupkg(SteamAppBranch steamAppBranch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);
            var name = isCore ? "Core" : moduleName;

            var outputFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(name, CreationCollisionOption.OpenIfExists);
            var refFolder = outputFolder.CreateFolder("ref", CreationCollisionOption.OpenIfExists);

            outputFolder
                .CreateFile($"{PackageName}.{name}.{steamAppBranch.Prefix}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateNuspec(steamAppBranch, name));

            outputFolder
                .CreateFile($"{PackageName}.{name}.{steamAppBranch.Prefix}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateCsproj(steamAppBranch, name));

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
                file.Copy(refFolder.CreateFile(file.Name, CreationCollisionOption.ReplaceExisting));

            var finalFolder = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", outputFolder.Path);
            Process.Start(new ProcessStartInfo("dotnet", $"pack -o {finalFolder.Path}")
            {
                WorkingDirectory = outputFolder.Path
            })!.WaitForExit();
        }
        private static void GenerateMetaNupkg(SteamAppBranch steamAppBranch, IFolder rootFolder, IEnumerable<string> deps)
        {

            var outputFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder("Meta", CreationCollisionOption.OpenIfExists);
            var refFolder = outputFolder.CreateFolder("ref", CreationCollisionOption.OpenIfExists);

            outputFolder
                .CreateFile($"{PackageName}.{steamAppBranch.Prefix}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateMetaNuspec(steamAppBranch, deps));

            outputFolder
                .CreateFile($"{PackageName}.{steamAppBranch.Prefix}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateMetaCsproj(steamAppBranch));

            var finalFolder = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", outputFolder.Path);
            Process.Start(new ProcessStartInfo("dotnet", $"pack -o {finalFolder.Path}")
            {
                WorkingDirectory = outputFolder.Path
            })!.WaitForExit();
        }

    }

}