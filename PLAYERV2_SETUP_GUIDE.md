# PlayerV2 Setup & Test Guide

> Living document — filled in as the parity port progresses. When the port is done,
> follow this top-to-bottom to set up and test the new controller in a scene.
> Live tuning values live in `PLAYER_VALUES.md`.

## Status legend
- ✅ ready to set up now
- 🚧 will be finalized in a later step

---

## 1. Overview

PlayerV2 is a modular, state-machine character controller in the `Game.PlayerV2`
assembly (`Assets/_Project/Scripts/`). It reproduces the feel of the old
StarterAssets `ThirdPersonController` while splitting responsibilities into:

- **PlayerController** — coordinates components, runs the state machine, ground check.
- **InputHandler** — locomotion input (Move/Look/Jump/Sprint/Stealth/Aim).
- **PlayerMotor** — all movement mechanics (speed/accel, rotation, gravity, jump) + animator. ✅
- **PlayerCameraRig** — rotates the Cinemachine follow target (look).
- **States** — Idle / Move / Sprint / Stealth / Jump (policy + transitions only).
- **PlayerStamina** — survival stats (unchanged from the live game). ✅ (driven via StaminaSystem; controller requires the component)

---

## 2. Input asset ✅

The controller uses **`Assets/Imported/Starter Assets/Runtime/InputSystem/StarterAssets.inputactions`**
(NOT the root `InputSystem_Actions.inputactions`, which is an unused default template).

On the player's **PlayerInput** component:
- **Actions:** `StarterAssets.inputactions`
- **Default Map:** `Player`
- **Behavior:** any (InputHandler subscribes to the action objects directly). The live
  game used *Send Messages* for `StarterAssetsInputs`; PlayerV2 does not use that component.

Relevant actions consumed by the controller: `Move`, `Look`, `Jump`, `Sprint`, `Stealth`, `Aim`.
(Item `Next Item`/`Previous Item`/`Use` stay owned by the Tools systems — CycleItems/AimManager.)

---

## 3. Player GameObject — required components ✅/🚧

Build the player object (or adapt a copy of the current player prefab) with:

| Component | Notes |
|---|---|
| `CharacterController` | capsule; radius should match `GroundedRadius` (0.28). |
| `Animator` | Controller = `StarterAssetsThirdPerson.controller` (see §6). |
| `PlayerInput` | see §2. |
| `InputHandler` | no config. |
| `PlayerController` | movement config — see §4. |
| `PlayerCameraRig` | camera config — see §5. |
| `PlayerStamina` | required by PlayerController; survival rates — see `PLAYER_VALUES.md`. Auto-added if missing. |
| `PlayerFootstepAudio` | receives the `OnFootstep`/`OnLand` animation events; assign footstep clips + landing clip (copy from the old ThirdPersonController's `FootstepAudioClips`/`LandingAudioClip`). Without it you get harmless "has no receiver" warnings and no footstep audio. |

> Components requiring each other: `PlayerController` requires `CharacterController`,
> `Animator`, `InputHandler`. `InputHandler` requires `PlayerInput`.

---

## 4. PlayerController — movement config ✅

Set these on the PlayerController inspector (defaults already match `PLAYER_VALUES.md`):

| Field | Value |
|---|---|
| Run Speed | 5 |
| Sprint Speed | 12 |
| Stealth Speed | 2 |
| Fatigued Speed | 1.2 |
| Speed Change Rate | 10 |
| Rotation Smooth Time | 0.12 |
| Jump Height | 1.2 |
| Gravity | -15 |
| Jump Timeout | 0.3 |
| Fall Timeout | 0.15 |
| Ground Layer | Default |

Camera-relative movement uses the **main camera** (tagged `MainCamera`); it's auto-found if
the controller's camera field is empty.

---

## 5. PlayerCameraRig — camera setup ✅

`PlayerCameraRig` rotates a **follow-target transform**; the Cinemachine vcam follows it.

1. Create/identify the follow target (e.g. a child `PlayerCameraRoot` at head height).
2. On `PlayerCameraRig`:
   - **Cinemachine Camera Target** → the follow target transform.
   - **Top Clamp** = 70, **Bottom Clamp** = -30, **Camera Angle Override** = 0 (defaults).
   - **Input** → auto-found if `InputHandler` is on the same GameObject.
3. On the **CinemachineCamera** (vcam): set **Follow** → the same follow target.
   Use a **CinemachineThirdPersonFollow** with ShoulderOffset (1,0,0), CameraDistance 4,
   CameraSide 0.5 (your live values). Aim modes adjust these at runtime via AimManager.

> The rig only rotates a Transform — no Cinemachine code dependency on the controller.

---

## 6. Animator ✅

- Animator Controller: `Assets/Imported/Starter Assets/Runtime/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller`
- Locomotion params driven by PlayerV2 (names/types must match):
  `Speed` (float), `MotionSpeed` (float), `Grounded` (bool), `Jump` (bool),
  `FreeFall` (bool), `Sprint` (bool), `Fatigued` (bool), `Stealth` (bool).
- Aim/tool params (`IsAiming`, `AimMoveX/Y`, `IsScanning`, `IsHookshotDragging`,
  `Fire`, `Throw`, etc.) are driven by the Tools systems, not the controller.

---

## 7. Tools / HUD integration ✅ (code) / 🚧 (prefab swap)

The aim/Tools systems (`AimModeBase` + `ShootAim`/`ThrowAim`/`ScanAim`, `HookshotDragMode`,
`AimManager`, `CharacterStateManager`) are now refactored off the old `ThirdPersonController`
onto the PlayerV2 interfaces (`IPlayerMotor`, `IControlLock`, `ICameraState`) and read move
input from `InputHandler`. They **resolve these at runtime** via `GetComponentInParent`, so
there's nothing to assign in the inspector for the controller link — the old `tpcController`
/ `starterInputs` fields are gone.

**Prefab swap (8c) — to bring the full game (aim/hookshot/HUD) onto PlayerV2:**
Take the ORIGINAL player prefab (the one with the whole aim/Tools stack) and:
1. Remove the old controller components: `ThirdPersonController`, `StarterAssetsInputs`,
   `DebugSurvivalInputs` (and the old, now-empty CharacterStateManager wiring is fine — the
   component stays, it just forwards to IControlLock now).
2. Add the PlayerV2 components: `PlayerController`, `InputHandler`, `PlayerCameraRig`,
   `PlayerFootstepAudio` (keep `Animator`, `CharacterController`, `PlayerInput`, `PlayerStamina`,
   and the entire aim/Tools/CycleItems stack).
3. Wire as in §2–§6. The aim stack auto-finds the new interfaces — no per-mode reassignment.

The HUD still reads `IStaminaData` from `PlayerStamina` (unchanged). After this, hookshot
fire/drag should freeze + drag via the ExternalControl state, and aiming should suppress
sprint / skip aim-exaggeration while the camera is frozen.

---

## 8. Test scene — step by step 🚧

1. Open/create a simple test scene with ground (on the Ground/Default layer).
2. Place the player object (§3) and a CinemachineCamera + CinemachineBrain on the Main Camera.
3. Assign the follow target (§5).
4. Enter Play mode and verify (see checklist).

## 9. Testing checklist 🚧

- [ ] Idle: stands still, no drift; `Speed`≈0.
- [ ] Walk/Run: moves camera-relative; character turns smoothly (RotationSmoothTime feel).
- [ ] Gamepad vs mouse look both feel right (no speed difference from frame rate).
- [ ] Sprint (hold Sprint + move): faster; `Sprint` bool on; suppressed while aiming.
- [ ] Stealth (toggle): slower; `Stealth` bool on; cancelled by sprint and jump.
- [ ] Jump: rises ~JumpHeight; `Jump` then `FreeFall` on the way down; lands cleanly.
- [ ] Camera pitch clamps at 70 / -30.
- [ ] (after stamina) Sprint drains stamina; fatigue forces slow speed, blocks jump/sprint.
