using System;
using System.Buffers.Binary;
using System.IO;

namespace RainbowForge.Core.Container
{
	/// <summary>
	///     An uncompressed, page-aligned Wwise RIFF/WEM payload stored directly in a v34+ soundmedia bank,
	///     with no container or data block headers around it
	/// </summary>
	public class RawRiffAsset : ForgeContainer
	{
		public const uint RiffMagic = 0x46464952; // "RIFF"

		public long Offset { get; }
		public int Length { get; }

		private RawRiffAsset(long offset, int length)
		{
			Offset = offset;
			Length = length;
		}

		public static RawRiffAsset Read(Entry entry)
		{
			return new RawRiffAsset(entry.Offset, (int)entry.Size);
		}

		internal static bool TryReadHeader(BinaryReader reader, long offset, long limit, out RawRiffAsset asset)
		{
			asset = null;
			var stream = reader.BaseStream;
			var start = stream.Position;
			try
			{
				if (offset < 0 || limit - offset < 12)
					return false;
				stream.Position = offset;
				Span<byte> header = stackalloc byte[12];
				stream.ReadExactly(header);
				if (header[..7].SequenceEqual(new byte[] { 0, 0, 0, 0xF8, 0, 0, 0 }) ||
				    header[..7].SequenceEqual(new byte[] { 0, 0, 0xF8, 0xFB, 0, 0, 0 }))
				{
					offset += 16;
					if (limit - offset < 12)
						return false;
					stream.Position = offset;
					stream.ReadExactly(header);
				}
				if (!header[8..].SequenceEqual("WAVE"u8))
					return false;
				long length;
				if (header[..4].SequenceEqual("RIFF"u8))
					length = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8L;
				else if (header[..4].SequenceEqual("RIFX"u8))
					length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]) + 8L;
				else
					return false;
				if (length < 12 || length > int.MaxValue || length > limit - offset)
					return false;
				asset = new RawRiffAsset(offset, (int)length);
				return true;
			}
			finally
			{
				stream.Position = start;
			}
		}

		public BinaryReader GetDataStream(Forge forge)
		{
			var ms = new MemoryStream();
			lock (forge.Stream)
			{
				forge.Stream.BaseStream.Seek(Offset, SeekOrigin.Begin);
				forge.Stream.BaseStream.CopyStream(ms, Length);
			}

			ms.Seek(0, SeekOrigin.Begin);
			return new BinaryReader(ms);
		}
	}
}
