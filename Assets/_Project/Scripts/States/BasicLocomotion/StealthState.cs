namespace Game.PlayerV2.States
{
    /// <summary>
    /// Stealth - grounded slow movement at StealthSpeed while the stealth toggle is on.
    /// Cancelled by sprinting and by jumping (jump clears the toggle).
    /// </summary>
    public class StealthState : GroundedLocomotionState
    {
        public override CharacterStateType StateType => CharacterStateType.Stealth;
        public override string StateName => "Stealth";

        protected override bool StealthFlag => true;

        public StealthState() : base() { }
        public StealthState(StateConfigSO config) : base(config) { }

        protected override float GetTargetSpeed(StateContext context) => context.Motor.StealthSpeed;

        public override bool CanEnterState(StateContext context) => context.Input.StealthToggled;

        protected override CharacterStateType GetGroundedTransition(StateContext context)
        {
            // Sprint cancels stealth.
            if (context.Input.SprintHeld && context.HasMoveInput() &&
                !context.Input.AimHeld && HasStamina(context) && !IsFatigued(context))
                return CharacterStateType.Sprint;

            // Toggled off → return to move/idle.
            if (!context.Input.StealthToggled)
                return context.HasMoveInput() ? CharacterStateType.Move : CharacterStateType.Idle;

            return StateType;
        }
    }
}
