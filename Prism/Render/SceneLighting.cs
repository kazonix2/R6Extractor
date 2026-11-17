using System;
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
	float OuterConeCosine)
{
	public float InnerConeCosine { get; init; } = Math.Min(1f, OuterConeCosine + 0.12f);
}

public sealed record BakedGiLighting(
	Vector3 AmbientColor,
	float AmbientIntensity,
	long SampleCount,
	long TailCellCount,
	string SourcePath)
{
	internal BakedGiGrid[] Grids { get; init; } = [];

	public static BakedGiLighting Default { get; } = new(Vector3.One, 0.32f, 0, 0, string.Empty);
}

internal sealed record BakedGiGrid(int CellCount, byte[] SampleTiles);
