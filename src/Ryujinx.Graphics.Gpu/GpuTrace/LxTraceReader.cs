// SPDX-License-Identifier: MIT
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ZstdSharp;

namespace Ryujinx.Graphics.Gpu.GpuTrace
{
    internal enum LxRecordType : uint
    {
        Config = 1,
        AddressSpace = 2,
        GmmuMap = 3,
        GmmuUnmap = 4,
        SmmuMap = 5,
        SmmuUnmap = 6,
        Channel = 7,
        State = 8,
        Blob = 9,
        Memory = 10,
        SnapshotEnd = 11,
        Submit = 12,
        Present = 13,
        Resume = 14,
        End = 15,
    }

    internal readonly record struct LxConfig(string ProgramId, bool Switch2, uint Width, uint Height);

    internal readonly record struct LxSubmit(
        int Channel,
        ulong[] Entries,
        uint[] Prefetch,
        uint[] Offsets,
        uint[] Words,
        uint[] Deferred);

    internal readonly record struct LxPresentLayer(
        ulong Address,
        uint Offset,
        uint Width,
        uint Height,
        uint Stride,
        uint PixelFormat,
        uint TransformFlags,
        int CropLeft,
        int CropTop,
        int CropRight,
        int CropBottom,
        uint Blending,
        uint NativeTiling,
        uint LayerStackMask,
        uint ColorSpace,
        uint LinearUnitLuminance);

    internal readonly record struct LxPresentFence(int Id, uint Value);

    internal readonly record struct LxPresent(ulong TimeNs, LxPresentLayer[] Layers, LxPresentFence[] Fences);

    internal readonly record struct LxMemory(ulong PhysicalAddress, ulong Size, ulong HashLow, ulong HashHigh);

    internal readonly record struct LxState(uint Kind, int Channel, byte[] Data);

    internal readonly record struct LxRecord(
        LxRecordType Type,
        byte[] Payload,
        LxConfig? Config = null,
        LxSubmit? Submit = null,
        LxPresent? Present = null,
        LxMemory? Memory = null,
        LxState? State = null);

    /// <summary>
    /// Streams a Lichii .lxtrace: a 64-byte header followed by one zstd stream of length-prefixed records.
    /// Records are single-pass; <see cref="Config"/> is populated when its record is read.
    /// </summary>
    internal sealed class LxTraceReader : IDisposable
    {
        private const int FileHeaderSize = 64;
        private const int RecordHeaderSize = 16;
        private const int SubmitHeaderSize = 32;
        private const int PresentHeaderSize = 16;
        private const int PresentLayerSize = 72;
        private const int PresentFenceSize = 8;
        private const int MemoryRecordSize = 32;
        private const uint FormatVersion = 2;

        private static ReadOnlySpan<byte> Magic => "LXGTRACE"u8;

        private readonly FileStream _file;
        private readonly DecompressionStream _stream;

        public uint Version { get; }
        public uint HeaderSize { get; }
        public LxConfig Config { get; private set; }

        public LxTraceReader(string path)
        {
            _file = File.OpenRead(path);
            try
            {
                Span<byte> header = stackalloc byte[FileHeaderSize];
                _file.ReadExactly(header);

                if (!header[..8].SequenceEqual(Magic))
                {
                    throw new InvalidDataException("Bad lxtrace magic.");
                }

                Version = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
                HeaderSize = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);

                if (Version != FormatVersion)
                {
                    throw new InvalidDataException($"Unsupported lxtrace version {Version}.");
                }

                if (HeaderSize != FileHeaderSize)
                {
                    throw new InvalidDataException($"Unsupported lxtrace header size {HeaderSize}.");
                }

                _stream = new DecompressionStream(_file);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _file.Dispose();
        }

        public IEnumerable<LxRecord> ReadRecords()
        {
            while (TryReadRecordHeader(out LxRecordType type, out ulong size))
            {
                if (size > int.MaxValue)
                {
                    throw new InvalidDataException($"lxtrace record {type} is too large: {size}.");
                }

                byte[] payload = new byte[(int)size];
                _stream.ReadExactly(payload);

                yield return ParseRecord(type, payload);

                if (type == LxRecordType.End)
                {
                    yield break;
                }
            }
        }

        private bool TryReadRecordHeader(out LxRecordType type, out ulong size)
        {
            Span<byte> header = stackalloc byte[RecordHeaderSize];
            if (_stream.Read(header[..1]) == 0)
            {
                type = default;
                size = default;
                return false;
            }

            _stream.ReadExactly(header[1..]);
            type = (LxRecordType)BinaryPrimitives.ReadUInt32LittleEndian(header);
            size = BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
            return true;
        }

        private LxRecord ParseRecord(LxRecordType type, byte[] payload)
        {
            switch (type)
            {
                case LxRecordType.Config:
                    Config = ParseConfig(payload);
                    return new LxRecord(type, payload, Config: Config);
                case LxRecordType.Submit:
                    return new LxRecord(type, payload, Submit: ParseSubmit(payload));
                case LxRecordType.Present:
                    return new LxRecord(type, payload, Present: ParsePresent(payload));
                case LxRecordType.Memory:
                    return new LxRecord(type, payload, Memory: ParseMemory(payload));
                case LxRecordType.State:
                    return new LxRecord(type, payload, State: ParseState(payload));
                case LxRecordType.AddressSpace:
                case LxRecordType.GmmuMap:
                case LxRecordType.GmmuUnmap:
                case LxRecordType.SmmuMap:
                case LxRecordType.SmmuUnmap:
                case LxRecordType.Channel:
                case LxRecordType.Blob:
                case LxRecordType.SnapshotEnd:
                case LxRecordType.Resume:
                case LxRecordType.End:
                    return new LxRecord(type, payload);
                default:
                    throw new InvalidDataException($"Unknown lxtrace record type 0x{(uint)type:X}.");
            }
        }

        private static LxConfig ParseConfig(byte[] payload)
        {
            using JsonDocument json = JsonDocument.Parse(payload);
            JsonElement root = json.RootElement;
            return new LxConfig(
                root.GetProperty("program_id").GetString() ?? "",
                root.GetProperty("switch2").GetBoolean(),
                root.GetProperty("width").GetUInt32(),
                root.GetProperty("height").GetUInt32());
        }

        private static LxSubmit ParseSubmit(byte[] payload)
        {
            ReadOnlySpan<byte> span = payload;
            Require(span.Length >= SubmitHeaderSize, "Submit");
            int channel = BinaryPrimitives.ReadInt32LittleEndian(span);
            uint entryCount = ReadU32(span, 4);
            uint prefetchCount = ReadU32(span, 8);
            uint offsetCount = ReadU32(span, 12);
            ulong wordCount = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
            uint deferredCount = ReadU32(span, 24);

            int position = SubmitHeaderSize;
            ulong[] entries = ReadU64Array(span, ref position, entryCount);
            uint[] prefetch = ReadU32Array(span, ref position, prefetchCount);
            uint[] offsets = ReadU32Array(span, ref position, offsetCount);
            uint[] words = ReadU32Array(span, ref position, wordCount);
            uint[] deferred = ReadU32Array(span, ref position, deferredCount);
            return new LxSubmit(channel, entries, prefetch, offsets, words, deferred);
        }

        private static LxPresent ParsePresent(byte[] payload)
        {
            ReadOnlySpan<byte> span = payload;
            Require(span.Length >= PresentHeaderSize, "Present");
            ulong timeNs = BinaryPrimitives.ReadUInt64LittleEndian(span);
            uint layerCount = ReadU32(span, 8);
            uint fenceCount = ReadU32(span, 12);

            int position = PresentHeaderSize;
            LxPresentLayer[] layers = new LxPresentLayer[CheckedCount(span, position, layerCount, PresentLayerSize)];
            for (int index = 0; index < layers.Length; index++)
            {
                ReadOnlySpan<byte> layer = span.Slice(position, PresentLayerSize);
                layers[index] = new LxPresentLayer(
                    BinaryPrimitives.ReadUInt64LittleEndian(layer),
                    ReadU32(layer, 8),
                    ReadU32(layer, 12),
                    ReadU32(layer, 16),
                    ReadU32(layer, 20),
                    ReadU32(layer, 24),
                    ReadU32(layer, 28),
                    BinaryPrimitives.ReadInt32LittleEndian(layer[32..]),
                    BinaryPrimitives.ReadInt32LittleEndian(layer[36..]),
                    BinaryPrimitives.ReadInt32LittleEndian(layer[40..]),
                    BinaryPrimitives.ReadInt32LittleEndian(layer[44..]),
                    ReadU32(layer, 48),
                    ReadU32(layer, 52),
                    ReadU32(layer, 56),
                    ReadU32(layer, 60),
                    ReadU32(layer, 64));
                position += PresentLayerSize;
            }

            LxPresentFence[] fences = new LxPresentFence[CheckedCount(span, position, fenceCount, PresentFenceSize)];
            for (int index = 0; index < fences.Length; index++)
            {
                ReadOnlySpan<byte> fence = span.Slice(position, PresentFenceSize);
                fences[index] = new LxPresentFence(
                    BinaryPrimitives.ReadInt32LittleEndian(fence),
                    ReadU32(fence, 4));
                position += PresentFenceSize;
            }

            return new LxPresent(timeNs, layers, fences);
        }

        private static LxMemory ParseMemory(byte[] payload)
        {
            ReadOnlySpan<byte> span = payload;
            Require(span.Length >= MemoryRecordSize, "Memory");
            return new LxMemory(
                BinaryPrimitives.ReadUInt64LittleEndian(span),
                BinaryPrimitives.ReadUInt64LittleEndian(span[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span[16..]),
                BinaryPrimitives.ReadUInt64LittleEndian(span[24..]));
        }

        private static LxState ParseState(byte[] payload)
        {
            ReadOnlySpan<byte> span = payload;
            Require(span.Length >= 8, "State");
            return new LxState(
                ReadU32(span, 0),
                BinaryPrimitives.ReadInt32LittleEndian(span[4..]),
                span[8..].ToArray());
        }

        private static uint[] ReadU32Array(ReadOnlySpan<byte> span, ref int position, ulong count)
        {
            uint[] values = new uint[CheckedCount(span, position, count, 4)];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = ReadU32(span, position + index * 4);
            }

            position += values.Length * 4;
            return values;
        }

        private static ulong[] ReadU64Array(ReadOnlySpan<byte> span, ref int position, ulong count)
        {
            ulong[] values = new ulong[CheckedCount(span, position, count, 8)];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(position + index * 8, 8));
            }

            position += values.Length * 8;
            return values;
        }

        private static uint ReadU32(ReadOnlySpan<byte> span, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

        private static int CheckedCount(ReadOnlySpan<byte> span, int position, ulong count, ulong elementSize)
        {
            Require(count <= (ulong)(span.Length - position) / elementSize, "record payload");
            return (int)count;
        }

        private static void Require(bool condition, string record)
        {
            if (!condition)
            {
                throw new InvalidDataException($"Truncated lxtrace {record} payload.");
            }
        }
    }
}
