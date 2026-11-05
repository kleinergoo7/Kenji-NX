using System;

namespace Ryujinx.Graphics.Shader.Translation
{
    [Flags]
    public enum TranslationFlags
    {
        None = 0,

        VertexA = 1 << 0,
        Compute = 1 << 1,
        DebugMode = 1 << 2,

        /// <summary>
        /// Use Sm70+ GPU features (v1.1 addition, T239).
        /// </summary>
        Sm70 = 1 << 3,
    }
}
