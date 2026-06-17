using UnityEngine;

namespace Game.PlayerV2
{
    /// <summary>
    /// Tunable movement values for <see cref="PlayerMotor"/>. Defaults match PLAYER_VALUES.md.
    /// </summary>
    [System.Serializable]
    public class MovementConfig
    {
        [Header("Speeds (m/s)")]
        public float RunSpeed = 5f;
        public float SprintSpeed = 12f;
        public float StealthSpeed = 2f;
        public float FatiguedSpeed = 1.2f;

        [Header("Accel / Rotation")]
        [Tooltip("Acceleration / deceleration toward target speed.")]
        public float SpeedChangeRate = 10f;
        [Range(0f, 0.3f)]
        [Tooltip("How fast the character turns to face the movement direction.")]
        public float RotationSmoothTime = 0.12f;

        [Header("Jump / Gravity")]
        public float JumpHeight = 1.2f;
        public float Gravity = -15f;
        [Tooltip("Delay before being able to jump again after landing.")]
        public float JumpTimeout = 0.3f;
        [Tooltip("Grace period before the fall animation plays (e.g. walking off a step).")]
        public float FallTimeout = 0.15f;
        public float TerminalVelocity = 53f;
    }

    /// <summary>
    /// Owns ALL character movement mechanics and the locomotion animator writes, reproducing
    /// the StarterAssets ThirdPersonController feel exactly: stick-magnitude-scaled target
    /// speed with SpeedChangeRate lerp, camera-relative SmoothDampAngle rotation, and
    /// timeout-driven jump/gravity. States are thin policy (which speed + which transitions);
    /// they drive the motor each frame so the per-frame movement state (speed, rotation
    /// velocity, vertical velocity) persists across state changes.
    ///
    /// Plain class (not a MonoBehaviour) so it's driven explicitly by the active state with
    /// no Update-ordering ambiguity. Animator parameters are cached as hash IDs once (no
    /// per-frame string lookups / allocations).
    /// </summary>
    public class PlayerMotor
    {
        // -- References --
        private readonly CharacterController _controller;
        private readonly Transform _transform;
        private readonly Animator _animator;
        private readonly Transform _cameraTransform; // main camera, for camera-relative movement
        private readonly bool _hasAnimator;

        // -- Config --
        private readonly MovementConfig _cfg;

        // -- Runtime movement state (persists across state transitions) --
        private float _speed;
        private float _animationBlend;
        private float _targetRotation;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;
        private Vector3 _moveDirection = Vector3.forward;

        // -- Decaying horizontal launch (e.g. climb jump-off), additive on top of input movement --
        private Vector3 _launchVelocity;
        private float _launchDecayRate;

        // -- Animator hash IDs --
        private readonly int _hSpeed, _hMotionSpeed, _hGrounded, _hJump, _hFreeFall, _hSprint, _hFatigued, _hStealth;
        // change-cached bools (SetBool only on change, like TPC)
        private bool _wasSprint, _wasFatigued, _wasStealth;

        private const float _speedOffset = 0.1f;

        /// <summary>If false, the character does not rotate to face movement (set by aim modes).</summary>
        public bool RotateOnMove { get; set; } = true;

        public float RunSpeed => _cfg.RunSpeed;
        public float SprintSpeed => _cfg.SprintSpeed;
        public float StealthSpeed => _cfg.StealthSpeed;
        public float FatiguedSpeed => _cfg.FatiguedSpeed;

        public float VerticalVelocity => _verticalVelocity;
        public float CurrentSpeed => _speed;
        /// <summary>True once the post-landing jump cooldown has elapsed.</summary>
        public bool CanJump => _jumpTimeoutDelta <= 0f;

        public PlayerMotor(CharacterController controller, Transform transform, Animator animator,
                           Transform cameraTransform, MovementConfig config)
        {
            _controller = controller;
            _transform = transform;
            _animator = animator;
            _cameraTransform = cameraTransform;
            _cfg = config ?? new MovementConfig();
            _hasAnimator = animator != null;

            _hSpeed = Animator.StringToHash(Constants.ANIM_SPEED);
            _hMotionSpeed = Animator.StringToHash(Constants.ANIM_MOTION_SPEED);
            _hGrounded = Animator.StringToHash(Constants.ANIM_GROUNDED);
            _hJump = Animator.StringToHash(Constants.ANIM_JUMP);
            _hFreeFall = Animator.StringToHash(Constants.ANIM_FREE_FALL);
            _hSprint = Animator.StringToHash(Constants.ANIM_SPRINT);
            _hFatigued = Animator.StringToHash(Constants.ANIM_FATIGUED);
            _hStealth = Animator.StringToHash(Constants.ANIM_STEALTH);

            _jumpTimeoutDelta = _cfg.JumpTimeout;
            _fallTimeoutDelta = _cfg.FallTimeout;
        }

        /// <summary>
        /// Grounded locomotion for one frame: keeps the character grounded, applies
        /// horizontal movement at <paramref name="targetSpeed"/>, updates locomotion
        /// animator flags, applies gravity, and moves the controller.
        /// </summary>
        public void TickGrounded(Vector2 moveInput, float targetSpeed, bool ignoreStickScaling,
                                 bool isSprinting, bool isStealth, bool isFatigued, float dt)
        {
            _fallTimeoutDelta = _cfg.FallTimeout;

            if (_hasAnimator)
            {
                _animator.SetBool(_hGrounded, true);
                _animator.SetBool(_hJump, false);
                _animator.SetBool(_hFreeFall, false);
            }

            // stick to the ground
            if (_verticalVelocity < 0f) _verticalVelocity = -2f;

            // count down the post-landing jump cooldown
            if (_jumpTimeoutDelta >= 0f) _jumpTimeoutDelta -= dt;

            MoveHorizontal(moveInput, targetSpeed, ignoreStickScaling, dt);
            UpdateLocomotionFlags(isSprinting, isStealth, isFatigued);
            ApplyGravity(dt);
            ApplyMove(dt);
        }

        /// <summary>
        /// Airborne locomotion for one frame: full air control at <paramref name="targetSpeed"/>,
        /// fall-timeout-driven FreeFall, gravity, and controller move.
        /// </summary>
        public void TickAir(Vector2 moveInput, float targetSpeed, bool ignoreStickScaling,
                            bool grounded, float dt)
        {
            // can't immediately re-jump after landing
            _jumpTimeoutDelta = _cfg.JumpTimeout;

            if (_hasAnimator) _animator.SetBool(_hGrounded, grounded);

            if (_fallTimeoutDelta >= 0f)
                _fallTimeoutDelta -= dt;
            else if (_hasAnimator)
                _animator.SetBool(_hFreeFall, true);

            MoveHorizontal(moveInput, targetSpeed, ignoreStickScaling, dt);
            ApplyGravity(dt);
            ApplyMove(dt);
        }

        /// <summary>
        /// Clears horizontal speed and zeroes the locomotion animator. Called when an
        /// external system takes over (ExternalControl) so the character doesn't keep
        /// playing a run animation while frozen/dragged.
        /// </summary>
        public void SuspendLocomotion()
        {
            _speed = 0f;
            _animationBlend = 0f;
            if (_hasAnimator)
            {
                _animator.SetFloat(_hSpeed, 0f);
                _animator.SetFloat(_hMotionSpeed, 0f);
            }
        }

        /// <summary>Applies the upward jump impulse and sets the Jump animator bool.</summary>
        public void BeginJump()
        {
            // v = sqrt(h * -2 * g)
            _verticalVelocity = Mathf.Sqrt(_cfg.JumpHeight * -2f * _cfg.Gravity);
            _jumpTimeoutDelta = _cfg.JumpTimeout;
            if (_hasAnimator)
            {
                _animator.SetBool(_hJump, true);
                _animator.SetBool(_hGrounded, false);
            }
        }

        /// <summary>Sets vertical velocity directly (climb jump-off impulse / zeroed exit).</summary>
        public void SetVerticalVelocity(float v) => _verticalVelocity = v;

        /// <summary>
        /// Adds a decaying horizontal launch velocity, applied additively on top of input-driven
        /// movement and decayed toward zero each frame (see <see cref="ApplyMove"/>). Used for a
        /// climb jump-off that arcs away from the wall before blending back to normal air control.
        /// Zero unless set, so ordinary locomotion is unaffected.
        /// </summary>
        public void AddLaunchVelocity(Vector3 horizontalWorld, float decayRate)
        {
            horizontalWorld.y = 0f;
            _launchVelocity = horizontalWorld;
            _launchDecayRate = Mathf.Max(0.01f, decayRate);
        }

        private void MoveHorizontal(Vector2 moveInput, float targetSpeed, bool ignoreStickScaling, float dt)
        {
            if (moveInput == Vector2.zero) targetSpeed = 0f;

            float currentHorizontalSpeed =
                new Vector3(_controller.velocity.x, 0f, _controller.velocity.z).magnitude;

            float inputMagnitude = Mathf.Clamp01(moveInput.magnitude);
            float scaledTarget = ignoreStickScaling ? targetSpeed : targetSpeed * inputMagnitude;

            if (currentHorizontalSpeed < scaledTarget - _speedOffset ||
                currentHorizontalSpeed > scaledTarget + _speedOffset)
            {
                _speed = Mathf.Lerp(currentHorizontalSpeed, scaledTarget, dt * _cfg.SpeedChangeRate);
                _speed = Mathf.Round(_speed * 1000f) / 1000f;
            }
            else
            {
                _speed = scaledTarget;
            }

            // Speed param is normalized to SprintSpeed (0..1 blend tree).
            float normalizedTarget = scaledTarget / _cfg.SprintSpeed;
            _animationBlend = Mathf.Lerp(_animationBlend, normalizedTarget, dt * _cfg.SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;

            Vector3 inputDirection = new Vector3(moveInput.x, 0f, moveInput.y).normalized;

            if (moveInput != Vector2.zero)
            {
                float cameraYaw = _cameraTransform != null ? _cameraTransform.eulerAngles.y : 0f;
                _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg + cameraYaw;

                float rotation = Mathf.SmoothDampAngle(_transform.eulerAngles.y, _targetRotation,
                    ref _rotationVelocity, _cfg.RotationSmoothTime);

                if (RotateOnMove)
                    _transform.rotation = Quaternion.Euler(0f, rotation, 0f);
            }

            _moveDirection = Quaternion.Euler(0f, _targetRotation, 0f) * Vector3.forward;

            if (_hasAnimator)
            {
                _animator.SetFloat(_hSpeed, _animationBlend);
                _animator.SetFloat(_hMotionSpeed, inputMagnitude);
            }
        }

        private void UpdateLocomotionFlags(bool isSprinting, bool isStealth, bool isFatigued)
        {
            if (!_hasAnimator) return;
            if (isSprinting != _wasSprint) { _animator.SetBool(_hSprint, isSprinting); _wasSprint = isSprinting; }
            if (isFatigued != _wasFatigued) { _animator.SetBool(_hFatigued, isFatigued); _wasFatigued = isFatigued; }
            if (isStealth != _wasStealth) { _animator.SetBool(_hStealth, isStealth); _wasStealth = isStealth; }
        }

        private void ApplyGravity(float dt)
        {
            if (_verticalVelocity < _cfg.TerminalVelocity)
                _verticalVelocity += _cfg.Gravity * dt;
        }

        private void ApplyMove(float dt)
        {
            _controller.Move(_moveDirection.normalized * (_speed * dt) +
                             _launchVelocity * dt +
                             new Vector3(0f, _verticalVelocity, 0f) * dt);

            // Decay the launch impulse toward zero so air control blends back to normal.
            if (_launchVelocity != Vector3.zero)
                _launchVelocity = Vector3.MoveTowards(_launchVelocity, Vector3.zero, _launchDecayRate * dt);
        }
    }
}
