using OpenTK;

namespace Prism.Render;

public enum SceneLightKind
{
	Omni,
	Spot
}

public sealed record SceneLight(
	ulong SourceUid,
	SceneLightKind Kind,
	Vector3 Position,
	Vector3 Direction,
	Vector3 Color,
	float Intensity,
	float Radius,
	float OuterConeCosine);

public sealed record BakedGiLighting(
	Vector3 AmbientColor,
	float AmbientIntensity,
	long SampleCount,
	long TailCellCount,
	string SourcePath)
{
	public static BakedGiLighting Default { get; } = new(Vector3.One, 0.45f, 0, 0, string.Empty);
}
