using RainbowForge.Core.Container;
using System;
using RainbowForge.Core.DataBlock;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace RainbowForge.Core
{
    public class Forge
	{
        private readonly Dictionary<ulong, int> _uidToEntryIndexMap;
		public uint Version { get; }
		public uint HeaderOffset { get; }
		public uint NumEntries { get; }
		public Entry[] Entries { get; }

		public BinaryReader Stream { get; }
		public string SourcePath { get; private set; }

		private Forge(uint version, uint headerOffset, uint numEntries, Entry[] entries, BinaryReader stream)
		{
			Version = version;
			HeaderOffset = headerOffset;
			NumEntries = numEntries;
			Entries = entries;
			Stream = stream;

			_uidToEntryIndexMap = new Dictionary<ulong, int>();

			for (var i = 0; i < entries.Length; i++)
				_uidToEntryIndexMap[entries[i].Uid] = i;
		}

		public static Forge Read(BinaryReader r, List<long> entryStarts = null)
		{
			/*
				"scimitar"
			    u32 _version = _binaryReader.readInt32();
			    u64 fatLocation = _binaryReader.readUInt64();
			    m_GlobalMetaFileKey = readResourceId(&_binaryReader); // 4 bytes or 8 depending of game (8 after AC3). It's always 0x10 as far as I can tell

			    // Fat header
			    int MaxFile = _binaryReader.readUInt32();
			    int MaxDir = _binaryReader.readInt32();
			    u64 MaxKey = readResourceId(&_binaryReader); (4 or 8 bytes)
			    u32 Root = _binaryReader.readInt32();
			    int FirstFreeFile = _binaryReader.readInt32();
			    int FirstFreeDir = _binaryReader.readInt32();
			    int SizeOfFat = _binaryReader.readInt32();
			    int NumFat = _binaryReader.readInt32();

			For each fat:
			    m_MaxFile = _binaryReader->readInt32();
			    m_MaxDir = _binaryReader->readInt32();
			    m_PosFat = _binaryReader->readInt64();
			    m_NextPosFat = _binaryReader->readInt64();
			    m_FirstIndex = _binaryReader->readInt32();
			    m_LastIndex = _binaryReader->readInt32();
			    _metaTableOffset = _binaryReader->readInt64();
			    _directoryOffset = _binaryReader->readInt64();
			    
			If metadata is present (not the case in valhalla and later)
			    m_LengthOnDisk = _binaryReader->readInt32(); //0
			    m_UMACHash = _binaryReader->readUInt64(); //8
			    m_EngineVersion = _binaryReader->readInt32(); //0x10
			    m_ClassID = _binaryReader->readUInt32(); //0x14
			    m_RevisionNumberData = _binaryReader->readInt32(); //0x18
			    m_RevisionNumberAttributes = _binaryReader->readInt32(); //0x1C
			    m_Prev = _binaryReader->readInt32(); //0x38
			    m_Next = _binaryReader->readInt32(); //0x3C
			    m_Parent = _binaryReader->readInt32(); //0x40
			    m_Time = (time_t)_binaryReader->readInt32(); //0x44
			    _binaryReader->readBuffer(_name, 128); // 0x48
			    _SCCStatusData = _binaryReader->readInt32(); // 0x24
			    m_MetaFileKey = readResourceId(_binaryReader); // 0x30
			    _SCCStatusAttributes = _binaryReader->readInt32(); // 0x28
			    _isHidden = _binaryReader->readUInt32(); //0x20 (is &1, the file is hidden)
			 */

			var formatId = Encoding.ASCII.GetBytes("scimitar\x00");

			var magic = r.ReadBytes(formatId.Length);
			if (!magic.SequenceEqual(formatId))
				throw new InvalidDataException("Input file not SCIMITAR archive");

			var version = r.ReadUInt32();
            var fatLocation = r.ReadUInt32();
			var gloablMetaFileKey = r.ReadUInt64();

			var x19 = r.ReadUInt32(); // [0x19] = 0
			var x1d = r.ReadByte(); // literally no correlation found to any data so far
            if (version == 29 && fatLocation > r.BaseStream.Position)
            {
                CheckRange(r.BaseStream, fatLocation, 44);
                r.BaseStream.Position = fatLocation;
            }
            if (version >= 34)
            {
                var unk4 = r.ReadUInt32();
            }
            var numEntries = r.ReadUInt32(); // files + hash entry + descriptor entry
			var numDirectories = r.ReadUInt32(); // [0x22] = 2
			var unk2 = r.ReadUInt32();
			var unk3 = r.ReadUInt32();

            var totalEntries = new List<Entry>();
            if (version < 34)
            {
				if (version >= 27)
				{
					var unk4 = r.ReadUInt32();
				}

				var firstFreeFile = r.ReadUInt32();
				var firstFreeDir = r.ReadUInt32();

				var sizeOfFat = r.ReadUInt32();
				var numTables = r.ReadUInt32();

				var tablePosition = r.ReadInt64();
				var visitedTables = new HashSet<long>();
				for (var i = 0; i < numTables; i++)
				{
					if (!visitedTables.Add(tablePosition))
						throw new InvalidDataException("Cyclic Forge allocation table chain.");
					CheckRange(r.BaseStream, tablePosition, 48);
					r.BaseStream.Position = tablePosition;
					var maxFile = r.ReadInt32();

					var maxDir = r.ReadInt32();
					var posFat = r.ReadInt64();
					var nextPosFat = r.ReadInt64();
					var firstIndex = r.ReadInt32();
					var lastIndex = r.ReadInt32();
					var metaTableOffset = r.ReadInt64();
					var directoryOffset = r.ReadInt64();

					CheckRange(r.BaseStream, posFat, maxFile * 20L);
					CheckRange(r.BaseStream, metaTableOffset, maxFile * 320L);
					r.BaseStream.Seek(posFat, SeekOrigin.Begin);
					var entries = new Entry[maxFile];
					for (var j = 0; j < maxFile; j++)
						entries[j] = Entry.Read(r);

					r.BaseStream.Seek(metaTableOffset, SeekOrigin.Begin);
					for (var j = 0; j < maxFile; j++)
						entries[j].MetaData = EntryMetaData.Read(r, entries[j].Uid, (ulong)entries[j].Offset, version);

					totalEntries.AddRange(entries);

					if (nextPosFat == -1)
						break;
					tablePosition = nextPosFat;
				}
            }
            if (version >= 34)
            {
                totalEntries = version == 34 ? Y11S2AllocationTable.TryRead(r) : null;
                if (totalEntries == null)
                {
                    entryStarts ??= GetEntryStarts(r);
                    // Soundmedia banks can contain raw page-aligned RIFF/WEM files.
                    totalEntries = entryStarts.Count > 0 ? GetEntries(r, entryStarts, useResourceUids: version >= 34) : GetRawRiffEntries(r);
                }
            }
            // TODO: organize Forge into tables instead of one big entry list
            return new Forge(version, fatLocation, numEntries, totalEntries.ToArray(), r);
		}

		private static void CheckRange(Stream stream, long offset, long size)
		{
			if (offset < 0 || size < 0 || offset > stream.Length || size > stream.Length - offset)
				throw new InvalidDataException("Forge allocation table extends beyond the archive.");
		}

		public static List<long> GetEntryStarts(BinaryReader r)
		{
            List<long> entryStarts = new List<long>();
			for (long index = 0x1000; index + 19 <= r.BaseStream.Length; index += 0x1000)
			{
				r.BaseStream.Seek(index, SeekOrigin.Begin);
				// Y11S3+: only the magic and the low 7 bytes of the second qword are stable; its top byte (0x01, 0x04, 0x09)
				// and the whole third qword vary per entry
				if (r.ReadUInt64() == 0x1015fa9957fbaa37 &&
					(r.ReadUInt64() & 0x00FFFFFFFFFFFFFF) == 0x00800400000f0003)
				{
					entryStarts.Add(index);
				}
			}
			return entryStarts;
        }

        public static List<Entry> GetEntries(BinaryReader r, List<long> entryStarts, bool useResourceUids = false)
		{
			var entries = new List<Entry>(entryStarts.Count);
			var resourceUids = new ulong[entryStarts.Count];
			for (var i = 0; i < entryStarts.Count; i++)
			{
				var start = entryStarts[i];
				var limit = i + 1 < entryStarts.Count ? entryStarts[i + 1] : r.BaseStream.Length;
				var end = limit;
				var exact = false;
				try
				{
					r.BaseStream.Position = start;
					var metadata = ChunkedDataBlock.TryReadVerifiedEnvelope(r, limit, out _);
					if (metadata != null && ChunkedDataBlock.TryReadVerifiedEnvelope(r, limit, out _) != null)
					{
						end = r.BaseStream.Position;
						exact = true;
						if (useResourceUids && metadata.UnpackedLength >= 16)
						{
							using var directory = new BinaryReader(metadata.GetDataPrefixStream(r, 16));
							var count = directory.ReadUInt32();
							if (count > 0 && 4L + 12L * count <= metadata.UnpackedLength)
								resourceUids[i] = directory.ReadUInt64();
						}
					}
				}
				catch (Exception error) when (error is IOException or InvalidDataException or OverflowException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
				{
					// Keep the page address available for diagnostics and legacy decoding.
				}
				entries.Add(new Entry((ulong)i, start, checked((uint)(end - start)), end) { HasExactChunkBounds = exact });
			}
			if (useResourceUids)
			{
				// Reserve unique resource IDs before retaining any fallback slot numbers.
				var counts = resourceUids.Where(uid => uid != 0).GroupBy(uid => uid)
					.ToDictionary(group => group.Key, group => group.Count());
				var assigned = counts.Where(pair => pair.Value == 1).Select(pair => pair.Key).ToHashSet();
				for (var i = 0; i < entries.Count; i++)
				{
					var uid = resourceUids[i];
					if (uid == 0 || counts[uid] != 1)
					{
						uid = (ulong)i;
						while (!assigned.Add(uid))
							uid += (ulong)entries.Count;
					}
					var entry = entries[i];
					entries[i] = new Entry(uid, entry.Offset, entry.Size, entry.End)
					{
						HasExactChunkBounds = entry.HasExactChunkBounds
					};
				}
			}
			return entries;
        }

        public static List<Entry> GetRawRiffEntries(BinaryReader r)
		{
			var entries = new List<Entry>();
			var length = r.BaseStream.Length;

			for (long index = 0x1000; index + 8 <= length; index += 0x1000)
			{
				if (!RawRiffAsset.TryReadHeader(r, index, length, out var riff))
					continue;

				entries.Add(new Entry((ulong)entries.Count, riff.Offset, (uint)riff.Length, riff.Offset + riff.Length));
				index = (riff.Offset + riff.Length - 1) & ~0xFFFL;
			}

			return entries;
		}

        public static Forge GetForge(string filename)
		{
			FileSystemUtil.AssertFileExists(filename);

			var forgeStream = new BinaryReader(File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
			try
			{
				var forge = Read(forgeStream);
				forge.SourcePath = Path.GetFullPath(filename);
				return forge;
			}
			catch
			{
				forgeStream.Dispose();
				throw;
			}
		}

		public ForgeContainer GetContainer(ulong entryUid)
		{
			return GetContainer(_uidToEntryIndexMap[entryUid]);
		}

		public BinaryReader GetEntryStream(Entry entry)
		{
			var ms = new MemoryStream();
			lock (Stream)
			{
				Stream.BaseStream.Seek(entry.Offset, SeekOrigin.Begin);
				Stream.BaseStream.CopyStream(ms, (int)entry.Size);
			}

			ms.Seek(0, SeekOrigin.Begin);

			return new BinaryReader(ms);
		}

		public ForgeContainer GetContainer(int i)
		{
			var entry = Entries[i];
			var start = entry.Offset;
			var end = entry.End;

			lock (Stream)
			{
				Stream.BaseStream.Seek(start, SeekOrigin.Begin);
				var containerMagic = Stream.ReadUInt32();
				var magic = (ContainerMagic)containerMagic;
				switch (magic)
				{
					case ContainerMagic.Descriptor:
						return Descriptor.Read(Stream, entry);
					case ContainerMagic.Hash:
						return Hash.Read(Stream);
					case ContainerMagic.LegacyFastLoad when Version == 29 && entry.Uid == 0xbf0:
						return Hash.ReadLegacyFastLoad(Stream, entry);
					case ContainerMagic.AprilAlphaFastLoad when Version == 29 && entry.Uid == 0xbf0:
						return Hash.ReadAprilFastLoad(Stream, entry);
					case ContainerMagic.LegacyStreamingDirectory when Version == 29 && entry.Uid == 0x51:
						return new ForgeContainer();
					case ContainerMagic.FileLzo:
						return ForgeAsset.Read(Stream, entry, requireVerifiedEnvelope: true);
					case ContainerMagic.File:
					case ContainerMagic.File2:
					case ContainerMagic.File3:
						return ForgeAsset.Read(Stream, entry);
					case (ContainerMagic)RawRiffAsset.RiffMagic:
						return RawRiffAsset.Read(entry);
					default:
						if (RawRiffAsset.TryReadHeader(Stream, start, Math.Min(end, Stream.BaseStream.Length), out var riff))
							return riff;
						throw new InvalidDataException($"No constructor for container {i} of style 0x{containerMagic:X} (from 0x{start:X} to 0x{end:X}), skipping");
				}
			}
		}
	}
}
