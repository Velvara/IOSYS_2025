using UnityEngine;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Base class for all character states, implementing common functionality
    /// Derive from this class to create new states
    /// </summary>
    public abstract class CharacterStateBase : ICharacterState
    {
        #region Properties

        public abstract CharacterStateType StateType { get; }
        public abstract string StateName { get; }
        public bool IsEnabled { get; set; } = true;
        public abstract StatePriority Priority { get; }

        #endregion

        #region Protected Fields

        /// <summary>
        /// Cached reference to the state context
        /// </summary>
        protected StateContext _context;

        /// <summary>
        /// Configuration settings for this state (can be null if not using ScriptableObject config)
        /// </summary>
        protected StateConfigSO _config;

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor
        /// </summary>
        protected CharacterStateBase()
        {
        }

        /// <summary>
        /// Constructor with configuration
        /// </summary>
        /// <param name="config">State configuration ScriptableObject</param>
        protected CharacterStateBase(StateConfigSO config)
        {
            _config = config;
            if (_config != null)
            {
                IsEnabled = _config.enabledByDefault;
            }
        }

        #endregion

        #region ICharacterState Implementation

        public virtual void OnEnter(StateContext context)
        {
            _context = context;
            _context.ResetTimeData();

            // Apply camera settings if available
            if (_context.CameraManager != null)
            {
                _context.CameraManager.ApplySettings(GetCameraSettings());
            }

            // Debug log
            if (Application.isEditor)
            {
                Debug.Log($"[State] Entering: {StateName}");
            }
        }

        public virtual void OnUpdate(float deltaTime)
        {
            _context.TimeInState += deltaTime;
            _context.DeltaTime = deltaTime;
        }

        public abstract void OnFixedUpdate(float fixedDeltaTime);

        public virtual void OnExit()
        {
            // Debug log
            if (Application.isEditor)
            {
                Debug.Log($"[State] Exiting: {StateName}");
            }
        }

        public abstract bool CanEnterState(StateContext context);

        public virtual bool CanExitState()
        {
            // By default, states can be exited at any time
            // Override this in states that have restrictions (e.g., animation locks)
            return true;
        }

        public abstract CharacterStateType CheckTransitions(StateContext context);

        public virtual CameraSettings GetCameraSettings()
        {
            // Return default camera settings if no config is set
            if (_config != null && _config.cameraSettings != null)
            {
                return new CameraSettings(_config.cameraSettings);
            }

            return CameraSettings.Default;
        }

        #endregion

        #region Protected Helper Methods

        /// <summary>
        /// Applies movement to the character controller
        /// </summary>
        protected void ApplyMovement(Vector3 movement, float deltaTime)
        {
            if (_context.CharacterController != null)
            {
                _context.CharacterController.Move(movement * deltaTime);
            }
        }

        /// <summary>
        /// Applies gravity to velocity
        /// </summary>
        protected void ApplyGravity(float deltaTime)
        {
            // Always apply gravity
            _context.Velocity += Vector3.up * Constants.GRAVITY * deltaTime;

            // If grounded and moving downward, clamp to small value to stay grounded
            if (_context.IsGrounded && _context.Velocity.y < 0f)
            {
                _context.Velocity = new Vector3(_context.Velocity.x, -2f, _context.Velocity.z);
            }
        }

        /// <summary>
        /// Updates animator parameters with current movement data
        /// </summary>
        protected void UpdateAnimator()
        {
            if (_context.Animator == null) return;

            // Normalize speed based on current max speed
            float normalizedSpeed = _context.CurrentSpeed / Constants.DEFAULT_MOVE_SPEED;
            _context.Animator.SetFloatSafe(Constants.ANIM_SPEED, normalizedSpeed);

            // Set vertical velocity for jump/fall animations
            _context.Animator.SetFloatSafe(Constants.ANIM_VERTICAL_VELOCITY, _context.Velocity.y);

            // Set grounded state
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_GROUNDED, _context.IsGrounded);
        }

        /// <summary>
        /// Rotates the character towards the movement direction
        /// </summary>
        protected void RotateTowardsMovement(float turnSpeed, float deltaTime)
        {
            if (_context.MoveDirection.sqrMagnitude < 0.01f) return;

            Quaternion targetRotation = Quaternion.LookRotation(_context.MoveDirection);
            _context.Transform.rotation = Quaternion.RotateTowards(
                _context.Transform.rotation,
                targetRotation,
                turnSpeed * deltaTime
            );
        }

        /// <summary>
        /// Checks if stamina is available for this state
        /// </summary>
        protected bool HasStamina()
        {
            return _context.Stamina != null && !_context.Stamina.IsDepleted;
        }

        /// <summary>
        /// Checks common high-priority state transitions (Drowned, Drained states)
        /// Returns the state type to transition to, or current state if no transition
        /// </summary>
        protected CharacterStateType CheckCommonTransitions()
        {
            // Critical: Check for drowned state
            if (_context.IsInWater && _context.Stamina.IsDepleted)
            {
                return CharacterStateType.Drowned;
            }

            // Check for drained states
            if (_context.Stamina.IsFullyDepleted)
            {
                if (_context.HasMoveInput())
                {
                    return CharacterStateType.DrainedMove;
                }
                else
                {
                    return CharacterStateType.DrainedIdle;
                }
            }

            return StateType; // No transition
        }

        #endregion
    }
}
