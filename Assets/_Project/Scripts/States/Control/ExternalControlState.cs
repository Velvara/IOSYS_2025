namespace Game.PlayerV2.States
{
    /// <summary>
    /// Highest-priority state entered when an external system takes over the character
    /// (hookshot drag, cutscene, scripted move). The controller relinquishes all locomotion
    /// and freezes camera look while active; the external system drives the CharacterController
    /// directly if it wants to. This is the first-class replacement for the old
    /// FreezeCharacter / IsExternalControlActive / enabled=false patches.
    /// </summary>
    public class ExternalControlState : CharacterStateBase
    {
        public override CharacterStateType StateType => CharacterStateType.ExternalControl;
        public override string StateName => "ExternalControl";
        public override StatePriority Priority => StatePriority.Critical;

        public ExternalControlState() : base() { }
        public ExternalControlState(StateConfigSO config) : base(config) { }

        public override void OnEnter(StateContext context)
        {
            base.OnEnter(context);
            // Stop driving locomotion (zero speed + animator) and freeze camera look.
            context.Motor.SuspendLocomotion();
            context.CameraRig?.SetFrozen(true);
        }

        // No movement and no gravity: the external system owns the CharacterController.
        public override void OnUpdate(float deltaTime)
        {
            base.OnUpdate(deltaTime);
        }

        public override void OnFixedUpdate(float fixedDeltaTime) { }

        public override void OnExit()
        {
            base.OnExit();
            _context.CameraRig?.SetFrozen(false);
        }

        public override bool CanEnterState(StateContext context) =>
            context.Controller.IsExternalControlActive;

        public override CharacterStateType CheckTransitions(StateContext context)
        {
            // Stay locked until the external system releases control.
            if (context.Controller.IsExternalControlActive)
                return StateType;

            // Released: hand control back based on the current situation.
            if (!context.IsGrounded)
                return CharacterStateType.Jump;
            if (context.HasMoveInput())
                return CharacterStateType.Move;
            return CharacterStateType.Idle;
        }
    }
}
