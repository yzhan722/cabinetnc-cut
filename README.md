# CabinetNC Cut / OmniCam

Standalone nesting + toolpath cutting station for panel furniture. Consumes Fusion exports
(`.cnjob` manufacturing snapshot, `cabinetnc.woodjob`, legacy cut-package JSON), nests, plans CAM,
posts NC for the shop's OSAI router (with Process 2 label pasting), and can reverse a machine
`.anc` back into panels for recuts.

Runtime: **.NET 10 · WPF · SkiaSharp · Clipper2 · SQLite** under `dotnet/`. Product direction and
the open decision about scope: [docs/VISION.md](./docs/VISION.md). Working agreement between the
two repositories (upstream gate / shop fork): [OWNERS.md](./OWNERS.md).

## Run the desktop

```powershell
$env:Path = "C:\Program Files\dotnet;" + $env:Path
cd dotnet
dotnet run --project src\CabinetNC.Desktop
```

Pack a runnable folder + zip: `powershell -ExecutionPolicy Bypass -File dotnet/scripts/pack.ps1` (see `dotnet/README.md`).

## Quality gates (what "green" means)

```powershell
cd dotnet
dotnet test tests/CabinetNC.Domain.Tests         -c Release
dotnet test tests/CabinetNC.Package.Tests        -c Release
dotnet test tests/CabinetNC.Infrastructure.Tests -c Release
dotnet test tests/CabinetNC.Desktop.Core.Tests   -c Release   # UI logic without WPF
dotnet build src/CabinetNC.Desktop               -c Release   # Windows only
```

| Gate | Where | Catches |
|------|-------|---------|
| Unit + behaviour tests | `tests/CabinetNC.*.Tests` | Geometry, CAM safety order, preflight codes, importers, SQLite round-trip |
| Golden regression | `tests/CabinetNC.Domain.Tests/Regression/ReleaseGoldenRegressionTests` + `tests/testdata/regression/goldens/` | "The NC for this job changed" — diff, then regenerate with `CABINETNC_UPDATE_GOLDENS=1` only if intended |
| Safety invariants | `Regression/NcSafetyInvariantTests` | Rapid below safe Z, cut past spoilboard allowance, feed/RPM not from recipe or ToolCatalog, spindle off while cutting, tool left low, cut outside a placed panel, `M6` in a Sheet×Tool file |
| Reverse round-trip | `NcReverseRoundTripTests` | Post output that `NcReverse` can no longer turn back into the same panels |
| Real shop programs | `ShopAncFixtureTests` over `tests/testdata/regression/shop-anc/*.anc` | Programs the machine actually ran must keep replaying |

CI: `.github/workflows/regression.yml` (Linux, the three suites) and `.github/workflows/windows-desktop.yml`
(Windows: Desktop + Worker build, suites again). Both must be green before a fork PR merges upstream.

Before and after any change to machine motion: [docs/sprint/POST_CHANGE_CHECKLIST.md](./docs/sprint/POST_CHANGE_CHECKLIST.md).
Before cutting: [docs/sprint/MACHINE_DRYRUN_CHECKLIST.md](./docs/sprint/MACHINE_DRYRUN_CHECKLIST.md); log the result in
[docs/sprint/SHOP_LOG.md](./docs/sprint/SHOP_LOG.md).

## Honest limits

[docs/sprint/KNOWN_LIMITATIONS.md](./docs/sprint/KNOWN_LIMITATIONS.md) — two post-processors with different Z frames and
tool-change behaviour, single-face only, grouped-BLF nesting (not NFP), Desktop has no automated tests
([plan](./docs/sprint/DESKTOP_TESTABILITY_PLAN.md)). Open shop questions: [dual-face questionnaire](./docs/sprint/DUAL_FACE_QUESTIONNAIRE.md),
[labeling requirements](./docs/sprint/LABELING_REQUIREMENTS.md).

## Layout

| Path | Role |
|------|------|
| `dotnet/src/CabinetNC.Domain` | Geometry, nesting, CAM, posts (`NcEmitter`, `NcEmitter.Troy`), labels, reverse |
| `dotnet/src/CabinetNC.FusionPackage` | `.cnjob` / woodjob / cut-package importers |
| `dotnet/src/CabinetNC.Application` | `ProjectSession` |
| `dotnet/src/CabinetNC.Infrastructure` | SQLite project store, workshop library (`%LocalAppData%\CabinetNC\library.json`), usage log |
| `dotnet/src/CabinetNC.Desktop.Core` | WPF-free UI logic (status inference, viewport math, sim timeline, unsaved-work fingerprint, recent files, export texts) with its own tests |
| `dotnet/src/CabinetNC.Desktop` | WPF shell (see testability plan) |
| `dotnet/src/CabinetNC.ComputeWorker` | gRPC named-pipe worker (mostly unused today; see VISION decision) |
| `docs/` | Vision, architecture, sprint acceptance plan, limitations, checklists |

## Archived: Vite prototype

`src/`, `scripts/`, `native/`, `desktop/` and `index.html` are the 2026-07 browser prototype that the .NET
product was ported from. They have not changed since the 2026-08-04 import. `npm install && npm run check`
still passes (17 content checks) and is kept only as a reference; do not add product work there.
