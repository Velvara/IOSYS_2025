using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Global constants used throughout the character controller system
    /// </summary>
    public static class Constants
    {
        #region Physics Constants
        
        public const float GRAVITY = -20f;
        public const float GROUND_CHECK_DISTANCE = 0.2f;
        public const float GROUND_CHECK_RADIUS = 0.3f;
        public const float SLOPE_LIMIT = 45f;
        public const float STEP_OFFSET = 0.3f;
        
        #endregion

        #region Animation Parameters
        
        // ── Real animator contract (StarterAssetsThirdPerson.controller) ──
        // These names/types MUST match the live Animator Controller exactly.
        // Floats:
        public const string ANIM_SPEED = "Speed";              // 0..1, normalized to SprintSpeed
        public const string ANIM_MOTION_SPEED = "MotionSpeed"; // input magnitude (blend playback rate)
        // Bools:
        public const string ANIM_GROUNDED = "Grounded";
        public const string ANIM_JUMP = "Jump";                // bool, NOT a trigger
        public const string ANIM_FREE_FALL = "FreeFall";
        public const string ANIM_SPRINT = "Sprint";
        public const string ANIM_FATIGUED = "Fatigued";
        public const string ANIM_STEALTH = "Stealth";

        // ── Future params (NOT in the animator yet; Set*Safe no-ops until added) ──
        // Driven by states/systems in later phases (swim, scan, drained, throw…).
        public const string ANIM_TURN_SPEED = "turnSpeed";
        public const string ANIM_SCAN_PROGRESS = "scanProgress";
        public const string ANIM_IS_IN_WATER = "IsInWater";
        public const string ANIM_IS_DRAINED = "IsDrained";
        public const string ANIM_HAS_STAMINA = "HasStamina";
        public const string ANIM_INTERACT = "Interact";
        public const string ANIM_START_SCAN = "StartScan";
        public const string ANIM_COMPLETE_SCAN = "CompleteScan";
        public const string ANIM_THROW = "Throw";
        public const string ANIM_MOVEMENT_STATE = "MovementState";
        public const string ANIM_ACTION_TYPE = "ActionType";
        
        #endregion

        #region Input Buffer Times
        
        public const float JUMP_BUFFER_TIME = 0.1f;
        public const float INTERACTION_BUFFER_TIME = 0.15f;
        public const float COYOTE_TIME = 0.15f; // Grace period after leaving ground
        
        #endregion

        #region Default Values
        
        public const float DEFAULT_MOVE_SPEED = 5f;
        public const float DEFAULT_SPRINT_SPEED = 8f;
        public const float DEFAULT_STEALTH_SPEED = 2.5f;
        public const float DEFAULT_JUMP_HEIGHT = 1.5f;
        public const float DEFAULT_TURN_SPEED = 720f;
        
        #endregion

        #region Camera Defaults
        
        public const float DEFAULT_CAMERA_DISTANCE = 5f;
        public const float DEFAULT_CAMERA_HEIGHT = 2f;
        public const float DEFAULT_CAMERA_FOV = 60f;
        public const float DEFAULT_CAMERA_SENSITIVITY_H = 2f;
        public const float DEFAULT_CAMERA_SENSITIVITY_V = 2f;
        
        #endregion

        #region Layer Names
        
        public const string LAYER_GROUND = "Ground";
        public const string LAYER_WATER = "Water";
        public const string LAYER_CLIMBABLE = "Climbable";
        public const string LAYER_INTERACTABLE = "Interactable";
        
        #endregion

        #region Tag Names
        
        public const string TAG_PLAYER = "Player";
        public const string TAG_WATER = "Water";
        public const string TAG_CLIMBABLE = "Climbable";
        public const string TAG_LEDGE = "Ledge";
        
        #endregion

        #region Debug Colors
        
        public static readonly Color DEBUG_GROUND_CHECK = Color.green;
        public static readonly Color DEBUG_WATER_LEVEL = Color.cyan;
        public static readonly Color DEBUG_CLIMB_NORMAL = Color.yellow;
        public static readonly Color DEBUG_MOVEMENT_VECTOR = Color.red;
        public static readonly Color DEBUG_LEDGE_DETECTION = Color.magenta;
        
        #endregion
    }
}
