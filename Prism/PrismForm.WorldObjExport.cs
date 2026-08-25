using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenTK;
using Prism.Render;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector3 = System.Numerics.Vector3;

namespace Prism;

public partial class PrismForm
{
	private async Task<string> ExportSelectionAsObjAsync(string outputDirectory, object selection)
	{
		var assetStream = GetAssetStream(selection);
		if (assetStream == null)
			throw new InvalidOperationException("The selected entry cannot be opened.");
		if (!IsWorldScene(assetStream))
		{
			DumpSelectionAsObj(outputDirectory, selection);
			return outputDirectory;
		}

		_previewCancellation?.Cancel();
		var cancellation = _previewCancellation = new CancellationTokenSource();
		var baseName = SanitizeFileName(string.IsNullOrWhiteSpace(assetStream.MetaData.Filename)
			? assetStream.MetaData.Uid.ToString("X16")
			: assetStream.MetaData.Filename);
		var outputPath = GetNonOverwritingPath(Path.Combine(outputDirectory, baseName + "_entire_map.obj"));
		var partialPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N");

		try
		{
			_statusForgeInfo.Text = "Exporting complete transformed map to OBJ...";
			var result = await Task.Run(() => ExportWorldObj(assetStream, partialPath, cancellation.Token,
				message => OnUiThread(() => _statusForgeInfo.Text = message)), cancellation.Token);
			File.Move(partialPath, outputPath, overwrite: false);
			_statusForgeInfo.Text = $"OBJ exported: {result.Instances:N0} instances, {result.Vertices:N0} vertices, {result.Faces:N0} faces.";
			return outputPath;
		}
		catch
		{
			if (File.Exists(partialPath))
				File.Delete(partialPath);
			throw;
		}
	}

	private WorldObjExportResult ExportWorldObj(AssetStream assetStream, string partialPath,
		CancellationToken cancellationToken, Action<string> progress)
	{
		long vertexOffset = 0;
		long vertices = 0;
		long faces = 0;
		long instances = 0;
		var meshSets = 0;
		var conversion = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(-90));

		using var file = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
		using var writer = new StreamWriter(file, new UTF8Encoding(false), 1 << 20);
		writer.WriteLine("# Prism complete World/LoadUnit export");
		writer.WriteLine("# Entity transforms followed by Siege-to-OBJ -90 degree X conversion");

		BuildWorldScene(assetStream, cancellationToken, progress, (meshChunk, _) =>
		{
			foreach (var sceneMesh in meshChunk)
			{
				cancellationToken.ThrowIfCancellationRequested();
				meshSets++;
				var mesh = sceneMesh.Mesh;
				var sourceVertices = mesh.Container.Vertices;
				var sourceNormals = mesh.Container.Normals;
				var sourceTexCoords = mesh.Container.TexCoords;
				var lod0ObjectCount = mesh.Objects.Count / (int)Math.Max(1, mesh.MeshHeader.NumLods);

				for (var instanceIndex = 0; instanceIndex < sceneMesh.Instances.Length; instanceIndex++)
				{
					cancellationToken.ThrowIfCancellationRequested();
					instances++;
					var transform = sceneMesh.Instances[instanceIndex] * conversion;
					writer.WriteLine(FormattableString.Invariant($"o mesh_{sceneMesh.SourceUid:X16}_instance_{instanceIndex:D4}"));

					for (var index = 0; index < sourceVertices.Length; index++)
					{
						var position = TransformPosition(sourceVertices[index], transform);
						writer.WriteLine(FormattableString.Invariant($"v {position.X:R} {position.Y:R} {position.Z:R}"));
					}
					for (var index = 0; index < sourceVertices.Length; index++)
					{
						var uv = sourceTexCoords != null && index < sourceTexCoords.Length ? sourceTexCoords[index] : NumericsVector2.Zero;
						writer.WriteLine(FormattableString.Invariant($"vt {uv.X:R} {uv.Y:R}"));
					}
					for (var index = 0; index < sourceVertices.Length; index++)
					{
						var sourceNormal = sourceNormals != null && index < sourceNormals.Length ? sourceNormals[index] : NumericsVector3.UnitY;
						var normal = NumericsVector3.Normalize(TransformDirection(sourceNormal, transform));
						writer.WriteLine(FormattableString.Invariant($"vn {normal.X:R} {normal.Y:R} {normal.Z:R}"));
					}

					for (var objectIndex = 0; objectIndex < lod0ObjectCount; objectIndex++)
					{
						writer.WriteLine($"g mesh_{sceneMesh.SourceUid:X16}_instance_{instanceIndex:D4}_part_{objectIndex:D3}");
						foreach (var triangle in mesh.Objects[objectIndex])
						{
							var a = vertexOffset + triangle.A + 1;
							var b = vertexOffset + triangle.B + 1;
							var c = vertexOffset + triangle.C + 1;
							writer.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
							faces++;
						}
					}
					vertexOffset += sourceVertices.Length;
					vertices += sourceVertices.Length;
				}
				progress?.Invoke($"Writing OBJ: {meshSets:N0} mesh set(s), {instances:N0} instance(s), {vertices:N0} vertices...");
			}
		}, loadTextures: false);

		writer.Flush();
		return new WorldObjExportResult(meshSets, instances, vertices, faces);
	}

	private static NumericsVector3 TransformPosition(NumericsVector3 value, Matrix4 matrix) => new(
		value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31 + matrix.M41,
		value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32 + matrix.M42,
		value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33 + matrix.M43);

	private static NumericsVector3 TransformDirection(NumericsVector3 value, Matrix4 matrix) => new(
		value.X * matrix.M11 + value.Y * matrix.M21 + value.Z * matrix.M31,
		value.X * matrix.M12 + value.Y * matrix.M22 + value.Z * matrix.M32,
		value.X * matrix.M13 + value.Y * matrix.M23 + value.Z * matrix.M33);

	private static string GetNonOverwritingPath(string requestedPath)
	{
		if (!File.Exists(requestedPath))
			return requestedPath;
		var directory = Path.GetDirectoryName(requestedPath) ?? string.Empty;
		var name = Path.GetFileNameWithoutExtension(requestedPath);
		var extension = Path.GetExtension(requestedPath);
		for (var index = 1; ; index++)
		{
			var candidate = Path.Combine(directory, $"{name}_{index}{extension}");
			if (!File.Exists(candidate))
				return candidate;
		}
	}

	private static string SanitizeFileName(string value)
	{
		foreach (var character in Path.GetInvalidFileNameChars())
			value = value.Replace(character, '_');
		return value;
	}

	private sealed record WorldObjExportResult(int MeshSets, long Instances, long Vertices, long Faces);
}
