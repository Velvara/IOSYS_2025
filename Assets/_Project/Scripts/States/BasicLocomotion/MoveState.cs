namespace Game.PlayerV2.States
{
    /// <summary>
    /// Move - grounded run/walk at RunSpeed, scaled by stick magnitude.
    /// </summary>
    public class MoveState : GroundedLocomotionState
    {
        public override CharacterStateType StateType => CharacterStateType.Move;
        public override string StateName => "Move";

        public MoveState() : base() { }
        public MoveState(StateConfigSO config) : base(config) { }

        protected override float GetTargetSpeed(StateContext context) => context.Motor.RunSpeed;

        public override bool CanEnterState(StateContext context) => context.HasMoveInput();

        protected override CharacterStateType GetGroundedTransition(StateContext context)
        {
            if (context.Input.SprintHeld && !context.Input.AimHeld && HasStamina(context) && !IsFatigued(context))
                return CharacterStateType.Sprint;

            if (context.Input.StealthToggled)
                return CharacterStateType.Stealth;

            if (!context.HasMoveInput())
                return CharacterStateType.Idle;

            return StateType;
        }
    }
}
