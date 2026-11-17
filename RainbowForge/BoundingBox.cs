using System.IO;

namespace RainbowForge
{
	public class BoundingBox
	{
		public float MinX { get; }
		public float MinY { get; }
		public float MinZ { get; }
		public float MaxX { get; }
		public float MaxY { get; }
		public float MaxZ { get; }

		private BoundingBox(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
		{
			MinX = minX;
			MinY = minY;
			MinZ = minZ;
			MaxX = maxX;
			MaxY = maxY;
			MaxZ = maxZ;
		}

		public static BoundingBox Read(BinaryReader r)
		{
			// TODO: These might not be in this order
			var minX = r.ReadSingle();
			var minY = r.ReadSingle();
			var minZ = r.ReadSingle();

			var maxX = r.ReadSingle();
			var maxY = r.ReadSingle();
			var maxZ = r.ReadSingle();

			return new BoundingBox(minX, minY, minZ, maxX, maxY, maxZ);
		}

		internal static BoundingBox ReadLegacy(BinaryReader r)
		{
			var center = r.ReadVector3();
			var extent = r.ReadVector3();
			r.ReadSingle();
			var min = center - extent;
			var max = center + extent;
			return new BoundingBox(min.X, min.Y, min.Z, max.X, max.Y, max.Z);
		}
	}
}
