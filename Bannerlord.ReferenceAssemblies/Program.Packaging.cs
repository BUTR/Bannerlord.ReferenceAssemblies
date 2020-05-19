using System;
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
        private static string GetSuffix(BranchType branchType)
            => SteamAppBranch.VersionPrefixToName[branchType] is { } str ? $".{str}" : "";

        private static string GetPackageName(string module, BranchType branchType)
            => $"{PackageName}{(string.IsNullOrEmpty(module) ? "" : $".{module}")}{(GetSuffix(branchType))}";

        private static void GeneratePackages(IEnumerable<SteamAppBranch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var refFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(steamDepotId.ToString())
                    .GetFolder(branch.BuildId.ToString());

                var deps = new List<string> {"Core"};
                // Core
                GenerateNupkg(branch, "", refFolder);
                // Modules
                foreach (var module in refFolder.GetFolder("Modules").GetFolders())
                {
                    deps.Add(module.Name);
                    GenerateNupkg(branch, module.Name, module);
                }

                // Meta
                GenerateMetaNupkg(branch, refFolder, deps);
            }
        }

        private static string GenerateNuspec(SteamAppBranch steamAppBranch, string moduleName)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("package-nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"moduleName", moduleName},
                    {"appVersion", steamAppBranch.Version},
                    {"appId", steamAppBranch.AppId.ToString()},
                    {"depotId", steamAppBranch.DepotId.ToString()},
                    {"buildId", steamAppBranch.BuildId.ToString()},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"packageVersion", steamAppBranch.Version}
                });

        private static string GenerateCsproj(SteamAppBranch steamAppBranch, string moduleName)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("package-csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"moduleName", moduleName},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"packageVersion", steamAppBranch.Version}
                });

        private static string GenerateMetaNuspec(SteamAppBranch steamAppBranch, IEnumerable<string> deps)
        {
            var packageVersion = steamAppBranch.Version;
            var dependenciesXml = deps.Select(dep
                => new XElement("dependency",
                    new XAttribute("id", $"{PackageName}.{dep}{GetSuffix(steamAppBranch.Prefix)}"),
                    new XAttribute("version", packageVersion))).ToList();
            return TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("metapackage-nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"appVersion", steamAppBranch.Version},
                    {"appId", steamAppBranch.AppId.ToString()},
                    {"depotId", steamAppBranch.DepotId.ToString()},
                    {"buildId", steamAppBranch.BuildId.ToString()},
                    {"packageVersion", packageVersion},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"dependenciesXml", string.Join(Environment.NewLine, dependenciesXml.Select(x => x.ToString()))}
                });
        }

        private static string GenerateMetaCsproj(SteamAppBranch steamAppBranch)
            => TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("metapackage-csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", PackageName},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)}
                });

        private static void GenerateNupkg(SteamAppBranch steamAppBranch, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);
            var name = isCore ? "Core" : moduleName;

            var nugetFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(name, CreationCollisionOption.OpenIfExists);
            var nugetRefFolder = nugetFolder.CreateFolder("ref", CreationCollisionOption.OpenIfExists);

            var fileNameBase = GetPackageName(name, steamAppBranch.Prefix);

            nugetFolder
                .CreateFile($"{fileNameBase}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateNuspec(steamAppBranch, name));

            nugetFolder
                .CreateFile($"{fileNameBase}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateCsproj(steamAppBranch, name));

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
                file.Copy(nugetRefFolder.CreateFile(file.Name, CreationCollisionOption.ReplaceExisting));

            var finalFolder = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", nugetFolder.Path);
        }

        private static void GenerateMetaNupkg(SteamAppBranch steamAppBranch, IFolder rootFolder, IEnumerable<string> deps)
        {
            var nugetFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamDepotId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder("Meta", CreationCollisionOption.OpenIfExists);

            var fileNameBase = GetPackageName("", steamAppBranch.Prefix);

            nugetFolder
                .CreateFile($"{fileNameBase}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateMetaNuspec(steamAppBranch, deps));

            nugetFolder
                .CreateFile($"{fileNameBase}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateMetaCsproj(steamAppBranch));

            var finalFolder = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", nugetFolder.Path);
        }

    }

}