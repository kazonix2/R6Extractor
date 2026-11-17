using System.IO;

namespace RainbowForge.Archive
{
	public class FlatArchiveEntry
	{
		public FileMetaData MetaData { get; }
		public int Index { get; }
		public long PayloadOffset { get; }
		public int PayloadLength { get; }
		public int HeaderLength { get; }

		private FlatArchiveEntry(FileMetaData metaData, int index, long payloadOffset, int payloadLength, int headerLength = 0)
		{
			MetaData = metaData;
			Index = index;
			PayloadOffset = payloadOffset;
			PayloadLength = payloadLength;
			HeaderLength = headerLength;
		}

		public static FlatArchiveEntry CreateDetected(FileMetaData metaData, int index, long payloadOffset, int payloadLength, int headerLength = 0)
		{
			return new FlatArchiveEntry(metaData, index, payloadOffset, payloadLength, headerLength);
		}

		public static FlatArchiveEntry Read(BinaryReader r, uint version, int index)
		{
			var meta = FileMetaData.Read(r, version);

			var length = meta.ContainerType - 8; // length - int64(uid)

			var payloadOffset = r.BaseStream.Position;

			if (r.BaseStream.Length < r.BaseStream.Position + length)
				throw new EndOfStreamException();

			r.BaseStream.Seek(length, SeekOrigin.Current);

			return new FlatArchiveEntry(meta, index, payloadOffset, (int)length, 0);
		}
	}
}
