# Climbing — Session Handoff & Setup (2026-06-18)

Standalone pick-up doc for the climbing work in `ClimbController.cs`
(`Assets/_Project/Climbing/Core/`). If the chat session is gone, this is what to do next
and what every recent change was.

---

## ▶ RESUME HERE (session 2026-06-24 — MANTLE / REACH-TOP wired, CODE-COMPLETE pending the ClimbUp clip)

**MANTLE IS IN (code-only, pending your animator work + recompile).** Decisions taken with the dev:
scripted interpolation (not root motion); automatic trigger on reaching a top; event-driven finalize
(`OnMantleComplete`) with a safety timeout. All in `ClimbController.cs`:

- **Trigger** (`TryMantle`, called each frame in `TickClimb` after `UpdatePoseSwitch`): fires when
  `Dot(AvgOutward(), up) > mantleEnterDot` (≈0.5, the reserved positive-`d` top zone) AND
  `ComputeMantleLanding` passes — an up-ray from the hands is **clear**, a down-probe just past the lip
  finds a near-horizontal surface, and `Physics.CheckCapsule` confirms standing room. All probes use the
  new **`mantleSurfaceMask`** (default Everything — **set this to your ground/environment layers**).
- **Move** (`BeginMantle`/`TickMantle`): captures start + landing, plays **`ClimbUp`** in `ClimbingLayer`,
  fades FBBIK out, and lerps the body up-and-over (`mantleDuration`, SmoothStep) while keeping the climb
  layer at full weight so ClimbUp reads through.
- **Finalize** (`OnMantleComplete` animation event → `FinishMantle`): snaps to the landing,
  `SetVerticalVelocity(0)`, sets a `regrabCooldown`, then the normal release path hands control back to
  the FSM standing on top. A **`mantleSafetyTimeout`** finalizes anyway if the event never fires.
- Also added: **regrab cooldown** — both mantle *and* a debug-key drop now suppress grab detection for
  `regrabCooldown` so you don't instantly re-grab.

### ⚠️ EDITOR PREREQS before this can be tested (your side)
1. **`ClimbUp` state** in **`ClimbingLayer`** (top level, so the name-hash CrossFade resolves), driving the
   mantle/top-out clip. Author it **full-body** — if you upper-body-masked `ClimbingLayer` for the animated
   legs, the mantle won't move the legs; either keep `ClimbingLayer` full-body (the legs layer is faded to
   0 during the mantle anyway) or put `ClimbUp` on a full-body layer.
2. **`OnMantleComplete` animation event** near the end of the ClimbUp clip (calls the public method on
   ClimbController — same GameObject as the Animator, so it routes). No event = the safety timeout still
   finalizes, just less precisely timed; keep `mantleSafetyTimeout` ≥ the clip length.
3. Set **`mantleSurfaceMask`** to your real ground/environment layers (default Everything works for a quick
   test but will treat the climbable itself as ground).
4. Tune `mantleEnterDot` / `mantleLandingForward` / `mantleLandingProbeUp`-`Down` / `mantleDuration` to your
   geometry. **Test spot:** a Flora trunk's bent near-horizontal tip, or a baked authored cliff with a flat top.

**STILL PENDING after mantle reads well:** full real entry/exit + charged jump-off (track A, uses the
unused `AddLaunchVelocity`), reach-bottom, then the brief's ragdoll/slide/tumble/weather.

---

## (Earlier) Climbing visual/polish baseline

Climbing is **visually complete + polished** and climbs **baked authored cliffs** (the C1 bake pipeline
is done & verified) **and Flora trunks**. Done since the free-hang work: per-step braced rotation **tween**
(`bodyTurnDurationScale`) + a position-pop fix, **head-look** (eased, with a `HeadLookTarget` mode toggle),
**trunk-axis torso orientation** (`alignTorsoToTrunkAxis`, trunks only — "up" heads toward the tip however
the trunk bends), hand `handHoldOffset` (wrist→fingers), legs-only-when-moving + `ClimbLegsSpeed` freeze,
and a code cleanup (removed surface-facing + the shelved lean). **C2 vertex-paint risk/icons is SHELVED** —
see `SHELVED_IDEAS.md`.

**Next task the dev chose: MANTLE / reach-top.** Top-out — when the climber reaches a surface **top**
(near-horizontal grab zone) play a ClimbUp and hand control back to locomotion standing on top. It is an
**exit transition**; note the *full* real entry/exit + charged jump-off (track A) is **still pending** — the
dev opted to do mantle first.

**Where it hooks:** `UpdatePoseSwitch` in `ClimbController.cs` handles only free-hang today
(`d = Dot(AvgOutward(), up) < freeHangEnterDot`). The **positive-`d` zone** (`d > +thresh` = outward points
up = on top of a ledge, chest-down) is **reserved** and currently just stays braced — that's where the
mantle trigger goes (own hysteresis; don't let it fight the free-hang band).

**Plan (brief §2.8 "Exit — reach top"):** trigger when the hands near a top — combine `d > +mantleEnterDot`
(≈ +0.5) with a **clearance check**: raycast **up** from both hands for open space + `Physics.CheckCapsule`
at the landing spot for standing room. On trigger → play `ClimbUp` (new animator state/clip) → move the body
up + over → animation event `OnMantleComplete` (or a timed fallback) → fade FBBIK out, `motor.SetVerticalVelocity(0)`,
`controlLock.ReleaseExternalControl()`, set a regrab cooldown so it doesn't instantly re-grab.

**Decide with the dev FIRST:** ClimbUp via **root motion** (the clip drives the move onto the ledge) vs a
**scripted** interpolation to a computed landing point (raycast down past the lip → move the CharacterController).

**Dev editor prereqs:** a ClimbUp/mantle clip + animator state in the climb layer; an `OnMantleComplete`
animation event (or accept a timed fallback).

**Test spot:** a Flora trunk's bent **tip** goes near-horizontal → natural mantle test (the new trunk-axis
torso tilt already inclines the torso as the trunk bends, so mantle should take over right as `d` crosses
the threshold). An authored cliff with a flat top works once baked. **Seams ready:** the reserved positive-`d`
zone, `IControlLock.ReleaseExternalControl`, the unused Phase-0 `IPlayerMotor.SetVerticalVelocity`, the
animator ClimbingLayer. Read **brief §2.8** before building.

---

## (Earlier) TL;DR — animated-legs editor task

We **pivoted the legs** from procedural foot-stepping to **animation + foot-IK**, because the
procedural feet stayed janky (flip-flopping holds, knees inverting, body turning outward). The
code for the new approach is written and waiting behind a toggle. **You need to author the
animator side**, then flip the toggle.

Decisions locked this session:
- Leg clips: **2D blend — up / down / left / right**.
- Foot contact: **surface raycast-smear** (feet pin to the real trunk surface, NOT to discrete holds).

### Editor setup for animated legs
1. **Lower-body Avatar Mask** — enable **pelvis/hips + both legs + both feet**; disable spine/arms/head.
2. **Animator layer `ClimbLegsLayer`** (exact name) — type **Override**, assign that mask, weight **0**
   (code drives the weight).
3. **2D blend tree** in that layer, two **float** params named exactly **`ClimbMoveX`** and **`ClimbMoveY`**:
   - Centre **(0,0)** = idle **braced-legs** pose (still = no stepping).
   - **+Y** climb up · **−Y** climb down · **+X** traverse right · **−X** traverse left.
   - Freeform Directional blend type recommended.
4. **Avoid double-driving the legs:** the existing `ClimbingLayer` is full-body Override. Either
   re-mask it to the **upper body**, or leave it and rely on `ClimbLegsLayer` being a **higher layer
   index** so the masked legs win. (Upper-body mask is cleaner.)
5. On `ClimbController`, set **`useAnimatedLegs` = ON**.
6. FBBIK must stay **enabled** (it is) — the foot-smear runs off its pre-solve callback.

### How to test animated legs
- **Grab** → upper body hangs as before; legs play the idle braced pose.
- **Climb up/down/around** → legs animate; each foot should **snap to the trunk when the clip plants
  it** and **follow the clip when it lifts** (stance is auto-derived from how close the animated foot
  is to the surface — no baked curves needed).
- Toggle `useAnimatedLegs` **off** anytime to fall back to the procedural feet.

### Known gap (intentional in v1)
**No foot-lock yet.** If a *planted* foot slides because the clip's cadence ≠ actual climb speed,
that's skating. Fix = freeze the foot in world space during its stance phase (and/or scale clip
playback by climb speed). Left out of v1 to see how far auto-stance gets us. **Report if it skates.**

---

## Current state of the system

Working & dev-approved on a grown Flora trunk (debug key **C** = grab/release):
- **Hands:** grab + camera-relative hand-over-hand traversal, elbow chirality fixed (FBBIK bend
  constraints), anti-cross, max-separation gap-close, shuffle gait.
- **Body pose:** faces the **flattened** into-surface direction, upright (yaw only) — the original
  pre-patch logic, restored. The lean and standoff patches were **shelved** (see below).
- **Torso standoff:** live again as a **toggle** — `enableStandoff` OFF = current (body can clip the
  trunk), ON = push the body off the surface (chest+hip probes). Use it to compare.
- **Lean (SHELVED):** never read right; behind `enableLean` (**keep OFF** = previous flattened rotation).
  Re-evaluate later, likely as a distance-driven lean from the two standoff probe deltas.
- **Animator climb pose:** `ClimbingLayer` (ClimbHang / FreeHang) driven by code; braced↔free chosen by
  `Dot(outwardNormal, up)` with hysteresis.
- **Legs:** procedural feet exist but are being replaced by animation+IK (above). `useAnimatedLegs` off
  = procedural, on = animated.

Phase-0 controller seams (done earlier, dev-verified): `IPlayerMotor.SetVerticalVelocity` /
`AddLaunchVelocity`, `PlayerStamina.ClimbDrainRate`/`SetClimbState`, `Game.Tools` gated on external
control, `Game.Core` climb contracts, Flora trunk hold emit from `TrunkGenerator`.

---

## Inspector reference (ClimbController)

### Animated Legs (the new path)
| Field | Meaning |
|---|---|
| `useAnimatedLegs` | OFF = procedural feet; ON = masked clip + foot-smear IK. |
| `climbMoveSmooth` | How fast `ClimbMoveX/Y` ease toward input. |
| `footContactNear` / `footContactFar` | Plant↔swing thresholds (animated-foot→surface distance). Pinning during swing → lower `Far`; never pinning → raise `Far`. |
| `footSmearBackup` | Probe origin backed out along the normal — raise if a foot can't find the surface. |
| `footSmearRadius` / `footSmearMaxDist` / `footSmearSurfaceOffset` | Probe thickness / reach / lift off surface. |
| `footIKWeight` | Max foot position IK weight when planted. |
| `footSmearRotWeight` | Max foot rotation IK weight (lower = keep more of the clip's foot angle). |

### Body lean — SHELVED (keep `enableLean` OFF)
| Field | Meaning |
|---|---|
| `enableLean` | **OFF = restored previous rotation** (flattened, upright, yaw only). ON = experimental full-3D lean (never read right). |
| `bodyOrientSpeed` / `footLeanInfluence` | Only used by the shelved lean. Ignore while `enableLean` is off. |

### Torso standoff (two probes) — SHELVED (call commented out)
The `ApplyStandoff(...)` call in `UpdateBodyPose` is commented out. Methods + fields below stay dormant
(no console warnings) for a one-line re-enable. Fields: `enableStandoff`, `chestProbeHeight`/`chestStandoff`,
`hipProbeHeight`/`hipStandoff`, `standoffRadius`/`standoffBackup`/`maxStandoffPush`/`standoffSpeed`.
Note: pure translation honoured the LARGER of the two pushes; different chest/hip gaps would need a lean
(e.g. a distance-driven lean from the two probe deltas) — the likely direction if revisited.

### Feet (procedural fallback — only when `useAnimatedLegs` is OFF)
`footDrop`, `footSide`, `legReach`, `footBelowHands`, `footHoldClearance`, `footCrossMargin`,
`footStickRadius` (stickiness — raise if feet hop between holds), `footMoveDuration`,
`footStepInterval`, `footWeightFadeSpeed`, `footGripRotation` + `footGripMirror` (per-axis chiral
mirror for the right foot, default `(-1,1,1)`), `kneeBendWeight` (dev set 0 — away-from-wall bend
inverted knees at 1), `kneeOutward`, `hipDropFromHands`, `hipForwardOffset`.

---

## Animator parameters this code expects
- `isClimbing` (bool) — set on grab/release.
- `ClimbHang`, `FreeHang` (states in `ClimbingLayer`, top level so name-hash Play/CrossFade resolves).
- `ClimbMoveX`, `ClimbMoveY` (floats) — drive the `ClimbLegsLayer` 2D blend.
- Layers by name: `ClimbingLayer` (exists), `ClimbLegsLayer` (you add).

---

## Roadmap after the animated legs read well
1. **Foot-lock** (if skating) — world-space pin during stance ± clip playback scaled by climb speed.
2. **Two-mass pendulum** — adds the chest-vs-hip twist a single transform can't; repoints
   `HipPosition` at `pendulum.LowerPos` (one line). `TwoMassPendulum` + `OscillatorBank` already exist,
   not yet wired; needs the `CharacterPendulum` LateUpdate follow.
3. Reach-top / **mantle** (`Dot(avgOutward, up)` > +threshold zone is reserved for it).
4. Oscillator sway, HoldStreamer, authored-cliff bake pipeline, then the rest of the brief
   (`ClimbSystem_Brief.md`): ragdoll, slide/tumble, icons/risk, weather/health hooks.

---

*Full design rationale & chronology: Claude memory `climbing-integration.md`. Master brief:
`_ClimbSystemBrief/ClimbSystem_Brief.md`.*
