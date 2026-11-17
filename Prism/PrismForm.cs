using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using OpenTK;
using JeremyAnsel.Media.WavefrontObj;
using Prism.Extensions;
using Prism.Render;
using Prism.Resources;
using RainbowForge;
using RainbowForge.Archive;
using RainbowForge.Components;
using RainbowForge.Core;
using RainbowForge.Core.Container;
using RainbowForge.Dump;
using RainbowForge.Image;
using RainbowForge.Link;
using RainbowForge.Model;
using RainbowForge.RenderPipeline;
using SkiaSharp;

namespace Prism
{
	public partial class PrismForm : Form
	{
		private ModelRenderer _renderer3d;
		private SurfaceRenderer _renderer2d;

		private Forge _openedForge;

		private AssetDependencyResolver _dependencyResolver;
		private CancellationTokenSource _previewCancellation;

		private readonly Dictionary<Forge, AssetDependencyResolver> _loadedForges = new();

		private void ActivateForge(Forge forge)
		{
			_openedForge = forge;
			_dependencyResolver = _loadedForges[forge];
		}

		private void DumpSelectionAsBin(string outputDir, object o)
		{
			var (_, assetMetaData, streamProvider) = GetAssetStream(o);
			using var stream = streamProvider.Invoke();
			DumpHelper.DumpBin(Path.Combine(outputDir, assetMetaData.Filename + ".bin"), stream.BaseStream);
		}

		private void DumpSelectionAsObj(string outputDir, object o)
		{
			var (_, assetMetaData, streamProvider) = GetAssetStream(o);
			using var stream = streamProvider.Invoke();

			if (MagicHelper.GetFiletype(assetMetaData.Magic) == AssetType.Mesh)
			{
				var header = MeshHeader.Read(stream, _openedForge.Version);

				var compiledMeshObject = CompiledMeshObject.Read(stream, header);

				var obj = new ObjFile();

				var numObjects = compiledMeshObject.Objects.Count / compiledMeshObject.MeshHeader.NumLods;

				for (var objId = 0; objId < (_settings.ExportAllModelLods ? compiledMeshObject.Objects.Count : numObjects); objId++)
				{
					var lod = objId / numObjects;

					var objObject = compiledMeshObject.Objects[objId];
					foreach (var face in objObject)
					{
						var objFace = new ObjFace
						{
							ObjectName = $"{assetMetaData.Filename + (_settings.ExportAllModelLods ? $"_lod{lod}_" : "_")}object{objId % numObjects}"
						};

						objFace.Vertices.Add(new ObjTriplet(face.A + 1, face.A + 1, face.A + 1));
						objFace.Vertices.Add(new ObjTriplet(face.B + 1, face.B + 1, face.B + 1));
						objFace.Vertices.Add(new ObjTriplet(face.C + 1, face.C + 1, face.C + 1));

						obj.Faces.Add(objFace);
					}
				}

                var container = compiledMeshObject.Container;
                for (var i = 0; i < container.Vertices.Length; i++)
                {
                    var vert = container.Vertices[i];
                    var color = container.Colors?[0, i] ?? new Color4(1, 1, 1, 1);

                    // Export raw local space position
                    obj.Vertices.Add(new ObjVertex(vert.X, vert.Y, vert.Z, color.R, color.G, color.B, color.A));
                }

                foreach (var v in container.Normals)
					obj.VertexNormals.Add(new ObjVector3(v.X, v.Y, v.Z));

				foreach (var v in container.TexCoords)
					obj.TextureVertices.Add(new ObjVector3(v.X, v.Y));

				obj.WriteTo(Path.Combine(outputDir, assetMetaData.Filename + ".obj"));
			}
			else if (MagicHelper.Equals(Magic.Mesh, assetMetaData.Magic))
			{
				var header = Mesh.Read(stream);

				var boneModel = ObjFile.FromStream(ResourceHelper.GetResource("bone.obj"));

				var obj = new ObjFile();

				var i = 0;
				foreach (var bone in header.Bones)
				{
					foreach (var face in boneModel.Faces)
					{
						var objFace = new ObjFace
						{
							ObjectName = $"Bone_{(Enum.IsDefined(typeof(BoneId), bone.Id) ? bone.Id : $"{(uint)bone.Id:X8}")}"
						};

						objFace.Vertices.Add(new ObjTriplet(face.Vertices[0].Vertex + i, face.Vertices[0].Texture + i, face.Vertices[0].Normal + i));
						objFace.Vertices.Add(new ObjTriplet(face.Vertices[1].Vertex + i, face.Vertices[1].Texture + i, face.Vertices[1].Normal + i));
						objFace.Vertices.Add(new ObjTriplet(face.Vertices[2].Vertex + i, face.Vertices[2].Texture + i, face.Vertices[2].Normal + i));

						obj.Faces.Add(objFace);
					}

					foreach (var vertex in boneModel.Vertices)
					{
						var x = vertex.Position.X;
						var y = vertex.Position.Y;
						var z = vertex.Position.Z;

						var nx = x * bone.Transformation.M11 + y * bone.Transformation.M21 + z * bone.Transformation.M31 + bone.Transformation.M41;
						var ny = x * bone.Transformation.M12 + y * bone.Transformation.M22 + z * bone.Transformation.M32 + bone.Transformation.M42;
						var nz = x * bone.Transformation.M13 + y * bone.Transformation.M23 + z * bone.Transformation.M33 + bone.Transformation.M43;

						obj.Vertices.Add(new ObjVertex(nx, ny, nz));
					}

					foreach (var vertex in boneModel.VertexNormals)
					{
						var x = vertex.X;
						var y = vertex.Y;
						var z = vertex.Z;

						var nx = x * bone.Transformation.M11 + y * bone.Transformation.M21 + z * bone.Transformation.M31 + bone.Transformation.M41;
						var ny = x * bone.Transformation.M12 + y * bone.Transformation.M22 + z * bone.Transformation.M32 + bone.Transformation.M42;
						var nz = x * bone.Transformation.M13 + y * bone.Transformation.M23 + z * bone.Transformation.M33 + bone.Transformation.M43;

						obj.VertexNormals.Add(new ObjVector3(nx, ny, nz));
					}

					foreach (var vertex in boneModel.TextureVertices)
					{
						obj.TextureVertices.Add(new ObjVector3(vertex.X, vertex.Y, vertex.Z));
					}

					i += boneModel.Vertices.Count;
				}

				obj.WriteTo(Path.Combine(outputDir, assetMetaData.Filename + "_bones.obj"));
			}
		}

		private void DumpSelectionAsDds(string outputDir, object o)
		{
			var (_, assetMetaData, streamProvider) = GetAssetStream(o);
			using var stream = streamProvider.Invoke();

			if (MagicHelper.GetFiletype(assetMetaData.Magic) == AssetType.Texture)
			{
				var texture = Texture.Read(stream, _openedForge.Version);
				var surface = texture.ReadSurfaceBytes(stream);
				using var ddsStream = DdsHelper.GetDdsStream(texture, surface);

				DumpHelper.DumpBin(Path.Combine(outputDir, assetMetaData.Filename + ".dds"), ddsStream);
			}
		}

		private void DumpSelectionAsPng(string outputDir, object o)
		{
			var (_, assetMetaData, streamProvider) = GetAssetStream(o);
			using var stream = streamProvider.Invoke();

			var texture = Texture.Read(stream, _openedForge.Version);
			using var image = Pfim.Pfim.FromStream(DdsHelper.GetDdsStream(texture, texture.ReadSurfaceBytes(stream)));
			using var bmp = image.CreateBitmap();

			if (_settings.FlipPngSpace)
				bmp.RotateFlip(RotateFlipType.RotateNoneFlipY);

			if ((texture.TexType == TextureType.Normal || texture.TexType == TextureType.Misc) &&
			    texture.TexFormat == 0x6)
			{
				if (_settings.FlipPngGreenChannel || _settings.RecalculatePngBlueChannel)
				{
					TextureUtil.PatchNormalMap(bmp, _settings.FlipPngGreenChannel ? 1 : -1, _settings.RecalculatePngBlueChannel);
				}
			}

			bmp.Save(Path.Combine(outputDir, assetMetaData.Filename + ".png"), ImageFormat.Png);
		}

		private static AssetMetaData GetAssetMetaData(object o)
		{
			return o switch
			{
				Entry e => new AssetMetaData(e.Uid, e.MetaData?.FileType ?? 0, 0, e.MetaData?.FileName ?? $"{e.Uid:X16}"),
				FlatArchiveEntry fae => new AssetMetaData(fae.MetaData?.Uid ?? 0, fae.MetaData?.FileType ?? 0, fae.MetaData?.ContainerType ?? 0, fae.MetaData?.FileName ?? $"{fae.MetaData?.Uid ?? 0:X16}"),
				_ => null
			};
		}

		private AssetMetaData GetDetectedAssetMetaData(Entry entry, Forge forge)
		{
			var meta = GetAssetMetaData(entry);
			return meta with { Magic = AssetTypeDetector.DetectMagic(forge, entry) };
		}

		private AssetStream GetAssetStream(object o)
		{
			var forge = _assetList.GetForge(o);
			if (forge == null)
				return null;
			switch (o)
			{
				case Entry entry:
				{
					var container = forge.GetContainer(entry.Uid);

					return container switch
					{
						ForgeAsset forgeAsset => new AssetStream(AssetStreamType.ForgeEntry, GetDetectedAssetMetaData(entry, forge), () => forgeAsset.GetDataStream(forge)),
						RawRiffAsset riff => new AssetStream(AssetStreamType.ForgeEntry, GetDetectedAssetMetaData(entry, forge), () => riff.GetDataStream(forge)),
						_ => new AssetStream(AssetStreamType.ForgeEntry, GetDetectedAssetMetaData(entry, forge), () => forge.GetEntryStream(entry))
					};
				}
				case FlatArchiveEntry flatArchiveEntry:
				{
					if (flatArchiveEntry?.MetaData == null || _assetList.GetParent(flatArchiveEntry) is not { } parent)
						return null;

					var container = forge.GetContainer(parent.Uid);
					if (container is not ForgeAsset forgeAsset)
						return null;

					return new AssetStream(AssetStreamType.ArchiveEntry, GetAssetMetaData(flatArchiveEntry),
						() =>
						{
							using var assetStream = forgeAsset.GetDataStream(forge);
							var arc = FlatArchive.Read(forge, forgeAsset, assetStream);
							// Texture.Read consumes the resource header as part of the texture record.
							if (MagicHelper.GetFiletype(flatArchiveEntry.MetaData.FileType) is AssetType.Texture or AssetType.Sound)
								return arc.GetEntryStream(assetStream.BaseStream, flatArchiveEntry.MetaData.Uid);
							return arc.GetEntryPayloadStream(assetStream.BaseStream, flatArchiveEntry.MetaData.Uid, forge.Version);
						}) { ParentUid = parent.Uid };
				}
			}

			return null;
		}

		private bool IsWorldScene(AssetStream assetStream) =>
			assetStream != null
			&& assetStream.StreamType == AssetStreamType.ForgeEntry
			&& ((Magic)assetStream.MetaData.Magic is Magic.World or Magic.WorldY11S3 or Magic.LoadUnit or Magic.LoadUnitY11S3);

		internal static bool IsLoadUnit(ulong magic) => (Magic)magic is Magic.LoadUnit or Magic.LoadUnitY11S3;

		private List<TexturedSceneMesh> BuildWorldScene(AssetStream assetStream, CancellationToken cancellationToken, Action<string> progress)
			=> BuildWorldScene(assetStream, cancellationToken, progress, null, loadTextures: true);

		private List<TexturedSceneMesh> BuildWorldScene(AssetStream assetStream, CancellationToken cancellationToken,
			Action<string> progress, Action<IReadOnlyList<TexturedSceneMesh>, IReadOnlyList<SceneLight>> chunkReady)
			=> BuildWorldScene(assetStream, cancellationToken, progress, chunkReady, loadTextures: true);

		private List<TexturedSceneMesh> BuildWorldScene(AssetStream assetStream, CancellationToken cancellationToken,
			Action<string> progress, Action<IReadOnlyList<TexturedSceneMesh>, IReadOnlyList<SceneLight>> chunkReady,
			bool loadTextures, Forge forge = null, AssetDependencyResolver resolver = null)
		{
			forge ??= _openedForge;
			resolver ??= _dependencyResolver;
			var loadUnitUids = IsLoadUnit(assetStream.MetaData.Magic)
				? new[] { assetStream.MetaData.Uid }
				: forge.Entries
					.Where(entry => IsLoadUnit(AssetTypeDetector.DetectMagic(forge, entry, inspectPayload: true)))
					.Select(entry => entry.Uid)
					.ToArray();

			var scene = new List<TexturedSceneMesh>();
			var textureCache = new Dictionary<ulong, SceneTextureData>();
			Exception firstLoadError = null;
			for (var index = 0; index < loadUnitUids.Length; index++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				progress?.Invoke($"Loading LoadUnit {index + 1:N0}/{loadUnitUids.Length:N0}...");
				try
				{
					var streamedMeshCount = 0;
					Action<TexturedSceneMesh, IReadOnlyList<SceneLight>> meshReady = null;
					if (chunkReady != null)
						meshReady = (mesh, lights) =>
						{
							streamedMeshCount++;
							scene.Add(mesh);
							chunkReady([mesh], lights);
						};
					var result = BuildLoadUnitScene(loadUnitUids[index], textureCache, cancellationToken, progress,
						meshReady, loadTextures, forge, resolver);
					if (chunkReady == null && result.Meshes.Count > 0)
					{
						scene.AddRange(result.Meshes);
					}
					else if (streamedMeshCount == 0 && result.Lights.Count > 0)
						chunkReady?.Invoke(Array.Empty<TexturedSceneMesh>(), result.Lights);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception error)
				{
					// A World can contain optional or partially installed LoadUnits. Keep
					// rendering the dependencies that are available locally.
					firstLoadError ??= error;
				}
			}

			if (scene.Count == 0)
				throw new InvalidDataException("No renderable mesh placements were found for this World/LoadUnit.", firstLoadError);
			return scene;
		}

		private sealed record LoadUnitSceneResult(List<TexturedSceneMesh> Meshes, List<SceneLight> Lights);

		private LoadUnitSceneResult BuildLoadUnitScene(ulong loadUnitUid, Dictionary<ulong, SceneTextureData> textureCache,
			CancellationToken cancellationToken, Action<string> progress,
			Action<TexturedSceneMesh, IReadOnlyList<SceneLight>> meshReady, bool loadTextures, Forge forge, AssetDependencyResolver resolver)
		{
			if (forge.GetContainer(loadUnitUid) is not ForgeAsset loadUnitAsset)
				return new LoadUnitSceneResult([], []);

			using var payload = loadUnitAsset.GetDataStream(forge);
			var archive = FlatArchive.Read(forge, loadUnitAsset, payload);
			return BuildArchiveScene(archive, payload.BaseStream, textureCache, cancellationToken, progress, meshReady, loadTextures, forge, resolver);
		}

		private LoadUnitSceneResult BuildArchiveScene(FlatArchive archive, Stream payload,
			Dictionary<ulong, SceneTextureData> textureCache, CancellationToken cancellationToken,
			Action<string> progress, Action<TexturedSceneMesh, IReadOnlyList<SceneLight>> meshReady, bool loadTextures, Forge forge, AssetDependencyResolver resolver,
			ulong? entityUid = null)
		{
			var entities = SceneLightingReader.ReadEntitiesAndLights(forge, archive, payload,
				readLights: !entityUid.HasValue, entityUid: entityUid);
			var meshEntries = archive.Entries.Where(entry => SceneLightingReader.IsMesh(entry.MetaData.FileType)).ToArray();
			var scene = new List<TexturedSceneMesh>();
			var materials = new SceneMaterialReader(archive, payload, forge.Version);
			Exception firstMeshError = null;

			for (var meshEntryIndex = 0; meshEntryIndex < meshEntries.Length; meshEntryIndex++)
			{
				var meshEntry = meshEntries[meshEntryIndex];
				cancellationToken.ThrowIfCancellationRequested();
				if (!entities.MeshPlacements.TryGetValue(meshEntry.MetaData.Uid, out var placements))
					continue;
				TexturedSceneMesh sceneMesh;
				try
				{
					using var meshStream = archive.GetEntryPayloadStream(payload, meshEntry.MetaData.Uid, forge.Version);
					var source = resolver.ResolveMesh(meshStream, forge, meshEntry.MetaData, cancellationToken, progress);
					var compiledMesh = source.Decode();
					var objectCount = compiledMesh.Objects.Count / (int)Math.Max(1u, compiledMesh.MeshHeader.NumLods);
					if (!compiledMesh.Objects.Take(objectCount).Any(part => part.Length > 0))
						continue;

					var partMaterials = new SceneMaterial[objectCount];
					for (var objectIndex = 0; loadTextures && objectIndex < objectCount; objectIndex++)
					{
						cancellationToken.ThrowIfCancellationRequested();
						partMaterials[objectIndex] = ReadSceneMaterial(materials, source.MaterialForPart(compiledMesh, objectIndex), textureCache, cancellationToken, progress, forge.Version, resolver);
					}

					var instances = entityUid.HasValue ? [Matrix4.Identity] :
						placements.Take(4096).Select(placement => placement.Transform).ToArray();
					sceneMesh = new TexturedSceneMesh(meshEntry.MetaData.Uid, compiledMesh, partMaterials, instances);
				}
				catch (Exception error) when (error is IOException or InvalidDataException or OverflowException)
				{
					firstMeshError ??= new InvalidDataException($"Mesh {meshEntry.MetaData.Uid:X16} could not be loaded.", error);
					continue;
				}
				scene.Add(sceneMesh);
				meshReady?.Invoke(sceneMesh, scene.Count == 1 ? entities.Lights : Array.Empty<SceneLight>());
			}
			if (scene.Count == 0 && firstMeshError != null)
				throw firstMeshError;
			return new LoadUnitSceneResult(scene, entities.Lights.ToList());
		}

		private SceneMaterial ReadSceneMaterial(SceneMaterialReader materials, ulong materialUid,
			Dictionary<ulong, SceneTextureData> textureCache, CancellationToken cancellationToken, Action<string> progress, uint version, AssetDependencyResolver resolver)
		{
			SceneMaterialReader.Binding material;
			try { material = materials.Read(materialUid); }
			catch (Exception error) when (error is IOException or InvalidDataException) { return null; }
			if (material == null)
				return null;
			var diffuse = ReadSceneTexture(material.Candidates(TextureType.Diffuse), materials, textureCache, cancellationToken, progress, resolver);
			var baseColor = diffuse ?? ReadSceneTexture(material.Candidates(TextureType.Misc), materials, textureCache, cancellationToken, progress, resolver);
			var blend = ReadSceneTexture(material.Candidates(TextureType.Diffuse, 1), materials, textureCache, cancellationToken, progress, resolver);
			return new SceneMaterial(
				baseColor,
				ReadSceneTexture(material.Candidates(TextureType.Normal), materials, textureCache, cancellationToken, progress, resolver),
				ReadSceneTexture(material.Candidates(TextureType.Specular), materials, textureCache, cancellationToken, progress, resolver),
				material.UvScale(version))
			{
				Opacity = SceneMaterialReader.ResolveOpacity(material, diffuse),
				Tint = material.Tint,
				GlassOpacity = material.Glass ? 0.28f : null,
				BlendDiffuse = blend,
				BlendMask = blend == null ? null : ReadSceneTexture(
					material.Candidates(TextureType.ColorMask).Concat(material.Candidates(TextureType.ColorMask, 1)),
					materials, textureCache, cancellationToken, progress, resolver),
				BlendUvScale = material.UvScale(version, 1)
			};
		}

		private SceneTextureData ReadSceneTexture(IEnumerable<ulong> candidates, SceneMaterialReader materials,
			Dictionary<ulong, SceneTextureData> textureCache,
			CancellationToken cancellationToken, Action<string> progress, AssetDependencyResolver resolver)
		{
			foreach (var textureUid in candidates)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (textureCache.TryGetValue(textureUid, out var cached))
				{
					if (cached != null)
						return cached;
					continue;
				}
				try
				{
					if (!materials.TryReadInlineTexture(textureUid, out var texture, out var surface))
					{
						var resolved = resolver.Resolve(textureUid, AssetBankKind.Texture, cancellationToken, progress);
						using var stream = resolved.OpenStream();
						texture = Texture.Read(stream, resolved.Version);
						surface = texture.ReadSurfaceBytes(stream);
					}
					using var dds = DdsHelper.GetDdsStream(texture, surface);
					using var image = Pfim.Pfim.FromStream(dds);
					return textureCache[textureUid] = SceneTextureData.FromPfim(textureUid, image, texture.TexFormat);
				}
				catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException
					or NotImplementedException or ArgumentException or KeyNotFoundException or DllNotFoundException)
				{
					textureCache[textureUid] = null;
				}
			}
			return null;
		}

		private CompiledMeshObject ReadMeshPreview(AssetStream assetStream)
		{
			using var stream = assetStream.StreamProvider.Invoke();
			var asset = assetStream.MetaData;
			if (!SceneLightingReader.IsMesh((uint)asset.Magic))
				return CompiledMeshObject.Read(stream, MeshHeader.Read(stream, _openedForge.Version));

			var metadata = FileMetaData.CreateDetected(asset.Filename, asset.ContainerType, (uint)asset.Magic, asset.Uid);
			if (assetStream.StreamType == AssetStreamType.ForgeEntry)
			{
				var start = stream.BaseStream.Position;
				var hasBodyMagic = stream.ReadUInt32() == (uint)asset.Magic;
				stream.BaseStream.Position = start;
				if (!hasBodyMagic)
					metadata = FileMetaData.Read(stream, _openedForge.Version);
			}
			return _dependencyResolver.ResolveMesh(stream, _openedForge, metadata, CancellationToken.None).Decode();
		}

		private void PreviewAsset(AssetStream assetStream)
		{
			switch (MagicHelper.GetFiletype(assetStream.MetaData.Magic))
			{
				case var _ when (Magic)assetStream.MetaData.Magic is Magic.Entity or Magic.EntityY11S3:
				{
					var scene = ReadEntityPreview(assetStream);
					if (scene.Count == 0)
					{
						_infoControl.SetObjects(new List<TreeListViewEntry>
						{
							CreateMetadataInfoNode(assetStream),
							new("3D preview", "No renderable mesh references found in this archive.")
						});
						_infoControl.ExpandAll();
						SetPreviewPanel(_infoControl);
						break;
					}
					SetPreviewPanel(_glControl);
					using (_glControl.SuspendPainting())
					{
						_glControl.MakeCurrent();
						_renderer3d.BuildTexturedScene(scene);
						_renderer3d.SetPartBounds([]);
						_renderer3d.ResetView();
					}
					break;
				}
				case AssetType.Mesh:
				case var _ when SceneLightingReader.IsMesh((uint)assetStream.MetaData.Magic):
				{
					var mesh = ReadMeshPreview(assetStream);
					if (mesh.MeshHeader.NumLods == 0 ||
					    !mesh.Objects.Take(mesh.Objects.Count / (int)mesh.MeshHeader.NumLods).Any(part => part.Length > 0))
						throw new InvalidDataException("This mesh has no renderable geometry in its highest detail level.");

					OnUiThread(() =>
					{
						SetPreviewPanel(_glControl);
						using (_glControl.SuspendPainting())
						{
							_glControl.MakeCurrent();
							_renderer3d.BuildModelQuads(mesh);
							_renderer3d.SetTexture(null);
							_renderer3d.SetPartBounds(mesh.MeshHeader.ObjectBoundingBoxes);
						}
					});

					break;
				}
				case AssetType.Sound:
				{
					PreviewSoundMedia(assetStream);
					break;
				}
				case AssetType.Texture:
				{
					using var stream = assetStream.StreamProvider.Invoke();
					var texture = Texture.Read(stream, _openedForge.Version);
					using var image = Pfim.Pfim.FromStream(DdsHelper.GetDdsStream(texture, texture.ReadSurfaceBytes(stream)));

					using (var skImage = image.CreateSkImage())
						_renderer2d.SetTexture(SKBitmap.FromImage(skImage), new KeyValuePair<string, string>[]
						{
							new("Width", texture.Width.ToString()),
							new("Height", texture.Height.ToString()),
							new("Size Scalar", texture.Chan.ToString()),
							new("Mips", texture.Mips.ToString()),
							new("Blocks", texture.NumBlocks.ToString()),
							new("Unk. Data 1 (flags?)", texture.Data1.ToString()),
							new("Unk. Data 2", texture.Data2.ToString()),
							new("Format", $"{DdsHelper.TextureFormats[texture.TexFormat]}"),
							new("Type", Enum.IsDefined(typeof(TextureType), texture.TexType) ? texture.TexType.ToString() : $"{texture.TexType:X}")
						});

					OnUiThread(() => { SetPreviewPanel(_imageControl); });

					break;
				}
				case AssetType.FlatArchive when assetStream.StreamType == AssetStreamType.ArchiveEntry:
				{
					// First entry in a flat archive is a UidLinkContainer

					using var stream = assetStream.StreamProvider.Invoke();
					var container = UidLinkContainer.Read(stream, assetStream.MetaData.ContainerType);

					var entries = new List<TreeListViewEntry>
					{
						CreateMetadataInfoNode(assetStream),
						new(nameof(UidLinkContainer), null,
							new TreeListViewEntry(nameof(UidLinkContainer.UidLinkEntries), null, container.UidLinkEntries.Select(CreateUidLinkEntryNode).ToArray())
						)
					};

					OnUiThread(() =>
					{
						_infoControl.SetObjects(entries);
						_infoControl.ExpandAll();
						SetPreviewPanel(_infoControl);
					});

					break;
				}
				default:
				{
					List<TreeListViewEntry> entries;

					switch ((Magic)assetStream.MetaData.Magic)
					{
						case Magic.ShaderCodeModuleUserMaterial:
						case Magic.ShaderCodeModulePostPro:
						{
							using var stream = assetStream.StreamProvider.Invoke();
							var mp = Shader.Read(stream);

							entries = new List<TreeListViewEntry>
							{
								CreateMetadataInfoNode(assetStream),
							};

							break;
						}
						case Magic.Material:
						{
							using var stream = assetStream.StreamProvider.Invoke();
							var mc = Material.Read(stream, _openedForge.Version);

							entries = new List<TreeListViewEntry>
							{
								CreateMetadataInfoNode(assetStream),
								new(nameof(Material), null,
									new TreeListViewEntry(nameof(Material.BaseTextureMapSpecs), null, mc.BaseTextureMapSpecs.Select(CreateMipContainerReferenceNode).ToArray()),
									new TreeListViewEntry(nameof(Material.SecondaryTextureMapSpecs), null, mc.SecondaryTextureMapSpecs.Select(CreateMipContainerReferenceNode).ToArray()),
									new TreeListViewEntry(nameof(Material.TertiaryTextureMapSpecs), null, mc.TertiaryTextureMapSpecs.Select(CreateMipContainerReferenceNode).ToArray())
								)
							};
							break;
						}
						case Magic.TextureMapSpec:
						{
							using var stream = assetStream.StreamProvider.Invoke();
							var mc = TextureMapSpec.Read(stream);

							entries = new List<TreeListViewEntry>
							{
								CreateMetadataInfoNode(assetStream),
								new(nameof(TextureMapSpec), null,
									new TreeListViewEntry(nameof(TextureMapSpec.TextureMapUid), $"{mc.TextureMapUid:X16}"),
									new TreeListViewEntry("TextureType", $"{mc.TextureType:X8}")
								)
							};
							break;
						}
						case Magic.TextureMap:
						{
							using var stream = assetStream.StreamProvider.Invoke();
							var mc = TextureMap.Read(stream, _openedForge.Version);

							entries = new List<TreeListViewEntry>
							{
								CreateMetadataInfoNode(assetStream),
								new(nameof(TextureMap), null,
									new TreeListViewEntry("Var1", mc.Var1),
									new TreeListViewEntry("Var2", mc.Var2),
									new TreeListViewEntry("Var3", mc.Var3),
									new TreeListViewEntry("Var4", mc.Var4),
									new TreeListViewEntry(nameof(TextureMap.TexUidMipSet1), null, mc.TexUidMipSet1.Select(arg => new TreeListViewEntry("UID", $"{arg:X16}")).ToArray()),
									new TreeListViewEntry(nameof(TextureMap.TexUidMipSet2), null, mc.TexUidMipSet2.Select(arg => new TreeListViewEntry("UID", $"{arg:X16}")).ToArray())
								)
							};
							break;
						}
						case Magic.R6AIWorldComponent:
						{
							using var stream = assetStream.StreamProvider.Invoke();
							var am = R6AIWorldComponent.Read(stream);

							entries = new List<TreeListViewEntry>
							{
								CreateMetadataInfoNode(assetStream),
								new(nameof(R6AIWorldComponent), null,
									new TreeListViewEntry(nameof(R6AIWorldComponent.Rooms), null,
										am.Rooms.Select(area => new TreeListViewEntry("Area", null,
											new TreeListViewEntry("Name", area.Name),
											new TreeListViewEntry("UIDs", null, area.Uids.Select(arg => new TreeListViewEntry("UID", $"{arg:X16}")).ToArray())
										)).ToArray()
									)
								)
							};
							break;
						}
						default:
						{
							entries = new List<TreeListViewEntry>
							{
								CreateMetadataInfoNode(assetStream),
							};
							break;
						}
					}

					OnUiThread(() =>
					{
						_infoControl.SetObjects(entries);
						_infoControl.ExpandAll();
						SetPreviewPanel(_infoControl);
					});

					break;
				}
			}
		}

		private List<TexturedSceneMesh> ReadEntityPreview(AssetStream asset)
		{
			if (asset.StreamType != AssetStreamType.ArchiveEntry ||
			    asset.ParentUid is not { } parentUid ||
			    _openedForge.GetContainer(parentUid) is not ForgeAsset container)
				return [];

			using var payload = container.GetDataStream(_openedForge);
			var archive = FlatArchive.Read(_openedForge, container, payload);
			return BuildArchiveScene(archive, payload.BaseStream, new Dictionary<ulong, SceneTextureData>(),
				CancellationToken.None, null, null, loadTextures: true, _openedForge, _dependencyResolver, entityUid: asset.MetaData.Uid).Meshes;
		}

		private static TreeListViewEntry CreateUidLinkEntryNode(UidLinkEntry ule)
		{
			var node1 = new TreeListViewEntry(nameof(UidLinkEntry.UidLinkNode1), ule.UidLinkNode1 == null ? "null" : $"{ule.UidLinkNode1.LinkedUid:X16}");
			var node2 = new TreeListViewEntry(nameof(UidLinkEntry.UidLinkNode2), ule.UidLinkNode2 == null ? "null" : $"{ule.UidLinkNode2.LinkedUid:X16}");
			return new TreeListViewEntry(nameof(UidLinkNode), null, node1, node2);
		}

		private static TreeListViewEntry CreateMipContainerReferenceNode(TextureSelector mcr)
		{
			return new TreeListViewEntry("MipContainerReference", null,
				new TreeListViewEntry("Var1", mcr.Var1),
				new TreeListViewEntry("MipTarget", $"{mcr.MipTarget:X8}"),
				new TreeListViewEntry("MipContainerUid", $"{mcr.TextureMapSpecUid:X16}")
			);
		}

		private static TreeListViewEntry CreateMetadataInfoNode(AssetStream stream)
		{
			return new TreeListViewEntry("Metadata", null,
				new TreeListViewEntry("Filename", stream.MetaData.Filename),
				new TreeListViewEntry("UID", $"{stream.MetaData.Uid:X16}"),
				new TreeListViewEntry("FileType", $"{stream.MetaData.Magic:X8}"),
				new TreeListViewEntry("Magic", (Magic)stream.MetaData.Magic),
				new TreeListViewEntry("AssetType", MagicHelper.GetFiletype(stream.MetaData.Magic))
			);
		}

		private static bool IsForgeFile(string path) =>
			string.Equals(Path.GetExtension(path), ".forge", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

		private void SelectForgeFiles(string[] filenames)
		{
			_previewCancellation?.Cancel();
			ResetAudioPreview();
			var failures = new List<string>();
			foreach (var filename in filenames.Where(IsForgeFile).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
			{
				if (_loadedForges.Keys.Any(forge => string.Equals(forge.SourcePath, filename, StringComparison.OrdinalIgnoreCase)))
					continue;
				try
				{
					var forge = Forge.GetForge(filename);
					_loadedForges.Add(forge, new AssetDependencyResolver(forge.SourcePath));
				}
				catch (Exception error)
				{
					failures.Add($"{Path.GetFileName(filename)}: {error.Message}");
				}
			}
			if (_loadedForges.Count > 0)
			{
				ActivateForge(_loadedForges.Keys.First());
				_assetList.SetForges(_loadedForges.Keys);
				_assetList.SelectFirstNode();
				Text = $"Prism - {_loadedForges.Count:N0} Forge file(s)";
				_statusForgeInfo.Text = $"{_loadedForges.Keys.Sum(forge => (long)forge.Entries.Length):N0} entries";
			}
			if (failures.Count > 0)
				MessageBox.Show(this, string.Join(Environment.NewLine, failures), "Open Forge", MessageBoxButtons.OK, MessageBoxIcon.Error);
		}

		protected override void OnFormClosed(FormClosedEventArgs e)
		{
			ResetAudioPreview();
			_previewCancellation?.Cancel();
			foreach (var (forge, resolver) in _loadedForges)
			{
				resolver.Dispose();
				forge.Stream.Dispose();
			}
			base.OnFormClosed(e);
		}

		private static void GenerateFileList(string[] forgeFiles)
		{
			var fileListPath = Path.Combine(Environment.CurrentDirectory, "filelist.txt");

			using var sw = new StreamWriter(fileListPath);
			foreach (var forge in forgeFiles)
			{
				sw.WriteLine(Path.GetFileName(forge));
				var currentForge = Forge.GetForge(forge);
				foreach (var entry in currentForge.Entries)
				{
					var entryMetaData = GetAssetMetaData(entry);
					sw.WriteLine("> " + entryMetaData.Uid.ToString("X16") + ": " + entryMetaData.Filename + "." + (Magic)entryMetaData.Magic);

					if (AssetTypeDetector.DetectAssetType(currentForge, entry) != AssetType.FlatArchive || currentForge.GetContainer(entryMetaData.Uid) is not ForgeAsset fa)
						continue;

					var archiveStream = fa.GetDataStream(currentForge);
					var archive = FlatArchive.Read(currentForge, fa, archiveStream);

					foreach (var archiveEntry in archive.Entries[1..]) // first archive entry always has the same UID, Magic and Name so we skip it
					{
						entryMetaData = GetAssetMetaData(archiveEntry);
						sw.WriteLine(">> " + entryMetaData.Uid.ToString("X16") + ": " + entryMetaData.Filename + "." + (Magic)entryMetaData.Magic);
					}
				}
			}

			MessageBox.Show("Successfully generated filelist.txt", "Done");
		}
	}
}
