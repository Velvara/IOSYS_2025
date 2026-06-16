namespace Game.PlayerV2.States
{
    /// <summary>
    /// Idle - grounded, no movement input. Target speed 0.
    /// </summary>
    public class IdleState : GroundedLocomotionState
    {
        public override CharacterStateType StateType => CharacterStateType.Idle;
        public override string StateName => "Idle";

        public IdleState() : base() { }
        public IdleState(StateConfigSO config) : base(config) { }

        protected override float GetTargetSpeed(StateContext context) => 0f;

        public override bool CanEnterState(StateContext context) => true;

        protected override CharacterStateType GetGroundedTransition(StateContext context)
        {
            if (context.Input.SprintHeld && context.HasMoveInput() &&
                !context.Input.AimHeld && HasStamina(context) && !IsFatigued(context))
                return CharacterStateType.Sprint;

            if (context.Input.StealthToggled)
                return CharacterStateType.Stealth;

            if (context.HasMoveInput())
                return CharacterStateType.Move;

            return StateType;
        }
    }
}
