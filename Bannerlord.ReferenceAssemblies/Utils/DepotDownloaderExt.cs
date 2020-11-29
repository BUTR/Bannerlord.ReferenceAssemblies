﻿using DepotDownloader;

using HarmonyLib;

using SteamKit2;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    public static class DepotDownloaderExt
    {
        private static Type ContentDownloaderType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.ContentDownloader")!;
        private static Type AccountSettingsStoreType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.AccountSettingsStore")!;
        private static Type ProgramType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.Program")!;
        private static Type Steam3SessionType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.Steam3Session")!;
        private static Type DownloadConfigType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.DownloadConfig")!;

        private static MethodInfo ShutdownSteam3Method { get; } = AccessTools.Method(ContentDownloaderType, "ShutdownSteam3");
        private static MethodInfo LoadFromFileMethod { get; } = AccessTools.Method(AccountSettingsStoreType, "LoadFromFile");
        private static MethodInfo InitializeSteamMethod { get; } = AccessTools.Method(ProgramType, "InitializeSteam");
        private static MethodInfo RequestAppInfoMethod { get; } = AccessTools.Method(Steam3SessionType, "RequestAppInfo");
        private static MethodInfo GetSteam3AppSectionMethod { get; } = AccessTools.Method(ContentDownloaderType, "GetSteam3AppSection");
        private static MethodInfo DownloadAppAsyncMethod { get; } = AccessTools.Method(ContentDownloaderType, "DownloadAppAsync");

        private static FieldInfo Steam3Field { get; } = AccessTools.Field(ContentDownloaderType, "steam3");
        private static FieldInfo ConfigField { get; } = AccessTools.Field(ContentDownloaderType, "Config");

        public static void ContentDownloaderShutdownSteam3() => ShutdownSteam3Method.Invoke(null, Array.Empty<object>());
        public static void AccountSettingsStoreLoadFromFile(string file) => LoadFromFileMethod.Invoke(null, new object?[] { file });
        public static void DepotDownloaderProgramInitializeSteam(string login, string password) => InitializeSteamMethod.Invoke(null, new object?[] { login, password });
        public static void ContentDownloadersteam3RequestAppInfo(uint appId) => RequestAppInfoMethod.Invoke(Steam3Field.GetValue(null), new object?[] { appId, false });
        public static KeyValue ContentDownloaderGetSteam3AppSection(uint appId) => (KeyValue) GetSteam3AppSectionMethod.Invoke(null, new object?[] { appId, EAppInfoSection.Depots })!;

        public static void ContentDownloaderConfigSetMaxDownloads(int maxDownloads)
        {
            var maxDownloadsProperty = AccessTools.Property(DownloadConfigType, "MaxDownloads");
            maxDownloadsProperty.SetValue(ConfigField.GetValue(null), maxDownloads);
        }
        public static void ContentDownloaderConfigSetInstallDirectory(string installDirectory)
        {
            var installDirectoryProperty = AccessTools.Property(DownloadConfigType, "InstallDirectory");
            installDirectoryProperty.SetValue(ConfigField.GetValue(null), installDirectory);
        }
        public static void ContentDownloaderConfigSetUsingFileList(bool usingFileList)
        {
            var usingFileListProperty = AccessTools.Property(DownloadConfigType, "UsingFileList");
            usingFileListProperty.SetValue(ConfigField.GetValue(null), usingFileList);
        }
        public static List<string> ContentDownloaderConfigGetFilesToDownload()
        {
            var config = ConfigField.GetValue(null);

            var filesToDownloadProperty = AccessTools.Property(DownloadConfigType, "FilesToDownload");
            var filesToDownload = filesToDownloadProperty.GetValue(config) as List<string>;
            if (filesToDownload is null)
            {
                filesToDownload = new List<string>();
                filesToDownloadProperty.SetValue(config, filesToDownload);
            }

            return filesToDownload;
        }
        public static List<Regex> ContentDownloaderConfigGetFilesToDownloadRegex()
        {
            var config = ConfigField.GetValue(null);

            var filesToDownloadRegexProperty = AccessTools.Property(DownloadConfigType, "FilesToDownloadRegex");
            var filesToDownloadRegex = filesToDownloadRegexProperty.GetValue(config) as List<Regex>;
            if (filesToDownloadRegex is null)
            {
                filesToDownloadRegex = new List<Regex>();
                filesToDownloadRegexProperty.SetValue(config, filesToDownloadRegex);
            }

            return filesToDownloadRegex;
        }

        public static Task ContentDownloaderDownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc )
        {
            return (Task) DownloadAppAsyncMethod.Invoke(null, new object?[] { appId, depotManifestIds, branch, os, arch, language, lv, isUgc })!;
        }
    }
}