// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.HLE.HOS.Services.Am.AppletAE.AllSystemAppletProxiesService.SystemAppletProxy;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Ryujinx.Tests.HLE
{
    public class Nx2PresentTextureRingTests
    {
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
