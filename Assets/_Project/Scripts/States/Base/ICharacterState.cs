namespace Game.PlayerV2.States
{
    /// <summary>
    /// Interface that all character states must implement
    /// </summary>
    public interface ICharacterState
    {
        /// <summary>
        /// The type identifier for this state
        /// </summary>
        CharacterStateType StateType { get; }

        /// <summary>
        /// Human-readable name for this state
        /// </summary>
        string StateName { get; }

        /// <summary>
        /// Whether this state is currently enabled (can be entered)
        /// </summary>
        bool IsEnabled { get; set; }

        /// <summary>
        /// Priority level for state transitions
        /// </summary>
        StatePriority Priority { get; }

        #region Lifecycle Methods

        /// <summary>
        /// Called when entering this state
        /// </summary>
        /// <param name="context">The state context containing all necessary references</param>
        void OnEnter(StateContext context);

        /// <summary>
        /// Called every frame while in this state
        /// </summary>
        /// <param name="deltaTime">Time since last frame</param>
        void OnUpdate(float deltaTime);

        /// <summary>
        /// Called at fixed timestep while in this state (for physics)
        /// </summary>
        /// <param name="fixedDeltaTime">Fixed time step</param>
        void OnFixedUpdate(float fixedDeltaTime);

        /// <summary>
        /// Called when exiting this state
        /// </summary>
        void OnExit();

        #endregion

        #region Transition Methods

        /// <summary>
        /// Checks if this state can be entered given the current context
        /// </summary>
        /// <param name="context">The state context to evaluate</param>
        /// <returns>True if the state can be entered</returns>
        bool CanEnterState(StateContext context);

        /// <summary>
        /// Checks if this state can be exited
        /// </summary>
        /// <returns>True if the state can be exited</returns>
        bool CanExitState();

        /// <summary>
        /// Evaluates possible state transitions and returns the highest priority valid transition
        /// </summary>
        /// <param name="context">The state context to evaluate</param>
        /// <returns>The state to transition to, or the current state if no transition is needed</returns>
        CharacterStateType CheckTransitions(StateContext context);

        #endregion
    }
}
