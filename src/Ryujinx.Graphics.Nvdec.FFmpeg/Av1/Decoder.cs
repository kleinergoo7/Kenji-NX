using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.Av1
{
    public sealed class Decoder : IDecoder
    {
        public bool IsHardwareAccelerated => false;

        private readonly FFmpegContext _context = new(AVCodecID.AV_CODEC_ID_AV1);

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            return _context.DecodeFrame(outSurf, bitstream.ToArray()) == 0;
        }

        public void Dispose() => _context.Dispose();
    }
}
