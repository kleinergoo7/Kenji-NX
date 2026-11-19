// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Shader.Decoders.Ampere;

namespace Ryujinx.Tests.Graphics
{
    public class Nx2AmpereLoweringTests
    {
        [Test]
        public void SyntheticSm86ProgramLowersToMaxwell()
        {
            AmpereInstruction[] code =
            [
                Mov(dest: 0, immediate: 0x1234),
                Mov(dest: 1, immediate: 0x5678),
                Exit(),
            ];

            AmpereLoweredProgram lowered = AmpereLowering.LowerToMaxwell(code);

            Assert.That(lowered.Success, Is.True, lowered.Error);
            Assert.That(lowered.Instructions.Count, Is.EqualTo(code.Length));
            Assert.That(lowered.SourceSize, Is.EqualTo((uint)(code.Length * AmpereInstruction.Size)));
            Assert.That(lowered.Instructions[0], Is.Not.Zero);
        }

        [Test]
        public void AmpereLoweringSelfTestDoesNotThrow()
        {
            Assert.DoesNotThrow(AmpereLoweringSelfTest.Run);
        }

        private static AmpereInstruction Mov(uint dest, uint immediate)
        {
            return new AmpereInstruction(0x882U | (7U << 12) | (dest << 16), immediate, 0, 0);
        }

        private static AmpereInstruction Exit()
        {
            return new AmpereInstruction(0x94dU | (7U << 12), 0, 7U << 23, 0);
        }
    }
}
