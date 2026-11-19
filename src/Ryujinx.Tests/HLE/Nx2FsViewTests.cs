// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Fs;

namespace Ryujinx.Tests.HLE
{
    public class Nx2FsViewTests
    {
        private const ulong AppPid = 109;

        [SetUp]
        public void SetUp()
        {
            FsLoaderProgress.ResetFsView();
            FsLoaderProgress.SetFsViewPid(AppPid);
        }

        [Test]
        public void ContentOpenFailAndReadCounters()
        {
            FsLoaderProgress.OnFsOpen(AppPid, "/romfs/data/asset.brtan");
            FsLoaderProgress.OnFsOpenFail(AppPid, "/romfs/data/missing.brtan");
            FsLoaderProgress.OnFsRead(AppPid, 4096);

            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("contentOpens=1"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("contentFails=1"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("reads=1"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("bytes=4096"));
        }

        [Test]
        public void DescribeFsViewRendersSummary()
        {
            FsLoaderProgress.OnFsOpen(AppPid, "/romfs/data/asset.brtan");
            FsLoaderProgress.OnFsOpenFail(AppPid, "/romfs/data/missing.brtan");
            FsLoaderProgress.OnFsRead(AppPid, 4096);

            Assert.That(FsLoaderProgress.DescribeFsView(),
                Does.Contain("contentOpens=1 contentFails=1 reads=1 bytes=4096"));
        }

        [Test]
        public void SaveBinIsNotCountedAsContent()
        {
            FsLoaderProgress.OnFsOpen(AppPid, "/save.bin");
            FsLoaderProgress.OnFsOpenFail(AppPid, "/save.bin");

            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("contentOpens=0"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("contentFails=0"));
        }

        [Test]
        public void NonApplicationPidIsExcluded()
        {
            FsLoaderProgress.OnFsOpen(999, "/romfs/data/asset.brtan");
            FsLoaderProgress.OnFsOpenFail(999, "/romfs/data/missing.brtan");
            FsLoaderProgress.OnFsRead(999, 4096);

            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("opens=0"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("contentOpens=0"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("contentFails=0"));
            Assert.That(FsLoaderProgress.DescribeFsView(), Does.Contain("reads=0"));
        }
    }
}
