using System.Collections.Generic;
using System.IO;
using System.Linq;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using System;

namespace RainbowForge.Archive
{
	public class FlatArchive
	{
		public FlatArchiveEntry[] Entries { get; }

		private FlatArchive(FlatArchiveEntry[] entries)
		{
			Entries = entries;
		}

		public static FlatArchive Read(BinaryReader r, uint version)
		{
			var entries = new List<FlatArchiveEntry>();

			var index = 0;
			while (r.BaseStream.Position != r.BaseStream.Length)
				entries.Add(FlatArchiveEntry.Read(r, version, index++));

			if (r.BaseStream.Length != r.BaseStream.Position)
				throw new InvalidDataException();

			return new FlatArchive(entries.ToArray());
		}

		/// <summary>
		/// Reads Siege v31-v33 archives whose UID/length table is stored in the
		/// ForgeAsset metadata block and whose payload block contains concatenated files.
		/// </summary>
		public static FlatArchive Read(Forge forge, ForgeAsset asset, BinaryReader payloadStream)
		{
			if (forge.Version is < 31 or > 33 || !asset.HasMeta)
				return Read(payloadStream, forge.Version);

			using var metadataStream = asset.GetMetaDataStream(forge);
			if (metadataStream.BaseStream.Length < sizeof(int)) throw new InvalidDataException("Separated archive metadata is truncated.");
			var count = metadataStream.ReadInt32();
			if (count < 0 || 4L + count * 12L > metadataStream.BaseStream.Length)
				throw new InvalidDataException("Separated archive UID/length table is invalid.");

			var entries = new List<FlatArchiveEntry>(count);
			long payloadOffset = 0;
			for (var index = 0; index < count; index++)
			{
				var uid = metadataStream.ReadUInt64();
				var length = checked((int)metadataStream.ReadUInt32());
				if (payloadOffset + length > payloadStream.BaseStream.Length)
					throw new EndOfStreamException("Separated archive payload exceeds the data block.");
				var detected = DetectPayloadMetadata(payloadStream, forge.Version, uid, length, payloadOffset);
				entries.Add(FlatArchiveEntry.CreateDetected(detected, index, payloadOffset, length));
				payloadOffset += length;
			}
			return new FlatArchive(entries.ToArray());
		}

		private static FileMetaData DetectPayloadMetadata(BinaryReader payload, uint version, ulong uid, int length, long offset)
		{
			var original = payload.BaseStream.Position;
			try
			{
				payload.BaseStream.Position = offset;
				var directMagic = length >= 4 ? payload.ReadUInt32() : 0;
				if (Enum.IsDefined(typeof(Magic), (ulong)directMagic) || MagicHelper.UnknownMagics.Contains(directMagic))
					return FileMetaData.CreateDetected(uid.ToString("X16"), checked((uint)length + 8), directMagic, uid);

				payload.BaseStream.Position = offset;
				var embedded = FileMetaData.Read(payload, version);
				return FileMetaData.CreateDetected(string.IsNullOrWhiteSpace(embedded.FileName) ? uid.ToString("X16") : embedded.FileName,
					checked((uint)length + 8), embedded.FileType, uid);
			}
			catch
			{
				return FileMetaData.CreateDetected(uid.ToString("X16"), checked((uint)length + 8), 0, uid);
			}
			finally { payload.BaseStream.Position = original; }
		}

		public BinaryReader GetEntryStream(Stream archiveStream, ulong entryUid)
		{
			var entry = Entries.FirstOrDefault(entry => entry.MetaData.Uid == entryUid);
			if (entry == null)
				return null;

			var ms = new MemoryStream();
			archiveStream.Seek(entry.PayloadOffset, SeekOrigin.Begin);
			archiveStream.CopyStream(ms, entry.PayloadLength);
			ms.Seek(0, SeekOrigin.Begin);
			return new BinaryReader(ms);
		}

		/// <summary>
		/// Opens an archive entry at its actual payload magic. Separated v31-v33
		/// archives can store a second FileMetaData header inside each length-table
		/// chunk; older archives already point directly at the payload.
		/// </summary>
		public BinaryReader GetEntryPayloadStream(Stream archiveStream, ulong entryUid, uint version)
		{
			var entry = Entries.FirstOrDefault(candidate => candidate.MetaData.Uid == entryUid);
			var reader = GetEntryStream(archiveStream, entryUid);
			if (entry == null || reader == null || version is < 31 or > 33 || reader.BaseStream.Length < sizeof(uint))
				return reader;

			var directMagic = reader.ReadUInt32();
			reader.BaseStream.Position = 0;
			if (directMagic == entry.MetaData.FileType)
				return reader;

			try
			{
				var embedded = FileMetaData.Read(reader, version);
				if (embedded.Uid == entryUid && embedded.FileType == entry.MetaData.FileType)
					return reader;
			}
			catch
			{
			}

			reader.BaseStream.Position = 0;
			return reader;
		}
	}
}
