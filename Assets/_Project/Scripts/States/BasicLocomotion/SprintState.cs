using UnityEngine;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Sprint state - character is running at high speed while draining stamina
    /// </summary>
    public class SprintState : CharacterStateBase
    {
        #region State Properties

        public override CharacterStateType StateType => CharacterStateType.Sprint;
        public override string StateName => "Sprint";
        public override StatePriority Priority => StatePriority.BasicLocomotion;

        #endregion

        #region Configuration

        private float _sprintSpeed = 8f;
        private float _acceleration = 12f;
        private float _deceleration = 10f;
        private float _turnSpeed = 540f; // Slightly slower turn while sprinting
        private float _staminaDrainRate = 15f;

        #endregion

        #region Constructors

        public SprintState() : base()
        {
        }

        public SprintState(StateConfigSO config) : base(config)
        {
            if (config != null)
            {
                _sprintSpeed = config.moveSpeed;
                _acceleration = config.acceleration;
                _deceleration = config.deceleration;
                _turnSpeed = config.turnSpeed;
                _staminaDrainRate = config.staminaDrainRate;
            }
        }

        #endregion

        #region State Lifecycle

        public override void OnEnter(StateContext context)
        {
            base.OnEnter(context);

            // Set animator to sprinting
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_SPRINTING, true);
        }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            // Drain stamina while sprinting
            if (_context.Stamina != null && _staminaDrainRate > 0f)
            {
                _context.Stamina.DrainStamina(_staminaDrainRate * deltaTime);
            }

            // Update animator
            UpdateAnimator();
        }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            // Calculate target speed
            float inputMagnitude = _context.MoveInput.magnitude;
            float targetSpeed = _sprintSpeed * Mathf.Clamp01(inputMagnitude);

            // Accelerate to sprint speed
            _context.CurrentSpeed = Mathf.MoveTowards(
                _context.CurrentSpeed,
                targetSpeed,
                _acceleration * fixedDeltaTime
            );

            // Apply movement
            if (_context.MoveDirection.sqrMagnitude > 0.01f)
            {
                // Move in the camera-relative direction
                Vector3 movement = _context.MoveDirection * _context.CurrentSpeed;
                
                // Keep vertical velocity
                movement.y = _context.Velocity.y;
                
                _context.Velocity = movement;

                // Rotate character towards movement direction
                RotateTowardsMovement(_turnSpeed, fixedDeltaTime);
            }

            // Apply gravity
            ApplyGravity(fixedDeltaTime);

            // Apply final movement
            ApplyMovement(_context.Velocity, fixedDeltaTime);
        }

        public override void OnExit()
        {
            base.OnExit();

            // Clear sprinting flag
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_SPRINTING, false);
        }

        #endregion

        #region Transitions

        public override bool CanEnterState(StateContext context)
        {
            // Can only sprint if:
            // - Has movement input
            // - Sprint button is held
            // - Has stamina
            return context.HasMoveInput() && 
                   context.Input.SprintHeld && 
                   context.Stamina != null && 
                   !context.Stamina.IsDepleted;
        }

        public override bool CanExitState()
        {
            return true;
        }

        public override CharacterStateType CheckTransitions(StateContext context)
        {
            // Check common high-priority transitions first
            CharacterStateType commonTransition = CheckCommonTransitions();
            if (commonTransition != StateType)
                return commonTransition;

            // Check for jump
            if (context.Input.JumpPressed && context.IsGrounded)
            {
                context.Input.ConsumeJumpBuffer();
                return CharacterStateType.Jump;
            }

            // Check if stamina depleted - transition to move
            if (!HasStamina())
            {
                return CharacterStateType.Move;
            }

            // Check if sprint button released - transition to move
            if (!context.Input.SprintHeld)
            {
                return CharacterStateType.Move;
            }

            // Check for stealth toggle
            if (context.Input.StealthToggled)
            {
                return CharacterStateType.Stealth;
            }

            // Check if stopped moving - transition to idle
            if (!context.HasMoveInput())
            {
                return CharacterStateType.Idle;
            }

            // Stay in sprint state
            return StateType;
        }

        #endregion

        #region Camera Settings

        public override CameraSettings GetCameraSettings()
        {
            // Use sprint camera preset (pulled back, wider FOV)
            if (_config != null && _config.cameraSettings != null)
            {
                return base.GetCameraSettings();
            }
            
            return CameraSettings.Sprint;
        }

        #endregion
    }
}
