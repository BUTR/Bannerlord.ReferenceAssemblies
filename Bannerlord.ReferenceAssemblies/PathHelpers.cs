using PCLExt.FileStorage;

using System.Collections.Generic;
using System.Linq;

namespace Bannerlord.ReferenceAssemblies
{
    internal static class PathHelpers
    {
        public static IFolder GetModuleFolder(this IFolder folder, string module, bool isCore = true) => isCore
            ? folder
            : folder.CreateFolder("Modules", CreationCollisionOption.OpenIfExists).CreateFolder(module, CreationCollisionOption.OpenIfExists);

        public static IEnumerable<IFile> GetModuleFiles(this IFolder folder, bool isCore = true) => isCore
            ? folder.GetFiles("TaleWorlds.*.dll").Concat(folder.GetFiles("*.exe"))
            : folder.GetFiles();
    }
}