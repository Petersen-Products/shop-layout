using System;
using System.Globalization;

namespace ShopLayout.Core.Geometry
{
    public struct Vec2
    {
        public double X, Y;
        public Vec2(double x, double y) { X = x; Y = y; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
        public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
        public static Vec2 operator *(double s, Vec2 a) => new Vec2(a.X * s, a.Y * s);

        public double Dot(Vec2 b) => X * b.X + Y * b.Y;
        /// <summary>Z component of the 3D cross product (positive when b is counter-clockwise from this).</summary>
        public double Cross(Vec2 b) => X * b.Y - Y * b.X;
        public double Length => Math.Sqrt(X * X + Y * Y);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.######}, {1:0.######})", X, Y);
    }

    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 UnitX = new Vec3(1, 0, 0);
        public static readonly Vec3 UnitY = new Vec3(0, 1, 0);
        public static readonly Vec3 UnitZ = new Vec3(0, 0, 1);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.X, -a.Y, -a.Z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.X * s, a.Y * s, a.Z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.X * s, a.Y * s, a.Z * s);

        public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec3 Cross(Vec3 b) => new Vec3(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public Vec3 Normalized()
        {
            double l = Length;
            return l > 1e-15 ? new Vec3(X / l, Y / l, Z / l) : this;
        }

        public double[] ToArray() => new[] { X, Y, Z };

        public static Vec3 FromArray(double[] a, int offset = 0) => new Vec3(a[offset], a[offset + 1], a[offset + 2]);

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.######}, {1:0.######}, {2:0.######})", X, Y, Z);
    }

    /// <summary>Axis-aligned 3D box accumulator.</summary>
    public sealed class Box3
    {
        public Vec3 Min = new Vec3(double.MaxValue, double.MaxValue, double.MaxValue);
        public Vec3 Max = new Vec3(double.MinValue, double.MinValue, double.MinValue);
        public bool IsEmpty => Min.X > Max.X;

        public void Add(Vec3 p)
        {
            if (p.X < Min.X) Min.X = p.X; if (p.Y < Min.Y) Min.Y = p.Y; if (p.Z < Min.Z) Min.Z = p.Z;
            if (p.X > Max.X) Max.X = p.X; if (p.Y > Max.Y) Max.Y = p.Y; if (p.Z > Max.Z) Max.Z = p.Z;
        }

        public Vec3 Size => IsEmpty ? Vec3.Zero : Max - Min;

        public Vec3[] Corners()
        {
            var c = new Vec3[8];
            for (int i = 0; i < 8; i++)
                c[i] = new Vec3((i & 1) == 0 ? Min.X : Max.X, (i & 2) == 0 ? Min.Y : Max.Y, (i & 4) == 0 ? Min.Z : Max.Z);
            return c;
        }

        /// <summary>Box from SolidWorks' [x1,y1,z1,x2,y2,z2] corner array (corners may be in any order).</summary>
        public static Box3 FromCorners(double[] six)
        {
            if (six == null || six.Length < 6) return null;
            var b = new Box3();
            b.Add(new Vec3(six[0], six[1], six[2]));
            b.Add(new Vec3(six[3], six[4], six[5]));
            return b;
        }

        /// <summary>Largest per-axis difference between the two boxes' corresponding bounds.</summary>
        public double MaxDeviation(Box3 other)
        {
            double d = 0;
            d = Math.Max(d, Math.Abs(Min.X - other.Min.X)); d = Math.Max(d, Math.Abs(Min.Y - other.Min.Y)); d = Math.Max(d, Math.Abs(Min.Z - other.Min.Z));
            d = Math.Max(d, Math.Abs(Max.X - other.Max.X)); d = Math.Max(d, Math.Abs(Max.Y - other.Max.Y)); d = Math.Max(d, Math.Abs(Max.Z - other.Max.Z));
            return d;
        }
    }
}
