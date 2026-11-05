using System;

namespace Ryujinx.Graphics.Texture
{
    /// <summary>
    /// Thrown when texture dimensions exceed the supported storage range.
    /// v1.1 (Switch 2 enablement): present in v1.x decompiles, missing from Kenji base.
    /// </summary>
    public sealed class InvalidTextureSizeException : Exception
    {
        public InvalidTextureSizeException(Exception inner = null)
            : base("Texture dimensions exceed the supported storage range.", inner)
        {
        }
    }
}
