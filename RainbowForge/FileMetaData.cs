using System;
using System.IO;
using System.Text;
using RainbowForge.Core;

namespace RainbowForge
{
	public class FileMetaData
	{
		public string FileName { get; }
		public byte[] EncodedMeta { get; }
		public uint ContainerType { get; }
		public uint FileType { get; }
		public ulong Uid { get; }

		private FileMetaData(string fileName, byte[] encodedMeta, uint containerType, uint fileType, ulong uid)
		{
			FileName = fileName;
			EncodedMeta = encodedMeta;
			ContainerType = containerType;
			FileType = fileType;
			Uid = uid;
		}

		public static FileMetaData CreateDetected(string fileName, uint containerType, uint fileType, ulong uid)
		{
			return new FileMetaData(fileName ?? string.Empty, Array.Empty<byte>(), containerType, fileType, uid);
		}

		public static FileMetaData Read(BinaryReader r, uint version)
		{
			switch (version)
			{
                case >= 32:
                {
                    // in AC, a name length with 0x80000000 bit set means encrypted
                    var filenameLength = r.ReadUInt16();
                    var var1 = r.ReadUInt16();
                    var var2 = r.ReadUInt32();
                    var filename = r.ReadBytes((int)filenameLength);
                    var fileType = r.ReadUInt32();
                    var uid = r.ReadUInt64();

                    var name = version >= 34 ? string.Empty : Encoding.ASCII.GetString(
                        NameEncoding.DecodeName(filename, fileType, uid, 0, NameEncoding.FILENAME_ENCODING_FILE_KEY_STEP));
                    return new FileMetaData(name, filename, var1, fileType, uid);
                }
                case 31:
                {
                    // in AC, a name length with 0x80000000 bit set means encrypted
                    var filenameLength = r.ReadUInt16();
                    var var1 = r.ReadUInt16();
                    var var2 = r.ReadUInt32();
                    var filename = r.ReadBytes((int)filenameLength);
                    var fileType = r.ReadUInt32();
                    var uid = r.ReadUInt64();

                    var name = NameEncoding.DecodeName(filename, fileType, uid, 0, NameEncoding.FILENAME_ENCODING_FILE_KEY_STEP);
                    return new FileMetaData(Encoding.ASCII.GetString(name), filename, var1, fileType, uid);
                }
                case 30:
				{
					// in AC, a name length with 0x80000000 bit set means encrypted
					var filenameLength = r.ReadUInt32();
					var filename = r.ReadBytes((int)filenameLength);
					var var1 = r.ReadUInt32();
					var fileType = r.ReadUInt32();
					var uid = r.ReadUInt64();

					var name = NameEncoding.DecodeName(filename, fileType, uid, 0, NameEncoding.FILENAME_ENCODING_FILE_KEY_STEP);
					return new FileMetaData(Encoding.ASCII.GetString(name), filename, var1, fileType, uid);
				}
				case <= 29:
					return ReadLegacy(r, hasNames: version == 29);
				default:
					throw new NotImplementedException($"Unsupported version {version}");
			}
		}

		internal static FileMetaData ReadLegacy(BinaryReader r, bool hasNames)
		{
			var fileType = r.ReadUInt32();
			var bodySize = r.ReadUInt32();
			var nameLength = r.ReadUInt32();
			var name = string.Empty;
			if (hasNames && nameLength != 0)
			{
				if (nameLength > ushort.MaxValue || nameLength > r.BaseStream.Length - r.BaseStream.Position - sizeof(ulong))
					throw new InvalidDataException("Legacy resource name extends beyond the header.");
				name = Encoding.ASCII.GetString(r.ReadBytes((int)nameLength));
			}
			return CreateDetected(name, bodySize, fileType, r.ReadUInt64());
		}
	}
}
