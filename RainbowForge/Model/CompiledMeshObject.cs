using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace RainbowForge.Model
{
	/// <summary>
	///     Mesh:
	///     verts   - [(float, float, float), ...]
	///     normals - [(float, float, float), ...]
	///     tangs   - [(float, float, float), ...]
	///     colors  - [(int, int, int, int), ...]
	///     unar1   - [(int, int, int, int), ...] for now
	///     uvs     - [(float, float), ...]
	///     islands - [    [(int, int, int), ...], ...    ]
	///     verts_data_len      - int
	///     verts_data_len - int
	///     tris_data_len - int
	///     num_verts      - int
	///     num_islands   - int
	/// </summary>
	public class CompiledMeshObject
	{
		private const int HeaderSize = 0x4C;
		private const int FaceChunkSize = 0x180;
		private const int TrisInChunk = 64;

		public MeshHeader MeshHeader { get; }
		public MeshObjectContainer Container { get; }
		public List<TrianglePointer[]> Objects { get; }
		public List<ushort[]> VertMaps { get; }
		public FaceBlockStat[] FaceStatBlocks { get; }
		public UnknownFaceDataBlock[] UnknownFaceData { get; }

		private CompiledMeshObject(MeshHeader meshHeader, MeshObjectContainer container, List<TrianglePointer[]> objects, List<ushort[]> vertMaps, FaceBlockStat[] faceStatBlocks,
			UnknownFaceDataBlock[] unknownFaceData)
		{
			MeshHeader = meshHeader;
			Container = container;
			Objects = objects;
			VertMaps = vertMaps;
			FaceStatBlocks = faceStatBlocks;
			UnknownFaceData = unknownFaceData;
		}

		public static CompiledMeshObject Read(BinaryReader r, MeshHeader header)
		{
			r.BaseStream.Seek(header.VertBlockOffset, SeekOrigin.Begin);

			var container = header.HasLegacyLayout
				? ReadLegacyVertices(r, header)
				: ReadVertices(r, header);

			var objects = ParseFaceBlocks(r, header);
			var vertMaps = ParseVertMaps(r, header);
			var faceStatBlocks = ParseFaceStatBlocks(r, header);
			var unknownFaceData = ParseUnknownFaceData(r, header);

			return new CompiledMeshObject(header, container, objects, vertMaps, faceStatBlocks, unknownFaceData);
		}

		private static MeshObjectContainer ReadVertices(BinaryReader r, MeshHeader header)
		{
			var container = CreateObjectContainer(header.Revision, header.VertLen, header.NumVerts);
			switch (header.Revision)
			{
				case 0:
				{
					switch (header.VertLen)
					{
						case 0x18:
						case 0x1C:
						{
							var skipBytes = (int) (header.VertLen - 12);

							for (var i = 0; i < header.NumVerts; i++)
							{
								container.Vertices[i] = r.ReadUInt64AsPos();
								r.ReadBytes(skipBytes);
								container.TexCoords[i] = r.ReadUInt32AsUv();
							}

							break;
						}
					}

					break;
				}
				case 1:
				case 2:
				{
					switch (header.VertLen)
					{
						case 0x18:
						case 0x1C:
						{
							for (var i = 0; i < header.NumVerts; i++)
								container.Vertices[i] = r.ReadUInt64AsPos();

							for (var i = 0; i < header.NumVerts; i++)
								container.Normals[i] = r.ReadUInt32AsVec();

							// Compact vertices use the same separate attribute streams as the
							// float-position layout: position, normal, tangent, binormal, UV, color.
							// Reading UVs here previously interpreted packed tangent bytes as half floats.
							for (var i = 0; i < header.NumVerts; i++)
								container.Tangents[i] = r.ReadUInt32AsVec();
							for (var i = 0; i < header.NumVerts; i++)
								container.Binormals[i] = r.ReadUInt32AsVec();

							for (var i = 0; i < header.NumVerts; i++)
								container.TexCoords[i] = r.ReadUInt32AsUv();

							if (header.VertLen == 0x1C)
								for (var i = 0; i < header.NumVerts; i++)
									container.Colors[0, i] = r.ReadUInt32AsColor();

							break;
						}
						case 0x24:
						case 0x28:
						case 0x2C:
						{
							const int sizeDataBeforeColor = sizeof(float) * 3 + sizeof(uint) * 4;

							for (var i = 0; i < header.NumVerts; i++)
								container.Vertices[i] = r.ReadVector3();

							for (var i = 0; i < header.NumVerts; i++)
								container.Normals[i] = r.ReadUInt32AsVec();

							for (var i = 0; i < header.NumVerts; i++)
								container.Tangents[i] = r.ReadUInt32AsVec();

							for (var i = 0; i < header.NumVerts; i++)
								container.Binormals[i] = r.ReadUInt32AsVec();

							for (var i = 0; i < header.NumVerts; i++)
								container.TexCoords[i] = r.ReadUInt32AsUv();

							var numColorTables = (header.VertLen - sizeDataBeforeColor) / sizeof(uint);
							for (var t = 0; t < numColorTables; t++)
							{
								for (var i = 0; i < header.NumVerts; i++)
									container.Colors[t, i] = r.ReadUInt32AsColor();
							}

							break;
						}
						case 0x5C:
						{
							for (var i = 0; i < header.NumVerts; i++)
							{
								container.Vertices[i] = r.ReadVector3();

								// unknown struct (20 float)
								r.ReadBytes(sizeof(float) * 20);
							}

							break;
						}
					}

					break;
				}
			}

			return container;
		}

		private static MeshObjectContainer ReadLegacyVertices(BinaryReader r, MeshHeader header)
		{
			if (header.Revision > 2 || header.VertsDataLen % header.VertLen != 0)
				throw new InvalidDataException("Invalid legacy mesh vertex buffer.");

			var compact = header.VertLen is 0x18 or 0x1c;
			var hasAttributes = header.VertLen != 0x5c;
			var vertices = new MeshObjectContainer
			{
				Vertices = new Vector3[header.NumVerts],
				Normals = hasAttributes ? new Vector3[header.NumVerts] : null,
				Tangents = hasAttributes ? new Vector3[header.NumVerts] : null,
				Binormals = hasAttributes && !compact ? new Vector3[header.NumVerts] : null,
				TexCoords = hasAttributes ? new Vector2[header.NumVerts] : null,
				Colors = compact ? new Color4[1, header.NumVerts] : null
			};

			for (var i = 0; i < vertices.Vertices.Length; i++)
			{
				var nextVertex = r.BaseStream.Position + header.VertLen;
				vertices.Vertices[i] = compact ? r.ReadUInt64AsPos() : r.ReadVector3();
				if (hasAttributes)
				{
					vertices.Normals[i] = r.ReadUInt32AsVec();
					vertices.Tangents[i] = r.ReadUInt32AsVec();
					if (compact)
						vertices.Colors[0, i] = r.ReadUInt32AsColor();
					else
						vertices.Binormals[i] = r.ReadUInt32AsVec();
					vertices.TexCoords[i] = r.ReadUInt32AsUv();
				}
				r.BaseStream.Position = nextVertex;
			}
			return vertices;
		}

		private static UnknownFaceDataBlock[] ParseUnknownFaceData(BinaryReader r, MeshHeader header)
		{
			var data = new UnknownFaceDataBlock[header.FaceUnknownDataLen / 4];

			for (var i = 0; i < data.Length; i++)
				data[i] = r.ReadStruct<UnknownFaceDataBlock>(4);

			return data;
		}

		private static FaceBlockStat[] ParseFaceStatBlocks(BinaryReader r, MeshHeader header)
		{
			if (header.HasLegacyLayout && header.Revision == 0)
				return [];
			r.BaseStream.Seek(header.ExtraDataStart + header.VertmapsDataLen + header.UnknownBlock1Len, SeekOrigin.Begin);

			var stats = new FaceBlockStat[header.FaceDataLen / FaceChunkSize];

			for (var i = 0; i < stats.Length; i++)
				stats[i] = r.ReadStruct<FaceBlockStat>(sizeof(float) * 11);

			return stats;
		}

		private static List<ushort[]> ParseVertMaps(BinaryReader r, MeshHeader header)
		{
			var vertmaps = new List<ushort[]>();

			if (header.VertmapsDataLen == 0)
				return vertmaps;

			r.BaseStream.Seek(header.ExtraDataStart, SeekOrigin.Begin);

			for (var i = 0; i < header.NumLods; i++)
			{
				var mapping = new ushort[header.NumVerts];
				for (var j = 0; j < mapping.Length; j++)
					mapping[j] = r.ReadUInt16();

				vertmaps.Add(mapping);
			}

			return vertmaps;
		}

		private static List<TrianglePointer[]> ParseFaceBlocks(BinaryReader r, MeshHeader header)
		{
			r.BaseStream.Seek(header.TrisBlockStart, SeekOrigin.Begin);

			var objects = new List<TrianglePointer[]>();
			// Early revision-0 meshes store unpadded triangles instead of 64-triangle chunks.
			var trianglesPerUnit = header.HasLegacyLayout && header.Revision == 0 ? 1 : TrisInChunk;
			var faceUnitSize = trianglesPerUnit * 3 * sizeof(ushort);

			foreach (var islandMeta in header.ObjectHeaders)
			{
				var vertexBase = 0;
				if (header.HasLegacyLayout)
				{
					var offset = (long)islandMeta.OffsetFaceChunks * faceUnitSize;
					var length = (long)islandMeta.NumFaceChunks * faceUnitSize;
					if (length != 0 && offset + length > header.FaceDataLen)
						throw new InvalidDataException("Mesh part extends beyond the face buffer.");
					r.BaseStream.Position = header.TrisBlockStart + offset;
					vertexBase = checked((int)islandMeta.OffsetVerts);
				}
				var triIndices = new TrianglePointer[checked(islandMeta.NumFaceChunks * trianglesPerUnit)];
				for (var i = 0; i < triIndices.Length; i++)
				{
					var a = r.ReadUInt16();
					var b = r.ReadUInt16();
					var c = r.ReadUInt16();
					triIndices[i] = new TrianglePointer(vertexBase + a, vertexBase + b, vertexBase + c);
				}

				if (triIndices.Length > 0)
				{
					var triangles = triIndices.Where(t => t.A != t.B && t.B != t.C && (!header.HasLegacyLayout || t.A != t.C)).ToArray();
					if (header.HasLegacyLayout && triangles.Any(t => (uint)t.A >= header.NumVerts || (uint)t.B >= header.NumVerts || (uint)t.C >= header.NumVerts))
						throw new InvalidDataException("Mesh face references a vertex outside the vertex buffer.");
					objects.Add(triangles);
				}
				else
					objects.Add(Array.Empty<TrianglePointer>());
			}

			return objects;
		}

		private static MeshObjectContainer CreateObjectContainer(uint revision, uint vertLen, uint numVerts)
		{
			switch (revision)
			{
				case 0 when vertLen == 0x18 || vertLen == 0x1C:
					return new MeshObjectContainer
					{
						Vertices = new Vector3[numVerts],
						TexCoords = new Vector2[numVerts]
					};
				case 1:
				case 2:
				{
					switch (vertLen)
					{
						case 0x18:
							return new MeshObjectContainer
							{
								Vertices = new Vector3[numVerts],
								Normals = new Vector3[numVerts],
								Tangents = new Vector3[numVerts],
								Binormals = new Vector3[numVerts],
								TexCoords = new Vector2[numVerts]
							};
						case 0x1C:
							return new MeshObjectContainer
							{
								Vertices = new Vector3[numVerts],
								Normals = new Vector3[numVerts],
								Tangents = new Vector3[numVerts],
								Binormals = new Vector3[numVerts],
								TexCoords = new Vector2[numVerts],
								Colors = new Color4[1, numVerts]
							};
						case 0x24:
							return new MeshObjectContainer
							{
								Vertices = new Vector3[numVerts],
								Normals = new Vector3[numVerts],
								Tangents = new Vector3[numVerts],
								Binormals = new Vector3[numVerts],
								TexCoords = new Vector2[numVerts],
								Colors = new Color4[8, numVerts]
							};
						case 0x28:
							return new MeshObjectContainer
							{
								Vertices = new Vector3[numVerts],
								Normals = new Vector3[numVerts],
								Tangents = new Vector3[numVerts],
								Binormals = new Vector3[numVerts],
								TexCoords = new Vector2[numVerts],
								Colors = new Color4[12, numVerts]
							};
						case 0x2C:
							return new MeshObjectContainer
							{
								Vertices = new Vector3[numVerts],
								Normals = new Vector3[numVerts],
								Tangents = new Vector3[numVerts],
								Binormals = new Vector3[numVerts],
								TexCoords = new Vector2[numVerts],
								Colors = new Color4[16, numVerts]
							};
						case 0x5C:
							return new MeshObjectContainer
							{
								Vertices = new Vector3[numVerts]
								// Also has an unknown array of struct (20 float)
							};
					}

					break;
				}
			}

			throw new InvalidDataException($"Unknown mesh vertex length: 0x{vertLen:X}, revision: {revision}");
		}
	}
}
