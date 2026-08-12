# CabinetNC Manufacturing Snapshot v1

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
