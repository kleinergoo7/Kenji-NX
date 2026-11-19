// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using System.Collections.Generic;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2FifoTraceTests
    {
        [Test]
        public void DisabledTraceProducesNoOutput()
        {
            Nx2FifoTrace trace = new(false);
            trace.Record(0, [1], 0, 0, 0, _ => Assert.Fail("Disabled trace must not write."));
        }

        [Test]
        public void RecordsPacketStateAndBothCompleteAndPartialLines()
        {
            Nx2FifoTrace trace = new(true);
            List<string> lines = new();
            int[] words = new int[17];
            words[0] = -1;
            words[16] = 123;
            trace.Record(0x1234, words, 0x1c, 2, 4, lines.Add);

            Assert.That(lines.Count, Is.EqualTo(3));
            Assert.That(lines[0], Is.EqualTo("NX2FIFO channel=0x00000000 buffer=1 va=0x1234 words=17 captured=17 pendingMethod=0x1C pendingWords=2 subchannel=4"));
            Assert.That(lines[1], Does.StartWith("NX2FIFO channel=0x00000000 buffer=1 word=0 data=[FFFFFFFF 00000000"));
            Assert.That(lines[2], Is.EqualTo("NX2FIFO channel=0x00000000 buffer=1 word=16 data=[0000007B]"));
        }

        [Test]
        public void EmptyBuffersCannotExceedTheBufferBudget()
        {
            Nx2FifoTrace trace = new(true);
            List<string> lines = new();
            for (int index = 0; index < Nx2FifoTrace.MaxBuffers + 1; index++)
            {
                trace.Record(0, [], 0, 0, 0, lines.Add);
            }

            Assert.That(lines.Count, Is.EqualTo(Nx2FifoTrace.MaxBuffers));
        }

        [Test]
        public void TruncatesAtTheWordBudgetAndSuppressesSubsequentBuffers()
        {
            Nx2FifoTrace trace = new(true);
            List<string> lines = new();
            int[] words = new int[Nx2FifoTrace.MaxWords + 1];
            words[^1] = 123;
            trace.Record(0, words, 0, 0, 0, lines.Add);
            trace.Record(0, [456], 0, 0, 0, lines.Add);

            Assert.That(lines.Count, Is.EqualTo(1 + Nx2FifoTrace.MaxWords / 16));
            Assert.That(lines[0], Does.Contain($"captured={Nx2FifoTrace.MaxWords}"));
            Assert.That(string.Join("\n", lines), Does.Not.Contain("0000007B"));
            Assert.That(string.Join("\n", lines), Does.Not.Contain("000001C8"));
        }
    }
}
