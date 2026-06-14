using UnityEngine;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Move state - character is walking/running with directional input
    /// </summary>
    public class MoveState : CharacterStateBase
    {
        #region State Properties

        public override CharacterStateType StateType => CharacterStateType.Move;
        public override string StateName => "Move";
        public override StatePriority Priority => StatePriority.BasicLocomotion;

        #endregion

        #region Configuration

        private float _moveSpeed = 5f;
        private float _acceleration = 10f;
        private float _deceleration = 10f;
        private float _turnSpeed = 720f;
        private float _staminaRecoveryRate = 5f;

        #endregion

        #region Constructors

        public MoveState() : base()
        {
        }

        public MoveState(StateConfigSO config) : base(config)
        {
            if (config != null)
            {
                _moveSpeed = config.moveSpeed;
                _acceleration = config.acceleration;
                _deceleration = config.deceleration;
                _turnSpeed = config.turnSpeed;
                _staminaRecoveryRate = config.staminaRecoveryRate;
            }
        }

        #endregion

        #region State Lifecycle

        public override void OnEnter(StateContext context)
        {
            base.OnEnter(context);

            // Set animator
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_SPRINTING, false);
        }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            // Recover stamina while moving (slower than idle)
            if (_context.Stamina != null && _staminaRecoveryRate > 0f)
            {
                _context.Stamina.RecoverStamina(_staminaRecoveryRate * deltaTime);
            }

            // Update animator
            UpdateAnimator();
        }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            // Calculate target speed based on input magnitude
            float inputMagnitude = _context.MoveInput.magnitude;
            float targetSpeed = _moveSpeed * Mathf.Clamp01(inputMagnitude);

            // Smoothly interpolate current speed
            if (inputMagnitude > 0.1f)
            {
                _context.CurrentSpeed = Mathf.MoveTowards(
                    _context.CurrentSpeed,
                    targetSpeed,
                    _acceleration * fixedDeltaTime
                );
            }
            else
            {
                _context.CurrentSpeed = Mathf.MoveTowards(
                    _context.CurrentSpeed,
                    0f,
                    _deceleration * fixedDeltaTime
                );
            }

            // Apply movement
            if (_context.MoveDirection.sqrMagnitude > 0.01f)
            {
                // Move in the camera-relative direction
                Vector3 movement = _context.MoveDirection * _context.CurrentSpeed;
                
                // Keep vertical velocity (for gravity/slopes)
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

        #endregion

        #region Transitions

        public override bool CanEnterState(StateContext context)
        {
            // Can enter if there's movement input
            return context.HasMoveInput();
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

            // Check for sprint (sprint button + has stamina)
            if (context.Input.SprintHeld && HasStamina())
            {
                return CharacterStateType.Sprint;
            }

            // Check for stealth toggle
            if (context.Input.StealthToggled)
            {
                return CharacterStateType.Stealth;
            }

            // Check if stopped moving
            if (!context.HasMoveInput())
            {
                return CharacterStateType.Idle;
            }

            // Stay in move state
            return StateType;
        }

        #endregion

        #region Camera Settings

        public override CameraSettings GetCameraSettings()
        {
            return base.GetCameraSettings();
        }

        #endregion
    }
}
