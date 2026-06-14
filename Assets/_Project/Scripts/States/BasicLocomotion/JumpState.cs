using UnityEngine;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Jump state - character is in the air after jumping
    /// </summary>
    public class JumpState : CharacterStateBase
    {
        #region State Properties

        public override CharacterStateType StateType => CharacterStateType.Jump;
        public override string StateName => "Jump";
        public override StatePriority Priority => StatePriority.BasicLocomotion;

        #endregion

        #region Configuration

        private float _jumpHeight = 3.5f;
        private float _airControlSpeed = 3f;
        private float _airTurnSpeed = 360f;
        private float _maxFallTime = 2f; // Safety timeout

        #endregion

        #region Private Fields

        private float _timeInAir;
        private bool _isDescending;

        #endregion

        #region Constructors

        public JumpState() : base()
        {
        }

        public JumpState(StateConfigSO config) : base(config)
        {
            if (config != null)
            {
                _airControlSpeed = config.moveSpeed * 0.6f; // 60% of ground speed
                _airTurnSpeed = config.turnSpeed * 0.5f; // 50% of ground turn speed
            }
        }

        #endregion

        #region State Lifecycle

        public override void OnEnter(StateContext context)
        {
            base.OnEnter(context);

            // Calculate jump velocity using physics formula: v = sqrt(2 * g * h)
            float jumpVelocity = Mathf.Sqrt(2 * Mathf.Abs(Constants.GRAVITY) * _jumpHeight);

            // Set vertical velocity
            Vector3 velocity = _context.Velocity;
            velocity.y = jumpVelocity;
            _context.Velocity = velocity;

            // Trigger jump animation
            _context.Animator.SetTriggerSafe(Constants.ANIM_JUMP);
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_GROUNDED, false);

            // Reset time tracking
            _timeInAir = 0f;
            _isDescending = false;
        }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            // Track time in air
            _timeInAir += deltaTime;

            // Debug: Log velocity every 0.5 seconds
            if ((int)(_timeInAir * 2) != (int)((_timeInAir - deltaTime) * 2))
            {
                Debug.Log($"[Jump] Time: {_timeInAir:F2}s | Y Velocity: {_context.Velocity.y:F2} | Grounded: {_context.IsGrounded}");
            }

            // Check if we've started descending
            if (!_isDescending && _context.Velocity.y < 0f)
            {
                _isDescending = true;
            }

            // Update animator
            UpdateAnimator();
        }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            // Allow limited air control
            if (_context.HasMoveInput())
            {
                // Calculate air movement
                Vector3 airMovement = _context.MoveDirection * _airControlSpeed;

                // Apply air control to horizontal velocity
                Vector3 horizontalVelocity = new Vector3(_context.Velocity.x, 0f, _context.Velocity.z);
                horizontalVelocity = Vector3.MoveTowards(
                    horizontalVelocity,
                    airMovement,
                    _airControlSpeed * fixedDeltaTime
                );

                // Combine with vertical velocity
                _context.Velocity = new Vector3(horizontalVelocity.x, _context.Velocity.y, horizontalVelocity.z);

                // Rotate towards movement (slower than on ground)
                RotateTowardsMovement(_airTurnSpeed, fixedDeltaTime);
            }

            // Apply gravity directly - don't use ApplyGravity helper
            _context.Velocity += Vector3.up * Constants.GRAVITY * fixedDeltaTime;

            if (_context.CharacterController != null)
            {
                _context.CharacterController.Move(_context.Velocity * fixedDeltaTime);
            }
        }

        public override void OnExit()
        {
            base.OnExit();

            // Reset animator flags
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_GROUNDED, true);
        }

        #endregion

        #region Transitions

        public override bool CanEnterState(StateContext context)
        {
            // Can only jump if grounded
            return context.IsGrounded;
        }

        public override bool CanExitState()
        {
            // Can exit once we've landed or timeout
            return _context.IsGrounded || _timeInAir >= _maxFallTime;
        }

        public override CharacterStateType CheckTransitions(StateContext context)
        {
            // Safety: Force exit if in air too long
            if (_timeInAir >= _maxFallTime)
            {
                Debug.LogWarning("[JumpState] Max fall time exceeded, forcing ground state");
                return CharacterStateType.Idle;
            }

            // Check if we've landed - use velocity change detection instead of IsGrounded
            // Landing = we're descending AND velocity is close to zero (hit something)
            bool hasLanded = _isDescending &&
                             _timeInAir > 0.15f &&
                             context.IsGrounded &&
                             Mathf.Abs(context.Velocity.y) < 2f;

            if (hasLanded)
            {
                // Transition based on input
                if (context.Input.SprintHeld && context.HasMoveInput() && HasStamina())
                {
                    return CharacterStateType.Sprint;
                }
                else if (context.Input.StealthToggled)
                {
                    return CharacterStateType.Stealth;
                }
                else if (context.HasMoveInput())
                {
                    return CharacterStateType.Move;
                }
                else
                {
                    return CharacterStateType.Idle;
                }
            }

            // Stay in jump state while airborne
            return StateType;
        }

        #endregion

        #region Camera Settings

        public override CameraSettings GetCameraSettings()
        {
            // Use default camera settings for jumping
            return base.GetCameraSettings();
        }

        #endregion
    }
}
