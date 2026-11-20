// SPDX-License-Identifier: MIT
using NUnit.Framework;
using Ryujinx.Graphics.Gpu.GpuTrace;
using System.IO;

namespace Ryujinx.Tests.Graphics
{
    public class LxTraceReplayerTests
    {
        private const string SamplePath = "/tmp/opencode/sample-trace/sample.lxtrace";

        [Test]
        public void ReplaySampleYieldsFourNonBlackFrames()
        {
            if (!File.Exists(SamplePath))
            {
                Assert.Ignore("sample trace not present");
            }

            ReplayResult result = LxTraceReplayer.Replay(SamplePath, 4);

            Assert.That(result.Frames.Count, Is.EqualTo(4));
            Assert.That(result.ScreenshotPath, Is.Not.Empty);

            for (int index = 0; index < result.Frames.Count; index++)
            {
                ReplayFrame frame = result.Frames[index];
                Assert.That(frame.Index, Is.EqualTo(index));
                Assert.That(frame.Width, Is.EqualTo(256));
                Assert.That(frame.Height, Is.EqualTo(144));
                Assert.That(frame.Hash, Is.Not.EqualTo("00000000000000000000000000000000"),
                    $"frame {index} has a zero hash");
                Assert.That(frame.Mean, Is.GreaterThan(0.05),
                    $"frame {index} is black (mean {frame.Mean})");
            }
        }
    }
}
