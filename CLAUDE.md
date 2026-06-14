# Unity Project — PHASE 2: Character Controller (Modular Parity Port)

## Operating Mode
You are a gameplay systems engineer with write access.
Your job is to build the parked `Game.PlayerV2` controller into the project's
canonical, clean, modular character controller, preserving the current game FEEL,
and then retire the heavily-modified Starter Assets controller.

IMPORTANT: Before doing anything, read `PARITY_PORT_BRIEF.md` (project root) and the
memory index `MEMORY.md` (esp. `controller-architecture`, `phase1-status`). The brief
contains the full plan, the coupling surface, and the known gaps to close.

## First Step (mandatory)
Before writing controller code, ASK THE DEVELOPER for the current scene's character
configuration and values (speeds, rotation/jump/gravity, grounding, camera/Cinemachine
rig, PlayerStamina rates, and the Animator Controller + its parameters). See
`PARITY_PORT_BRIEF.md` §2 for the exact list. Defaults in code are likely overridden
on the player prefab/scene instance.

## Guiding Principles
- Build a clean BASE ARCHITECTURE; do not port the Starter Assets implementation 1:1.
- Connected systems (aiming/tools, HUD, inventory) ADAPT TO the new controller, not the reverse.
- Preserve game FEEL, not implementation. Redesign current "patches" (e.g. FreezeCharacter /
  external-control freeze, cross-state speed selection) as first-class state-machine concepts.

## What You Are Allowed To Do
- Read any file in the project
- Create new files and folders
- Edit existing scripts (imports, namespaces, assembly references)
- Move scripts by moving them together with their `.meta` files (preserve GUIDs)
- Create, modify, or delete .asmdef files
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

## Current State (post Phase 1)
- The assembly/folder restructure is COMPLETE. Code is split into explicit assemblies:
  `Game.Core`, `Game.Systems`, `Game.Powers`, `Game.Player` (current canonical controller +
  survival), `Game.Tools`, `Game.UI`, and `Game.PlayerV2` (parked modular controller, the
  subject of this phase). `Assembly-CSharp` is empty; all `Game.*` are `autoReferenced:false`
  with explicit references. See `phase1-status` memory for the graph and deferred cleanup items.
- `Game.PlayerV2` compiles in isolation but is unreferenced. Its camera and stamina systems are
  placeholders, its animator parameter names differ from the live animator, and
  `StateManager.CheckPriorityTransitions()` uses per-frame LINQ that must be replaced.

## Planned Systems (for architecture context)
- Open world exploration with dynamic weather and in-game calendar
- Third-person character controller with multiple movement modes:
  walk, run, jump, aim, crouch, sneak, swim, climb, glide
- Tool system: throwing with trajectory prediction line
- Inventory system
- NPC dialog system
- Story event system

## How to Work
Read `PARITY_PORT_BRIEF.md` and the relevant memory before touching anything.
Ask for the scene's character values first. Work in small, reviewable increments.
You CANNOT compile or run Unity from here — after each meaningful change, ask the
developer to recompile and confirm a clean Console before continuing. When you make
changes, explain what you changed and why. After completing a section, summarize what
was done so the developer can review before continuing.

## Performance and Technical Constraints
- No deprecated Built-in Render Pipeline features
- URP only for all rendering
- No Legacy Input System — use Input System package
- No per-frame allocations (no LINQ in Update loops)
- Design with Switch 2 GPU tier in mind
- Use object pooling for runtime-spawned objects
