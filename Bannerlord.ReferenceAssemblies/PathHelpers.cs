using System.Collections.Generic;
using PCLExt.FileStorage;

namespace Bannerlord.ReferenceAssemblies
{

    internal static class PathHelpers
    {

        public static IFolder GetModuleFolder(this IFolder folder, string module, bool isCore = true)
            => isCore ? folder : folder.CreateFolder("Modules", CreationCollisionOption.OpenIfExists).CreateFolder(module, CreationCollisionOption.OpenIfExists);

        public static IList<IFile> GetModuleFiles(this IFolder folder, bool isCore = true)
            => isCore ? folder.GetFiles("TaleWorlds.*") : folder.GetFiles();

    }

}