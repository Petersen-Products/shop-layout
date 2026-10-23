using System;
using System.Collections.Generic;
using System.Linq;
using ShopLayout.Core.Geometry;

namespace ShopLayout.Core.Model
{
    public enum DocKind { Part, Assembly, Other }

    /// <summary>Normalized SolidWorks suppression/load state.</summary>
    public enum LoadState { Resolved, Lightweight, Suppressed, IdMismatch, Unknown }

    public sealed class RawProperty
    {
        public string Name;
        /// <summary>Text as typed (may contain $PRP:/equations).</summary>
        public string Raw;
        /// <summary>Evaluated value.</summary>
        public string Value;
    }

    /// <summary>Custom properties keyed case-insensitively; enumerates in a stable (ordinal, case-insensitive) order.</summary>
    public sealed class PropertySet
    {
        private readonly SortedDictionary<string, RawProperty> _map =
            new SortedDictionary<string, RawProperty>(StringComparer.OrdinalIgnoreCase);

        public void Set(string name, string raw, string value)
        {
            if (string.IsNullOrEmpty(name)) return;
            _map[name] = new RawProperty { Name = name, Raw = raw ?? "", Value = value ?? raw ?? "" };
        }

        public RawProperty Get(string name) =>
            name != null && _map.TryGetValue(name, out var p) ? p : null;

        public IEnumerable<RawProperty> All => _map.Values;
        public int Count => _map.Count;
    }

    /// <summary>
    /// One component occurrence exactly as read from the CAD model, before any interpretation.
    /// Produced by an <see cref="IAssemblySource"/>; contains nothing SolidWorks-specific beyond names.
    /// </summary>
    public sealed class RawComponent
    {
        /// <summary>SolidWorks component ID (IComponent2.GetID): unique within the owning assembly, persistent, rename-proof.</summary>
        public int LocalId;
        /// <summary>Chain of LocalIds from the root, e.g. "12/4/7". The canonical stable component identifier.</summary>
        public string IdPath;
        /// <summary>Set when the source could not provide a real component ID and fell back to names.</summary>
        public bool IdIsFallback;
        /// <summary>Full SolidWorks Name2, e.g. "Cell A-1/Robot-2".</summary>
        public string Name2;
        /// <summary>Last segment of Name2, e.g. "Robot-2".</summary>
        public string InstanceName;
        public string ComponentReference;
        public string FilePath;
        public bool? FileExists;
        public DocKind DocKind;
        public string Configuration;
        public LoadState LoadState;
        public bool? IsLoaded;
        public bool? Visible;
        public bool IsEnvelope;
        public bool IsVirtual;
        public bool ExcludeFromBom;
        public bool IsFixed;
        public bool IsFlexible;
        public bool IsPatternInstance;
        public bool IsMirrored;
        /// <summary>SolidWorks Transform2.ArrayData (relative to the root assembly). Null when unavailable.</summary>
        public double[] TransformArray;
        /// <summary>IComponent2.GetBox in root coordinates (approximate, axis-aligned). Null when unavailable.</summary>
        public double[] RootBox;
        public PropertySet FileProperties = new PropertySet();
        public PropertySet ConfigProperties = new PropertySet();
        public bool PropertiesAvailable;
        public List<RawComponent> Children = new List<RawComponent>();
        /// <summary>Problems the source hit while reading this component.</summary>
        public List<Issue> ReadIssues = new List<Issue>();

        // Filled by the pipeline after reading.
        public RawComponent Parent;
        public int Depth;

        public string FileName => string.IsNullOrEmpty(FilePath) ? "" : System.IO.Path.GetFileName(FilePath);

        /// <summary>Configuration-specific value wins over the file-level value (SolidWorks convention).</summary>
        public RawProperty GetProperty(string name) => ConfigProperties.Get(name) ?? FileProperties.Get(name);

        public IEnumerable<RawComponent> Descendants()
        {
            foreach (var c in Children)
            {
                yield return c;
                foreach (var d in c.Descendants()) yield return d;
            }
        }

        public IEnumerable<RawComponent> Ancestors()
        {
            for (var p = Parent; p != null; p = p.Parent) yield return p;
        }

        public override string ToString() => $"{IdPath} {Name2}";
    }

    public sealed class RawAssembly
    {
        public string RootFilePath;
        public string RootConfiguration;
        public string SourceApplication = "SOLIDWORKS";
        public string SourceVersion;
        public PropertySet RootProperties = new PropertySet();
        public List<RawComponent> TopLevel = new List<RawComponent>();
        public List<Issue> ReadIssues = new List<Issue>();

        public IEnumerable<RawComponent> AllComponents()
        {
            foreach (var c in TopLevel)
            {
                yield return c;
                foreach (var d in c.Descendants()) yield return d;
            }
        }
    }

    /// <summary>Triangles of one part occurrence, expressed in that component's own local frame (metres).</summary>
    public sealed class MeshResult
    {
        public bool Success;
        /// <summary>9 floats per triangle.</summary>
        public float[] Triangles = new float[0];
        /// <summary>"faceTessellation", "componentTessellation", or "none".</summary>
        public string Source = "none";
        /// <summary>Which frame the source data was found in ("local", "root", "assumedLocal").</summary>
        public string FrameDetection;
        public bool ResolvedForGeometry;
        public List<Issue> Issues = new List<Issue>();
    }

    public sealed class RawSketch
    {
        public string Name;
        /// <summary>Supported segments (lines, arcs, circles) as polylines in the component's local frame.</summary>
        public List<List<Vec3>> Polylines = new List<List<Vec3>>();
        public int UnsupportedSegments;
    }

    public sealed class SketchResult
    {
        public bool ModelAvailable;
        public bool ResolvedForSketches;
        public List<RawSketch> Sketches = new List<RawSketch>();
        public List<Issue> Issues = new List<Issue>();
    }

    /// <summary>
    /// Everything the export pipeline needs from a CAD system. SolidWorks implements this in
    /// ShopLayout.SolidWorks; snapshots implement it for offline re-runs and tests.
    /// </summary>
    public interface IAssemblySource
    {
        string Description { get; }

        /// <summary>Reads the component tree, transforms, states and properties. Must not change the model.</summary>
        RawAssembly ReadStructure();

        /// <summary>Tessellation of a part occurrence in its local frame. May resolve a lightweight component if allowed.</summary>
        MeshResult GetMesh(RawComponent part, bool allowResolve);

        /// <summary>Reads sketches whose names equal <paramref name="footprintName"/> or start with <paramref name="clearancePrefix"/>.</summary>
        SketchResult GetLayoutSketches(RawComponent component, string footprintName, string clearancePrefix, bool allowResolve);

        /// <summary>SolidWorks ArrayData of a named reference plane in the root assembly, or null.</summary>
        double[] GetReferencePlaneTransform(string planeName);

        /// <summary>Undo temporary state changes (e.g. restore lightweight) and report anything left modified.</summary>
        List<Issue> Finish();
    }
}
