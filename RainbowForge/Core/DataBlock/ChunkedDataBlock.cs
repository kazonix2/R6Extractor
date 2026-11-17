using System;
using System.IO;
using System.IO.Compression;
using RainbowForge.Compression;
using Zstandard.Net;

namespace RainbowForge.Core.DataBlock
{
	public class ChunkedDataBlock : IAssetBlock
	{
		public ChunkedData[] Chunks { get; }
		public bool IsPacked { get; }
		public uint PackedLength { get; }
		public uint UnpackedLength { get; }
		public bool UseOodle { get; }

		private bool VerifyChecksums { get; init; }
		internal bool UseLzo { get; private init; }

		public ChunkedDataBlock(ChunkedData[] chunks, bool isPacked, uint packedLength, uint unpackedLength, bool useOodle)
		{
			Chunks = chunks;
			IsPacked = isPacked;
			PackedLength = packedLength;
			UnpackedLength = unpackedLength;
			UseOodle = useOodle;
		}
		public Stream GetDataStream(BinaryReader r) => GetDataPrefixStream(r, checked((int)UnpackedLength));

		public Stream GetDataPrefixStream(BinaryReader r, int maxBytes)
		{
			var requested = Math.Max(0, maxBytes);
			var ms = new MemoryStream(Math.Min(requested, checked((int)Math.Min(UnpackedLength, int.MaxValue))));

			foreach (var chunk in Chunks)
			{
				var remaining = requested - (int)ms.Length;
				if (remaining == 0)
					break;
				r.BaseStream.Seek(chunk.Offset, SeekOrigin.Begin);
				var packed = r.ReadBytes(checked((int)chunk.SerializedLength));
				if (packed.Length != chunk.SerializedLength)
					throw new EndOfStreamException("Truncated packed chunk.");
				if (VerifyChecksums && PackedChecksum(packed) != chunk.Hash)
					throw new InvalidDataException($"Chunk checksum mismatch at 0x{chunk.Offset:X}.");

				if (!chunk.IsCompressed)
					ms.Write(packed, 0, Math.Min(remaining, packed.Length));
				else if (UseLzo)
				{
					var decoded = Lzo1x.Decompress(packed, (int)chunk.DataLength);
					ms.Write(decoded, 0, Math.Min(remaining, decoded.Length));
				}
				else if (UseOodle)
				{
					var decoded = Oodle2Core8.Decompress(packed, checked((int)chunk.DataLength), Oodle2Core8.FuzzSafe.Yes);
					ms.Write(decoded, 0, Math.Min(remaining, decoded.Length));
				}
				else
				{
					using var input = new MemoryStream(packed, false);
					using var decoder = new ZstandardStream(input, CompressionMode.Decompress, true);
					var length = Math.Min(remaining, checked((int)chunk.DataLength));
					var decoded = new byte[length];
					decoder.ReadExactly(decoded);
					if (VerifyChecksums && length == chunk.DataLength && decoder.ReadByte() != -1)
						throw new InvalidDataException("Zstandard chunk exceeded its declared decoded size.");
					ms.Write(decoded);
				}
			}

			ms.Seek(0, SeekOrigin.Begin);
			return ms;
		}

		private static uint PackedChecksum(byte[] bytes)
		{
			// Unlike standard Adler-32, these envelopes initialize both accumulators to zero.
			uint sum = 0, weighted = 0;
			foreach (var value in bytes)
			{
				sum = (sum + value) % 65521;
				weighted = (weighted + sum) % 65521;
			}
			return (weighted << 16) | sum;
		}

		internal static ChunkedDataBlock TryReadVerifiedEnvelope(BinaryReader r, long end, out ulong magic)
		{
			magic = 0;
			var stream = r.BaseStream;
			var start = stream.Position;
			if (end > stream.Length)
				throw new EndOfStreamException("Chunk envelope extends beyond the archive.");
			if (end - start < 15)
				return null;
			magic = r.ReadUInt64();
			var version = r.ReadUInt16();
			var codec = r.ReadByte();
			var capacity = r.ReadUInt16();
			var flags = r.ReadUInt16();
			var lzo = magic == 0x1004fa9957fbaa33;
			var oodle = magic == 0x1015fa9957fbaa37;
			var known = magic switch
			{
				0x1004fa9957fbaa33 => version == 1 && codec is 0 or 1 && capacity == 0x8000 && flags == 0x8000,
				0x1014fa9957fbaa34 => version == 2 && codec == 3 && capacity == 0 && flags == 0x8004,
				0x1015fa9957fbaa37 => version == 3 && codec == 15 && capacity == 0 && flags == 0x8004,
				_ => false
			};
			if (!known)
			{
				stream.Position = start;
				return null;
			}

			if (end - stream.Position < 4)
				throw new EndOfStreamException("Truncated chunk count.");
			var count = r.ReadUInt32();
			if (count == 0 || count > 65536 || count * (lzo ? 4L : 8L) > end - stream.Position)
				throw new InvalidDataException("Invalid chunk table size.");
			return ReadChunks(r, (int)count, oodle, end, lzo);
		}

		public static ChunkedDataBlock Read(BinaryReader r, bool useOodle = false)
		{
			// numChunks used to be an int32, but it caused read errors
			// TODO: is it actually an int16?
			var numChunks = r.ReadUInt16();
			var u2 = r.ReadUInt16();

			return ReadChunks(r, numChunks, useOodle, null);
		}

		private static ChunkedDataBlock ReadChunks(BinaryReader r, int numChunks, bool useOodle, long? verifiedEnd, bool useLzo = false)
		{
			var serializedLength = 0u;
			var dataLength = 0u;
			var isCompressed = false;

			var chunks = new ChunkedData[numChunks];

			for (var i = 0; i < numChunks; i++)
			{
				var chunk = ChunkedData.Read(r, useLzo);
				if (verifiedEnd.HasValue && (chunk.SerializedLength == 0 || chunk.SerializedLength > chunk.DataLength || chunk.DataLength > (useLzo ? 32 : 256) * 1024))
					throw new InvalidDataException("Invalid packed or decoded chunk size.");

				serializedLength = checked(serializedLength + chunk.SerializedLength);
				dataLength = checked(dataLength + chunk.DataLength);

				isCompressed |= chunk.IsCompressed;

				chunks[i] = chunk;
			}
			// April Alpha stores the core bootstrap as a single 414 MiB LZO block.
			var decodedLimit = useLzo ? 512U * 1024 * 1024 : int.MaxValue;
			if (verifiedEnd.HasValue && (dataLength > decodedLimit || serializedLength + numChunks * 4L > verifiedEnd.Value - r.BaseStream.Position))
				throw new InvalidDataException("Packed chunks exceed the record bounds or decoded size limit.");

			foreach (var chunk in chunks)
			{
				chunk.Finalize(r);
				r.BaseStream.Seek(chunk.SerializedLength, SeekOrigin.Current);
			}

			return new ChunkedDataBlock(chunks, isCompressed, serializedLength, dataLength, useOodle)
			{
				VerifyChecksums = verifiedEnd.HasValue,
				UseLzo = useLzo
			};
		}
	}
}
