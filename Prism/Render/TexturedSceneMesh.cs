using OpenTK;
using RainbowForge.Model;

namespace Prism.Render;

public sealed class TexturedSceneMesh
{
	public ulong SourceUid { get; }
	public CompiledMeshObject Mesh { get; }
	public SceneTextureData[] ObjectTextures { get; }
	public Matrix4[] Instances { get; }

	public TexturedSceneMesh(ulong sourceUid, CompiledMeshObject mesh, SceneTextureData[] objectTextures, Matrix4[] instances)
	{
		SourceUid = sourceUid;
		Mesh = mesh;
		ObjectTextures = objectTextures ?? [];
		Instances = instances is { Length: > 0 } ? instances : [Matrix4.Identity];
	}
}
