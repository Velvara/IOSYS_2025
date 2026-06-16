using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Rotates the Cinemachine follow target from look input (yaw + clamped pitch),
    /// reproducing the StarterAssets ThirdPersonController camera feel.
    ///
    /// Self-contained and independent of the locomotion controller: the Cinemachine
    /// virtual camera follows the assigned target transform, and this script only
    /// rotates that target. Aim-mode rig changes (shoulder offset / distance / side)
    /// remain owned by AimManager, so no Cinemachine assembly reference is needed here —
    /// we only drive a Transform.
    /// </summary>
    public class PlayerCameraRig : MonoBehaviour, ICameraState
    {
        [Header("Target")]
        [Tooltip("The transform the Cinemachine virtual camera follows (e.g. PlayerCameraRoot).")]
        [SerializeField] private Transform _cinemachineCameraTarget;

        [Header("Pitch Clamp (degrees)")]
        [Tooltip("How far up the camera can look.")]
        [SerializeField] private float _topClamp = 70f;
        [Tooltip("How far down the camera can look.")]
        [SerializeField] private float _bottomClamp = -30f;
        [Tooltip("Extra pitch offset for fine-tuning the camera angle.")]
        [SerializeField] private float _cameraAngleOverride = 0f;

        [Tooltip("Locks all camera look rotation.")]
        [SerializeField] private bool _lockCameraPosition = false;

        [Header("Input")]
        [Tooltip("Look input source. Auto-found on this GameObject if left empty.")]
        [SerializeField] private InputHandler _input;

        private float _targetYaw;
        private float _targetPitch;
        private bool _frozen;

        private const float _lookThreshold = 0.01f;

        /// <summary>True while camera look is frozen (e.g. external control / hookshot).</summary>
        public bool IsCameraFrozen => _frozen;

        /// <summary>The follow target this rig rotates.</summary>
        public Transform CameraTarget => _cinemachineCameraTarget;

        private void Awake()
        {
            if (_input == null) _input = GetComponent<InputHandler>();
        }

        private void Start()
        {
            if (_cinemachineCameraTarget != null)
                _targetYaw = _cinemachineCameraTarget.rotation.eulerAngles.y;
            else
                Debug.LogError("[PlayerCameraRig] No Cinemachine camera target assigned.");
        }

        private void LateUpdate()
        {
            if (_frozen || _cinemachineCameraTarget == null) return;

            Vector2 look = _input != null ? _input.LookInput : Vector2.zero;

            if (look.sqrMagnitude >= _lookThreshold && !_lockCameraPosition)
            {
                // Mouse delta is already frame-rate independent; gamepad stick is a rate.
                float deltaTimeMultiplier = (_input != null && _input.IsCurrentDeviceMouse) ? 1f : Time.deltaTime;
                _targetYaw += look.x * deltaTimeMultiplier;
                _targetPitch += look.y * deltaTimeMultiplier;
            }

            _targetYaw = ClampAngle(_targetYaw, float.MinValue, float.MaxValue);
            _targetPitch = ClampAngle(_targetPitch, _bottomClamp, _topClamp);

            _cinemachineCameraTarget.rotation = Quaternion.Euler(
                _targetPitch + _cameraAngleOverride, _targetYaw, 0.0f);
        }

        /// <summary>
        /// Freezes or unfreezes camera look. On unfreeze the yaw/pitch are re-synced from
        /// the target's current rotation so control resumes without a snap.
        /// </summary>
        public void SetFrozen(bool frozen)
        {
            if (_frozen == frozen) return;
            _frozen = frozen;
            if (!_frozen) ResyncFromTarget();
        }

        private void ResyncFromTarget()
        {
            if (_cinemachineCameraTarget == null) return;
            Vector3 e = _cinemachineCameraTarget.rotation.eulerAngles;
            _targetYaw = e.y;
            _targetPitch = NormalizeAngleSigned(e.x);
        }

        private static float NormalizeAngleSigned(float angle)
        {
            if (angle > 180f) angle -= 360f;
            return angle;
        }

        private static float ClampAngle(float angle, float min, float max)
        {
            if (angle < -360f) angle += 360f;
            if (angle > 360f) angle -= 360f;
            return Mathf.Clamp(angle, min, max);
        }
    }
}
