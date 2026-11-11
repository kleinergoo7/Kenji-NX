// SPDX-License-Identifier: MIT
using System;
using System.Text;

namespace Ryujinx.Graphics.Gpu.Engine.GPFifo
{
    /// <summary>
    /// Bounded capture of the words actually parsed, including split-packet state.
    /// </summary>
    internal sealed class Nx2FifoTrace
    {
        internal const int MaxBuffers = 64;
        internal const int MaxWords = 16384;
        private const int WordsPerLine = 16;

        private readonly bool _enabled;
        private int _buffers;
        private int _words;

        internal Nx2FifoTrace(bool enabled)
        {
            _enabled = enabled;
        }

        internal void Record(ulong address, ReadOnlySpan<int> words, int method, int remaining, int subchannel, Action<string> write, int channel = 0)
        {
            if (!_enabled || _buffers >= MaxBuffers || _words >= MaxWords)
            {
                return;
            }

            int captured = Math.Min(words.Length, MaxWords - _words);
            _buffers++;
            _words += captured;
            write($"NX2FIFO channel=0x{channel:X8} buffer={_buffers} va=0x{address:X} words={words.Length} captured={captured} pendingMethod=0x{method:X} pendingWords={remaining} subchannel={subchannel}");

            for (int offset = 0; offset < captured; offset += WordsPerLine)
            {
                int end = Math.Min(offset + WordsPerLine, captured);
                StringBuilder line = new($"NX2FIFO channel=0x{channel:X8} buffer={_buffers} word={offset} data=[");
                for (int index = offset; index < end; index++)
                {
                    if (index != offset)
                    {
                        line.Append(' ');
                    }

                    line.Append(words[index].ToString("X8"));
                }

                line.Append(']');
                write(line.ToString());
            }
        }
    }
}
