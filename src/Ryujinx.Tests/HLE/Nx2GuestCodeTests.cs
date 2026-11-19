// SPDX-License-Identifier: MIT
using NUnit.Framework;
using System;

namespace Ryujinx.Tests.HLE
{
    class Nx2GuestCodeTests
    {
        // Worked example from the Task 1d brief: a module loaded at 0x85CC000 whose NSO text
        // section has file offset 0x1000, seen from guest VA 0x863d118 at file offset 0x11118.
        private const ulong ModuleBase = 0x85CC000;
        private const ulong TextFileOffset = 0x1000;
        private const ulong GuestVa = 0x863d118;
        private const ulong ExpectedFileOffset = 0x11118;

        // The brief's three values are consistent only if text is mapped 0x61000 into the
        // module: 0x1000 + (0x863d118 - 0x85CC000 - 0x61000) == 0x11118. The shell helper
        // must apply the same arithmetic with the real module's base and text RVA.
        private const ulong TextVirtualOffset = GuestVa - ModuleBase - (ExpectedFileOffset - TextFileOffset);

        private static ulong GuestVaToFileOffset(ulong guestVa, ulong moduleBase, ulong textFileOffset, ulong textVirtualOffset)
        {
            if (guestVa < moduleBase + textVirtualOffset)
            {
                throw new ArgumentOutOfRangeException(nameof(guestVa), "Address is before the NSO text section.");
            }

            return textFileOffset + guestVa - moduleBase - textVirtualOffset;
        }

        [Test]
        public void GuestVaMapsToNsoTextFileOffset()
        {
            Assert.That(TextVirtualOffset, Is.EqualTo(0x61000UL));
            Assert.That(GuestVaToFileOffset(GuestVa, ModuleBase, TextFileOffset, TextVirtualOffset),
                Is.EqualTo(ExpectedFileOffset));
        }

        [Test]
        public void AddressOutsideTextSectionIsRejected()
        {
            Assert.That(() => GuestVaToFileOffset(ModuleBase, ModuleBase, TextFileOffset, TextVirtualOffset),
                Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }
}
