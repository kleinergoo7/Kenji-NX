// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.GpuTrace;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ZstdSharp;

namespace Ryujinx.Tests.Graphics
{
    public class LxTraceReaderTests
    {
        private const uint ConfigType = 1;
        private const uint SubmitType = 12;
        private const uint PresentType = 13;
        private const uint EndType = 15;

        [Test]
        public void SyntheticTraceParsesConfigSubmitPresentAndEnd()
        {
            byte[] config = Encoding.UTF8.GetBytes("{\"height\":144,\"program_id\":\"0400000000000001\",\"switch2\":true,\"width\":256}");
            byte[] submit = BuildSubmit(3, [0x1122334455667788UL], [0xAA, 0xBB], [0x10], [0x1234, 0x5678], [0x99]);
            byte[] present = BuildPresent(1000, 1, 1);

            WithTrace(BuildTrace(Record(ConfigType, config), Record(SubmitType, submit), Record(PresentType, present), Record(EndType, [])), reader =>
            {
                Assert.That(reader.Version, Is.EqualTo(2));
                Assert.That(reader.HeaderSize, Is.EqualTo(64));

                List<LxRecord> records = reader.ReadRecords().ToList();

                Assert.That(reader.Config.ProgramId, Is.EqualTo("0400000000000001"));
                Assert.That(reader.Config.Switch2, Is.True);
                Assert.That(reader.Config.Width, Is.EqualTo(256));
                Assert.That(reader.Config.Height, Is.EqualTo(144));

                LxSubmit parsedSubmit = records.Single(r => r.Type == LxRecordType.Submit).Submit!.Value;
                Assert.That(parsedSubmit.Channel, Is.EqualTo(3));
                Assert.That(parsedSubmit.Entries, Is.EqualTo(new ulong[] { 0x1122334455667788 }));
                Assert.That(parsedSubmit.Prefetch, Is.EqualTo(new uint[] { 0xAA, 0xBB }));
                Assert.That(parsedSubmit.Offsets, Is.EqualTo(new uint[] { 0x10 }));
                Assert.That(parsedSubmit.Words, Is.EqualTo(new uint[] { 0x1234, 0x5678 }));
                Assert.That(parsedSubmit.Deferred, Is.EqualTo(new uint[] { 0x99 }));

                LxPresent parsedPresent = records.Single(r => r.Type == LxRecordType.Present).Present!.Value;
                Assert.That(parsedPresent.TimeNs, Is.EqualTo(1000));
                Assert.That(parsedPresent.Layers.Length, Is.EqualTo(1));
                Assert.That(parsedPresent.Layers[0].Address, Is.EqualTo(0x2b50fe00UL));
                Assert.That(parsedPresent.Layers[0].Width, Is.EqualTo(256));
                Assert.That(parsedPresent.Fences.Length, Is.EqualTo(1));
                Assert.That(parsedPresent.Fences[0].Id, Is.EqualTo(-7));
                Assert.That(parsedPresent.Fences[0].Value, Is.EqualTo(42));

                Assert.That(records[^1].Type, Is.EqualTo(LxRecordType.End));
            });
        }

        [Test]
        public void ConfigThrowsUntilEnumerationBegins()
        {
            byte[] config = Encoding.UTF8.GetBytes("{\"height\":144,\"program_id\":\"0400000000000001\",\"switch2\":true,\"width\":256}");

            WithTrace(BuildTrace(Record(ConfigType, config), Record(EndType, [])), reader =>
            {
                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => { _ = reader.Config; });
                Assert.That(exception!.Message, Does.Contain("ReadRecords()"));

                using IEnumerator<LxRecord> enumerator = reader.ReadRecords().GetEnumerator();
                Assert.That(enumerator.MoveNext(), Is.True);
                Assert.That(reader.Config.Switch2, Is.True);
                Assert.That(reader.Config.Width, Is.EqualTo(256));
            });
        }

        [Test]
        public void UnknownRecordTypeThrowsWithTypeId()
        {
            WithTrace(BuildTrace(Record(0x1234, [])), reader =>
            {
                InvalidDataException exception = Assert.Throws<InvalidDataException>(() => reader.ReadRecords().ToList());
                Assert.That(exception!.Message, Does.Contain("0x1234"));
            });
        }

        [Test]
        public void RealSampleTraceParsesConfigAndSubmit()
        {
            const string path = "/tmp/opencode/sample-trace/sample.lxtrace";
            if (!File.Exists(path))
            {
                Assert.Ignore("sample trace not present");
            }

            using LxTraceReader reader = new(path);
            List<LxRecord> records = reader.ReadRecords().ToList();

            Assert.That(reader.Config.Switch2, Is.True);
            Assert.That(reader.Config.ProgramId, Is.EqualTo("0400000000000001"));
            Assert.That(reader.Config.Width, Is.EqualTo(256));
            Assert.That(reader.Config.Height, Is.EqualTo(144));
            Assert.That(records.Count(r => r.Type == LxRecordType.Submit), Is.GreaterThanOrEqualTo(1));
        }

        private static void WithTrace(byte[] trace, Action<LxTraceReader> action)
        {
            string path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, trace);
                using LxTraceReader reader = new(path);
                action(reader);
            }
            finally
            {
                File.Delete(path);
            }
        }

        private static byte[] BuildTrace(params byte[][] records)
        {
            using MemoryStream plain = new();
            foreach (byte[] record in records)
            {
                plain.Write(record);
            }

            using MemoryStream compressed = new();
            using (CompressionStream zstd = new(compressed, level: 3, bufferSize: 0, leaveOpen: true))
            {
                zstd.Write(plain.ToArray());
            }

            byte[] zstdBytes = compressed.ToArray();
            byte[] trace = new byte[64 + zstdBytes.Length];
            "LXGTRACE"u8.CopyTo(trace);
            BinaryPrimitives.WriteUInt32LittleEndian(trace.AsSpan(8), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(trace.AsSpan(12), 64);
            zstdBytes.CopyTo(trace, 64);
            return trace;
        }

        private static byte[] Record(uint type, byte[] payload)
        {
            byte[] record = new byte[16 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(record, type);
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), (ulong)payload.Length);
            payload.CopyTo(record, 16);
            return record;
        }

        private static byte[] BuildSubmit(int channel, ulong[] entries, uint[] prefetch, uint[] offsets, uint[] words, uint[] deferred)
        {
            using MemoryStream stream = new();
            WriteI32(stream, channel);
            WriteU32(stream, (uint)entries.Length);
            WriteU32(stream, (uint)prefetch.Length);
            WriteU32(stream, (uint)offsets.Length);
            WriteU64(stream, (ulong)words.Length);
            WriteU32(stream, (uint)deferred.Length);
            WriteU32(stream, 0);
            foreach (ulong value in entries) WriteU64(stream, value);
            foreach (uint value in prefetch) WriteU32(stream, value);
            foreach (uint value in offsets) WriteU32(stream, value);
            foreach (uint value in words) WriteU32(stream, value);
            foreach (uint value in deferred) WriteU32(stream, value);
            return stream.ToArray();
        }

        private static byte[] BuildPresent(ulong timeNs, int layerCount, int fenceCount)
        {
            using MemoryStream stream = new();
            WriteU64(stream, timeNs);
            WriteU32(stream, (uint)layerCount);
            WriteU32(stream, (uint)fenceCount);
            for (int index = 0; index < layerCount; index++)
            {
                WriteU64(stream, 0x2b50fe00);
                WriteU32(stream, 0);
                WriteU32(stream, 256);
                WriteU32(stream, 144);
                WriteU32(stream, 1024);
                WriteU32(stream, 0);
                WriteU32(stream, 0);
                WriteI32(stream, 0);
                WriteI32(stream, 0);
                WriteI32(stream, 256);
                WriteI32(stream, 144);
                WriteU32(stream, 0);
                WriteU32(stream, 0);
                WriteU32(stream, 0);
                WriteU32(stream, 0);
                WriteU32(stream, 0);
                WriteU32(stream, 0);
            }

            for (int index = 0; index < fenceCount; index++)
            {
                WriteI32(stream, -7);
                WriteU32(stream, 42);
            }

            return stream.ToArray();
        }

        private static void WriteU32(Stream stream, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            stream.Write(bytes);
        }

        private static void WriteI32(Stream stream, int value) => WriteU32(stream, unchecked((uint)value));

        private static void WriteU64(Stream stream, ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            stream.Write(bytes);
        }
    }
}
