using UnityEngine;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Base class for all character states, implementing common functionality
    /// Derive from this class to create new states
    /// </summary>
    public abstract class CharacterStateBase : ICharacterState
    {
        #region Properties

        public abstract CharacterStateType StateType { get; }
        public abstract string StateName { get; }
        public bool IsEnabled { get; set; } = true;
        public abstract StatePriority Priority { get; }

        #endregion

        #region Protected Fields

        /// <summary>
        /// Cached reference to the state context
        /// </summary>
        protected StateContext _context;

        /// <summary>
        /// Configuration settings for this state (can be null if not using ScriptableObject config)
        /// </summary>
        protected StateConfigSO _config;

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor
        /// </summary>
        protected CharacterStateBase()
        {
        }

        /// <summary>
        /// Constructor with configuration
        /// </summary>
        /// <param name="config">State configuration ScriptableObject</param>
        protected CharacterStateBase(StateConfigSO config)
        {
            _config = config;
            if (_config != null)
            {
                IsEnabled = _config.enabledByDefault;
            }
        }

        #endregion

        #region ICharacterState Implementation

        public virtual void OnEnter(StateContext context)
        {
            _context = context;
            _context.ResetTimeData();

            // Debug log
            if (Application.isEditor)
            {
                Debug.Log($"[State] Entering: {StateName}");
            }
        }

        public virtual void OnUpdate(float deltaTime)
        {
            _context.TimeInState += deltaTime;
            _context.DeltaTime = deltaTime;
        }

        public abstract void OnFixedUpdate(float fixedDeltaTime);

        public virtual void OnExit()
        {
            // Debug log
            if (Application.isEditor)
            {
                Debug.Log($"[State] Exiting: {StateName}");
            }
        }

        public abstract bool CanEnterState(StateContext context);

        public virtual bool CanExitState()
        {
            // By default, states can be exited at any time
            // Override this in states that have restrictions (e.g., animation locks)
            return true;
        }

        public abstract CharacterStateType CheckTransitions(StateContext context);

        #endregion

        #region Protected Helper Methods

        // Movement mechanics and animator writes are owned by PlayerMotor (driven by states);
        // states are thin policy and no longer move the controller or set animator params directly.

        /// <summary>
        /// Checks if stamina is available for this state
        /// </summary>
        protected bool HasStamina(StateContext context)
        {
            return context.Stamina != null && !context.Stamina.IsDepleted;
        }

        /// <summary>
        /// Checks common high-priority state transitions (Drowned, Drained states)
        /// Returns the state type to transition to, or current state if no transition
        /// </summary>
        protected CharacterStateType CheckCommonTransitions()
        {
            // Critical: Check for drowned state
            if (_context.IsInWater && _context.Stamina.IsDepleted)
            {
                return CharacterStateType.Drowned;
            }

            // Check for drained states
            if (_context.Stamina.IsFullyDepleted)
            {
                if (_context.HasMoveInput())
                {
                    return CharacterStateType.DrainedMove;
                }
                else
                {
                    return CharacterStateType.DrainedIdle;
                }
            }

            return StateType; // No transition
        }

        #endregion
    }
}
