using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using ShopLayout.Core.Geometry;
using ShopLayout.Core.Json;

namespace ShopLayout.Drawio
{
    /// <summary>
    /// Writes a <see cref="DiagramModel"/> as an uncompressed diagrams.net file (readable, diffable XML)
    /// and as a diagrams.net shape library. Floor metres (y up) become pixels (y down) at the model scale;
    /// rotations become clockwise diagrams.net rotations.
    /// </summary>
    public static class DrawioWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string WriteDiagram(DiagramModel m)
        {
            var pts = m.Items.SelectMany(i => i.Corners()).ToList();
            var (mn, mx) = pts.Count > 0 ? Polygon2.Bounds(pts) : (new Vec2(0, 0), new Vec2(10, 10));
            double ox = Math.Floor(mn.X - m.MarginMeters), oy = Math.Ceiling(mx.Y + m.MarginMeters);
            double pageW = Math.Ceiling((mx.X - ox + m.MarginMeters) * m.Scale), pageH = Math.Ceiling((oy - mn.Y + m.MarginMeters) * m.Scale);

            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, IndentChars = "  ", OmitXmlDeclaration = true, NewLineChars = "\n" }))
            {
                w.WriteStartElement("mxfile");
                w.WriteAttributeString("host", "ShopLayoutExport");
                w.WriteAttributeString("type", "device");
                w.WriteStartElement("diagram");
                w.WriteAttributeString("id", Slug(m.Name));
                w.WriteAttributeString("name", m.Name);
                WriteModel(w, m.Layers, m.Items, m.Scale, m.GridSizePx, ox, oy, pageW, pageH, page: true);
                w.WriteEndElement();
                w.WriteEndElement();
            }
            return sb.ToString() + "\n";
        }

        /// <summary>diagrams.net library: one entry per equipment definition, at true size.</summary>
        public static string WriteLibrary(DiagramModel m)
        {
            var entries = new List<object>();
            foreach (var shape in m.Library)
            {
                var t = shape.Template;
                // Template sits with its unrotated box at the origin.
                var item = Clone(t);
                item.Id = "2";
                item.Layer = "1";
                double ox = -t.Width / 2, oy = t.Height / 2;
                var sb = new StringBuilder();
                using (var w = XmlWriter.Create(sb, new XmlWriterSettings { OmitXmlDeclaration = true }))
                    WriteModel(w, new[] { new DiagramLayer { Id = "1", Name = "" } }, new[] { item }, m.Scale, m.GridSizePx, ox, oy, 0, 0, page: false);
                entries.Add(new JsonObject()
                    .Add("xml", Compress(sb.ToString()))
                    .Add("w", Math.Round(t.Width * m.Scale, 2))
                    .Add("h", Math.Round(t.Height * m.Scale, 2))
                    .Add("aspect", "fixed")
                    .Add("title", shape.Title));
            }
            return "<mxlibrary>" + new JsonWriter(4, false).Write(entries) + "</mxlibrary>\n";
        }

        private static void WriteModel(XmlWriter w, IEnumerable<DiagramLayer> layers, IEnumerable<DiagramItem> items, double scale, int grid,
            double ox, double oy, double pageW, double pageH, bool page)
        {
            w.WriteStartElement("mxGraphModel");
            if (page)
            {
                w.WriteAttributeString("grid", "1");
                w.WriteAttributeString("gridSize", grid.ToString(Inv));
                w.WriteAttributeString("guides", "1");
                w.WriteAttributeString("tooltips", "1");
                w.WriteAttributeString("connect", "0");
                w.WriteAttributeString("arrows", "0");
                w.WriteAttributeString("fold", "1");
                w.WriteAttributeString("page", "1");
                w.WriteAttributeString("pageScale", "1");
                w.WriteAttributeString("pageWidth", N(pageW));
                w.WriteAttributeString("pageHeight", N(pageH));
                w.WriteAttributeString("math", "0");
                w.WriteAttributeString("shadow", "0");
            }
            w.WriteStartElement("root");
            w.WriteStartElement("mxCell"); w.WriteAttributeString("id", "0"); w.WriteEndElement();
            foreach (var l in layers)
            {
                w.WriteStartElement("mxCell");
                w.WriteAttributeString("id", l.Id);
                if (!string.IsNullOrEmpty(l.Name)) w.WriteAttributeString("value", l.Name);
                if (l.Locked) w.WriteAttributeString("style", "locked=1;");
                w.WriteAttributeString("parent", "0");
                w.WriteEndElement();
            }
            foreach (var it in items) WriteItem(w, it, scale, ox, oy);
            w.WriteEndElement(); // root
            w.WriteEndElement(); // mxGraphModel
        }

        private static void WriteItem(XmlWriter w, DiagramItem it, double scale, double ox, double oy)
        {
            double cx = (it.Center.X - ox) * scale, cy = (oy - it.Center.Y) * scale;
            double pw = it.Width * scale, ph = it.Height * scale;
            string style = it.Style ?? "";
            double rot = ((360 - it.RotationDeg) % 360 + 360) % 360;
            if (rot > 1e-9 && 360 - rot > 1e-9) style += "rotation=" + N(rot) + ";";

            bool wrap = it.Data.Count > 0 || it.UsePlaceholders;
            if (wrap)
            {
                w.WriteStartElement("object");
                w.WriteAttributeString("id", it.Id);
                w.WriteAttributeString("label", it.Label ?? "");
                if (it.UsePlaceholders) w.WriteAttributeString("placeholders", "1");
                foreach (var kv in it.Data) w.WriteAttributeString(XmlConvert.EncodeLocalName(kv.Key), kv.Value ?? "");
                w.WriteStartElement("mxCell");
            }
            else
            {
                w.WriteStartElement("mxCell");
                w.WriteAttributeString("id", it.Id);
                w.WriteAttributeString("value", it.Label ?? "");
            }
            w.WriteAttributeString("style", style);
            w.WriteAttributeString("vertex", "1");
            w.WriteAttributeString("parent", it.Layer);
            w.WriteStartElement("mxGeometry");
            w.WriteAttributeString("x", N(cx - pw / 2));
            w.WriteAttributeString("y", N(cy - ph / 2));
            w.WriteAttributeString("width", N(pw));
            w.WriteAttributeString("height", N(ph));
            w.WriteAttributeString("as", "geometry");
            w.WriteEndElement();
            w.WriteEndElement(); // mxCell
            if (wrap) w.WriteEndElement(); // object
        }

        /// <summary>diagrams.net compression: base64(deflateRaw(encodeURIComponent(xml))).</summary>
        public static string Compress(string xml)
        {
            var bytes = Encoding.UTF8.GetBytes(Uri.EscapeDataString(xml));
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(bytes, 0, bytes.Length);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        public static string Decompress(string b64)
        {
            using (var ms = new MemoryStream(Convert.FromBase64String(b64)))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var sr = new StreamReader(ds, Encoding.UTF8))
                return Uri.UnescapeDataString(sr.ReadToEnd());
        }

        private static DiagramItem Clone(DiagramItem t) => new DiagramItem
        {
            Id = t.Id, Layer = t.Layer, Kind = t.Kind, Label = t.Label, Shape = t.Shape, Center = t.Center, Width = t.Width, Height = t.Height,
            RotationDeg = t.RotationDeg, PolyCoords = t.PolyCoords, Style = t.Style,
            Data = new SortedDictionary<string, string>(t.Data, StringComparer.Ordinal), UsePlaceholders = t.UsePlaceholders
        };

        private static string N(double v)
        {
            double r = Math.Round(v, 2);
            return (r == 0 ? 0 : r).ToString("0.##", Inv);
        }

        private static string Slug(string s) => new string((s ?? "diagram").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

        /// <summary>Markdown summary of everything the import changed.</summary>
        public static string WriteLog(DiagramModel m)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Import log: {m.Name}");
            sb.AppendLine();
            foreach (var s in m.Log.Summary) sb.AppendLine("- " + s);
            Table(sb, "Rooms", new[] { "Name", "Area (m²)", "Extent W × D (m)", "Centre (m)" }, m.Log.Rooms);
            Table(sb, "Doors, docks and windows", new[] { "Name", "Type", "Width (m)", "Sill (m)", "Head (m)", "Centre (m)" }, m.Log.Openings);
            Table(sb, "Rotations squared up", new[] { "Label", "Component", "From (°)", "To (°)" }, m.Log.Snapped);
            Table(sb, "Duplicates dropped", new[] { "Dropped", "Component", "Same as", "Component" }, m.Log.Duplicates);
            Table(sb, "Labels", new[] { "SolidWorks name", "Diagram label", "Instances" }, m.Log.Renamed);
            if (m.Log.Notes.Count > 0)
            {
                sb.AppendLine().AppendLine("## Notes").AppendLine();
                foreach (var n in m.Log.Notes) sb.AppendLine("- " + n);
            }
            return sb.ToString();
        }

        private static void Table(StringBuilder sb, string title, string[] head, List<string[]> rows)
        {
            sb.AppendLine().AppendLine($"## {title} ({rows.Count})").AppendLine();
            if (rows.Count == 0) { sb.AppendLine("None."); return; }
            sb.AppendLine("| " + string.Join(" | ", head) + " |");
            sb.AppendLine("|" + string.Concat(head.Select(_ => "---|")));
            foreach (var r in rows) sb.AppendLine("| " + string.Join(" | ", r.Select(c => (c ?? "").Replace("|", "\\|"))) + " |");
        }
    }
}
