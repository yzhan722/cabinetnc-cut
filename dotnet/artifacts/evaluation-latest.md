# CabinetNC Product Evaluation

- Evaluated: 2026-09-03T16:24:33.6650126+08:00
- Score: **99/100** (target 85)
- Hard gates: **PASS**
- Status: **READY**

## Criteria

| ID | Score | Result | Evidence |
|----|-------|--------|----------|
| A1-import | 6/6 | PASS | tests + ui-smoke 01 (demo import) |
| A2-nest | 8/8 | PASS | tests + ui-smoke 01/02 (nest, stale -> re-nest) |
| A3-cam-nc | 8/8 | PASS | tests + ui-smoke 01 (compute-all, sim step) |
| A4-export | 6/6 | PASS | export sources + tests + ui-smoke 01/03 (.anc + flat BMPs) |
| A5-persistence | 4/4 | PASS | SQLite/library sources + tests + ui-smoke 04/05 |
| A6-modules | 3/3 | PASS | ui-smoke 03/04 (remnants module) |
| B1-full-tests | 6/6 | PASS | D:\project\cabinetnc-cut\dotnet\artifacts\evaluation-tests.log |
| B2-rotation-xy | 5/5 | PASS | rotation unit tests + NcSafetyInvariantTests |
| B3-invalid-input | 3/3 | PASS | importer rejection tests + ui-smoke 05 |
| B4-poly-gap | 4/4 | PASS | Clipper tests + ui-smoke 01 |
| B5-preflight | 4/4 | PASS | NcPreflight tests + export gate in ui-smoke 01 |
| B6-review-lint | 2/3 | PARTIAL | 0 CS/high-NuGet/compatibility warnings |
| C1-startup-worker | 3/3 | PASS | ui-smoke startup (ready status) |
| C2-stage-gates | 2/2 | PASS | ui-smoke 02 (stale banner, workflow pills) |
| C3-module-navigation | 3/3 | PASS | ui-smoke 03/04 |
| C4-feedback-dialog | 2/2 | PASS | toasts asserted in ui-smoke 01/04 |
| C5-manual-library | 3/3 | PASS | SMOKE_CASE_LIBRARY.md + MANUAL_SMOKE_10MIN.md |
| C6-status-preflight | 2/2 | PASS | ui-smoke 01 export status |
| D1-domain-tests | 3/3 | PASS | Domain/Package/Infrastructure/Desktop.Core suites |
| D2-repeatable-uia | 2/2 | PASS | ui-smoke results.json + exit code |
| D3-open-dependencies | 2/2 | PASS | Clipper2 package; no MakerHub binaries |
| D4-doc-consistency | 3/3 | PASS | rubric + manual library |
| E1-desktop-build | 5/5 | PASS | D:\project\cabinetnc-cut\dotnet\artifacts\evaluation-build.log |
| E2-pack-script | 3/3 | PASS | scripts/pack.ps1 |
| E3-automated-smoke | 4/4 | PASS | D:\project\cabinetnc-cut\dotnet\artifacts\ui-smoke\results.json |
| E4-manual-runbook | 3/3 | PASS | manual cases with steps/expected/risk |

## Residual risk

- True NFP/DXOPT-grade placement is not implemented
- CAM simulation is point-playhead, not material removal
- SkiaSharp/OpenTK emits NU1701 target-framework compatibility warnings
- Signed MSI and real-machine validation remain
- UI smoke runs on the hosted Windows runner but is still non-blocking in CI (see windows-desktop.yml)
