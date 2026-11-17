using System.IO;

namespace RainbowForge.Core.DataBlock
{
	/// <summary>
	///     Describes where to find particular data chunk, whether
	///     it is compressed/raw and where to find it within data
	///     stream. It's serialized form is split in parts. First
	///     we have ``[unpacked, packed]`` for each chunk, then we
	///     have ``[hash, data]`` for each chunk. ``offset`` is not
	///     a serialized variable, it's added merely for reverse
	///     engineering ease.
	/// </summary>
	public class ChunkedData
	{
		public uint DataLength { get; }
		public uint SerializedLength { get; }
		public uint Hash { get; private set; }
		public long Offset { get; private set; }

		public bool IsCompressed => DataLength > SerializedLength;

		private ChunkedData(uint dataLength, uint serializedLength)
		{
			DataLength = dataLength;
			SerializedLength = serializedLength;
		}

		public void Finalize(BinaryReader r)
		{
			Hash = r.ReadUInt32();
			Offset = r.BaseStream.Position;
		}

		public static ChunkedData Read(BinaryReader r) => Read(r, shortSizes: false);

		internal static ChunkedData Read(BinaryReader r, bool shortSizes)
		{
			var unpacked = shortSizes ? r.ReadUInt16() : r.ReadUInt32();
			var packed = shortSizes ? r.ReadUInt16() : r.ReadUInt32();

			return new ChunkedData(unpacked, packed);
		}
	}
}
