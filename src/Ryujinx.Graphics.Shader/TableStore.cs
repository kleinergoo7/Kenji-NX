namespace Ryujinx.Graphics.Shader
{
    /// <summary>
    /// Table store descriptor (T239).
    /// v1.1 (Switch 2 enablement): new descriptor carried alongside CBuffers/SBuffers/Textures/Images.
    /// Reconstructed from v1.1 decompile evidence (field names verbatim).
    /// </summary>
    public struct TableStore
    {
        public int TableCbSlot;
        public int TableCbOffset;
        public int EntryShift;
        public int EntryOffset;
        public int AddressOffset;
        public int Size;
        public uint Value;
    }
}
