// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Cpu.Jit;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.Memory;

namespace Ryujinx.Tests.HLE
{
    public class KTlsPageInfoTests
    {
        [TestCase(ConsoleGeneration.Switch2, true, 0x4001C)]
        [TestCase(ConsoleGeneration.Switch1, true, 0)]
        [TestCase(ConsoleGeneration.Switch2, false, 0)]
        public void WritesOnlyTheSwitch2ThreadHandleField(ConsoleGeneration generation, bool hasTls, int expected)
        {
            ulong pageSize = MemoryBlock.GetPageSize();
            using MemoryBlock ram = new(pageSize);
            MemoryManager memory = new(ram, pageSize * 4);
            memory.IncrementReferenceCount();

            try
            {
                ulong tlsAddress = pageSize + 0x200;
                memory.Map(pageSize, 0, pageSize, MemoryMapFlags.Private);
                memory.Write(tlsAddress + 0x10C, 0x12345678);
                memory.Write(tlsAddress + 0x114, 0x76543210);

                KTlsPageInfo.InitializeThreadHandle(memory, hasTls ? tlsAddress : 0, generation, 0x4001C);

                Assert.That(memory.Read<int>(tlsAddress + 0x110), Is.EqualTo(expected));
                Assert.That(memory.Read<int>(tlsAddress + 0x10C), Is.EqualTo(0x12345678));
                Assert.That(memory.Read<int>(tlsAddress + 0x114), Is.EqualTo(0x76543210));
            }
            finally
            {
                memory.DecrementReferenceCount();
            }
        }
    }
}
