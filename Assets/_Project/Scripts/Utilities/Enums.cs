using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Defines all possible character states
    /// </summary>
    public enum CharacterStateType
    {
        // Basic Locomotion
        Idle,
        Move,
        Sprint,
        Jump,
        Stealth,
        
        // Advanced Locomotion
        DrainedIdle,
        DrainedMove,
        Swim,
        Dive,
        Drowned,
        Climb,
        
        // Actions
        Interact,
        Aim,
        Scan,
        Throw
    }

    /// <summary>
    /// Movement modes for different contexts
    /// </summary>
    public enum MovementMode
    {
        Grounded,
        Airborne,
        Swimming,
        Climbing
    }

    /// <summary>
    /// State transition priority levels
    /// </summary>
    public enum StatePriority
    {
        Critical = 0,      // Drowned, game over states
        Environmental = 1,  // Swim, Dive, Climb
        Action = 2,         // Aim, Scan, Throw
        Drained = 3,        // DrainedIdle, DrainedMove
        BasicLocomotion = 4 // Idle, Move, Sprint, Jump, Stealth
    }

    /// <summary>
    /// Input context for action map switching
    /// </summary>
    public enum InputContext
    {
        Gameplay,
        UI,
        Cutscene,
        Disabled
    }

    /// <summary>
    /// Interaction types for contextual actions
    /// </summary>
    public enum InteractionType
    {
        None,
        PickUp,
        Use,
        Talk,
        Open,
        Climb,
        Custom
    }
}
