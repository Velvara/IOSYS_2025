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

        [Header("Pitch Lock (aim look-down)")]
        [Tooltip("How fast the camera eases into/out of a forced pitch (e.g. rope aim looking at the ground).")]
        [SerializeField] private float _pitchLockLerpSpeed = 6f;

        [Header("Input")]
        [Tooltip("Look input source. Auto-found on this GameObject if left empty.")]
        [SerializeField] private InputHandler _input;

        private float _targetYaw;
        private float _targetPitch;
        private bool _frozen;

        // Pitch lock: a forced pitch (e.g. rope aim looking down), eased in/out. The free-look pitch
        // (_targetPitch) is preserved so releasing the lock returns to exactly where free-look was.
        private bool _pitchLocked;
        private float _lockedPitch;
        private float _pitchLockBlend;   // 0 = free pitch, 1 = fully at _lockedPitch

        // Scripted look override: a caller (e.g. the rappel enter blend) eases the camera from its
        // current angles to a target direction by advancing a 0→1 progress, ignoring input meanwhile.
        private bool _lookOverride;
        private float _ovStartYaw, _ovStartPitch, _ovTargetYaw, _ovTargetPitch;
        private float _ovYaw, _ovPitch;   // currently applied override angles

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

            // Scripted orient (e.g. rappel enter blend easing to the character's back): input ignored,
            // the caller advances the progress; we just apply the eased angles.
            if (_lookOverride)
            {
                _cinemachineCameraTarget.rotation = Quaternion.Euler(
                    _ovPitch + _cameraAngleOverride, _ovYaw, 0f);
                return;
            }

            float dt = Time.deltaTime;
            Vector2 look = _input != null ? _input.LookInput : Vector2.zero;

            if (look.sqrMagnitude >= _lookThreshold && !_lockCameraPosition)
            {
                // Mouse delta is already frame-rate independent; gamepad stick is a rate.
                float deltaTimeMultiplier = (_input != null && _input.IsCurrentDeviceMouse) ? 1f : dt;
                _targetYaw += look.x * deltaTimeMultiplier;
                if (!_pitchLocked)                              // vertical look frozen while pitch-locked
                    _targetPitch += look.y * deltaTimeMultiplier;
            }

            _targetYaw = ClampAngle(_targetYaw, float.MinValue, float.MaxValue);
            _targetPitch = ClampAngle(_targetPitch, _bottomClamp, _topClamp);

            // Ease toward the forced pitch (e.g. rope aim). Applied via a blend off the preserved free
            // pitch, so the override can exceed the normal clamps and releasing eases back without a snap.
            _pitchLockBlend = Mathf.MoveTowards(_pitchLockBlend, _pitchLocked ? 1f : 0f, _pitchLockLerpSpeed * dt);
            float appliedPitch = Mathf.Lerp(_targetPitch, _lockedPitch, _pitchLockBlend);

            _cinemachineCameraTarget.rotation = Quaternion.Euler(
                appliedPitch + _cameraAngleOverride, _targetYaw, 0.0f);
        }

        /// <summary>
        /// Points free-look in the given world direction, regardless of frozen state — e.g. climbing
        /// primes the camera to face into the new wall on a mid-air grab. Goes through the INTERNAL
        /// yaw/pitch (a direct target-transform write doesn't survive: unfrozen, LateUpdate rewrites
        /// it from these angles next frame; frozen, a parent rotation can drag it before the
        /// unfreeze-resync reads it). Also writes the transform so a same-frame resync agrees.
        /// </summary>
        public void SetLookDirection(Vector3 worldDirection)
        {
            if (_cinemachineCameraTarget == null || worldDirection.sqrMagnitude < 1e-6f) return;
            Vector3 e = Quaternion.LookRotation(worldDirection.normalized, Vector3.up).eulerAngles;
            _targetYaw = e.y;
            _targetPitch = Mathf.Clamp(NormalizeAngleSigned(e.x), _bottomClamp, _topClamp);
            _cinemachineCameraTarget.rotation = Quaternion.Euler(
                _targetPitch + _cameraAngleOverride, _targetYaw, 0f);
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

        /// <summary>
        /// Forces the camera pitch to a fixed angle (e.g. rope aim looking down into the ground), easing
        /// in and out at <see cref="_pitchLockLerpSpeed"/>. Horizontal (yaw) look stays live; vertical
        /// look input is ignored while locked. The free-look pitch is preserved, so releasing eases back
        /// to it without a snap. <paramref name="pitchDegrees"/> is applied directly and bypasses the
        /// normal look clamps (its sign/scale depend on the rig — tune it on the caller).
        /// </summary>
        public void SetPitchLock(bool locked, float pitchDegrees)
        {
            _pitchLocked = locked;
            if (locked) _lockedPitch = Mathf.Clamp(pitchDegrees, -89f, 89f);
        }

        /// <summary>
        /// Begins a scripted camera orient: from the current look toward <paramref name="worldDir"/>, with
        /// input ignored. The caller drives it with <see cref="SetLookOverrideProgress"/> (0→1) and ends it
        /// with <see cref="ReleaseLookOverride"/> (free-look resumes from wherever it landed — no snap).
        /// Used to settle the camera onto the character's back as the rappel enter blend completes.
        /// </summary>
        public void BeginLookOverride(Vector3 worldDir)
        {
            if (_cinemachineCameraTarget == null || worldDir.sqrMagnitude < 1e-6f) return;

            Vector3 cur = _cinemachineCameraTarget.rotation.eulerAngles;   // start from the actual visual angle
            _ovStartYaw = cur.y;
            _ovStartPitch = NormalizeAngleSigned(cur.x);

            Vector3 e = Quaternion.LookRotation(worldDir.normalized, Vector3.up).eulerAngles;
            _ovTargetYaw = e.y;
            _ovTargetPitch = NormalizeAngleSigned(e.x);

            _ovYaw = _ovStartYaw;
            _ovPitch = _ovStartPitch;
            // The scripted orient owns pitch outright — drop any aim pitch-lock so it can't reappear on release.
            _pitchLocked = false;
            _pitchLockBlend = 0f;
            _lookOverride = true;
        }

        /// <summary>Advances the scripted orient (0 = start angles, 1 = fully on the target direction).</summary>
        public void SetLookOverrideProgress(float t)
        {
            if (!_lookOverride) return;
            t = Mathf.Clamp01(t);
            _ovYaw = Mathf.LerpAngle(_ovStartYaw, _ovTargetYaw, t);
            _ovPitch = Mathf.Lerp(_ovStartPitch, _ovTargetPitch, t);
        }

        /// <summary>Ends the scripted orient; free-look resumes from the angles it ended on.</summary>
        public void ReleaseLookOverride()
        {
            if (!_lookOverride) return;
            _lookOverride = false;
            _targetYaw = _ovYaw;
            _targetPitch = Mathf.Clamp(_ovPitch, _bottomClamp, _topClamp);
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
