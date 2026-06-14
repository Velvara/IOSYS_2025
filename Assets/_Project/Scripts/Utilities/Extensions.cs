using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Extension methods for common operations
    /// </summary>
    public static class Extensions
    {
        #region Vector3 Extensions
        
        /// <summary>
        /// Returns a copy of the vector with Y component set to zero (flattened to XZ plane)
        /// </summary>
        public static Vector3 Flatten(this Vector3 vector)
        {
            return new Vector3(vector.x, 0f, vector.z);
        }

        /// <summary>
        /// Returns the horizontal magnitude (ignoring Y component)
        /// </summary>
        public static float HorizontalMagnitude(this Vector3 vector)
        {
            return new Vector2(vector.x, vector.z).magnitude;
        }

        /// <summary>
        /// Clamps the vector's magnitude to a maximum value
        /// </summary>
        public static Vector3 ClampMagnitude(this Vector3 vector, float maxMagnitude)
        {
            return Vector3.ClampMagnitude(vector, maxMagnitude);
        }

        /// <summary>
        /// Returns true if the vector is approximately zero
        /// </summary>
        public static bool IsApproximatelyZero(this Vector3 vector, float threshold = 0.01f)
        {
            return vector.sqrMagnitude < threshold * threshold;
        }

        #endregion

        #region Transform Extensions

        /// <summary>
        /// Sets the position's Y component while keeping X and Z
        /// </summary>
        public static void SetPositionY(this Transform transform, float y)
        {
            Vector3 pos = transform.position;
            pos.y = y;
            transform.position = pos;
        }

        /// <summary>
        /// Gets the forward direction flattened to the XZ plane
        /// </summary>
        public static Vector3 GetFlatForward(this Transform transform)
        {
            return transform.forward.Flatten().normalized;
        }

        /// <summary>
        /// Gets the right direction flattened to the XZ plane
        /// </summary>
        public static Vector3 GetFlatRight(this Transform transform)
        {
            return transform.right.Flatten().normalized;
        }

        #endregion

        #region Animator Extensions

        /// <summary>
        /// Safely sets a float parameter if it exists
        /// </summary>
        public static void SetFloatSafe(this Animator animator, string parameterName, float value)
        {
            if (animator.HasParameter(parameterName))
            {
                animator.SetFloat(parameterName, value);
            }
        }

        /// <summary>
        /// Safely sets a bool parameter if it exists
        /// </summary>
        public static void SetBoolSafe(this Animator animator, string parameterName, bool value)
        {
            if (animator.HasParameter(parameterName))
            {
                animator.SetBool(parameterName, value);
            }
        }

        /// <summary>
        /// Safely sets a trigger parameter if it exists
        /// </summary>
        public static void SetTriggerSafe(this Animator animator, string parameterName)
        {
            if (animator.HasParameter(parameterName))
            {
                animator.SetTrigger(parameterName);
            }
        }

        /// <summary>
        /// Safely sets an integer parameter if it exists
        /// </summary>
        public static void SetIntegerSafe(this Animator animator, string parameterName, int value)
        {
            if (animator.HasParameter(parameterName))
            {
                animator.SetInteger(parameterName, value);
            }
        }

        /// <summary>
        /// Checks if the animator has a specific parameter
        /// </summary>
        public static bool HasParameter(this Animator animator, string parameterName)
        {
            foreach (AnimatorControllerParameter param in animator.parameters)
            {
                if (param.name == parameterName)
                    return true;
            }
            return false;
        }

        #endregion

        #region LayerMask Extensions

        /// <summary>
        /// Checks if a layer is included in the LayerMask
        /// </summary>
        public static bool Contains(this LayerMask layerMask, int layer)
        {
            return (layerMask.value & (1 << layer)) != 0;
        }

        #endregion

        #region Float Extensions

        /// <summary>
        /// Remaps a value from one range to another
        /// </summary>
        public static float Remap(this float value, float fromMin, float fromMax, float toMin, float toMax)
        {
            return (value - fromMin) / (fromMax - fromMin) * (toMax - toMin) + toMin;
        }

        /// <summary>
        /// Returns true if the value is approximately equal to another value
        /// </summary>
        public static bool Approximately(this float value, float other, float threshold = 0.01f)
        {
            return Mathf.Abs(value - other) < threshold;
        }

        #endregion

        #region CharacterController Extensions

        /// <summary>
        /// Checks if the CharacterController is on a valid ground surface
        /// </summary>
        public static bool IsGroundedExtended(this CharacterController controller, LayerMask groundLayer, float extraDistance = 0.1f)
        {
            Vector3 origin = controller.transform.position + controller.center;
            float radius = controller.radius * 0.9f;
            float distance = (controller.height / 2f) - radius + extraDistance;

            return Physics.SphereCast(origin, radius, Vector3.down, out _, distance, groundLayer);
        }

        #endregion
    }
}
