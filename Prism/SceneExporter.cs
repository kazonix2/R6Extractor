using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Prism.Render;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using RainbowForge.Image;
using RainbowForge.Model;
using RainbowForge.RenderPipeline;

namespace Prism;

/// <summary>
///     Headless export of a World/LoadUnit into an instanced scene package for the RavenEditor level editor:
///     scene.json (meshes + entity instances with their serialized 4x4 transforms), meshes/*.rmesh and textures/*.rtex
/// </summary>
internal static class SceneExporter
{
	private const int MaxTextureSize = 1024;

	public static int Run(string[] args)
	{
		if (args.Length < 3)
		{
			Console.Error.WriteLine("usage: Prism --export-scene <map.forge> <outputDir> [worldOrLoadUnitUidHex]");
			return 2;
		}

		var forgePath = Path.GetFullPath(args[1]);
		var outputDirectory = Path.GetFullPath(args[2]);
		ulong? rootUid = args.Length > 3 ? ulong.Parse(args[3].Replace("0x", ""), NumberStyles.HexNumber) : null;

		var forge = Forge.GetForge(forgePath);
		using var resolver = new AssetDependencyResolver(forge.SourcePath);
		var exporter = new Package(forge, resolver, outputDirectory);

		var loadUnits = forge.Entries
			.Where(entry => rootUid == null || entry.Uid == rootUid)
			.Where(entry => PrismForm.IsLoadUnit(AssetTypeDetector.DetectMagic(forge, entry, inspectPayload: true)))
			.Select(entry => entry.Uid)
			.ToArray();
		if (loadUnits.Length == 0)
		{
			Console.Error.WriteLine("No LoadUnits found in this forge (is it a map forge, e.g. datapc64_pvp01_house.forge?)");
			return 1;
		}

		for (var index = 0; index < loadUnits.Length; index++)
		{
			Console.Error.WriteLine($"LoadUnit {index + 1}/{loadUnits.Length} ({loadUnits[index]:X16})");
			try
			{
				exporter.AddLoadUnit(loadUnits[index]);
			}
			catch (Exception e)
			{
				// Maps can reference optional LoadUnits that are not installed; keep what is available
				Console.Error.WriteLine($"  skipped: {e.Message}");
			}
		}

		exporter.WriteManifest(forgePath);
		Console.Error.WriteLine($"Exported {exporter.MeshCount} meshes, {exporter.InstanceCount} instances, {exporter.TextureCount} textures to {outputDirectory}");
		return 0;
	}

	private sealed class Package
	{
		private readonly Forge _forge;
		private readonly AssetDependencyResolver _resolver;
		private readonly string _root;
		private readonly Dictionary<(ulong Compiled, ulong Mesh), int> _meshIndexByBinding = new();
		private readonly Dictionary<ulong, string> _textureFileByUid = new();
		private readonly Dictionary<string, SceneMaterialReader.Binding> _materials = new();
		private readonly List<MeshRecord> _meshes = new();
		private readonly List<InstanceRecord> _instances = new();

		public int MeshCount => _meshes.Count;
		public int InstanceCount => _instances.Count;
		public int TextureCount => _textureFileByUid.Count;

		public Package(Forge forge, AssetDependencyResolver resolver, string root)
		{
			_forge = forge;
			_resolver = resolver;
			_root = root;
			Directory.CreateDirectory(Path.Combine(root, "meshes"));
			Directory.CreateDirectory(Path.Combine(root, "textures"));
		}

		public void AddLoadUnit(ulong loadUnitUid)
		{
			if (_forge.GetContainer(loadUnitUid) is not ForgeAsset asset)
				return;

			using var payload = asset.GetDataStream(_forge);
			var archive = FlatArchive.Read(_forge, asset, payload);

			var placements = SceneLightingReader.ReadEntitiesAndLights(_forge, archive, payload.BaseStream, readLights: false).MeshPlacements;
			var materials = new SceneMaterialReader(archive, payload.BaseStream, _forge.Version);
			foreach (var meshEntry in archive.Entries.Where(entry => SceneLightingReader.IsMesh(entry.MetaData.FileType)))
			{
				if (!placements.TryGetValue(meshEntry.MetaData.Uid, out var instances))
					continue;
				try
				{
					var index = AddMesh(archive, payload.BaseStream, meshEntry, materials);
					foreach (var instance in instances)
					{
						var matrix = instance.Transform;
						_instances.Add(new InstanceRecord
						{
							Id = _instances.Count,
							Mesh = index,
							Entity = instance.EntityUid.ToString("X16"),
							LoadUnit = loadUnitUid.ToString("X16"),
							Name = string.IsNullOrWhiteSpace(instance.EntityName) ? null : instance.EntityName,
							Matrix =
							[
								matrix.M11, matrix.M12, matrix.M13, matrix.M14, matrix.M21, matrix.M22, matrix.M23, matrix.M24,
								matrix.M31, matrix.M32, matrix.M33, matrix.M34, matrix.M41, matrix.M42, matrix.M43, matrix.M44
							]
						});
					}
				}
				catch (Exception error)
				{
					Console.Error.WriteLine($"  mesh {meshEntry.MetaData.Uid:X16} skipped: {error.Message}");
				}
			}
		}

		private int AddMesh(FlatArchive archive, Stream payload, FlatArchiveEntry meshEntry, SceneMaterialReader materials)
		{
			using var meshStream = archive.GetEntryPayloadStream(payload, meshEntry.MetaData.Uid, _forge.Version);
			var source = _resolver.ResolveMesh(meshStream, _forge, meshEntry.MetaData, CancellationToken.None);
			var binding = (source.Uid, meshEntry.MetaData.Uid);
			if (_meshIndexByBinding.TryGetValue(binding, out var existing))
				return existing;

			var compiled = source.Decode();

			var objectCount = compiled.Objects.Count / (int)Math.Max(1, compiled.MeshHeader.NumLods);
			var textures = new string[objectCount];
			var materialUids = new string[objectCount];
			for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
			{
				var materialUid = source.MaterialForPart(compiled, objectIndex);
				var id = materialUid.ToString("X16");
				try
				{
					if (materials.Read(materialUid) is { } material)
					{
						_materials.TryAdd(id, material);
						materialUids[objectIndex] = id;
						textures[objectIndex] = TryExportDiffuse(materials, material);
					}
				}
				catch (Exception error) when (error is IOException or InvalidDataException)
				{
					Console.Error.WriteLine($"  material {id} skipped: {error.Message}");
				}
			}

			var file = $"meshes/{source.Uid:X16}.rmesh";
			WriteMesh(Path.Combine(_root, file), compiled, objectCount, out var boundsMin, out var boundsMax);

			var index = _meshes.Count;
			_meshes.Add(new MeshRecord
			{
				Id = index,
				Uid = source.Uid.ToString("X16"),
				File = file,
				Textures = textures,
				Materials = materialUids,
				BoundsMin = boundsMin,
				BoundsMax = boundsMax
			});
			_meshIndexByBinding[binding] = index;
			return index;
		}

		// .rmesh v1: "RMSH" u32 version, u32 vertexCount, u32 indexCount, u32 submeshCount,
		// vertexCount * (pos3 nrm3 uv2 floats), indexCount * u32, submeshCount * (u32 indexStart, u32 indexCount)
		private static void WriteMesh(string path, CompiledMeshObject mesh, int objectCount, out float[] boundsMin, out float[] boundsMax)
		{
			var positions = mesh.Container.Vertices;
			var normals = mesh.Container.Normals;
			var uvs = mesh.Container.TexCoords;
			var indices = new List<uint>();
			var submeshes = new List<(uint Start, uint Count)>();
			for (var objectIndex = 0; objectIndex < objectCount; objectIndex++)
			{
				var start = (uint)indices.Count;
				foreach (var triangle in mesh.Objects[objectIndex])
				{
					indices.Add((uint)triangle.A);
					indices.Add((uint)triangle.B);
					indices.Add((uint)triangle.C);
				}
				submeshes.Add((start, (uint)indices.Count - start));
			}

			boundsMin = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
			boundsMax = new[] { float.MinValue, float.MinValue, float.MinValue };

			using var w = new BinaryWriter(File.Create(path));
			w.Write(Encoding.ASCII.GetBytes("RMSH"));
			w.Write(1u);
			w.Write((uint)positions.Length);
			w.Write((uint)indices.Count);
			w.Write((uint)submeshes.Count);
			for (var i = 0; i < positions.Length; i++)
			{
				var p = positions[i];
				var n = normals != null && i < normals.Length ? normals[i] : System.Numerics.Vector3.UnitZ;
				var t = uvs != null && i < uvs.Length ? uvs[i] : System.Numerics.Vector2.Zero;
				w.Write(p.X); w.Write(p.Y); w.Write(p.Z);
				w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
				w.Write(t.X); w.Write(t.Y);
				boundsMin[0] = Math.Min(boundsMin[0], p.X); boundsMin[1] = Math.Min(boundsMin[1], p.Y); boundsMin[2] = Math.Min(boundsMin[2], p.Z);
				boundsMax[0] = Math.Max(boundsMax[0], p.X); boundsMax[1] = Math.Max(boundsMax[1], p.Y); boundsMax[2] = Math.Max(boundsMax[2], p.Z);
			}
			foreach (var index in indices)
				w.Write(index);
			foreach (var (start, count) in submeshes)
			{
				w.Write(start);
				w.Write(count);
			}

			if (positions.Length == 0)
			{
				boundsMin = new float[3];
				boundsMax = new float[3];
			}
		}

		private string TryExportDiffuse(SceneMaterialReader materials, SceneMaterialReader.Binding material)
		{
			foreach (var textureUid in material.DiffuseCandidates())
			{
				if (_textureFileByUid.TryGetValue(textureUid, out var cached))
				{
					if (cached != null)
						return cached;
					continue;
				}
				SceneTextureData data;
				try
				{
					if (!materials.TryReadInlineTexture(textureUid, out var texture, out var surface))
					{
						var resolved = _resolver.Resolve(textureUid, AssetBankKind.Texture, CancellationToken.None);
						using var stream = resolved.OpenStream();
						texture = Texture.Read(stream, resolved.Version);
						surface = texture.ReadSurfaceBytes(stream);
					}
					using var dds = DdsHelper.GetDdsStream(texture, surface);
					using var image = Pfim.Pfim.FromStream(dds);
					data = SceneTextureData.FromPfim(textureUid, image);
				}
				catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
					or NotImplementedException or ArgumentException or KeyNotFoundException or DllNotFoundException)
				{
					_textureFileByUid[textureUid] = null;
					continue;
				}
				var file = $"textures/{textureUid:X16}.rtex";
				WriteTexture(Path.Combine(_root, file), data);
				return _textureFileByUid[textureUid] = file;
			}
			return null;
		}

		// .rtex v1: "RTEX" u32 version, u32 width, u32 height, width*height BGRA8 pixels (box-downsampled to <= 1024)
		private static void WriteTexture(string path, SceneTextureData data)
		{
			var width = data.Width;
			var height = data.Height;
			var pixels = data.BgraPixels;
			while (width > MaxTextureSize || height > MaxTextureSize)
			{
				var nw = Math.Max(1, width / 2);
				var nh = Math.Max(1, height / 2);
				var next = new byte[nw * nh * 4];
				for (var y = 0; y < nh; y++)
				for (var x = 0; x < nw; x++)
				for (var c = 0; c < 4; c++)
				{
					var sum = 0;
					for (var dy = 0; dy < 2; dy++)
					for (var dx = 0; dx < 2; dx++)
					{
						var sx = Math.Min(width - 1, x * 2 + dx);
						var sy = Math.Min(height - 1, y * 2 + dy);
						sum += pixels[(sy * width + sx) * 4 + c];
					}
					next[(y * nw + x) * 4 + c] = (byte)(sum / 4);
				}
				width = nw;
				height = nh;
				pixels = next;
			}

			using var w = new BinaryWriter(File.Create(path));
			w.Write(Encoding.ASCII.GetBytes("RTEX"));
			w.Write(1u);
			w.Write((uint)width);
			w.Write((uint)height);
			w.Write(pixels);
		}

		public void WriteManifest(string forgePath)
		{
			var manifest = new SceneManifest
			{
				Format = "raven-scene",
				Version = 1,
				Source = forgePath,
				ForgeVersion = _forge.Version,
				Meshes = _meshes,
				Instances = _instances,
				Materials = _materials.ToDictionary(pair => pair.Key, pair => (object)new
				{
					baseUvTransform = Convert.ToHexString(pair.Value.BaseUvTransform),
					secondaryUvTransform = Convert.ToHexString(pair.Value.SecondaryUvTransform),
					maps = pair.Value.Maps.Select(map => new
					{
						layer = map.Layer,
						slot = map.Slot,
						type = map.Type.ToString(),
						specUid = map.SpecUid.ToString("X16"),
						mapUid = map.MapUid.ToString("X16"),
						textureUids = map.TextureUids.Select(uid => uid.ToString("X16"))
					})
				})
			};
			File.WriteAllText(Path.Combine(_root, "scene.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented,
				new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
		}
	}

	private sealed class SceneManifest
	{
		[JsonProperty("format")] public string Format { get; set; }
		[JsonProperty("version")] public int Version { get; set; }
		[JsonProperty("source")] public string Source { get; set; }
		[JsonProperty("forgeVersion")] public uint ForgeVersion { get; set; }
		[JsonProperty("meshes")] public List<MeshRecord> Meshes { get; set; }
		[JsonProperty("instances")] public List<InstanceRecord> Instances { get; set; }
		[JsonProperty("materials")] public Dictionary<string, object> Materials { get; set; }
	}

	private sealed class MeshRecord
	{
		[JsonProperty("id")] public int Id { get; set; }
		[JsonProperty("uid")] public string Uid { get; set; }
		[JsonProperty("file")] public string File { get; set; }
		[JsonProperty("textures")] public string[] Textures { get; set; }
		[JsonProperty("materials")] public string[] Materials { get; set; }
		[JsonProperty("boundsMin")] public float[] BoundsMin { get; set; }
		[JsonProperty("boundsMax")] public float[] BoundsMax { get; set; }
	}

	private sealed class InstanceRecord
	{
		[JsonProperty("id")] public int Id { get; set; }
		[JsonProperty("mesh")] public int Mesh { get; set; }
		[JsonProperty("entity")] public string Entity { get; set; }
		[JsonProperty("loadUnit")] public string LoadUnit { get; set; }
		[JsonProperty("name")] public string Name { get; set; }
		[JsonProperty("matrix")] public float[] Matrix { get; set; }
	}
}
