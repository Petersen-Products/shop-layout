using System;

namespace ShopLayout.Core.Geometry
{
    /// <summary>
    /// The export coordinate system. Floor coordinates are (x, y) along U and V; elevation is along N = U × V.
    /// All floor-space output (poses, footprints, bounds) is expressed in this frame; the original
    /// root-assembly matrices are exported unchanged next to it.
    /// </summary>
    public sealed class FloorFrame
    {
        public string Name { get; }
        public Vec3 Origin { get; }
        public Vec3 U { get; }
        public Vec3 V { get; }
        public Vec3 N { get; }
        /// <summary>Floor → root assembly.</summary>
        public Affine3 FloorToRoot { get; }
        /// <summary>Root assembly → floor.</summary>
        public Affine3 RootToFloor { get; }

        public FloorFrame(string name, Vec3 origin, Vec3 u, Vec3 v)
        {
            var uu = u.Normalized();
            var n = uu.Cross(v).Normalized();
            if (n.Length < 0.5) throw new ArgumentException("Floor U and V axes must not be parallel.");
            var vv = n.Cross(uu).Normalized();
            Name = name;
            Origin = origin;
            U = uu; V = vv; N = n;
            FloorToRoot = Affine3.FromBasis(origin, uu, vv, n);
            RootToFloor = FloorToRoot.Inverse();
        }

        /// <summary>
        /// Presets. "XY": floor = assembly XY, up = +Z. "XZ": SolidWorks' default Top Plane, up = +Y,
        /// drawn like the Top view (x = X, y = −Z). "YZ": up = +X.
        /// </summary>
        public static FloorFrame FromPreset(string plane, Vec3 origin)
        {
            switch ((plane ?? "XY").Trim().ToUpperInvariant())
            {
                case "XY": return new FloorFrame("XY", origin, Vec3.UnitX, Vec3.UnitY);
                case "XZ": return new FloorFrame("XZ", origin, Vec3.UnitX, new Vec3(0, 0, -1));
                case "YZ": return new FloorFrame("YZ", origin, Vec3.UnitY, Vec3.UnitZ);
                default: throw new ArgumentException($"Unknown floor plane preset '{plane}'. Use XY, XZ, YZ, custom or referencePlane.");
            }
        }

        /// <summary>Frame from a SolidWorks reference-plane transform: plane X/Y become U/V, plane normal is up.</summary>
        public static FloorFrame FromPlaneTransform(string name, double[] arrayData)
        {
            var m = Affine3.FromSolidWorksArray(arrayData);
            return new FloorFrame(name, m.T, m.Column(0), m.Column(1));
        }
    }

    /// <summary>A signed principal axis of a component's local frame (+X, −X, +Y, −Y, +Z, −Z).</summary>
    public sealed class LocalAxis
    {
        public string Name { get; }
        public Vec3 Up { get; }
        /// <summary>In-plane footprint axes. Chosen so that A × B = Up (right-handed footprint coordinates).</summary>
        public Vec3 A { get; }
        public Vec3 B { get; }
        public string AName { get; }
        public string BName { get; }

        private LocalAxis(string name, Vec3 up, Vec3 a, string aName, Vec3 b, string bName)
        {
            Name = name; Up = up; A = a; B = b; AName = aName; BName = bName;
        }

        public static readonly LocalAxis PosZ = new LocalAxis("+Z", Vec3.UnitZ, Vec3.UnitX, "+X", Vec3.UnitY, "+Y");
        public static readonly LocalAxis NegZ = new LocalAxis("-Z", -Vec3.UnitZ, Vec3.UnitY, "+Y", Vec3.UnitX, "+X");
        // +Y up uses (X, −Z) so an unrotated Y-up part on the XZ floor reads 0°, matching SolidWorks' Top view.
        public static readonly LocalAxis PosY = new LocalAxis("+Y", Vec3.UnitY, Vec3.UnitX, "+X", -Vec3.UnitZ, "-Z");
        public static readonly LocalAxis NegY = new LocalAxis("-Y", -Vec3.UnitY, Vec3.UnitX, "+X", Vec3.UnitZ, "+Z");
        public static readonly LocalAxis PosX = new LocalAxis("+X", Vec3.UnitX, Vec3.UnitY, "+Y", Vec3.UnitZ, "+Z");
        public static readonly LocalAxis NegX = new LocalAxis("-X", -Vec3.UnitX, Vec3.UnitZ, "+Z", Vec3.UnitY, "+Y");

        public static readonly LocalAxis[] All = { PosZ, NegZ, PosY, NegY, PosX, NegX };

        public static LocalAxis Parse(string name)
        {
            foreach (var a in All) if (string.Equals(a.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        /// <summary>Footprint coordinates (a, b, h) of a local-frame point.</summary>
        public Vec3 ToFootprint(Vec3 p) => new Vec3(p.Dot(A), p.Dot(B), p.Dot(Up));

        /// <summary>Local-frame point from footprint coordinates (a, b, h).</summary>
        public Vec3 FromFootprint(double a, double b, double h) => A * a + B * b + Up * h;

        /// <summary>
        /// Chooses the local principal axis whose image (under a local→floor transform) is closest to floor up.
        /// Returns the tilt angle (degrees) between that axis image and the floor normal.
        /// </summary>
        public static LocalAxis DetectUp(Affine3 localToFloor, out double tiltDegrees)
        {
            LocalAxis best = PosZ;
            double bestCos = double.MinValue;
            foreach (var axis in All)
            {
                var img = localToFloor.ApplyVector(axis.Up);
                double len = img.Length;
                if (len < 1e-12) continue;
                double cos = img.Z / len;
                if (cos > bestCos + 1e-12) { bestCos = cos; best = axis; }
            }
            tiltDegrees = Math.Acos(Math.Max(-1, Math.Min(1, bestCos))) * 180.0 / Math.PI;
            return best;
        }
    }
}
