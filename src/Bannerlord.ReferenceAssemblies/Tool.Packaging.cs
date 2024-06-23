using PCLExt.FileStorage;
using PCLExt.FileStorage.Extensions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Bannerlord.ReferenceAssemblies;

internal partial class Tool
{
    private static string GetSuffix(BranchType branchType) =>
        SteamAppBranch.VersionPrefixToName[branchType] is { } str ? $".{str}" : "";

    private string GetPackageName(string module, BranchType branchType) =>
        $"Bannerlord.ReferenceAssemblies{(string.IsNullOrEmpty(module) ? "" : $".{module}")}{GetSuffix(branchType)}";

    private void GeneratePackages(IEnumerable<SteamAppBranchWithVersion> toDownload)
    {
        foreach (var branch in toDownload)
        {
            var refFolder = ExecutableFolder
                .GetFolder("ref")
                .GetFolder(branch.BuildId.ToString());

            var deps = new List<string> { "Core" };
            var version = $"{branch.Version.Substring(1)}.{branch.ChangeSet}{(branch.IsBeta ? "-beta" : "")}";
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

    private string GenerateNuspec(SteamAppBranchWithVersion steamAppBranch, string version, string moduleName) =>
        TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/package-nuspec-template.xml"),
            new Dictionary<string, string>
            {
                {"packageName", "Bannerlord.ReferenceAssemblies"},
                {"moduleName", moduleName},
                {"appId", steamAppBranch.AppId.ToString()},
                {"buildId", steamAppBranch.BuildId.ToString()},
                {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                {"packageVersion", version}
            });

    private string GenerateCsproj(SteamAppBranchWithVersion steamAppBranch, string moduleName) =>
        TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/package-csproj-template.xml"),
            new Dictionary<string, string>
            {
                {"packageName", "Bannerlord.ReferenceAssemblies"},
                {"moduleName", moduleName},
                {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
            });

    private string GenerateMetaNuspec(SteamAppBranchWithVersion steamAppBranch, string version, IEnumerable<string> deps)
    {
        var dependenciesXml = deps.Select(dep => new XElement("dependency",
            new XAttribute("id", $"Bannerlord.ReferenceAssemblies.{dep}{GetSuffix(steamAppBranch.Prefix)}"),
            new XAttribute("version", version))).ToList();
        return TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/metapackage-nuspec-template.xml"),
            new Dictionary<string, string>
            {
                {"packageName", "Bannerlord.ReferenceAssemblies"},
                {"appId", steamAppBranch.AppId.ToString()},
                {"buildId", steamAppBranch.BuildId.ToString()},
                {"packageVersion", version},
                {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
                {"dependenciesXml", string.Join(Environment.NewLine, dependenciesXml.Select(x => x.ToString()))}
            });
    }

    private string GenerateMetaCsproj(SteamAppBranchWithVersion steamAppBranch) =>
        TemplateHelpers.ApplyTemplate(Resourcer.Resource.AsString("Resources/metapackage-csproj-template.xml"),
            new Dictionary<string, string>
            {
                {"packageName", "Bannerlord.ReferenceAssemblies"},
                {"packageNameSuffix", GetSuffix(steamAppBranch.Prefix)},
            });

    private void GenerateNupkg(SteamAppBranchWithVersion steamAppBranch, string version, string moduleName, IFolder rootFolder)
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
            .WriteAllText(GenerateNuspec(steamAppBranch, version, name));

        nugetFolder
            .CreateFile($"{fileNameBase}.csproj", CreationCollisionOption.ReplaceExisting)
            .WriteAllText(GenerateCsproj(steamAppBranch, name));

        foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
            file.Copy(nugetRefFolder.CreateFile(file.Name, CreationCollisionOption.ReplaceExisting));

        var finalFolder = ExecutableFolder
            .CreateFolder("final", CreationCollisionOption.OpenIfExists);

        ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", nugetFolder.Path);
    }

    private void GenerateMetaNupkg(SteamAppBranchWithVersion steamAppBranch, string version, IEnumerable<string> deps)
    {
        var nugetFolder = ExecutableFolder
            .CreateFolder("nuget", CreationCollisionOption.OpenIfExists)
            .CreateFolder(steamAppBranch.BuildId.ToString(), CreationCollisionOption.OpenIfExists)
            .CreateFolder("Meta", CreationCollisionOption.OpenIfExists);

        var fileNameBase = GetPackageName("", steamAppBranch.Prefix);

        nugetFolder
            .CreateFile($"{fileNameBase}.nuspec", CreationCollisionOption.ReplaceExisting)
            .WriteAllText(GenerateMetaNuspec(steamAppBranch, version, deps));

        nugetFolder
            .CreateFile($"{fileNameBase}.csproj", CreationCollisionOption.ReplaceExisting)
            .WriteAllText(GenerateMetaCsproj(steamAppBranch));

        var finalFolder = ExecutableFolder
            .CreateFolder("final", CreationCollisionOption.OpenIfExists);

        ProcessHelpers.Run("dotnet", $"pack -o \"{finalFolder.Path}\"", nugetFolder.Path);
    }
}