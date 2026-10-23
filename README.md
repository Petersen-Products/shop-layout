# Shop Layout

Editable shop-floor layouts for diagrams.net, imported once from SolidWorks.

**The diagram is the source of truth.** SolidWorks was used for a one-time import (October 2026) and is no longer part of the workflow.

| Path | What it is |
|---|---|
| `diagrams/back_area.drawio` | **The layout.** Edit it in diagrams.net. |
| `diagrams/back_area_library.xml` | True-size equipment shapes (diagrams.net › File › Open Library). |
| `diagrams/back_area_import_log.md` | What the import changed: squared rotations, dropped duplicates, renamed labels, detected rooms. |
| `exports/back_area/` | The import record: the sanitized SolidWorks snapshot plus the JSON exports made from it. Read-only history. |
| `config/` | Export settings and the import cleanup rules. |
| `src/`, `tests/` | The exporter and the importer (C#, .NET Framework 4.8). Only needed to re-run the import. |

**Editing the layout:**
- **In the browser:** open [app.diagrams.net](https://app.diagrams.net), choose *GitHub*, and open `diagrams/back_area.drawio`. Each save becomes a commit.
- **Locally, or with Claude:** edit and commit the file like any other. It is uncompressed XML, so diffs are readable.
- Two people saving the same diagram at the same time will conflict, so coordinate big edits.

**Conventions in the diagram:**
- 100 px = 1 m, and the grid is 10 cm.
- Layers: Building (locked), Rooms, Zones, Equipment, Clearances, Notes.
- Equipment shapes keep their original SolidWorks data (`component_id`, `sw_instance`, `sw_file`, dimensions). View it with Edit › Edit Data.

The rest of this README documents the import tooling.

---

# ShopLayoutExport (import tooling)

Exports the active SolidWorks shop-floor assembly to two application-neutral JSON files:

- **`shop_layout.json`**: placed equipment instances, building elements, zones, the full component tree, and warnings.
- **`equipment_library.json`**: reusable equipment definitions (dimensions, footprints, clearances, custom properties).

The format has no dependency on diagrams.net. A diagrams.net converter (or any other consumer) can be built from the JSON alone.

## Build

Requirements:
- .NET SDK 8+ (builds the `net48` targets).
- .NET Framework 4.8.
- SolidWorks installed. The interop DLLs are read from `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist`. Override with `/p:SolidWorksApiDir=...`.

```bash
dotnet build ShopLayoutExport.sln -c Release
```

| Project | Purpose |
|---|---|
| `src/ShopLayout.Core` | Pure C#, no SolidWorks reference. Holds the raw model, classification, geometry (footprints, hulls, transforms), JSON output, and snapshots. |
| `src/ShopLayout.SolidWorks` | `SwAssemblySource`, the only code that calls the SolidWorks API. Turns a live assembly into the Core raw model. |
| `src/ShopLayout.Cli` | `ShopLayoutExport.exe`, a console host that attaches to a running SolidWorks through COM. |
| `src/ShopLayout.Drawio` | One-time import: export result + cleanup rules → `.drawio` diagram, shape library and import log. |
| `tests/ShopLayout.Tests` | Console test runner (112 checks), with a fake in-memory assembly. Pass `--write-sample <dir>` to regenerate `samples/`. |

**Adding an add-in later:** create a `swpublished` add-in project that references `ShopLayout.Core` and `ShopLayout.SolidWorks`. Construct `new SwAssemblySource(addinSldWorks)` and run `new ExportPipeline(source, config).Run()`. No extraction code changes.

> **Path length:** .NET Framework executables will not start from very deep folders (more than about 260 characters). Build and run from a short path such as `C:\Tools\ShopLayoutExport`.

## Run

1. Start SolidWorks and make the shop assembly the active document.
2. Run the exporter as the same Windows user as SolidWorks. Both elevated, or both not.

```bash
ShopLayoutExport.exe validate                      # dry run: report only, writes nothing, never resolves lightweight
ShopLayoutExport.exe validate --allow-resolve      # dry run with exact dimensions for lightweight parts
ShopLayoutExport.exe export --out C:\Layouts\Shop  # writes shop_layout.json, equipment_library.json, validation_report.json
ShopLayoutExport.exe init-config                   # writes a commented export.config.json to edit
```

Options:
- `--config <file>`: use a specific config file.
- `--floor XY|XZ|YZ|plane:<Name>`: override the floor plane.
- `--no-resolve`: never resolve lightweight components.
- `--record snapshot.json` / `--from-snapshot snapshot.json`: capture everything read from SolidWorks once, then re-run rules and footprint settings offline.
- `--strict`: exit code 1 on warnings.
- `--quiet`: summary line only.

Exit codes: 0 ok · 1 warnings (only with `--strict`) · 2 errors · 3 fatal · 4 usage.

The validation report lists:
- every top-level and group-level classification, with the rule or property that decided it;
- every placed object, with W × D × H, floor position, rotation and geometry source;
- all warnings.

## What it does to your model

| Action | Done? |
|---|---|
| Save, rebuild, move components, change configurations or display states | **Never** |
| Read structure, transforms, states, custom properties, tessellation, named sketches | Yes |
| Resolve a lightweight component | Only when it returns no geometry, only in `export` mode (or `validate --allow-resolve`), and only if `resolveLightweight` is true. Restored to lightweight afterwards (`restoreLightweight`). |

Resolving can make SolidWorks flag the assembly as modified. The exporter reports `DOCUMENT_MARKED_MODIFIED`. Nothing was saved, so you can close without saving.

## Classification and double counting

Every component gets exactly one role: `Group`, `Equipment`, `Building`, `Clearance`, `Ignore`, or the derived `Contained`.

For each node, the first of these that applies decides its role:
1. The **`LayoutRole` custom property** (configuration-specific value first, then file-level).
2. The **first matching rule** in `classification.rules`. Rules can match on file name or path, instance name, name path, configuration, parent or ancestor instance name, doc type, depth, envelope/virtual/BOM/suppressed/lightweight flags, and property conditions.
3. The built-in Toolbox rule: anything under `\Toolbox\` or `\SOLIDWORKS Data\` → `Ignore`.
4. `defaultTopLevelRole` (default `Equipment`) for direct children of the root or of a `Group`.

Classification runs top-down. Once a node is Equipment, Building or Clearance, its whole subtree is `Contained` and counted inside it. No custom properties are required.

Inside equipment, only these are honoured:
- `Clearance`: the child becomes that machine's clearance zone and is excluded from its footprint.
- `Ignore`: excluded from the footprint.

Any other role on a nested node raises `NESTED_ROLE_IGNORED`. To place the children of a subassembly individually, mark the subassembly as `Group`.

## Coordinate system

- All internal and exported lengths are **metres**. Angles are **degrees**.
- The floor frame is configurable:
  - `XY` (default): up = +Z.
  - `XZ`: SolidWorks Top Plane, up = +Y, floor y = −Z (like the Top view).
  - `YZ`: up = +X.
  - `custom`: your own `u`/`v` vectors and origin.
  - `referencePlane`: a named plane in the root assembly.
- Every placed object keeps the **original SolidWorks `Transform2.ArrayData`** unchanged. It also gets the same transform as a 4×4 matrix in root and floor coordinates, so you can re-derive anything in another coordinate system.

**Local up axis:** for each instance, the principal local axis (±X/±Y/±Z) closest to the floor normal is detected automatically. Override it with the `LayoutUpAxis` property. Instances tilted beyond `planarToleranceDeg` are flagged `planar:false`, and their floor footprint is projected from their actual 3D geometry.

**Footprint frame:** definitions store footprints in local coordinates (a, b) along `footprintAxisA` / `footprintAxisB`. Each instance carries `transform.footprintToFloor`, with floor = t + a·fa + b·fb. This 2D affine map keeps rotated equipment at its true size and handles mirrored placements correctly.

## Geometry sources (what is real and what is approximate)

Every shape carries `source`, `accuracy` and `complete`:

| source | accuracy | meaning |
|---|---|---|
| `authoredSketch` | exact | Closed loops of a sketch named `LAYOUT_FOOTPRINT` in the equipment model. Lines, arcs and circles only. |
| `tessellatedOutline` | tessellated | Union of the projected solid-body tessellation, traced on a grid (`footprintResolution`, default 5 mm). Concave shapes and holes are kept. |
| `convexHull` | approximate | Convex hull of all projected vertices. |
| `orientedRect` | approximate | Minimum-area rectangle of the hull. |
| `rootBoundingBox` | approximate / approximateInflated | SolidWorks `GetBox`. Used only when no tessellation is available. "Inflated" means the part is rotated off the root axes, so the box is oversized. |

The primary footprint is the first available of: authored sketch, tessellated outline, convex hull, root bounding box. The alternatives are exported too.

**Clearance zones are never inferred.** They come only from:
- sketches named `LAYOUT_CLEARANCE*` (for example `LAYOUT_CLEARANCE_Operator`);
- child components classified `Clearance`;
- `ClearanceFront/Back/Left/Right` properties (offsets from the footprint's bounding rectangle; Front = −b side; values like `600`, `600mm`, `0.6m`, `24in`).

## Stable identifiers

- **`componentId`** is the chain of SolidWorks component IDs (`IComponent2.GetID`), for example `12/4`. It is persistent across sessions, saves, renames and tree reordering, and is the default `instanceId`.
- With `ids.instanceIdStrategy = assetTagOrComponentIdPath`, an `AssetTag` property becomes `asset:<tag>` when it is unique.
  - Custom properties belong to a file or configuration, so all instances of one file share a tag. Duplicates are reported (`DUPLICATE_ASSET_TAG`), and those instances fall back to `componentId`.
  - For per-machine tags, use configuration-specific properties.
- **`definitionId`** is `eq:<relative path, lowercased>@<configuration>#<up axis>`. Flexible subassemblies and virtual components get a per-instance suffix.

**Determinism:**
- Output is sorted by ID, with fixed key order, invariant-culture numbers, fixed rounding (lengths 1 µm, matrices 1e-9) and no −0.
- Footprint grids are aligned to multiples of the resolution.
- `contentHash` (SHA-256) covers everything except the `run` block (timestamp, machine). Compare hashes to tell whether anything really changed.
- Tests check that the hash does not change with timestamp or traversal order.

## JSON outline

See `samples/` for complete files generated from the test fixture.

```text
shop_layout.json
  schema "petersen.shop-layout", schemaVersion, units, source, settings
  coordinateSystem { floorPlane, origin, u, v, normal, rootToFloor }
  library { file, contentHash }
  summary { components, equipment, equipmentDefinitions, building, zones, errors, warnings }
  equipment[] { instanceId, componentId, assetTag?, label, instanceName, namePath, definitionId, category?,
                classification{role,source}, state{suppression,visible,fileFound,envelope,virtual,...},
                pose{x,y,elevation,rotationDeg,mirrored,scale,upAxis,upAxisSource,tiltDeg,planar},
                transform{root{matrix,solidworksArrayData}, floor{matrix}, footprintToFloor{a,b,t}},
                floorFootprint{source,accuracy,resolution,complete,method,bounds,area,regions[{outer,holes?}]},
                floorClearances[], floorBounds{min,max}, rootBoundingBox, warnings[] }
  building[] / zones[]  same as equipment, plus kind, file, configuration, properties, heightRange
  components[]  full tree: componentId, parentId, localId, depth, name2, file, configuration,
                role, roleSource, ownerId, state, transformRoot, rootBoundingBox, childIds
  warnings[] { severity, code, componentId?, message }
  contentHash, run { exportedAtUtc, machine, mode }

equipment_library.json
  schema "petersen.equipment-library", schemaVersion, units, source
  definitions[] { definitionId, kind, file{fileName,relativePath,absolutePath,docType}, configuration, category?,
                  localFrame{upAxis, footprintAxisA, footprintAxisB},
                  dimensions{width,depth,height,basis}, localBoundingBox, heightRange,
                  footprint{primary, alternatives{tessellatedOutline?, convexHull?, orientedRect?}},
                  clearances[{name, definedBy, zoneType, shape}],
                  properties{available, file{name: value | {value, expression}}, configuration{...}},
                  geometryStats{partsTotal, partsWithGeometry, partsResolvedForGeometry, triangles, meshSources, complete},
                  representativeComponentId, instanceIds[], warnings[] }
  contentHash, run
```

## Warning codes

**Errors**
- `DUPLICATE_COMPONENT_ID`
- `FLOOR_PLANE_NOT_FOUND`, `FLOOR_CONFIG_INVALID`
- `CONFIG_INVALID_*`
- `SINGULAR_TRANSFORM`

**Warnings**
- Geometry: `NO_GEOMETRY`, `GEOMETRY_FROM_BOUNDING_BOX`, `PARTIAL_GEOMETRY`, `NO_TESSELLATION`, `NO_PARTS`, `SUPPRESSED_NO_GEOMETRY`, `CLEARANCE_NO_GEOMETRY`, `MESH_READ_FAILED`
- Files and the API: `FILE_NOT_FOUND`, `UNRESOLVED_REFERENCE`, `API_READ_FAILED`, `COMPONENT_ID_MISMATCH`, `COMPONENT_ID_FALLBACK`
- Transforms and placement: `TRANSFORM_UNAVAILABLE`, `TRANSFORM_NOT_RIGID`, `NON_PLANAR_PLACEMENT`, `SCALED_PLACEMENT`
- Classification and properties: `NESTED_ROLE_IGNORED`, `INVALID_LAYOUT_ROLE`, `GROUP_ROLE_ON_PART`, `DUPLICATE_ASSET_TAG`, `UP_AXIS_PROPERTY_INVALID`, `CLEARANCE_PROPERTY_INVALID`
- Sketches: `SKETCH_NOT_CLOSED`, `SKETCH_SEGMENT_UNSUPPORTED`, `SKETCH_NOT_FLOOR_PARALLEL`, `SKETCH_READ_FAILED`
- Lightweight resolution and session state: `LIGHTWEIGHT_RESOLVE_FAILED`, `LIGHTWEIGHT_RESTORE_FAILED`, `DOCUMENT_MARKED_MODIFIED`, `ROOT_NOT_SAVED`

**Info**
- Exclusions and placement notes: `SUPPRESSED_EXCLUDED`, `PROPERTIES_UNAVAILABLE`, `MIRRORED_PLACEMENT`, `FLEXIBLE_SUBASSEMBLY`, `ASSEMBLY_LEVEL_BODIES`
- Lightweight handling: `LIGHTWEIGHT_RESOLVED`, `LIGHTWEIGHT_RESTORED`, `LIGHTWEIGHT_NOT_RESOLVED`, `SKETCHES_UNAVAILABLE`
- Geometry checks: `FRAME_CHECK_INCONCLUSIVE`, `OUTLINE_RESOLUTION_COARSENED`

## Limitations

- **Tested offline, not yet on a real shop assembly.** The pipeline is covered by synthetic fixtures. The SolidWorks layer compiles against the 2025 SP2 interop, and its COM connection has been confirmed. Run `validate` on your assembly first and check the report.
  - The coordinate frame of component-body tessellation is cross-checked at runtime against `GetBox`. The data is converted if it turns out to be in root coordinates, and `FRAME_CHECK_INCONCLUSIVE` flags doubtful cases.
- **Lightweight components:**
  - Geometry comes from `GetBodies3`, then the lightweight display tessellation, then temporary resolution, then `GetBox`.
  - In `validate`, nothing is resolved, so lightweight parts may show bounding-box dimensions.
  - Custom properties are read through `IComponent2.CustomPropertyManager`. If SolidWorks returns none for a lightweight part, it is classified by rules only.
  - The Document Manager API is not used (by design, for now).
- **Suppressed** components stay in the component tree with their state, but have no geometry or transform. **Missing files** are reported, and only in-memory data is used.
- **Tessellation-based footprints** are accurate to about one grid cell and depend on SolidWorks' display tessellation quality.
  - Very large elements coarsen the grid automatically (`OUTLINE_RESOLUTION_COARSENED`).
  - Building outlines are top-down projections, not horizontal sections at a cut height.
- **Layout sketches:**
  - Sketches are read from the equipment's own model document (part, or subassembly top level), from its active configuration. Lightweight models need resolving first.
  - Splines and ellipses are skipped with a warning. Use lines and arcs.
- **Configuration differences:** instances of the same file and configuration share one definition, measured from the first instance. Flexible subassemblies are measured per instance.
- **Instance-specific metadata:** SolidWorks custom properties belong to files and configurations, not instances. Instance-level data is limited to names, IDs, states and transforms.

## One-time import to diagrams.net

SolidWorks is used once. After the import, the `.drawio` file is the source of truth.

```bash
ShopLayoutExport.exe drawio --from-snapshot exports\back_area\solidworks_snapshot.json --config config\export.config.json --cleanup config\import_cleanup.json --out diagrams --name back_area
```

The import runs from the recorded snapshot (`export --record`), not from SolidWorks. It writes three files:
- `back_area.drawio`: the diagram, as uncompressed XML.
- `back_area_library.xml`: a diagrams.net shape library with one true-size shape per equipment definition (File › Open Library).
- `back_area_import_log.md`: every change the cleanup made.

Cleanup rules, all set in [`config/import_cleanup.json`](config/import_cleanup.json):
- **Squaring:** rotations within `snapToleranceDeg` (1°) of a multiple of 90° are squared up about the footprint centre.
- **Duplicates:** instances of the same definition at the same pose are dropped; the lowest component ID is kept.
- **Labels:** SolidWorks instance suffixes (`-1`, `-12`) are stripped, then `labels.map` fixes typos and capitalisation. The original name stays on each shape as `sw_instance`.
- **Building:** every building element (Room, Wall, …) is cut at `building.cutHeight` (1.2 m), like an architectural plan. Openings below the cut, such as doors, stay open. Walls are drawn as uniform rectangles on the locked *Building* layer, and the slab becomes the floor outline.
- **Rooms:** enclosed spaces in that wall plan become labelled room shapes on the *Rooms* layer, with name, type and area.
  - Door openings up to `maxOpeningWidth` are bridged when finding rooms. Larger openings, such as roll-up doors, leave a space unenclosed, and it is not detected.
  - Name a room by giving a point inside it under `rooms.names`, or edit its `name` field in diagrams.net (Edit Data); the label updates itself.
  - Hand-drawn zones (aisles, work areas) go under `zones`, or are drawn directly on the *Zones* layer.

**Diagram conventions:**
- Scale is 100 px = 1 m and the grid is 10 cm. A scale note and a 5 m scale bar are on the *Notes* layer.
- Layers, bottom to top: Building (locked), Rooms, Zones, Equipment, Clearances, Notes.
- Each equipment shape carries: `component_id`, `definition`, `sw_instance`, `sw_file`, `sw_configuration`, `width_m`, `depth_m`, `height_m`, `elevation_m`, `rotation_deg`, `footprint`, and `import_note` when it was squared up.
- Non-rectangular footprints use the diagrams.net *Polygon* shape. Footprint holes (for example inside SHELVING) are not shown.

**Verified:**
- On the back-area import, every equipment shape's bounds match the exported footprint. Unsnapped shapes are exact; squared-up shapes move by at most 1.3 cm at their corners.
- The file renders in the official diagrams.net viewer, and the library entries decompress with diagrams.net's own code.
