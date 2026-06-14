using UnityEngine;

namespace Game.PlayerV2.Systems
{
    /// <summary>
    /// Defines camera positioning and behavior settings for a character state
    /// </summary>
    [System.Serializable]
    public class CameraSettings
    {
        #region Positioning

        [Header("Positioning")]
        [Tooltip("Distance from the target")]
        public float distance = Constants.DEFAULT_CAMERA_DISTANCE;

        [Tooltip("Height offset from the target")]
        public float height = Constants.DEFAULT_CAMERA_HEIGHT;

        [Tooltip("Offset applied to the target position")]
        public Vector3 targetOffset = Vector3.up * 1.5f;

        #endregion

        #region Look Settings

        [Header("Look")]
        [Tooltip("Field of view in degrees")]
        public float fov = Constants.DEFAULT_CAMERA_FOV;

        [Tooltip("Min/Max vertical rotation limits (pitch)")]
        public Vector2 rotationLimits = new Vector2(-40f, 70f);

        #endregion

        #region Sensitivity

        [Header("Sensitivity")]
        [Tooltip("Horizontal look speed")]
        public float horizontalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_H;

        [Tooltip("Vertical look speed")]
        public float verticalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_V;

        #endregion

        #region Smoothing

        [Header("Smoothing")]
        [Tooltip("Position damping for smooth camera movement")]
        public float positionDamping = 1f;

        [Tooltip("Rotation damping for smooth camera rotation")]
        public float rotationDamping = 1f;

        #endregion

        #region Target Override

        [Header("Target")]
        [Tooltip("Optional custom target transform (e.g., for aiming)")]
        public Transform customTarget;

        #endregion

        #region Constructors

        /// <summary>
        /// Default constructor with standard third-person values
        /// </summary>
        public CameraSettings()
        {
            distance = Constants.DEFAULT_CAMERA_DISTANCE;
            height = Constants.DEFAULT_CAMERA_HEIGHT;
            targetOffset = Vector3.up * 1.5f;
            fov = Constants.DEFAULT_CAMERA_FOV;
            rotationLimits = new Vector2(-40f, 70f);
            horizontalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_H;
            verticalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_V;
            positionDamping = 1f;
            rotationDamping = 1f;
            customTarget = null;
        }

        /// <summary>
        /// Copy constructor
        /// </summary>
        public CameraSettings(CameraSettingsData data)
        {
            if (data != null)
            {
                distance = data.distance;
                height = data.height;
                targetOffset = data.targetOffset;
                fov = data.fov;
                rotationLimits = data.rotationLimits;
                horizontalSpeed = data.horizontalSpeed;
                verticalSpeed = data.verticalSpeed;
                positionDamping = data.positionDamping;
                rotationDamping = data.rotationDamping;
                customTarget = data.customTarget;
            }
            else
            {
                // Use default values if data is null
                this.distance = Constants.DEFAULT_CAMERA_DISTANCE;
                this.height = Constants.DEFAULT_CAMERA_HEIGHT;
                this.targetOffset = Vector3.up * 1.5f;
                this.fov = Constants.DEFAULT_CAMERA_FOV;
                this.rotationLimits = new Vector2(-40f, 70f);
                this.horizontalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_H;
                this.verticalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_V;
                this.positionDamping = 1f;
                this.rotationDamping = 1f;
                this.customTarget = null;
            }
        }

        /// <summary>
        /// Constructor with basic parameters
        /// </summary>
        public CameraSettings(float distance, float height, float fov)
        {
            this.distance = distance;
            this.height = height;
            this.fov = fov;
            this.targetOffset = Vector3.up * 1.5f;
            this.rotationLimits = new Vector2(-40f, 70f);
            this.horizontalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_H;
            this.verticalSpeed = Constants.DEFAULT_CAMERA_SENSITIVITY_V;
            this.positionDamping = 1f;
            this.rotationDamping = 1f;
            this.customTarget = null;
        }

        #endregion

        #region Static Defaults

        /// <summary>
        /// Default camera settings for standard third-person view
        /// </summary>
        public static CameraSettings Default => new CameraSettings();

        /// <summary>
        /// Camera settings for close-up view (stealth)
        /// </summary>
        public static CameraSettings Stealth => new CameraSettings(3f, 1f, 55f);

        /// <summary>
        /// Camera settings for over-shoulder aim view
        /// </summary>
        public static CameraSettings Aim => new CameraSettings
        {
            distance = 2f,
            height = 1.8f,
            targetOffset = new Vector3(0.5f, 1.6f, 0f), // Offset to right shoulder
            fov = 50f,
            positionDamping = 2f,
            rotationDamping = 2f
        };

        /// <summary>
        /// Camera settings for sprint (pulled back slightly)
        /// </summary>
        public static CameraSettings Sprint => new CameraSettings(6f, 2.2f, 65f);

        #endregion

        #region Helper Methods

        /// <summary>
        /// Creates a copy of these settings
        /// </summary>
        public CameraSettings Clone()
        {
            return new CameraSettings
            {
                distance = this.distance,
                height = this.height,
                targetOffset = this.targetOffset,
                fov = this.fov,
                rotationLimits = this.rotationLimits,
                horizontalSpeed = this.horizontalSpeed,
                verticalSpeed = this.verticalSpeed,
                positionDamping = this.positionDamping,
                rotationDamping = this.rotationDamping,
                customTarget = this.customTarget
            };
        }

        /// <summary>
        /// Lerps between two camera settings
        /// </summary>
        public static CameraSettings Lerp(CameraSettings from, CameraSettings to, float t)
        {
            return new CameraSettings
            {
                distance = Mathf.Lerp(from.distance, to.distance, t),
                height = Mathf.Lerp(from.height, to.height, t),
                targetOffset = Vector3.Lerp(from.targetOffset, to.targetOffset, t),
                fov = Mathf.Lerp(from.fov, to.fov, t),
                rotationLimits = Vector2.Lerp(from.rotationLimits, to.rotationLimits, t),
                horizontalSpeed = Mathf.Lerp(from.horizontalSpeed, to.horizontalSpeed, t),
                verticalSpeed = Mathf.Lerp(from.verticalSpeed, to.verticalSpeed, t),
                positionDamping = Mathf.Lerp(from.positionDamping, to.positionDamping, t),
                rotationDamping = Mathf.Lerp(from.rotationDamping, to.rotationDamping, t),
                customTarget = t > 0.5f ? to.customTarget : from.customTarget
            };
        }

        #endregion
    }

    /// <summary>
    /// Serializable version of CameraSettings for use in ScriptableObjects
    /// </summary>
    [System.Serializable]
    public class CameraSettingsData
    {
        public float distance = 5f;
        public float height = 2f;
        public Vector3 targetOffset = Vector3.up * 1.5f;
        public float fov = 60f;
        public Vector2 rotationLimits = new Vector2(-40f, 70f);
        public float horizontalSpeed = 2f;
        public float verticalSpeed = 2f;
        public float positionDamping = 1f;
        public float rotationDamping = 1f;
        public Transform customTarget;
    }
}
