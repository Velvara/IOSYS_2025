namespace Game.PlayerV2.States
{
    /// <summary>
    /// Base for grounded locomotion states (Idle / Move / Sprint / Stealth). Drives the
    /// shared <see cref="PlayerMotor"/> each frame with this state's target speed and flags,
    /// and handles the transitions common to all of them (jump, falling off a ledge).
    /// Subclasses only declare their target speed and their ground-to-ground transitions.
    /// </summary>
    public abstract class GroundedLocomotionState : CharacterStateBase
    {
        protected GroundedLocomotionState() : base() { }
        protected GroundedLocomotionState(StateConfigSO config) : base(config) { }

        public override StatePriority Priority => StatePriority.BasicLocomotion;

        /// <summary>Target horizontal speed for this state (m/s). 0 = idle.</summary>
        protected abstract float GetTargetSpeed(StateContext context);

        /// <summary>Sprint ignores stick-magnitude scaling (always full speed).</summary>
        protected virtual bool IgnoresStickScaling => false;

        /// <summary>Animator Sprint flag while in this state.</summary>
        protected virtual bool SprintFlag => false;

        /// <summary>Animator Stealth flag while in this state.</summary>
        protected virtual bool StealthFlag => false;

        /// <summary>Ground-to-ground transition decision (subclass policy).</summary>
        protected abstract CharacterStateType GetGroundedTransition(StateContext context);

        protected static bool IsFatigued(StateContext context) =>
            context.Stamina != null && context.Stamina.IsFatigued;

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            bool fatigued = IsFatigued(_context);
            // Fatigue overrides speed and suppresses the sprint flag.
            float targetSpeed = fatigued ? _context.Motor.FatiguedSpeed : GetTargetSpeed(_context);

            _context.Motor.TickGrounded(
                _context.MoveInput,
                targetSpeed,
                IgnoresStickScaling && !fatigued,
                SprintFlag && !fatigued,
                StealthFlag,
                fatigued,
                deltaTime);
        }

        // Movement runs in OnUpdate (variable timestep, like the StarterAssets controller).
        public override void OnFixedUpdate(float fixedDeltaTime) { }

        public override CharacterStateType CheckTransitions(StateContext context)
        {
            // Jump: grounded + buffered jump + cooldown elapsed + not fatigued.
            if (context.Input.JumpPressed && context.IsGrounded &&
                context.Motor.CanJump && !IsFatigued(context))
            {
                context.Input.ConsumeJumpBuffer();
                context.Input.SetStealth(false); // jumping cancels stealth
                context.Motor.BeginJump();
                return CharacterStateType.Jump;
            }

            // Walked off a ledge → airborne (no jump impulse).
            if (!context.IsGrounded)
                return CharacterStateType.Jump;

            return GetGroundedTransition(context);
        }
    }
}
