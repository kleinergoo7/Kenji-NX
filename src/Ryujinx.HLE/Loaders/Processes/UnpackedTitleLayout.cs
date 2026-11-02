// SPDX-License-Identifier: MIT
using System.IO;

namespace Ryujinx.HLE.Loaders.Processes
{
    public static class UnpackedTitleLayout
    {
        public const string ExeFsDirectoryName = "exefs";

        public static bool IsUnpackedTitleFolder(string rootPath)
        {
            return HasExecutables(rootPath) ||
                HasExecutables(Path.Combine(rootPath, ExeFsDirectoryName));
        }

        private static bool HasExecutables(string exeFsPath)
        {
            return File.Exists(Path.Combine(exeFsPath, "main")) &&
                File.Exists(Path.Combine(exeFsPath, "main.npdm"));
        }
    }
}
