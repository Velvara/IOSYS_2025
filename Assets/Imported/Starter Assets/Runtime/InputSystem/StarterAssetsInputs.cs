using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.Windows;
#endif

namespace StarterAssets
{
    public class StarterAssetsInputs : MonoBehaviour
    {
        [Header("Character Input Values")]
        public Vector2 move;
        public Vector2 CurrentMoveInput => move;
        public Vector2 look;
        public bool jump;
        public bool sprint;
        public bool aim;
        public bool stealth;

        [Header("Movement Settings")]
        public bool analogMovement;

        [Header("Mouse Cursor Settings")]
        public bool cursorLocked = true;
        public bool cursorInputForLook = true;

        [Header("Input Lock State")]
        public bool inputsLocked = false;

        // -- Debug inputs --
        // Read each frame by DebugSurvivalInputs.
        // All six are held-style bools: true while the key is held, false when released.
        // Exception: debugRest is consumed (set to false) immediately after one read
        // in DebugSurvivalInputs, giving it single-press behaviour.
        [Header("Debug Input Values")]
        public bool debugRest;
        public bool debugEat;
        public bool debugDrink;
        public bool debugDrainStamina;
        public bool debugDrainHunger;
        public bool debugDrainThirst;

#if ENABLE_INPUT_SYSTEM
        public void OnMove(InputValue value)
        {
            if (inputsLocked) return;
            MoveInput(value.Get<Vector2>());
        }

        public void OnLook(InputValue value)
        {
            if (inputsLocked || !cursorInputForLook)
            {
                look = Vector2.zero;
                return;
            }
            LookInput(value.Get<Vector2>());
        }

        public void OnJump(InputValue value)
        {
            if (inputsLocked) return;
            JumpInput(value.isPressed);
        }

        public void OnSprint(InputValue value)
        {
            if (inputsLocked) return;
            SprintInput(value.isPressed);
        }

        public void OnStealth(InputValue value)
        {
            if (inputsLocked) return;
            StealthInput(value.isPressed);
        }

        public void OnAim(InputValue value)
        {
            if (inputsLocked) return;
            AimInput(value.isPressed);
        }

        // -- Debug input callbacks --
        // Named to match the action names in the Input Asset:
        // DEBUG_U, DEBUG_I, DEBUG_O, DEBUG_J, DEBUG_K, DEBUG_L

        public void OnDEBUG_U(InputValue value) { debugRest         = value.isPressed; }
        public void OnDEBUG_I(InputValue value) { debugEat          = value.isPressed; }
        public void OnDEBUG_O(InputValue value) { debugDrink        = value.isPressed; }
        public void OnDEBUG_J(InputValue value) { debugDrainStamina = value.isPressed; }
        public void OnDEBUG_K(InputValue value) { debugDrainHunger  = value.isPressed; }
        public void OnDEBUG_L(InputValue value) { debugDrainThirst  = value.isPressed; }
#endif

        public void MoveInput(Vector2 newMoveDirection)  { move    = newMoveDirection; }
        public void LookInput(Vector2 newLookDirection)  { look    = newLookDirection; }
        public void JumpInput(bool newJumpState)         { jump    = newJumpState; }
        public void SprintInput(bool newSprintState)     { sprint  = newSprintState; }
        public void StealthInput(bool newStealthState)   { stealth = newStealthState; }
        public void AimInput(bool newAimState)           { aim     = newAimState; }

        private void OnApplicationFocus(bool hasFocus) { SetCursorState(cursorLocked); }
        private void SetCursorState(bool newState)
        {
            Cursor.lockState = newState ? CursorLockMode.Locked : CursorLockMode.None;
        }
    }
}
