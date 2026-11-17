using OpenTK;

namespace Prism.Render;

public sealed record SceneMaterial(SceneTextureData Diffuse, SceneTextureData Normal, SceneTextureData Specular, Vector2 UvScale)
{
	public SceneTextureData.AlphaMode? Opacity { get; init; }
	public Vector3 Tint { get; init; } = Vector3.One;
	public float? GlassOpacity { get; init; }
	public SceneTextureData BlendDiffuse { get; init; }
	public SceneTextureData BlendMask { get; init; }
	public Vector2 BlendUvScale { get; init; } = Vector2.One;
}
