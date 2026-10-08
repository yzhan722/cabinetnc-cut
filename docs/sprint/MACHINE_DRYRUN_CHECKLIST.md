# Machine dry-run checklist

Use after loading a real job and exporting a bundle from Desktop (`一键打包`).

## Before power

- [ ] Build identified by **commit SHA** (write it on the job sheet; goldens and invariants for that SHA were green in CI)
- [ ] Read `KNOWN_LIMITATIONS.md` — note there are **two posts** with different Z frames and tool-change behaviour
- [ ] Which post produced the files?
  - **Sheet × Tool** (`{job}_S{n}_{Tn}.nc`): Z0 = sheet top, safe Z 8, **no M6** — operator loads one tool program at a time
  - **Troy single-file** (`.anc`): Z0 = board bottom, safe Z 30, **`M6 T<n>` per tool** — ATC changes tools automatically; confirm the `M6` macro parks Z before the first `G0 X.. Y.. Z30`
- [ ] Confirm tool magazine: T1 6.35 / T2 10 / T3 3 (or remapped) matches `M6 T` numbers in the file
- [ ] If the file has Process 2 (`LS11`/`M701`): every `LS11` stem has `<stem>.bmp` **directly in** the label software's picture folder (`D:\CNC` on the shop PC, no sub-folder); label PC NIC is `192.168.0.4` and can ping `.2` (controller) and `.3` (printer)

## Bundle contents

- [ ] `{job}.bundle.json` present (`outputPolicy: sheet_x_tool_nc`)
- [ ] Each sheet has `{job}_S{n}.dxf`, `{job}_S{n}.manifest.json`
- [ ] Each sheet×tool has `{job}_S{n}_{Tn}.nc` (not a mixed-tool single NC)
- [ ] Manifest `programs[]` lists every tool NC
- [ ] `{job}_bom.csv` and `{job}_labels.html` present
- [ ] Manifest panel/tool IDs match labels

## Preflight (software)

- [ ] Manufacturing not dirty (re-nest after edits)
- [ ] Nest export gate OK (no poly/AABB/mixed-group errors)
- [ ] No missing ToolId
- [ ] No `pocket_depth_missing` / `pocket_too_small_for_tool`
- [ ] Outer contour after drill/groove in NC order within each tool file
- [ ] Mixed thickness: outer depth = thickness + 0.5 (spot-check 15 vs 18)
- [ ] If any B-side ops exist: registration strategy configured — else expect block
- [ ] NC header shows ToolId / DiameterMm / FeedXY / FeedZ / RPM matching ToolCatalog

## On machine (air cut)

- [ ] Sheet × Tool: load `S1_T1.nc` only first (single tool); then `S1_T2.nc` / `S1_T3.nc` as required (manual tool change)
- [ ] Troy `.anc`: air-cut with Z offset raised by the board thickness first; watch the first rapid after each `M6` (combined XY+Z move)
- [ ] Origin = sheet SW (or note if fixture differs)
- [ ] Spindle/tool length offset verified for each tool used
- [ ] Dry-run contour clears clamps, including travel-optimised entry corners
- [ ] Peck/drill depth safe vs spoilboard (+1.0 mm max software allowance; Troy through pass is −0.55)
- [ ] Repeat for remaining sheets

## Evidence to record (so the other side can review without the machine)

- [ ] Commit SHA, post used, file names
- [ ] Photo/video of the first profile pass and of any tab/bridge
- [ ] Any operator override (feed %, Z offset) and why
- [ ] Result: accepted / rework / stopped — one line each
- [ ] Copy the `.anc` that actually ran into `dotnet/tests/testdata/regression/shop-anc/` with a row in its README

## Stop / escalate

- [ ] Any collision warning → do not cut
- [ ] Dual-face needed → wait for the flip answers in `DUAL_FACE_QUESTIONNAIRE.md` before B programs
- [ ] `M6` behaviour differs from the macro assumption → stop, do not edit the post on the shop PC; file the observation
- [ ] Labeler stuck in `P2M701` / `@I54=0` → stop Process 2, follow `LABELING_INCIDENT_HANDOVER_2026-08-19.md`; never bypass sensors or wait conditions
