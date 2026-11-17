using System.IO;

using System;

namespace RainbowForge.RenderPipeline
{
	public class Material
	{
		public TextureSelector[] BaseTextureMapSpecs { get; }
		public TextureSelector[] SecondaryTextureMapSpecs { get; }
		public TextureSelector[] TertiaryTextureMapSpecs { get; }
		public byte[] BaseUvTransform { get; private init; }
		public byte[] SecondaryUvTransform { get; private init; }

		private Material(TextureSelector[] baseTextureMapSpecs, TextureSelector[] secondaryTextureMapSpecs, TextureSelector[] tertiaryTextureMapSpecs)
		{
			BaseTextureMapSpecs = baseTextureMapSpecs;
			SecondaryTextureMapSpecs = secondaryTextureMapSpecs;
			TertiaryTextureMapSpecs = tertiaryTextureMapSpecs;
		}

		public static Material Read(BinaryReader r, uint version = 33)
		{
			var start = r.BaseStream.Position;
			var magic1 = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.Material, magic1);

			var baseCount = 5;
			if (version == 29)
			{
				// v29 has a variable-length parameter prefix, then four selectors.
				var remaining = r.BaseStream.Length - r.BaseStream.Position;
				var offset = FindLegacySelectors(r.ReadBytes((int)Math.Min(remaining, 1024)));
				if (offset < 0)
					throw new InvalidDataException("Legacy material selector block was not found.");
				r.BaseStream.Position = start + 4 + offset;
				baseCount = 4;
			}
			var baseMipContainers = new TextureSelector[baseCount];
			for (var i = 0; i < baseMipContainers.Length; i++)
				baseMipContainers[i] = TextureSelector.Read(r);

			var padding = r.ReadUInt64();

			var magic2 = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.UVTransform, magic2);

			var uvTransform1 = r.ReadBytes(version == 29 ? 43 : 51);

			var secondaryMipContainers = new TextureSelector[3];
			for (var i = 0; i < secondaryMipContainers.Length; i++)
				secondaryMipContainers[i] = TextureSelector.Read(r);

			var padding2 = r.ReadUInt64();

			var magic3 = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.UVTransform, magic3);

			var uvTransform2 = r.ReadBytes(version == 29 ? 43 : 51);

			var internalUid1 = r.ReadUInt64();
			var magic4 = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.DetailMapDescriptor, magic4);

			var detailLength = 16;
			if (version == 29)
			{
				var detailStart = r.BaseStream.Position;
				if (r.BaseStream.Length - detailStart >= 32)
				{
					r.BaseStream.Position = detailStart + 28;
					if (r.ReadUInt32() == (uint)Magic.DetailMapDescriptor)
						detailLength = 20;
					r.BaseStream.Position = detailStart;
				}
			}
			var detailMapDesc1 = r.ReadBytes(detailLength);

			var internalUid2 = r.ReadUInt64();
			var magic5 = r.ReadUInt32();
			MagicHelper.AssertEquals(Magic.DetailMapDescriptor, magic5);

			var detailMapDesc2 = r.ReadBytes(detailLength);

			var tertiaryMipContainers = new TextureSelector[2];
			for (var i = 0; i < tertiaryMipContainers.Length; i++)
				tertiaryMipContainers[i] = TextureSelector.Read(r);

			return new Material(baseMipContainers, secondaryMipContainers, tertiaryMipContainers)
			{
				BaseUvTransform = uvTransform1,
				SecondaryUvTransform = uvTransform2
			};
		}

		private static int FindLegacySelectors(ReadOnlySpan<byte> bytes)
		{
			const int selectorSize = 28;
			const int selectorCount = 4;
			for (var offset = 0; offset <= bytes.Length - selectorCount * selectorSize - 12; offset++)
			{
				if (BitConverter.ToUInt32(bytes[(offset + 8)..]) != (uint)Magic.TextureSelector ||
				    BitConverter.ToUInt32(bytes[(offset + selectorCount * selectorSize + 8)..]) != (uint)Magic.UVTransform)
					continue;
				var matches = true;
				for (var slot = 1; slot < selectorCount; slot++)
					matches &= BitConverter.ToUInt32(bytes[(offset + slot * selectorSize + 8)..]) == (uint)Magic.TextureSelector;
				if (matches)
					return offset;
			}
			return -1;
		}
	}
}
