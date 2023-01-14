using DepotDownloader;

using HarmonyLib;
using HarmonyLib.BUTR.Extensions;

using SteamKit2;

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bannerlord.ReferenceAssemblies
{
    public static class DepotDownloaderExt
    {
        public static void Init()
        {
            var harmony = new Harmony("123");

            Assembly.Load("DepotDownloader");

            harmony.Patch(
                AccessTools2.Method("DepotDownloader.ContentDownloader:DownloadAppAsync"),
                transpiler: new HarmonyMethod(AccessTools2.Method(typeof(DepotDownloaderExt), nameof(BlankTranspiler))));

            harmony.Patch(
                AccessTools2.Method("DepotDownloader.DepotConfigStore:LoadFromFile"),
                prefix: new HarmonyMethod(AccessTools2.Method(typeof(DepotDownloaderExt), nameof(LoadFromFilePrefix))));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool LoadFromFilePrefix()
        {
            var isLoaded = DepotConfigStore.Loaded;
            return !isLoaded;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static IEnumerable<CodeInstruction> BlankTranspiler(IEnumerable<CodeInstruction> instructions) => instructions;


        public static void ContentDownloaderShutdownSteam3() => ContentDownloader.ShutdownSteam3();
        public static void AccountSettingsStoreLoadFromFile(string file)
        {
            var isLoaded = DepotConfigStore.Loaded;
            if (!isLoaded)
                AccountSettingsStore.LoadFromFile(file);
        }

        public static void DepotDownloaderProgramInitializeSteam(string login, string password) => DepotDownloader.Program.InitializeSteam(login, password);
        public static void ContentDownloadersteam3RequestAppInfo(uint appId) => ContentDownloader.steam3.RequestAppInfo(appId, false);
        public static KeyValue ContentDownloaderGetSteam3AppSection(uint appId) => ContentDownloader.GetSteam3AppSection(appId, EAppInfoSection.Depots);

        public static void ContentDownloaderConfigSetMaxDownloads(int maxDownloads) => ContentDownloader.Config.MaxDownloads = maxDownloads;
        public static void ContentDownloaderConfigSetInstallDirectory(string installDirectory) => ContentDownloader.Config.InstallDirectory = installDirectory;
        public static void ContentDownloaderConfigSetUsingFileList(bool usingFileList) => ContentDownloader.Config.UsingFileList = usingFileList;
        public static HashSet<string> ContentDownloaderConfigGetFilesToDownload()
        {
            var filesToDownload = ContentDownloader.Config.FilesToDownload;
            if (filesToDownload is null)
            {
                filesToDownload = new HashSet<string>();
                ContentDownloader.Config.FilesToDownload = filesToDownload;
            }

            return filesToDownload;
        }
        public static List<Regex> ContentDownloaderConfigGetFilesToDownloadRegex()
        {
            var filesToDownloadRegex = ContentDownloader.Config.FilesToDownloadRegex;
            if (filesToDownloadRegex is null)
            {
                filesToDownloadRegex = new List<Regex>();
                ContentDownloader.Config.FilesToDownloadRegex = filesToDownloadRegex;
            }

            return filesToDownloadRegex;
        }

        public static Task ContentDownloaderDownloadAppAsync(uint appId, List<(uint depotId, ulong manifestId)> depotManifestIds, string branch, string os, string arch, string language, bool lv, bool isUgc) =>
            ContentDownloader.DownloadAppAsync(appId, depotManifestIds, branch, os, arch, language, lv, isUgc)!;
    }
}