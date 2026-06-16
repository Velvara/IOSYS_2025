using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// ScriptableObject for configuring character state parameters
    /// Create instances via: Create > Character > State Config
    /// </summary>
    [CreateAssetMenu(fileName = "StateConfig_", menuName = "Character/State Config", order = 0)]
    public class StateConfigSO : ScriptableObject
    {
        #region State Identity

        [Header("State Identity")]
        [Tooltip("The type of state this configuration is for")]
        public CharacterStateType stateType = CharacterStateType.Idle;

        [Tooltip("Display name for this state")]
        public string displayName = "State";

        [Tooltip("Is this state enabled by default?")]
        public bool enabledByDefault = true;

        #endregion

        #region Movement Parameters

        [Header("Movement")]
        [Tooltip("Movement speed in units per second")]
        public float moveSpeed = 5f;

        [Tooltip("How quickly the character accelerates")]
        public float acceleration = 10f;

        [Tooltip("How quickly the character decelerates")]
        public float deceleration = 10f;

        [Tooltip("Rotation speed in degrees per second")]
        public float turnSpeed = 720f;

        #endregion

        #region Stamina Configuration

        [Header("Stamina")]
        [Tooltip("Does this state consume stamina?")]
        public bool consumesStamina = false;

        [Tooltip("Stamina drain rate per second (if consumesStamina is true)")]
        public float staminaDrainRate = 0f;

        [Tooltip("Does this state recover stamina?")]
        public bool recoversStamina = false;

        [Tooltip("Stamina recovery rate per second (if recoversStamina is true)")]
        public float staminaRecoveryRate = 0f;

        #endregion

        #region Animation Settings

        [Header("Animation")]
        [Tooltip("Name of the animator state to transition to")]
        public string animatorStateName = "";

        [Tooltip("Duration of the crossfade transition")]
        [Range(0f, 1f)]
        public float crossfadeDuration = 0.2f;

        #endregion

        #region Debug Settings

        [Header("Debug")]
        [Tooltip("Color to use for debug visualization")]
        public Color debugColor = Color.white;

        [Tooltip("Show debug information in scene view")]
        public bool showDebugInfo = true;

        #endregion

        #region Validation

        private void OnValidate()
        {
            // Ensure values are within reasonable ranges
            moveSpeed = Mathf.Max(0f, moveSpeed);
            acceleration = Mathf.Max(0.1f, acceleration);
            deceleration = Mathf.Max(0.1f, deceleration);
            turnSpeed = Mathf.Max(0f, turnSpeed);
            staminaDrainRate = Mathf.Max(0f, staminaDrainRate);
            staminaRecoveryRate = Mathf.Max(0f, staminaRecoveryRate);

            // Update display name if empty
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = stateType.ToString();
            }
        }

        #endregion
    }
}
