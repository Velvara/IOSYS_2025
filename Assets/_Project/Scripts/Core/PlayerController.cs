using UnityEngine;
using Game.PlayerV2.States;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2
{
    /// <summary>
    /// Main character controller that coordinates all systems and state management
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(Animator))]
    [RequireComponent(typeof(InputHandler))]
    [RequireComponent(typeof(PlayerStamina))]
    public class PlayerController : MonoBehaviour, IControlLock, IPlayerMotor
    {
        #region Inspector Fields

        [Header("References")]
        [Tooltip("Camera transform for camera-relative movement")]
        [SerializeField] private Transform _cameraTransform;

        [Header("Movement")]
        [Tooltip("Speeds, acceleration, rotation, jump/gravity. Defaults match PLAYER_VALUES.md.")]
        [SerializeField] private MovementConfig _movement = new MovementConfig();

        [Header("Ground Detection")]
        [Tooltip("What layers count as ground.")]
        [SerializeField] private LayerMask _groundLayers = 1; // Default layer

        [Tooltip("Vertical offset of the ground-check sphere (forgiving over bumps). Matches the old controller.")]
        [SerializeField] private float _groundedOffset = -0.14f;

        [Tooltip("Radius of the ground-check sphere. Should match the CharacterController radius.")]
        [SerializeField] private float _groundedRadius = 0.28f;

        [Header("Water Detection")]
        [Tooltip("Layer mask for water detection")]
        [SerializeField] private LayerMask _waterLayer;

        [Header("Debug")]
        [Tooltip("Enable debug visualization")]
        [SerializeField] private bool _enableDebug = true;

        #endregion

        #region Component References

        private CharacterController _characterController;
        private Animator _animator;
        private InputHandler _inputHandler;
        private PlayerStamina _playerStamina;
        private PlayerCameraRig _cameraRig;

        #endregion

        #region Systems

        private StateManager _stateManager;
        private PlayerMotor _motor;
        private StaminaSystem _staminaSystem;
        private HealthSystem _healthSystem;
        private InventorySystem _inventorySystem;
        // Camera is a self-contained component (PlayerCameraRig); the controller does not own it.

        #endregion

        #region State Context

        private StateContext _stateContext;

        #endregion

        #region Properties

        /// <summary>
        /// Current state type
        /// </summary>
        public CharacterStateType CurrentState => _stateManager?.CurrentStateType ?? CharacterStateType.Idle;

        /// <summary>
        /// Is the player currently grounded?
        /// </summary>
        public bool IsGrounded { get; private set; }

        /// <summary>
        /// Current velocity
        /// </summary>
        public Vector3 Velocity { get; private set; }

        /// <summary>
        /// Whether the character rotates to face its movement direction. Aim modes set this
        /// to false so the character faces the camera/aim direction instead.
        /// </summary>
        public bool RotateOnMove
        {
            get => _motor != null && _motor.RotateOnMove;
            set { if (_motor != null) _motor.RotateOnMove = value; }
        }

        // ── IPlayerMotor ─────────────────────────────────────────────────────
        public Transform Transform => transform;
        public CharacterController Controller => _characterController;

        // Motor velocity surface (used by external systems like climbing for exit/jump-off).
        public float VerticalVelocity => _motor != null ? _motor.VerticalVelocity : 0f;
        public void SetVerticalVelocity(float v) => _motor?.SetVerticalVelocity(v);
        public void AddLaunchVelocity(Vector3 horizontalWorld, float decayRate) =>
            _motor?.AddLaunchVelocity(horizontalWorld, decayRate);

        // ── IControlLock ─────────────────────────────────────────────────────
        // External systems (hookshot drag, cutscenes) call these to take/return control.
        // While active, the state machine sits in ExternalControl (no locomotion/look).
        public bool IsExternalControlActive { get; private set; }
        public void RequestExternalControl() => IsExternalControlActive = true;
        public void ReleaseExternalControl() => IsExternalControlActive = false;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            InitializeComponents();
            InitializeSystems();
            InitializeStateContext();
            InitializeStates();
        }

        private void Start()
        {
            // Set the initial state
            _stateManager.SetInitialState(CharacterStateType.Idle);
        }

        private void Update()
        {
            // Update environment queries
            UpdateEnvironmentChecks();

            // Update context with current frame data
            UpdateStateContext();

            // Tick stamina BEFORE the state update so states read fresh fatigue this frame.
            // "Sprinting" is derived from INPUT INTENT (not the Sprint state), so a brief
            // airborne frame over a bump doesn't interrupt the stamina/rest-penalty drain —
            // matching the old controller.
            if (_staminaSystem != null)
            {
                bool wantsToSprint = _inputHandler.SprintHeld && !_inputHandler.AimHeld &&
                                     _inputHandler.HasMoveInput();
                bool isSprinting = wantsToSprint && !_staminaSystem.IsFatigued &&
                                   _staminaSystem.CurrentStamina > 0f;
                _staminaSystem.Tick(isSprinting);
            }

            // Update state manager (handles state transitions and updates)
            _stateManager?.Update(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            // Fixed update for physics-based movement
            _stateManager?.FixedUpdate(Time.fixedDeltaTime);
        }

        private void OnDrawGizmos()
        {
            if (!_enableDebug) return;

            DrawDebugVisualization();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes component references
        /// </summary>
        private void InitializeComponents()
        {
            _characterController = GetComponent<CharacterController>();
            _animator = GetComponent<Animator>();
            _inputHandler = GetComponent<InputHandler>();
            _playerStamina = GetComponent<PlayerStamina>();
            _cameraRig = GetComponent<PlayerCameraRig>(); // optional; null if camera is elsewhere

            if (_characterController == null)
                Debug.LogError("[PlayerController] CharacterController component missing!");
            if (_animator == null)
                Debug.LogError("[PlayerController] Animator component missing!");
            if (_inputHandler == null)
                Debug.LogError("[PlayerController] InputHandler component missing!");
            if (_playerStamina == null)
                Debug.LogError("[PlayerController] PlayerStamina component missing!");

            // Find camera if not assigned
            if (_cameraTransform == null)
            {
                _cameraTransform = Camera.main?.transform;
                if (_cameraTransform == null)
                {
                    Debug.LogWarning("[PlayerController] No camera assigned and Camera.main not found!");
                }
            }
        }

        /// <summary>
        /// Initializes game systems
        /// </summary>
        private void InitializeSystems()
        {
            _motor = new PlayerMotor(_characterController, transform, _animator, _cameraTransform, _movement);

            _staminaSystem = new StaminaSystem(_playerStamina);

            // TODO: health/inventory are still placeholders.
            _healthSystem = new HealthSystem();
            _inventorySystem = new InventorySystem();

            Debug.Log("[PlayerController] Systems initialized");
        }

        /// <summary>
        /// Initializes the state context with all necessary references
        /// </summary>
        private void InitializeStateContext()
        {
            _stateContext = new StateContext
            {
                Controller = this,
                CharacterController = _characterController,
                Animator = _animator,
                Transform = transform,
                Motor = _motor,
                CameraRig = _cameraRig,
                Input = _inputHandler,
                Stamina = _staminaSystem,
                Health = _healthSystem,
                Inventory = _inventorySystem
            };
        }

        /// <summary>
        /// Initializes the state manager and registers all states
        /// </summary>
        private void InitializeStates()
        {
            _stateManager = new StateManager(_stateContext);

            // Register Phase 2: Basic Locomotion States
            _stateManager.RegisterState(new IdleState());
            _stateManager.RegisterState(new MoveState());
            _stateManager.RegisterState(new SprintState());
            _stateManager.RegisterState(new JumpState());
            _stateManager.RegisterState(new StealthState());

            // Control states
            _stateManager.RegisterState(new ExternalControlState());

            Debug.Log("[PlayerController] States registered: Idle, Move, Sprint, Jump, Stealth, ExternalControl");
        }

        #endregion

        #region Environment Checks

        /// <summary>
        /// Updates environment detection (ground, water, climbable surfaces)
        /// </summary>
        private void UpdateEnvironmentChecks()
        {
            // Ground check using CharacterController
            //IsGrounded = _characterController.isGrounded;

            // Reflect the controller's actual velocity (the motor moves the controller).
            Velocity = _characterController.velocity;

            // Forgiving ground check (CheckSphere at the feet) — ported from the old
            // controller so the character stays grounded over small bumps.
            Vector3 spherePosition = new Vector3(transform.position.x,
                transform.position.y - _groundedOffset, transform.position.z);
            IsGrounded = Physics.CheckSphere(spherePosition, _groundedRadius, _groundLayers,
                QueryTriggerInteraction.Ignore);

            // Update state context
            _stateContext.IsGrounded = IsGrounded;

            // TODO: Water detection will be implemented in Phase 4
            _stateContext.IsInWater = false;
            _stateContext.WaterLevel = 0f;

            // TODO: Climbable surface detection will be implemented in Phase 6
            _stateContext.IsNearClimbable = false;
            _stateContext.IsNearLedge = false;
            _stateContext.ClimbNormal = Vector3.zero;
            _stateContext.LedgePosition = Vector3.zero;
        }



        #endregion

        #region State Context Updates

        /// <summary>
        /// Updates the state context with current frame data
        /// </summary>
        private void UpdateStateContext()
        {
            // Input
            _stateContext.MoveInput = _inputHandler.MoveInput;
            _stateContext.LookInput = _inputHandler.LookInput;

            // Calculate camera-relative movement direction
            if (_cameraTransform != null)
            {
                _stateContext.MoveDirection = _stateContext.GetCameraRelativeMovement(_cameraTransform);
            }
            else
            {
                // Fallback to world-space input
                _stateContext.MoveDirection = new Vector3(_inputHandler.MoveInput.x, 0f, _inputHandler.MoveInput.y);
            }

            // Velocity
            _stateContext.Velocity = Velocity;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Applies movement to the character (called by states)
        /// </summary>
        public void ApplyMovement(Vector3 movement)
        {
            _characterController.Move(movement);
            Velocity = movement / Time.deltaTime;
        }

        /// <summary>
        /// Sets the velocity directly
        /// </summary>
        public void SetVelocity(Vector3 velocity)
        {
            Velocity = velocity;
            _stateContext.Velocity = velocity;
        }

        /// <summary>
        /// Gets the current state name for debugging
        /// </summary>
        public string GetCurrentStateName()
        {
            return _stateManager?.CurrentState?.StateName ?? "None";
        }

        /// <summary>
        /// Forces a transition to a specific state (for external triggers)
        /// </summary>
        public bool ForceStateTransition(CharacterStateType stateType)
        {
            return _stateManager?.TryTransitionToState(stateType) ?? false;
        }

        /// <summary>
        /// Enables or disables a specific state
        /// </summary>
        public void SetStateEnabled(CharacterStateType stateType, bool enabled)
        {
            _stateManager?.SetStateEnabled(stateType, enabled);
        }

        #endregion

        #region Debug Visualization

        /// <summary>
        /// Draws debug visualization in the scene view
        /// </summary>
        private void DrawDebugVisualization()
        {
            if (_characterController == null) return;

            // Ground check visualization (matches the CheckSphere used at runtime)
            Vector3 spherePosition = new Vector3(transform.position.x,
                transform.position.y - _groundedOffset, transform.position.z);
            Gizmos.color = IsGrounded ? Constants.DEBUG_GROUND_CHECK : Color.red;
            Gizmos.DrawWireSphere(spherePosition, _groundedRadius);

            // Movement direction
            if (_stateContext != null && _stateContext.MoveDirection.sqrMagnitude > 0.01f)
            {
                Gizmos.color = Constants.DEBUG_MOVEMENT_VECTOR;
                Gizmos.DrawRay(transform.position + Vector3.up, _stateContext.MoveDirection * 2f);
            }

            // State info label (requires Handles in editor)
#if UNITY_EDITOR
            if (_stateManager != null)
            {
                UnityEditor.Handles.Label(
                    transform.position + Vector3.up * 3f,
                    _stateManager.GetDebugInfo()
                );
            }
#endif
        }

        #endregion
    }
}
