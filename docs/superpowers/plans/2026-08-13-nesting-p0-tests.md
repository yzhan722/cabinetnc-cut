# Nesting P0 Test Expansion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the first deterministic P0 nesting safety suite covering export gates, routing/cancellation, NFP/PIP boundaries, drag/holding behavior, Guillotine isolation, and generated invariant scenarios.

**Architecture:** Keep production algorithms unchanged unless a new safety test exposes a real defect. Tests live in focused Domain test files and use shared local builders rather than UI automation. Desktop-only Holding Bay rendering remains manual, while its Domain drag/clamp/collision primitives receive automatic coverage.

**Tech Stack:** .NET 10, C#, xUnit 2.9, CabinetNC Domain/Manufacturing APIs, Clipper2.

## Global Constraints

- Tests must be deterministic and use fixed scenario data/seeds.
- Every placed result must conserve parts: placed + unplaced equals input, except a PIP child remains in placed.
- Safety assertions take priority over utilization/optimality.
- Do not weaken `NestExportGate`, material/thickness separation, clearance, or single-face CAM gates.
- Do not add a new test dependency in this first batch.
- If a test fails, investigate root cause before changing production code.

## Execution Result (2026-08-13)

- [x] Baseline nesting tests: 26 passed.
- [x] Added `NestP0SafetyGateTests`: 16 cases passed.
- [x] Added `NestP0RoutingTests`: 8 cases passed.
- [x] Added `NestP0NfpPipTests`: 14 cases passed.
- [x] Added `NestP0InteractionTests`: 23 cases passed.
- [x] Full regression: Domain 146 / Package 31 / Infrastructure 4 passed.
- [x] Desktop Release build: 0 errors (existing NU1701 warnings remain).
- [x] Fixed six P0 defects exposed by the suite.

---

### Task 1: Establish the current nesting baseline

**Files:**
- No changes

**Interfaces:**
- Consumes: existing `CabinetNC.Domain.Tests` nesting tests
- Produces: baseline pass/fail counts and failure evidence

- [ ] **Step 1: Run all current nesting-related tests**

Run:

```powershell
dotnet test tests\CabinetNC.Domain.Tests -c Release --filter "FullyQualifiedName~Nest|FullyQualifiedName~PartsInPart|FullyQualifiedName~Guillotine" --verbosity minimal
```

Expected: all existing nesting tests pass before new cases are added.

- [ ] **Step 2: Record baseline count in the execution summary**

No source change; retain console output as evidence.

---

### Task 2: Add export-gate and invariant matrix cases

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NestP0SafetyGateTests.cs`

**Interfaces:**
- Consumes: `NestExportGate.Check`, `NestValidator.FindAabbCollisions`, `NestValidator.FindPolygonCollisions`
- Produces: P0 tests for mixed groups, legal/illegal ignore pairs, exact clearance, sheet separation, and part conservation

- [ ] **Step 1: Write failing/characterization tests**

Add xUnit cases equivalent to:

```csharp
[Fact]
public void Gate_rejects_mixed_material_and_thickness_on_same_sheet()
{
    var gate = NestExportGate.Check(panels, placements, 8);
    Assert.False(gate.Ok);
    Assert.Contains(gate.Errors, e => e.StartsWith("mixed_group_sheet:"));
}

[Theory]
[InlineData(0, true)]
[InlineData(7.999, false)]
[InlineData(8, true)]
public void Exact_clearance_boundary_is_deterministic(double gap, bool expectedOk)
{
    // Two 100x100 panels; placement separation = 100 + gap.
}
```

Also cover:

- placements on different sheets do not collide;
- unknown placement IDs do not suppress mixed-group errors for known panels;
- only an enabled PIP host/child pair may be ignored;
- reverse PIP pair is ignored;
- a third overlapping part remains blocked;
- empty placements fail when required and pass when `requirePlacements=false`;
- `placed + unplaced == input` for grouped BLF scenarios.

- [ ] **Step 2: Run the new class**

Run:

```powershell
dotnet test tests\CabinetNC.Domain.Tests -c Release --filter "FullyQualifiedName~NestP0SafetyGateTests"
```

Expected: failures identify current gate boundary defects; passing cases characterize existing behavior.

- [ ] **Step 3: Fix only confirmed production defects**

Modify only the responsible Domain method after tracing the failure. Re-run the exact failing test first.

- [ ] **Step 4: Re-run the class**

Expected: all P0 gate cases pass.

---

### Task 3: Add routing, progress, cancellation, and fallback cases

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NestP0RoutingTests.cs`

**Interfaces:**
- Consumes: `NestEngineRouter.Run`, `NestEngineRequest.Progress`, `CancellationToken`
- Produces: deterministic orchestration and cancellation guarantees

- [ ] **Step 1: Add controllable fake engines and tests**

Use small in-test `INestingEngine` implementations:

```csharp
sealed class CancelingEngine : INestingEngine
{
    public string Name => "canceling";
    public NestResult Pack(..., CancellationToken ct = default, IProgress<NestProgressReport>? progress = null)
        => throw new OperationCanceledException(ct);
}
```

Test:

- BLF progress begins and ends with monotonic `Done`, stable `Total`;
- preferred advanced failure reports fallback message and `FallbackReason`;
- advanced timeout falls back to BLF;
- caller cancellation before explicit BLF propagates cancellation;
- caller cancellation during advanced must not silently return a production result;
- unknown preference selects BLF and records `unknown_preference`;
- selected engine tag matches result and run log;
- PIP post-pass preserves engine tag.

- [ ] **Step 2: Run the new class**

Run:

```powershell
dotnet test tests\CabinetNC.Domain.Tests -c Release --filter "FullyQualifiedName~NestP0RoutingTests"
```

- [ ] **Step 3: Investigate and minimally fix confirmed cancellation/fallback defects**

Do not convert caller cancellation into fallback unless the specification explicitly requires it.

- [ ] **Step 4: Re-run routing tests**

Expected: all routing cases pass or unresolved product-policy failures are reported without weakening assertions.

---

### Task 4: Add NFP, grain, blocked-region, and PIP boundary cases

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NestP0NfpPipTests.cs`

**Interfaces:**
- Consumes: `ClipperNfpNestingEngine`, `PartsInPartPacker`, `NestSettings.PanelMayRotate90`
- Produces: P0 geometric safety coverage

- [ ] **Step 1: Add NFP and rotation cases**

Cover:

- rectangle and L/U-shape batches produce no polygon collision;
- all placements remain within sheet border;
- exact/tiny-clearance boundaries;
- grain-locked panels never use 90/270 rotation;
- allowed rotations are respected;
- blocked stock regions are not intersected;
- fixed scenario results are deterministic.

- [ ] **Step 2: Add PIP boundary cases**

Cover:

- child fits after clearance → slot created;
- child misses by 0.001 mm → no slot;
- non-through pocket is not a PIP host;
- different material or thickness → no slot;
- disabled stock → no slot;
- void smaller than `MinVoidMm` → no slot;
- rotated host transforms void correctly;
- multiple children do not overlap inside one void;
- disabled slot does not create ignore pair;
- PIP never creates a parent/child cycle in the scenario.

- [ ] **Step 3: Run the new class**

Run:

```powershell
dotnet test tests\CabinetNC.Domain.Tests -c Release --filter "FullyQualifiedName~NestP0NfpPipTests"
```

- [ ] **Step 4: Investigate failures and re-run**

Expected: production-safety invariants pass; utilization is not asserted as globally optimal.

---

### Task 5: Add drag/Holding primitives and Guillotine isolation cases

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/NestP0InteractionTests.cs`

**Interfaces:**
- Consumes: `NestDrag`, `GuillotineCutPlanner`, `NcEmitter`
- Produces: automatic coverage beneath Holding Bay UI plus no-NC isolation

- [ ] **Step 1: Add `NestDrag` theories**

Cover:

- clamp all four sheet borders;
- 90/270 rotations swap width/height;
- overlap returns exact fallback and `Blocked=true`;
- different-sheet parts do not block;
- same panel ID does not block itself;
- exact spacing is allowed, short by 0.001 is blocked;
- `allowOverlap=true` bypasses collision but still clamps;
- snap behavior for positive and negative coordinates.

- [ ] **Step 2: Add Guillotine isolation cases**

Cover:

- empty and insufficient remnants return null;
- valid vertical/horizontal/L plans stay inside sheet bounds;
- generating a preview plan does not add CutOps or `(guillotine)` commands to emitted NC;
- toggling preview has no effect on Sheet×Tool program count.

- [ ] **Step 3: Run the new class**

Run:

```powershell
dotnet test tests\CabinetNC.Domain.Tests -c Release --filter "FullyQualifiedName~NestP0InteractionTests"
```

- [ ] **Step 4: Record Desktop-only residual**

Holding Bay layout, hit-testing, drag visuals, and “holding non-empty blocks export” remain explicit Desktop Smoke items unless extracted behind a Domain service.

---

### Task 6: Full regression and test inventory

**Files:**
- Modify if needed: `docs/sprint/14-DAY-ACCEPTANCE-PLAN.md`

**Interfaces:**
- Consumes: all new P0 test classes
- Produces: fresh pass/fail counts and an actionable failure list

- [ ] **Step 1: Run Domain tests**

```powershell
dotnet test tests\CabinetNC.Domain.Tests -c Release --verbosity minimal
```

- [ ] **Step 2: Run full solution tests**

```powershell
dotnet test -c Release --verbosity minimal
```

- [ ] **Step 3: Build Desktop**

```powershell
dotnet build src\CabinetNC.Desktop\CabinetNC.Desktop.csproj -c Release --verbosity minimal
```

- [ ] **Step 4: Summarize**

Report:

- new test case count by class;
- passes/failures;
- root cause for each failure;
- production defects vs unresolved product-policy decisions;
- remaining manual Holding/NFP/PIP/Guillotine checks.

