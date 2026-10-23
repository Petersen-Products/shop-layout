using System;

namespace ShopLayout.Core.Geometry
{
    /// <summary>
    /// 3D affine transform using the column-vector convention: p' = L·p + T.
    ///
    /// SolidWorks MathTransform.ArrayData layout (16 doubles):
    ///   [0..2]  image of the local X axis in the parent frame
    ///   [3..5]  image of the local Y axis
    ///   [6..8]  image of the local Z axis
    ///   [9..11] translation (metres)
    ///   [12]    uniform scale
    ///   [13..15] unused
    /// i.e. x' = s·(a0·x + a3·y + a6·z) + a9, and so on. <see cref="FromSolidWorksArray"/> converts that
    /// into L (whose columns are the scaled axis images) and T.
    /// </summary>
    public sealed class Affine3
    {
        /// <summary>Row-major 3x3 linear part.</summary>
        public readonly double[] L = new double[9];
        public Vec3 T;

        public static Affine3 Identity()
        {
            var a = new Affine3();
            a.L[0] = a.L[4] = a.L[8] = 1;
            return a;
        }

        public static Affine3 FromSolidWorksArray(double[] a)
        {
            if (a == null || a.Length < 12) throw new ArgumentException("SolidWorks transform array needs at least 12 values.");
            double s = a.Length > 12 && a[12] != 0 && !double.IsNaN(a[12]) ? a[12] : 1.0;
            var m = new Affine3();
            // column 0 = local X image, column 1 = local Y image, column 2 = local Z image
            m.L[0] = a[0] * s; m.L[1] = a[3] * s; m.L[2] = a[6] * s;
            m.L[3] = a[1] * s; m.L[4] = a[4] * s; m.L[5] = a[7] * s;
            m.L[6] = a[2] * s; m.L[7] = a[5] * s; m.L[8] = a[8] * s;
            m.T = new Vec3(a[9], a[10], a[11]);
            return m;
        }

        /// <summary>Transform whose columns are the given axes and whose translation is the origin.</summary>
        public static Affine3 FromBasis(Vec3 origin, Vec3 x, Vec3 y, Vec3 z)
        {
            var m = new Affine3();
            m.L[0] = x.X; m.L[1] = y.X; m.L[2] = z.X;
            m.L[3] = x.Y; m.L[4] = y.Y; m.L[5] = z.Y;
            m.L[6] = x.Z; m.L[7] = y.Z; m.L[8] = z.Z;
            m.T = origin;
            return m;
        }

        public Vec3 Column(int c) => new Vec3(L[c], L[3 + c], L[6 + c]);

        public Vec3 Apply(Vec3 p) => new Vec3(
            L[0] * p.X + L[1] * p.Y + L[2] * p.Z + T.X,
            L[3] * p.X + L[4] * p.Y + L[5] * p.Z + T.Y,
            L[6] * p.X + L[7] * p.Y + L[8] * p.Z + T.Z);

        public Vec3 ApplyVector(Vec3 v) => new Vec3(
            L[0] * v.X + L[1] * v.Y + L[2] * v.Z,
            L[3] * v.X + L[4] * v.Y + L[5] * v.Z,
            L[6] * v.X + L[7] * v.Y + L[8] * v.Z);

        /// <summary>Returns this ∘ b (apply b first, then this).</summary>
        public Affine3 Multiply(Affine3 b)
        {
            var r = new Affine3();
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    r.L[i * 3 + j] = L[i * 3] * b.L[j] + L[i * 3 + 1] * b.L[3 + j] + L[i * 3 + 2] * b.L[6 + j];
            r.T = Apply(b.T);
            return r;
        }

        public double Determinant =>
            L[0] * (L[4] * L[8] - L[5] * L[7]) - L[1] * (L[3] * L[8] - L[5] * L[6]) + L[2] * (L[3] * L[7] - L[4] * L[6]);

        /// <summary>Uniform scale estimate (cube root of |det|).</summary>
        public double UniformScale => Math.Pow(Math.Abs(Determinant), 1.0 / 3.0);

        public Affine3 Inverse()
        {
            double det = Determinant;
            if (Math.Abs(det) < 1e-300) throw new InvalidOperationException("Transform is singular.");
            var r = new Affine3();
            double id = 1.0 / det;
            r.L[0] = (L[4] * L[8] - L[5] * L[7]) * id;
            r.L[1] = (L[2] * L[7] - L[1] * L[8]) * id;
            r.L[2] = (L[1] * L[5] - L[2] * L[4]) * id;
            r.L[3] = (L[5] * L[6] - L[3] * L[8]) * id;
            r.L[4] = (L[0] * L[8] - L[2] * L[6]) * id;
            r.L[5] = (L[2] * L[3] - L[0] * L[5]) * id;
            r.L[6] = (L[3] * L[7] - L[4] * L[6]) * id;
            r.L[7] = (L[1] * L[6] - L[0] * L[7]) * id;
            r.L[8] = (L[0] * L[4] - L[1] * L[3]) * id;
            var t = r.ApplyVector(T);
            r.T = new Vec3(-t.X, -t.Y, -t.Z);
            return r;
        }

        /// <summary>Row-major 4x4 matrix for the column-vector convention (last row 0,0,0,1).</summary>
        public double[] ToRowMajor4x4() => new[]
        {
            L[0], L[1], L[2], T.X,
            L[3], L[4], L[5], T.Y,
            L[6], L[7], L[8], T.Z,
            0, 0, 0, 1.0
        };

        public static Affine3 FromRowMajor4x4(double[] m)
        {
            var a = new Affine3();
            a.L[0] = m[0]; a.L[1] = m[1]; a.L[2] = m[2]; a.T.X = m[3];
            a.L[3] = m[4]; a.L[4] = m[5]; a.L[5] = m[6]; a.T.Y = m[7];
            a.L[6] = m[8]; a.L[7] = m[9]; a.L[8] = m[10]; a.T.Z = m[11];
            return a;
        }

        /// <summary>Largest deviation of L·Lᵀ/s² from identity: 0 for a pure rotation (+uniform scale).</summary>
        public double OrthogonalityError()
        {
            double s2 = Math.Pow(UniformScale, 2);
            if (s2 < 1e-300) return double.PositiveInfinity;
            double err = 0;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double d = (L[i * 3] * L[j * 3] + L[i * 3 + 1] * L[j * 3 + 1] + L[i * 3 + 2] * L[j * 3 + 2]) / s2;
                    err = Math.Max(err, Math.Abs(d - (i == j ? 1 : 0)));
                }
            return err;
        }
    }
}
