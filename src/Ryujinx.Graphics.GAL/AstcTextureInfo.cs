namespace Ryujinx.Graphics.GAL
{
    /// <summary>
    /// ASTC texture metadata passed to <see cref="ITexture.SetAstcData"/>.
    /// v1.1 (Switch 2 enablement) addition.
    /// </summary>
    public readonly record struct AstcTextureInfo(
        int BlockWidth,
        int BlockHeight,
        int Width,
        int Height,
        int Depth,
        int Levels,
        int Layers);
}
