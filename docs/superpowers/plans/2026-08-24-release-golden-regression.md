# 发版金样回归 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a long-lived golden NC regression pack (normalizer + three synthetic jobs + reserved fixture folder) on top of the existing `dotnet test -c Release` suite.

**Architecture:** Keep goldens in the Domain test project. One runner nests, plans, preflights, and posts; comparison is against normalized NC plus layout/preflight code files. Production emitters are not changed.

**Tech Stack:** .NET 10, xUnit, existing CabinetNC.Domain nest/CAM/post types.

## Global Constraints

- Branch `sprint/14d-rc`; no force-push.
- Do not change U/V label geometry or `D:\Label` / `D:\CNC` paths.
- Do not put the normalizer in production Domain.
- Goldens follow the current emitter; do not change shop NC to match tests.
- Env var to rewrite goldens: `CABINETNC_UPDATE_GOLDENS=1`.

---

### Task 1: NC normalizer

**Files:**
- Create: `dotnet/tests/CabinetNC.Domain.Tests/Regression/NcTextNormalizer.cs`
- Create: `dotnet/tests/CabinetNC.Domain.Tests/Regression/NcTextNormalizerTests.cs`

- [ ] Write failing tests for CRLF, N-words, dropped decor comments, kept `(tool` / `(UAO,` / `M6 T`
- [ ] Implement `NcTextNormalizer.Normalize`
- [ ] `dotnet test tests/CabinetNC.Domain.Tests -c Release --filter FullyQualifiedName~NcTextNormalizerTests`

### Task 2: Runner + three jobs + goldens

**Files:**
- Create runner, fixtures, facts, `testdata/regression/goldens/{jobId}/`, `jobs/README.md`

- [ ] Implement runner against `GroupedBlfNester` / `OpsPlanner` / `NcPreflight` / `SheetBundleBuilder` / `NcEmitter`
- [ ] Generate goldens with `CABINETNC_UPDATE_GOLDENS=1`
- [ ] Re-run without the env var (must stay green)
- [ ] Update `docs/testing/PRODUCT_EVALUATION_RULES.md` and `docs/sprint/KNOWN_LIMITATIONS.md`

---
