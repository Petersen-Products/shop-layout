using System;
using System.Collections.Generic;
using ShopLayout.Core.Geometry;

namespace ShopLayout.Drawio
{
    public enum ShapeKind { Rect, Polygon, Text }

    /// <summary>
    /// One diagram shape in floor metres (y up). The writer converts to diagrams.net pixels (y down).
    /// A shape is an unrotated W×H box around <see cref="Center"/>, rotated counter-clockwise by
    /// <see cref="RotationDeg"/>; polygons give their outline relative to that box.
    /// </summary>
    public sealed class DiagramItem
    {
        public string Id;
        public string Layer;
        /// <summary>Optional parent shape (diagrams.net child cell); geometry is then written relative to it.</summary>
        public DiagramItem Parent;
        public string Kind;
        public string Label;
        public ShapeKind Shape = ShapeKind.Rect;
        public Vec2 Center;
        public double Width, Height;
        public double RotationDeg;
        /// <summary>Polygon outline in box-relative coordinates: u right (0..1), v down (0..1), before rotation.</summary>
        public List<Vec2> PolyCoords;
        public string Style;
        /// <summary>Custom data shown in diagrams.net "Edit Data"; written in key order.</summary>
        public SortedDictionary<string, string> Data = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// <summary>When set, the label is a template over Data (diagrams.net placeholders), e.g. "%name%".</summary>
        public bool UsePlaceholders;

        public IEnumerable<Vec2> Corners()
        {
            double a = RotationDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
            foreach (var (dx, dy) in new[] { (-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5) })
            {
                double x = dx * Width, y = dy * Height;
                yield return new Vec2(Center.X + x * c - y * s, Center.Y + x * s + y * c);
            }
        }
    }

    public sealed class DiagramLayer
    {
        public string Id;
        public string Name;
        public bool Locked;
    }

    /// <summary>A reusable shape for the diagrams.net library (one per equipment definition).</summary>
    public sealed class LibraryShape
    {
        public string Title;
        public DiagramItem Template;
    }

    public sealed class DiagramModel
    {
        public string Name;
        public double Scale;
        public int GridSizePx;
        public double MarginMeters;
        public List<DiagramLayer> Layers = new List<DiagramLayer>();
        public List<DiagramItem> Items = new List<DiagramItem>();
        public List<LibraryShape> Library = new List<LibraryShape>();
        public ImportLog Log = new ImportLog();
    }

    public sealed class ImportLog
    {
        public List<string> Summary = new List<string>();
        public List<string[]> Snapped = new List<string[]>();
        public List<string[]> Duplicates = new List<string[]>();
        public List<string[]> Renamed = new List<string[]>();
        public List<string[]> Rooms = new List<string[]>();
        public List<string[]> Openings = new List<string[]>();
        public List<string> Notes = new List<string>();
    }
}
