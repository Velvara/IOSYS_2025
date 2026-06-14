using UnityEngine;
using Game.PlayerV2.Systems;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Idle state - character is standing still and recovering stamina
    /// </summary>
    public class IdleState : CharacterStateBase
    {
        #region State Properties

        public override CharacterStateType StateType => CharacterStateType.Idle;
        public override string StateName => "Idle";
        public override StatePriority Priority => StatePriority.BasicLocomotion;

        #endregion

        #region Configuration

        private float _staminaRecoveryRate = 10f;

        #endregion

        #region Constructors

        public IdleState() : base()
        {
        }

        public IdleState(StateConfigSO config) : base(config)
        {
            if (config != null)
            {
                _staminaRecoveryRate = config.staminaRecoveryRate;
            }
        }

        #endregion

        #region State Lifecycle

        public override void OnEnter(StateContext context)
        {
            base.OnEnter(context);

            // Set animator to idle
            _context.Animator.SetFloatSafe(Constants.ANIM_SPEED, 0f);
            _context.Animator.SetBoolSafe(Constants.ANIM_IS_SPRINTING, false);
            
            // Reset velocity
            _context.Velocity = Vector3.zero;
            _context.CurrentSpeed = 0f;
        }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            // Recover stamina while idle
            if (_context.Stamina != null && _staminaRecoveryRate > 0f)
            {
                _context.Stamina.RecoverStamina(_staminaRecoveryRate * deltaTime);
            }

            // Update animator
            UpdateAnimator();
        }

        public override void OnFixedUpdate(float fixedDeltaTime)
        {
            // Apply gravity to keep grounded
            ApplyGravity(fixedDeltaTime);

            // Apply minimal downward movement
            ApplyMovement(_context.Velocity, fixedDeltaTime);
        }

        #endregion

        #region Transitions

        public override bool CanEnterState(StateContext context)
        {
            // Can always enter idle state
            return true;
        }

        public override bool CanExitState()
        {
            // Can always exit idle
            return true;
        }

        public override CharacterStateType CheckTransitions(StateContext context)
        {
            // Check common high-priority transitions first (Drowned, Drained)
            CharacterStateType commonTransition = CheckCommonTransitions();
            if (commonTransition != StateType)
                return commonTransition;

            // Check for jump
            if (context.Input.JumpPressed && context.IsGrounded)
            {
                context.Input.ConsumeJumpBuffer();
                return CharacterStateType.Jump;
            }

            // Check for sprint (requires move input + sprint held + stamina)
            if (context.Input.SprintHeld && context.HasMoveInput() && HasStamina())
            {
                return CharacterStateType.Sprint;
            }

            // Check for stealth toggle
            if (context.Input.StealthToggled)
            {
                return CharacterStateType.Stealth;
            }

            // Check for movement
            if (context.HasMoveInput())
            {
                return CharacterStateType.Move;
            }

            // Stay in idle
            return StateType;
        }

        #endregion

        #region Camera Settings

        public override CameraSettings GetCameraSettings()
        {
            // Use default camera settings or config settings
            return base.GetCameraSettings();
        }

        #endregion
    }
}
