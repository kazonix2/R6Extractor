using System;
using System.IO;
using RainbowForge.Core.DataBlock;

namespace RainbowForge.Core.Container
{
	public class ForgeAsset : ForgeContainer
	{
		public IAssetBlock MetaBlock { get; }
		public ulong AssetBlockMagic { get; }
		public IAssetBlock AssetBlock { get; }

		public bool HasMeta => MetaBlock != null;

		private ForgeAsset(IAssetBlock metaBlock, ulong assetBlockMagic, IAssetBlock assetBlock)
		{
			AssetBlockMagic = assetBlockMagic;
			MetaBlock = metaBlock;
			AssetBlock = assetBlock;
		}

		public static ForgeAsset Read(BinaryReader r, Entry entry) => Read(r, entry, requireVerifiedEnvelope: false);

		internal static ForgeAsset Read(BinaryReader r, Entry entry, bool requireVerifiedEnvelope)
		{
			if (entry.HasExactChunkBounds || requireVerifiedEnvelope)
			{
				// GetContainer has already read the low word of the first block magic.
				r.BaseStream.Position = entry.Offset;
				var first = ChunkedDataBlock.TryReadVerifiedEnvelope(r, entry.End, out _);
				if (first != null)
				{
					if (r.BaseStream.Position == entry.End)
						return new ForgeAsset(null, 0, first);
					var second = ChunkedDataBlock.TryReadVerifiedEnvelope(r, entry.End, out var secondMagic);
					if (second == null || r.BaseStream.Position != entry.End)
						throw new InvalidDataException("The second block does not fill the Forge allocation.");
					return new ForgeAsset(first, secondMagic, second);
				}
				if (requireVerifiedEnvelope)
					throw new InvalidDataException("Unsupported LZO chunk envelope.");
				r.BaseStream.Position = entry.Offset + sizeof(uint);
			}

			var magic = r.ReadUInt32();
			// MagicHelper.AssertEquals(Magic.FileContainer, magic);

			var end = entry.End;

			var blockA = GetAssetBlock(r, entry);

			if (r.BaseStream.Position >= end)
				return new ForgeAsset(null, 0, blockA);

			var assetBlockMagic = r.ReadUInt64(); // this might actually be 8 shorts and 16 0x00 bytes

			var blockB = GetAssetBlock(r, entry);

			return new ForgeAsset(blockA, assetBlockMagic, blockB);
		}

		private static IAssetBlock GetAssetBlock(BinaryReader r, Entry entry)
		{
			var x = r.ReadUInt16(); // 2 (changed to 3 in Y5) <-- container deserializer type
			var assetDeserializerType = r.ReadUInt16(); // 3 for chunked, 7 for linear (flags?)
			var y = r.ReadByte(); // 0
			var z = r.ReadUInt16();

			return assetDeserializerType switch
			{
				3 => ChunkedDataBlock.Read(r),
				13 => ChunkedDataBlock.Read(r, true),
				14 => ChunkedDataBlock.Read(r, true),
                15 => ChunkedDataBlock.Read(r, true),
				7 => FlatDataBlock.Read(r, entry),
                _ => throw new NotImplementedException()
			};
		}

		public BinaryReader GetDataStream(Forge forge)
		{
			lock (forge.Stream)
				return new BinaryReader(AssetBlock.GetDataStream(forge.Stream));
		}

		public BinaryReader GetDataPrefixStream(Forge forge, int maxBytes)
		{
			lock (forge.Stream)
				return new BinaryReader(AssetBlock.GetDataPrefixStream(forge.Stream, maxBytes));
		}

		public BinaryReader GetMetaDataStream(Forge forge)
		{
			if (!HasMeta) throw new InvalidOperationException("This Forge asset does not contain a metadata block.");
			lock (forge.Stream)
				return new BinaryReader(MetaBlock.GetDataStream(forge.Stream));
		}
	}
}
