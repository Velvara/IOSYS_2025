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
    public class PlayerController : MonoBehaviour
    {
        #region Inspector Fields

        [Header("References")]
        [Tooltip("Camera transform for camera-relative movement")]
        [SerializeField] private Transform _cameraTransform;

        [Header("Ground Detection")]
        [Tooltip("Layer mask for ground detection")]
        [SerializeField] private LayerMask _groundLayer = 1; // Default layer

        [Tooltip("Extra distance for ground check")]
        [SerializeField] private float _groundCheckDistance = 0.2f;

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

        #endregion

        #region Systems

        private StateManager _stateManager;
        private StaminaSystem _staminaSystem;
        private HealthSystem _healthSystem;
        private InventorySystem _inventorySystem;
        private CameraManager _cameraManager;

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

            // Initialize camera if available
            if (_cameraManager != null && _cameraTransform != null)
            {
                _cameraManager.Initialize(_cameraTransform, transform);
            }
        }

        private void Update()
        {
            // Update environment queries
            UpdateEnvironmentChecks();

            // Debug falling
            if (!IsGrounded && Application.isEditor)
            {
                Debug.Log($"FALLING - Y Velocity: {Velocity.y:F2} | Grounded: {IsGrounded} | State: {GetCurrentStateName()}");
            }

            // Update context with current frame data
            UpdateStateContext();

            // Update state manager (handles state transitions and updates)
            _stateManager?.Update(Time.deltaTime);

            // Update systems
            _staminaSystem?.Update(Time.deltaTime);
            _cameraManager?.Update(Time.deltaTime);
        }

        private void FixedUpdate()
        {
            // Fixed update for physics-based movement
            _stateManager?.FixedUpdate(Time.fixedDeltaTime);
        }

        private void LateUpdate()
        {
            // Camera updates after all movement
            _cameraManager?.LateUpdate(Time.deltaTime);
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

            if (_characterController == null)
                Debug.LogError("[PlayerController] CharacterController component missing!");
            if (_animator == null)
                Debug.LogError("[PlayerController] Animator component missing!");
            if (_inputHandler == null)
                Debug.LogError("[PlayerController] InputHandler component missing!");

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
            // TODO: These will be properly initialized in Phase 3
            _staminaSystem = new StaminaSystem();
            _healthSystem = new HealthSystem();
            _inventorySystem = new InventorySystem();
            _cameraManager = new CameraManager();

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
                Input = _inputHandler,
                Stamina = _staminaSystem,
                Health = _healthSystem,
                Inventory = _inventorySystem,
                CameraManager = _cameraManager
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

            Debug.Log("[PlayerController] Phase 2 states registered: Idle, Move, Sprint, Jump, Stealth");
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

            // Enhanced ground check with sphere cast
            Vector3 origin = transform.position + _characterController.center;
            float radius = _characterController.radius * 0.9f;
            float distance = (_characterController.height / 2f) - radius + _groundCheckDistance;

            bool spherecastGrounded = Physics.SphereCast(origin, radius, Vector3.down, out _, distance, _groundLayer);

            // Only consider grounded if spherecast hits AND not falling fast
            IsGrounded = spherecastGrounded && Velocity.y > -10f;

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

            // Ground check visualization
            Vector3 origin = transform.position + _characterController.center;
            float radius = _characterController.radius * 0.9f;
            float distance = (_characterController.height / 2f) - radius + _groundCheckDistance;

            Gizmos.color = IsGrounded ? Constants.DEBUG_GROUND_CHECK : Color.red;
            Gizmos.DrawWireSphere(origin + Vector3.down * distance, radius);
            Gizmos.DrawLine(origin, origin + Vector3.down * distance);

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
