using UnityEngine;
using UnityEngine.InputSystem;

namespace StarterAssets
{
    public class DebugSurvivalInputs : MonoBehaviour
    {
        [SerializeField] private PlayerStamina _stamina;

        // -- Cached InputActions read directly each frame --
        // Bypasses the StarterAssetsInputs bool pattern entirely, which relies
        // on PlayerInput firing callbacks on both press AND release. Reading
        // InputAction.IsPressed() each frame is simpler and always correct.
        private InputAction _restAction;
        private InputAction _eatAction;
        private InputAction _drinkAction;
        private InputAction _drainStaminaAction;
        private InputAction _drainHungerAction;
        private InputAction _drainThirstAction;

        private void Start()
        {
            if (_stamina == null)
                _stamina = GetComponent<PlayerStamina>();

            var playerInput = GetComponent<PlayerInput>();

            if (_stamina == null)
                Debug.LogError("DebugSurvivalInputs: PlayerStamina not found.", this);

            if (playerInput == null)
            {
                Debug.LogError("DebugSurvivalInputs: PlayerInput not found.", this);
                return;
            }

            _restAction = playerInput.actions["DEBUG_U"];
            _eatAction = playerInput.actions["DEBUG_I"];
            _drinkAction = playerInput.actions["DEBUG_O"];
            _drainStaminaAction = playerInput.actions["DEBUG_J"];
            _drainHungerAction = playerInput.actions["DEBUG_K"];
            _drainThirstAction = playerInput.actions["DEBUG_L"];
        }

        private void Update()
        {
            if (_stamina == null || _restAction == null) return;

            float dt = Time.deltaTime;

            // DEBUG_U -- single press. WasPressedThisFrame fires exactly once
            // per press regardless of how long the key is held.
            if (_restAction.WasPressedThisFrame())
                _stamina.Debug_RestoreRest();

            // Held inputs -- IsPressed() is true every frame the key is down,
            // false the frame it's released. No bools, no stale state.
            if (_eatAction.IsPressed()) _stamina.Debug_RestoreHunger(dt);
            if (_drinkAction.IsPressed()) _stamina.Debug_RestoreThirst(dt);
            if (_drainStaminaAction.IsPressed()) _stamina.Debug_DrainStamina(dt);
            if (_drainHungerAction.IsPressed()) _stamina.Debug_DrainHunger(dt);
            if (_drainThirstAction.IsPressed()) _stamina.Debug_DrainThirst(dt);
        }
    }
}