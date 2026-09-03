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

## Post / tools — two posts coexist (audited 2026-09-02)

There are **two** post-processors and they make different promises. Check which one a file
came from before applying the rules below; the NC header tells you.

### A. Sheet × Tool (generic, `SheetBundleBuilder` → `NcEmitter.OpsToNc` without recipe)

- **Output policy:** one NC file per **Sheet × Tool** (`{job}_S{n}_{Tn}.nc`). Manifest lists programs. DXF remains per sheet.
- Z0 = **sheet top**; safe Z = `MachineProfile.SafeZMm` (8 mm on `osai_e4_1325`); through = thickness + 0.5.
- Each single-tool NC header includes ToolId, DiameterMm, FeedXY, FeedZ, RPM, origin note.
- Real `S`/`F` come from `ToolCatalog` (machine profile is fallback only).
- **No** automatic `M6` — `IToolChangePost` reserved; default `NullToolChangePost` returns null. Operator loads one tool program at a time.
- Tool IDs ASSUMED T1/T2/T3 presets — shop must confirm magazine numbers.
- **Hole-size gap with the preset tools:** holes < Ø5 are drilled with T3 (Ø3, so they come out Ø3); holes ≥ Ø5 are pocket-cleared and need ~1 mm more than the smallest router (T1 Ø6.35). A Ø5 system hole — the most common cabinet hole — is therefore rejected by preflight (`pocket_too_small_for_tool`) until the shop adds a Ø5 drill to 工艺模版 / the post. The bundled demo was changed from Ø5 to Ø4 so a first run can export; the gap itself is a tooling decision for Troy.

### B. Troy single-file OSAI (`PostRecipe.TroyDefault()`, `.anc`)

- **One program for the whole sheet**, tools in order drill → tongue → clearance → inner/outer profiles.
- **Emits `M6 T<n>` + `M3 S<rpm>` + `(DLY,3)` per tool** — this post *does* change tools automatically on the OSAI ATC. The "no M6" statement above does not apply to it.
- Z0 = **board bottom / spoilboard top** (`Z0IsBoardBottom`); safe Z = 30; profile leave pass at Z 0.5 then through at Z −0.55; bridges leave Z 1.45.
- Feeds/RPM come from `TroyRecipe` constants via `PostRecipe`, **not** from `ToolCatalog` (plunge 1000, first pass 12000, last pass 20000, tongue 9000, S14500).
- Since `7f486c3` the leave pass runs over **all** profiles before the through pass, profile order is travel-optimised and the entry corner is chosen per pass; tool changes no longer home XY first. `G0 X0 Y0` remains at program end (`HomeXyAtEnd`).
- The first rapid after a tool change may move XY and Z together (`G0 X.. Y.. Z30`). The file relies on the OSAI `M6` macro leaving Z at tool-change height; this assumption is written down in `POST_CHANGE_CHECKLIST.md` and must be re-verified if the macro changes.
- Optional Process 2 (`LS11` / `M701` / `M702`) label pasting is appended by `LabelExport.WrapCutWithLabelProcess`; see `LABELING_REQUIREMENTS.md` for its open items (`E41/E42` retry limit is **not** implemented).

### Both

- `Regression/NcSafetyInvariantTests` enforce for both posts: no rapid XY below safe Z, no rapid ending in the material zone, no cut below the spoilboard allowance (1.0 mm), feeds/RPM only from the recipe or ToolCatalog, vertical plunges at plunge feed, spindle on before cutting, retracted at program end, every cut on a placed panel.
- `Regression/ReleaseGoldenRegressionTests` lock the normalised NC of three synthetic jobs. Real shop programs go to `dotnet/tests/testdata/regression/shop-anc/` and are replayed by `ShopAncFixtureTests`.

## Reverse / recut (`.anc` → panels)

- `NcReverse` recovers rectangles and polylines from OSAI-Troy programs; arcs are tessellated for inference only.
- Two-pass (and repeat-pass) profiles are merged by loop geometry (area centroid, area, perimeter), so pass order, entry corner and direction do not matter (fixed 2026-09-02; previously a different entry corner produced a phantom panel).
- Inner windows are recovered at the finished size (cutter-centre loop expanded by the tool radius). Outer profiles are inset by the radius.
- A first chord of 8–80 mm is treated as a ramp **only** when removing it does not open a closed loop.
- Not recovered: pockets as pockets when Z is a blind depth (classified `pocket` only when closed and blind), guillotine cuts as panels, B-side ops.

## Desktop

- `CabinetNC.Desktop` (13k lines, `MainWindow.xaml.cs` ≈ 7.4k) has **no automated tests**. The Windows CI job compiles it; behaviour is verified manually. Plan: `DESKTOP_TESTABILITY_PLAN.md`.
- Label bitmaps are written flat next to the NC and checked against every `LS11` in the program; the machine picture folder is a library setting (`Labeler.MachinePictureDir`, default `D:\CNC`).
- Viewport zoom/pan works on nest, ops and export stages (one shared view transform); the view resets when the active sheet changes or a nest is re-run. The nest stage's holding bay is screen-anchored and covers zoomed sheet content beneath it by design.
- Export simulation: DRO readout and code ↔ backplot sync rely on `ToolStroke.LineIndex` (source line of the block). Blocks without motion (comments, `M` codes) map to the next motion block when clicked.
- Unsaved-work detection is a fingerprint of the saveable content (package JSON, placements, CAM session without view state, project name, machine). It is recomputed on major refreshes and on close/open, not on every drag, so the title-bar `*` can lag one action behind a nest drag.
- Recent files live in `library.json` (`recentFiles`, max 10); a missing file is greyed in the menu and dropped when clicked.
- Display layer toggles (grain / features / label anchors / dims / sim rapids) are session-only and not persisted.
- The reverse audit card counts what `NcReverse` classified; a contour it could not classify at all (open loop, mid-depth pass) shows up as a groove/pocket op, not as an "unassigned contour".

## UIA / smoke

- Automated UIA smoke can hang in non-interactive agent shells; **do not mark UIA PASS**.
- `smoke_desktop.py` still targets pre-OmniCam control names and is **not** a gate. Its replacement is `dotnet/tests/ui-smoke` (PowerShell + UIA: demo → nest → CAM → export with file assertions, stale banner). It runs locally via `run-all.ps1` and on the Windows CI job as a *non-blocking* step until it has a flake-free track record; treat a red UI smoke as "look at the screenshots", not as a release block yet.
- `OMNICAM_AUTO_EXPORT_DIR` (environment variable) makes exports skip the native dialogs and write into that folder — for the UI smoke and shop batch scripts only; never set it on an operator PC.
- `dotnet/artifacts/smoke-latest.json` and `evaluation-latest.md` date from 2026-07-22 and predate the OmniCam UI; they are not current evidence.
- Release goldens lock normalized NC only (`Category=GoldenRegression`). Labels, U/V, and UIA are **not** in that pack.
- Manual path: `docs/sprint/MANUAL_SMOKE_10MIN.md` — items start as `MANUAL PENDING`.

## Not in RC (Campaign 2+)

- True NFP / engine contest
- Material-removal solid simulation
- Signed MSI / learning patterns
- Encrypted woodjob
- Machine-specific ATC/M6 macros
