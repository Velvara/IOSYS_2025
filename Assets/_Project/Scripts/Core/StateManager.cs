using System;
using System.Collections.Generic;
using UnityEngine;
using Game.PlayerV2.States;

namespace Game.PlayerV2
{
    /// <summary>
    /// Manages character states, handles transitions, and coordinates state updates
    /// </summary>
    public class StateManager
    {
        #region Fields

        private Dictionary<CharacterStateType, ICharacterState> _states;

        // Cached, pre-sorted view of _states ordered by ascending Priority
        // (lower number = higher priority = checked first). Rebuilt only on
        // registration so CheckPriorityTransitions allocates nothing per frame.
        private readonly List<ICharacterState> _statesByPriority = new List<ICharacterState>();
        private static readonly Comparison<ICharacterState> _priorityComparison =
            (a, b) => a.Priority.CompareTo(b.Priority);

        private ICharacterState _currentState;
        private StateContext _context;

        #endregion

        #region Properties

        /// <summary>
        /// The currently active state
        /// </summary>
        public ICharacterState CurrentState => _currentState;

        /// <summary>
        /// The type of the currently active state
        /// </summary>
        public CharacterStateType CurrentStateType => _currentState?.StateType ?? CharacterStateType.Idle;

        /// <summary>
        /// The state context shared by all states
        /// </summary>
        public StateContext Context => _context;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the state manager with a context
        /// </summary>
        public StateManager(StateContext context)
        {
            _context = context;
            _states = new Dictionary<CharacterStateType, ICharacterState>();
        }

        /// <summary>
        /// Registers a state with the manager
        /// </summary>
        public void RegisterState(ICharacterState state)
        {
            if (state == null)
            {
                Debug.LogError("[StateManager] Attempted to register null state");
                return;
            }

            if (_states.ContainsKey(state.StateType))
            {
                Debug.LogWarning($"[StateManager] State {state.StateType} is already registered. Overwriting.");
            }

            _states[state.StateType] = state;
            RebuildPriorityOrder();
            Debug.Log($"[StateManager] Registered state: {state.StateName}");
        }

        /// <summary>
        /// Rebuilds the priority-sorted state list. Called on registration only,
        /// never per frame. Runtime enable/disable does not reorder (priority is
        /// fixed per state), so SetStateEnabled does not need to call this.
        /// </summary>
        private void RebuildPriorityOrder()
        {
            _statesByPriority.Clear();
            foreach (var state in _states.Values)
                _statesByPriority.Add(state);
            _statesByPriority.Sort(_priorityComparison);
        }

        /// <summary>
        /// Registers multiple states at once
        /// </summary>
        public void RegisterStates(params ICharacterState[] states)
        {
            foreach (var state in states)
            {
                RegisterState(state);
            }
        }

        /// <summary>
        /// Sets the initial state (should be called after all states are registered)
        /// </summary>
        public void SetInitialState(CharacterStateType stateType)
        {
            if (!_states.ContainsKey(stateType))
            {
                Debug.LogError($"[StateManager] Cannot set initial state {stateType} - state not registered");
                return;
            }

            _currentState = _states[stateType];
            _currentState.OnEnter(_context);
        }

        #endregion

        #region Update Methods

        /// <summary>
        /// Updates the current state (call from Update)
        /// </summary>
        public void Update(float deltaTime)
        {
            if (_currentState == null)
            {
                Debug.LogWarning("[StateManager] No current state set");
                return;
            }

            // Update the current state
            _currentState.OnUpdate(deltaTime);

            // Check for state transitions
            CheckTransitions();
        }

        /// <summary>
        /// Fixed update for the current state (call from FixedUpdate)
        /// </summary>
        public void FixedUpdate(float fixedDeltaTime)
        {
            if (_currentState == null) return;

            _currentState.OnFixedUpdate(fixedDeltaTime);
        }

        #endregion

        #region Transition Management

        /// <summary>
        /// Checks for and executes state transitions
        /// </summary>
        private void CheckTransitions()
        {
            if (_currentState == null || !_currentState.CanExitState())
                return;

            // Ask current state what it wants to transition to
            CharacterStateType nextStateType = _currentState.CheckTransitions(_context);

            // If the state wants to transition
            if (nextStateType != _currentState.StateType)
            {
                TransitionToState(nextStateType);
                return;
            }

            // Otherwise, check all states by priority to see if any higher priority state can be entered
            CheckPriorityTransitions();
        }

        /// <summary>
        /// Checks if any higher priority state can be entered
        /// </summary>
        private void CheckPriorityTransitions()
        {
            // Iterate the cached, pre-sorted (ascending priority) list. No allocation.
            for (int i = 0; i < _statesByPriority.Count; i++)
            {
                ICharacterState state = _statesByPriority[i];

                // Stop checking once we reach same or lower priority than current
                if (state.Priority >= _currentState.Priority)
                    break;

                if (!state.IsEnabled || state.StateType == _currentState.StateType)
                    continue;

                // Check if this higher priority state can be entered
                if (state.CanEnterState(_context))
                {
                    TransitionToState(state.StateType);
                    return;
                }
            }
        }

        /// <summary>
        /// Forces a transition to a specific state
        /// </summary>
        public void TransitionToState(CharacterStateType stateType)
        {
            if (!_states.ContainsKey(stateType))
            {
                Debug.LogError($"[StateManager] Cannot transition to {stateType} - state not registered");
                return;
            }

            ICharacterState nextState = _states[stateType];

            if (!nextState.IsEnabled)
            {
                Debug.LogWarning($"[StateManager] Cannot transition to {stateType} - state is disabled");
                return;
            }

            if (!nextState.CanEnterState(_context))
            {
                Debug.LogWarning($"[StateManager] Cannot transition to {stateType} - entry conditions not met");
                return;
            }

            // Exit current state
            if (_currentState != null)
            {
                _currentState.OnExit();
            }

            // Enter new state
            _currentState = nextState;
            _currentState.OnEnter(_context);

            Debug.Log($"[StateManager] Transitioned to: {_currentState.StateName}");
        }

        /// <summary>
        /// Attempts to transition to a state, returns true if successful
        /// </summary>
        public bool TryTransitionToState(CharacterStateType stateType)
        {
            if (!_states.ContainsKey(stateType) || !_states[stateType].IsEnabled)
                return false;

            if (!_states[stateType].CanEnterState(_context))
                return false;

            TransitionToState(stateType);
            return true;
        }

        #endregion

        #region State Query Methods

        /// <summary>
        /// Gets a state by type
        /// </summary>
        public ICharacterState GetState(CharacterStateType stateType)
        {
            return _states.ContainsKey(stateType) ? _states[stateType] : null;
        }

        /// <summary>
        /// Checks if a state is registered
        /// </summary>
        public bool HasState(CharacterStateType stateType)
        {
            return _states.ContainsKey(stateType);
        }

        /// <summary>
        /// Gets all registered states
        /// </summary>
        public IEnumerable<ICharacterState> GetAllStates()
        {
            return _states.Values;
        }

        /// <summary>
        /// Enables or disables a state
        /// </summary>
        public void SetStateEnabled(CharacterStateType stateType, bool enabled)
        {
            if (_states.ContainsKey(stateType))
            {
                _states[stateType].IsEnabled = enabled;
                Debug.Log($"[StateManager] State {stateType} {(enabled ? "enabled" : "disabled")}");
            }
        }

        /// <summary>
        /// Checks if currently in a specific state
        /// </summary>
        public bool IsInState(CharacterStateType stateType)
        {
            return _currentState?.StateType == stateType;
        }

        /// <summary>
        /// Checks if currently in any of the specified states
        /// </summary>
        public bool IsInAnyState(params CharacterStateType[] stateTypes)
        {
            CharacterStateType current = CurrentStateType;
            for (int i = 0; i < stateTypes.Length; i++)
            {
                if (stateTypes[i] == current)
                    return true;
            }
            return false;
        }

        #endregion

        #region Debug

        /// <summary>
        /// Gets debug information about the state manager
        /// </summary>
        public string GetDebugInfo()
        {
            if (_currentState == null)
                return "No active state";

            return $"Current: {_currentState.StateName} | Priority: {_currentState.Priority} | Time: {_context.TimeInState:F2}s";
        }

        #endregion
    }
}
