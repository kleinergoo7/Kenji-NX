// SPDX-License-Identifier: MIT
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Memory;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.Memory;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CpuMemoryManager = Ryujinx.Cpu.Jit.MemoryManager;
using GpuMemoryManager = Ryujinx.Graphics.Gpu.Memory.MemoryManager;
using GalFormat = Ryujinx.Graphics.GAL.Format;
using MemMapFlags = Ryujinx.Memory.MemoryMapFlags;

namespace Ryujinx.Graphics.Gpu.GpuTrace
{
    /// <summary>
    /// One captured frame of a replay: the RGBA hash (as in frames.json) and its mean brightness.
    /// </summary>
    internal sealed class ReplayFrame
    {
        public int Index { get; init; }
        public string Hash { get; init; }
        public double Mean { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }
        public string ScreenshotPath { get; init; }
    }

    /// <summary>
    /// Result of a trace replay. <see cref="ScreenshotPath"/> is the directory holding the raw frames.
    /// </summary>
    internal sealed class ReplayResult
    {
        public List<ReplayFrame> Frames { get; } = [];
        public string ScreenshotPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// Streams a Lichii .lxtrace through our own headless GPU pipeline: a Vulkan renderer on an
    /// off-screen surface, a GpuContext with a minimal page table, and the trace's GPFIFO/presents.
    /// No guest or Switch boot, and (for the no-snapshot sample) no engine-state restore.
    /// </summary>
    internal sealed class LxTraceReplayer
    {
        private const ulong Pid = 1;
        // shortcut: the Present record has no block-height field; 16 is the sample's BlockHeightLog2.
        // Upgrade by deriving it from the buffer's tiling when replaying non-sample traces.
        private const int GobBlocksInY = 16;

        private readonly IRenderer _renderer;
        private readonly GpuContext _gpu;
        private readonly CpuMemoryManager _cpuMemory;
        private readonly MemoryBlock _backing;
        private GpuMemoryManager _memory;
        private GpuChannel _channel;

        private readonly Dictionary<(ulong, ulong), byte[]> _blobs = [];
        private readonly Dictionary<int, GpuChannel> _channels = [];
        private ulong _gpuBase;
        private ulong _deviceBase;
        private bool _initialized;

        private byte[] _captured;
        private bool _capturedBgra;
        private readonly List<ReplayFrame> _frames = [];
        private readonly string _outputDir;

        private LxTraceReplayer(string outputDir)
        {
            _outputDir = outputDir;
            _renderer = CreateHeadlessRenderer();
            _renderer.Initialize(GraphicsDebugLevel.None);
            _backing = new MemoryBlock(16UL << 20);
            // Guest DRAM is identity-mapped; mirror the trace's device pages onto the backing at offset 0.
            _cpuMemory = new CpuMemoryManager(_backing, 1UL << 31);
            _gpu = new GpuContext(_renderer);
            _gpu.SetGpuThread();
            _gpu.HostInitalized.Set();
            _renderer.ScreenCaptured += OnScreenCaptured;
        }

        public static ReplayResult Replay(string path, int maxFrames)
        {
            string outputDir = Path.Combine(Path.GetTempPath(), "lxtrace-replay-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDir);

            LxTraceReplayer replayer = new(outputDir);
            try
            {
                return replayer.Run(path, maxFrames);
            }
            finally
            {
                replayer.Dispose();
            }
        }

        private ReplayResult Run(string path, int maxFrames)
        {
            using LxTraceReader reader = new(path);
            foreach (LxRecord record in reader.ReadRecords())
            {
                switch (record.Type)
                {
                    case LxRecordType.Config:
                        Setup(reader.Config);
                        break;
                    case LxRecordType.AddressSpace:
                    case LxRecordType.SmmuMap:
                        break; // Identity-mapped when the process page table was created.
                    case LxRecordType.GmmuMap:
                        ApplyGmmuMap(record.Payload);
                        break;
                    case LxRecordType.Channel:
                        ApplyChannel(record.Payload);
                        break;
                    case LxRecordType.Blob:
                        ApplyBlob(record.Payload);
                        break;
                    case LxRecordType.Memory:
                        ApplyMemory(record.Memory.Value);
                        break;
                    case LxRecordType.SnapshotEnd:
                        break;
                    case LxRecordType.Submit:
                        Submit(record.Submit.Value);
                        break;
                    case LxRecordType.Present:
                        if (Present(record.Present.Value))
                        {
                            if (_frames.Count >= maxFrames)
                            {
                                return BuildResult();
                            }
                        }
                        break;
                    case LxRecordType.Resume:
                        break; // No semaphore suspension in this synchronous pipeline.
                    case LxRecordType.End:
                        return BuildResult();
                }
            }

            return BuildResult();
        }

        private ReplayResult BuildResult()
        {
            ReplayResult result = new() { ScreenshotPath = _outputDir };
            result.Frames.AddRange(_frames);
            return result;
        }

        private void Setup(LxConfig config)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _gpu.IsAmpere = config.Switch2; // Gate NX2 behavior on the trace's config.

            ulong size = 16UL << 20;
            _gpu.RegisterProcess(Pid, _cpuMemory);
            _memory = _gpu.CreateMemoryManager(Pid, size);
            _memory.Nx2IdentityMapping = false; // We map the trace's GMMU range explicitly.
            _gpu.InitializeShaderCache(CancellationToken.None);
        }

        private void ApplyGmmuMap(byte[] payload)
        {
            ReadOnlySpan<byte> span = payload;
            if (span.Length < 32)
            {
                return;
            }

            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            ulong gpuAddress = BinaryPrimitives.ReadUInt64LittleEndian(span[8..]);
            ulong deviceAddress = BinaryPrimitives.ReadUInt64LittleEndian(span[16..]);
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);

            _gpuBase = gpuAddress;
            _deviceBase = deviceAddress;
            // shortcut: the last PITCH map wins; the sample has a single 16 MiB map.
            // Upgrade to per-range translation for traces with multiple maps.
            _memory.Map(deviceAddress, gpuAddress, size, (PteKind)kind);
            _cpuMemory.Map(deviceAddress, 0, size, MemMapFlags.None);
        }

        private void ApplyChannel(byte[] payload)
        {
            if (payload.Length < 16)
            {
                return;
            }

            int bindId = BinaryPrimitives.ReadInt32LittleEndian(payload);
            // shortcut: one GpuChannel serves every bind id; the sample has a single channel.
            // Upgrade to one channel per bind id for multi-channel traces.
            if (_channel == null)
            {
                _channel = _gpu.CreateChannel();
            }

            _channel.BindMemory(_memory);
            _channels[bindId] = _channel;
        }

        private void ApplyBlob(byte[] payload)
        {
            if (payload.Length < 16)
            {
                return;
            }

            ulong low = BinaryPrimitives.ReadUInt64LittleEndian(payload);
            ulong high = BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]);
            _blobs[(low, high)] = payload[16..];
        }

        private void ApplyMemory(LxMemory memoryRecord)
        {
            if (!_blobs.TryGetValue((memoryRecord.HashLow, memoryRecord.HashHigh), out byte[] data))
            {
                return;
            }

            // The Memory record names a device/physical address; Write takes a GPU VA.
            ulong gpuVa = memoryRecord.PhysicalAddress - _deviceBase + _gpuBase;
            _memory.Write(gpuVa, data);
        }

        private void Submit(LxSubmit submit)
        {
            if (!_channels.TryGetValue(submit.Channel, out GpuChannel channel))
            {
                channel = _channel;
            }

            if (channel == null)
            {
                return;
            }

            if (submit.Entries.Length != 0)
            {
                channel.PushEntries(submit.Entries);
            }

            if (submit.Prefetch.Length != 0)
            {
                int[] words = new int[submit.Prefetch.Length];
                for (int i = 0; i < words.Length; i++)
                {
                    words[i] = unchecked((int)submit.Prefetch[i]);
                }

                channel.PushHostCommandBuffer(words);
            }

            _renderer.PreFrame();
            _gpu.GPFifo.SignalNewEntries();
            _gpu.GPFifo.DispatchCalls();
        }

        private bool Present(LxPresent present)
        {
            // Drain queued GPU work before compositing, as the native replayer does.
            _gpu.GPFifo.DispatchCalls();

            if (present.Layers.Length == 0)
            {
                return false;
            }

            LxPresentLayer layer = present.Layers[0];
            int width = (int)layer.Width;
            int height = (int)layer.Height;
            ulong address = layer.Address + layer.Offset;

            GalFormat format = layer.PixelFormat switch
            {
                1 => GalFormat.R8G8B8A8Unorm, // Rgba8888
                2 => GalFormat.B8G8R8A8Unorm, // Rgb8888
                _ => GalFormat.R8G8B8A8Unorm,
            };

            ImageCrop crop = new(
                (int)layer.CropLeft, (int)layer.CropRight, (int)layer.CropTop, (int)layer.CropBottom,
                false, false, false, 1f, 1f);

            _captured = null;
            _capturedBgra = false;
            _renderer.Screenshot();
            _gpu.Window.EnqueueFrameThreadSafe(
                Pid, address, width, height, 0, false, GobBlocksInY, format, 4, crop,
                static (_, _) => { }, static (_) => { }, null);
            _gpu.Window.SignalFrameReady();
            _gpu.Window.Present(static () => { });

            if (_captured == null)
            {
                return false;
            }

            // ORACLE.md hashes the post-swap RGBA buffer. Lichii's raw buffer is BGRA, so its PNG
            // (and hash) are R/B-swapped into RGBA order. Our RGBA8 capture is already in that
            // order; only a BGRA capture needs the swap to match.
            byte[] pixels = (byte[])_captured.Clone();
            if (_capturedBgra)
            {
                for (int i = 0; i + 2 < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                }
            }

            // Match ORACLE.md: ImageMagick's fx:mean averages all four channels, alpha included.
            double sum = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                sum += pixels[i] + pixels[i + 1] + pixels[i + 2] + pixels[i + 3];
            }

            double mean = pixels.Length == 0 ? 0 : sum / (pixels.Length / 4 * 4.0 * 255);
            string hash = XXHash128.ComputeHash(pixels).ToString();

            int index = _frames.Count;
            string path = Path.Combine(_outputDir, $"frame-{index:0000}.rgba");
            File.WriteAllBytes(path, pixels);

            _frames.Add(new ReplayFrame
            {
                Index = index,
                Hash = hash,
                Mean = mean,
                Width = width,
                Height = height,
                ScreenshotPath = path,
            });

            return true;
        }

        private void OnScreenCaptured(object sender, ScreenCaptureImageInfo info)
        {
            _captured = info.Data;
            _capturedBgra = info.IsBgra;
        }

        private static unsafe IRenderer CreateHeadlessRenderer()
        {
            Vk api = Vk.GetApi();
            string[] extensions = ["VK_KHR_surface", "VK_EXT_headless_surface"];

            return new VulkanRenderer(
                api,
                (instance, vk) =>
                {
                    if (!vk.TryGetInstanceExtension(instance, out ExtHeadlessSurface headless))
                    {
                        throw new InvalidOperationException("VK_EXT_headless_surface is not available.");
                    }

                    HeadlessSurfaceCreateInfoEXT createInfo = new()
                    {
                        SType = StructureType.HeadlessSurfaceCreateInfoExt,
                    };

                    Result result = headless.CreateHeadlessSurface(instance, in createInfo, null, out SurfaceKHR surface);
                    if (result != Result.Success)
                    {
                        throw new InvalidOperationException($"vkCreateHeadlessSurface failed: {result}");
                    }

                    return surface;
                },
                () => extensions,
                string.Empty);
        }

        private void Dispose()
        {
            _channel?.Dispose();
            _gpu?.Dispose();
            _backing?.Dispose();
        }
    }
}
