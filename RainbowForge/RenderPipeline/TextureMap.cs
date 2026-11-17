using System.IO;

namespace RainbowForge.RenderPipeline
{
	public class TextureMap
	{
		public uint Var1 { get; }
		public uint Var2 { get; }
		public uint Var3 { get; }
		public uint Var4 { get; }
		public byte[] Data { get; }
		public ulong[] TexUidMipSet1 { get; }
		public ulong[] TexUidMipSet2 { get; }
		public byte[] Data2 { get; }

		private TextureMap(uint var1, uint var2, uint var3, uint var4, byte[] data, ulong[] texUidMipSet1, ulong[] texUidMipSet2, byte[] data2)
		{
			Var1 = var1;
			Var2 = var2;
			Var3 = var3;
			Var4 = var4;
			Data = data;
			TexUidMipSet1 = texUidMipSet1;
			TexUidMipSet2 = texUidMipSet2;
			Data2 = data2;
		}

		public static TextureMap Read(BinaryReader r, uint version = 33)
		{
			var start = r.BaseStream.Position;
			var magic = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.TextureMap, magic);

			// August Closed Alpha stores five compiled mip UIDs after its resource path.
			if (version == 29 && r.BaseStream.Length - start >= 170)
			{
				r.BaseStream.Position = start + 108;
				var pathLength = r.ReadUInt32();
				if (pathLength > 0 && r.BaseStream.Length - start == 170L + pathLength &&
				    r.ReadByte() == '/')
				{
					var mips = ReadMipUids(r, r.BaseStream.Length - 41);
					return new TextureMap(0, 0, 0, 0, [], [], mips, []);
				}
				r.BaseStream.Position = start + sizeof(uint);
			}

			if (version == 29 && r.BaseStream.Length - start == 157)
			{
				var first = ReadMipUids(r, start + 8);
				var second = ReadMipUids(r, start + 116);
				return new TextureMap(0, 0, 0, 0, [], first, second, []);
			}

			var var1 = r.ReadUInt32();
			var var2 = r.ReadUInt32();
			var var3 = r.ReadUInt32();
			var var4 = r.ReadUInt32();

			var data = r.ReadBytes(52);

			var texUidMipSet1 = new ulong[5];
			for (var i = 0; i < 5; i++)
				texUidMipSet1[i] = r.ReadUInt64();

			var texUidMipSet2 = new ulong[5];
			for (var i = 0; i < 5; i++)
				texUidMipSet2[i] = r.ReadUInt64();

			var data2 = r.ReadBytes(32);

			return new TextureMap(var1, var2, var3, var4, data, texUidMipSet1, texUidMipSet2, data2);
		}

		private static ulong[] ReadMipUids(BinaryReader reader, long offset)
		{
			reader.BaseStream.Position = offset;
			var uids = new ulong[5];
			for (var i = 0; i < uids.Length; i++)
				uids[i] = reader.ReadUInt64();
			return uids;
		}
	}
}
