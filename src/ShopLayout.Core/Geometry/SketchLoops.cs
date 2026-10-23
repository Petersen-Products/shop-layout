using System;
using System.Collections.Generic;
using System.Linq;

namespace ShopLayout.Core.Geometry
{
    /// <summary>Helpers for turning authored layout sketches (lines and arcs) into closed polygons.</summary>
    public static class SketchLoops
    {
        /// <summary>
        /// Discretizes a 2D arc. <paramref name="ccw"/> is the sweep direction; a full circle is produced
        /// when start and end coincide. The chord error stays below <paramref name="chordTolerance"/>.
        /// Returned points include both end points.
        /// </summary>
        public static List<Vec2> DiscretizeArc(Vec2 center, Vec2 start, Vec2 end, bool ccw, double chordTolerance)
        {
            double radius = (start - center).Length;
            double a0 = Math.Atan2(start.Y - center.Y, start.X - center.X);
            double a1 = Math.Atan2(end.Y - center.Y, end.X - center.X);
            double sweep;
            if ((start - end).Length < 1e-9) sweep = 2 * Math.PI;
            else
            {
                sweep = ccw ? a1 - a0 : a0 - a1;
                while (sweep <= 0) sweep += 2 * Math.PI;
                while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;
            }
            double tol = Math.Max(chordTolerance, 1e-6);
            double step = radius > tol ? 2 * Math.Acos(Math.Max(-1, 1 - tol / radius)) : Math.PI / 2;
            int n = Math.Max(2, (int)Math.Ceiling(sweep / Math.Max(step, 1e-3)));
            n = Math.Min(n, 720);
            var pts = new List<Vec2>(n + 1);
            for (int i = 0; i <= n; i++)
            {
                double a = a0 + (ccw ? 1 : -1) * sweep * i / n;
                pts.Add(new Vec2(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a)));
            }
            pts[pts.Count - 1] = (start - end).Length < 1e-9 ? start : end;
            return pts;
        }

        /// <summary>
        /// Chains polylines (each with ≥2 points) into closed loops by matching end points within
        /// <paramref name="tolerance"/>. Chains that cannot be closed are returned in <paramref name="openChains"/>.
        /// </summary>
        public static List<List<Vec3>> ChainClosedLoops(IEnumerable<List<Vec3>> polylines, double tolerance, out int openChains)
        {
            var pending = polylines.Where(p => p != null && p.Count >= 2).Select(p => new List<Vec3>(p)).ToList();
            var loops = new List<List<Vec3>>();
            openChains = 0;

            // Polylines that are already closed (circles) are loops on their own.
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if ((p[0] - p[p.Count - 1]).Length <= tolerance && p.Count >= 4)
                {
                    p.RemoveAt(p.Count - 1);
                    loops.Add(p);
                    pending.RemoveAt(i);
                }
            }

            while (pending.Count > 0)
            {
                var chain = pending[0];
                pending.RemoveAt(0);
                bool extended = true;
                while (extended)
                {
                    extended = false;
                    if ((chain[0] - chain[chain.Count - 1]).Length <= tolerance && chain.Count >= 3) break;
                    var tail = chain[chain.Count - 1];
                    for (int i = 0; i < pending.Count; i++)
                    {
                        var p = pending[i];
                        if ((p[0] - tail).Length <= tolerance) { chain.AddRange(p.Skip(1)); }
                        else if ((p[p.Count - 1] - tail).Length <= tolerance) { p.Reverse(); chain.AddRange(p.Skip(1)); }
                        else continue;
                        pending.RemoveAt(i);
                        extended = true;
                        break;
                    }
                }
                if ((chain[0] - chain[chain.Count - 1]).Length <= tolerance && chain.Count >= 4)
                {
                    chain.RemoveAt(chain.Count - 1);
                    loops.Add(chain);
                }
                else openChains++;
            }
            return loops;
        }

        /// <summary>Newell normal of a 3D loop (unit length, zero vector if degenerate).</summary>
        public static Vec3 Normal(IReadOnlyList<Vec3> loop)
        {
            var n = Vec3.Zero;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                n.X += (a.Y - b.Y) * (a.Z + b.Z);
                n.Y += (a.Z - b.Z) * (a.X + b.X);
                n.Z += (a.X - b.X) * (a.Y + b.Y);
            }
            return n.Normalized();
        }

        /// <summary>
        /// Converts 2D loops to regions with even-odd nesting: a loop inside an odd number of other loops is a hole.
        /// </summary>
        public static List<PolygonRegion> LoopsToRegions(List<List<Vec2>> loops)
        {
            var rings = new List<List<Vec2>>();
            for (int i = 0; i < loops.Count; i++)
            {
                int depth = 0;
                for (int j = 0; j < loops.Count; j++)
                    if (i != j && loops[i].Count > 0 && Polygon2.Contains(loops[j], loops[i][0])) depth++;
                var ring = new List<Vec2>(loops[i]);
                bool wantCcw = depth % 2 == 0;
                if ((Polygon2.SignedArea(ring) > 0) != wantCcw) ring.Reverse();
                rings.Add(ring);
            }
            return Polygon2.BuildRegions(rings, 0, 0);
        }
    }
}
