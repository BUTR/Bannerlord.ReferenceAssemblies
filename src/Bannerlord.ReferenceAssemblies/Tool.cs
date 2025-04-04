﻿using AsmResolver.DotNet;

using Bannerlord.ReferenceAssemblies.Options;

using NuGet.Packaging;

using PCLExt.FileStorage;
using PCLExt.FileStorage.Folders;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies;

internal partial class Tool
{
    private static readonly Dictionary<string, string> SupportMatrix = new()
    {
        {"BannerlordReferenceAssembliesMultiplayer", "v1.2.0" },
    };
    private static readonly Dictionary<string, string> ExcludeMatrix = new()
    {
        {"BannerlordReferenceAssembliesDedicatedCustomServerHelper", "v1.2.0" },
    };
    private static readonly HashSet<string> ExcludePublicMatrix = new()
    {
        {"BannerlordReferenceAssembliesDedicatedCustomServerHelper" },
    };

    private static readonly IFolder ExecutableFolder = new FolderFromPath(AppDomain.CurrentDomain.BaseDirectory);

    private readonly GenerateOptions _options;

    public Tool(GenerateOptions options)
    {
        _options = options;
    }

    private static string StripEnding(string packageId)
    {
        foreach (var (_, value) in SteamAppBranch.VersionPrefixToName)
        {
            packageId = packageId.Replace($".{value}", string.Empty);
        }
        return packageId;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var nugetFeed = new NuGetFeed(_options.FeedUrl, _options.FeedUser, _options.FeedPassword);

        Trace.WriteLine("Checking NuGet...");
        var packages = await nugetFeed.GetVersionsAsync(ct);

        foreach (var (key, value) in packages)
            Trace.WriteLine($"{key}: [{string.Join(", ", value)}]");

        Trace.WriteLine("Checking branches...");
        var branches = (await GetAllBranches()).ToList();
        //var publicBranch = branches.First(x => x.Name == "public");
        //if (branches.Any(x => x.BuildId == publicBranch.BuildId))
        //    branches.Remove(publicBranch);

        var packageNameWithBuildIds = new Dictionary<string, List<uint>>();
        foreach (var (packageId, package) in packages)
        {
            var strippedPackageId = StripEnding(packageId);
            if (!packageNameWithBuildIds.TryGetValue(strippedPackageId, out var buildIds))
            {
                buildIds = new List<uint>();
                packageNameWithBuildIds[strippedPackageId] = buildIds;
            }
            buildIds.AddRange(package.Select(x => x.BuildId));
        }

        var toDownload = new HashSet<SteamAppBranch>();
        foreach (var (packageId, buildIds) in packageNameWithBuildIds)
        {
            var missing = branches.Where(x => (!SupportMatrix.TryGetValue(packageId, out var val) || new AlphanumComparatorFast().Compare(val, x.Name) <= 0) &&
                                              (!ExcludeMatrix.TryGetValue(packageId, out var val2) || new AlphanumComparatorFast().Compare(val2, x.Name) > 0) &&
                                              (x.Name != "public" || !ExcludePublicMatrix.Contains(packageId)) &&
                                              !buildIds.Contains(x.BuildId)).ToArray();
            toDownload.AddRange(missing);
        }

        if (toDownload.Count == 0)
        {
            Trace.WriteLine("No new version detected! Exiting...");
            DepotDownloader.ContentDownloader.ShutdownSteam3();
            return;
        }

        Trace.WriteLine("New versions:");
        foreach (var br in toDownload)
            Trace.WriteLine($"{br.Name}: ({br.AppId} {br.BuildId})");

        Trace.WriteLine("Checking downloading...");
        await DownloadBranchesAsync(toDownload, ct);

        var toDownloadWithVersion = toDownload.Select(branch =>
        {
            var depotsBinFolder = ExecutableFolder
                .GetFolder("depots")
                .GetFolder(branch.BuildId.ToString());

            var version = GetAssembliesVersion(depotsBinFolder.Path);
            var changeSet = GetAssembliesAppVersion(depotsBinFolder.Path);
            if (version == null)
            {
                Trace.WriteLine($"Branch {branch.Name} ({branch.AppId} {branch.BuildId}) does not include a readable Version, skipping...");
                return null;
            }
            if (changeSet == null)
            {
                Trace.WriteLine($"Branch {branch.Name} ({branch.AppId} {branch.BuildId}) does not include a readable ChangeSet, skipping...");
                return null;
            }
            return new SteamAppBranchWithVersion(version, changeSet, branch);
        }).OfType<SteamAppBranchWithVersion>().ToArray();

        Trace.WriteLine("Generating references...");
        GenerateReferences(toDownloadWithVersion);

        Trace.WriteLine("Generating packages...");

        GeneratePackages(toDownloadWithVersion);

        Trace.WriteLine("Done!");
    }


    private static string? GetAssembliesAppVersion(string path) =>
        ProcessHelpers.Run("getblmeta", $"getchangeset -f {path}", out var versionStr) != 0
            ? null
            : versionStr.Replace("\r", "").Replace("\n", "");

    private static string? GetAssembliesVersion(string path) =>
        ProcessHelpers.Run("getblmeta", $"getversion -f {path}", out var versionStr) != 0
            ? null
            : versionStr.Replace("\r", "").Replace("\n", "");

    private void GenerateReferences(IEnumerable<SteamAppBranchWithVersion> toDownload)
    {
        foreach (var branch in toDownload)
        {
            var depotsFolder = ExecutableFolder
                .GetFolder("depots")
                .GetFolder(branch.BuildId.ToString());

            var refFolder = ExecutableFolder
                .CreateFolder("ref", CreationCollisionOption.OpenIfExists)
                .CreateFolder(branch.BuildId.ToString(), CreationCollisionOption.OpenIfExists);

            // core
            var coreRefFolder = refFolder
                .GetModuleFolder("")
                .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);
            GenerateReference("", depotsFolder, coreRefFolder);

            // official modules
            foreach (var module in depotsFolder.GetFolder("Modules").GetFolders())
            {
                var name = module.Name;
                var moduleOutputFolder = refFolder
                    .GetModuleFolder(name, false)
                    .CreateFolder("bin", CreationCollisionOption.OpenIfExists)
                    .CreateFolder("Win64_Shipping_Client", CreationCollisionOption.OpenIfExists);
                GenerateReference(name, module, moduleOutputFolder);
            }
        }
    }
    private static void GenerateReference(string moduleName, IFolder rootFolder, IFolder outputFolder)
    {
        var isCore = string.IsNullOrEmpty(moduleName);

        foreach (var file in rootFolder.GetFolder("bin").GetFolder("Win64_Shipping_Client").GetModuleFiles(isCore))
        {
            var outputFile = Path.Combine(outputFolder.Path, file.Name);
            ProcessHelpers.Run("assembly-publicizer", $"{file.Path} -o {outputFile} --strip-only");
        }

        var net472Folder = outputFolder.CreateFolder("net472", CreationCollisionOption.OpenIfExists);
        var netStandardFolder = outputFolder.CreateFolder("netstandard2.0", CreationCollisionOption.OpenIfExists);
        //var netcoreapp30Folder = outputFolder.CreateFolder("netcoreapp3.0", CreationCollisionOption.OpenIfExists);
        //var net60Folder = outputFolder.CreateFolder("net6.0", CreationCollisionOption.OpenIfExists);
        foreach (var outputFile in outputFolder.GetFiles())
        {
            var assembly = AssemblyDefinition.FromFile(outputFile.Path);
            if (assembly.Modules.First().OriginalTargetRuntime.IsNetStandard)
            {
                outputFile.Copy(net472Folder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
                outputFile.Copy(netStandardFolder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
                //outputFile.Copy(netcoreapp30Folder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
                //outputFile.Copy(net60Folder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
                outputFile.Delete();
            }
            else if (assembly.Modules.First().OriginalTargetRuntime.IsNetFramework)
            {
                outputFile.Move(net472Folder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
            }
            /*
            else if (assembly.Modules.First().OriginalTargetRuntime.IsNetCoreApp)
            {
                if (assembly.Modules.First().OriginalTargetRuntime.Version == new Version(3, 0))
                {
                    outputFile.Move(netcoreapp30Folder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
                }
                if (assembly.Modules.First().OriginalTargetRuntime.Version == new Version(6, 0))
                {
                    outputFile.Move(net60Folder.CreateFile(outputFile.Name, CreationCollisionOption.ReplaceExisting));
                }
            }
            */
        }
    }
}