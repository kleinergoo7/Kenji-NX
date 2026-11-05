using System;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Texture;

namespace Ryujinx.Graphics.Gpu.Image
{
	internal static class TextureCompatibility
	{
		private enum X018934
		{
			Unclassified,
			Bc1Rgba,
			Bc2,
			Bc3,
			Bc4,
			Bc5,
			Bc6,
			Bc7,
			Etc2Rgb,
			Etc2Rgba,
			Astc4x4,
			Astc5x4,
			Astc5x5,
			Astc6x5,
			Astc6x6,
			Astc8x5,
			Astc8x6,
			Astc8x8,
			Astc10x5,
			Astc10x6,
			Astc10x8,
			Astc10x10,
			Astc12x10,
			Astc12x12
		}

		public static bool CanTextureFlush(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.GAL.Capabilities P_1)
		{
			Format format = P_0.FormatInfo.Format;
			return ToHostCompatibleFormat(P_0, P_1).Format != format;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.FormatInfo ToHostCompatibleFormat(global::Ryujinx.Graphics.Gpu.Image.TextureInfo info, global::Ryujinx.Graphics.GAL.Capabilities caps)
		{
			return ToHostCompatibleFormat(info.FormatInfo, info.Target, caps);
		}

		public static global::Ryujinx.Graphics.Gpu.Image.FormatInfo ToHostCompatibleFormat(global::Ryujinx.Graphics.Gpu.Image.FormatInfo formatInfo, global::Ryujinx.Graphics.GAL.Target target, global::Ryujinx.Graphics.GAL.Capabilities caps)
		{
			if (!caps.SupportsAstcCompression)
			{
				if (formatInfo.Format.IsAstcUnorm())
				{
					if (global::Ryujinx.Graphics.Gpu.GraphicsConfig.EnableTextureRecompression)
					{
						return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.Bc7Unorm, 4, 4, 16, 4);
					}
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
				}
				if (formatInfo.Format.IsAstcSrgb())
				{
					if (global::Ryujinx.Graphics.Gpu.GraphicsConfig.EnableTextureRecompression)
					{
						return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.Bc7Srgb, 4, 4, 16, 4);
					}
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Srgb, 1, 1, 4, 4);
				}
			}
			if (!HostSupportsBcFormat(formatInfo.Format, target, caps))
			{
				switch (formatInfo.Format)
				{
				case Format.Bc6HSfloat:
				case Format.Bc6HUfloat:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R16G16B16A16Float, 1, 1, 8, 4);
				case Format.Bc4Unorm:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8Unorm, 1, 1, 1, 1);
				case Format.Bc4Snorm:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8Snorm, 1, 1, 1, 1);
				case Format.Bc1RgbaUnorm:
				case Format.Bc2Unorm:
				case Format.Bc3Unorm:
				case Format.Bc7Unorm:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
				case Format.Bc1RgbaSrgb:
				case Format.Bc2Srgb:
				case Format.Bc3Srgb:
				case Format.Bc7Srgb:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Srgb, 1, 1, 4, 4);
				case Format.Bc5Snorm:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8Snorm, 1, 1, 2, 2);
				case Format.Bc5Unorm:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8Unorm, 1, 1, 2, 2);
				}
			}
			if (!caps.SupportsEtc2Compression)
			{
				switch (formatInfo.Format)
				{
				case Format.Etc2RgbUnorm:
				case Format.Etc2RgbaUnorm:
				case Format.Etc2RgbPtaUnorm:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
				case Format.Etc2RgbSrgb:
				case Format.Etc2RgbaSrgb:
				case Format.Etc2RgbPtaSrgb:
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Srgb, 1, 1, 4, 4);
				}
			}
			if (!caps.SupportsR4G4Format && formatInfo.Format == Format.R4G4Unorm)
			{
				if (caps.SupportsR4G4B4A4Format)
				{
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R4G4B4A4Unorm, 1, 1, 2, 4);
				}
				return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
			}
			if (formatInfo.Format == Format.R4G4B4A4Unorm)
			{
				if (!caps.SupportsR4G4B4A4Format)
				{
					return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo(Format.R8G8B8A8Unorm, 1, 1, 4, 4);
				}
			}
			else if (!caps.Supports5BitComponentFormat && formatInfo.Format.Is16BitPacked())
			{
				return new global::Ryujinx.Graphics.Gpu.Image.FormatInfo((!formatInfo.Format.IsBgr()) ? Format.R8G8B8A8Unorm : Format.B8G8R8A8Unorm, 1, 1, 4, 4);
			}
			return formatInfo;
		}

		public static bool HostSupportsBcFormat(Format P_0, global::Ryujinx.Graphics.GAL.Target P_1, global::Ryujinx.Graphics.GAL.Capabilities P_2)
		{
			bool flag = P_1 != global::Ryujinx.Graphics.GAL.Target.Texture3D || P_2.Supports3DTextureCompression;
			switch (P_0)
			{
			case Format.Bc1RgbaUnorm:
			case Format.Bc2Unorm:
			case Format.Bc3Unorm:
			case Format.Bc1RgbaSrgb:
			case Format.Bc2Srgb:
			case Format.Bc3Srgb:
				return P_2.SupportsBc123Compression & flag;
			case Format.Bc7Unorm:
			case Format.Bc7Srgb:
			case Format.Bc6HSfloat:
			case Format.Bc6HUfloat:
				return P_2.SupportsBc67Compression & flag;
			default:
				return true;
			case Format.Bc4Unorm:
			case Format.Bc4Snorm:
			case Format.Bc5Unorm:
			case Format.Bc5Snorm:
				return P_2.SupportsBc45Compression & flag;
			}
		}

		public static bool IsFormatHostIncompatible(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.GAL.Capabilities P_1)
		{
			if (!CanTextureFlush(P_0, P_1))
			{
				if (P_0.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DMultisample || P_0.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DMultisampleArray)
				{
					return false;
				}
				return true;
			}
			return false;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality FormatMatches(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, bool P_2, bool P_3)
		{
			if (IsValidDepthAsColorAlias(P_0.FormatInfo.Format, P_1.FormatInfo.Format) && (P_2 | P_3))
			{
				return global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.FormatAlias;
			}
			if (P_3)
			{
				if (P_0.FormatInfo.Format == Format.S8Uint && P_1.FormatInfo.Format == Format.R8Unorm)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.FormatAlias;
				}
				if ((P_0.FormatInfo.Format == Format.D24UnormS8Uint || P_0.FormatInfo.Format == Format.S8UintD24Unorm || P_0.FormatInfo.Format == Format.X8UintD24Unorm) && P_1.FormatInfo.Format == Format.B8G8R8A8Unorm)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.FormatAlias;
				}
				if (P_0.FormatInfo.Format == Format.D32FloatS8Uint && P_1.FormatInfo.Format == Format.R32G32Float)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.FormatAlias;
				}
			}
			if (P_0.FormatInfo.Format != P_1.FormatInfo.Format)
			{
				return global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.NoMatch;
			}
			return global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.Perfect;
		}

		public static bool LayoutMatches(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1)
		{
			if (P_0.IsLinear == P_1.IsLinear && (P_0.IsLinear || P_0.UsesTuringLayout == P_1.UsesTuringLayout))
			{
				if (!P_1.IsLinear)
				{
					if (P_0.GobBlocksInY != P_1.GobBlocksInY)
					{
						return false;
					}
					return P_0.GobBlocksInZ == P_1.GobBlocksInZ;
				}
				return P_0.Stride == P_1.Stride;
			}
			return false;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility PropagateViewCompatibility(global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility P_0, global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility P_1)
		{
			return (global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility)Math.Min((int)P_0, (int)P_1);
		}

		public static bool X018937(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, int P_2, int P_3)
		{
			global::Ryujinx.Graphics.Texture.Size obj = GetAlignedSize(P_0, P_2);
			global::Ryujinx.Graphics.Texture.Size obj2 = GetAlignedSize(P_1, P_3);
			if (obj.Width == obj2.Width && obj.Height == obj2.Height)
			{
				return true;
			}
			if (!P_0.IsLinear || !P_1.IsLinear)
			{
				return false;
			}
			return global::Ryujinx.Common.BitUtils.AlignUp(Math.Max(1, P_0.Stride >> P_2), 32) == P_1.Stride;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility ViewSizeMatches(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, bool P_2, int P_3)
		{
			global::Ryujinx.Graphics.Texture.Size obj = GetAlignedSize(P_0, P_3);
			global::Ryujinx.Graphics.Texture.Size obj2 = GetAlignedSize(P_1);
			global::Ryujinx.Graphics.Texture.Size obj3 = GetSizeInBlocks(P_0, P_3);
			global::Ryujinx.Graphics.Texture.Size obj4 = GetSizeInBlocks(P_1);
			bool flag = obj.Width == obj2.Width;
			if (P_0.FormatInfo.BytesPerPixel != P_1.FormatInfo.BytesPerPixel && FormatInfoEquals(P_0.FormatInfo, P_1.FormatInfo))
			{
				int num = Math.Max(P_0.FormatInfo.BytesPerPixel, P_1.FormatInfo.BytesPerPixel);
				int num2 = global::Ryujinx.Common.BitUtils.AlignUp(obj3.Width * P_0.FormatInfo.BytesPerPixel, num);
				int num3 = global::Ryujinx.Common.BitUtils.AlignUp(obj4.Width * P_1.FormatInfo.BytesPerPixel, num);
				flag = num2 == num3;
			}
			global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility result = global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
			if (P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture3D && obj3.Depth != obj4.Depth)
			{
				result = global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
			}
			if (!flag || obj3.Height != obj4.Height)
			{
				if (P_0.IsLinear && P_1.IsLinear && obj3.Height == obj4.Height)
				{
					if (global::Ryujinx.Common.BitUtils.AlignUp(Math.Max(1, P_0.Stride >> P_3), 32) != P_1.Stride)
					{
						return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.LayoutIncompatible;
					}
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
				}
				if (((P_0.Target.IsMultisample != P_1.Target.IsMultisample) & flag) && obj.Height == obj2.Height)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
				}
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.LayoutIncompatible;
			}
			if ((P_2 && obj3.Width != obj4.Width) || obj3.Width < obj4.Width)
			{
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
			}
			return result;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility ViewSubImagesInBounds(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, int P_2, int P_3)
		{
			if (P_3 + P_1.Levels > P_0.Levels || P_2 + P_1.GetSlices() > P_0.GetSlices())
			{
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.LayoutIncompatible;
			}
			return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
		}

		public static bool SizeMatches(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, bool P_2)
		{
			if (P_0.GetLayers() != P_1.GetLayers())
			{
				return false;
			}
			global::Ryujinx.Graphics.Texture.Size obj = GetSizeInBlocks(P_0);
			global::Ryujinx.Graphics.Texture.Size obj2 = GetSizeInBlocks(P_1);
			if (P_2 || P_0.IsLinear || P_1.IsLinear)
			{
				if (obj.Width != obj2.Width || obj.Height != obj2.Height)
				{
					return false;
				}
				return obj.Depth == obj2.Depth;
			}
			global::Ryujinx.Graphics.Texture.Size obj3 = GetAlignedSize(P_0);
			global::Ryujinx.Graphics.Texture.Size obj4 = GetAlignedSize(P_1);
			if (obj3.Width != obj4.Width || obj.Width < obj2.Width || obj.Height != obj2.Height)
			{
				return false;
			}
			return obj.Depth == obj2.Depth;
		}

		private static global::Ryujinx.Graphics.Texture.Size X018941(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, int P_1, int P_2, int P_3)
		{
			if (!P_0.IsLinear)
			{
				return global::Ryujinx.Graphics.Texture.SizeCalculator.GetBlockLinearAlignedSize(P_1, P_2, P_3, P_0.FormatInfo.BlockWidth, P_0.FormatInfo.BlockHeight, P_0.FormatInfo.BytesPerPixel, P_0.GobBlocksInY, P_0.GobBlocksInZ, P_0.GobBlocksInTileX);
			}
			return global::Ryujinx.Graphics.Texture.SizeCalculator.GetLinearAlignedSize(P_1, P_2, P_0.FormatInfo.BlockWidth, P_0.FormatInfo.BlockHeight, P_0.FormatInfo.BytesPerPixel);
		}

		public static global::Ryujinx.Graphics.Texture.Size GetAlignedSize(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, int P_1 = 0)
		{
			int num = Math.Max(1, P_0.Width >> P_1);
			int num2 = Math.Max(1, P_0.Height >> P_1);
			int num3 = Math.Max(1, P_0.GetDepth() >> P_1);
			return X018941(P_0, num, num2, num3);
		}

		public static global::Ryujinx.Graphics.Texture.Size GetSizeInBlocks(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, int P_1 = 0)
		{
			int num = Math.Max(1, P_0.Width >> P_1);
			int num2 = Math.Max(1, P_0.Height >> P_1);
			return new global::Ryujinx.Graphics.Texture.Size(depth: Math.Max(1, P_0.GetDepth() >> P_1), width: global::Ryujinx.Common.BitUtils.DivRoundUp(num, P_0.FormatInfo.BlockWidth), height: global::Ryujinx.Common.BitUtils.DivRoundUp(num2, P_0.FormatInfo.BlockHeight));
		}

		public static bool ViewLayoutCompatible(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, int P_2)
		{
			if (P_0.IsLinear != P_1.IsLinear || (!P_0.IsLinear && P_0.UsesTuringLayout != P_1.UsesTuringLayout))
			{
				return false;
			}
			if (!P_1.IsLinear)
			{
				int num = Math.Max(1, P_0.Height >> P_2);
				int num2 = Math.Max(1, P_0.GetDepth() >> P_2);
				var (num3, num4) = global::Ryujinx.Graphics.Texture.SizeCalculator.GetMipGobBlockSizes(num, num2, P_0.FormatInfo.BlockHeight, P_0.GobBlocksInY, P_0.GobBlocksInZ, P_2);
				if (num3 == P_1.GobBlocksInY)
				{
					return num4 == P_1.GobBlocksInZ;
				}
				return false;
			}
			return global::Ryujinx.Common.BitUtils.AlignUp(Math.Max(1, P_0.Stride >> P_2), 32) == P_1.Stride;
		}

		public static bool ViewLayoutCompatible(global::Ryujinx.Graphics.Gpu.Image.TextureInfo lhs, global::Ryujinx.Graphics.Gpu.Image.TextureInfo rhs, int lhsLevel, int rhsLevel)
		{
			// v1.0-shaped overload retained for TextureGroup; v1.1 adds the Turing-layout check.
			if (lhs.IsLinear != rhs.IsLinear || (!lhs.IsLinear && lhs.UsesTuringLayout != rhs.UsesTuringLayout))
			{
				return false;
			}

			if (rhs.IsLinear)
			{
				int lhsStride = global::Ryujinx.Common.BitUtils.AlignUp(Math.Max(1, lhs.Stride >> lhsLevel), 32);
				return lhsStride == Math.Max(1, rhs.Stride >> rhsLevel);
			}

			int lhsHeight = Math.Max(1, lhs.Height >> lhsLevel);
			int lhsDepth = Math.Max(1, lhs.GetDepth() >> lhsLevel);
			var (lhsGobBlocksInY, lhsGobBlocksInZ) = global::Ryujinx.Graphics.Texture.SizeCalculator.GetMipGobBlockSizes(lhsHeight, lhsDepth, lhs.FormatInfo.BlockHeight, lhs.GobBlocksInY, lhs.GobBlocksInZ, lhsLevel);
			int rhsHeight = Math.Max(1, rhs.Height >> rhsLevel);
			int rhsDepth = Math.Max(1, rhs.GetDepth() >> rhsLevel);
			var (rhsGobBlocksInY, rhsGobBlocksInZ) = global::Ryujinx.Graphics.Texture.SizeCalculator.GetMipGobBlockSizes(rhsHeight, rhsDepth, rhs.FormatInfo.BlockHeight, rhs.GobBlocksInY, rhs.GobBlocksInZ, rhsLevel);
			return lhsGobBlocksInY == rhsGobBlocksInY && lhsGobBlocksInZ == rhsGobBlocksInZ;
		}

		public static bool CopySizeMatches(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, int P_2, int P_3)
		{
			if (P_0.IsLinear == P_1.IsLinear && (P_0.IsLinear || P_0.UsesTuringLayout == P_1.UsesTuringLayout))
			{
				if (!P_1.IsLinear)
				{
					int num = Math.Max(1, P_0.Height >> P_2);
					int num2 = Math.Max(1, P_0.GetDepth() >> P_2);
					(int, int) tuple = global::Ryujinx.Graphics.Texture.SizeCalculator.GetMipGobBlockSizes(num, num2, P_0.FormatInfo.BlockHeight, P_0.GobBlocksInY, P_0.GobBlocksInZ, P_2);
					int item = tuple.Item1;
					int item2 = tuple.Item2;
					int num3 = Math.Max(1, P_1.Height >> P_3);
					int num4 = Math.Max(1, P_1.GetDepth() >> P_3);
					var (num5, num6) = global::Ryujinx.Graphics.Texture.SizeCalculator.GetMipGobBlockSizes(num3, num4, P_1.FormatInfo.BlockHeight, P_1.GobBlocksInY, P_1.GobBlocksInZ, P_3);
					if (item != num5)
					{
						return false;
					}
					return item2 == num6;
				}
				int num7 = global::Ryujinx.Common.BitUtils.AlignUp(Math.Max(1, P_0.Stride >> P_2), 32);
				int num8 = Math.Max(1, P_1.Stride >> P_3);
				num8 = global::Ryujinx.Common.BitUtils.AlignUp(num8, 32);
				return num7 == num8;
			}
			return false;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility ViewFormatCompatible(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, global::Ryujinx.Graphics.GAL.Capabilities P_2, global::Ryujinx.Graphics.Gpu.Image.TextureSearchFlags P_3)
		{
			global::Ryujinx.Graphics.Gpu.Image.FormatInfo formatInfo = P_0.FormatInfo;
			global::Ryujinx.Graphics.Gpu.Image.FormatInfo formatInfo2 = P_1.FormatInfo;
			if (!formatInfo.Format.IsDepthOrStencil() && !formatInfo2.Format.IsDepthOrStencil())
			{
				if (CanTextureFlush(P_0, P_2) || CanTextureFlush(P_1, P_2))
				{
					if (formatInfo.Format == formatInfo2.Format)
					{
						return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
					}
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Incompatible;
				}
				if (!formatInfo.IsCompressed || !formatInfo2.IsCompressed)
				{
					if (formatInfo.BytesPerPixel != formatInfo2.BytesPerPixel)
					{
						if (FormatInfoEquals(formatInfo, formatInfo2))
						{
							return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
						}
						return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Incompatible;
					}
					if (P_0.FormatInfo.IsCompressed == P_1.FormatInfo.IsCompressed)
					{
						return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
					}
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
				}
				X018934 obj = GetFormatClass(formatInfo.Format);
				X018934 obj2 = GetFormatClass(formatInfo2.Format);
				if (obj == obj2)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
				}
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Incompatible;
			}
			bool flag = P_3.HasFlag(global::Ryujinx.Graphics.Gpu.Image.TextureSearchFlags.ForSampler);
			bool flag2 = P_3.HasFlag(global::Ryujinx.Graphics.Gpu.Image.TextureSearchFlags.DepthAlias);
			switch (FormatMatches(P_0, P_1, flag, flag2))
			{
			case global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.FormatAlias:
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.FormatAlias;
			default:
				if (!IsValidColorAsDepthAlias(formatInfo.Format, formatInfo2.Format) && !IsValidDepthAsColorAlias(formatInfo.Format, formatInfo2.Format))
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Incompatible;
				}
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
			case global::Ryujinx.Graphics.Gpu.Image.TextureMatchQuality.Perfect:
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
			}
		}

		private static bool IsValidColorAsDepthAlias(Format P_0, Format P_1)
		{
			if (P_0 != Format.R32Float || P_1 != Format.D32Float)
			{
				if (P_0 != Format.R16Unorm)
				{
					return false;
				}
				return P_1 == Format.D16Unorm;
			}
			return true;
		}

		private static bool IsValidDepthAsColorAlias(Format P_0, Format P_1)
		{
			if (P_0 == Format.D32Float && P_1 == Format.R32Float)
			{
				return true;
			}
			if (P_0 != Format.D16Unorm)
			{
				return false;
			}
			return P_1 == Format.R16Unorm;
		}

		private static bool FormatInfoEquals(global::Ryujinx.Graphics.Gpu.Image.FormatInfo P_0, global::Ryujinx.Graphics.Gpu.Image.FormatInfo P_1)
		{
			if (P_1.BytesPerPixel < P_0.BytesPerPixel)
			{
				global::Ryujinx.Graphics.Gpu.Image.FormatInfo obj = P_1;
				P_1 = P_0;
				P_0 = obj;
			}
			if ((P_0.Format == Format.R8G8B8A8Unorm && P_1.Format == Format.R32G32B32A32Float) || (P_0.Format == Format.R8Unorm && P_1.Format == Format.R8G8B8A8Unorm))
			{
				return true;
			}
			if (P_0.Format == Format.R8Unorm)
			{
				return P_1.Format == Format.R32Uint;
			}
			return false;
		}

		public static global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility ViewTargetCompatible(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1, ref global::Ryujinx.Graphics.GAL.Capabilities P_2)
		{
			bool flag = false;
			switch (P_0.Target)
			{
			case global::Ryujinx.Graphics.GAL.Target.Cubemap:
			case global::Ryujinx.Graphics.GAL.Target.CubemapArray:
				flag = P_1.Target == global::Ryujinx.Graphics.GAL.Target.Cubemap || P_1.Target == global::Ryujinx.Graphics.GAL.Target.CubemapArray;
				if (P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2D || P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DArray)
				{
					if (!P_2.SupportsCubemapView)
					{
						return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
					}
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
				}
				goto default;
			case global::Ryujinx.Graphics.GAL.Target.Texture2DMultisample:
			case global::Ryujinx.Graphics.GAL.Target.Texture2DMultisampleArray:
				if (P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2D || P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DArray)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
				}
				flag = P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DMultisample || P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DMultisampleArray;
				goto default;
			case global::Ryujinx.Graphics.GAL.Target.Texture1D:
			case global::Ryujinx.Graphics.GAL.Target.Texture1DArray:
				flag = P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture1D || P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture1DArray;
				goto default;
			case global::Ryujinx.Graphics.GAL.Target.Texture3D:
				if (P_1.Target != global::Ryujinx.Graphics.GAL.Target.Texture2D)
				{
					flag = P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture3D;
					goto default;
				}
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
			case global::Ryujinx.Graphics.GAL.Target.Texture2DArray:
				flag = P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2D || P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DArray;
				if (P_1.Target == global::Ryujinx.Graphics.GAL.Target.Cubemap || P_1.Target == global::Ryujinx.Graphics.GAL.Target.CubemapArray)
				{
					if (P_2.SupportsCubemapView)
					{
						return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
					}
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.CopyOnly;
				}
				goto default;
			default:
				if (!flag)
				{
					return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Incompatible;
				}
				return global::Ryujinx.Graphics.Gpu.Image.TextureViewCompatibility.Full;
			case global::Ryujinx.Graphics.GAL.Target.Texture2D:
				flag = P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2D || P_1.Target == global::Ryujinx.Graphics.GAL.Target.Texture2DArray;
				goto default;
			}
		}

		public static bool TargetAndSamplesCompatible(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1)
		{
			if (P_0.DepthStencilMode != P_1.DepthStencilMode || P_0.SwizzleR != P_1.SwizzleR || P_0.SwizzleG != P_1.SwizzleG || P_0.SwizzleB != P_1.SwizzleB)
			{
				return false;
			}
			return P_0.SwizzleA == P_1.SwizzleA;
		}

		public static bool SamplerParamsMatches(global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_0, global::Ryujinx.Graphics.Gpu.Image.TextureInfo P_1)
		{
			if (P_0.Target != P_1.Target || P_0.SamplesInX != P_1.SamplesInX)
			{
				return false;
			}
			return P_0.SamplesInY == P_1.SamplesInY;
		}

		private static X018934 GetFormatClass(Format P_0)
		{
			switch (P_0)
			{
			case Format.Astc10x6Unorm:
			case Format.Astc10x6Srgb:
				return X018934.Astc10x6;
			default:
				return X018934.Unclassified;
			case Format.Astc6x6Unorm:
			case Format.Astc6x6Srgb:
				return X018934.Astc6x6;
			case Format.Bc2Unorm:
			case Format.Bc2Srgb:
				return X018934.Bc2;
			case Format.Astc4x4Unorm:
			case Format.Astc4x4Srgb:
				return X018934.Astc4x4;
			case Format.Bc1RgbaUnorm:
			case Format.Bc1RgbaSrgb:
				return X018934.Bc1Rgba;
			case Format.Bc7Unorm:
			case Format.Bc7Srgb:
				return X018934.Bc7;
			case Format.Astc5x5Unorm:
			case Format.Astc5x5Srgb:
				return X018934.Astc5x5;
			case Format.Bc6HSfloat:
			case Format.Bc6HUfloat:
				return X018934.Bc6;
			case Format.Astc8x6Unorm:
			case Format.Astc8x6Srgb:
				return X018934.Astc8x6;
			case Format.Astc10x10Unorm:
			case Format.Astc10x10Srgb:
				return X018934.Astc10x10;
			case Format.Bc5Unorm:
			case Format.Bc5Snorm:
				return X018934.Bc5;
			case Format.Astc12x12Unorm:
			case Format.Astc12x12Srgb:
				return X018934.Astc12x12;
			case Format.Astc10x8Unorm:
			case Format.Astc10x8Srgb:
				return X018934.Astc10x8;
			case Format.Bc4Unorm:
			case Format.Bc4Snorm:
				return X018934.Bc4;
			case Format.Astc8x8Unorm:
			case Format.Astc8x8Srgb:
				return X018934.Astc8x8;
			case Format.Astc8x5Unorm:
			case Format.Astc8x5Srgb:
				return X018934.Astc8x5;
			case Format.Astc6x5Unorm:
			case Format.Astc6x5Srgb:
				return X018934.Astc6x5;
			case Format.Astc10x5Unorm:
			case Format.Astc10x5Srgb:
				return X018934.Astc10x5;
			case Format.Astc12x10Unorm:
			case Format.Astc12x10Srgb:
				return X018934.Astc12x10;
			case Format.Bc3Unorm:
			case Format.Bc3Srgb:
				return X018934.Bc3;
			case Format.Astc5x4Unorm:
			case Format.Astc5x4Srgb:
				return X018934.Astc5x4;
			case Format.Etc2RgbUnorm:
			case Format.Etc2RgbSrgb:
				return X018934.Etc2Rgb;
			case Format.Etc2RgbaUnorm:
			case Format.Etc2RgbaSrgb:
				return X018934.Etc2Rgba;
			}
		}
	}
}