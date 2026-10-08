# CabinetNC Cut / OmniCam — ownership and working agreement

Use with [ARCHITECTURE.md](./ARCHITECTURE.md). Rewritten 2026-09-02; the Vite dual-window
lanes that used to live here are archived at the bottom.

## Two repositories, two roles

| | `yzhan722/cabinetnc-cut` (upstream) | `Trojanes/cabinetnc-cut` (fork) |
|---|---|---|
| Role | Requirements, acceptance, tests, CI, docs — the **gate** | Shop-driven features against the real OSAI machine — the **source of truth for what the shop needs** |
| Integration branch | `main` | `sprint/14d-rc` |
| Merges | Fork PR → `main` only when Regression **and** Windows Desktop workflows are green | `upstream/main` → `sprint/14d-rc` at least weekly |
| Release evidence | Golden + safety invariants green | Machine dry-run log (see `docs/sprint/POST_CHANGE_CHECKLIST.md`) |

The fork owner cuts on the machine; the upstream owner cannot. Every shop decision that
changes NC output must therefore arrive with evidence the other side can read: a golden
diff, a safety-invariant run, and for motion changes a dry-run note.

## Cadence and service levels

1. **Sync weekly.** Monday: fork merges `upstream/main` into `sprint/14d-rc` and pushes.
   Nine days apart was enough to hide a broken golden and two missing safety fixes.
2. **Red CI is fixed within one working day**, by whoever pushed. If the fix needs the other
   side (for example a golden owned upstream), open an issue the same day and say so in the PR.
3. **Orphan branches die within a week.** Anything not merged or explicitly parked (issue link
   in the branch description) after seven days is deleted. `cursor/nc-inner-offset-df9a` held
   two real fixes for nine days before anyone noticed.
4. **Goldens are regenerated only with a reason.** `CABINETNC_UPDATE_GOLDENS=1` runs must be
   accompanied by the diff and one sentence on why the new output is intended.

## Pull request size

- One PR = one module or one shop story (post-processor, labeler, nest UX, importer, reverse/recut …).
- Soft limit **≤ 1 500 changed lines** of source, tests included. Above that, split before asking for review.
- A PR that changes machine motion (`NcEmitter*`, `PostRecipe`, `ProfileBridge`, `OuterProfileOrder`,
  `LabelExport.EmitPro2`) must link the completed checklist in `docs/sprint/POST_CHANGE_CHECKLIST.md`.
- Commits named `checkpoint …` are fine locally; squash them before the PR.
- No scratch files at the repo root (`tmp_*`), no machine-local absolute paths in tests
  (use `dotnet/tests/testdata/regression/shop-anc/` for real programs).

## What must stay true (both repos)

| Guarantee | Enforced by |
|---|---|
| Domain / Package / Infrastructure tests green on Linux | `.github/workflows/regression.yml` |
| Desktop and Worker compile; suites also green on Windows | `.github/workflows/windows-desktop.yml` |
| Same job ⇒ same normalised NC | `Regression/ReleaseGoldenRegressionTests` + goldens under `dotnet/tests/testdata/regression/goldens/` |
| No rapid below safe Z, no cut past the spoilboard allowance, feeds/RPM from the recipe or ToolCatalog, spindle on before cutting, program ends retracted, cuts stay on placed panels | `Regression/NcSafetyInvariantTests` |
| Whatever the post emits, `NcReverse` recovers the same panels | `NcReverseRoundTripTests` |
| Real machine programs keep replaying | `ShopAncFixtureTests` over `shop-anc/*.anc` |
| Goldens are LF on every OS | `.gitattributes` |
| Desktop logic that does not need WPF is tested without WPF | `CabinetNC.Desktop.Core` + `Desktop.Core.Tests` (incl. the no-WPF-reference guard) |

## Code ownership (review routing)

| Area | Owner | Second |
|---|---|---|
| `dotnet/src/CabinetNC.Domain/Manufacturing/NcEmitter*.cs`, `PostRecipe.cs`, `ProfileBridge.cs`, `OuterProfileOrder.cs` | Troy (machine) | Yi Zhou (goldens, invariants) |
| `dotnet/src/CabinetNC.Domain/Manufacturing/LabelExport.cs`, `Desktop/LabelBmp.cs` | Troy | Yi Zhou |
| `dotnet/src/CabinetNC.Domain/Nesting/**` | Yi Zhou (P0 safety tests) | Troy |
| `dotnet/src/CabinetNC.Domain/Manufacturing/NcReverse*.cs`, `NcProcessInfer.cs`, `NcToPanels.cs` | shared | — |
| `dotnet/src/CabinetNC.Desktop/**` | Troy | — (WPF shell; logic moves to `Desktop.Core`, see `docs/sprint/DESKTOP_TESTABILITY_PLAN.md`) |
| `dotnet/src/CabinetNC.Desktop.Core/**` | shared | must stay WPF-free; every new behaviour lands with a test |
| `dotnet/tests/**`, `.github/**`, `docs/**` | Yi Zhou | Troy |

## Archived: Vite dual-window lanes (2026-07)

`src/`, `scripts/`, `native/` and `desktop/` have not changed since the 2026-08-04 import.
The "Window A · Geom / Window B · Nest/CAM" lanes, hot-file ACK rules and paste-ready
prompts that used to be in this file applied to that prototype. `npm run check` still passes
and is kept as a content oracle only; new product work happens in `dotnet/`.
