using OpenTK;
using RainbowForge.Model;

namespace Prism.Render;

public sealed class TexturedSceneMesh
{
	public ulong SourceUid { get; }
	public CompiledMeshObject Mesh { get; }
	public SceneMaterial[] Materials { get; }
	public Matrix4[] Instances { get; }

	public TexturedSceneMesh(ulong sourceUid, CompiledMeshObject mesh, SceneMaterial[] materials, Matrix4[] instances)
	{
		SourceUid = sourceUid;
		Mesh = mesh;
		Materials = materials ?? [];
		Instances = instances is { Length: > 0 } ? instances : [Matrix4.Identity];
	}
}
