using System;
using System.IO;

namespace RainbowForge.Image
{
	public class Texture
	{
		public FileMetaData MetaData { get; }
		public long DataStart { get; }
		public long DdsStart { get; }
		public uint TexFormat { get; }
		public TextureType TexType { get; }
		public uint ContainerId { get; }
		public ushort NumBlocks { get; }
		public long Texsize { get; }
		public uint Chan { get; }
		public uint Mips { get; }
		public int Width { get; }
		public int Height { get; }
		public int Data1 { get; }
		public int Data2 { get; }

		private Texture(FileMetaData metaData, long dataStart, long ddsStart, uint texFormat, TextureType texType, uint containerId, ushort numBlocks, long texsize, uint chan, uint mips, int width,
			int height, int data1, int data2)
		{
			MetaData = metaData;
			DataStart = dataStart;
			DdsStart = ddsStart;
			TexFormat = texFormat;
			TexType = texType;
			ContainerId = containerId;
			NumBlocks = numBlocks;
			Texsize = texsize;
			Chan = chan;
			Mips = mips;
			Width = width;
			Height = height;
			Data1 = data1;
			Data2 = data2;
		}

		public static Texture Read(BinaryReader r, uint version)
		{
			var header = FileMetaData.Read(r, version);

			var secondMagic = r.ReadUInt32();
			var var2 = r.ReadUInt32();
			var var3 = r.ReadUInt32();

			var magic = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.CompiledTextureMap, magic);

			var dataStart = r.BaseStream.Position;
			var texFormat = 0u;
			var texType = (TextureType)0; ;
            if (version < 32)
			{
				texFormat = r.ReadUInt32(); // [0x00]
				var x04 = r.ReadUInt32(); // 1
				var x08 = r.ReadUInt32();
				texType = (TextureType)r.ReadUInt32(); // [0x0C] see docstring for details
				var x10 = r.ReadUInt32();
				var x14 = r.ReadUInt32();
				var x18 = r.ReadUInt32();
				var x1C = r.ReadUInt32(); // 0
			}
			var x20 = r.ReadUInt32(); // 0
			var x24 = r.ReadUInt32(); // 0
			var containerId = r.ReadUInt32(); // [0x28] container id
			MagicHelper.AssertEquals(Magic.CompiledTextureMapData, containerId);

			var pixelInfo = r.ReadUInt32();
			var x30 = r.ReadUInt32(); // 7
			var ddsStart = r.BaseStream.Position;
			// April Alpha serializes the pixel block length here and has a 42-byte footer.
			var aprilLayout = version == 29 && pixelInfo >= 4 && ddsStart + pixelInfo - 4 == r.BaseStream.Length - 42;
			var data1 = aprilLayout ? 0 : (byte)pixelInfo;
			var numBlocks = aprilLayout ? (ushort)0 : (ushort)(pixelInfo >> 8);
			var data2 = aprilLayout ? 0 : (byte)(pixelInfo >> 24);
			var footerSize = version >= 32 ? 0x49 : aprilLayout ? 42 : 0x29;
			var end = r.BaseStream.Length - footerSize;
			if (end < ddsStart)
				throw new InvalidDataException("Texture pixel data extends into its footer.");
			r.BaseStream.Position = end;
			var texsize = end - ddsStart;

			var w = r.ReadUInt32();
			var h = r.ReadUInt32();

            // eN notation is same hex offset, e just means 'end' because those
            // values go after the actual dds blob
            var e8 = r.ReadUInt32(); // 1
            var eC = r.ReadUInt32(); // 0
            var chan = r.ReadUInt32();
            var e14 = r.ReadUInt32();
            var mips = r.ReadUInt32();
            var e1C = r.ReadUInt32();
            if (version >= 32)
            {
                texFormat = r.ReadUInt32(); // [0x00]
                var x04 = r.ReadUInt32(); // 1
                var x08 = r.ReadUInt32();
                texType = (TextureType)r.ReadUInt32(); // [0x0C] see docstring for details
                var x10 = r.ReadUInt32();
                var x14 = r.ReadUInt32();
                var x18 = r.ReadUInt32();
                var x1C = r.ReadUInt32(); // 0
            }
			if (aprilLayout)
				r.ReadByte();
            var e20 = r.ReadUInt32(); // 7
			var e24 = r.ReadUInt32(); // 7
			var e28 = r.ReadByte(); // 1

			r.BaseStream.Seek(ddsStart, SeekOrigin.Begin);

			var powChan = Math.Pow(2, chan);
			var width = (int)Math.Floor(w / powChan);
			var height = (int)Math.Floor(h / powChan);

			return new Texture(header, dataStart, ddsStart, texFormat, texType, containerId, numBlocks, texsize, chan, mips, width, height, data1, data2);
		}

		public byte[] ReadSurfaceBytes(BinaryReader r)
		{
			r.BaseStream.Seek(DdsStart, SeekOrigin.Begin);
			return r.ReadBytes((int)Texsize);
		}

		// April includes a low-resolution mip chain directly in the TextureMap body.
		public static bool TryReadAprilInline(BinaryReader reader, out Texture texture)
		{
			texture = null;
			var stream = reader.BaseStream;
			var start = stream.Position;
			try
			{
				if (stream.Length - start < 119 || reader.ReadUInt32() != (uint)Magic.TextureMap)
					return false;
				stream.Position = start + 21;
				if (reader.ReadUInt32() != (uint)Magic.CompiledTextureMap)
					return false;
				var format = reader.ReadUInt32();
				stream.Position = start + 65;
				if (reader.ReadUInt32() != (uint)Magic.CompiledTextureMapData)
					return false;
				var blockLength = reader.ReadUInt32();
				var pixels = start + 77;
				var length = (long)blockLength - 4;
				if (length <= 0 || length > stream.Length - pixels - 42)
					return false;
				stream.Position = pixels + length;
				var width = reader.ReadInt32();
				var height = reader.ReadInt32();
				var depth = reader.ReadUInt32();
				stream.Position += 12;
				var mips = reader.ReadUInt32();
				if (width is < 1 or > 16384 || height is < 1 or > 16384 ||
				    depth != 1 || mips is < 1 or > 15)
					return false;
				texture = new Texture(null, start + 25, pixels, format, TextureType.Diffuse,
					(uint)Magic.CompiledTextureMapData, 0, length, 0, mips, width, height, 0, 0);
				return true;
			}
			finally
			{
				stream.Position = start;
			}
		}
	}
}
