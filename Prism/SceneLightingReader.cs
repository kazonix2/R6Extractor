using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OpenTK;
using Prism.Render;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Core;
using RainbowForge.Core.Container;

namespace Prism;

internal static class SceneLightingReader
{
	internal sealed record EntitySceneData(
		Matrix4[] Transforms,
		SceneLight[] Lights,
		Dictionary<ulong, Matrix4[]> AssetTransforms);

	public static BakedGiLighting TryReadBakedGi(string mapForgePath, CancellationToken cancellationToken)
	{
		var giPath = Path.Combine(Path.GetDirectoryName(mapForgePath) ?? string.Empty,
			Path.GetFileNameWithoutExtension(mapForgePath) + "_bnk_gidata.forge");
		if (!File.Exists(giPath))
			return BakedGiLighting.Default;

		var forge = Forge.GetForge(giPath);
		try
		{
			foreach (var entry in forge.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (AssetTypeDetector.DetectMagic(forge, entry, inspectPayload: true) != (ulong)Magic.GIStream
				    || forge.GetContainer(entry.Uid) is not ForgeAsset asset)
					continue;

				using var data = asset.GetDataStream(forge);
				var archive = FlatArchive.Read(forge, asset, data);
				var streamEntry = archive.Entries.FirstOrDefault(candidate => candidate.MetaData.FileType == (uint)Magic.GIStream)
				                  ?? archive.Entries.FirstOrDefault();
				if (streamEntry == null)
					continue;
				using var stream = archive.GetEntryPayloadStream(data.BaseStream, streamEntry.MetaData.Uid, forge.Version);
				if (stream.ReadUInt32() != (uint)Magic.GIStream)
					continue;
				var primaryBytes = stream.ReadUInt32();
				var primaryStart = stream.BaseStream.Position;
				if (primaryStart + primaryBytes > stream.BaseStream.Length)
					continue;

				// GIStream coefficient semantics are only partly reversed. Sample the
				// established 12 x u16 + u64 records and derive a stable, conservative
				// RGB ambient term. Raw counts remain available for future spatial probes.
				var recordCount = (long)primaryBytes / 32;
				var sampleCount = Math.Min(recordCount, 16384);
				var stride = Math.Max(1L, recordCount / Math.Max(1, sampleCount));
				double r = 0, g = 0, b = 0;
				long accepted = 0;
				var coefficient = new ushort[12];
				for (long record = 0; record < recordCount && accepted < sampleCount; record += stride)
				{
					cancellationToken.ThrowIfCancellationRequested();
					stream.BaseStream.Position = primaryStart + record * 32;
					for (var index = 0; index < coefficient.Length; index++)
						coefficient[index] = stream.ReadUInt16();
					stream.ReadUInt64();
					var localMax = coefficient.ToArray().Max();
					if (localMax > 4096)
						continue; // outside the currently understood coefficient-record region
					r += (coefficient[0] + coefficient[3] + coefficient[6] + coefficient[9]) / 4d;
					g += (coefficient[1] + coefficient[4] + coefficient[7] + coefficient[10]) / 4d;
					b += (coefficient[2] + coefficient[5] + coefficient[8] + coefficient[11]) / 4d;
					accepted++;
				}

				if (accepted == 0)
					return BakedGiLighting.Default with { SourcePath = giPath };
				var rgb = new Vector3((float)(r / accepted), (float)(g / accepted), (float)(b / accepted));
				var peak = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
				var color = peak > 0.001f ? rgb / peak : Vector3.One;
				color = new Vector3(Math.Clamp(color.X, 0.35f, 1f), Math.Clamp(color.Y, 0.35f, 1f), Math.Clamp(color.Z, 0.35f, 1f));
				var intensity = Math.Clamp(0.25f + peak / 768f, 0.3f, 0.8f);
				var tailBytes = stream.BaseStream.Length - primaryStart - primaryBytes;
				var tailCells = tailBytes >= 84 && (tailBytes - 84) % 4 == 0 ? (tailBytes - 84) / 4 : 0;
				return new BakedGiLighting(color, intensity, accepted, tailCells, giPath);
			}
		}
		finally
		{
			forge.Stream?.Dispose();
		}

		return BakedGiLighting.Default;
	}

	public static EntitySceneData ReadEntitiesAndLights(
		Forge forge, FlatArchive archive, Stream payload)
	{
		var definitions = ReadLightDefinitions(forge, archive, payload);
		var referencedAssets = archive.Entries
			.Where(entry => entry.MetaData.FileType == (uint)Magic.Mesh)
			.Select(entry => (entry.MetaData.Uid, Needle: BitConverter.GetBytes(entry.MetaData.Uid)))
			.ToArray();
		var assetTransforms = new Dictionary<ulong, List<Matrix4>>();
		var transforms = new List<Matrix4>();
		var lights = new List<SceneLight>();
		foreach (var entity in archive.Entries.Where(entry => entry.MetaData.FileType == (uint)Magic.Entity))
		{
			try
			{
				using var stream = archive.GetEntryPayloadStream(payload, entity.MetaData.Uid, forge.Version);
				var bytes = stream.ReadBytes(checked((int)(stream.BaseStream.Length - stream.BaseStream.Position)));
				if (bytes.Length < 69 || BitConverter.ToUInt32(bytes, 0) != (uint)Magic.Entity)
					continue;
				var matrix = ReadEntityMatrix(bytes);
				transforms.Add(matrix);
				foreach (var asset in referencedAssets)
				{
					if (bytes.AsSpan().IndexOf(asset.Needle) < 0)
						continue;
					if (!assetTransforms.TryGetValue(asset.Uid, out var placements))
						assetTransforms[asset.Uid] = placements = new List<Matrix4>();
					placements.Add(matrix);
				}
				foreach (var definition in definitions)
				{
					if (bytes.AsSpan().IndexOf(BitConverter.GetBytes(definition.Key)) < 0)
						continue;
					var position = new Vector3(matrix.M41, matrix.M42, matrix.M43);
					var direction = Vector3.Normalize(new Vector3(-matrix.M31, -matrix.M32, -matrix.M33));
					var item = definition.Value;
					lights.Add(item with { Position = position, Direction = direction });
				}
			}
			catch
			{
			}
		}
		return new EntitySceneData(
			transforms.Count > 0 ? transforms.ToArray() : [Matrix4.Identity],
			lights.ToArray(),
			assetTransforms.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
	}

	private static Dictionary<ulong, SceneLight> ReadLightDefinitions(Forge forge, FlatArchive archive, Stream payload)
	{
		var result = new Dictionary<ulong, SceneLight>();
		foreach (var entry in archive.Entries.Where(entry => entry.MetaData.FileType is (uint)Magic.OmniLight or (uint)Magic.SpotLight))
		{
			try
			{
				using var stream = archive.GetEntryPayloadStream(payload, entry.MetaData.Uid, forge.Version);
				var bytes = stream.ReadBytes(checked((int)(stream.BaseStream.Length - stream.BaseStream.Position)));
				if (bytes.Length < 49 || BitConverter.ToUInt32(bytes, 0) != entry.MetaData.FileType)
					continue;
				var color = new Vector3(SafeFloat(bytes, 20, 1f), SafeFloat(bytes, 24, 1f), SafeFloat(bytes, 28, 1f));
				color = new Vector3(Math.Clamp(color.X, 0f, 4f), Math.Clamp(color.Y, 0f, 4f), Math.Clamp(color.Z, 0f, 4f));
				var intensity = Math.Clamp(SafeFloat(bytes, 32, 1f), 0.05f, 16f);
				var kind = entry.MetaData.FileType == (uint)Magic.SpotLight ? SceneLightKind.Spot : SceneLightKind.Omni;
				result[entry.MetaData.Uid] = new SceneLight(entry.MetaData.Uid, kind, Vector3.Zero, -Vector3.UnitZ,
					color, intensity, kind == SceneLightKind.Spot ? 24f : 18f, kind == SceneLightKind.Spot ? 0.72f : -1f);
			}
			catch
			{
			}
		}
		return result;
	}

	private static Matrix4 ReadEntityMatrix(byte[] bytes)
	{
		var values = new float[16];
		for (var index = 0; index < values.Length; index++)
			values[index] = BitConverter.ToSingle(bytes, 5 + index * 4);
		// Siege serializes the transform as four contiguous columns. OpenTK's
		// Matrix4 backing layout uses the same 16-value order for GL upload, so
		// transposing here moves Tx/Ty/Tz into M14/M24/M34 and makes every M41/M42/M43
		// position appear to be zero. Preserve the serialized order instead.
		return new Matrix4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
			values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
	}

	private static float SafeFloat(byte[] bytes, int offset, float fallback)
	{
		var value = BitConverter.ToSingle(bytes, offset);
		return float.IsFinite(value) ? value : fallback;
	}
}
