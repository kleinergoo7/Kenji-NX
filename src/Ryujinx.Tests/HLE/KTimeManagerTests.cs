using NUnit.Framework;
using Ryujinx.HLE;
using Ryujinx.HLE.HOS.Kernel.Common;

namespace Ryujinx.Tests.HLE
{
    /// <summary>
    /// Guards the Switch 1 (19.2 MHz) / Switch 2 (31.25 MHz) guest-counter split.
    /// A wrong frequency skews every tick-based timeout and frame deadline by 1.63x.
    /// </summary>
    public class KTimeManagerTests
    {
        [Test]
        public void Switch1TickFrequencyMatchesOriginal19_2MHz()
        {
            Assert.That(CompatibilityProfile.Switch1.TickFrequency, Is.EqualTo(19_200_000UL));
        }

        [Test]
        public void Switch2TickFrequencyIs31_25MHz()
        {
            Assert.That(CompatibilityProfile.Switch2.TickFrequency, Is.EqualTo(31_250_000UL));
        }

        [Test]
        public void DefaultTimeIncrementIsTwoTicksAt19_2MHz()
        {
            // ceil(2 * (1e9 / 19_200_000)) = 105 ns; the pre-existing Switch 1 value.
            Assert.That(KTimeManager.ConvertGuestTicksToNanoseconds(2, 19_200_000), Is.EqualTo(105));
        }

        [Test]
        public void DefaultTimeIncrementIsTwoTicksAt31_25MHz()
        {
            // ceil(2 * (1e9 / 31_250_000)) = 64 ns; 1.64x smaller than Switch 1, as expected.
            Assert.That(KTimeManager.ConvertGuestTicksToNanoseconds(2, 31_250_000), Is.EqualTo(64));
        }

        [Test]
        public void OneSecondOfGuestTicksConvertsToBillisecond()
        {
            Assert.That(KTimeManager.ConvertGuestTicksToNanoseconds(19_200_000, 19_200_000), Is.EqualTo(1_000_000_000));
            Assert.That(KTimeManager.ConvertGuestTicksToNanoseconds(31_250_000, 31_250_000), Is.EqualTo(1_000_000_000));
        }
    }
}
