using System.IO;
using System.Runtime.InteropServices;
using RainbowForge.Core.DataBlock;

namespace RainbowForge.Core
{
	public class DepGraph
	{
		public DepGraphEntry[] Structs { get; private set; }
		public byte Version { get; }

		private DepGraph(byte version, DepGraphEntry[] structs)
		{
			Version = version;
			Structs = structs;
		}

		public static DepGraph Read(BinaryReader r)
		{
			using var stream = Unpack(r);
			using var depGraphStream = new BinaryReader(stream);
			var version = depGraphStream.ReadByte();
			var structs = new DepGraphEntry[(stream.Length - 1) / DepGraphEntry.SizeInBytes];
			for (var i = 0; i < structs.Length; i++)
				structs[i] = depGraphStream.ReadStruct<DepGraphEntry>(DepGraphEntry.SizeInBytes);
			return new DepGraph(version, structs);
		}

		public static Stream Unpack(BinaryReader r)
		{
			var input = r.BaseStream;
			var start = input.Position;
			var end = input.Length;
			if (start < end && r.ReadByte() == 1)
			{
				// Closed Alpha stores version-1 graphs directly, without a chunk envelope.
				var size = end - start;
				if (size > 128 * 1024 * 1024 || (size - 1) % DepGraphEntry.SizeInBytes != 0)
					throw new InvalidDataException("Unexpected raw dependency graph payload layout.");
				input.Position = start;
				var decodedBytes = new byte[(int)size];
				input.ReadExactly(decodedBytes);
				return new MemoryStream(decodedBytes, writable: false);
			}
			input.Position = start;
			var block = ChunkedDataBlock.TryReadVerifiedEnvelope(r, end, out _);
			if (block == null)
				block = ReadLegacyBlock(r);
			if (r.BaseStream.Position != end)
				throw new InvalidDataException("Trailing dependency graph bytes.");
			var decoded = block.GetDataStream(r);
			if (decoded.ReadByte() is not (1 or 2) || (decoded.Length - 1) % DepGraphEntry.SizeInBytes != 0)
			{
				decoded.Dispose();
				throw new InvalidDataException("Unexpected dependency graph payload layout.");
			}
			decoded.Position = 0;
			return decoded;
		}

		private static ChunkedDataBlock ReadLegacyBlock(BinaryReader r)
		{
			var containerMagic = (ContainerMagic)r.ReadUInt32();
			if (containerMagic != ContainerMagic.File)
				throw new InvalidDataException($"Expected container magic, found 0x{containerMagic:X8}");

			var fileContainerMagic = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.FileContainer, fileContainerMagic);

			var x = r.ReadUInt16(); // 2 (changed to 3 in Y5) <-- container deserializer type
			var assetDeserializerType = r.ReadUInt16(); // 3 for chunked, 7 for linear (flags?)
			var y = r.ReadByte(); // 0
			var z = r.ReadUInt16();

			if (assetDeserializerType != 3)
				throw new InvalidDataException($"Expected chunked asset deserializer magic, found 0x{assetDeserializerType:X4}");

			return ChunkedDataBlock.Read(r);
		}
	}

	[StructLayout(LayoutKind.Explicit)]
	public struct DepGraphEntry
	{
		public const int SizeInBytes = 24;

		[FieldOffset(0)] public ulong ParentUid;
		[FieldOffset(8)] public ulong ChildUid;
		[FieldOffset(16)] public uint ChildType;
		[FieldOffset(20)] public ushort Unk1;
		[FieldOffset(22)] public byte Unk2;
		[FieldOffset(23)] public byte Unk3;

		public readonly ulong Flags => ChildType | ((ulong)Unk1 << 32) | ((ulong)Unk2 << 48) | ((ulong)Unk3 << 56);
	}
}
