using System;
using System.Collections.Generic;
using System.Linq;
using ShopLayout.Core.Classification;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Model;

namespace ShopLayout.Core.Export
{
    public enum PlacedKind { Equipment, Building, Zone }

    /// <summary>A 2D shape with provenance. Every footprint and zone in the output carries source + accuracy.</summary>
    public sealed class Footprint
    {
        /// <summary>authoredSketch | tessellatedOutline | convexHull | orientedRect | rootBoundingBox | properties | clearanceComponent</summary>
        public string Source;
        /// <summary>exact | tessellated | approximate | approximateInflated</summary>
        public string Accuracy;
        public double? Resolution;
        public bool Complete = true;
        public List<PolygonRegion> Regions = new List<PolygonRegion>();

        public bool IsEmpty => Regions.Count == 0;

        public IEnumerable<Vec2> AllPoints() => Regions.SelectMany(r => r.Outer);

        public Footprint Map(Func<Vec2, Vec2> f, bool flips) => new Footprint
        {
            Source = Source,
            Accuracy = Accuracy,
            Resolution = Resolution,
            Complete = Complete,
            Regions = Regions.Select(r => r.Map(f, flips)).ToList()
        };

        public static Footprint FromRing(string source, string accuracy, List<Vec2> ring)
        {
            var r = new PolygonRegion { Outer = ring };
            Polygon2.Canonicalize(r);
            return new Footprint { Source = source, Accuracy = accuracy, Regions = { r } };
        }
    }

    /// <summary>An explicitly defined clearance / keep-out zone (never inferred from geometry).</summary>
    public sealed class Zone
    {
        public string Name;
        public Footprint Shape;
        /// <summary>Component that defines the zone (clearance marker) or the sketch name.</summary>
        public string DefinedBy;
    }

    /// <summary>Geometry of a definition in its local footprint frame (a, b along the footprint axes, h along up).</summary>
    public sealed class DefinitionGeometry
    {
        /// <summary>Triangles in the definition's local xyz frame, 9 doubles each.</summary>
        public List<double> LocalTriangles = new List<double>();
        public Box3 LocalBox = new Box3();
        public string LocalBoxSource;
        public double HMin, HMax;
        public bool HasHeight;
        public Footprint Primary;
        public Footprint Outline;
        public Footprint ConvexHull;
        public OrientedRect Rect;
        public Footprint Authored;
        public List<Zone> Clearances = new List<Zone>();
        public int PartsTotal, PartsWithGeometry, PartsResolved, PartsSkipped;
        public SortedSet<string> MeshSources = new SortedSet<string>(StringComparer.Ordinal);
        public int TriangleCount => LocalTriangles.Count / 9;
        public bool Complete => PartsTotal > 0 && PartsWithGeometry == PartsTotal;

        /// <summary>(a, b) extents of the primary footprint.</summary>
        public (Vec2 min, Vec2 max)? FootprintBounds()
        {
            var fp = Primary;
            if (fp == null || fp.IsEmpty) return null;
            return Polygon2.Bounds(fp.AllPoints());
        }
    }

    public sealed class Definition
    {
        public string Id;
        public PlacedKind Kind;
        public RawComponent Representative;
        public ComponentClass RepresentativeClass;
        public LocalAxis Up;
        public string RelativePath;
        public string Configuration;
        public List<Placement> Instances = new List<Placement>();
        public DefinitionGeometry Geometry;
        public List<Issue> Issues = new List<Issue>();
    }

    /// <summary>One placed object (equipment instance, building element or zone) with its pose in the floor frame.</summary>
    public sealed class Placement
    {
        public RawComponent Component;
        public ComponentClass Class;
        public PlacedKind Kind;
        public string InstanceId;
        public string AssetTag;
        public string Label;
        public Affine3 RootTransform;
        public bool TransformMissing;
        public Affine3 FloorTransform;
        public LocalAxis Up;
        public string UpSource;
        public double TiltDeg;
        public bool Planar;
        public bool Mirrored;
        public double RotationDeg;
        public double Scale;
        /// <summary>Floor-plane images of the footprint axes and the origin: floor = T + a·A + b·B.</summary>
        public Vec2 A, B, T;
        public double Elevation;
        public Definition Definition;

        public Vec2 MapFootprint(Vec2 ab) => new Vec2(T.X + ab.X * A.X + ab.Y * B.X, T.Y + ab.X * A.Y + ab.Y * B.Y);
    }
}
