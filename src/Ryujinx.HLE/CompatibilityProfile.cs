// SPDX-License-Identifier: MIT
// Console compatibility profile (modeled on the NX Infinity v1.1 Switch 2
// enablement: one profile object threaded through kernel, CPU, GPU and HLE
// instead of scattered isSwitch2 checks; see research_ressource/_patch/).
using System;

namespace Ryujinx.HLE
{
    /// <summary>
    /// Which console generation the emulated title targets.
    /// </summary>
    public enum ConsoleGeneration
    {
        Switch1,
        Switch2,
    }

    /// <summary>
    /// Baked per-generation capabilities. Sessions, processes and services read
    /// features from here instead of branching on ad-hoc flags.
    /// </summary>
    public sealed class CompatibilityProfile
    {
        /// <summary>
        /// Original Switch: 4 CPU cores for guests, no GPU memory attributes.
        /// </summary>
        public static readonly CompatibilityProfile Switch1 = new(ConsoleGeneration.Switch1, 4, false);

        /// <summary>
        /// Switch 2 (Tegra T239): 6 of 8 CPU cores visible to guests, GPU memory attributes.
        /// </summary>
        public static readonly CompatibilityProfile Switch2 = new(ConsoleGeneration.Switch2, 6, true);

        private CompatibilityProfile(ConsoleGeneration generation, int requiredCoreCount, bool supportsGpuMemoryAttributes)
        {
            Generation = generation;
            RequiredCoreCount = requiredCoreCount;
            SupportsGpuMemoryAttributes = supportsGpuMemoryAttributes;
        }

        public ConsoleGeneration Generation { get; }

        /// <summary>
        /// Guest-visible CPU core count consumed at startup.
        /// </summary>
        public int RequiredCoreCount { get; }

        /// <summary>
        /// Gates SVC 0x80 (GPU-attributed mapping). Switch 1 returns NotImplemented.
        /// </summary>
        public bool SupportsGpuMemoryAttributes { get; }

        /// <summary>
        /// ARMv8.3 pointer authentication (A78C). Reserved for CPU feature gating.
        /// </summary>
        public bool SupportsPointerAuthenticationCompatibility => Generation == ConsoleGeneration.Switch2;

        /// <summary>
        /// MTE-tagged memory paths. Reserved for CPU feature gating.
        /// </summary>
        public bool SupportsMemoryTaggingCompatibility => Generation == ConsoleGeneration.Switch2;

        /// <summary>
        /// Master switch for new-instruction translation (dotprod/SHA3 and later).
        /// Reserved for CPU feature gating; no consumer yet.
        /// </summary>
        public bool RequiresArm64InstructionCompatibility => Generation == ConsoleGeneration.Switch2;

        /// <summary>
        /// Guest arch-timer frequency (CNTFRQ_EL0 and the SVC GetSystemTick domain).
        /// Switch 1 runs at 19.2 MHz. Switch 2 / T239 runs at 31.25 MHz: its SDK reads
        /// this value and converts ticks with it (nn::os::GetSystemTickFrequency,
        /// TickManager::ConvertToTimeSpan/ConvertToTick). The working Eglantine
        /// reference uses 31.25 MHz for Switch 2 titles; at 19.2 MHz every tick-based
        /// time in Mario Kart World was off by 1.63x and frame deadlines in ticks
        /// waited ~27 ms, stalling renderer init.
        /// </summary>
        public ulong TickFrequency => Generation == ConsoleGeneration.Switch2
            ? 31_250_000UL
            : 19_200_000UL;
    }
}
