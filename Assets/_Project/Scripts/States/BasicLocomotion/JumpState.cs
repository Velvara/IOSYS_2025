namespace Game.PlayerV2.States
{
    /// <summary>
    /// Airborne state - active while the character is off the ground, whether from a jump
    /// (the grounded state applied the impulse via Motor.BeginJump) or from walking off a
    /// ledge. Provides full air control via the motor and lands back into a ground state.
    /// </summary>
    public class JumpState : CharacterStateBase
    {
        public override CharacterStateType StateType => CharacterStateType.Jump;
        public override string StateName => "Jump";
        public override StatePriority Priority => StatePriority.BasicLocomotion;

        // Minimum airborne time before a landing can be detected (avoids landing on the
        // takeoff frame while still grounded) and a safety timeout.
        private const float _minAirTime = 0.1f;
        private const float _maxAirTime = 5f;

        public JumpState() : base() { }
        public JumpState(StateConfigSO config) : base(config) { }

        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);

            // Full air control: sprint speed if sprint is held (and not aiming), else run speed.
            bool sprinting = _context.Input.SprintHeld && !_context.Input.AimHeld;
            float targetSpeed = sprinting ? _context.Motor.SprintSpeed : _context.Motor.RunSpeed;

            _context.Motor.TickAir(_context.MoveInput, targetSpeed, sprinting, _context.IsGrounded, deltaTime);
        }

        public override void OnFixedUpdate(float fixedDeltaTime) { }

        public override bool CanEnterState(StateContext context) => true;

        public override bool CanExitState() =>
            _context.IsGrounded || _context.TimeInState >= _maxAirTime;

        public override CharacterStateType CheckTransitions(StateContext context)
        {
            // Safety: forced exit if airborne too long.
            if (context.TimeInState >= _maxAirTime)
                return CharacterStateType.Idle;

            // Landed: grounded, descending, and past the takeoff frame.
            bool landed = context.IsGrounded &&
                          context.Motor.VerticalVelocity <= 0f &&
                          context.TimeInState > _minAirTime;

            if (!landed)
                return StateType;

            // Choose the ground state from current input.
            bool fatigued = context.Stamina != null && context.Stamina.IsFatigued;
            if (context.Input.SprintHeld && context.HasMoveInput() &&
                !context.Input.AimHeld && HasStamina(context) && !fatigued)
                return CharacterStateType.Sprint;

            if (context.Input.StealthToggled)
                return CharacterStateType.Stealth;

            if (context.HasMoveInput())
                return CharacterStateType.Move;

            return CharacterStateType.Idle;
        }
    }
}
