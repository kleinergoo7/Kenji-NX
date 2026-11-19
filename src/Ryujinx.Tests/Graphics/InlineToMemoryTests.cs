using NUnit.Framework;
using Ryujinx.Graphics.Gpu.Engine.InlineToMemory;

namespace Ryujinx.Tests.Graphics
{
    public class InlineToMemoryTests
    {
        [TestCase(5u, 3u, 24)]
        [TestCase(0u, uint.MaxValue, 0)]
        [TestCase(4u, 0x1fffffffu, 0x7ffffffc)]
        public void CalculatesWordPaddedTransferSize(uint length, uint count, int expected)
        {
            Assert.That(InlineToMemoryClass.TryGetTransferSize(length, count, out int size), Is.True);
            Assert.That(size, Is.EqualTo(expected));
        }

        [TestCase(4u, 0x20000000u)]
        [TestCase(4u, 0x40000001u)]
        [TestCase(uint.MaxValue, uint.MaxValue)]
        public void RejectsUnrepresentableTransferSize(uint length, uint count)
        {
            Assert.That(InlineToMemoryClass.TryGetTransferSize(length, count, out int size), Is.False);
            Assert.That(size, Is.Zero);
        }

        [TestCase(0u, 1u)]
        [TestCase(4u, 0x20000000u)]
        public void AbortsPreviousTransferWhenLaunchCannotAcceptData(uint length, uint count)
        {
            InlineToMemoryClass engine = new(null, null, false);
            InlineToMemoryClassState state = new() { LineLengthIn = 4, LineCount = 1 };
            engine.LaunchDma(ref state, 1);

            state.LineLengthIn = length;
            state.LineCount = count;
            engine.LaunchDma(ref state, 1);

            // Completing the old transfer would write through the absent GPU channel.
            Assert.DoesNotThrow(() => engine.LoadInlineData(1));
            Assert.DoesNotThrow(() => engine.LoadInlineData(new[] { 1, 2 }));
        }
    }
}
