using System.IO;
using System.Text;

namespace RainbowForge.Core.Container
{
	public class Hash : ForgeContainer
	{
		public ulong Hash1 { get; }
		public ulong Hash2 { get; }
		public string Name { get; }

		private Hash(ulong hash1, ulong hash2 = 0, string name = "")
		{
			Hash1 = hash1;
			Hash2 = hash2;
			Name = name;
		}

		internal static Hash ReadLegacyFastLoad(BinaryReader r, Entry entry)
		{
			if (entry.Size is not (20 or 112) || entry.End > r.BaseStream.Length)
				throw new InvalidDataException("Unexpected legacy fast-load record size.");
			var hash = r.ReadUInt64();
			if (r.ReadUInt32() != 0 || r.ReadUInt32() != (entry.Size == 20 ? 0U : 1U))
				throw new InvalidDataException("Unexpected legacy fast-load record flags.");
			if (entry.Size == 20)
				return new Hash(hash);

			var name = Encoding.ASCII.GetString(r.ReadBytes(64)).TrimEnd('\0');
			return new Hash(hash, name: name);
		}

		internal static Hash ReadAprilFastLoad(BinaryReader r, Entry entry)
		{
			if (entry.Size < 20 || entry.End > r.BaseStream.Length)
				throw new InvalidDataException("Truncated April Alpha fast-load record.");
			var hash = r.ReadUInt64();
			var nameFlag = r.ReadUInt32();
			if (nameFlag == 0)
				return new Hash(hash);
			if (nameFlag != 1 || entry.Size < 80)
				throw new InvalidDataException("Invalid April Alpha fast-load name.");
			return new Hash(hash, name: Encoding.ASCII.GetString(r.ReadBytes(64)).TrimEnd('\0'));
		}

		public static Hash Read(BinaryReader r)
		{
			var hash1 = r.ReadUInt64(); // [0x0]

			var continueFlag = r.ReadUInt32(); // [0x8]
			if (continueFlag == 0) // switch value, controls whether there is more metadata
				return new Hash(hash1);

			var hash2 = r.ReadUInt64(); // [0xC]

			continueFlag = r.ReadUInt32(); // [0x14]
			if (continueFlag == 0)
				return new Hash(hash1, hash2);

			var nameBytes = r.ReadBytes(0x40);
			var name = Encoding.UTF8.GetString(nameBytes); // [0x18]
			var x58 = r.ReadUInt64(); // [0x58]
			var x60 = r.ReadUInt64(); // [0x60]

			continueFlag = r.ReadUInt32(); // [0x68]
			if (continueFlag == 0)
				return new Hash(hash1, hash2, name);

			var x6C = r.ReadUInt64(); // [0x6C]
			var x74 = r.ReadUInt64(); // [0x74]

			continueFlag = r.ReadUInt32(); // [0x7C]
			if (continueFlag == 0)
				return new Hash(hash1, hash2, name);

			var x80 = r.ReadUInt64(); // [0x80]

			return new Hash(hash1, hash2, name);
		}
	}
}
