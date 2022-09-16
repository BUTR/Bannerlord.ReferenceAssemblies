using PCLExt.FileStorage;
using PCLExt.FileStorage.Extensions;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Xml.Linq;

namespace Bannerlord.ReferenceAssemblies
{
    internal partial class Tool
    {
        private static string GetSuffix(BranchType branchType) =>
            SteamAppBranch.VersionPrefixToName[branchType] is { } str ? $".{str}" : "";

        private string GetPackageName(string module, BranchType branchType) =>
            $"{_options.PackageBaseName}{(string.IsNullOrEmpty(module) ? "" : $".{module}")}{GetSuffix(branchType)}";

        private void GeneratePackages(IEnumerable<SteamAppBranch> toDownload)
        {
            foreach (var branch in toDownload)
            {
                var refFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(branch.BuildId.ToString());

                var coreBinFolder = ExecutableFolder
                    .GetFolder("ref")
                    .GetFolder(branch.BuildId.ToString())
                    .GetFolder("bin")
                    .GetFolder("Win64_Shipping_Client");
                var version = GetAssembliesVersion(coreBinFolder.Path)?.Split('.').Last();
                if (version == null)
                {
                    Trace.WriteLine($"Branch {branch.Name} ({branch.AppId} {branch.BuildId}) does not include a readable App Version, skipping...");
                    continue;
                }

                var deps = new List<string> { "Core" };
                // Core
                GenerateNupkg(branch, version, "", refFolder);
                // Modules
                foreach (var module in refFolder.GetFolder("Modules").GetFolders())
                {
                    deps.Add(module.Name);
                    GenerateNupkg(branch, version, module.Name, module);
                }

                // Meta
                GenerateMetaNupkg(branch, version, deps);
            }
        }

        private string GenerateNuspec(SteamAppBranch steamAppBranch, string appVersion, string moduleName) =>
            TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/package-nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", _options.PackageBaseName},
                    {"moduleName", moduleName},
                    {"appVersion", appVersion},
                    {"appId", steamAppBranch.AppId.ToString()},
                    {"buildId", steamAppBranch.BuildId.ToString()},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"packageVersion", steamAppBranch.GetVersion(appVersion)}
                });

        private string GenerateCsproj(SteamAppBranch steamAppBranch, string appVersion, string moduleName) =>
            TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/package-csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", _options.PackageBaseName},
                    {"moduleName", moduleName},
                    {"appVersion", appVersion},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"packageVersion", steamAppBranch.GetVersion(appVersion)}
                });

        private string GenerateMetaNuspec(SteamAppBranch steamAppBranch, string appVersion, IEnumerable<string> deps)
        {
            var packageVersion = steamAppBranch.GetVersion(appVersion);
            var dependenciesXml = deps.Select(dep => new XElement("dependency",
                    new XAttribute("id", $"{_options.PackageBaseName}.{dep}{GetSuffix(steamAppBranch.Prefix)}"),
                    new XAttribute("version", packageVersion))).ToList();
            return TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/metapackage-nuspec-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", _options.PackageBaseName},
                    {"appVersion", appVersion},
                    {"appId", steamAppBranch.AppId.ToString()},
                    {"buildId", steamAppBranch.BuildId.ToString()},
                    {"packageVersion", packageVersion},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"dependenciesXml", string.Join(Environment.NewLine, dependenciesXml.Select(x => x.ToString()))}
                });
        }

        private string GenerateMetaCsproj(SteamAppBranch steamAppBranch, string appVersion) =>
            TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/metapackage-csproj-template.xml"),
                new Dictionary<string, string>
                {
                    {"packageName", _options.PackageBaseName},
                    {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                    {"appVersion", appVersion},
                });

        private void GenerateNupkg(SteamAppBranch steamAppBranch, string appVersion, string moduleName, IFolder rootFolder)
        {
            var isCore = string.IsNullOrEmpty(moduleName);
            var name = isCore ? "Core" : moduleName;

            var nugetFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder(name, CreationCollisionOption.OpenIfExists);
            var nugetRefFolder = nugetFolder.CreateFolder("ref", CreationCollisionOption.OpenIfExists);

            var fileNameBase = GetPackageName(name, steamAppBranch.Prefix);

            nugetFolder
                .CreateFile($"{fileNameBase}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateNuspec(steamAppBranch, appVersion, name));

            nugetFolder
                .CreateFile($"{fileNameBase}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateCsproj(steamAppBranch, appVersion, name));

            foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
                file.Copy(nugetRefFolder.CreateFile(file.Name, CreationCollisionOption.ReplaceExisting));

            var finalFolder = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", nugetFolder.Path);
        }

        private void GenerateMetaNupkg(SteamAppBranch steamAppBranch, string appVersion, IEnumerable<string> deps)
        {
            var nugetFolder = ExecutableFolder
                .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
                .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
                .CreateFolder("Meta", CreationCollisionOption.OpenIfExists);

            var fileNameBase = GetPackageName("", steamAppBranch.Prefix);

            nugetFolder
                .CreateFile($"{fileNameBase}.nuspec", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateMetaNuspec(steamAppBranch, appVersion, deps));

            nugetFolder
                .CreateFile($"{fileNameBase}.csproj", CreationCollisionOption.ReplaceExisting)
                .WriteAllText(GenerateMetaCsproj(steamAppBranch, appVersion));

            var finalFolder = ExecutableFolder
                .CreateFolder("final", CreationCollisionOption.OpenIfExists);

            ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", nugetFolder.Path);
        }
    }
}