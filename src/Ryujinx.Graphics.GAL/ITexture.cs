using Ryujinx.Common.Memory;

namespace Ryujinx.Graphics.GAL
{
    public interface ITexture
    {
        int Width { get; }
        int Height { get; }

        void CopyTo(ITexture destination, int firstLayer, int firstLevel);
        void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel);
        void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter);
        void CopyTo(BufferRange range, int layer, int level, int stride);

        ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel);

        PinnedSpan<byte> GetData();
        PinnedSpan<byte> GetData(int layer, int level);

        /// <summary>
        /// Sets the texture data. The data passed as a <see cref="MemoryOwner{Byte}" /> will be disposed when
        /// the operation completes.
        /// </summary>
        /// <param name="data">Texture data bytes</param>
        void SetData(MemoryOwner<byte> data);

        /// <summary>
        /// Sets the texture data. The data passed as a <see cref="MemoryOwner{Byte}" /> will be disposed when
        /// the operation completes.
        /// </summary>
        /// <param name="data">Texture data bytes</param>
        /// <param name="layer">Target layer</param>
        /// <param name="level">Target level</param>
        void SetData(MemoryOwner<byte> data, int layer, int level);

        /// <summary>
        /// Sets the texture data. The data passed as a <see cref="MemoryOwner{Byte}" /> will be disposed when
        /// the operation completes.
        /// </summary>
        /// <param name="data">Texture data bytes</param>
        /// <param name="layer">Target layer</param>
        /// <param name="level">Target level</param>
        /// <param name="region">Target sub-region of the texture to update</param>
        void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region);

        /// <summary>
        /// Sets raw ASTC data so the backend may use hardware ASTC decoding (v1.1 addition).
        /// Backends without ASTC acceleration may ignore this call.
        /// </summary>
        /// <param name="data">ASTC data bytes</param>
        /// <param name="info">ASTC texture metadata</param>
        /// <param name="layer">Target layer</param>
        /// <param name="level">Target level</param>
        /// <param name="single">True when the texture has a single layer and level</param>
        void SetAstcData(MemoryOwner<byte> data, AstcTextureInfo info, int layer, int level, bool single)
        {
            data.Dispose();
        }

        void SetStorage(BufferRange buffer);

        void Release();
    }
}
