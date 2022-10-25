using DepotDownloader;

using HarmonyLib;
using HarmonyLib.BUTR.Extensions;

using SteamKit2;

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    public static class DepotDownloaderExt
    {
        private delegate void ShutdownSteam3Delegate();

        private delegate void LoadFromFileDelegate(string filename);

        private delegate bool InitializeSteamDelegate(string username, string password);

        private delegate void RequestAppInfoDelegate(object instance, uint appId, bool bForce = false);

        private delegate KeyValue GetSteam3AppSectionDelegate(uint appId, EAppInfoSection section);

        private delegate Task DownloadAppAsyncDelegate(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds,
            string branch, string os, string arch, string language, bool lv, bool isUgc);




        private static Type ContentDownloaderType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.ContentDownloader")!;
        private static Type DownloadConfigType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.DownloadConfig")!;
        private static Type DepotConfigStoreType { get; } =
            typeof(ContentDownloaderException).Assembly.GetType("DepotDownloader.DepotConfigStore")!;

        private static ShutdownSteam3Delegate? ShutdownSteam3Method { get; } =
            AccessTools2.GetDelegate<ShutdownSteam3Delegate>("DepotDownloader.ContentDownloader:ShutdownSteam3");
        private static LoadFromFileDelegate? LoadFromFileMethod { get; } =
            AccessTools2.GetDelegate<LoadFromFileDelegate>("DepotDownloader.AccountSettingsStore:LoadFromFile");
        private static InitializeSteamDelegate? InitializeSteamMethod { get; } =
            AccessTools2.GetDelegate<InitializeSteamDelegate>("DepotDownloader.Program:InitializeSteam");
        private static RequestAppInfoDelegate? RequestAppInfoMethod { get; } =
            AccessTools2.GetDelegate<RequestAppInfoDelegate>("DepotDownloader.Steam3Session:RequestAppInfo");
        private static GetSteam3AppSectionDelegate? GetSteam3AppSectionMethod { get; } =
            AccessTools2.GetDelegate<GetSteam3AppSectionDelegate>("DepotDownloader.ContentDownloader:GetSteam3AppSection");
        private static DownloadAppAsyncDelegate? DownloadAppAsyncMethod { get; } =
            AccessTools2.GetDelegate<DownloadAppAsyncDelegate>("DepotDownloader.ContentDownloader:DownloadAppAsync");

        private static FieldInfo Steam3Field { get; } = AccessTools.Field(ContentDownloaderType, "steam3");
        private static FieldInfo ConfigField { get; } = AccessTools.Field(ContentDownloaderType, "Config");

        private static PropertyInfo LoadedProperty { get; } = AccessTools.Property(DepotConfigStoreType, "Loaded");
        private static PropertyInfo MaxDownloadsProperty { get; } = AccessTools.Property(DownloadConfigType, "MaxDownloads");
        private static PropertyInfo InstallDirectoryProperty { get; } = AccessTools.Property(DownloadConfigType, "InstallDirectory");
        private static PropertyInfo UsingFileListProperty { get; } = AccessTools.Property(DownloadConfigType, "UsingFileList");
        private static PropertyInfo FilesToDownloadProperty { get; } = AccessTools.Property(DownloadConfigType, "FilesToDownload");
        private static PropertyInfo FilesToDownloadRegexProperty { get; } = AccessTools.Property(DownloadConfigType, "FilesToDownloadRegex");


        public static void Init()
        {
            var harmony = new Harmony("123");

            Assembly.Load("DepotDownloader");

            harmony.Patch(
                AccessTools2.Method("DepotDownloader.ContentDownloader:DownloadAppAsync"),
                transpiler: new HarmonyMethod(AccessTools2.Method(typeof(DepotDownloaderExt), nameof(BlankTranspiler))));

            harmony.Patch(
                AccessTools2.Method(DepotConfigStoreType, "LoadFromFile"),
                prefix: new HarmonyMethod(AccessTools2.Method(typeof(DepotDownloaderExt), nameof(LoadFromFilePrefix))));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LoadFromFilePrefix()
        {
            Console.WriteLine("Test123");
            var isLoaded = (bool) LoadedProperty.GetValue(null)!;
            return !isLoaded;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<CodeInstruction> BlankTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;


        public static void ContentDownloaderShutdownSteam3() => ShutdownSteam3Method();
        public static void AccountSettingsStoreLoadFromFile(string file)
        {
            var isLoaded = (bool) LoadedProperty.GetValue(null)!;
            if (!isLoaded)
                LoadFromFileMethod(file);
        }

        public static void DepotDownloaderProgramInitializeSteam(string login, string password) => InitializeSteamMethod(login, password);
        public static void ContentDownloadersteam3RequestAppInfo(uint appId) => RequestAppInfoMethod(Steam3Field.GetValue(null), appId, false);
        public static KeyValue ContentDownloaderGetSteam3AppSection(uint appId) => GetSteam3AppSectionMethod(appId, EAppInfoSection.Depots);

        public static void ContentDownloaderConfigSetMaxDownloads(int maxDownloads) => MaxDownloadsProperty.SetValue(ConfigField.GetValue(null), maxDownloads);
        public static void ContentDownloaderConfigSetInstallDirectory(string installDirectory) => InstallDirectoryProperty.SetValue(ConfigField.GetValue(null), installDirectory);
        public static void ContentDownloaderConfigSetUsingFileList(bool usingFileList) => UsingFileListProperty.SetValue(ConfigField.GetValue(null), usingFileList);
        public static HashSet<string> ContentDownloaderConfigGetFilesToDownload()
        {
            var config = ConfigField.GetValue(null);

            var filesToDownload = FilesToDownloadProperty.GetValue(config) as HashSet<string>;
            if (filesToDownload is null)
            {
                filesToDownload = new HashSet<string>();
                FilesToDownloadProperty.SetValue(config, filesToDownload);
            }

            return filesToDownload;
        }
        public static List<Regex> ContentDownloaderConfigGetFilesToDownloadRegex()
        {
            var config = ConfigField.GetValue(null);

            var filesToDownloadRegex = FilesToDownloadRegexProperty.GetValue(config) as List<Regex>;
            if (filesToDownloadRegex is null)
            {
                filesToDownloadRegex = new List<Regex>();
                FilesToDownloadRegexProperty.SetValue(config, filesToDownloadRegex);
            }

            return filesToDownloadRegex;
        }

        public static Task ContentDownloaderDownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc)
        {
            return DownloadAppAsyncMethod(appId, depotManifestIds, branch, os, arch, language, lv, isUgc)!;
        }
    }
}