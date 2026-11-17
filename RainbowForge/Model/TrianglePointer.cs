using System;

namespace RainbowForge.Model
{
	public class TrianglePointer
	{
		public int A { get; }
		public int B { get; }
		public int C { get; }

		public TrianglePointer(int a, int b, int c)
		{
			A = a;
			B = b;
			C = c;
		}

		protected bool Equals(TrianglePointer other)
		{
			return A == other.A && B == other.B && C == other.C;
		}

		/// <inheritdoc />
		public override bool Equals(object obj)
		{
			if (ReferenceEquals(null, obj)) return false;
			if (ReferenceEquals(this, obj)) return true;
			if (obj.GetType() != GetType()) return false;
			return Equals((TrianglePointer) obj);
		}

		/// <inheritdoc />
		public override int GetHashCode()
		{
			return HashCode.Combine(A, B, C);
		}

		public static bool operator ==(TrianglePointer left, TrianglePointer right)
		{
			return Equals(left, right);
		}

		public static bool operator !=(TrianglePointer left, TrianglePointer right)
		{
			return !Equals(left, right);
		}
	}
}
