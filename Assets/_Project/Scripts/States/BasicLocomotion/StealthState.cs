using UnityEngine;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Stealth state - character is moving slowly and crouched
    /// </summary>
    public class StealthState : CharacterStateBase
    {
        #region State Properties

        public override CharacterStateType StateType => CharacterStateType.Stealth;
        public override string StateName => "Stealth";
        public override StatePriority Priority => StatePriority.BasicLocomotion;

        #endregion

        #region Configuration

        private float _stealthSpeed = 2.5f;
        private float _acceleration = 8f;
        private float _deceleration = 10f;
        private float _turnSpeed = 540f;
        private float _staminaRecoveryRate = 7f; // Slower recovery than idle, faster than move

        #endregion

        #region Constructors

        public StealthState() : base()
        {
        }

        public StealthState(StateConfigSO config) : base(config)
        {
            if (config != null)
            {
                _stealthSpeed = config.moveSpeed;
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

            // Set stealth animator flag
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_STEALTH, true);
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_SPRINTING, false);
        }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            // Recover stamina while in stealth (moderate rate)
            if (_context.Stamina != null && _staminaRecoveryRate > 0f)
            {
                _context.Stamina.RecoverStamina(_staminaRecoveryRate * deltaTime);
            }

            // Update animator
            UpdateAnimator();
        }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            // Calculate target speed based on input
            float inputMagnitude = _context.MoveInput.magnitude;
            float targetSpeed = _stealthSpeed * Mathf.Clamp01(inputMagnitude);

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
                
                // Keep vertical velocity
                movement.y = _context.Velocity.y;
                
                _context.Velocity = movement;

                // Rotate character towards movement direction
                RotateTowardsMovement(_turnSpeed, fixedDeltaTime);
            }
            else
            {
                // Idle in stealth - reduce horizontal velocity
                Vector3 velocity = _context.Velocity;
                velocity.x = 0f;
                velocity.z = 0f;
                _context.Velocity = velocity;
            }

            // Apply gravity
            ApplyGravity(fixedDeltaTime);

            // Apply final movement
            ApplyMovement(_context.Velocity, fixedDeltaTime);
        }

        public override void OnExit()
        {
            base.OnExit();

            // Clear stealth flag
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_STEALTH, false);
        }

        #endregion

        #region Transitions

        public override bool CanEnterState(StateContext context)
        {
            // Can enter stealth if the toggle is active
            return context.Input.StealthToggled;
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

            // Check for sprint - exits stealth and enters sprint
            if (context.Input.SprintHeld && context.HasMoveInput() && HasStamina())
            {
                return CharacterStateType.Sprint;
            }

            // Check if stealth was toggled off
            if (!context.Input.StealthToggled)
            {
                // Exit stealth - transition based on input
                if (context.HasMoveInput())
                {
                    return CharacterStateType.Move;
                }
                else
                {
                    return CharacterStateType.Idle;
                }
            }

            // Stay in stealth state
            return StateType;
        }

        #endregion

        #region Camera Settings

        public override CameraSettings GetCameraSettings()
        {
            // Use stealth camera preset (closer, lower)
            if (_config != null && _config.cameraSettings != null)
            {
                return base.GetCameraSettings();
            }
            
            return CameraSettings.Stealth;
        }

        #endregion
    }
}
