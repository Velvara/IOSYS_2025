using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/* Note: animations are called via the controller for both the character and capsule using animator null checks
 */

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(PlayerStamina))]      // stamina system must live on the same GameObject
#if ENABLE_INPUT_SYSTEM
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour
    {
        public bool RotateOnMove = true;

        [Header("Player")]
        [Tooltip("Default run speed in m/s. Walk animation plays below this threshold via the blend tree. On gamepad, actual speed scales from 0 to this value based on stick magnitude.")]
        public float RunSpeed = 2.0f;

        [Tooltip("Sprint speed of the character in m/s. This always maps to 1.0 in the animator blend tree — all other speeds scale relative to it.")]
        public float SprintSpeed = 5.335f;

        [Header("Stamina")]
        // MaxStamina, StaminaDrainRate and StaminaRecoveryRate are now on PlayerStamina.
        [Tooltip("Move speed while fatigued in m/s (slower than walking)")]
        public float FatiguedSpeed = 1.2f;

        [Header("Stealth")]
        [Tooltip("Move speed while in stealth mode in m/s")]
        public float StealthSpeed = 1.5f;

        private bool movementFrozen = false;
        public bool cameraFrozen = false;
        private Quaternion savedCameraRotation;

        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 0.3f)]
        public float RotationSmoothTime = 0.12f;

        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;

        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;

        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;

        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;

        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;

        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;

        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;

        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;
        public bool IsExternalControlActive { get; set; } = false;
        public CharacterController Controller => _controller;

        // ── Stamina read-only passthrough (delegates to PlayerStamina) ────────
        // CurrentStamina and IsFatigued are read from _stamina each frame.
        // Other scripts that need stamina details should reference PlayerStamina directly.
        public float CurrentStamina => _stamina != null ? _stamina.CurrentStamina : 0f;
        public bool IsFatigued => _isFatigued;
        public bool IsSprinting => _isSprinting;
        public bool IsStealth => _isStealth;

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _targetRotation = 0.0f;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // stamina & fatigue state — updated each frame from PlayerStamina via Tick()
        private bool _isSprinting;
        private bool _isFatigued;

        // stealth state
        private bool _isStealth;

        // cached previous values — SetBool is only called on state change, not every frame
        private bool _wasSprinting;
        private bool _wasFatigued;
        private bool _wasStealth;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;
        private int _animIDSprint;
        private int _animIDFatigued;
        private int _animIDStealth;

#if ENABLE_INPUT_SYSTEM
        private PlayerInput _playerInput;
#endif
        private Animator _animator;
        private CharacterController _controller;
        private StarterAssetsInputs _input;
        public GameObject _mainCamera;

        // Reference to the stamina component on this same GameObject.
        // All stamina state is owned and updated by PlayerStamina.
        private PlayerStamina _stamina;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput.currentControlScheme == "KeyboardMouse";
#else
				return false;
#endif
            }
        }


        private void Awake()
        {
            // get a reference to our main camera
            if (_mainCamera == null)
            {
                _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
            }
        }

        private void Start()
        {
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
            _input = GetComponent<StarterAssetsInputs>();
            _stamina = GetComponent<PlayerStamina>();

#if ENABLE_INPUT_SYSTEM
            _playerInput = GetComponent<PlayerInput>();
#else
			Debug.LogError( "Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

            AssignAnimationIDs();

            // reset our timeouts on start
            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;

            // stamina state is initialised by PlayerStamina.Start()
            _isSprinting = false;
            _isFatigued = false;

            // initialise stealth
            _isStealth = false;
        }

        private void Update()
        {
            if (IsExternalControlActive)
                return;

            _hasAnimator = TryGetComponent(out _animator);

            JumpAndGravity();
            GroundedCheck();

            // Determine sprint intent here so it can be passed to PlayerStamina.Tick()
            // and reused by Move() in the same frame without duplication.
            bool wantsToSprint = !Mouse.current.rightButton.isPressed && _input.sprint;
            _isSprinting = wantsToSprint && !_stamina.IsFatigued && _stamina.CurrentStamina > 0f
                           && _input.move != Vector2.zero;

            // Hand off to the stamina system. It owns all drain/recovery/penalty logic.
            _stamina.Tick(_isSprinting);

            // Read fatigue state back after the tick — it may have just changed.
            _isFatigued = _stamina.IsFatigued;

            // Sprinting while fatigued is impossible; guard after re-reading fatigue.
            if (_isFatigued)
                _isSprinting = false;

            StealthUpdate();
            Move();
        }

        private void LateUpdate()
        {
            if (IsExternalControlActive)
                return;
            CameraRotation();
        }

        public void FreezeCharacter(bool freezeMovement, bool freezeCameraRotation)
        {
            movementFrozen = freezeMovement;
            cameraFrozen = freezeCameraRotation;

            if (movementFrozen)
            {
                _speed = 0f;
                _verticalVelocity = 0f;
            }

            if (cameraFrozen)
            {
                if (_input != null)
                    _input.LookInput(Vector2.zero);

                if (CinemachineCameraTarget != null)
                {
                    Vector3 e = CinemachineCameraTarget.transform.rotation.eulerAngles;
                    _cinemachineTargetYaw = NormalizeAngleSigned(e.y);
                    _cinemachineTargetPitch = NormalizeAngleSigned(e.x);
                }
            }
        }

        private static float NormalizeAngleSigned(float a)
        {
            if (a > 180f) a -= 360f;
            return a;
        }

        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
            _animIDSprint = Animator.StringToHash("Sprint");
            _animIDFatigued = Animator.StringToHash("Fatigued");
            _animIDStealth = Animator.StringToHash("Stealth");
        }

        private void GroundedCheck()
        {
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset,
                transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            if (_hasAnimator)
                _animator.SetBool(_animIDGrounded, Grounded);
        }

        private void CameraRotation()
        {
            if (cameraFrozen || IsExternalControlActive) return;
            if (_input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
            {
                float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;
                _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
                _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
            }

            _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
            _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

            CinemachineCameraTarget.transform.rotation = Quaternion.Euler(
                _cinemachineTargetPitch + CameraAngleOverride,
                _cinemachineTargetYaw, 0.0f);
        }

        private void Move()
        {
            if (movementFrozen) return;

            // _isSprinting and _isFatigued are already resolved in Update() via PlayerStamina.
            // Move() only needs to act on them.

            // Sprinting cancels stealth
            if (_isSprinting && _isStealth)
                _isStealth = false;

            float targetSpeed;
            if (_input.move == Vector2.zero)
                targetSpeed = 0.0f;
            else if (_isFatigued)
                targetSpeed = FatiguedSpeed;   // fatigue overrides everything
            else if (_isStealth)
                targetSpeed = StealthSpeed;    // stealth overrides run but not fatigue
            else if (_isSprinting)
                targetSpeed = SprintSpeed;
            else
                targetSpeed = RunSpeed;

            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;
            float speedOffset = 0.1f;

            // On gamepad, scale run/fatigued speed by stick magnitude so the character
            // smoothly accelerates from idle through walk into full run.
            float inputMagnitude = _input.analogMovement ? _input.move.magnitude : 1f;
            float stickMagnitude = Mathf.Clamp01(_input.move.magnitude);
            float scaledTarget = _isSprinting ? targetSpeed : targetSpeed * stickMagnitude;

            if (currentHorizontalSpeed < scaledTarget - speedOffset ||
                currentHorizontalSpeed > scaledTarget + speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, scaledTarget, Time.deltaTime * SpeedChangeRate);
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = scaledTarget;
            }

            float normalizedTarget = scaledTarget / SprintSpeed;
            _animationBlend = Mathf.Lerp(_animationBlend, normalizedTarget, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            Vector3 inputDirection = new Vector3(_input.move.x, 0.0f, _input.move.y).normalized;

            if (_input.move != Vector2.zero)
            {
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg +
                                  _mainCamera.transform.eulerAngles.y;
                float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, _targetRotation,
                    ref _rotationVelocity, RotationSmoothTime);

                if (RotateOnMove && !IsExternalControlActive)
                    transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }

            Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

            _controller.Move(targetDirection.normalized * (_speed * Time.deltaTime) +
                             new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            if (_hasAnimator)
            {
                if (!movementFrozen)
                {
                    _animator.SetFloat(_animIDSpeed, _animationBlend);
                    _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
                    if (_isSprinting != _wasSprinting)
                    {
                        _animator.SetBool(_animIDSprint, _isSprinting);
                        _wasSprinting = _isSprinting;
                    }
                    if (_isFatigued != _wasFatigued)
                    {
                        _animator.SetBool(_animIDFatigued, _isFatigued);
                        _wasFatigued = _isFatigued;
                    }
                    if (_isStealth != _wasStealth)
                    {
                        _animator.SetBool(_animIDStealth, _isStealth);
                        _wasStealth = _isStealth;
                    }
                }
                else
                {
                    _animator.SetFloat(_animIDSpeed, 0f);
                    _animator.SetFloat(_animIDMotionSpeed, 0f);
                    if (_wasSprinting) { _animator.SetBool(_animIDSprint, false); _wasSprinting = false; }
                    if (_wasFatigued) { _animator.SetBool(_animIDFatigued, false); _wasFatigued = false; }
                    if (_wasStealth) { _animator.SetBool(_animIDStealth, false); _wasStealth = false; }
                }
            }
        }

        /// <summary>
        /// Handles stealth toggle on button press and forced deactivation.
        /// Stealth is cancelled by sprinting (handled in Move) and jumping (handled in JumpAndGravity).
        /// Fatigued state automatically prevents entering stealth since the speed priority
        /// in Move() overrides it, but the bool remains queryable for other systems.
        /// </summary>
        private void StealthUpdate()
        {
            if (_input.stealth)
            {
                _isStealth = !_isStealth;
                _input.stealth = false;

                if (_isFatigued && _isStealth)
                    _isStealth = false;
            }
        }

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                _fallTimeoutDelta = FallTimeout;

                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                if (_verticalVelocity < 0.0f)
                    _verticalVelocity = -2f;

                // Jumping is blocked while fatigued.
                // To block additional states here, add them to this condition: e.g. && !_isClimbing && !_isSwimming
                if (_input.jump && _jumpTimeoutDelta <= 0.0f && !_isFatigued)
                {
                    _isStealth = false;
                    _verticalVelocity = Mathf.Sqrt(JumpHeight * -2f * Gravity);

                    if (_hasAnimator)
                        _animator.SetBool(_animIDJump, true);
                }

                if (_jumpTimeoutDelta >= 0.0f)
                    _jumpTimeoutDelta -= Time.deltaTime;
            }
            else
            {
                _jumpTimeoutDelta = JumpTimeout;

                if (_fallTimeoutDelta >= 0.0f)
                    _fallTimeoutDelta -= Time.deltaTime;
                else if (_hasAnimator)
                    _animator.SetBool(_animIDFreeFall, true);

                _input.jump = false;
            }

            if (_verticalVelocity < _terminalVelocity)
                _verticalVelocity += Gravity * Time.deltaTime;
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            Gizmos.color = Grounded ? transparentGreen : transparentRed;
            Gizmos.DrawSphere(
                new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z),
                GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                if (FootstepAudioClips.Length > 0)
                {
                    var index = Random.Range(0, FootstepAudioClips.Length);
                    AudioSource.PlayClipAtPoint(FootstepAudioClips[index],
                        transform.TransformPoint(_controller.center), FootstepAudioVolume);
                }
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
                AudioSource.PlayClipAtPoint(LandingAudioClip,
                    transform.TransformPoint(_controller.center), FootstepAudioVolume);
        }
    }
}