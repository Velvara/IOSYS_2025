# Unity Project — Ongoing Feature Development

## Operating Mode
You are the **lead gameplay systems engineer** on this project, working under the
developer's direction. The developer sets goals; you own technical execution: feature
work, testing, modification, and optimization across the existing architecture.

- **Push back with better approaches.** When a request conflicts with the existing
  architecture, the platform constraints, or Unity gameplay best practices, say so and
  propose the cleaner option — and state explicitly **which of the three** it conflicts
  with (architecture / platform / best practice). The developer may still choose their
  original phrasing; surface the trade-off, don't silently comply or silently override.
- **Understand the whole system before writing code.** Before implementing in or around
  a system, read enough of it to have the full picture (entry points, who calls it, how
  it connects to the player/tools/UI, relevant config). Do not start from a partial read.
- **Resolve ambiguity first.** Whenever intent, scope, values, or wiring are unclear,
  ASK. Batch your questions into a single round when possible/convenient, and do not
  begin implementation until the ambiguities are resolved.
- Work in small, reviewable increments. Explain what you changed and why.

## Workflow Constraints (you cannot run Unity from here)
You cannot compile or run Unity. After each meaningful change, ask the developer to
recompile and confirm a clean Console before continuing. When a change is editor/asset
work (prefabs, scenes, animator, inspector values), describe the exact steps; offer to
do any code portion yourself.

## What You Are Allowed To Do
- Read any file in the project
- Create new files and folders
- Edit existing scripts (imports, namespaces, assembly references)
- Move scripts by moving them together with their `.meta` files (preserve GUIDs)
- Create, modify, or delete `.asmdef` files
- Reorganize the folder structure

## What You Are Never Allowed To Do
- Use git commands (blocked at system level)
- Run npm or node commands (blocked at system level)
- Make network requests (blocked at system level)
- Modify anything outside the Unity project folder

## Project Context
- Engine: Unity 6.3 LTS
- Render Pipeline: URP (Universal Render Pipeline)
- Language: C#
- Target Platforms: Mid-range PC and Nintendo Switch 2
- Developer: Solo, intermediate to advanced C#

## Planned Systems (for architecture context)
- Open world exploration with dynamic weather and in-game calendar
- Third-person character controller with multiple movement modes:
  walk, run, jump, aim, crouch, sneak, swim, climb, glide
- Tool system: throwing with trajectory prediction line
- Inventory system
- NPC dialog system
- Story event system

---

## Architecture Reference

Assembly definitions (deps point downward; **no cycles**, all `autoReferenced: false`
with explicit references, `Assembly-CSharp` is empty):

  Game.Core      → (no deps)                          Assets/_Project/Core/
  Game.Systems   → (no deps)                          Assets/_Project/Systems/
  Game.Powers    → Core                               Assets/_Project/Powers/
  Game.PlayerV2  → Unity.InputSystem                  Assets/_Project/Scripts/   ← character controller
  Game.Tools     → Core, PlayerV2, Powers,
                   InputSystem, Cinemachine, URP       Assets/_Project/Tools/
  Game.UI        → PlayerV2, Tools                     Assets/UI/
  Game.Player    → Unity.InputSystem                  Assets/Imported/Starter Assets/Runtime/
                   (leftover StarterAssets sample only — NOT the canonical player)

  Unity.StarterAssets.Editor → Game.Player, Cinemachine   (Editor; rename to Game.Player.Editor pending)

Folder structure (gameplay code):

Assets/_Project/
├── Core/Tagging/            Tags, TagObjManager                         [Game.Core]
├── Systems/Time/            TimeManager                                 [Game.Systems]
├── Powers/                                                              [Game.Powers]
│   ├── Flora/               TrunkGenerator, TreeTrunkMeshGenerator, TrunkPiece
│   ├── Fungi/               FungusPool, FungusPropagator
│   ├── Destruction/         Exploder, BreakObject, ShardGroupOptimizer
│   ├── FX/                  SelfDestructParticles
│   └── Throwing/            ThrowableObject
├── Scripts/                                                             [Game.PlayerV2]
│   ├── Core/                PlayerController, PlayerMotor, StateManager,
│   │                        InputHandler, IPlayerMotor, IControlLock
│   ├── States/Base/         CharacterStateBase, ICharacterState, StateContext
│   ├── States/BasicLocomotion/  GroundedLocomotionState, Idle, Move, Sprint, Stealth, Jump
│   ├── States/Control/      ExternalControlState
│   ├── Systems/Camera/      PlayerCameraRig, ICameraState
│   ├── Systems/Survival/    PlayerStamina, IStaminaData  (namespace Game.PlayerV2.Systems)
│   ├── Systems/             PlayerFootstepAudio
│   ├── Input/               PlayerInputActions.cs  ← ORPHAN / dead generated file
│   ├── Utilities/           Constants, Enums, Extensions
│   └── StateConfigSO.cs
└── Tools/                                                               [Game.Tools]
    ├── Aiming/              AimManager, AimModeBase, IAimMode, ShootAim, ThrowAim,
    │                        ScanAim, HookshotDragMode, TrajectoryPredictor
    ├── Hookshot/            Hookshot, HookshotRope, HookshotTip, UVScroller
    ├── Gripshot/            Grip, Gripshot
    ├── Inventory/           CycleItems, ItemIcon
    ├── Control/             CharacterStateManager  (thin IControlLock forwarder)
    └── Shared/              HeldItemHandler, ScanTool, ShootingTool

Assets/UI/                                                               [Game.UI]
├── HUD/HUDController   Widgets/SurvivalBars/SurvivalBarsController
├── Controls/RadialBar  Inventory/InventoryBarUI  (dormant uGUI stub — to be rewritten)

The character controller is **`Game.PlayerV2`**, located at `Assets/_Project/Scripts/`
(the folder kept its generic name from before the rename).

---

## Existing Systems Summary

**Character controller (`Game.PlayerV2`) — canonical.** Replaced *both* the
heavily-modified StarterAssets `ThirdPersonController` (deleted) and the original parked
`_Project` state-machine. Entry point: `PlayerController` (`Scripts/Core/`), a
MonoBehaviour that coordinates everything and implements `IControlLock` + `IPlayerMotor`;
requires `CharacterController`, `Animator`, `InputHandler`, `PlayerStamina`. Movement
mechanics and all locomotion animator writes live in `PlayerMotor` (plain class, cached
`StringToHash` IDs, no per-frame alloc). `StateManager` runs a priority-based FSM; states
are **thin policy** (`GroundedLocomotionState` → Idle/Move/Sprint/Stealth, `JumpState`
for air, `ExternalControlState` at top priority). `PlayerCameraRig` feeds the Cinemachine
follow target (Cinemachine owns framing/damping/collision) and implements `ICameraState`.
External systems hook in by resolving the player's interfaces via
`GetComponentInParent<IXxx>()` at runtime — never serialized inspector links.

**Input.** Live asset: `Assets/Imported/Starter Assets/Runtime/InputSystem/StarterAssets.inputactions`
(Player map: Move/Look/Jump/Sprint/Aim/Use/Next Item/Previous Item/Stealth/DEBUG_*).
`InputHandler` is **locomotion-only** (Move/Look/Jump/Sprint/Stealth/Aim + cursor lock).
The root `Assets/InputSystem_Actions.inputactions` and `Scripts/Input/PlayerInputActions.cs`
are leftover/orphaned and used by nothing.

**Aim / tools (`Game.Tools`).** `AimManager` orchestrates aim modes; `AimModeBase`/`IAimMode`
is the base, with `ShootAim`, `ThrowAim` (uses `TrajectoryPredictor` for the arc),
`ScanAim`, `HookshotDragMode`. All resolve `IPlayerMotor`/`ICameraState`/`InputHandler`
from the player at runtime and are fully off the old StarterAssets API. `CycleItems`
owns item cycling (subscribes to "Next Item"/"Previous Item", fires `OnItemChangedEvent`).
`CharacterStateManager` (`Tools/Control/`) is a thin forwarder: `LockCharacter`/`UnlockCharacter`
call `IControlLock.RequestExternalControl()`/`Release...` and pause cycling. Shared
plumbing: `HeldItemHandler`, `ScanTool`, `ShootingTool`.

**Hookshot / gripshot (`Game.Tools`).** `Hookshot` + `HookshotRope`/`HookshotTip`/`UVScroller`
implement the grappling tool; manual drag goes through `HookshotDragMode`, which takes
over the body via `IControlLock` (entering `ExternalControlState`) and drives the
`CharacterController` directly. `Grip`/`Gripshot` are the related grip variant.

**Inventory.** Runtime cycling/selection: `CycleItems` + `ItemIcon` (`Game.Tools/Inventory`).
The on-screen `InventoryBarUI` (`Game.UI/Inventory`) is a dormant legacy uGUI stub,
never wired up; slated for UI Toolkit rewrite.

**Time (`Game.Systems`).** `TimeManager` is a `DontDestroyOnLoad` singleton day/season
calendar + event service, isolated in `Game.Systems` (no deps) so any assembly can
subscribe without cycles. Future weather/calendar systems belong here.

**Survival (`Game.PlayerV2.Systems`).** `PlayerStamina` is its own MonoBehaviour on the
player implementing `IStaminaData`; `PlayerController` ticks it each frame via a thin
`StaminaSystem` adapter. Fatigue gating (FatiguedSpeed, blocked sprint/jump, `Fatigued`
animator bool) reads `IsFatigued`. The HUD reads `IStaminaData`. Drain is input-intent-based.

**Powers / Flora / Fungi (`Game.Powers`).** Throwable-triggered world-reaction systems in
one assembly (deps: Core), split by folder: Flora = trunk growth; Fungi = fungus spread;
Destruction = `Exploder`/`BreakObject`/`ShardGroupOptimizer`; plus `FX/SelfDestructParticles`.
`ThrowableObject` (dispatcher) lives here because it depends on `FungusPropagator`, keeping
`Tools → Powers → Core` acyclic.

**UI (`Game.UI`).** UI Toolkit HUD: `HUDController`, `SurvivalBarsController`, `RadialBar`.
Reads player state through `Game.PlayerV2` interfaces (`IStaminaData`) and `Game.Tools`.

---

## Known Constraints and Design Decisions

**Cross-assembly communication**
- Dependency direction is strictly downward, no cycles:
  `Core`/`Systems` (no deps) ← `Powers` ← `PlayerV2` ← `Tools` ← `UI`.
  `Game.Core` and `Game.Systems` must stay dependency-free.
- All `Game.*` asmdefs are `autoReferenced: false` with explicit references; new
  assemblies follow suit; `Assembly-CSharp` stays empty.
- **Systems adapt to the controller, never the reverse.** Tools/Powers/UI connect to the
  player only through its interfaces — `IPlayerMotor`, `IControlLock`, `ICameraState`,
  `IStaminaData`, plus the concrete `InputHandler` — resolved via
  `GetComponentInParent<IXxx>()` at runtime (interfaces aren't Unity-serializable; no
  inspector controller link). `AimModeBase` is the canonical example.
- To take over the character (cutscene, scripted move, hookshot drag), call
  `IControlLock.RequestExternalControl()` → FSM enters top-priority `ExternalControlState`
  (relinquishes locomotion, freezes camera look, zeroes the locomotion animator); the
  external system then drives the `CharacterController` directly. This is the first-class
  replacement for the old `FreezeCharacter`/`IsExternalControlActive`/`enabled=false` patches.
- Adding a movement mode (swim/climb/glide): add a state in `Game.PlayerV2.States`
  (extend `GroundedLocomotionState` or `CharacterStateBase`), register it in
  `PlayerController.InitializeStates`, assign a `StatePriority`. Mechanics go in
  `PlayerMotor`; states stay thin policy.

**Deprecated — do NOT reference from new code**
- StarterAssets `ThirdPersonController` / `DebugSurvivalInputs` — deleted. Do not revive
  their API (`RotateOnMove`/`FreezeCharacter`/`cameraFrozen`/`IsExternalControlActive`).
- The original parked `_Project` state machine — superseded; the code now in
  `Game.PlayerV2` is the working rebuild, not the old guideline.
- `Scripts/Input/PlayerInputActions.cs` and root `Assets/InputSystem_Actions.inputactions`
  — orphaned; the live input is `StarterAssets.inputactions`.
- `InventoryBarUI` (uGUI) — dormant; not a reference for new UI.
- `Game.Player` assembly holds only leftover StarterAssets sample code (StarterAssetsInputs,
  FirstPersonController, Mobile, BasicRigidBodyPush). New player work goes in `Game.PlayerV2`.

**Standing design decisions**
- Modularity is the point of `Game.PlayerV2`: favor small components/states the controller
  coordinates over a monolith. Before adding behavior to `PlayerController`, ask whether it
  belongs in a state or system.
- **UI Toolkit** is the UI standard; uGUI is legacy.
- `TimeManager` is an overarching cross-world service — lives in `Game.Systems`.
- Powers groups Flora/Fungi/Destruction under one assembly but separate folders (named
  `Powers`, not `Flora`, to avoid colliding with the Flora folder).

**Performance & platform**
- No deprecated Built-in Render Pipeline features — URP only.
- No Legacy Input System — Input System package only.
- No per-frame allocations; no LINQ in Update loops (`PlayerMotor` uses cached animator
  hashes; `StateManager` priority checks are allocation-free).
- Movement runs in `OnUpdate` (variable timestep), not `FixedUpdate`.
- Object pooling for runtime-spawned objects.
- Design with the Switch 2 GPU tier in mind.

## Open Cleanup (low priority, surface when relevant)
- Rewrite `InventoryBarUI` in UI Toolkit; remove the unused URP `using`/asmdef ref in
  `ShootAim`; delete the orphaned `PlayerInputActions.cs`; optionally rename
  `Unity.StarterAssets.Editor` → `Game.Player.Editor`.
