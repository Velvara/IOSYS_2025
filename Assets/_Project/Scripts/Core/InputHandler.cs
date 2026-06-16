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
        /// Is aim button held? Used by the controller to suppress sprint while aiming.
        /// Item use / fire / scan are handled by the aim systems reading PlayerInput directly.
        /// </summary>
        public bool AimHeld { get; private set; }

        /// <summary>
        /// Is stealth toggled on?
        /// </summary>
        public bool StealthToggled { get; private set; }

        /// <summary>
        /// Is the active control scheme keyboard & mouse? The camera uses this to skip
        /// the per-frame deltaTime scaling gamepad look needs (mouse delta is already
        /// frame-rate independent). Scheme name matches StarterAssets.inputactions.
        /// </summary>
        public bool IsCurrentDeviceMouse =>
            _playerInput != null && _playerInput.currentControlScheme == "KeyboardMouse";

        #endregion

        #region Private Fields

        private PlayerInput _playerInput;
        private InputActionMap _gameplayActionMap;

        [Tooltip("Lock and hide the hardware cursor (needed for mouse look).")]
        [SerializeField] private bool _lockCursor = true;

        // Input buffering
        private float _jumpBufferCounter;

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

        private void Start()
        {
            ApplyCursorLock();
        }

        private void Update()
        {
            UpdateInputBuffers();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus) ApplyCursorLock();
        }

        private void ApplyCursorLock()
        {
            Cursor.lockState = _lockCursor ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !_lockCursor;
        }

        #endregion

        #region Input Action Callbacks

        private void SubscribeToInputActions()
        {
            if (_gameplayActionMap == null) return;

            // Movement and camera
            _gameplayActionMap["Move"].performed += OnMove;
            _gameplayActionMap["Move"].canceled += OnMove;
            _gameplayActionMap["Look"].performed += OnLook;
            _gameplayActionMap["Look"].canceled += OnLook;

            // Actions (locomotion-relevant only; item use/cycling are owned by the
            // aim/inventory systems, which subscribe to PlayerInput directly)
            _gameplayActionMap["Sprint"].performed += OnSprint;
            _gameplayActionMap["Sprint"].canceled += OnSprint;
            _gameplayActionMap["Jump"].performed += OnJump;
            _gameplayActionMap["Aim"].performed += OnAim;
            _gameplayActionMap["Aim"].canceled += OnAim;
            _gameplayActionMap["Stealth"].performed += OnStealth;
        }

        private void UnsubscribeFromInputActions()
        {
            if (_gameplayActionMap == null) return;

            _gameplayActionMap["Move"].performed -= OnMove;
            _gameplayActionMap["Move"].canceled -= OnMove;
            _gameplayActionMap["Look"].performed -= OnLook;
            _gameplayActionMap["Look"].canceled -= OnLook;

            _gameplayActionMap["Sprint"].performed -= OnSprint;
            _gameplayActionMap["Sprint"].canceled -= OnSprint;
            _gameplayActionMap["Jump"].performed -= OnJump;
            _gameplayActionMap["Aim"].performed -= OnAim;
            _gameplayActionMap["Aim"].canceled -= OnAim;
            _gameplayActionMap["Stealth"].performed -= OnStealth;
        }

        // Input callbacks
        private void OnMove(InputAction.CallbackContext context)
        {
            MoveInput = context.ReadValue<Vector2>();
        }

        private void OnLook(InputAction.CallbackContext context)
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

        private void OnAim(InputAction.CallbackContext context)
        {
            AimHeld = context.ReadValueAsButton();
        }

        private void OnStealth(InputAction.CallbackContext context)
        {
            // Toggle stealth
            StealthToggled = !StealthToggled;
        }

        /// <summary>
        /// Externally sets the stealth toggle (e.g. jumping/sprinting cancels stealth).
        /// </summary>
        public void SetStealth(bool on)
        {
            StealthToggled = on;
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
                    if (_playerInput.actions.FindActionMap("UI") != null)
                        _playerInput.SwitchCurrentActionMap("UI");
                    else
                        Debug.LogWarning("[InputHandler] No 'UI' action map in the input asset; UI context ignored.");
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
            AimHeld = false;
            StealthToggled = false;
        }

        #endregion
    }
}
