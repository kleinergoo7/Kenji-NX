// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Gpu.Synchronization;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ryujinx.Tests.HLE
{
    public class Nx2PresentTextureRingTests
    {
        [Test]
        public void ReachedSyncpointCompletesInlineWithoutRegisteringAWaiter()
        {
            SynchronizationManager syncpoints = new();
            int completions = 0;
            syncpoints.IncrementSyncpoint(1);

            Assert.That(syncpoints.RegisterCallbackOnSyncpoint(1, 1, _ => completions++), Is.Null);
            Assert.That(completions, Is.EqualTo(1));
            syncpoints.IncrementSyncpoint(1);
            Assert.That(completions, Is.EqualTo(1));
        }

        [Test]
        public void SyncpointWaitReturnsTrueOnlyOnTimeout()
        {
            SynchronizationManager syncpoints = new();
            Assert.That(syncpoints.WaitOnSyncpoint(1, 1, TimeSpan.Zero), Is.True);
            syncpoints.IncrementSyncpoint(1);
            Assert.That(syncpoints.WaitOnSyncpoint(1, 1, TimeSpan.Zero), Is.False);
        }

        [TestCase("sead::MainThread", true, true)]
        [TestCase("ModuleSystemWorker3", true, true)]
        [TestCase("nn.npln.Worker", true, true)]
        [TestCase("AudioFrameworkThread", true, false)]
        [TestCase("", true, false)]
        [TestCase("ModuleSystemWorker3", false, false)]
        [TestCase("BaseProcExecutor0", false, true)]
        [TestCase("BaseProcExecutor1", true, true)]
        public void StallStackProbeSelectsStartupThreads(string name, bool waiting, bool expected)
        {
            Assert.That(Nx2ThreadDump.ShouldDumpStallStack(name, waiting), Is.EqualTo(expected));
        }

        [TestCase(0x0001003Cu, 16_666_666L)]
        [TestCase(0x00020078u, 16_666_666L)]
        [TestCase(0x00000000u, 16_666_666L)]
        [TestCase(0x0005001Eu, 133_333_333L)]
        public void PresentIntervalDecodesSwapCountAndRefreshRate(uint timing, long expected)
        {
            Assert.That(INx2Vi2Session.GetPresentIntervalNanoseconds(timing), Is.EqualTo(expected));
        }

        [TestCase(0x0400486024DEC000UL, 2u, 20u)]
        [TestCase(0UL, 1u, 10u)]
        public void PresentFenceSelectionUsesTheCurrentFenceForOcarina(ulong programId, uint id, uint value)
        {
            Assert.That(INx2Vi2Session.GetPresentWaitFence(programId, (2u, 20u), (1u, 10u)), Is.EqualTo((id, value)));
        }

        [TestCase(0xa140)]
        [TestCase(0xc7c0)]
        [TestCase(0xc797)]
        [TestCase(0xb1c0)]
        [TestCase(0xb197)]
        public void InlineUploadFollowsTheBoundEngineOnEverySubchannel(int classId)
        {
            const ulong Page = 0x1000;
            using MemoryBlock backing = new(Page);
            Ryujinx.Cpu.Jit.MemoryManager cpu = new(backing, 0x10000);
            cpu.Map(Page, 0, Page, MemoryMapFlags.None);
            IRenderer renderer = Stub.Create<IRenderer>((_, _) => null);
            using GpuContext gpu = new(renderer) { IsAmpere = classId is 0xc7c0 or 0xc797 };
            gpu.RegisterProcess(1, cpu);
            Ryujinx.Graphics.Gpu.Memory.MemoryManager memory = gpu.CreateMemoryManager(1, 0x10000);
            memory.Map(Page, Page, Page, PteKind.Pitch);
            using GpuChannel channel = gpu.CreateChannel();
            channel.BindMemory(memory);

            for (int subchannel = 0; subchannel < 8; subchannel++)
            {
                int subchannelBits = subchannel << 13;
                int destination = 0x1000 + subchannel * 8;
                channel.PushHostCommandBuffer([
                    0x20010000 | subchannelBits, classId,
                    0x20040060 | subchannelBits, 8, 1, 0, destination,
                    unchecked((int)0x8001006c) | subchannelBits,
                    0x6002006d | subchannelBits, destination,
                ]);
                // A packet may span GPFIFO entries. Its second word must still reach the bound engine.
                channel.PushHostCommandBuffer([destination + 4]);
                gpu.GPFifo.DispatchCalls();
                Assert.That(memory.Read<int>((ulong)destination), Is.EqualTo(destination), $"class=0x{classId:X} subchannel={subchannel}");
                Assert.That(memory.Read<int>((ulong)destination + 4), Is.EqualTo(destination + 4), $"class=0x{classId:X} subchannel={subchannel} continuation");
            }
        }

        [Test]
        public void UnbindingInlineEnginePreventsUploadsUntilItIsBoundAgain()
        {
            const ulong Page = 0x1000;
            using MemoryBlock backing = new(Page);
            Ryujinx.Cpu.Jit.MemoryManager cpu = new(backing, 0x10000);
            cpu.Map(Page, 0, Page, MemoryMapFlags.None);
            IRenderer renderer = Stub.Create<IRenderer>((_, _) => null);
            using GpuContext gpu = new(renderer) { IsAmpere = true };
            gpu.RegisterProcess(1, cpu);
            Ryujinx.Graphics.Gpu.Memory.MemoryManager memory = gpu.CreateMemoryManager(1, 0x10000);
            memory.Map(Page, Page, Page, PteKind.Pitch);
            using GpuChannel channel = gpu.CreateChannel();
            channel.BindMemory(memory);

            const int SubchannelBits = 2 << 13;
            channel.PushHostCommandBuffer([
                0x20010000 | SubchannelBits, 0xa140,
                0x20040060 | SubchannelBits, 4, 1, 0, 0x1000,
                unchecked((int)0x8001006c) | SubchannelBits,
                0x20010000 | SubchannelBits, 0,
                0x6001006d | SubchannelBits, 123,
            ]);
            gpu.GPFifo.DispatchCalls();
            Assert.That(memory.Read<int>(Page), Is.Zero);

            channel.PushHostCommandBuffer([
                0x20010000 | SubchannelBits, 0xa140,
                0x6001006d | SubchannelBits, 456,
            ]);
            gpu.GPFifo.DispatchCalls();
            Assert.That(memory.Read<int>(Page), Is.EqualTo(456));
        }

        [Test]
        public void HostCommandBufferResumesAcrossTwoSemaphoreAcquires()
        {
            const int SemaphorePacket = 0x20040004;
            const int Acquire = 0x01000001;
            const int Release = 0x01000002;
            const ulong Page = 0x1000;

            using MemoryBlock backing = new(Page);
            Ryujinx.Cpu.Jit.MemoryManager cpu = new(backing, 0x10000);
            cpu.Map(Page, 0, Page, MemoryMapFlags.None);
            IRenderer renderer = Stub.Create<IRenderer>((_, _) => null);
            using GpuContext gpu = new(renderer) { IsAmpere = true };
            gpu.RegisterProcess(1, cpu);
            Ryujinx.Graphics.Gpu.Memory.MemoryManager memory = gpu.CreateMemoryManager(1, 0x10000);
            memory.Map(Page, Page, Page, PteKind.Pitch);
            using GpuChannel channel = gpu.CreateChannel();
            channel.BindMemory(memory);

            channel.PushHostCommandBuffer([
                SemaphorePacket, 0, 0x1000, 1, Acquire,
                SemaphorePacket, 0, 0x1004, 2, Acquire,
                SemaphorePacket, 0, 0x1008, 3, Release,
            ]);
            channel.PushHostCommandBuffer([SemaphorePacket, 0, 0x1008, 4, Release]);
            gpu.GPFifo.DispatchCalls();
            Assert.That(memory.Read<int>(0x1008), Is.Zero);

            memory.Write(0x1000, 1);
            Assert.That(gpu.GPFifo.WaitForCommands(), Is.True);
            gpu.GPFifo.DispatchCalls();
            Assert.That(memory.Read<int>(0x1008), Is.Zero);

            memory.Write(0x1004, 2);
            Assert.That(gpu.GPFifo.WaitForCommands(), Is.True);
            gpu.GPFifo.DispatchCalls();
            Assert.That(memory.Read<int>(0x1008), Is.EqualTo(4));
            Assert.That(gpu.GPFifo.WaitForCommands(), Is.False);
        }

        [TestCase(null, "1", 1, false)]
        [TestCase("1", "1", 1, true)]
        [TestCase(null, "8", 8, true)]
        [TestCase("2", "8", 8, false)]
        [TestCase("0", null, 0, false)]
        [TestCase("invalid", null, 1, false)]
        public void PresentThreadDumpUsesAnExplicitTrigger(string presentSetting, string periodicSetting, int present, bool expected)
        {
            Assert.That(INx2Vi2Session.ShouldDumpThreadsAtPresent(presentSetting, periodicSetting, present), Is.EqualTo(expected));
        }

        [Test]
        public void HostFrameQueueReturnsImmediatelyAndPresentsOnlyTheAcceptedFrame()
        {
            IRenderer renderer = Stub.Create<IRenderer>((_, _) => null);
            using GpuContext gpu = new(renderer);
            int swaps = 0;
            int presents = 0;

            Assert.IsTrue(gpu.Window.TryEnqueueHostFrame(swap =>
            {
                presents++;
                swap();
            }));
            Assert.IsFalse(gpu.Window.TryEnqueueHostFrame(_ => Assert.Fail("A second pending frame must be dropped.")));
            Assert.AreEqual(0, presents);
            Assert.IsTrue(gpu.Window.ConsumeFrameAvailable());
            Assert.IsFalse(gpu.Window.ConsumeFrameAvailable());

            gpu.Window.Present(() => swaps++);
            Assert.AreEqual(1, presents);
            Assert.AreEqual(1, swaps);
            Assert.IsTrue(gpu.Window.TryEnqueueHostFrame(_ => { }));
        }

        [Test]
        public void HostFrameCaptureRunsOnlyWhenTheQueueHasRoom()
        {
            IRenderer renderer = Stub.Create<IRenderer>((_, _) => null);
            using GpuContext gpu = new(renderer);
            int presents = 0;

            Assert.IsFalse(gpu.Window.TryCaptureHostFrame(() => null));
            Assert.IsFalse(gpu.Window.ConsumeFrameAvailable());

            Assert.IsTrue(gpu.Window.TryCaptureHostFrame(() => _ => presents++));
            Assert.IsFalse(gpu.Window.TryCaptureHostFrame(() =>
            {
                Assert.Fail("A pending frame must prevent a second snapshot.");
                return null;
            }));

            Assert.IsTrue(gpu.Window.ConsumeFrameAvailable());
            gpu.Window.Present(() => { });
            Assert.AreEqual(1, presents);
            Assert.IsTrue(gpu.Window.TryCaptureHostFrame(() => _ => presents++));
            Assert.IsTrue(gpu.Window.ConsumeFrameAvailable());
            gpu.Window.Present(() => { });
            Assert.AreEqual(2, presents);
        }

        [Test]
        public void ReusesThreeTexturesAndReleasesThemWhenSizeChanges()
        {
            List<ITexture> created = [];
            List<ITexture> released = [];
            IRenderer renderer = Stub.Create<IRenderer>((method, _) =>
            {
                Assert.AreEqual(nameof(IRenderer.CreateTexture), method.Name);
                ITexture texture = null;
                texture = Stub.Create<ITexture>((textureMethod, _) =>
                {
                    Assert.AreEqual(nameof(ITexture.Release), textureMethod.Name);
                    released.Add(texture);
                    return null;
                });
                created.Add(texture);
                return texture;
            });

            INx2Vi2Session.PresentTextureRing ring = new();
            TextureCreateInfo first = CreateInfo(2560, 1440);
            ITexture[] initial = [ring.Rent(renderer, first), ring.Rent(renderer, first), ring.Rent(renderer, first)];

            foreach (ITexture expected in initial)
            {
                Assert.AreSame(expected, ring.Rent(renderer, first));
            }

            Assert.AreEqual(3, created.Count);
            Assert.IsEmpty(released);

            ITexture resized = ring.Rent(renderer, CreateInfo(1920, 1080));
            CollectionAssert.AreEqual(initial, released);
            Assert.AreEqual(4, created.Count);
            Assert.AreSame(created[3], resized);
        }

        private static TextureCreateInfo CreateInfo(int width, int height) =>
            new(width, height, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth,
                Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

        public class Stub : DispatchProxy
        {
            private Func<MethodInfo, object[], object> _invoke;

            public static T Create<T>(Func<MethodInfo, object[], object> invoke) where T : class
            {
                T proxy = DispatchProxy.Create<T, Stub>();
                ((Stub)(object)proxy)._invoke = invoke;
                return proxy;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args) => _invoke(targetMethod, args);
        }
    }
}
