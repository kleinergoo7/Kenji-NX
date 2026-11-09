// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel;
using Ryujinx.HLE.HOS.Services.Nv.NvDrvServices.NvHostChannel.Types;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ryujinx.Tests.HLE.Nv
{
    public class SubmitGpfifoTests
    {
        [Test]
        public void SubmitGpfifoWritesFenceAndFlagsToIoctlBuffer()
        {
            const int headerSize = 0x18;
            Assert.AreEqual(headerSize, Unsafe.SizeOf<SubmitGpfifoArguments>());
            Span<byte> arguments = stackalloc byte[headerSize + 2 * sizeof(ulong)];
            arguments.Clear();

            ref SubmitGpfifoArguments header = ref MemoryMarshal.AsRef<SubmitGpfifoArguments>(arguments);
            header.NumEntries = 1;
            header.Flags = SubmitGpfifoFlags.FenceIncrement;
            MemoryMarshal.Cast<byte, ulong>(arguments[headerSize..])[0] = 0x00000C0000400000UL;

            NvInternalResult result = NvHostChannelDeviceFile.SubmitGpfifo(arguments, static (ref SubmitGpfifoArguments submission, Span<ulong> entries) =>
            {
                Assert.AreEqual(1, entries.Length);
                Assert.AreEqual(0x00000C0000400000UL, entries[0]);
                Assert.AreEqual(SubmitGpfifoFlags.FenceIncrement, submission.Flags);
                submission.Fence.Id = 7;
                submission.Fence.Value = 18;
                submission.Flags = SubmitGpfifoFlags.None;

                return NvInternalResult.Success;
            });

            Assert.AreEqual(NvInternalResult.Success, result);
            Assert.AreEqual(7u, header.Fence.Id);
            Assert.AreEqual(18u, header.Fence.Value);
            Assert.AreEqual(SubmitGpfifoFlags.None, header.Flags);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SubmitGpfifoOnlyQueuesTheDeclaredInlineEntries(int entryCount)
        {
            SubmitGpfifoArguments header = new() { NumEntries = entryCount };
            Span<ulong> inlineData = stackalloc ulong[] { 0x1000, 0x2000 };

            NvInternalResult result = NvHostChannelDeviceFile.SubmitGpfifoEntries(ref header, inlineData, (ref SubmitGpfifoArguments _, Span<ulong> entries) =>
            {
                Assert.AreEqual(entryCount, entries.Length);
                CollectionAssert.AreEqual(new ulong[] { 0x1000, 0x2000 }[..entryCount], entries.ToArray());

                return NvInternalResult.Success;
            });

            Assert.AreEqual(NvInternalResult.Success, result);
        }

        [TestCase(0x17, 0)]
        [TestCase(0x18, -1)]
        [TestCase(0x1F, 1)]
        [TestCase(0x20, 2)]
        public void SubmitGpfifoRejectsTruncatedOrInvalidInput(int bufferSize, int entryCount)
        {
            Span<byte> arguments = stackalloc byte[bufferSize];
            arguments.Clear();

            if (bufferSize >= Unsafe.SizeOf<SubmitGpfifoArguments>())
            {
                MemoryMarshal.AsRef<SubmitGpfifoArguments>(arguments).NumEntries = entryCount;
            }

            NvInternalResult result = NvHostChannelDeviceFile.SubmitGpfifo(arguments, static (ref SubmitGpfifoArguments _, Span<ulong> _) =>
                throw new AssertionException("Malformed input reached the submission callback."));

            Assert.AreEqual(NvInternalResult.InvalidInput, result);
        }
    }
}
