# Shop `.anc` replay fixtures

Real OSAI-Troy programs that the machine has actually run. `ShopAncFixtureTests`
replays every `*.anc` in this directory through `NcReverse` and requires:

- motion is recovered (`Strokes.Count > 10`, no `no_motion` warning);
- at least one closed outer profile becomes a panel (no `no_panel` warning);
- the reverse result round-trips into a `CutPackage`.

This replaces the old test that pointed at `E:\Work\CNC software\...` on one PC and
silently passed everywhere else.

## Adding a fixture

1. Copy the program the machine ran (`*.anc`), keep the file name meaningful
   (`2026-09-02_lounge_divider_recut.anc`).
2. Strip anything customer-identifying from comments if needed; geometry stays.
3. Add one line to the table below: commit SHA of OmniCam that produced it, machine,
   and whether the cut was accepted on the shop floor.
4. Keep files under ~300 KB. Files are stored byte-for-byte (`*.anc -text`), CRLF included.

| File | Produced by (SHA) | Machine | Shop result |
|------|-------------------|---------|-------------|
| _(none yet)_ | | | |
