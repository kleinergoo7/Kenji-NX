using LibHac;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.Loader;
using LibHac.Util;
using Ryujinx.Common;
using Ryujinx.Common.Logging;
using System;
using System.Buffers.Binary;

namespace Ryujinx.HLE.Loaders.Processes.Extensions
{
    public static class MetaLoaderExtensions
    {
        private const int MetaSize = 0x80;
        private const int AcidOffsetField = 0x78;
        private const int AcidSizeField = 0x7C;
        private const int AcidSignatureSwitch1 = 0x200;
        private const int AcidSignatureSwitch2 = 0x300;
        private const int AcidBodySize = 0x40;
        private const int AcidFsAccessControlOffset = 0x20;
        private const int AcidServiceAccessControlOffset = 0x28;
        private const int AcidKernelCapabilityOffset = 0x30;
        private const int MaxNpdmSize = 0x8000;

        public static ulong GetProgramId(this MetaLoader metaLoader)
        {
            metaLoader.GetNpdm(out LibHac.Loader.Npdm npdm).ThrowIfFailure();

            return npdm.Aci.ProgramId.Value;
        }

        public static string GetProgramName(this MetaLoader metaLoader)
        {
            metaLoader.GetNpdm(out LibHac.Loader.Npdm npdm).ThrowIfFailure();

            return StringUtils.Utf8ZToString(npdm.Meta.ProgramName);
        }

        public static bool IsProgram64Bit(this MetaLoader metaLoader)
        {
            metaLoader.GetNpdm(out LibHac.Loader.Npdm npdm).ThrowIfFailure();

            return (npdm.Meta.Flags & 1) != 0;
        }

        public static void LoadDefault(this MetaLoader metaLoader)
        {
            byte[] npdmBuffer = EmbeddedResources.Read("Ryujinx.HLE/Homebrew.npdm");

            metaLoader.Load(npdmBuffer).ThrowIfFailure();
        }

        public static void LoadFromFile(this MetaLoader metaLoader, IFileSystem fileSystem, string path = "")
        {
            if (string.IsNullOrEmpty(path))
            {
                path = ProcessConst.MainNpdmPath;
            }

            using UniqueRef<IFile> npdmFile = new();

            fileSystem.OpenFile(ref npdmFile.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();

            npdmFile.Get.GetSize(out long fileSize).ThrowIfFailure();

            Span<byte> npdmBuffer = new byte[fileSize];

            npdmFile.Get.Read(out _, 0, npdmBuffer).ThrowIfFailure();

            metaLoader.Load(npdmBuffer).ThrowIfFailure();
        }

        // Switch 2 NPDMs use a 0x300-byte ACID signature; LibHac expects 0x200.
        // Normalize it before loading so the title's metadata is accepted.
        public static Result LoadFromFileCompat(this MetaLoader metaLoader, IFileSystem fileSystem, string path = ProcessConst.MainNpdmPath)
        {
            using UniqueRef<IFile> file = new();

            fileSystem.OpenFile(ref file.Ref, path.ToU8Span(), OpenMode.Read).ThrowIfFailure();
            file.Get.GetSize(out long size).ThrowIfFailure();

            if (size < 0 || size > MaxNpdmSize)
            {
                return metaLoader.LoadFromFile(fileSystem, path);
            }

            byte[] data = new byte[size];

            file.Get.Read(out _, 0, data).ThrowIfFailure();

            return metaLoader.LoadCompat(data);
        }

        public static Result LoadCompat(this MetaLoader metaLoader, byte[] data)
        {
            if (TryNormalizeSwitch2Acid(data, out byte[] normalized))
            {
                Logger.Info?.Print(LogClass.Loader, "NPDM uses the Switch 2 ACID layout");

                data = normalized;
            }

            return metaLoader.Load(data);
        }

        private static bool TryNormalizeSwitch2Acid(byte[] data, out byte[] normalized)
        {
            normalized = null;

            if (data.Length < MetaSize || !data.AsSpan(0, 4).SequenceEqual("META"u8))
            {
                return false;
            }

            int acidOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(AcidOffsetField));
            int acidSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(AcidSizeField));

            if (acidOffset < MetaSize || acidSize < AcidSignatureSwitch2 + AcidBodySize ||
                (long)acidOffset + acidSize > data.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> acid = data.AsSpan(acidOffset, acidSize);

            if (!acid.Slice(AcidSignatureSwitch2, 4).SequenceEqual("ACID"u8))
            {
                return false;
            }

            int newAcidSize = acidSize - (AcidSignatureSwitch2 - AcidSignatureSwitch1);
            byte[] result = (byte[])data.Clone();

            acid.Slice(AcidSignatureSwitch2).CopyTo(result.AsSpan(acidOffset + AcidSignatureSwitch1));

            int bodyOffset = acidOffset + AcidSignatureSwitch1;
            int[] offsetFields = [AcidFsAccessControlOffset, AcidServiceAccessControlOffset, AcidKernelCapabilityOffset];

            foreach (int field in offsetFields)
            {
                int value = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(bodyOffset + field));

                if (value >= AcidSignatureSwitch2 - AcidSignatureSwitch1)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(bodyOffset + field), value - (AcidSignatureSwitch2 - AcidSignatureSwitch1));
                }
            }

            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(AcidSizeField), newAcidSize);
            normalized = result;

            return true;
        }
    }
}
