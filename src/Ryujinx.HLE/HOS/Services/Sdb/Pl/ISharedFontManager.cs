using Ryujinx.HLE.HOS.Ipc;
using Ryujinx.HLE.HOS.Services.Sdb.Pl.Types;
using Ryujinx.Horizon.Common;
using System;

namespace Ryujinx.HLE.HOS.Services.Sdb.Pl
{
    [Service("pl:u")]
    [Service("pl:s")] // 9.0.0+
    class ISharedFontManager : IpcService
    {
        private int _fontSharedMemHandle;

        public ISharedFontManager(ServiceCtx context) { }

        [CommandCmif(0)]
        // RequestLoad(u32)
        public ResultCode RequestLoad(ServiceCtx context)
        {
#pragma warning disable IDE0059 // Remove unnecessary value assignment
            SharedFontType fontType = (SharedFontType)context.RequestData.ReadInt32();
#pragma warning restore IDE0059

            // We don't need to do anything here because we do lazy initialization
            // on SharedFontManager (the font is loaded when necessary).
            return ResultCode.Success;
        }

        [CommandCmif(1)]
        // GetLoadState(u32) -> u32
        public ResultCode GetLoadState(ServiceCtx context)
        {
#pragma warning disable IDE0059 // Remove unnecessary value assignment
            SharedFontType fontType = (SharedFontType)context.RequestData.ReadInt32();
#pragma warning restore IDE0059

            // 1 (true) indicates that the font is already loaded.
            // All fonts are already loaded.
            context.ResponseData.Write(1);

            return ResultCode.Success;
        }

        [CommandCmif(2)]
        // GetFontSize(u32) -> u32
        public ResultCode GetFontSize(ServiceCtx context)
        {
            SharedFontType fontType = (SharedFontType)context.RequestData.ReadInt32();

            context.ResponseData.Write(context.Device.System.SharedFontManager.GetFontSize(fontType));

            return ResultCode.Success;
        }

        [CommandCmif(3)]
        // GetSharedMemoryAddressOffset(u32) -> u32
        public ResultCode GetSharedMemoryAddressOffset(ServiceCtx context)
        {
            SharedFontType fontType = (SharedFontType)context.RequestData.ReadInt32();

            context.ResponseData.Write(context.Device.System.SharedFontManager.GetSharedMemoryAddressOffset(fontType));

            return ResultCode.Success;
        }

        [CommandCmif(4)]
        // GetSharedMemoryNativeHandle() -> handle<copy>
        public ResultCode GetSharedMemoryNativeHandle(ServiceCtx context)
        {
            context.Device.System.SharedFontManager.EnsureInitialized(context.Device.System.ContentManager);

            if (_fontSharedMemHandle == 0)
            {
                if (context.Process.HandleTable.GenerateHandle(context.Device.System.FontSharedMem, out _fontSharedMemHandle) != Result.Success)
                {
                    throw new InvalidOperationException("Out of handles!");
                }
            }

            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(_fontSharedMemHandle);

            return ResultCode.Success;
        }

        [CommandCmif(5)]
        // GetSharedFontInOrderOfPriority(bytes<8, 1>) -> (u8, u32, buffer<unknown, 6>, buffer<unknown, 6>, buffer<unknown, 6>)
        public ResultCode GetSharedFontInOrderOfPriority(ServiceCtx context)
        {
#pragma warning disable IDE0059 // Remove unnecessary value assignment
            long languageCode = context.RequestData.ReadInt64();
#pragma warning restore IDE0059
            int loadedCount = 0;

            for (SharedFontType type = 0; type < SharedFontType.Count; type++)
            {
                uint offset = (uint)type * 4;

                if (!AddFontToOrderOfPriorityList(context, type, offset))
                {
                    break;
                }

                loadedCount++;
            }

            // The first return value is a boolean "fonts_are_loaded" flag (u8), not a count.
            // Some titles (e.g. newer SDK builds) validate it strictly against 1, so writing
            // the font count here (e.g. 6) makes them reject the result and spin re-opening pl:u.
            context.ResponseData.Write(1);
            context.ResponseData.Write(loadedCount);
            // context.ResponseData.Write((int)SharedFontType.Count);

            return ResultCode.Success;
        }

        [CommandCmif(6)] // 4.0.0+
        // GetSharedFontInOrderOfPriorityForSystem(bytes<8, 1>) -> (u8, u32, buffer<unknown, 6>, buffer<unknown, 6>, buffer<unknown, 6>)
        public ResultCode GetSharedFontInOrderOfPriorityForSystem(ServiceCtx context)
        {
            // TODO: Check the differencies with GetSharedFontInOrderOfPriority.

            return GetSharedFontInOrderOfPriority(context);
        }

        // Switch 2 built-in font shared memory: the font shared-memory handle
        // plus the total shared memory size.
        // Switch 2 built-in font shared memory: the font shared-memory handle
        // plus the total shared memory size.
        [CommandCmif(2000)]
        public ResultCode GetBuiltInFontSharedMemory(ServiceCtx context)
        {
            context.Device.System.SharedFontManager.EnsureBuiltInFonts(context.Device.System.ContentManager);

            if (_fontSharedMemHandle == 0 && context.Process.HandleTable.GenerateHandle(context.Device.System.FontSharedMem, out _fontSharedMemHandle) != Ryujinx.Horizon.Common.Result.Success)
            {
                throw new System.InvalidOperationException("Out of handles!");
            }

            context.ResponseData.Write(Horizon.FontSize);
            context.Response.HandleDesc = IpcHandleDesc.MakeCopy(_fontSharedMemHandle);

            return ResultCode.Success;
        }

        // Switch 2 built-in font data: takes a font index, returns found plus the
        // plain-TTF {flags, offset, size} descriptor. Plain TTF copies live in
        // shared memory after the BFTTF entries.
        [CommandCmif(2010)]
        public ResultCode GetBuiltInFontData(ServiceCtx context)
        {
            uint type = context.RequestData.ReadUInt32();
            byte unused = context.RequestData.ReadByte();

            SharedFontManager manager = context.Device.System.SharedFontManager;
            manager.EnsureBuiltInFonts(context.Device.System.ContentManager);

            int count = manager.GetBuiltInFontsCount();
            SharedFontType fontType = (SharedFontType)System.Math.Min(type, (uint)System.Math.Max(count - 1, 0));

            uint offset = 0;
            uint size = 0;
            SharedFontManager.FontInfo info = default;
            bool found = count > 0 && manager.TryGetBuiltInFont(fontType, out info);

            if (found)
            {
                offset = (uint)info.Offset;
                size = (uint)info.Size;
            }

            context.ResponseData.Write(found ? 1u : 0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(0u);
            context.ResponseData.Write(offset);
            context.ResponseData.Write(size);
            context.ResponseData.Write(0u);

            return ResultCode.Success;
        }

        private bool AddFontToOrderOfPriorityList(ServiceCtx context, SharedFontType fontType, uint offset)
        {
            ulong typesPosition = context.Request.ReceiveBuff[0].Position;
            ulong typesSize = context.Request.ReceiveBuff[0].Size;

            ulong offsetsPosition = context.Request.ReceiveBuff[1].Position;
            ulong offsetsSize = context.Request.ReceiveBuff[1].Size;

            ulong fontSizeBufferPosition = context.Request.ReceiveBuff[2].Position;
            ulong fontSizeBufferSize = context.Request.ReceiveBuff[2].Size;

            if (offset + 4 > (uint)typesSize ||
                offset + 4 > (uint)offsetsSize ||
                offset + 4 > (uint)fontSizeBufferSize)
            {
                return false;
            }

            context.Memory.Write(typesPosition + offset, (int)fontType);
            context.Memory.Write(offsetsPosition + offset, context.Device.System.SharedFontManager.GetSharedMemoryAddressOffset(fontType));
            context.Memory.Write(fontSizeBufferPosition + offset, context.Device.System.SharedFontManager.GetFontSize(fontType));

            return true;
        }
    }
}
