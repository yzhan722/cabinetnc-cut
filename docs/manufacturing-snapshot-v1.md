# CabinetNC Manufacturing Snapshot v1

> **版本区分（2026-10-10）**：本文记录**制造数据协议**（当前 Snapshot 1.1）；它不等于 Agent 接口版本。OmniCam Agent 接口实况请读 [Agent v1](agent/INTERFACES-v1.md)，下一阶段设计请读 [Agent v2](agent/INTERFACES-v2.md)。

`cabinetnc.manufacturing-snapshot` is a vendor-neutral, immutable CAD-to-shop handoff.
The shipping container uses the `.cnjob` extension and is a ZIP archive:

```text
job.cnjob
├── manifest.json
└── snapshot.json
```

The snapshot describes manufacturing facts. Nest placements, stock inventory, tool
selection, feeds, postprocessor settings, work offsets, and NC remain cutting-station
state and must not be emitted by CAD plugins.

## Coordinate and face convention

- Linear units are millimetres.
- Every workpiece owns a rigid, panel-local XY frame.
- Z is the panel thickness direction.
- **Snapshot `A` is always the machining face.** Opposite face is `B`.
- Face `role` / finish still describe design meaning (exterior colour, interior, etc.).
- `A` / `B` are manufacturing labels, not CAD entity tokens.
- Feature depth is positive into the material from `sourceFace`.
- Outer profiles are counter-clockwise and inner profiles are clockwise.
- The closing point is not repeated.
- Production geometry must be `exact` or `tessellated`; `bboxFallback` is rejected.

## Single-side manufacturing rule

CabinetNC v1 only supports single-side machining:

- exporters must normalize so all blind features open on Snapshot `A`;
- `manufacturing.machiningFace` must be `A` (or omitted; importer forces `A`);
- through features do not constrain the machining face;
- jobs with blind features on both faces before normalization are rejected;
- blind features with `UNKNOWN` face are rejected;
- importers remap a legacy `B`-only blind set to Snapshot `A` and warn.

There is no flip axis, secondary setup, or dual-face NC in this contract.

## v1.1 (additive; 1.0 files still import)

Every CAD exporter (the Fusion plugin, The Cab Lab) writes the same `.cnjob`.
The producer is named in `source.producer` / `source.producerVersion`; the
cutting station treats every producer alike.

- **`materialId` = one sheet.** Two parts share a `materialId` only when they can be
  cut from the same sheet; different sheets never share one. Recommended form:
  `{series}-{decor}-{1s|2s}-{thickness}`, lower-case, hyphen-separated — e.g.
  `pvc-white-stipple-2s-15`, `acrylic-gloss-white-1s-16`, `hpl-chestnut-1s-16`.
  The part's use (door, side, shelf) is `identity.role`, never part of the id.
  CabinetNC groups the nest by `materialId` + thickness and matches its material
  library by `materialId` only (not by display name). For a v1.1 id the stock-card
  label starts with the series (`HPL_Chestnut_SS · 16mm`), so a group's label no
  longer depends on which part's role happened to come first.
- **Material facts:** `colorName` (the real decor name), `surfaceMode`
  (`SINGLE_SIDED` / `DOUBLE_SIDED`), `series`, and `grained` (the sheet has a wood
  grain). A grained material whose part has no `grainDirection` imports with a
  `grain_missing` warning.
- **`manufacturing.machiningFace: "EITHER"`**: the part has no blind features and
  neither face is `NOT_ALLOWED`, so either face may lie on the table. CabinetNC keeps
  it as `orientation.allowMirror = true` for nesting. With blind features, or a
  `NOT_ALLOWED` face, EITHER is ignored (`machining_face_either_ignored` warning).
- **Face permissions:** single-sided stock marks its colour face `NOT_ALLOWED` and the
  back `PRIMARY`; double-sided stock marks both faces `ALLOWED` (or the one carrying
  blind work `PRIMARY`).
- **`edgeBands`:** tape on outline edges. Each entry is `{ i, thicknessMm, colorName? }`.
  `i` is the segment of `outerProfile.points` (edge i runs point i → point (i + 1) mod n).
  Omitted or `[]` means the part is not banded. A 1.0 file has no field and imports
  the same way. An index outside the outline, a duplicate index, or a thickness
  ≤ 0 rejects the workpiece.

## Canonical workpiece shape

```json
{
  "workpieceId": "WP-001",
  "name": "Left side",
  "identity": {
    "projectId": "KITCHEN-01",
    "moduleId": "BASE-01",
    "role": "left_side"
  },
  "material": {
    "materialId": "PB-WHITE-18",
    "thicknessMm": 18
  },
  "geometry": {
    "quality": "tessellated",
    "toleranceMm": 0.1,
    "outerProfile": {
      "closed": true,
      "points": [[0, 0], [600, 0], [600, 720], [0, 720]]
    },
    "nestingPolygon": [[0, 0], [600, 0], [600, 720], [0, 720]]
  },
  "faces": [
    {
      "faceId": "A",
      "role": "exterior",
      "finish": { "finishId": "white-stipple", "finishName": "White Stipple" }
    },
    {
      "faceId": "B",
      "role": "interior",
      "finish": { "finishId": "white-stipple", "finishName": "White Stipple" }
    }
  ],
  "features": [
    {
      "featureId": "H-001",
      "kind": "bore",
      "sourceFace": "A",
      "geometry": { "center": [80, 80], "diameterMm": 5 },
      "depthMm": 12,
      "through": false,
      "intent": { "purpose": "connector" }
    }
  ]
}
```

## Feature vocabulary

- `bore`: centre + diameter;
- `groove`: centreline + width;
- `pocket`: closed profile + depth;
- `throughProfile`: closed profile, always through;
- `counterbore`, `countersink`, `edgeRabbet`: reserved canonical extensions;
- `custom`: preserved in the source snapshot but not production-ready unless a
  downstream capability explicitly supports it.

Relationship and hardware semantics may be included in `relationships[]` and
`feature.intent`. Any actual machining must still be resolved into `features[]`;
the cutting station does not derive holes or grooves from relationships.

## Compatibility

- `.cnjob` is the new primary input.
- `cabinetnc.woodjob` v2 and `cabinetnc.cut-package` v1 remain read-only legacy inputs.
- The original `snapshot.json` is retained in `project.db`.
- Current Nest/CAM receives a compatibility projection to flat `CutPackage.Panels[]`.
- Information not represented by the flat projection remains available in the
  immutable source snapshot.

## Fusion export (sample job)

1. In Fusion, select the panel bodies (or a cabinet occurrence) for the job.
2. Nesting tab → **Export Selected → .cnjob** (use **Export All** only when you
   intentionally want every source panel).
3. Open the `.cnjob` in CabinetNC Desktop — the job list is the selected
   workpieces. Single-side gates still apply (outline required; no blind features
   on both faces; bbox fallback rejected).

JSON Schema: `docs/manufacturing-snapshot-v1.schema.json`.
