// SPDX-License-Identifier: MIT
using LibHac;
using LibHac.Account;
using LibHac.Common;
using LibHac.Common.Keys;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Fs.Shim;
using LibHac.FsSrv;
using LibHac.FsSrv.Impl;
using LibHac.FsSystem;
using LibHac.Loader;
using LibHac.Ncm;
using LibHac.Ns;
using LibHac.Tools.Fs;
using NUnit.Framework;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.Loaders.Processes;
using System;
using ApplicationId = LibHac.Ncm.ApplicationId;

namespace Ryujinx.Tests.HLE
{
    public class NupTemporarySaveDataTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void FallbackControlProvisionsTemporaryStorageOnlyWhenRequested(bool includeTemporaryStorage)
        {
            using InMemoryFileSystem baseFileSystem = new();
            LibHac.Horizon horizon = new(new HorizonConfiguration());
            HorizonClient serverClient = horizon.CreatePrivilegedHorizonClient();
            FileSystemServer server = new(serverClient);
            KeySet keys = KeySet.CreateDefaultKeySet();
            RandomDataGenerator random = Random.Shared.NextBytes;
            DefaultFsServerObjects objects = DefaultFsServerObjects.GetDefaultEmulatedCreators(baseFileSystem, keys, server, random);
            objects.FsCreators.EncryptedFileSystemCreator = new EncryptedFileSystemCreator();

            FileSystemServerInitializer.InitializeWithConfig(serverClient, server, new FileSystemServerConfig
            {
                ExternalKeySet = keys.ExternalKeySet,
                FsCreators = objects.FsCreators,
                StorageDeviceManagerFactory = objects.StorageDeviceManagerFactory,
                RandomGenerator = random,
            });

            ApplicationId applicationId = new(0x0100000000001000);
            ApplicationControlProperty control = ProcessLoaderHelper.CreateFallbackSaveDataControl(applicationId, includeTemporaryStorage);
            Uid user = new(1, 0);

            serverClient.Fs.EnsureApplicationSaveData(out _, applicationId, in control, in user).ThrowIfFailure();

            HorizonClient appClient = horizon.CreateHorizonClient(
                new ProgramLocation(new ProgramId(applicationId.Value), StorageId.BuiltInUser), AccessControlBits.Bits.ApplicationInfo);

            Result mount = appClient.Fs.MountTemporaryStorage("savt".ToU8Span());

            if (includeTemporaryStorage)
            {
                Assert.That(mount.IsSuccess(), Is.True, mount.ToStringWithName());
                appClient.Fs.Unmount("savt".ToU8Span());
            }
            else
            {
                Assert.That(ResultFs.TargetNotFound.Includes(mount), Is.True, mount.ToStringWithName());
            }
        }
    }
}
