using System;
using Pfim;

namespace Prism.Render;

/// <summary>
/// An owned, tightly packed BGRA8 surface suitable for deterministic GL upload.
/// It deliberately has no GDI+ object or unmanaged pixel lifetime.
/// </summary>
public sealed class SceneTextureData
{
	public enum AlphaMode { Opaque, Cutout, Blend }
	public ulong Uid { get; }
	public int Width { get; }
	public int Height { get; }
	public uint Format { get; }
	public byte[] BgraPixels { get; }
	public AlphaMode Alpha { get; }

	private SceneTextureData(ulong uid, int width, int height, byte[] bgraPixels, uint format)
	{
		Uid = uid;
		Width = width;
		Height = height;
		Format = format;
		BgraPixels = bgraPixels;
		Alpha = ClassifyAlpha(bgraPixels);
	}

	internal static AlphaMode ClassifyAlpha(byte[] pixels)
	{
		var mode = AlphaMode.Opaque;
		for (var i = 3; i < pixels.Length; i += 4)
		{
			if (pixels[i] is > 0 and < 255)
				return AlphaMode.Blend;
			if (pixels[i] == 0)
				mode = AlphaMode.Cutout;
		}
		return mode;
	}

	public static SceneTextureData FromPfim(ulong uid, IImage image, uint format = uint.MaxValue)
	{
		if (image.Width <= 0 || image.Height <= 0)
			throw new ArgumentOutOfRangeException(nameof(image), "Texture dimensions must be positive.");
		var result = new byte[checked(image.Width * image.Height * 4)];
		for (var y = 0; y < image.Height; y++)
		{
			var sourceRow = checked(y * image.Stride);
			var destinationRow = checked(y * image.Width * 4);
			for (var x = 0; x < image.Width; x++)
			{
				var destination = destinationRow + x * 4;
				switch (image.Format)
				{
					case ImageFormat.Rgba32:
					{
						var source = sourceRow + x * 4;
						result[destination] = image.Data[source];
						result[destination + 1] = image.Data[source + 1];
						result[destination + 2] = image.Data[source + 2];
						result[destination + 3] = image.Data[source + 3];
						break;
					}
					case ImageFormat.Rgb24:
					{
						var source = sourceRow + x * 3;
						result[destination] = image.Data[source];
						result[destination + 1] = image.Data[source + 1];
						result[destination + 2] = image.Data[source + 2];
						result[destination + 3] = 255;
						break;
					}
					case ImageFormat.Rgb8:
					{
						var value = image.Data[sourceRow + x];
						result[destination] = value;
						result[destination + 1] = value;
						result[destination + 2] = value;
						result[destination + 3] = 255;
						break;
					}
					case ImageFormat.R5g6b5:
						Write16(image, sourceRow, x, destination, result, 5, 6, 5, false);
						break;
					case ImageFormat.R5g5b5:
						Write16(image, sourceRow, x, destination, result, 5, 5, 5, false);
						break;
					case ImageFormat.R5g5b5a1:
						Write16(image, sourceRow, x, destination, result, 5, 5, 5, true);
						break;
					default:
						throw new NotSupportedException($"Unsupported Pfim scene texture format: {image.Format}");
				}
			}
		}
		return new SceneTextureData(uid, image.Width, image.Height, result, format);
	}

	private static void Write16(IImage image, int sourceRow, int x, int destination, byte[] result,
		int blueBits, int greenBits, int redBits, bool alphaBit)
	{
		var source = sourceRow + x * 2;
		var packed = image.Data[source] | image.Data[source + 1] << 8;
		var blueMask = (1 << blueBits) - 1;
		var greenMask = (1 << greenBits) - 1;
		var redMask = (1 << redBits) - 1;
		var greenShift = blueBits;
		var redShift = blueBits + greenBits;
		result[destination] = (byte)((packed & blueMask) * 255 / blueMask);
		result[destination + 1] = (byte)(((packed >> greenShift) & greenMask) * 255 / greenMask);
		result[destination + 2] = (byte)(((packed >> redShift) & redMask) * 255 / redMask);
		result[destination + 3] = alphaBit && (packed & 0x8000) == 0 ? (byte)0 : (byte)255;
	}
}
