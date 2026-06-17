# Procedural Climbing System — Implementation Brief (PlayerV2)

**Target architecture:** `Game.Climbing` (new assembly) + `Game.PlayerV2` (canonical controller).
**Supersedes:** `ClimbSystem_Brief_old.md` — that brief targets the **deleted** StarterAssets
`ThirdPersonController`. Its *peripheral mechanics* (slide, tumble, ragdoll, stamina, risk
zones, UI, entry/exit) are reused; its *integration wiring* is discarded. Do not reference it
for connection points — use this document.

Source article: <https://www.uproomgames.com/dev-log/procedural-climbing>. Reference images in
`_ClimbSystemBrief/*.png`. IK system: **FinalIK** (FullBodyBipedIK), owned in project.

---

## 0. Architecture & Operating Model

### 0.1 Where climbing lives
A new assembly **`Game.Climbing`** at the Tools tier:

```
Game.Climbing  →  Game.Core, Game.PlayerV2, FinalIK, Unity.Cinemachine   (autoReferenced:false)
```

Acyclic: `Core/Systems ← Powers ← PlayerV2 ← Tools ← UI`, with `Climbing` parallel to `Tools`
(both depend on `PlayerV2`). Climbing **never** is referenced by `PlayerV2`; the controller
stays ignorant of climbing. The world-space climbing **UI** (Section 7) lives in `Game.UI`,
which gains a `→ Game.Climbing` reference to read a climbing-state interface.

Folder layout:
```
Assets/_Project/Climbing/                                          [Game.Climbing]
├── Core/            ClimbController, ClimbState enums, IClimbUIState
├── IK/              EffectorRig (5-effector LAST/CURRENT/NEXT), EaseLerp
├── Dynamics/        OscillatorBank, CharacterPendulum
├── Holds/           ClimbableSurface, HandholdParser (editor bake), HoldStreamer, HoldData SO
├── Flora/           TrunkClimbableEmitter (bridges TrunkGenerator → holds)
├── Visuals/         HoldIconPool (billboarded, pooled)
├── Camera/          ClimbCameraReframe (drives CinemachineThirdPersonFollow)
└── Editor/          ClimbBakeWindow, ClimbableSurfaceEditor
```

### 0.2 Takeover model (decision: external takeover, **not** an FSM ClimbState)
Climbing is a self-contained subsystem that **takes over the body** through the existing seam:

1. On entry it calls `IControlLock.RequestExternalControl()` → FSM enters the `Critical`-priority
   `ExternalControlState`, which already calls `Motor.SuspendLocomotion()` (zeros locomotion
   speed + Speed/MotionSpeed animator) and `PlayerCameraRig.SetFrozen(true)`.
2. Climbing then **re-enables free-look** (`PlayerCameraRig.SetFrozen(false)` — decision) and
   drives `transform` directly + FinalIK each frame.
3. On exit it sets exit velocity (Section 0.4) and calls `IControlLock.ReleaseExternalControl()`;
   `ExternalControlState.CheckTransitions` hands control back to `Jump`/`Move`/`Idle` automatically.

No `ClimbState` is added to the FSM: the subsystem needs its own `Update`/`FixedUpdate`/
`LateUpdate` split (pendulum sim in `FixedUpdate`, pendulum follow + camera in `LateUpdate`,
FinalIK solving last), which a single FSM `OnUpdate` tick cannot express.

The `CharacterController` stays **enabled but unused** during a climb (no `Motor.Move` runs in
`ExternalControlState`); climbing writes `transform.position`. Keep it enabled so capsule
dimensions (`cc.radius`/`cc.height`) are readable for the mantle clearance check.

### 0.3 Script execution order (set explicitly in Project Settings)
```
PlayerController  →  ClimbController  →  CharacterPendulum  →  FinalIK (FullBodyBipedIK)
```
`PlayerCameraRig` (LateUpdate) and `CharacterPendulum`/`ClimbCameraReframe` (LateUpdate) must be
ordered so the pendulum follow and camera read final effector positions. Use
`[DefaultExecutionOrder]` on the climbing scripts; verify FinalIK solves after targets are set.

### 0.4 Resolved interfaces (climbing → player, at runtime via `GetComponentInParent`)
| Need | Resolved as |
|---|---|
| Take/return control | `IControlLock` |
| Transform + CharacterController + launch/vertical velocity | `IPlayerMotor` (extended, §1.1) |
| Free-look toggle | concrete `PlayerCameraRig.SetFrozen(bool)` |
| Move/Look input | concrete `InputHandler` |
| Jump-hold / Use input | direct `PlayerInput` action subscription (§1.4) |
| Stamina drain + fatigue | concrete `PlayerStamina` (extended, §1.3) |
| Incoming velocity (slide/ragdoll) | `IPlayerMotor.Controller.velocity` |

Interfaces aren't Unity-serializable — resolve at runtime, never via inspector links.

---

## 1. Phase 0 — Controller & Tools Preparation

Isolated, low-risk changes to `Game.PlayerV2` and `Game.Tools`. **Each must be verified with a
clean Console and identical existing behaviour before Phase 1.** (Claude cannot run Unity —
after each change, the dev recompiles and confirms.)

### 1.1 `IPlayerMotor` + `PlayerMotor` — exit velocity & horizontal launch
The motor owns `_verticalVelocity` privately and has **no horizontal launch momentum** (air
control is input-only). Climb jump-off (decision: *add horizontal launch*) needs both.

**`IPlayerMotor`** — add:
```csharp
float VerticalVelocity { get; }                                  // read (already on PlayerMotor)
void  SetVerticalVelocity(float v);                              // exit/jump-off vertical impulse
void  AddLaunchVelocity(Vector3 horizontalWorld, float decayRate); // decaying horizontal launch
```

**`PlayerMotor`** — add:
```csharp
private Vector3 _launchVelocity;     // world-space, horizontal
private float   _launchDecayRate;

public void SetVerticalVelocity(float v) => _verticalVelocity = v;

public void AddLaunchVelocity(Vector3 horizontalWorld, float decayRate)
{
    horizontalWorld.y = 0f;
    _launchVelocity   = horizontalWorld;
    _launchDecayRate  = Mathf.Max(0.01f, decayRate);
}
```
In `ApplyMove`, add the launch term and decay it:
```csharp
_controller.Move(_moveDirection.normalized * (_speed * dt)
               + _launchVelocity * dt
               + new Vector3(0f, _verticalVelocity, 0f) * dt);
_launchVelocity = Vector3.MoveTowards(_launchVelocity, Vector3.zero, _launchDecayRate * dt);
```
`PlayerController` forwards the new `IPlayerMotor` members to `_motor`.

**Test:** ordinary jump, run, sprint, fall feel **identical** (launch is zero unless a climb
sets it). Confirm a manual `AddLaunchVelocity` call produces an arcing push that decays.

> ⚠️ `PlayerController.SetVelocity()` is a red herring — it sets a `Velocity` property the motor
> never reads. Do **not** use it for jump-off; use `IPlayerMotor.SetVerticalVelocity` /
> `AddLaunchVelocity`.

### 1.2 `PlayerCameraRig` — (no change needed)
`SetFrozen(bool)` is already public and re-syncs yaw/pitch on unfreeze. Climbing calls it.

### 1.3 `PlayerStamina` — climbing drain
`PlayerStamina` already has a TODO block (≈L199–205) anticipating `ClimbDrainRate`. `IStaminaData`
is HUD-only (no `IsFatigued`), so climbing resolves the **concrete** `PlayerStamina`.

Add:
```csharp
[Header("Climbing")]
[Tooltip("Stamina drained per second while climbing and moving between holds. ~25/s")]
public float ClimbDrainRate = 25f;

private bool _isClimbing;
private bool _isMovingBetweenHolds;

/// Called by the climbing subsystem each frame while climbing.
public void SetClimbState(bool climbing, bool movingBetweenHolds)
{
    _isClimbing = climbing;
    _isMovingBetweenHolds = movingBetweenHolds;
}
```
In `Tick`: while `_isClimbing`, **suppress recovery** and drain `ClimbDrainRate * dt` only when
`_isMovingBetweenHolds`. Keep it isolated from the sprint path so non-climb behaviour is
unchanged. Fatigue (`IsFatigued`) reuses the existing enter-at-0 logic and drives auto-tumble
(§5).

**Test:** sprint/thirst/hunger/rest behaviour unchanged when not climbing.

### 1.4 Input — no controller change; climbing subscribes directly
`InputHandler` stays locomotion-only. Climbing reads `Move`/`Look` from `InputHandler` and
subscribes **directly** to the `Player` map's `Jump` (`performed` + `canceled`, for hold/charge
timing) and `Use` actions off `PlayerInput` — the same pattern `CycleItems` uses for item
cycling. `InputHandler.JumpPressed` is press-buffered only and insufficient for charge.

### 1.5 `Game.Tools` — gate tools on external control (decision)
Tools are currently not inert under external control (you could aim/throw mid-climb, and
`AimManager.OnAnimatorIK` would fight FinalIK). Small, general fix benefiting all takeovers:

- `AimManager`: resolve `IControlLock` (parent); in `OnAimPerformed`, early-return while
  `IsExternalControlActive`; call `ForceAimExit()` if control is taken mid-aim.
- `CycleItems`: pause cycling while `IControlLock.IsExternalControlActive` (it already has
  `LockCycling`; drive it from external-control state or have it check the flag).

**Test:** during a hookshot drag (existing external control), aim and item-cycle are inert;
normal play unaffected.

### 1.6 `Game.Core` — Flora handoff contract
For `TrunkGenerator` (in `Game.Powers`) to feed holds to climbing without a `Powers ↔ Climbing`
cycle, define the contract in `Game.Core` (dependency-free, referenced by both):
```csharp
namespace Game.Core.Climbing
{
    public struct ClimbHoldData {            // local-space, parent applied at runtime
        public Vector3    LocalPosition;
        public Quaternion LocalRotation;     // up = surface up, forward = outward normal
        public float      RiskValue;         // fallbackRisk for trunks
        public byte       IconId;            // 0 = none/fallback for trunks
    }
    public interface IClimbableMeshConsumer {
        void ReceiveHolds(System.Collections.Generic.IReadOnlyList<ClimbHoldData> holds);
    }
}
```

**Gate for Phase 1:** all of Phase 0 compiles, Console clean, existing controller/tools/stamina
behaviour confirmed identical.

---

## 2. Phase 1 — Core Climbing (authored surfaces + trunks)

### 2.1 `ClimbableSurface` (component on each climbable)
Holds per-surface config + the runtime hold set. Implements
`Game.Core.Climbing.IClimbableMeshConsumer` (for trunk handoff).
```csharp
[SerializeField] HoldDataSO holdData;        // auto-assigned at bake (§6)
[SerializeField] float fallbackRisk = 0.05f; // used when no vertex paint
[SerializeField] bool  alwaysDry = false;    // overrides global wet (Phase 6 weather)
[SerializeField] float redRisk, greenRisk, blueRisk;   // per-channel risk (Phase 4)
[SerializeField] float searchRadius = 12f;   // hold streaming radius
```
- References its `MeshCollider` (low-poly, runtime casts) — **not** the bake mesh.
- Static/authored surfaces stream holds from `holdData` via `HoldStreamer`.
- Trunks/small surfaces (≤ a few hundred holds) parent holds directly, skip streaming.

### 2.2 `HoldStreamer` (global pool)
Port of the article's `PruneHoldTransforms`:
- Global pool of ~2500 pre-instantiated hold `Transform`s, shared across **all** surfaces.
- Coroutine paged at ~5000 checks/frame; holds within `searchRadius` are dequeued, positioned
  (`parent.TransformPoint(localPos)`), rotated, parented; holds outside are returned to the pool.
- Moving/kinematic surfaces parent holds to the surface so they follow automatically.

### 2.3 `EffectorRig` (5-effector IK driver)
Effectors `RIGHT_HAND, LEFT_HAND, RIGHT_FOOT, LEFT_FOOT, ROOT`; states `LAST=0, CURRENT=1,
NEXT=2`. NEXT transforms are parented to hold transforms (follow moving surfaces); LAST/CURRENT
are unparented world transforms. Per-frame interpolation (article Snippet 1):
- advance `afterTime`; apply per-effector `delay`/`lag`; `EaseLerp(LAST, NEXT, t)` (custom
  ease-in/out via serialized `AnimationCurve`); add `GetOffsetVectorForEffector`; slerp rotation;
  `ikRotationWeight = (1 - sin(t·π)) · modeWeight`; foot "stretch" during jump.
- writes targets to **FinalIK** `FullBodyBipedIK` effectors.
- **FinalIK weight fade:** entry 0→1 over `ikFadeInDuration`; exit 1→0 over `ikFadeOutDuration`;
  ragdoll transition zeroes weights over 2 frames before enabling rigidbodies.

### 2.4 Root derivation (article Snippet 2)
```
handAvg = (LEFT_HAND.CURRENT + RIGHT_HAND.CURRENT) / 2
BRACED: root = handAvg - forward*0.5 - up*(1.3 - handDiff/4)
FREE  : root = handAvg - forward*0.1 - up*2.05
```
Then add `OscillatorBank` linear offsets to `transform.position`, then apply rotational pendulum
springs via `transform.RotateAround(pivot, axis, angle)` where `pivot = (RH.NEXT + LH.NEXT)/2`.

### 2.5 Foot placement (BRACED)
Each frame: SphereCast from expected foot pos toward the wall; on hit, place the foot NEXT at the
hit point. No baked foot holds. BRACED↔FREE is chosen per-frame by whether foot casts find surface.

### 2.6 `OscillatorBank` (damped harmonic, FixedUpdate)
Dictionary of named oscillators `{ value, velocity, angularFrequency, dampingRatio, direction }`,
each returning a float offset applied as `offset · direction`. Named springs: `wallImpact`,
`sideSway`, `jumpWindUp`, `jumpStretch`, `landingCompression`. (Pendulum sim runs in `FixedUpdate`
— a justified physics exception to the "movement in Update" rule; it integrates a spring, not
locomotion.)

### 2.7 `CharacterPendulum` (FREE hang, LateUpdate)
Two-mass pendulum (M1 ≈ hips, M2 ≈ upper chest — see `Free-Annotated.png`):
```csharp
root.position = Lerp(root.position, mass1.position, w);
root.rotation = Slerp(root.rotation, mass1.rotation, w);
spine.rotation= Slerp(spine.rotation, mass2.rotation, w/2);
// w interpolates to 0 on exit
```
Instantiated at runtime, parented to the climbable so surface motion propagates.

### 2.8 `ClimbController` (the subsystem brain)
Resolves the interfaces in §0.4. Owns climb state flags (`isClimbing`, `isRagdoll`, `isSliding`,
`isTumbling`, `isCharging`, `isMovingBetweenHolds`, `regrabCooldownTimer`), grab detection, hold
selection, and the entry/exit handshake. Drives `EffectorRig`, `OscillatorBank`, free-look, and
each frame calls `PlayerStamina.SetClimbState(isClimbing, isMovingBetweenHolds)`.

**Continuous grab detection** (while `!isClimbing && !isRagdoll && regrabCooldownTimer<=0`):
`OverlapSphere` filtered to `ClimbableSurface`; reject by facing angle, surface angle (no
floors/ceilings), and `IsExternalControlActive`; best candidate → `candidateHold`.

**Hold selection:** `OverlapSphere` → reject if character would intersect geometry at candidate
→ line-of-sight raycast from LAST effector → sort by input-direction alignment + distance from
ideal reach + rotational variation → top candidate = NEXT.

**Entry** (Use pressed + `candidateHold != null` + not aiming):
1. `RequestExternalControl()` (FSM → ExternalControl; locomotion zeroed, look frozen).
2. `cameraRig.SetFrozen(false)` (free-look).
3. FinalIK weights fade in; set NEXT targets to `candidateHold` + nearest adjacent holds.
4. Phase 1: **snap-grab** (CURRENT = NEXT). Slide replaces this in Phase 5/§5.
5. `// CLIMBING_HOOK: sprint-grab damage` (stub) if entering from high speed.

**Exit — drop** (Jump tapped < `minJumpHoldTime`): fade IK out;
`motor.SetVerticalVelocity(0)`; `ReleaseExternalControl()`; `regrabCooldownTimer = regrabCooldown`.

**Exit — reach top (mantle, automatic):** when hand effectors near a surface top, raycast up
from both hands for clear space + `Physics.CheckCapsule(cc.radius, cc.height)` for standing room;
play `ClimbUp`; animation event `OnMantleComplete` → fade IK out, `SetVerticalVelocity(0)`,
`ReleaseExternalControl()`.

**Exit — reach bottom:** foot SphereCast finds ground within threshold → release with zero velocity.

### 2.9 Flora trunks — emit from ring data (first-class)
Trunks are smooth tubes — the ledge parser finds ~no holds, so trunks **do not parse**; they
emit holds from `TrunkGenerator`'s ring lattice.

- New `TrunkClimbableEmitter` (`Game.Climbing/Flora`) on the trunk prefab; it carries a
  `ClimbableSurface` and implements `IClimbableMeshConsumer`.
- **Minimal edit to `TrunkGenerator.cs`** (Game.Powers): in the `AnimateGrowth` finalize block,
  **after** the `MeshCollider` is added (≈L530) and **before** `Destroy(this)` (≈L542), build
  `ClimbHoldData[]` from the ring lattice and call
  `GetComponent<IClimbableMeshConsumer>()?.ReceiveHolds(holds)`:
  - position = `segmentRings[r][i]` (→ local), up = segment direction, forward = radial
    `(vert − ringCenter).normalized`, `RiskValue = fallbackRisk`, `IconId = 0`.
  - **Skip rings** whose radius `< minClimbableRingRadius` (exposed, **default 0.25f**) so the
    twiggy taper isn't grabbable.
- Trunk hold count (~80) → parent directly, skip streaming. Mid-growth (deforming) is not
  climbable; only the finalized mesh is.

### 2.10 Animator
New override layer `ClimbingLayer` at **full weight (1.0)** — required because
`Motor.SuspendLocomotion()` zeros only Speed/MotionSpeed, leaving stale Grounded/Jump/FreeFall on
the base layer (which the full-override climb layer must mask). New bool `Climbing`. States:
`BracedClimb`, `FreeHang` (single-keyframe poses), `ClimbUp`, `ClimbJump`, `ClimbFall`. No
existing states/params/transitions modified.

**Gate:** grab → braced/free hang on authored cliffs **and** a finished Flora trunk; traverse via
input; drop/mantle/reach-bottom return cleanly to the FSM; tools inert; free-look works.

---

## 3. Phase 2 — Ragdoll Fall

IK→ragdoll: zero FinalIK weights over 2 frames → enable pre-configured ragdoll rigidbodies →
seed root rigidbody with `IPlayerMotor.Controller.velocity` → `isRagdoll = true` (blocks grab,
suppresses input) → `ReleaseExternalControl()`.

Recovery (FixedUpdate while `isRagdoll`): root velocity `< settleVelocityThreshold` for
`settleTimeThreshold` seconds AND `HasHealthRemaining()` (stub → true) → play get-up → disable
ragdoll rigidbodies → set transform to ragdoll settle pose → release with zero velocity.

Fall damage: on first significant ragdoll `OnCollisionEnter`,
`equivalentFallDistance = relVel² / (2·9.81)` → `// CLIMBING_HOOK: ApplyFallDamage(...)` (Phase 6).
Temporary serialized `debugRagdollKey` for isolated testing (removed in Phase 5).

---

## 4. Phase 3 — Hold Icons & Vertex-Painted Risk

### 4.1 Vertex paint → risk + icon id (bake-time)
`HandholdParser.GetHoldRisk` reads vertex colors from the **bake mesh** (§6) — per-instance via
`additionalVertexStreams.colors`, falling back to `sharedMesh.colors`, then `fallbackRisk`. For
each hold, store **two** baked values:
- `RiskValue` = `avgR·redRisk + avgG·greenRisk + avgB·blueRisk` (drives the tumble roll, §5).
- `IconId` = `argmax(avgR, avgG, avgB)` → maps R→icon C, G→icon B, B→icon A (which PNG to show).

### 4.2 `HoldIconPool` (pooled billboards — **not** UI Toolkit)
Per-hold world-space markers, active **only while climbing** that surface, faded by a
`visualizationRadius`. UI Toolkit per-icon is too heavy at this count, so:
- Pool of billboarded quads (100–300), rendered via a 3-cell **atlas (one material, per-instance
  UV offset)** or 3 `DrawMeshInstanced` batches — ≤ a few draw calls, **no tint** (the PNG itself
  carries the colour meaning; `IconId` selects the cell).
- Piggyback on `HoldStreamer`'s nearby-hold set. Target alpha =
  `InverseLerp(outerRadius, innerRadius, dist)`. Dequeue an icon when a hold enters the radius
  (alpha 0→up); enqueue back when alpha hits ~0. Alpha via `MaterialPropertyBlock`, no per-frame
  alloc, assignment scan throttled.

### 4.3 Grab marker & charge bar (singular HUD — UI Toolkit OK)
The single-instance elements **may** use UI Toolkit world-space (`Game.UI`, reading
`IClimbUIState` from `ClimbController`): a grab-candidate marker (shown when
`candidateHold != null && !isClimbing`) at the hold position, and a jump-charge fill bar. These
are one-per-player, so UI Toolkit cost is fine.

---

## 5. Phase 4 — Slide, Tumble, Charged Jump-off, Stamina

### 5.1 Slide (replaces Phase 1 snap-grab on air-grabs)
`incomingVelocity = |IPlayerMotor.Controller.velocity.y|` sampled at grab;
`slideDistance = defaultSlideLength · (incomingVelocity / referenceVelocity)`. SphereCast down
`shoulderWidth` for obstruction → `slideEndTarget`. Below `minSlideDistance` → snap-grab. During
slide: input suppressed, effectors travel via `slideEaseCurve`; if no valid hold at the current
slide point (gap/edge) → `ReleaseExternalControl(currentVelocity)` → FSM falls to Jump.

### 5.2 Charged jump-off (decision: **horizontal launch**)
While Jump held ≥ `minJumpHoldTime`: `chargeNormalized = chargeTime / maxChargeTime`; `jumpWindUp`
oscillator; **`ClimbCameraReframe`** drives `CinemachineThirdPersonFollow` (height/dist/side) over
`jumpChargeCamTransitionDuration` (climbing owns this, no AimManager); charge bar fills; cancel on
Use. On release:
```csharp
float v   = Mathf.Lerp(minPushVertical,   maxPushVertical,   chargeNormalized);
Vector3 h = awayFromWallDir * Mathf.Lerp(minPushHorizontal, maxPushHorizontal, chargeNormalized);
motor.SetVerticalVelocity(v);
motor.AddLaunchVelocity(h, launchDecayRate);
ReleaseExternalControl();   // FSM → Jump; arc decays into normal air control
```
Play `ClimbJump`; fade IK out; reframe camera back to defaults.

### 5.3 Tumble (QTE)
Trigger = probability roll on each new hold (`Random.value < finalRisk`, §5.5) — Phase 4 debug
toggle first, then the roll. Flow: downward slide of `tumbleSlideLength`; QTE prompt; input
suppressed except `Use`. **Success** (Use before slide end): hands lock (NEXT = CURRENT),
`landingCompression` oscillator, resume climb. **Failure**: → ragdoll (§3). **Auto-tumble**
(`PlayerStamina.IsFatigued` while climbing): no QTE, full slide → ragdoll.

### 5.4 Stamina wiring
`isMovingBetweenHolds = any effector has ikEventTime > 0`. Each frame
`PlayerStamina.SetClimbState(isClimbing, isMovingBetweenHolds)`: drains `ClimbDrainRate` while
moving, no drain while still, no recovery while climbing. Fatigue → auto-tumble.

### 5.5 Risk formula (per new hold)
```csharp
float rainMultiplier   = 1f; // CLIMBING_HOOK: weather (Phase 6) — bool wet = isRaining && !alwaysDry
float staminaMultiplier = Mathf.Lerp(emptyStaminaMultiplier, fullStaminaMultiplier,
                                     stamina.NormalizedStamina); // 0-1, clamped to EffectiveMax
float finalRisk = hold.RiskValue * rainMultiplier * staminaMultiplier;
```
Hunger/rest penalties lower `NormalizedStamina`'s ceiling → degraded survival passively raises
climbing risk (intentional).

---

## 6. Climbable Bake Pipeline (decoupled parse vs collider)

**Per authored rock/cliff prefab:**
- **Visual mesh** — rendered, detailed, unpainted.
- **Low-poly `MeshCollider`** — runtime casts only (foot SphereCasts, hand validation, LoS).
  Concave → non-convex, on static/kinematic bodies. Poly count driven only by "do casts land
  believably," never by icon density. (Fallback for precise overhangs: a second pre-cooked
  collider toggled via `.enabled` on climb enter/exit — avoid `sharedMesh` LOD swaps, they re-cook.)
- **High-poly painted bake child**, tagged **`EditorOnly`** (auto-stripped from builds — no
  runtime mesh, no runtime removal logic). Painted **per-instance** with **Polybrush** into
  `meshRenderer.additionalVertexStreams` (so one shared base mesh can carry different paint per
  mountain). Parser reads geometry from `sharedMesh`, colours from `additionalVertexStreams`
  (same vertex indices).

**Bake (editor tool, one click — `ClimbBakeWindow`):**
1. Run in the **assembled scene** (so neighbour culling sees other pieces).
2. For each `ClimbableSurface`: parse its bake child (`CrawlMeshForHandholds` — adjacency via a
   vertex/edge **hash, O(n)**; face normals via cross product, not smoothed mesh normals;
   convex-ledge accept / concave-corner reject by the up-vs-outward normal + centroid-height test
   — see `C1.png`/`C2.png`). Edges subdivided at `MIN_HH_DIST`.
3. **Cull buried holds:** `CheckSphere(hold.pos + normal·handClearance, r, otherPiecesMask)` —
   holds inside neighbouring solids are dropped, never serialized (no icons, no budget, **no
   runtime per-frame filtering**).
4. Store `RiskValue` + `IconId` per hold; **auto-create/overwrite** a **per-piece** `HoldDataSO`
   (`CreateInstance` → `CreateAsset` → assign ref). Idempotent on re-bake. One pass → N assets.

Runtime cost is independent of bake-mesh poly and of how many surfaces exist (global ~2500 pool).

---

## 7. Phase 5 — Cleanup & Phase 6 — Future Hooks

- **Phase 5:** remove `debugRagdollKey` and the tumble debug toggle (replaced by the roll);
  confirm tumble/ragdoll paths before enabling the probability roll.
- **Phase 6 (`// CLIMBING_HOOK`):** weather rain multiplier (uncomment; `alwaysDry` already
  present; source `isRaining` from the future `Game.Systems` weather service); fall damage +
  sprint-grab damage (wire to the health interface when it exists).

---

## 8. Final Step — Author `CLIMB_SURFACE_CREATION_SETUP.md`

Write a standalone, step-by-step guide for turning a **new `.fbx`** into a climbable surface, for
the dev to follow without re-deriving the pipeline. It must cover:
1. Import the `.fbx`; set up the **visual mesh** (material, rendering).
2. Create/assign the **low-poly `MeshCollider`** (how low; silhouette-match guidance; enable
   Read/Write only if baking at runtime).
3. Add the **high-poly painted bake child**, tag it **`EditorOnly`**.
4. **Polybrush** workflow: paint per-instance vertex colours into `additionalVertexStreams`
   (red/green/blue = risk channels → icons C/B/A); how to verify colours land in the stream, not
   the shared asset; how to apply/save if needed.
5. Add the **`ClimbableSurface`** component; set `fallbackRisk`, per-channel risks, `searchRadius`.
6. **Bake:** open `ClimbBakeWindow`, bake **in the assembled scene** (for neighbour culling),
   confirm the per-piece `HoldDataSO` is created and assigned.
7. Verify in play mode: holds stream in, grab works, icons show the right PNG, risk feels right.
8. **Flora exception:** trunks need none of this — they auto-emit on growth-complete; only note
   `minClimbableRingRadius` tuning.

---

## Open / Deferred
- **Open-world scaling** (how many climbables at once; streaming distant mountains / collider &
  SO load) — answered **after** implementation against real numbers. Directionally: load
  per-piece `HoldDataSO`s with additive scene/world-cell chunks; distant mountains render as
  collider-less LODs/imposters; `ClimbableSurface` activates within a load radius. Live hold cost
  is already bounded by the global pool — the lever is collider memory + SO load.
- Runtime mesh-deforming climbables — out of scope.

*End of brief.*
