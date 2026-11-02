// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.Loaders.Processes;
using System;
using System.IO;

namespace Ryujinx.Tests.HLE
{
    public class UnpackedTitleLayoutTests
    {
        private static string CreateTitleFolder(bool withExeFsDir, bool withNpdm)
        {
            string root = Path.Combine(Path.GetTempPath(), "UnpackedTitleLayoutTests", Guid.NewGuid().ToString("N"));

            string exeFs = withExeFsDir ? Path.Combine(root, "exefs") : root;

            Directory.CreateDirectory(exeFs);
            File.WriteAllBytes(Path.Combine(exeFs, "main"), new byte[16]);

            if (withNpdm)
            {
                File.WriteAllBytes(Path.Combine(exeFs, "main.npdm"), new byte[16]);
            }

            return root;
        }

        [Test]
        public void RootMainAndNpdmIsTitleFolder()
        {
            string root = CreateTitleFolder(withExeFsDir: false, withNpdm: true);

            try
            {
                Assert.That(UnpackedTitleLayout.IsUnpackedTitleFolder(root), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void ExeFsSubfolderMainAndNpdmIsTitleFolder()
        {
            string root = CreateTitleFolder(withExeFsDir: true, withNpdm: true);

            try
            {
                Assert.That(UnpackedTitleLayout.IsUnpackedTitleFolder(root), Is.True);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void MissingNpdmIsNotTitleFolder()
        {
            string root = CreateTitleFolder(withExeFsDir: true, withNpdm: false);

            try
            {
                Assert.That(UnpackedTitleLayout.IsUnpackedTitleFolder(root), Is.False);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void EmptyFolderIsNotTitleFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "UnpackedTitleLayoutTests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(root);

            try
            {
                Assert.That(UnpackedTitleLayout.IsUnpackedTitleFolder(root), Is.False);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
