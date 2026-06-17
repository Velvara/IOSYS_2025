# Procedural Climbing System — Implementation Brief
## For Claude: Full Context, No Prior Conversation Needed

---

## Context Files to Upload Alongside This Document

The following scripts must be uploaded in the same conversation as this brief.
Claude should read them directly rather than assuming their contents,
as project-specific modifications may differ from Starter Assets defaults:

1. `ThirdPersonController.cs`
2. `PlayerStamina.cs`
3. `StarterAssetsInputs.cs`
4. `CharacterStateManager.cs`
5. `AimManager.cs`
6. `AimModeBase.cs`
7. `ThrowAim.cs`
8. `Stamina_System_Summary.md`

---

## Project Environment

- **Unity version:** 6000.3.7f1
- **Input system:** Unity New Input System (`PlayerInput`, `InputAction`)
- **Base controller:** Unity Starter Assets Third Person Controller
- **IK system:** FinalIK (purchased, available in project)
- **UI system:** UI Toolkit (Unity 6.3 world-space support)
- **Assembly:** All custom scripts share the same assembly as Starter Assets scripts
- **Script locations:**
  - `Assets/Imported/Starter Assets/Runtime/ThirdPersonController/Scripts/` — ThirdPersonController, PlayerStamina, IStaminaData, StarterAssetsInputs
  - Climbing scripts will live here too unless otherwise directed

---

## Existing Scripts — Integration Reference

These scripts are uploaded as context files. Read them directly.
The integration points listed below describe what the climbing system needs from each —
verify exact property/method names against the uploaded files before writing code.

### `ThirdPersonController.cs`
- `IsExternalControlActive { get; set; }` — when `true`, `Update()` returns immediately before any movement logic runs (gravity, grounding, stamina tick, move, camera). `LateUpdate()` camera rotation also suppressed. This is the primary handoff mechanism for climbing.
- `Controller` — public property exposing the `CharacterController` component
- `VerticalVelocity` — **to be added in Phase 0** as `public float VerticalVelocity => _verticalVelocity;`
- `FreezeCharacter(bool freezeMovement, bool freezeCamera)` — zeroes speed and vertical velocity when freeze is true
- `RotateOnMove` — public bool, set to false during aim modes
- `IsFatigued`, `IsSprinting`, `IsStealth` — public read-only state
- `cameraFrozen` — public bool
- `CinemachineCameraTarget` — public GameObject
- **Phase 0 additions:**
  - Remove `_hasAnimator = TryGetComponent(out _animator)` from `Update()` — redundant, already cached in `Start()`
  - Add `public float VerticalVelocity => _verticalVelocity;`
  - Add `public bool IsClimbing { get; set; } = false;`
  - Add method `HandOffToClimbing()`: zeroes `_verticalVelocity` and `_speed`, sets animator Speed/MotionSpeed to 0 and Jump/FreeFall to false, sets `IsExternalControlActive = true`
  - Add method `ReturnFromClimbing(float exitVerticalVelocity = 0f)`: sets `_verticalVelocity` to parameter, resets `_jumpTimeoutDelta = JumpTimeout` and `_fallTimeoutDelta = FallTimeout`, sets `IsExternalControlActive = false`

### `PlayerStamina.cs` / `IStaminaData`
- Read the uploaded file directly for exact property names and method signatures.
- The climbing system needs: current normalized stamina (0–1), fatigue state bool, and the ability to pass a climbing state into the tick method.
- `NormalizedStamina` (0–1, from `IStaminaData`) is used in the climbing risk formula. Note: this value is clamped to `EffectiveMax`, meaning hunger/rest penalties reduce its ceiling below 1.0. This is intentional — penalties passively increase climbing risk.
- **Phase 1a addition to `PlayerStamina`:** `[SerializeField] public float ClimbingDrainRate` — drain per second while climbing and moving between holds.
- **Phase 1a modification:** `Tick()` signature extended to accept a climbing bool parameter. Read the current signature from the uploaded file before modifying.
- Stamina behaviour during climbing:
  - Drains at `ClimbingDrainRate` per second while moving between holds
  - Does not drain while holding still
  - Does not recover while climbing (moving or still)
  - Fatigue while climbing triggers auto-tumble in `ClimbingSystem` (Phase 3)

### `StarterAssetsInputs.cs`
- Read the uploaded file directly for exact method names.
- The climbing system reads Move input direction each frame during climbing.
- Input suppression during slides and tumble is handled by ignoring input reads in `ClimbingSystem`, not by modifying `StarterAssetsInputs`.

### `CharacterStateManager.cs`
- `LockCharacter()` — disables `ThirdPersonController` component entirely. **Do not use for climbing** — disabling the component blocks access to `CharacterController` via the `Controller` property.
- `UnlockCharacter()` — re-enables controller, restores input state.
- Climbing uses `IsExternalControlActive` directly, not `LockCharacter()`.
- On climbing entry: call `AimManager.Instance.ForceAimExit()` as safety. Set a flag that blocks aim re-entry while `isClimbing` is true.

### `AimManager.cs`
- Singleton: `AimManager.Instance`
- `IsAiming` — bool, must be false to allow climbing entry
- `ForceAimExit()` — cleanly exits any active aim mode, resets camera to defaults
- `RequestCameraTransition(float targetHeight, float targetDist, float targetSide, float? duration = null)` — smoothly transitions `CinemachineThirdPersonFollow` via coroutine using `SmoothStep`. Duration defaults to `cameraTransitionDuration` (serialized on AimManager — designed for hookshot, likely slow). Always pass an explicit duration override from the climbing system.
- Default camera values: `GetDefaultCamHeight()`, `GetDefaultCamDist()`, `GetDefaultCamSide()`
- Camera transition modifies: `ShoulderOffset.y` (height), `CameraDistance` (dist), `CameraSide` (side)
- **No camera orientation lock is applied during climbing** — only the three values above change.

### `AimModeBase.cs`
- Defines camera field pattern: `camHeight`, `camDist`, `camSide` — all public serialized floats.
- Jump charge camera in climbing system follows this exact same pattern — three serialized floats plus its own transition duration.

---

## Architecture Overview

The climbing system does **not** use a formal state machine class hierarchy. It uses `IsExternalControlActive` on `ThirdPersonController` as the handoff mechanism. The `ClimbingSystem` MonoBehaviour runs its own `Update()`, `FixedUpdate()`, and `LateUpdate()` loops independently, and is responsible for setting `IsExternalControlActive` on entry and clearing it on exit.

**Script Execution Order — must be set explicitly in Unity Project Settings:**
1. `PlayerStamina`
2. `ThirdPersonController`
3. `ClimbingSystem`
4. `CharacterPendulum`
5. FinalIK `FullBodyBipedIK`

---

## Phase 0 — Controller Preparation

**Scope:** Isolated changes to `ThirdPersonController.cs` only.
Test that all existing behaviour is identical before proceeding to Phase 1.

**Changes:**
- Remove `_hasAnimator = TryGetComponent(out _animator)` from `Update()` — redundant, already cached in `Start()`
- Add `public float VerticalVelocity => _verticalVelocity;`
- Add `public bool IsClimbing { get; set; } = false;`
- Add `HandOffToClimbing()` method
- Add `ReturnFromClimbing(float exitVerticalVelocity = 0f)` method

**Test:** Confirm movement, sprinting, stamina, stealth, camera, and jumping all behave identically to before.

---

## Phase 1a — Core Climbing System

### New Script: `ClimbableSurface.cs`

Component placed on any climbable GameObject.

```csharp
[SerializeField] float fallbackRisk        // used if mesh.colors is empty
[SerializeField] bool AlwaysDry            // overrides global rain wet state (for indoor surfaces)
[SerializeField] float redRisk             // tumble risk value for red-painted vertices (Phase 4 active, Phase 1a stub)
[SerializeField] float greenRisk           // tumble risk value for green-painted vertices
[SerializeField] float blueRisk            // tumble risk value for blue-painted vertices
```

- Holds reference to its `MeshFilter` (static/moving objects) or `SkinnedMeshRenderer` (hard-surface-on-bone only — not deforming skins)
- Hard-surface-on-bone: a rigid mesh parented to or weighted 100% to a single bone, treated identically to a moving object. Uses `sharedMesh` for parsing. Hold Transforms parented to the bone Transform directly.
- `HandholdParser` reads this component to access the mesh.

---

### New Script: `HandholdParser.cs`

Editor tool and runtime component on the same GameObject as `ClimbableSurface`.

**Mesh parsing algorithm (`CrawlMeshForHandholds()`):**
- Iterates all triangles in mesh, builds list of adjacent triangle pairs (pairs sharing 2 vertices — found by matching vertex indices for welded meshes, or fuzzy position comparison for unwelded meshes)
- For each pair: computes surface normal via cross product of edge vectors — **not** mesh normals, which are smoothed and corrupt edge detection
- Determines which triangle is "up-facing" and which is "outward-facing" via `Vector3.Dot(Vector3.up, normal)`
- Rejects inside corners (concave edges): if centroid Y of the outward-facing triangle is **above** centroid Y of the up-facing triangle, it is an inside corner — skip
- Accepts outside corners (convex edges, climbable ledges): outward-facing triangle centroid is **below** up-facing triangle centroid
- Angle thresholds (all serialized): `UP_THRESHOLD` (max angle from vertical for up-facing normal), `MIN_DIFF` and `MAX_DIFF` (angle range between the two normals)
- Calls `AddHandholds()` along the shared edge

**`AddHandholds(SharedEdge e, Vector3 upNormal, Vector3 forwardNormal)`:**
- Places hold GameObjects at each vertex of the shared edge and at evenly-spaced intervals between them (spacing = `MIN_HH_DIST`, serialized)
- Hold Transform orientation: up = upNormal, forward = forwardNormal (outward surface normal)
- Per hold, stores `riskValue` float:
  - **Phase 1a:** always returns `ClimbableSurface.fallbackRisk`
  - **Phase 4:** activates vertex color averaging (see Phase 4)
- Serializes hold data as list of structs: `{ Vector3 localPosition, Quaternion localRotation, Transform parent, float riskValue }`

**Runtime hold streaming (`PruneHoldTransforms()` coroutine):**
- Global queue of ~2500 pre-instantiated Transforms shared across all `ClimbableSurface` objects
- Each `ClimbableSurface` runs the coroutine when character enters `searchRadius` (serialized)
- Coroutine processes 5000 holds per frame maximum to avoid stalls
- Holds within radius: dequeued from pool, positioned/rotated/parented in correct local space
- Holds outside radius: returned to pool, Transform parent cleared
- Hard-surface-on-bone objects: skip streaming — use naively parented hold Transforms directly (small hold count, always active)
- Moving objects: hold Transforms parented to the climbable object — `TransformPoint()` handles world position automatically, no per-frame position update needed
- Hard-surface-on-bone: hold Transforms parented to the bone Transform — same automatic update

---

### New Script: `ClimbingSystem.cs`

Main climbing logic. Attached to the player GameObject or a child of it.

**Inspector references (assign or find in Awake):**
- `ThirdPersonController tpc`
- `CharacterController cc` — via `tpc.Controller`
- `PlayerStamina stamina`
- `AimManager aimManager` — via `AimManager.Instance`
- `Animator animator`
- `FullBodyBipedIK ik` — FinalIK component on player
- `Transform defaultCameraRoot` — child object of player named "DefaultCameraRoot"
- `Transform playerSpine` — bone reference for pendulum influence
- `CharacterPendulum pendulumRef` — prefab reference

---

#### 5-Effector System

Effector name constants: `RIGHT_HAND`, `LEFT_HAND`, `RIGHT_FOOT`, `LEFT_FOOT`, `ROOT`

Position state indices: `LAST = 0`, `CURRENT = 1`, `NEXT = 2`

```csharp
Dictionary<string, Transform[]> ikTargets       // [effector][LAST/CURRENT/NEXT]
Dictionary<string, float> ikEventTimes          // elapsed time since last effector move started
Dictionary<string, float> ikEffectorDelayTime   // per-effector start delay
Dictionary<string, float> ikEffectorLagTime     // per-effector lag modifier
Dictionary<string, float> ikRotationWeights     // per-effector IK rotation blend
```

All Transforms in `ikTargets` are instantiated at runtime.
NEXT Transforms are parented to hold Transforms — they automatically follow moving and animated surfaces.
LAST and CURRENT are unparented world-space Transforms.

---

#### `EaseLerp(Vector3 from, Vector3 to, float t)`

Custom lerp with ease-in and ease-out applied via serialized `AnimationCurve`.
`t` is normalized time 0–1 through the move.

---

#### Effector Update Loop (runs in `Update()`)

```
for each active effector:
    afterTime += Time.deltaTime
    adjustedTime = clamp with delay/lag offsets
    if reached destination:
        CURRENT = NEXT position
        LAST = NEXT position
        parent LAST to NEXT's parent
        reset ikEventTimes[effector] = 0
    else:
        CURRENT = EaseLerp(LAST, NEXT, adjustedTime / totalTime)
        CURRENT += GetOffsetVectorForEffector(effector, normalizedT - 0.5)
    rotation = Quaternion.Slerp(LAST.rotation, NEXT.rotation, t)
    ikRotationWeights[effector] = (1 - Sin(t * PI)) * modeWeight
    SetIKTarget(effector, CURRENT, newPosition, newRotation)
```

---

#### ROOT Position Derivation

```csharp
Vector3 handAvg = (ikTargets[LEFT_HAND][CURRENT].position
                 + ikTargets[RIGHT_HAND][CURRENT].position) / 2f;
float handDiff = Mathf.Abs(
    (ikTargets[LEFT_HAND][CURRENT].position
   - ikTargets[RIGHT_HAND][CURRENT].position).y);

// BRACED state:
rootPos = handAvg - (forward * 0.5f) - (Vector3.up * (1.3f - handDiff / 4f));

// FREE state:
rootPos = handAvg - (forward * 0.1f) - (Vector3.up * 2.05f);
```

---

#### Foot Placement (BRACED state only)

- Each frame: SphereCast from expected foot position (derived from hand position + offsets) toward the wall surface
- If hit: insert/move a Transform at hit point, use as NEXT for foot effectors
- No baked foot holds — feet find wall geometry dynamically each frame

---

#### Two Climb States

- `BRACED` — feet find wall via raycast. Wall provides push-back reference. Oscillators drive body sway.
- `FREE` — no foot contact. Legs dangle freely. Pendulum simulation drives body swing.
- State determined each frame by whether foot SphereCasts find a climbable surface.

---

#### Damped Harmonic Oscillators

```csharp
Dictionary<string, OscillatorState> oscillators
// OscillatorState: { float value, float velocity, float angularFrequency, float dampingRatio, Vector3 direction }
```

Updated in `FixedUpdate()`. Each oscillator returns a single float offset applied as `offset * direction`.

Named oscillators:
- `"wallImpact"` — fires on grab/land, drives into-wall compression
- `"sideSway"` — drives lateral body sway during traversal
- `"jumpWindUp"` — fires during charged jump build-up
- `"jumpStretch"` — fires during jump-off, extends limbs
- `"landingCompression"` — fires on tumble success, downward bounce on body

---

#### Body Position Assembly

Applied in `Update()` after effector positions are set, before pendulum runs in `LateUpdate()`:

```csharp
Vector3 offset = GetCharacterOffset(); // static offsets per climb state

foreach (string s in climbingSprings)
    offset += oscillators[s].value * oscillators[s].direction;

transform.position += offset;

// Pendulum rotation (both states, different springs active):
Vector3 pivot = (ikTargets[RIGHT_HAND][NEXT].position
               + ikTargets[LEFT_HAND][NEXT].position) / 2f;

foreach (string st in pendulumSprings)
{
    Vector3 axis = Vector3.Normalize(
        Vector3.Cross(oscillators[st].direction, Vector3.up));
    float angle = oscillators[st].value * (180f / Mathf.PI);
    transform.RotateAround(pivot, axis, angle);
}
```

---

#### Hold Selection Algorithm

1. `Physics.OverlapSphere(characterPosition, searchRadius)` filtered by `ClimbableSurface` component presence
2. Validation rejection: sphere overlap at candidate position checks if character would intersect geometry there
3. Line-of-sight raycast from LAST effector position to candidate
4. Sort by: alignment to input direction + distance from ideal reach distance + rotational variation
5. Top candidate selected as NEXT target
6. Adjacent `ClimbableSurface` objects allowed — radius search spans multiple surfaces naturally

---

#### Continuous Grab Detection

Runs every frame while `!isClimbing && !isRagdoll && regrabCooldownTimer <= 0`:
- Same OverlapSphere and sort as hold selection
- Additional filters:
  - Facing angle: `Vector3.Angle(transform.forward, -surfaceNormal) < maxGrabAngle` (serialized)
  - Surface angle: `Vector3.Angle(surfaceNormal, Vector3.up) > minSurfaceAngle` (serialized — prevents grabbing floors/ceilings)
  - `!AimManager.Instance.IsAiming`
- Result stored as `candidateHold` — drives UI marker and Use button handler
- Runs in both `GroundedState` and `AirborneState`

---

#### State Tracking

```csharp
bool isClimbing
bool isRagdoll
bool isSliding
bool isTumbling
bool isCharging              // jump charge in progress
bool isMovingBetweenHolds   // true while any effector has ikEventTimes[effector] > 0
float regrabCooldownTimer   // counts down after intentional drop before detection re-activates
```

---

#### FinalIK Weight Management

- **Entry:** lerp all `FullBodyBipedIK` effector weights 0 → 1 over `ikFadeInDuration` (serialized)
- **Exit:** lerp all weights 1 → 0 over `ikFadeOutDuration` (serialized)
- **Ragdoll transition:** weights zeroed over 2 frames before ragdoll Rigidbodies are enabled

---

### New Script: `CharacterPendulum.cs`

Two-mass pendulum for FREE hang state.

```csharp
public Transform mass1          // upper chest level
public Transform mass2          // hip level
public float pendulumWeight     // serialized, 0-1 influence on character
public Transform playerRoot     // character hips bone
public Transform playerSpine    // upper spine bone
public bool usePendulum
private float timeToExit, currentExitTime;
private bool exiting;
```

`LateUpdate()`:
```csharp
if (usePendulum)
{
    float modifiedWeight = exiting
        ? pendulumWeight * (1f - currentExitTime / timeToExit)
        : pendulumWeight;

    playerRoot.position = Vector3.Lerp(
        playerRoot.position, mass1.position, modifiedWeight);
    playerRoot.rotation = Quaternion.Slerp(
        playerRoot.rotation, mass1.rotation, modifiedWeight);
    playerSpine.rotation = Quaternion.Slerp(
        playerSpine.rotation, mass2.rotation, modifiedWeight / 2f);

    if (exiting)
    {
        currentExitTime += Time.deltaTime;
        if (currentExitTime >= timeToExit) StopPendulum();
    }
}
```

Pendulum is instantiated at runtime and parented to the climbable surface geometry.
Secondary motion from surface movement propagates to the character automatically via the parent Transform.

---

### Animator Changes

**No existing states, parameters, or transitions are modified.**

New additions only:

- New Animator Layer: `ClimbingLayer` — full override weight (1.0), blended in/out via `climbLayerWeight` float (not snapped)
- New bool parameter: `Climbing`
- New animation states in `ClimbingLayer`:
  - `BracedClimb` — single keyframe pose, feet on wall
  - `FreeHang` — single keyframe pose, legs dangling
  - `ClimbUp` — mantle animation (traditional, plays once)
  - `ClimbJump` — jump-off animation (traditional, plays once)
  - `ClimbFall` — fall transition animation
- `BracedClimb` ↔ `FreeHang` blend driven by whether foot SphereCasts find a surface each frame

---

### Entry Conditions and Logic

Checked each frame while `!isClimbing && !isRagdoll && regrabCooldownTimer <= 0`:

1. `!AimManager.Instance.IsAiming`
2. `candidateHold != null`
3. `Use` button pressed (New Input System action)

On valid entry:
1. If entering from sprint: `damage = cc.velocity.magnitude * sprintGrabDamagePerMeterPerSecond` — **stubbed**, tagged `// CLIMBING_SYSTEM_HOOK: ApplyDamage(sprintGrabDamage)`
2. `AimManager.Instance.ForceAimExit()` — safety call
3. `tpc.HandOffToClimbing()`
4. `isClimbing = true`, `tpc.IsClimbing = true`
5. FinalIK weights begin fade-in
6. Set initial NEXT targets to `candidateHold` and nearest adjacent holds
7. **Phase 1a: snap-grab** — CURRENT immediately equals NEXT, no slide. Phase 3 replaces this.
8. Overhead UI marker hides

---

### Exit Conditions and Logic

#### Drop (Jump pressed, held < `minJumpHoldTime`)

1. `isClimbing = false`, `tpc.IsClimbing = false`
2. FinalIK weights fade out
3. `tpc.ReturnFromClimbing(0f)`
4. `regrabCooldownTimer = regrabCooldown` (serialized)

---

#### Charged Jump-off (Jump held ≥ `minJumpHoldTime`, then released)

**While charging (Jump held):**
- `chargeTime += Time.deltaTime`
- `chargeNormalized = Mathf.Clamp01(chargeTime / maxChargeTime)`
- Camera transition called once on charge start:
  `AimManager.Instance.RequestCameraTransition(jumpChargeCamHeight, jumpChargeCamDist, jumpChargeCamSide, jumpChargeCamTransitionDuration)`
  — all four values serialized on `ClimbingSystem`. No orientation lock, no aim mode behaviour.
- `DefaultCameraRoot` Y rotation: smoothly lerps to `jumpChargeRootYRotation` (serialized) over `jumpChargeRootRotationDuration` (serialized) in `Update()`
- Overhead charge bar UI fills proportionally to `chargeNormalized`
- `"jumpWindUp"` oscillator fires
- **Cancel (Use pressed during charge):** camera returns via `RequestCameraTransition(defaults)`, `DefaultCameraRoot` rotation returns to 0, charge bar hides, return to normal climbing state

**On release:**
```csharp
float pushVelocity = Mathf.Lerp(minPushVelocity, maxPushVelocity, chargeNormalized);
```
1. `"jumpStretch"` oscillator fires
2. Play `ClimbJump` animation
3. `isClimbing = false`, `tpc.IsClimbing = false`
4. FinalIK weights fade out
5. `tpc.ReturnFromClimbing(pushVelocity)` — velocity directed away from wall
6. Camera and `DefaultCameraRoot` rotation return to defaults

---

#### Reach Top — Mantle (automatic)

Checked each frame while climbing, when hand effectors are near the top of a surface:

1. Raycast upward from both hand positions — finds open space above ledge
2. Capsule test above ledge: `Physics.CheckCapsule()` using `cc.radius` and `cc.height` read directly from the `CharacterController` component — ensures character has room to stand regardless of capsule size changes
3. If both conditions met: trigger mantle
4. All input suppressed until animation event fires
5. Play `ClimbUp` animation
6. Animation event `OnMantleComplete()` at end of animation:
   - `isClimbing = false`, `tpc.IsClimbing = false`
   - FinalIK weights fade out
   - `tpc.ReturnFromClimbing(0f)`
   - Input restored
   - Transition to `GroundedState`

---

#### Reach Bottom

- Each frame while climbing: SphereCast downward from foot positions toward ground layers
- If ground found within threshold: `isClimbing = false`, `tpc.IsClimbing = false`, FinalIK fade out, `tpc.ReturnFromClimbing(0f)`

---

### WorldSpace UI — Phase 1a

- **Grab candidate marker:** UI Toolkit world-space panel positioned above character's head. Shows when `candidateHold != null && !isClimbing && !isRagdoll`. Hides on climbing entry. Upgraded to hold-position marker in Phase 1b.
- **Jump charge bar:** UI Toolkit world-space panel above character's head. Reuses existing filling bar element. Fills proportionally to `chargeNormalized`. Hides when charge is cancelled or released.

Both panels use Unity 6.3 UI Toolkit world-space document. Setup required fresh — no existing world-space `UIDocument` in the project.

---

### PlayerStamina — Phase 1a Stub

- Extend `Tick()` to accept climbing bool — read current signature from uploaded file before modifying
- Add `[SerializeField] public float ClimbingDrainRate`
- `isMovingBetweenHolds` stubbed as always true in Phase 1a — fully wired in Phase 3
- No drain while `isClimbing && !isMovingBetweenHolds`
- No recovery while `isClimbing`
- Sprint cancelled on climbing entry: `tpc.IsSprinting` becomes false via `HandOffToClimbing()`

---

## Phase 1b — WorldSpace UI Upgrade

**Scope:** Move grab candidate marker from above character's head to world-space position on the target hold.

- New world-space `UIDocument` set up (assist user with this — first world-space UI in the project)
- Marker panel positioned at `candidateHold.position` in world space each frame
- All show/hide conditions unchanged from Phase 1a
- Jump charge bar remains above character's head — unchanged

---

## Phase 2 — Ragdoll Fall

### IK-to-Ragdoll Transition

1. All `FullBodyBipedIK` effector weights lerped to 0 over 2 frames — prevents visual pop
2. Ragdoll `Rigidbody` components enabled (pre-configured on character, disabled at start)
3. Ragdoll root `Rigidbody` velocity seeded with `cc.velocity` at release moment
4. `ragdollStartY = transform.position.y` stored
5. `isRagdoll = true` — blocks grab detection, suppresses all input, blocks climbing entry
6. `isClimbing = false`, `tpc.IsClimbing = false`

### Recovery Condition

Checked in `FixedUpdate()` while `isRagdoll`:

```csharp
bool velocitySettled = ragdollRootRigidbody.velocity.magnitude < settleVelocityThreshold;
bool timeSettled = timeInSettledState > settleTimeThreshold;
bool hasHealth = HasHealthRemaining(); // stubbed — returns true until health system built
                                       // CLIMBING_SYSTEM_HOOK: wire to health interface

if (velocitySettled && timeSettled && hasHealth)
    TriggerRecovery();
```

On recovery:
1. Play get-up animation
2. Ragdoll `Rigidbody` components disabled
3. `isRagdoll = false`
4. `tpc.ReturnFromClimbing(0f)`
5. Character position set to ragdoll's final world position
6. Transition to `GroundedState`

### Fall Damage

On first significant `OnCollisionEnter` on ragdoll root Rigidbody:

```csharp
float impactSpeed = collision.relativeVelocity.magnitude;
float equivalentFallDistance = (impactSpeed * impactSpeed) / (2f * 9.81f);
// CLIMBING_SYSTEM_HOOK: ApplyFallDamage(equivalentFallDistance)
// Wire to health interface in Phase 5
```

`// CLIMBING_SYSTEM_HOOK` tag is used throughout the codebase for all stubbed future connections.
Search for this tag when wiring Phase 5.

### Debug Trigger

Temporary: serialized `KeyCode debugRagdollKey` in `ClimbingSystem` Inspector — triggers ragdoll from any state for isolated testing. **Removed in Phase 3.**

---

## Phase 3 — Slide, Tumble, Stamina Integration

### Slide (Replaces Phase 1a Snap-Grab)

On grab from `AirborneState`:

```csharp
float incomingVelocity = Mathf.Abs(tpc.VerticalVelocity); // sampled at grab moment
float slideDistance = defaultSlideLength * (incomingVelocity / referenceVelocity);
// If velocity near 0 (jump apex): slideDistance ≈ 0, snap-grab applies
```

Slide endpoint:
```csharp
Vector3 slideEndTarget = holdPosition - (Vector3.up * slideDistance);

if (Physics.SphereCast(holdPosition, shoulderWidth, Vector3.down,
    out RaycastHit hit, slideDistance, climbableLayers))
{
    if (hit.distance < minSlideDistance)
        // collapse to snap-grab
    else
        slideEndTarget = hit.point + (Vector3.up * standingHeightOffset);
}
```

During slide:
- All input suppressed
- Effectors travel to `slideEndTarget` via `EaseLerp` with `slideEaseCurve` (fast-start/slow-end, serialized separately from normal traversal curve)
- Each frame: check if valid hold exists at current slide position — if not (gap, edge of mesh, cave entrance): transition to `AirborneState` immediately
- At `slideEndTarget`: effectors re-lock to nearest valid holds at that position

Grounded grab or grab at jump apex (velocity ≈ 0): snap-grab, no slide.

### Tumble State

**Phase 3 trigger:** debug inspector toggle for isolated testing. Phase 4 replaces with probability roll.

**Flow:**
1. `isTumbling = true`
2. Downward slide begins — length = `tumbleSlideLength` (serialized, separate from normal slide length)
3. QTE prompt appears on overhead UI immediately on trigger
4. All input suppressed except `Use` button
5. Each frame: check valid hold exists below — if not, fall at that point regardless of QTE state

**Success — Use pressed before slide end:**
1. NEXT = CURRENT immediately — hands lock at current position
2. `"landingCompression"` oscillator fires — downward body bounce for sense of impetus
3. `isTumbling = false`
4. QTE UI hides
5. Return to normal climbing state

**Failure — slide completes without input:**
1. `isTumbling = false`
2. Transition to ragdoll via Phase 2 system
3. Debug ragdoll trigger from Phase 2 removed

**Auto-tumble (stamina reaches 0 while climbing):**
- QTE prompt never appears
- Slide plays at full `tumbleSlideLength`
- Straight to ragdoll failure path on slide end

### Stamina Full Wiring

- `isMovingBetweenHolds`: `true` while any `ikEventTimes[effector] > 0`
- Drain: `ClimbingDrainRate * Time.deltaTime` while `isClimbing && isMovingBetweenHolds`
- No drain while `isClimbing && !isMovingBetweenHolds`
- No recovery while `isClimbing` (either state)
- Fatigue while climbing: `IsFatigued` becomes true → call `TriggerAutoTumble()` in `ClimbingSystem`

---

## Phase 4 — Vertex Painted Risk Zones

### Vertex Color Reading

Activated in `HandholdParser.GetHoldRisk()`:

```csharp
private float GetHoldRisk(Mesh mesh, int triIndex, ClimbableSurface surface)
{
    if (mesh.colors == null || mesh.colors.Length == 0)
        return surface.fallbackRisk;

    Color c0 = mesh.colors[mesh.triangles[triIndex]];
    Color c1 = mesh.colors[mesh.triangles[triIndex + 1]];
    Color c2 = mesh.colors[mesh.triangles[triIndex + 2]];

    float avgR = (c0.r + c1.r + c2.r) / 3f;
    float avgG = (c0.g + c1.g + c2.g) / 3f;
    float avgB = (c0.b + c1.b + c2.b) / 3f;

    // Vertices painted 100% in one channel — edges naturally average fractionally
    return (avgR * surface.redRisk)
         + (avgG * surface.greenRisk)
         + (avgB * surface.blueRisk);
}
```

Mesh source:
- Static / moving object: `GetComponent<MeshFilter>().sharedMesh`
- Hard-surface-on-bone: `GetComponent<SkinnedMeshRenderer>().sharedMesh`
- Read at parse time only — vertex colors are baked, never change at runtime

### Risk Formula

Applied in `ClimbingSystem` on each new hold acquired:

```csharp
float vertexRisk = currentHold.riskValue;

// CLIMBING_SYSTEM_HOOK: Weather integration — activate in Phase 5
// bool wet = isRaining && !currentSurface.AlwaysDry;
// float rainMultiplier = wet ? this.rainMultiplier : 1f;
float rainMultiplier = 1f; // stubbed

float staminaMultiplier = Mathf.Lerp(
    emptyStaminaMultiplier,     // serialized — default 1f (full risk at empty stamina)
    fullStaminaMultiplier,      // serialized — default 0f (no risk at full stamina)
    stamina.NormalizedStamina   // 0-1 from IStaminaData, clamped to EffectiveMax
);

float finalRisk = vertexRisk * rainMultiplier * staminaMultiplier;
```

Note on `NormalizedStamina`: hunger and rest penalties reduce its ceiling, so `fullStaminaMultiplier` is only reached when stamina is genuinely full with no penalties. This is intentional emergent behaviour — degraded survival state passively increases climbing risk.

**Serialized variables:**
- `float rainMultiplier` (default 1.5f) — commented out until Phase 5
- `float fullStaminaMultiplier` (default 0f)
- `float emptyStaminaMultiplier` (default 1f)

### Tumble Probability Roll

On each new hold acquired:
```csharp
if (Random.value < finalRisk)
    TriggerTumble();
```

Phase 3 debug toggle replaced by this roll.
Confirm Phase 3 tumble path is working correctly before activating the roll.

---

## Phase 5 — Weather and Fall Damage (Future)

All hooks already commented with `// CLIMBING_SYSTEM_HOOK` throughout the codebase.
Search for this tag to find every stub requiring wiring.

**To activate in Phase 5:**
- Source `isRaining` bool from global weather system (interface TBD at time of implementation)
- Uncomment `rainMultiplier` lines in risk formula — `AlwaysDry` already present on `ClimbableSurface` from Phase 1a
- Uncomment `ApplyFallDamage(equivalentFallDistance)` call — wire to health interface (TBD)
- Uncomment sprint grab damage call — wire to same health interface

---

## Complete Serialized Variables Reference

### `ClimbableSurface.cs`
| Variable | Type | Description |
|---|---|---|
| `fallbackRisk` | `float` | Risk value used when mesh has no vertex colors |
| `AlwaysDry` | `bool` | Overrides global wet state — for indoor surfaces |
| `redRisk` | `float` | Risk value for red-painted vertices |
| `greenRisk` | `float` | Risk value for green-painted vertices |
| `blueRisk` | `float` | Risk value for blue-painted vertices |

### `HandholdParser.cs`
| Variable | Type | Description |
|---|---|---|
| `MIN_HH_DIST` | `float` | Minimum spacing between holds along an edge |
| `UP_THRESHOLD` | `float` | Max angle from vertical for up-facing triangle normal |
| `MIN_DIFF` | `float` | Minimum angle between triangle normals to qualify as edge |
| `MAX_DIFF` | `float` | Maximum angle between triangle normals to qualify as edge |
| `searchRadius` | `float` | Hold streaming radius around character |
| `standingHeightOffset` | `float` | Upward offset from SphereCast hit for slide endpoint |

### `ClimbingSystem.cs`
| Variable | Type | Description |
|---|---|---|
| `maxGrabAngle` | `float` | Max angle between character forward and surface normal for grab |
| `minSurfaceAngle` | `float` | Min angle from vertical — prevents grabbing floors/ceilings |
| `regrabCooldown` | `float` | Seconds before grab detection re-activates after intentional drop |
| `sprintGrabDamagePerMeterPerSecond` | `float` | Linear damage scalar for sprint entry (Phase 5 hook) |
| `defaultSlideLength` | `float` | Base slide distance at reference velocity |
| `referenceVelocity` | `float` | Velocity at which slide equals defaultSlideLength |
| `minSlideDistance` | `float` | Below this distance, slide collapses to snap-grab |
| `shoulderWidth` | `float` | SphereCast radius for slide obstruction check |
| `tumbleSlideLength` | `float` | Slide distance during tumble (separate from normal slide) |
| `minJumpHoldTime` | `float` | Seconds Jump must be held before charge begins |
| `maxChargeTime` | `float` | Seconds from charge start to full charge |
| `minPushVelocity` | `float` | Jump-off velocity at minimum charge |
| `maxPushVelocity` | `float` | Jump-off velocity at maximum charge |
| `jumpChargeCamHeight` | `float` | Camera ShoulderOffset.y during jump charge |
| `jumpChargeCamDist` | `float` | Camera CameraDistance during jump charge |
| `jumpChargeCamSide` | `float` | Camera CameraSide during jump charge |
| `jumpChargeCamTransitionDuration` | `float` | Override duration for AimManager camera transition |
| `jumpChargeRootYRotation` | `float` | Target Y rotation for DefaultCameraRoot during charge |
| `jumpChargeRootRotationDuration` | `float` | Duration of DefaultCameraRoot rotation transition |
| `ikFadeInDuration` | `float` | Seconds to fade FinalIK weights in on climb entry |
| `ikFadeOutDuration` | `float` | Seconds to fade FinalIK weights out on climb exit |
| `pendulumWeight` | `float` | 0-1 influence of pendulum on character during FREE hang |
| `effectorEaseCurve` | `AnimationCurve` | Ease curve for normal hold traversal |
| `slideEaseCurve` | `AnimationCurve` | Fast-start/slow-end curve for slides |
| `rainMultiplier` | `float` | Risk multiplier when raining (default 1.5f, Phase 5 stub) |
| `fullStaminaMultiplier` | `float` | Risk multiplier at NormalizedStamina = 1 (default 0f) |
| `emptyStaminaMultiplier` | `float` | Risk multiplier at NormalizedStamina = 0 (default 1f) |
| `settleVelocityThreshold` | `float` | Ragdoll root velocity below which recovery timer starts |
| `settleTimeThreshold` | `float` | Seconds below velocity threshold before recovery triggers |

### `CharacterPendulum.cs`
| Variable | Type | Description |
|---|---|---|
| `pendulumWeight` | `float` | 0-1 — how strongly character follows pendulum masses |

### `PlayerStamina.cs` additions
| Variable | Type | Description |
|---|---|---|
| `ClimbingDrainRate` | `float` | Stamina drained per second while climbing and moving between holds |

---

## Known Limitations and Deferred Items

- **Rope/carbine system:** Removed from scope entirely.
- **Deforming surfaces:** Not supported. Only rigid moving objects and hard-surface-on-bone meshes are climbable.
- **WorldSpace UI first pass:** Grab candidate marker above character's head in Phase 1a. Upgraded to hold-position marker in Phase 1b. Phase 1b requires setting up a fresh world-space `UIDocument` — user will need assistance with this.
- **Health system integration:** Stubbed. Sprint grab damage and fall damage tagged `// CLIMBING_SYSTEM_HOOK` — wired in Phase 5.
- **Weather system integration:** Stubbed. Rain multiplier tagged `// CLIMBING_SYSTEM_HOOK` — wired in Phase 5.
- **Inventory system:** Out of scope for this system.

---

*End of implementation brief.*
*Upload the eight context files listed at the top alongside this document when beginning the implementation conversation.*
