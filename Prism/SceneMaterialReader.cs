using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK;
using Prism.Render;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Image;
using RainbowForge.RenderPipeline;

namespace Prism;

internal sealed class SceneMaterialReader(FlatArchive archive, Stream payload, uint version)
{
	internal sealed record Map(int Layer, int Slot, TextureType Type, ulong SpecUid, ulong MapUid, ulong[] TextureUids);
	internal sealed record Binding(byte[] BaseUvTransform, byte[] SecondaryUvTransform, Map[] Maps)
	{
		public Vector3 Tint { get; init; } = Vector3.One;
		public bool Glass { get; init; }
		public bool AlphaTest { get; init; }
		public OpenTK.Vector2 UvScale(uint version, int layer = 0)
		{
			var transform = layer == 0 ? BaseUvTransform : SecondaryUvTransform;
			// The v29 base block stores its U and V scales at offsets 4 and 8.
			if (version != 29 || transform.Length != 43)
				return OpenTK.Vector2.One;
			var u = BitConverter.ToSingle(transform, 4);
			var v = BitConverter.ToSingle(transform, 8);
			return float.IsFinite(u) && float.IsFinite(v) && u != 0 && v != 0
				? new OpenTK.Vector2(u, v) : OpenTK.Vector2.One;
		}

		public IEnumerable<ulong> Candidates(TextureType type, int layer = 0) => Maps
			.Where(map => map.Layer == layer && map.Type == type)
			.SelectMany(map => map.TextureUids).Distinct();

		public IEnumerable<ulong> DiffuseCandidates() =>
			Candidates(TextureType.Diffuse).Concat(Candidates(TextureType.Misc)).Distinct();
	}

	private readonly Dictionary<ulong, FlatArchiveEntry> _entries = archive.Entries
		.GroupBy(entry => entry.MetaData.Uid).ToDictionary(group => group.Key, group => group.First());
	private readonly Dictionary<ulong, Binding> _cache = new();
	private const ulong GlassShader = 0x523BA2BF;
	private const ulong TranslucentFoliageShader = 0x3F79AE19;

	internal static SceneTextureData.AlphaMode GetPreviewOpacity(SceneTextureData diffuse)
	{
		// BC1A explicitly reserves one-bit coverage. General-purpose RGBA
		// alpha can carry height or another shader input and needs a material
		// render-state flag before it can be used for blending.
		return diffuse is { Format: 0x3, Alpha: not SceneTextureData.AlphaMode.Opaque }
			? SceneTextureData.AlphaMode.Cutout : SceneTextureData.AlphaMode.Opaque;
	}

	internal static SceneTextureData.AlphaMode ResolveOpacity(Binding material, SceneTextureData diffuse) =>
		material.Glass ? SceneTextureData.AlphaMode.Blend :
		material.AlphaTest && diffuse is { Alpha: not SceneTextureData.AlphaMode.Opaque }
			? SceneTextureData.AlphaMode.Cutout : GetPreviewOpacity(diffuse);

	public Binding Read(ulong materialUid)
	{
		if (_cache.TryGetValue(materialUid, out var cached))
			return cached;
		if (version >= 34 && _entries.TryGetValue(materialUid, out var entry) &&
		    entry.MetaData.FileType == (uint)Magic.MaterialY11S3)
			return _cache[materialUid] = ReadY11S3(materialUid);
		using var materialStream = Open(materialUid, Magic.Material);
		if (materialStream == null)
			return _cache[materialUid] = null;
		var start = materialStream.BaseStream.Position;
		var prefix = materialStream.ReadBytes(100);
		materialStream.BaseStream.Position = start;
		var material = Material.Read(materialStream, version);
		// April stores the shader UID four bytes earlier than August.
		var shader = 0UL;
		if (version == 29 && prefix.Length >= 95)
		{
			shader = BitConverter.ToUInt64(prefix, 83);
			if (shader is not (GlassShader or TranslucentFoliageShader))
				shader = BitConverter.ToUInt64(prefix, 87);
		}
		var layers = new[] { material.BaseTextureMapSpecs, material.SecondaryTextureMapSpecs, material.TertiaryTextureMapSpecs };
		var maps = new List<Map>();
		for (var layer = 0; layer < layers.Length; layer++)
		for (var slot = 0; slot < layers[layer].Length; slot++)
		{
			var specUid = layers[layer][slot].TextureMapSpecUid;
			using var specStream = Open(specUid, Magic.TextureMapSpec);
			if (specStream == null)
				continue;
			var spec = TextureMapSpec.Read(specStream);
			using var mapStream = Open(spec.TextureMapUid, Magic.TextureMap);
			if (mapStream == null)
				continue;
			if (version == 29 && Texture.TryReadAprilInline(mapStream, out _))
			{
				maps.Add(new Map(layer, slot, (TextureType)spec.TextureType, specUid, spec.TextureMapUid, [spec.TextureMapUid]));
				continue;
			}
			var map = TextureMap.Read(mapStream, version);
			var uids = map.TexUidMipSet2.Concat(map.TexUidMipSet1)
				.Where(uid => uid != 0 && (!_entries.TryGetValue(uid, out var entry) ||
					MagicHelper.GetFiletype(entry.MetaData.FileType) == AssetType.Texture)).Distinct().ToArray();
			maps.Add(new Map(layer, slot, (TextureType)spec.TextureType, specUid, spec.TextureMapUid, uids));
		}
		return _cache[materialUid] = new Binding(material.BaseUvTransform, material.SecondaryUvTransform, maps.ToArray())
		{
			Tint = version == 29 ? ReadTint(prefix, 4) : Vector3.One,
			Glass = shader == GlassShader,
			AlphaTest = shader == TranslucentFoliageShader
		};
	}

	public bool TryReadInlineTexture(ulong uid, out Texture texture, out byte[] surface)
	{
		texture = null;
		surface = null;
		if (version != 29)
			return false;
		using var reader = Open(uid, Magic.TextureMap);
		if (reader == null || !Texture.TryReadAprilInline(reader, out texture))
			return false;
		surface = texture.ReadSurfaceBytes(reader);
		return true;
	}

	internal static Vector3 ReadTint(byte[] data, int offset)
	{
		if (data.Length - offset < 16)
			return Vector3.One;
		var tint = new Vector3(BitConverter.ToSingle(data, offset), BitConverter.ToSingle(data, offset + 4),
			BitConverter.ToSingle(data, offset + 8));
		return tint.X is >= 0 and <= 1 && tint.Y is >= 0 and <= 1 && tint.Z is >= 0 and <= 1
			? tint : Vector3.One;
	}

	private Binding ReadY11S3(ulong uid)
	{
		using var stream = Open(uid, Magic.MaterialY11S3);
		var bytes = stream.ReadBytes(checked((int)(stream.BaseStream.Length - stream.BaseStream.Position)));
		var maps = new List<Map>();
		var selectors = new HashSet<(uint Layer, uint Slot, ulong Image)>();
		// A selector is 28 bytes, ending in layer, slot and the image UID.
		for (var offset = 20; offset <= bytes.Length - sizeof(ulong); offset++)
		{
			var imageUid = BitConverter.ToUInt64(bytes, offset);
			if (!_entries.TryGetValue(imageUid, out var imageEntry) || imageEntry.MetaData.FileType != (uint)Magic.PhoenixImage)
				continue;
			var layer = BitConverter.ToUInt32(bytes, offset - 8);
			var slot = BitConverter.ToUInt32(bytes, offset - 4);
			if (layer > 2 || slot > 9 || !selectors.Add((layer, slot, imageUid)))
				continue;
			using var image = Open(imageUid, Magic.PhoenixImage);
			if (image.BaseStream.Length - image.BaseStream.Position != 19)
				continue;
			image.BaseStream.Position += 11;
			var mapUid = image.ReadUInt64();
			using var texture = Open(mapUid, Magic.PhoenixTexture);
			if (texture == null || texture.BaseStream.Length - texture.BaseStream.Position != 225)
				continue;
			texture.BaseStream.Position += 57;
			if (texture.ReadUInt32() != 0x6F697D77)
				continue;
			texture.BaseStream.Position += 44;
			var candidates = new ulong[10];
			for (var i = 0; i < candidates.Length; i++)
				candidates[i] = texture.ReadUInt64();
			maps.Add(new Map((int)layer, (int)slot, (TextureType)slot, imageUid, mapUid,
				candidates.Where(candidate => candidate != 0).Distinct().ToArray()));
		}
		var shader = bytes.Length >= 12 ? BitConverter.ToUInt64(bytes, 4) : 0;
		return new Binding([], [], maps.ToArray())
		{
			Tint = ReadTint(bytes, 12),
			Glass = shader == GlassShader,
			AlphaTest = shader == TranslucentFoliageShader
		};
	}

	private BinaryReader Open(ulong uid, Magic type) =>
		_entries.TryGetValue(uid, out var entry) && entry.MetaData.FileType == (uint)type
			? archive.GetEntryPayloadStream(payload, uid, version) : null;
}
