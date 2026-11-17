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
	private const uint RenderComponent = 0xC5EA802B;
	private const uint DirectMeshReference = 0x89EBEBE5;
	private const uint LightParametersY11S3 = 0x0D6DBCA9;
	private const uint EntityCollectionY11S3 = 0x745FDD6D;

	internal sealed record EntitySceneData(
		SceneLight[] Lights,
		Dictionary<ulong, MeshPlacement[]> MeshPlacements);

	internal sealed record MeshPlacement(ulong EntityUid, string EntityName, Matrix4 Transform);

	public static BakedGiLighting TryReadBakedGi(string mapForgePath, CancellationToken cancellationToken)
	{
		var giPath = Path.Combine(Path.GetDirectoryName(mapForgePath) ?? string.Empty,
			Path.GetFileNameWithoutExtension(mapForgePath) + "_bnk_gidata.forge");
		if (!File.Exists(giPath))
			return BakedGiLighting.Default;

		try
		{
			var forge = Forge.GetForge(giPath);
			using var forgeStream = forge.Stream;
			var grids = new List<BakedGiGrid>();
			BakedGiLighting legacy = null;
			foreach (var entry in forge.Entries)
			{
				cancellationToken.ThrowIfCancellationRequested();
				try
				{
					if (forge.GetContainer(entry.Uid) is not ForgeAsset asset)
						continue;

					using var data = asset.GetDataStream(forge);
					var archive = FlatArchive.Read(forge, asset, data);
					var streamEntry = archive.Entries.FirstOrDefault(candidate =>
						candidate.MetaData.FileType is (uint)Magic.GIStream or (uint)Magic.GIStreamModern);
					if (streamEntry == null)
						continue;
					using var stream = archive.GetEntryPayloadStream(data.BaseStream, streamEntry.MetaData.Uid, forge.Version);
					var entryMagic = stream.ReadUInt32();
					if (entryMagic == (uint)Magic.GIStreamModern)
					{
						if (ReadGiGrid(stream) is { } grid)
							grids.Add(grid);
						continue;
					}
					if (legacy != null)
						continue;
					if (entryMagic != (uint)Magic.GIStream)
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
						var localMax = coefficient.Max();
						if (localMax > 4096)
							continue; // outside the currently understood coefficient-record region
						r += (coefficient[0] + coefficient[3] + coefficient[6] + coefficient[9]) / 4d;
						g += (coefficient[1] + coefficient[4] + coefficient[7] + coefficient[10]) / 4d;
						b += (coefficient[2] + coefficient[5] + coefficient[8] + coefficient[11]) / 4d;
						accepted++;
					}

					if (accepted == 0)
						continue;
					var rgb = new Vector3((float)(r / accepted), (float)(g / accepted), (float)(b / accepted));
					var peak = Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z));
					var color = peak > 0.001f ? rgb / peak : Vector3.One;
					color = new Vector3(Math.Clamp(color.X, 0.35f, 1f), Math.Clamp(color.Y, 0.35f, 1f), Math.Clamp(color.Z, 0.35f, 1f));
					var intensity = Math.Clamp(0.25f + peak / 768f, 0.3f, 0.8f);
					var tailBytes = stream.BaseStream.Length - primaryStart - primaryBytes;
					var tailCells = tailBytes >= 84 && (tailBytes - 84) % 4 == 0 ? (tailBytes - 84) / 4 : 0;
					legacy = new BakedGiLighting(color, intensity, accepted, tailCells, giPath);
				}
				catch (Exception error) when (error is IOException or InvalidDataException or
					NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
				{
					System.Diagnostics.Trace.WriteLine($"Skipped GI archive entry {entry.Uid:X16}: {error.Message}");
				}
			}
			return grids.Count > 0
				? BakedGiLighting.Default with { SourcePath = giPath, Grids = grids.ToArray() }
				: legacy ?? BakedGiLighting.Default with { SourcePath = giPath };
		}
		catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException)
		{
			System.Diagnostics.Trace.WriteLine($"Unable to open GI bank {giPath}: {error.Message}");
			return BakedGiLighting.Default with { SourcePath = giPath };
		}
	}

	// The modern stream stores a block-compressed 8x8 tile and a second
	// 16x16 tile of four bytes per texel for each cell, plus eight bytes per cell.
	// The second tile, per-cell metadata and volume transform are still unknown.
	internal static BakedGiGrid ReadGiGrid(BinaryReader reader)
	{
		const int bytesPerCell = 64;
		const int footerLength = 48;
		var stream = reader.BaseStream;
		var compressedLength = reader.ReadUInt32();
		var compressedStart = stream.Position;
		if (compressedLength == 0 || compressedLength % bytesPerCell != 0 ||
		    stream.Length - compressedStart != compressedLength * 17L + compressedLength / 8L + 8 + footerLength)
			return null;
		stream.Position += compressedLength;
		var auxiliaryLength = reader.ReadUInt32();
		if (auxiliaryLength != compressedLength * 16L)
			return null;
		stream.Position += auxiliaryLength;
		var cellDataLength = reader.ReadUInt32();
		if (cellDataLength != compressedLength / 8)
			return null;
		stream.Position += cellDataLength + footerLength - 20;
		var dimensions = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
		var storedCellCount = (int)(compressedLength / bytesPerCell);
		var volumeCellCount = (double)dimensions.X * dimensions.Y * dimensions.Z;
		if (!(dimensions.X >= 1 && dimensions.Y >= 1 && dimensions.Z >= 1) ||
		    dimensions.X != MathF.Truncate(dimensions.X) || dimensions.Y != MathF.Truncate(dimensions.Y) ||
		    dimensions.Z != MathF.Truncate(dimensions.Z) ||
		    volumeCellCount > storedCellCount || storedCellCount % volumeCellCount != 0 ||
		    reader.ReadUInt32() != 8 || reader.ReadUInt32() != 16)
			return null;

		// Sample the entire grid in storage order and keep each tile intact.
		var sampleCount = Math.Min(storedCellCount, 1024);
		var samples = new byte[sampleCount * bytesPerCell];
		for (var sample = 0; sample < sampleCount; sample++)
		{
			var cell = (2L * sample + 1) * storedCellCount / (2L * sampleCount);
			stream.Position = compressedStart + cell * bytesPerCell;
			stream.ReadExactly(samples.AsSpan(sample * bytesPerCell, bytesPerCell));
		}
		return new BakedGiGrid((int)volumeCellCount, samples);
	}

	public static EntitySceneData ReadEntitiesAndLights(
		Forge forge, FlatArchive archive, Stream payload, bool readLights = true, ulong? entityUid = null)
	{
		var definitions = readLights ? ReadLightDefinitions(forge, archive, payload) : new Dictionary<ulong, SceneLight>();
		var meshes = archive.Entries
			.Where(entry => IsMesh(entry.MetaData.FileType))
			.Select(entry => entry.MetaData.Uid).ToHashSet();
		var destructionGroups = new Dictionary<ulong, ulong[]>();
		foreach (var entry in archive.Entries.Where(entry => entry.MetaData.FileType is (uint)Magic.DestructionMeshGroup or (uint)Magic.DestructionMeshGroupY11S3))
		{
			using var reader = archive.GetEntryPayloadStream(payload, entry.MetaData.Uid, forge.Version);
			var bytes = reader.ReadBytes(checked((int)(reader.BaseStream.Length - reader.BaseStream.Position)));
			var members = ReadDestructionMeshGroup(bytes).Where(meshes.Contains).ToArray();
			if (members.Length > 0)
				destructionGroups[entry.MetaData.Uid] = members;
		}
		var placements = new Dictionary<ulong, List<MeshPlacement>>();
		var lights = new List<SceneLight>();
		foreach (var entity in archive.Entries.Where(entry =>
			(entry.MetaData.FileType is (uint)Magic.Entity or (uint)Magic.EntityY11S3) &&
			(!entityUid.HasValue || entry.MetaData.Uid == entityUid.Value)))
		{
			try
			{
				using var stream = archive.GetEntryPayloadStream(payload, entity.MetaData.Uid, forge.Version);
				var bytes = stream.ReadBytes(checked((int)(stream.BaseStream.Length - stream.BaseStream.Position)));
				if (!TryReadEntityMatrix(bytes, forge.Version, out var matrix))
					continue;
				foreach (var uid in FindEntityMeshes(bytes, forge.Version, meshes, destructionGroups))
				{
					if (!placements.TryGetValue(uid, out var instances))
						placements[uid] = instances = new List<MeshPlacement>();
					instances.Add(new MeshPlacement(entity.MetaData.Uid, entity.MetaData.FileName, matrix));
				}
				if (!readLights)
					continue;
				var position = new Vector3(matrix.M41, matrix.M42, matrix.M43);
				var direction = Vector3.Normalize(new Vector3(-matrix.M31, -matrix.M32, -matrix.M33));
				if (entity.MetaData.FileType == (uint)Magic.EntityY11S3)
				{
					if (ReadModernLight(bytes, entity.MetaData.Uid, position, direction) is { } light)
						lights.Add(light);
					continue;
				}
				foreach (var definition in definitions)
				{
					if (bytes.AsSpan().IndexOf(BitConverter.GetBytes(definition.Key)) < 0)
						continue;
					var item = definition.Value;
					lights.Add(item with { Position = position, Direction = direction });
				}
			}
			catch
			{
			}
		}
		foreach (var collection in archive.Entries.Where(entry => !entityUid.HasValue && entry.MetaData.FileType == EntityCollectionY11S3))
		{
			using var reader = archive.GetEntryPayloadStream(payload, collection.MetaData.Uid, forge.Version);
			var bytes = reader.ReadBytes(checked((int)(reader.BaseStream.Length - reader.BaseStream.Position)));
			foreach (var (uid, matrix) in ReadNestedDestructionPlacements(bytes, destructionGroups))
			{
				if (!placements.TryGetValue(uid, out var instances))
					placements[uid] = instances = new List<MeshPlacement>();
				if (!instances.Any(instance => NearlyEqual(instance.Transform, matrix)))
					instances.Add(new MeshPlacement(collection.MetaData.Uid, collection.MetaData.FileName, matrix));
			}
		}
		return new EntitySceneData(
			lights.ToArray(),
			placements.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
	}

	internal static IEnumerable<(ulong Uid, Matrix4 Matrix)> ReadNestedDestructionPlacements(
		byte[] bytes, Dictionary<ulong, ulong[]> groups)
	{
		if (bytes.Length < 68 || BitConverter.ToUInt32(bytes) != EntityCollectionY11S3)
			yield break;
		var matrix = default(Matrix4);
		var hasMatrix = false;
		// Embedded entities carry their own world matrix, followed by the same
		// render component link as standalone modern entities.
		for (var offset = 68; offset <= bytes.Length - 14; offset++)
		{
			var tag = BitConverter.ToUInt32(bytes, offset);
			if (tag == (uint)Magic.EntityY11S3 && offset <= bytes.Length - 68)
			{
				hasMatrix = TryReadMatrix(bytes, offset + 4, out matrix);
				offset += 67;
			}
			else if (hasMatrix && tag == RenderComponent &&
			         IsRenderComponentLink(bytes, offset + 6) &&
			         groups.TryGetValue(BitConverter.ToUInt64(bytes, offset + 6), out var members))
			{
				foreach (var uid in members)
					yield return (uid, matrix);
			}
		}
	}

	private static bool NearlyEqual(Matrix4 a, Matrix4 b) =>
		(a.Row0 - b.Row0).LengthSquared < 1e-8f && (a.Row1 - b.Row1).LengthSquared < 1e-8f &&
		(a.Row2 - b.Row2).LengthSquared < 1e-8f && (a.Row3 - b.Row3).LengthSquared < 1e-8f;

	internal static SceneLight ReadModernLight(byte[] bytes, ulong entityUid, Vector3 position, Vector3 direction)
	{
		const uint lightResourceReference = 0xCF25BE94;
		const int colorOffset = 101;
		const int powerOffset = 117;
		const int rangeOffset = 264;
		const int innerAngleOffset = 268;
		const int outerAngleOffset = 272;

		for (var offset = 68; offset <= bytes.Length - outerAngleOffset - sizeof(float); offset++)
		{
			if (!HasComponentTag(bytes, offset, LightParametersY11S3) ||
			    !HasComponentTag(bytes, offset - 21, lightResourceReference))
				continue;

			var color = new Vector3(BitConverter.ToSingle(bytes, offset + colorOffset),
				BitConverter.ToSingle(bytes, offset + colorOffset + 4),
				BitConverter.ToSingle(bytes, offset + colorOffset + 8));
			var power = BitConverter.ToSingle(bytes, offset + powerOffset);
			var range = BitConverter.ToSingle(bytes, offset + rangeOffset);
			var innerAngle = BitConverter.ToSingle(bytes, offset + innerAngleOffset);
			var outerAngle = BitConverter.ToSingle(bytes, offset + outerAngleOffset);
			if (!(color.X is >= 0 and <= 4 && color.Y is >= 0 and <= 4 && color.Z is >= 0 and <= 4 &&
			      power is > 0 and <= 1e8f && range is > 0 and <= 1000f &&
			      innerAngle >= 0 && outerAngle >= innerAngle && outerAngle <= MathF.PI))
				continue;

			// Point fixtures in this layout carry the default cone pair. Spot
			// fixtures override it with the authored angles in radians.
			var spot = innerAngle != 0.5f || outerAngle != 1f;
			var outerCosine = spot ? MathF.Cos(outerAngle) : -1f;
			// Preview exposure until the game's intensity units are resolved.
			var intensity = Math.Min(power * 0.025f, 64f);
			return new SceneLight(entityUid, spot ? SceneLightKind.Spot : SceneLightKind.Omni,
				position, direction, color, intensity, range, outerCosine)
			{
				InnerConeCosine = spot ? MathF.Cos(innerAngle) : -1f
			};
		}
		return null;
	}

	internal static ulong[] ReadDestructionMeshGroup(byte[] bytes)
	{
		if (bytes.Length < 12 || BitConverter.ToUInt32(bytes) is not ((uint)Magic.DestructionMeshGroup or (uint)Magic.DestructionMeshGroupY11S3))
			return [];
		var count = BitConverter.ToUInt32(bytes, 8);
		if (12 + count * 8L != bytes.Length)
			return [];
		var members = new ulong[count];
		for (var i = 0; i < members.Length; i++)
			members[i] = BitConverter.ToUInt64(bytes, 12 + i * sizeof(ulong));
		return members;
	}

	internal static HashSet<ulong> FindEntityMeshes(byte[] bytes, uint version, HashSet<ulong> meshes, Dictionary<ulong, ulong[]> groups)
	{
		var found = new HashSet<ulong>();
		var modern = bytes.Length >= 4 && BitConverter.ToUInt32(bytes) == (uint)Magic.EntityY11S3;
		// Entity components follow the matrix and serialize UIDs without alignment.
		for (var offset = EntityMatrixOffset(bytes, version) + 64; offset <= bytes.Length - sizeof(ulong); offset++)
		{
			if (modern)
			{
				// Render component links carry two flags after their tag. Direct
				// mesh resource references carry their UID immediately after it.
				// Nested resources can also contain mesh UIDs with their own transforms.
				if (BitConverter.ToUInt32(bytes, offset - 6) == RenderComponent && IsRenderComponentLink(bytes, offset))
				{
					var renderUid = BitConverter.ToUInt64(bytes, offset);
					if (groups.TryGetValue(renderUid, out var renderMembers))
						found.UnionWith(renderMembers);
					else if (meshes.Contains(renderUid))
						found.Add(renderUid);
				}
				else if (HasComponentTag(bytes, offset - 4, DirectMeshReference))
				{
					var meshUid = BitConverter.ToUInt64(bytes, offset);
					if (meshes.Contains(meshUid))
						found.Add(meshUid);
				}
				continue;
			}
			var uid = BitConverter.ToUInt64(bytes, offset);
			if (meshes.Contains(uid))
				found.Add(uid);
			if (groups.TryGetValue(uid, out var members))
				found.UnionWith(members);
		}
		return found;
	}

	private static bool IsRenderComponentLink(byte[] bytes, int uidOffset) =>
		uidOffset <= bytes.Length - 16 && bytes[uidOffset - 2] <= 1 && bytes[uidOffset - 1] <= 1 &&
		BitConverter.ToUInt64(bytes, uidOffset + 8) == 0;

	private static bool HasComponentTag(byte[] bytes, int offset, uint tag) =>
		BitConverter.ToUInt32(bytes, offset) == tag &&
		BitConverter.ToUInt32(bytes, offset - 6) == 0xFBF8 &&
		BitConverter.ToUInt16(bytes, offset - 2) == 0;

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

	internal static bool TryReadEntityMatrix(byte[] bytes, uint version, out Matrix4 matrix)
	{
		matrix = default;
		// Version 29 places the matrix immediately after the magic. Later entities
		// have an extra byte. Keep the serialized column order for OpenTK.
		var start = EntityMatrixOffset(bytes, version);
		if (bytes.Length < start + 16 * sizeof(float) ||
		    BitConverter.ToUInt32(bytes, 0) is not ((uint)Magic.Entity or (uint)Magic.EntityY11S3))
			return false;
		return TryReadMatrix(bytes, start, out matrix);
	}

	private static bool TryReadMatrix(byte[] bytes, int start, out Matrix4 matrix)
	{
		matrix = default;
		Span<float> values = stackalloc float[16];
		for (var index = 0; index < values.Length; index++)
		{
			values[index] = BitConverter.ToSingle(bytes, start + index * sizeof(float));
			if (!float.IsFinite(values[index]))
				return false;
		}
		if (Math.Abs(values[3]) > 0.001f || Math.Abs(values[7]) > 0.001f ||
		    Math.Abs(values[11]) > 0.001f || Math.Abs(values[15] - 1f) > 0.001f)
			return false;
		matrix = new Matrix4(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7],
			values[8], values[9], values[10], values[11], values[12], values[13], values[14], values[15]);
		return float.IsFinite(matrix.Determinant) && Math.Abs(matrix.Determinant) > 1e-12f;
	}

	private static int EntityMatrixOffset(byte[] bytes, uint version) =>
		version == 29 || (bytes.Length >= 4 && BitConverter.ToUInt32(bytes) == (uint)Magic.EntityY11S3) ? 4 : 5;

	internal static bool IsMesh(uint type) => type is (uint)Magic.Mesh or (uint)Magic.MeshY11S3;

	private static float SafeFloat(byte[] bytes, int offset, float fallback)
	{
		var value = BitConverter.ToSingle(bytes, offset);
		return float.IsFinite(value) ? value : fallback;
	}
}
