# Known limitations (RC) — audit2

Honest limits — do **not** treat these as done.

## CAM ordering (audited)

- `CamSafety.OrderSafe` sorts **SequenceRank before PanelId** (sheet → rank → panel → tool → feature). All drills/grooves on a sheet complete before any outer contour, even across panels.
- Over-deep grooves are **not** clamped in `ApplyPanelDepths`; `NcPreflight` / `DepthIssues` must still see the illegal depth (`groove_too_deep`).

## Pocket (audited)

- Zigzag + Clipper inset clear v1. Not trochoidal; corner residual may remain after finish inset pass.
- Scan strokes are **disjoint segments**; `NcEmitter` rapid (G0) between segments and emits finish loop separately.
- Missing `DepthMm` → `pocket_depth_missing` (error). Does **not** default to full panel thickness.
- Cavity too small for tool+onion → `pocket_too_small_for_tool` (error). Not a silent skip.
- Hard pocket/tool/depth errors block Desktop override and `SheetBundleBuilder.Build` when `enforcePreflight` is on.

## Dual-face CAM (Day 11 PARTIAL)

- B-side ops without `FaceRegistration.Strategy` are **blocked** (`no_registration`).
- Production `S{n}_A.nc` / `S{n}_B.nc` WCS after physical flip is **not** implemented pending Troy answers (flip axis, origin, default strategy).
- `DoubleSideGate.MirrorLocal` is math-only; not shop WCS.

## Nesting

- Engine is **grouped BLF (AABB)**, not NFP. Advanced stub always falls back (`blf_fallback`).
- Part-in-part model exists but is **disabled**.
- Desktop nests via Domain locally; Worker uses the same Domain router when called (not every Desktop nest RPC-roundtrips).
- **Worker gRPC still sends rectangle width/height and forces BLF.** Automated tests lock this contract (`WorkerNestContractTests`). Do not claim Desktop NFP/PIP Worker parity.

## Import / CAD

- DXF importer: rectangles / LWPOLYLINE points. **No** full arc tessellation / CAD editor.
- Cross-project import: Domain `WorkpieceImporter` only (no polished Desktop UI wizard yet).

## Post / tools

- **Output policy:** one NC file per **Sheet × Tool** (`{job}_S{n}_{Tn}.nc`). Manifest lists programs. DXF remains per sheet.
- Each single-tool NC header includes ToolId, DiameterMm, FeedXY, FeedZ, RPM, origin note.
- Real `S`/`F` come from `ToolCatalog` (machine profile is fallback only).
- **No** automatic `M6` — `IToolChangePost` reserved; default `NullToolChangePost` returns null until shop confirms controller syntax.
- Tool IDs ASSUMED T1/T2/T3 presets — shop must confirm magazine numbers.

## UIA / smoke

- Automated UIA smoke can hang in non-interactive agent shells; **do not mark UIA PASS**.
- `smoke_desktop.py` still targets pre-OmniCam control names; it is **not** an RC gate after the OmniCam UI change. Re-record before treating UIA as coverage.
- Release goldens lock normalized NC only (`Category=GoldenRegression`). Labels, U/V, and UIA are **not** in that pack.
- Manual path: `docs/sprint/MANUAL_SMOKE_10MIN.md` — items start as `MANUAL PENDING`.

## Not in RC (Campaign 2+)

- True NFP / engine contest
- Material-removal solid simulation
- Signed MSI / learning patterns
- Encrypted woodjob
- Machine-specific ATC/M6 macros
