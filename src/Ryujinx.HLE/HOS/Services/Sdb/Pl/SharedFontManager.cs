using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.FsSystem;
using LibHac.Tools.FsSystem.NcaUtils;
using Microsoft.IO;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.HLE.Exceptions;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Services.Sdb.Pl.Types;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.HLE.HOS.Services.Sdb.Pl
{
    class SharedFontManager
    {
        private const uint FontKey = 0x06186249;
        private const uint BFTTFMagic = 0x18029a7f;
        private const uint TtfSfntTrueType = 0x00010000;
        private const uint TtfSfntCff = 0x4F54544F; // 'OTTO'
        private const uint TtfSfntTrue = 0x74727565; // 'true'
        private const uint TtfSfntTyp1 = 0x74797031; // 'typ1'

        // Host fallback when no system titles are installed (same layout as below).
        private static readonly string[] FallbackFontPaths =
        [
            "/usr/share/fonts/truetype/Carlito-Regular.ttf",
            "/usr/share/fonts/truetype/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/LiberationSans-Regular.ttf",
            "/usr/share/fonts/truetype/Roboto-Regular.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf",
        ];

        private sealed class MissingSystemFontException(string message) : InvalidSystemResourceException(message);

        private readonly Switch _device;
        private readonly SharedMemoryStorage _storage;

        internal struct FontInfo
        {
            public int Offset;
            public int Size;

            public FontInfo(int offset, int size)
            {
                Offset = offset;
                Size = size;
            }
        }

        private Dictionary<SharedFontType, FontInfo> _fontData;

        // Plain TTF copies after the BFTTF data (Switch 2 GetBuiltInFontData).
        private Dictionary<SharedFontType, FontInfo> _builtInFontData;

        public SharedFontManager(Switch device, SharedMemoryStorage storage)
        {
            _device = device;
            _storage = storage;
        }

        public void Initialize()
        {
            _fontData?.Clear();
            _fontData = null;

        }

        public void EnsureInitialized(ContentManager contentManager)
        {
            try
            {
                EnsureSystemFonts(contentManager);
            }
            catch (MissingSystemFontException ex) when (
                _device.CompatibilityProfile.Generation == ConsoleGeneration.Switch2 &&
                _device.Configuration.MemoryConfiguration == MemoryConfiguration.MemoryConfiguration12GiB)
            {
                Logger.Warning?.Print(LogClass.ServiceFs, ex.Message);
                LoadFallbackFonts();
            }
        }

        private void EnsureSystemFonts(ContentManager contentManager)
        {
            if (_fontData == null)
            {
                _storage.ZeroFill();

                uint fontOffset = 0;

                FontInfo CreateFont(string name)
                {
                    if (contentManager.TryGetFontTitle(name, out ulong fontTitle) && contentManager.TryGetFontFilename(name, out string fontFilename))
                    {
                        string contentPath = contentManager.GetInstalledContentPath(fontTitle, StorageId.BuiltInSystem, NcaContentType.Data);
                        string fontPath = VirtualFileSystem.SwitchPathToSystemPath(contentPath);

                        if (!string.IsNullOrWhiteSpace(fontPath))
                        {
                            byte[] data;

                            using (IStorage ncaFileStream = new LocalStorage(fontPath, FileAccess.Read, FileMode.Open))
                            {
                                Nca nca = new(_device.System.KeySet, ncaFileStream);
                                IFileSystem romfs = nca.OpenFileSystem(NcaSectionType.Data, _device.System.FsIntegrityCheckLevel);

                                using UniqueRef<IFile> fontFile = new();

                                romfs.OpenFile(ref fontFile.Ref, ("/" + fontFilename).ToU8Span(), OpenMode.Read).ThrowIfFailure();

                                data = DecryptFont(fontFile.Get.AsStream());
                            }

                            FontInfo info = new((int)fontOffset, data.Length);

                            WriteMagicAndSize(fontOffset, data.Length);

                            fontOffset += 8;

                            uint start = fontOffset;

                            for (; fontOffset - start < data.Length; fontOffset++)
                            {
                                _storage.GetRef<byte>(fontOffset) = data[fontOffset - start];
                            }

                            return info;
                        }
                        else
                        {
                            if (!contentManager.TryGetSystemTitlesName(fontTitle, out string titleName))
                            {
                                titleName = "Unknown";
                            }

                            throw new MissingSystemFontException($"{titleName} ({fontTitle:x8}) system title not found! This font will not work, provide the system archive to fix this error.");
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown font \"{name}\"!");
                    }
                }

                _fontData = new Dictionary<SharedFontType, FontInfo>
                {
                    { SharedFontType.JapanUsEurope,       CreateFont("FontStandard")                  },
                    { SharedFontType.SimplifiedChinese,   CreateFont("FontChineseSimplified")         },
                    { SharedFontType.SimplifiedChineseEx, CreateFont("FontExtendedChineseSimplified") },
                    { SharedFontType.TraditionalChinese,  CreateFont("FontChineseTraditional")        },
                    { SharedFontType.Korean,              CreateFont("FontKorean")                    },
                    { SharedFontType.NintendoEx,          CreateFont("FontNintendoExtended")          },
                };

                if (fontOffset > Horizon.FontSize)
                {
                    throw new InvalidSystemResourceException("The sum of all fonts size exceed the shared memory size. " +
                                                             $"Please make sure that the fonts don't exceed {Horizon.FontSize} bytes in total. (actual size: {fontOffset} bytes).");
                }
            }
        }

        private void WriteMagicAndSize(ulong offset, int size)
        {
            const int Key = 0x49621806;

            int encryptedSize = BinaryPrimitives.ReverseEndianness(size ^ Key);

            _storage.GetRef<int>(offset + 0) = (int)BFTTFMagic;
            _storage.GetRef<int>(offset + 4) = encryptedSize;
        }

        public void EnsureBuiltInFonts(ContentManager contentManager)
        {
            if (_builtInFontData != null)
            {
                return;
            }

            try
            {
                EnsureSystemFonts(contentManager);
            }
            catch (Exception ex) when (ex is InvalidSystemResourceException || ex is ArgumentException)
            {
                LoadFallbackFonts();
            }

            AppendPlainTtfCopies();
        }

        public bool TryGetBuiltInFont(SharedFontType type, out FontInfo info)
        {
            EnsureBuiltInFonts(_device.System.ContentManager);

            return _builtInFontData.TryGetValue(type, out info);
        }

        public int GetBuiltInFontsCount()
        {
            EnsureBuiltInFonts(_device.System.ContentManager);

            return _builtInFontData.Count;
        }

        private void AppendPlainTtfCopies()
        {
            _builtInFontData = new Dictionary<SharedFontType, FontInfo>();

            ulong offset = 0;

            foreach (KeyValuePair<SharedFontType, FontInfo> entry in _fontData)
            {
                offset = Math.Max(offset, (ulong)(entry.Value.Offset + 8 + entry.Value.Size));
            }

            foreach (SharedFontType type in new[]
            {
                SharedFontType.JapanUsEurope,
                SharedFontType.SimplifiedChinese,
                SharedFontType.SimplifiedChineseEx,
                SharedFontType.TraditionalChinese,
                SharedFontType.Korean,
                SharedFontType.NintendoEx,
            })
            {
                FontInfo src = _fontData[type];

                offset = (offset + 0xFFF) & ~0xFFFUL;

                if (offset + (ulong)src.Size > (ulong)Horizon.FontSize)
                {
                    throw new InvalidSystemResourceException("The sum of all font size exceed the shared memory size.");
                }

                for (int i = 0; i < src.Size; i++)
                {
                    _storage.GetRef<byte>(offset + (ulong)i) = _storage.GetRef<byte>((ulong)(src.Offset + 8 + i));
                }

                _builtInFontData[type] = new FontInfo((int)offset, src.Size);
                offset += (ulong)src.Size;
            }
        }

        private void LoadFallbackFonts()
        {
            _storage.ZeroFill();

            byte[] ttf = null;
            string usedPath = null;

            foreach (string path in FallbackFontPaths)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    using FileStream stream = File.OpenRead(path);
                    byte[] candidate = ReadFallbackFont(stream);
                    if (candidate != null)
                    {
                        ttf = candidate;
                        usedPath = path;
                        break;
                    }

                    Logger.Warning?.Print(LogClass.ServiceFs, $"Host fallback font '{path}' has invalid size or format.");
                }
                catch (IOException)
                {
                    // Try the next candidate path.
                }
            }

            if (ttf == null)
            {
                ttf = ReadBundledFallbackFont();
                Logger.Info?.Print(LogClass.ServiceFs, "Using bundled fallback font 'DejaVu Sans' for shared fonts.");
            }
            else
            {
                Logger.Info?.Print(LogClass.ServiceFs, $"Using host fallback font '{usedPath}' for shared fonts.");
            }

            _fontData = new Dictionary<SharedFontType, FontInfo>();
            ulong fontOffset = 0;

            foreach (SharedFontType type in new[]
            {
                SharedFontType.JapanUsEurope,
                SharedFontType.SimplifiedChinese,
                SharedFontType.SimplifiedChineseEx,
                SharedFontType.TraditionalChinese,
                SharedFontType.Korean,
                SharedFontType.NintendoEx,
            })
            {
                WriteMagicAndSize(fontOffset, ttf.Length);

                fontOffset += 8;

                for (int i = 0; i < ttf.Length; i++)
                {
                    _storage.GetRef<byte>(fontOffset + (ulong)i) = ttf[i];
                }

                _fontData[type] = new FontInfo((int)fontOffset - 8, ttf.Length);
                fontOffset += (ulong)ttf.Length;

                if (fontOffset > (ulong)Horizon.FontSize)
                {
                    throw new InvalidSystemResourceException("The sum of all font size exceed the shared memory size.");
                }
            }

            _builtInFontData = null;
        }

        internal static byte[] ReadBundledFallbackFont()
        {
            const string resourceName = "Resources.DejaVuSans.ttf";
            using Stream stream = typeof(SharedFontManager).Assembly.GetManifestResourceStream(typeof(SharedFontManager), resourceName);

            return (stream == null ? null : ReadFallbackFont(stream)) ??
                throw new InvalidSystemResourceException($"Bundled shared font '{resourceName}' is missing or invalid.");
        }

        private static byte[] ReadFallbackFont(Stream stream)
        {
            // Shared memory reserves a header and one copy for each font type.
            const int maxFontBytes = Horizon.FontSize / (int)SharedFontType.Count - 8;
            if (stream.Length <= sizeof(uint) || stream.Length > maxFontBytes)
            {
                return null;
            }

            // The shared-memory limit fits in int and bounds the allocation.
            byte[] data = new byte[(int)stream.Length];
            stream.ReadExactly(data);

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(data);

            return magic is TtfSfntTrueType or TtfSfntCff or TtfSfntTrue or TtfSfntTyp1 ? data : null;
        }

        public int GetFontSize(SharedFontType fontType)
        {
            EnsureInitialized(_device.System.ContentManager);

            return _fontData[fontType].Size;
        }

        public int GetSharedMemoryAddressOffset(SharedFontType fontType)
        {
            EnsureInitialized(_device.System.ContentManager);

            return _fontData[fontType].Offset + 8;
        }

        private byte[] DecryptFont(Stream bfttfStream)
        {
            static uint KXor(uint data) => data ^ FontKey;

            using BinaryReader reader = new(bfttfStream);
            using RecyclableMemoryStream ttfStream = MemoryStreamManager.Shared.GetStream();
            using BinaryWriter output = new(ttfStream);

            if (KXor(reader.ReadUInt32()) != BFTTFMagic)
            {
                throw new InvalidDataException("Error: Input file is not in BFTTF format!");
            }

            bfttfStream.Position += 4;

            for (int i = 0; i < (bfttfStream.Length - 8) / 4; i++)
            {
                output.Write(KXor(reader.ReadUInt32()));
            }

            return ttfStream.ToArray();
        }
    }
}
