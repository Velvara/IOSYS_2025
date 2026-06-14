using UnityEngine;

namespace Game.PlayerV2.States
{
    /// <summary>
    /// Contains all the context data needed by states to make decisions and execute logic
    /// This is passed to states on enter, update, and transition checks
    /// </summary>
    public class StateContext
    {
        #region Core References

        /// <summary>
        /// Reference to the main player controller
        /// </summary>
        public PlayerController Controller { get; set; }

        /// <summary>
        /// Reference to the Unity Character Controller component
        /// </summary>
        public CharacterController CharacterController { get; set; }

        /// <summary>
        /// Reference to the Animator component
        /// </summary>
        public Animator Animator { get; set; }

        /// <summary>
        /// Reference to the character's transform
        /// </summary>
        public Transform Transform { get; set; }

        #endregion

        #region System References

        /// <summary>
        /// Input handling system
        /// </summary>
        public InputHandler Input { get; set; }

        /// <summary>
        /// Stamina management system
        /// </summary>
        public Systems.StaminaSystem Stamina { get; set; }

        /// <summary>
        /// Health management system
        /// </summary>
        public Systems.HealthSystem Health { get; set; }

        /// <summary>
        /// Inventory management system
        /// </summary>
        public Systems.InventorySystem Inventory { get; set; }

        /// <summary>
        /// Camera management system
        /// </summary>
        public Systems.CameraManager CameraManager { get; set; }

        #endregion

        #region Environment Queries

        /// <summary>
        /// Is the character currently grounded?
        /// </summary>
        public bool IsGrounded { get; set; }

        /// <summary>
        /// Is the character currently in water?
        /// </summary>
        public bool IsInWater { get; set; }

        /// <summary>
        /// Is the character near a climbable surface?
        /// </summary>
        public bool IsNearClimbable { get; set; }

        /// <summary>
        /// Is the character near a ledge that can be climbed onto?
        /// </summary>
        public bool IsNearLedge { get; set; }

        /// <summary>
        /// Current water surface level (Y position)
        /// </summary>
        public float WaterLevel { get; set; }

        /// <summary>
        /// Normal vector of the nearest climbable wall
        /// </summary>
        public Vector3 ClimbNormal { get; set; }

        /// <summary>
        /// Position of the nearest ledge
        /// </summary>
        public Vector3 LedgePosition { get; set; }

        #endregion

        #region Movement Data

        /// <summary>
        /// Current velocity of the character
        /// </summary>
        public Vector3 Velocity { get; set; }

        /// <summary>
        /// Normalized movement input from player (range -1 to 1 on each axis)
        /// </summary>
        public Vector2 MoveInput { get; set; }

        /// <summary>
        /// Camera look input from player
        /// </summary>
        public Vector2 LookInput { get; set; }

        /// <summary>
        /// Current movement speed
        /// </summary>
        public float CurrentSpeed { get; set; }

        /// <summary>
        /// Target movement direction in world space
        /// </summary>
        public Vector3 MoveDirection { get; set; }

        #endregion

        #region Time Data

        /// <summary>
        /// Time spent in the current state
        /// </summary>
        public float TimeInState { get; set; }

        /// <summary>
        /// Delta time for the current frame
        /// </summary>
        public float DeltaTime { get; set; }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Calculates the camera-relative movement direction from input
        /// </summary>
        /// <param name="cameraTransform">The camera transform to use for reference</param>
        /// <returns>World-space movement direction</returns>
        public Vector3 GetCameraRelativeMovement(Transform cameraTransform)
        {
            if (MoveInput.sqrMagnitude < 0.01f)
                return Vector3.zero;

            Vector3 forward = cameraTransform.forward.Flatten().normalized;
            Vector3 right = cameraTransform.right.Flatten().normalized;

            return (forward * MoveInput.y + right * MoveInput.x).normalized;
        }

        /// <summary>
        /// Checks if the character has any movement input
        /// </summary>
        public bool HasMoveInput()
        {
            return MoveInput.sqrMagnitude > 0.01f;
        }

        /// <summary>
        /// Resets time-related data (called when entering a new state)
        /// </summary>
        public void ResetTimeData()
        {
            TimeInState = 0f;
        }

        #endregion
    }
}
