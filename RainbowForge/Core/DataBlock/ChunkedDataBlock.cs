using System;
using System.IO;
using System.IO.Compression;
using System.Text;
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

		public ChunkedDataBlock(ChunkedData[] chunks, bool isPacked, uint packedLength, uint unpackedLength, bool useOodle)
		{
			Chunks = chunks;
			IsPacked = isPacked;
			PackedLength = packedLength;
			UnpackedLength = unpackedLength;
			UseOodle = useOodle;
		}
        public Stream GetDataStream(BinaryReader r)
		{
			var ms = new MemoryStream();

			foreach (var chunk in Chunks)
			{
				r.BaseStream.Seek(chunk.Offset, SeekOrigin.Begin);

				if (chunk.IsCompressed)
				{
					if (UseOodle)
					{
						// Contents are compressed
						var compressed = r.ReadBytes((int)chunk.SerializedLength);
						var decompressed = Oodle2Core8.Decompress(compressed, (int)chunk.DataLength);
						ms.Write(decompressed, 0, decompressed.Length);
						//For Hex retro ingeneering
                        //File.WriteAllBytes($"E:\\dump.forge", decompressed);
                        //File.WriteAllBytes($"E:\\dump{chunk.Offset}.forge", decompressed);
                    }
					else
					{
						using var dctx = new ZstandardStream(r.BaseStream, CompressionMode.Decompress, true);
						// TODO: make sure this reads exactly {chunk.SerializedLength} bytes -- it should,
						// but reading {chunk.DataLength} bytes from a decompression stream is
						// a weird way to do it
						dctx.CopyStream(ms, (int)chunk.DataLength);
					}
				}
				else
				{
					r.BaseStream.CopyStream(ms, (int)chunk.SerializedLength);
				}
			}

			ms.Seek(0, SeekOrigin.Begin);
			return ms;
		}

		public Stream GetDataPrefixStream(BinaryReader r, int maxBytes)
		{
			var requested = Math.Max(0, maxBytes);
			var ms = new MemoryStream(Math.Min(requested, checked((int)Math.Min(UnpackedLength, int.MaxValue))));

			foreach (var chunk in Chunks)
			{
				var remaining = requested - checked((int)ms.Length);
				if (remaining <= 0)
					break;

				r.BaseStream.Seek(chunk.Offset, SeekOrigin.Begin);
				var bytesToKeep = Math.Min(remaining, checked((int)chunk.DataLength));
				if (chunk.IsCompressed)
				{
					if (UseOodle)
					{
						// Oodle operates on a complete chunk, but header detection no longer
						// decompresses any of the later chunks.
						var compressed = r.ReadBytes(checked((int)chunk.SerializedLength));
						var decompressed = Oodle2Core8.Decompress(compressed, checked((int)chunk.DataLength));
						ms.Write(decompressed, 0, bytesToKeep);
					}
					else
					{
						using var dctx = new ZstandardStream(r.BaseStream, CompressionMode.Decompress, true);
						dctx.CopyStream(ms, bytesToKeep);
					}
				}
				else
				{
					r.BaseStream.CopyStream(ms, Math.Min(bytesToKeep, checked((int)chunk.SerializedLength)));
				}
			}

			ms.Seek(0, SeekOrigin.Begin);
			return ms;
		}

		public static ChunkedDataBlock Read(BinaryReader r, bool useOodle = false)
		{
			// numChunks used to be an int32, but it caused read errors
			// TODO: is it actually an int16?
			var numChunks = r.ReadUInt16();
			var u2 = r.ReadUInt16();

			var serializedLength = 0u;
			var dataLength = 0u;
			var isCompressed = false;

			var chunks = new ChunkedData[numChunks];

			for (var i = 0; i < numChunks; i++)
			{
				var chunk = ChunkedData.Read(r);

				serializedLength += chunk.SerializedLength;
				dataLength += chunk.DataLength;

				isCompressed |= chunk.IsCompressed;

				chunks[i] = chunk;
			}

			foreach (var chunk in chunks)
			{
				chunk.Finalize(r);
				r.BaseStream.Seek(chunk.SerializedLength, SeekOrigin.Current);
			}

			return new ChunkedDataBlock(chunks, isCompressed, serializedLength, dataLength, useOodle);
		}
	}
}
