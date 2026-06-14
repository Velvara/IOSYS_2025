using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.PlayerV2
{
    /// <summary>
    /// Handles all player input using the New Input System
    /// Processes raw input and provides buffering for responsive controls
    /// </summary>
    [RequireComponent(typeof(PlayerInput))]
    public class InputHandler : MonoBehaviour
    {
        #region Input Properties

        /// <summary>
        /// Normalized movement input (range -1 to 1 on each axis)
        /// </summary>
        public Vector2 MoveInput { get; private set; }

        /// <summary>
        /// Camera look input delta
        /// </summary>
        public Vector2 LookInput { get; private set; }

        /// <summary>
        /// Is sprint button held?
        /// </summary>
        public bool SprintHeld { get; private set; }

        /// <summary>
        /// Was jump button pressed this frame or recently?
        /// </summary>
        public bool JumpPressed => _jumpBufferCounter > 0f;

        /// <summary>
        /// Was interact button pressed this frame?
        /// </summary>
        public bool InteractPressed { get; private set; }

        /// <summary>
        /// Is aim button held?
        /// </summary>
        public bool AimHeld { get; private set; }

        /// <summary>
        /// Is scan button held?
        /// </summary>
        public bool ScanHeld { get; private set; }

        /// <summary>
        /// How long has the scan button been held?
        /// </summary>
        public float ScanHoldTime { get; private set; }

        /// <summary>
        /// Is stealth toggled on?
        /// </summary>
        public bool StealthToggled { get; private set; }

        /// <summary>
        /// Was cycle inventory forward pressed?
        /// </summary>
        public bool CycleInventoryForwardPressed { get; private set; }

        /// <summary>
        /// Was cycle inventory backward pressed?
        /// </summary>
        public bool CycleInventoryBackwardPressed { get; private set; }

        /// <summary>
        /// Was open inventory pressed?
        /// </summary>
        public bool OpenInventoryPressed { get; private set; }

        #endregion

        #region Private Fields

        private PlayerInput _playerInput;
        private InputActionMap _gameplayActionMap;

        // Input buffering
        private float _jumpBufferCounter;
        private float _scanStartTime;

        // Input context
        private InputContext _currentContext = InputContext.Gameplay;

        #endregion

        #region Events

        /// <summary>
        /// Event triggered when input context changes
        /// </summary>
        public event System.Action<InputContext> OnInputContextChanged;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _playerInput = GetComponent<PlayerInput>();
            
            if (_playerInput == null)
            {
                Debug.LogError("[InputHandler] PlayerInput component not found!");
                return;
            }

            // Get the gameplay action map
            _gameplayActionMap = _playerInput.actions.FindActionMap("Player");
            
            if (_gameplayActionMap == null)
            {
                Debug.LogError("[InputHandler] 'Player' action map not found! Make sure your Input Actions asset has a 'Player' action map.");
            }
        }

        private void OnEnable()
        {
            SubscribeToInputActions();
        }

        private void OnDisable()
        {
            UnsubscribeFromInputActions();
        }

        private void Update()
        {
            UpdateInputBuffers();
            ResetFrameInputs();
        }

        #endregion

        #region Input Action Callbacks

        private void SubscribeToInputActions()
        {
            if (_gameplayActionMap == null) return;

            // Movement and camera
            _gameplayActionMap["Move"].performed += OnMove;
            _gameplayActionMap["Move"].canceled += OnMove;
            _gameplayActionMap["CameraLook"].performed += OnCameraLook;
            _gameplayActionMap["CameraLook"].canceled += OnCameraLook;

            // Actions
            _gameplayActionMap["Sprint"].performed += OnSprint;
            _gameplayActionMap["Sprint"].canceled += OnSprint;
            _gameplayActionMap["Jump"].performed += OnJump;
            _gameplayActionMap["Interact"].performed += OnInteract;
            _gameplayActionMap["Aim"].performed += OnAim;
            _gameplayActionMap["Aim"].canceled += OnAim;
            _gameplayActionMap["Scan"].performed += OnScan;
            _gameplayActionMap["Scan"].canceled += OnScan;
            _gameplayActionMap["Stealth"].performed += OnStealth;

            // Inventory
            _gameplayActionMap["CycleInv"].performed += OnCycleInventory;
            _gameplayActionMap["OpenInv"].performed += OnOpenInventory;
        }

        private void UnsubscribeFromInputActions()
        {
            if (_gameplayActionMap == null) return;

            _gameplayActionMap["Move"].performed -= OnMove;
            _gameplayActionMap["Move"].canceled -= OnMove;
            _gameplayActionMap["CameraLook"].performed -= OnCameraLook;
            _gameplayActionMap["CameraLook"].canceled -= OnCameraLook;

            _gameplayActionMap["Sprint"].performed -= OnSprint;
            _gameplayActionMap["Sprint"].canceled -= OnSprint;
            _gameplayActionMap["Jump"].performed -= OnJump;
            _gameplayActionMap["Interact"].performed -= OnInteract;
            _gameplayActionMap["Aim"].performed -= OnAim;
            _gameplayActionMap["Aim"].canceled -= OnAim;
            _gameplayActionMap["Scan"].performed -= OnScan;
            _gameplayActionMap["Scan"].canceled -= OnScan;
            _gameplayActionMap["Stealth"].performed -= OnStealth;

            _gameplayActionMap["CycleInv"].performed -= OnCycleInventory;
            _gameplayActionMap["OpenInv"].performed -= OnOpenInventory;
        }

        // Input callbacks
        private void OnMove(InputAction.CallbackContext context)
        {
            MoveInput = context.ReadValue<Vector2>();
        }

        private void OnCameraLook(InputAction.CallbackContext context)
        {
            LookInput = context.ReadValue<Vector2>();
        }

        private void OnSprint(InputAction.CallbackContext context)
        {
            SprintHeld = context.ReadValueAsButton();

            // If starting to sprint, disable stealth toggle
            if (SprintHeld)
            {
                StealthToggled = false;
            }
        }

        private void OnJump(InputAction.CallbackContext context)
        {
            // Buffer the jump input
            _jumpBufferCounter = Constants.JUMP_BUFFER_TIME;
        }

        private void OnInteract(InputAction.CallbackContext context)
        {
            InteractPressed = true;
        }

        private void OnAim(InputAction.CallbackContext context)
        {
            AimHeld = context.ReadValueAsButton();
        }

        private void OnScan(InputAction.CallbackContext context)
        {
            ScanHeld = context.ReadValueAsButton();
            
            if (ScanHeld)
            {
                _scanStartTime = Time.time;
            }
            else
            {
                ScanHoldTime = 0f;
            }
        }

        private void OnStealth(InputAction.CallbackContext context)
        {
            // Toggle stealth
            StealthToggled = !StealthToggled;
        }

        private void OnCycleInventory(InputAction.CallbackContext context)
        {
            float value = context.ReadValue<float>();

            if (value > 0.1f)
            {
                CycleInventoryForwardPressed = true;
            }
            else if (value < -0.1f)
            {
                CycleInventoryBackwardPressed = true;
            }
        }

        private void OnOpenInventory(InputAction.CallbackContext context)
        {
            OpenInventoryPressed = true;
        }

        #endregion

        #region Input Processing

        /// <summary>
        /// Updates input buffers and timers
        /// </summary>
        private void UpdateInputBuffers()
        {
            // Decrease jump buffer
            if (_jumpBufferCounter > 0f)
            {
                _jumpBufferCounter -= Time.deltaTime;
            }

            // Update scan hold time
            if (ScanHeld)
            {
                ScanHoldTime = Time.time - _scanStartTime;
            }
        }

        /// <summary>
        /// Resets single-frame inputs (pressed/released events)
        /// </summary>
        private void ResetFrameInputs()
        {
            InteractPressed = false;
            CycleInventoryForwardPressed = false;
            CycleInventoryBackwardPressed = false;
            OpenInventoryPressed = false;
        }

        /// <summary>
        /// Consumes the jump buffer (call this when jump is used)
        /// </summary>
        public void ConsumeJumpBuffer()
        {
            _jumpBufferCounter = 0f;
        }

        #endregion

        #region Input Context Management

        /// <summary>
        /// Changes the input context (e.g., switching to UI mode)
        /// </summary>
        public void SetInputContext(InputContext context)
        {
            if (_currentContext == context) return;

            _currentContext = context;

            switch (context)
            {
                case InputContext.Gameplay:
                    _playerInput.SwitchCurrentActionMap("Player");
                    break;
                case InputContext.UI:
                    _playerInput.SwitchCurrentActionMap("UI");
                    break;
                case InputContext.Disabled:
                    _playerInput.DeactivateInput();
                    break;
            }

            OnInputContextChanged?.Invoke(context);
        }

        /// <summary>
        /// Gets the current input context
        /// </summary>
        public InputContext GetCurrentContext()
        {
            return _currentContext;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Checks if there is any movement input
        /// </summary>
        public bool HasMoveInput()
        {
            return MoveInput.sqrMagnitude > 0.01f;
        }

        /// <summary>
        /// Gets the movement input magnitude (0 to 1)
        /// </summary>
        public float GetMoveInputMagnitude()
        {
            return MoveInput.magnitude;
        }

        /// <summary>
        /// Resets all input states (useful when disabling player control)
        /// </summary>
        public void ResetAllInputs()
        {
            MoveInput = Vector2.zero;
            LookInput = Vector2.zero;
            SprintHeld = false;
            _jumpBufferCounter = 0f;
            InteractPressed = false;
            AimHeld = false;
            ScanHeld = false;
            ScanHoldTime = 0f;
            CycleInventoryForwardPressed = false;
            CycleInventoryBackwardPressed = false;
            OpenInventoryPressed = false;
        }

        #endregion
    }
}
