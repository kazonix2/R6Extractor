using System.Collections.Generic;
using System.IO;
using System.Linq;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using RainbowForge.Core.DataBlock;
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
		/// Reads archives whose UID/length table is stored in the
		/// ForgeAsset metadata block and whose payload block contains concatenated files.
		/// </summary>
		public static FlatArchive Read(Forge forge, ForgeAsset asset, BinaryReader payloadStream)
		{
			var legacyDirectory = forge.Version < 31 && asset.MetaBlock is ChunkedDataBlock { UseLzo: true };
			if (!asset.HasMeta || (forge.Version < 31 && !legacyDirectory))
				return Read(payloadStream, forge.Version);

			using var metadataStream = asset.GetMetaDataStream(forge);
			var countSize = legacyDirectory ? sizeof(ushort) : sizeof(int);
			if (metadataStream.BaseStream.Length < countSize) throw new InvalidDataException("Separated archive metadata is truncated.");
			var count = legacyDirectory ? metadataStream.ReadUInt16() : metadataStream.ReadInt32();
			if (count < 0 || countSize + count * 12L > metadataStream.BaseStream.Length)
				throw new InvalidDataException("Separated archive UID/length table is invalid.");

			var entries = new List<FlatArchiveEntry>(count);
			long payloadOffset = 0;
			for (var index = 0; index < count; index++)
			{
				var uid = metadataStream.ReadUInt64();
				var rawLength = metadataStream.ReadUInt32();
				var length = checked((int)(rawLength & 0x3FFFFFFF));
				// v34+: bit 30 marks a row followed by an extra 16-byte block (seen on textures and sounds)
				if ((rawLength & 0x40000000) != 0)
				{
					if (metadataStream.BaseStream.Length - metadataStream.BaseStream.Position < 16)
						throw new InvalidDataException("Separated archive length extension is truncated.");
					metadataStream.BaseStream.Seek(16, SeekOrigin.Current);
				}
				if (payloadOffset + length > payloadStream.BaseStream.Length)
					throw new EndOfStreamException("Separated archive payload exceeds the data block.");
				var (detected, headerLen) = legacyDirectory
					? ReadLegacyMember(payloadStream, uid, length, payloadOffset)
					: DetectPayloadMetadata(payloadStream, forge.Version, uid, length, payloadOffset);
				entries.Add(FlatArchiveEntry.CreateDetected(detected, index, payloadOffset, length, headerLen));
				payloadOffset += length;
			}
			if (legacyDirectory && (payloadOffset != payloadStream.BaseStream.Length || metadataStream.BaseStream.Position != metadataStream.BaseStream.Length))
				throw new InvalidDataException("Legacy archive directory does not cover its data block.");
			return new FlatArchive(entries.ToArray());
		}

		private static (FileMetaData Meta, int HeaderLength) ReadLegacyMember(BinaryReader payload, ulong uid, int length, long offset)
		{
			var original = payload.BaseStream.Position;
			try
			{
				payload.BaseStream.Position = offset;
				var type = length >= sizeof(uint) ? payload.ReadUInt32() : 0;
				if (length >= 20)
				{
					var bodySize = payload.ReadUInt32(); // Includes the UID.
					var nameSize = payload.ReadUInt32();
					if (bodySize >= 8 && nameSize <= ushort.MaxValue && 12L + nameSize + bodySize == length)
					{
						payload.BaseStream.Position = offset;
						var metadata = FileMetaData.ReadLegacy(payload, hasNames: true);
						if (metadata.Uid == uid)
							return (metadata, 20 + (int)nameSize);
					}
				}
				return (FileMetaData.CreateDetected(uid.ToString("X16"), checked((uint)length + 8), type, uid), 0);
			}
			finally { payload.BaseStream.Position = original; }
		}

		private static (FileMetaData Meta, int HeaderLength) DetectPayloadMetadata(BinaryReader payload, uint version, ulong uid, int length, long offset)
		{
			var original = payload.BaseStream.Position;
			try
			{
				if (length >= 20)
				{
					payload.BaseStream.Position = offset;
					var nameLen = payload.ReadUInt16();
					var containerType = payload.ReadUInt16();

					// In v31+, embedded FileMetaData has containerType == 2 and a valid name length
					if (containerType == 2 && nameLen + 20 <= length)
					{
						payload.BaseStream.Position = offset;
						try
						{
							var embedded = FileMetaData.Read(payload, version);
							if (embedded.FileType != 0 && (version < 34 || embedded.Uid == uid))
							{
								var headerLen = (int)(payload.BaseStream.Position - offset);
								return (FileMetaData.CreateDetected(
									string.IsNullOrWhiteSpace(embedded.FileName) ? uid.ToString("X16") : embedded.FileName,
									checked((uint)length + 8), embedded.FileType, uid), headerLen);
							}
						}
						catch
						{
						}
					}
				}

				payload.BaseStream.Position = offset;
				var directMagic = length >= 4 ? payload.ReadUInt32() : 0;
				if (directMagic != 0 && directMagic != (uint)Magic.Metadata &&
				    (Enum.IsDefined(typeof(Magic), (ulong)directMagic) || MagicHelper.UnknownMagics.Contains(directMagic)))
				{
					return (FileMetaData.CreateDetected(uid.ToString("X16"), checked((uint)length + 8), directMagic, uid), 0);
				}

				payload.BaseStream.Position = offset;
				if (version >= 34)
					return (FileMetaData.CreateDetected(uid.ToString("X16"), checked((uint)length + 8), directMagic, uid), 0);
				var fallback = FileMetaData.Read(payload, version);
				var fallbackHeaderLen = (int)(payload.BaseStream.Position - offset);
				return (FileMetaData.CreateDetected(
					string.IsNullOrWhiteSpace(fallback.FileName) ? uid.ToString("X16") : fallback.FileName,
					checked((uint)length + 8), fallback.FileType, uid), fallbackHeaderLen);
			}
			catch
			{
				return (FileMetaData.CreateDetected(uid.ToString("X16"), checked((uint)length + 8), 0, uid), 0);
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
			if (entry == null || reader == null)
				return reader;

			if (entry.HeaderLength > 0 && entry.HeaderLength < reader.BaseStream.Length)
			{
				reader.BaseStream.Position = entry.HeaderLength;
				return reader;
			}

			if (version < 31 || reader.BaseStream.Length < sizeof(uint))
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
