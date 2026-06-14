# Character Controller — Modular Parity Port (Handoff Brief)

> Read this together with `CLAUDE.md` and the memory index `MEMORY.md`
> (esp. `controller-architecture`, `phase1-status`). Phase 1 (assembly/folder
> restructure) is DONE; this brief is for the next task.

## 1. Goal & philosophy
Build the parked **`Game.PlayerV2`** controller into the project's **canonical, clean,
modular character controller**, then retire the modified Starter Assets controller.

- Do **NOT** port the Starter Assets implementation 1:1. Build a clean BASE ARCHITECTURE.
- **Connected systems adapt to the new controller**, not the reverse.
- **Preserve game FEEL, not implementation.** Match how it plays, using whatever is
  cleanest in the new design.
- Current Starter Assets behaviors that are *patches* around its limitations should be
  redesigned as first-class concepts (see §5).

## 2. FIRST STEP (do this before writing any controller code)
**Ask the developer for the current scene's character configuration / values.** The
controller defaults in code are likely overridden on the player prefab/scene instance.
Request the live values for:
- Movement: RunSpeed, SprintSpeed, StealthSpeed, FatiguedSpeed, SpeedChangeRate, RotationSmoothTime
- Jump/gravity: JumpHeight, Gravity, JumpTimeout, FallTimeout
- Grounding: GroundedOffset, GroundedRadius, GroundLayers
- Camera: TopClamp, BottomClamp, CameraAngleOverride, the Cinemachine rig setup
  (CinemachineThirdPersonFollow ShoulderOffset/CameraDistance/CameraSide defaults & aim values)
- PlayerStamina: all rates (drain/recovery/thirst/hunger/penalties — already in `PlayerStamina.cs`,
  but confirm any per-instance overrides)
- The Animator Controller asset used by the player, and its parameter list.

## 3. Where things are now
- **`Game.PlayerV2`** (parked, unreferenced, `autoReferenced:false`): `Assets/_Project/Scripts/`
  - `Core/PlayerController.cs`, `Core/StateManager.cs`, `Core/InputHandler.cs`
  - `States/Base/{ICharacterState,CharacterStateBase,StateContext}.cs`
  - `States/BasicLocomotion/{Idle,Move,Sprint,Jump,Stealth}State.cs`
  - `Systems/SystemsPlaceholder.cs` (placeholder Stamina/Health/Inventory/Camera),
    `Systems/Camera/CameraSettings.cs`
  - `StateConfigSO.cs`, `Utilities/{Constants,Enums,Extensions}.cs`
  - `Input/PlayerInputActions.cs` (generated)
  - Namespaces: `Game.PlayerV2`, `Game.PlayerV2.States`, `Game.PlayerV2.Systems`
  - asmdef references: `Unity.InputSystem`
- **`Game.Player`** (canonical TODAY): `Assets/Imported/Starter Assets/Runtime/`
  - `ThirdPersonController.cs` (heavily modified — the behavior to match),
    `StarterAssetsInputs.cs`, `PlayerStamina.cs`, `IStaminaData.cs`, `DebugSurvivalInputs.cs`

## 4. The integration/coupling surface (what a drop-in must satisfy)
The aiming/tools + HUD systems are welded to the current controller. The new controller
must either expose the same public API (drop-in) OR these consumers must be refactored.
Decide the strategy with the dev. Consumers and the members they use:

- `Game.Tools/Aiming/AimModeBase.cs` → field `StarterAssets.ThirdPersonController tpcController`;
  reads `cameraFrozen`; sets `RotateOnMove`.
- `Game.Tools/Aiming/ShootAim.cs` → `tpcController.cameraFrozen`, aim direction logic.
- `Game.Tools/Aiming/HookshotDragMode.cs` → `IsExternalControlActive`, `RotateOnMove`,
  `transform`, `GetComponent<CharacterController>()`, `enabled`.
- `Game.Tools/Aiming/AimManager.cs` → IK in `OnAnimatorIK`, reads aim/freeze state.
- `Game.Tools/Control/CharacterStateManager.cs` → `FreezeCharacter(bool,bool)`,
  `enabled=false`, `StarterAssetsInputs` zeroing, `CycleItems.LockCycling`.
- `Game.UI` (HUD) → `IStaminaData` (implemented by `PlayerStamina`).

Public API the current controller exposes (replicate or replace deliberately):
`RotateOnMove`, `FreezeCharacter(bool,bool)`, `cameraFrozen`, `IsExternalControlActive`,
`Controller` (CharacterController), `CurrentStamina`, `IsFatigued`, `IsSprinting`, `IsStealth`.

## 5. Patches to redesign as first-class concepts (don't reproduce as-is)
- **External control / freeze** (`FreezeCharacter`, `IsExternalControlActive`, disabling the
  whole component for hookshot drag) → model as a proper **state** (e.g. `ExternalControl`/`Locked`)
  in the state machine. `CharacterStateManager` should then largely dissolve into the machine.
- **Cross-state speed selection** (Run/Sprint/Stealth/Fatigued chosen in one `Move()` with
  stick-magnitude scaling + SpeedChangeRate lerp) → choose the cleanest approach in the new
  design that preserves the same acceleration feel.

## 6. Known gaps in `Game.PlayerV2` to close
- `Systems/Camera/CameraManager` is a **placeholder** — no Cinemachine. Port the camera
  rotation (yaw/pitch clamps, mouse-vs-gamepad deltaTime) and Cinemachine rig handling.
  (Will require adding `Unity.Cinemachine` to the PlayerV2 asmdef.)
- `Systems/StaminaSystem` is a **placeholder** — must drive the real `PlayerStamina`
  (`Tick(isSprinting)`, read `IsFatigued`) and feed the HUD via `IStaminaData`. Decide whether
  `PlayerStamina`/`IStaminaData` move into PlayerV2 or PlayerV2 references `Game.Player`.
- **Animator parameter mismatch.** Current animator contract (from `ThirdPersonController`):
  `Speed` (normalized to SprintSpeed), `MotionSpeed`, `Grounded`, `Jump`, `FreeFall`,
  `Sprint`, `Fatigued`, `Stealth`. PlayerV2 currently uses different names
  (`speed`, `IsGrounded`, `verticalVelocity`, `IsSprinting`, `IsStealth`). Align PlayerV2 to
  the REAL animator (don't rebuild the animator unless intended).
- **Sprint-while-aiming suppression**: current controller disables sprint while right-mouse
  (aim) is held (`!Mouse.current.rightButton.isPressed && _input.sprint`). The new Sprint
  state must respect aim.
- **Input reconciliation**: PlayerV2's `InputHandler` expects a `Player`/`UI` action map with
  actions (Move, CameraLook, Sprint, Jump, Interact, Aim, Scan, Stealth, CycleInv, OpenInv).
  The live game currently uses `Assets/InputSystem_Actions.inputactions` with a different set
  (Move, Look, Sprint, Jump, Aim, Use, Next Item, Previous Item, Stealth, DEBUG_*) consumed by
  `StarterAssetsInputs` (SendMessages) + the aim/cycle scripts. Converge onto one input asset.
- **Perf (Switch 2 / no per-frame alloc rule)**: `StateManager.CheckPriorityTransitions()` runs
  LINQ (`Where/OrderBy/ToList`) every frame — replace with a cached, pre-sorted state list.

## 7. Suggested approach
1. Get scene values (§2).
2. Agree drop-in vs. refactor-consumers (§4) with the dev.
3. Build/extend PlayerV2 incrementally in a **test scene**, side-by-side with the current
   player, verifying feel against the live values.
4. Port camera + real stamina + animator contract; add the external-control state.
5. Reconcile input asset.
6. When parity is confirmed, swap the player prefab to PlayerV2, repoint `Game.Tools`/`Game.UI`
   references from `Game.Player` to the new controller, and retire the Starter Assets controller.
7. Decide the final namespace/asmdef name (likely promote `Game.PlayerV2` → `Game.Player` once
   the old one is retired).

## 8. Working constraints / process
- Claude **cannot compile or run Unity** here. After each meaningful change, ask the dev to
  recompile and confirm a clean Console before continuing (this cadence worked well in Phase 1).
- Move `.cs` files **with their `.meta`** (preserves GUIDs / scene-prefab references).
- git / npm / node / network are blocked at the system level. Project is under Plastic SCM
  (changes are recoverable).
- URP only; Input System only (no legacy); design for Switch 2 GPU tier; object pooling for
  runtime spawns; no per-frame allocations.
