using System.Collections.Generic;
using System.Numerics;
using RainbowForge.Core.Container;
using System.IO;

namespace RainbowForge.Core
{
	internal static class Y11S2AllocationTable
	{
		internal static List<Entry> TryRead(BinaryReader reader)
		{
			var stream = reader.BaseStream;
			if (stream.Length < 126)
				return null;

			stream.Position = 66;
			if (reader.ReadUInt32() != 1 || reader.ReadUInt64() != 78)
				return null;

			var count = reader.ReadUInt32();
			var directories = reader.ReadUInt32();
			var allocation = reader.ReadInt64();
			var next = reader.ReadInt64();
			stream.Position = 110;
			var metadata = reader.ReadInt64();
			var directory = reader.ReadInt64();
			if (count < 2 || count > 200_000 || directories != 2 || allocation != 126 || next != -1 ||
			    metadata != allocation + (count + 2L) * 20 || directory != metadata + (count + 2L) * 320 ||
			    directory + 608 > stream.Length)
				return null;

			var entries = new List<Entry>((int)count - 2);
			var uids = new HashSet<ulong>();
			var previousEnd = directory + 608;
			stream.Position = allocation;
			var firstOffset = reader.ReadUInt64();
			var newerEncoding = (firstOffset & 0xFFF) == 0x5A6;
			stream.Position = allocation;
			for (var i = 0; i < count; i++)
			{
				var encodedOffset = reader.ReadUInt64();
				var encodedUid = reader.ReadUInt64();
				var encodedSize = reader.ReadUInt32();
				long offset;
				ulong uid;
				uint size;
				if (newerEncoding)
				{
					if ((encodedOffset & 0xFFF) != 0x5A6)
						return null;
					var page = unchecked((0x67A721A1C1758UL - (encodedOffset >> 12)) ^ 0x7F51UL);
					if (page > (ulong)(stream.Length >> 12))
						return null;
					offset = (long)(page << 12);
					// The UID is rotated by four bits per allocation page, modulo 62.
					var unrotated = BitOperations.RotateRight(encodedUid, (int)(page * 4 % 62) - 4);
					if ((unrotated & 31) != 7)
						return null;
					uid = unchecked(((unrotated >> 5) ^ 0x4B29B7FFB04FADCUL) - 0x64019A970AUL);
					size = unchecked((0xAA08AF34U - encodedSize) ^ 0x386971U);
				}
				else
				{
					offset = unchecked((long)((encodedOffset ^ 0x2cbcb97535777724UL) - 0x611c4a000UL));
					uid = unchecked(((encodedUid ^ 0x095907fffffffff8UL) - 0x14ff5b37fb8UL) ^ 0x1cf540b495cUL);
					size = unchecked((encodedSize ^ 0x514053a0U) - 0x48e3f9U);
				}
				if (offset < previousEnd || (offset & 0xfff) != 0 || size == 0 || size > stream.Length - offset || !uids.Add(uid))
					return null;

				previousEnd = offset + size;
				// The global metadata and fast-load table are not asset containers.
				if (uid is not (0x10 or 0xbe0))
				{
					if (newerEncoding)
					{
						var nextRow = stream.Position;
						stream.Position = offset;
						var isChunk = size >= 8 && reader.ReadUInt64() == 0x1015FA9957FBAA37UL;
						if (!isChunk &&
						    (!RawRiffAsset.TryReadHeader(reader, offset, previousEnd, out var riff) ||
						     riff.Offset + riff.Length != previousEnd))
							return null;
						stream.Position = nextRow;
					}
					entries.Add(new Entry(uid, offset, size, previousEnd) { HasExactChunkBounds = true });
				}
			}

			return uids.Contains(0x10) && uids.Contains(0xbe0) ? entries : null;
		}
	}
}
