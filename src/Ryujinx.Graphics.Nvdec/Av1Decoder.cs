using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Av1;
using Ryujinx.Graphics.Nvdec.Image;
using Ryujinx.Graphics.Video;
using System;

namespace Ryujinx.Graphics.Nvdec
{
    // AV1 Nvdec support follows the VP8/H.264 pattern: a thin Nvdec-side wrapper
    // around the FFmpeg-backed AV1 decoder (Ryujinx.Graphics.Nvdec.FFmpeg.Av1).
    // The Nvdec register layout / picture-info types for AV1 are not yet defined,
    // so this mirrors Vp8Decoder's shape and delegates to the FFmpeg decoder.
    static class Av1Decoder
    {
        public static void Decode(NvdecDecoderContext context, ResourceManager rm, ref NvdecRegisters state)
        {
            // TODO: parse AV1 picture info from Nvdec registers once the AV1
            // register layout types are defined, mirroring Vp8Decoder.Decode.
            // The FFmpeg-backed decoder is available via context.GetAv1Decoder().
            Logger.Warning?.Print(LogClass.Nvdec, "AV1 decode requested but Nvdec register types are not yet implemented.");
        }
    }
}