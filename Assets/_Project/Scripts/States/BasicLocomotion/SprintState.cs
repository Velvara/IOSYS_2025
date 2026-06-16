namespace Game.PlayerV2.States
{
    /// <summary>
    /// Sprint - grounded run at full SprintSpeed (ignores stick-magnitude scaling).
    /// Suppressed while aiming and while fatigued. Drains stamina (driven by the
    /// stamina system, which knows the current state is Sprint).
    /// </summary>
    public class SprintState : GroundedLocomotionState
    {
        public override CharacterStateType StateType => CharacterStateType.Sprint;
        public override string StateName => "Sprint";

        protected override bool IgnoresStickScaling => true;
        protected override bool SprintFlag => true;

        public SprintState() : base() { }
        public SprintState(StateConfigSO config) : base(config) { }

        protected override float GetTargetSpeed(StateContext context) => context.Motor.SprintSpeed;

        public override bool CanEnterState(StateContext context) =>
            context.HasMoveInput() &&
            context.Input.SprintHeld &&
            !context.Input.AimHeld &&
            HasStamina(context) &&
            !IsFatigued(context);

        protected override CharacterStateType GetGroundedTransition(StateContext context)
        {
            // Stop sprinting if the button is released, aiming starts, or stamina runs out.
            if (!context.Input.SprintHeld || context.Input.AimHeld || !HasStamina(context) || IsFatigued(context))
                return context.HasMoveInput() ? CharacterStateType.Move : CharacterStateType.Idle;

            if (!context.HasMoveInput())
                return CharacterStateType.Idle;

            return StateType;
        }
    }
}
