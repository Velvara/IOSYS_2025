# Shelved Ideas

Features intentionally **deferred** — parked here so the design isn't lost and can be resumed cleanly.
Shelved ≠ cancelled; it means "not now." Each entry records the idea, why it's parked, what scaffolding
already exists, and where to pick it up.

---

## Vertex-Painted Per-Hold Risk (+ coupled Hold Icons) — climbing
**Shelved 2026-06-21.** Domain: `Game.Climbing` authored-cliff bake pipeline (this was step **C2**).

### What it is
Author per-handhold **risk** (and a per-hold **icon id**) by **vertex-painting** the climbable's high-poly
bake mesh. At bake time the handhold parser samples the painted vertex colours near each hold and stores two
values per hold:
- `RiskValue = avgR·redRisk + avgG·greenRisk + avgB·blueRisk` — drives a future tumble/slip roll (a hold can be "risky").
- `IconId = argmax(avgR, avgG, avgB)` — selects which world-space icon PNG to show (R→icon C, G→icon B, B→icon A); a pure channel pick, **not** a tint.

The world-space **icons** are a separate runtime system but are **coupled** to the same vertex paint, so they're shelved together.

### Why shelved
Deprioritized by the dev — not needed for the current climbing milestone. The bake pipeline (C1) works without it;
every baked hold currently gets `RiskValue = 0 / IconId = 0`, which resolves to the surface's `fallbackRisk` and no icon.

### What already exists (scaffolding — left in place, harmless)
- `Game.Core.Climbing.ClimbHoldData.RiskValue` (float) + `IconId` (byte) — fields exist, currently baked as 0.
- `ClimbableSurface` already carries the per-channel risk config: `fallbackRisk`, `redRisk`, `greenRisk`,
  `blueRisk`, plus `alwaysDry` (a weather hook). Today only `fallbackRisk` is meaningful.
- `HandholdParser` produces the holds geometrically; the colour-sampling step is the missing half (it sets
  `RiskValue = 0` with a note pointing here).

### To resume (design — from brief §4)
1. **Parser:** add colour sampling to `HandholdParser` — for each baked hold, average the painted colours of the
   nearby vertices, compute `RiskValue` + `IconId`, store on the `ClimbHoldData`. Colour source order (per-instance
   paint): `meshRenderer.additionalVertexStreams.colors` → `sharedMesh.colors` → `fallbackRisk`. Pass the surface's
   red/green/blue risks into the parser `Settings`.
2. **Authoring:** paint per-instance with **Polybrush** into `additionalVertexStreams` on the **EditorOnly** bake
   child (so one shared base mesh can carry different paint per instance). Parser reads `sharedMesh` geometry +
   `additionalVertexStreams` colours (same vertex indices).
3. **Bake tool:** no UI change — the per-piece `HoldDataSO` simply carries non-zero risk/icon.
4. **Icons (`HoldIconPool`):** pooled billboarded quads (**not** UI Toolkit — a deliberate perf exception),
   per-instance alpha via `MaterialPropertyBlock`, atlas or `DrawMeshInstanced` (≤ a few draws), 3 PNGs (one per
   channel). Active only while hanging on the surface; appear/disappear by a `visualizationRadius` with opacity
   fade; piggyback on the hold-streamer's nearby-hold set.
5. **Risk roll (brief §5):** `finalRisk = hold.RiskValue · rainMultiplier · staminaMultiplier`; probability roll
   on each new hold → tumble.

### Dependencies / sequencing when resumed
- Needs the **authored-cliff bake pipeline** (C1 ✓) + the **EditorOnly painted bake child** workflow.
- Icons benefit from the **HoldStreamer** (C4) for the nearby-hold set, but can run off a small surface's own list.
- The risk **roll** only does something once the **tumble/ragdoll** system (brief Phase 2/5) exists.

### Pointers
- Master brief: `_ClimbSystemBrief/ClimbSystem_Brief.md` §4 (icons + vertex risk), §5 (risk formula).
- Locked design notes: Claude memory `climbing-integration.md` (vertex-icons + risk decisions).
