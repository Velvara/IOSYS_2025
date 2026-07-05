using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.PlayerV2.Systems
{
    /// <summary>
    /// Body-level ragdoll for the player: hard falls anywhere — climb drops, jump-offs, walked-off
    /// cliffs — knock the character into physics, and a Use press once the body has settled blends
    /// back to animation and returns control. External systems (climbing today, powers later) can
    /// also force it via <see cref="IRagdoll.TriggerRagdoll()"/> and react through the
    /// <see cref="RagdollStarting"/> / <see cref="RagdollRecovered"/> events.
    ///
    /// Owns the whole takeover seam: <see cref="IControlLock.RequestExternalControl"/> →
    /// CharacterController + Animator off, bone rigidbodies live (motor velocity carried in) →
    /// the camera follow target tracks the pelvis (free-look stays live) → on recover, the root
    /// teleports under the body, physics freezes, and the pose blends from the settled ragdoll
    /// into the animator over blendToAnimationTime before control returns. The bone
    /// rigidbodies/colliders are DISABLED whenever not ragdolled, so the setup costs nothing in
    /// normal play and can never fight the CharacterController capsule or thrown projectiles.
    ///
    /// EDITOR PREREQ: a ragdoll rig (Ragdoll Wizard) under the humanoid Hips. Kinematic state,
    /// collider enabling and interpolation are all forced from here at Awake, so wizard defaults
    /// are fine as-created; no layer setup is required.
    ///
    /// Runs after PlayerController (execution order) so the per-frame free-look unfreeze wins over
    /// ExternalControlState's freeze-on-enter — the same trick ClimbController uses.
    /// </summary>
    [DefaultExecutionOrder(150)]
    public class PlayerRagdoll : MonoBehaviour, IRagdoll
    {
        [Header("Rig")]
        [Tooltip("Root bone of the ragdoll (the wizard's Pelvis). Auto-resolves to the humanoid Hips if left empty.")]
        [SerializeField] private Transform pelvis;

        [Header("Hard-Fall Trigger")]
        [Tooltip("Ragdoll automatically when any fall exceeds hardFallHeight — climb drops, jump-offs, walking off cliffs. Off = ragdoll only fires when triggered by code (IRagdoll.TriggerRagdoll).")]
        [SerializeField] private bool ragdollOnHardFall = true;
        [Tooltip("Fall distance (m) below the airborne APEX that triggers the ragdoll, while still falling. Watched only while the motor owns the body — never during climbing/hookshot external control.")]
        [SerializeField] private float hardFallHeight = 5f;

        [Header("Recovery (Use to get up)")]
        [Tooltip("Use presses are ignored for this long after the ragdoll starts, so the fall gets to play out.")]
        [SerializeField] private float minRagdollTime = 0.5f;
        [Tooltip("The body counts as settled once the pelvis stays under this speed (m/s)...")]
        [SerializeField] private float settleSpeed = 0.25f;
        [Tooltip("...for this many seconds.")]
        [SerializeField] private float settleTime = 0.3f;
        [Tooltip("Safety: Use recovers even if the body never settles (e.g. sliding on a slope) once this much time has passed.")]
        [SerializeField] private float recoverAnywayAfter = 5f;
        [Tooltip("Seconds the pose blends from the settled ragdoll into the animator on recovery — the placeholder 'get up' until real GetUp clips exist. Control returns when the blend completes.")]
        [SerializeField] private float blendToAnimationTime = 0.35f;
        [Tooltip("Ground layers for the recovery snap: the root teleports to the ground under the pelvis before standing up. Match PlayerController's Ground Layers.")]
        [SerializeField] private LayerMask groundMask = 1;

        [Header("Air Steering (while ragdolled, until ground contact)")]
        [Tooltip("While the ragdolled body is still AIRBORNE, move input steers it with this acceleration (m/s², camera-relative). Steering cuts out FOR GOOD the moment the pelvis touches ground — the downed body can't be crawled around. 0 = no steering at all.")]
        [SerializeField] private float airSteerAcceleration = 8f;
        [Tooltip("Radius of the ground-contact probe around the pelvis (vs groundMask) that ends the steering.")]
        [SerializeField] private float groundTouchRadius = 0.35f;

        [Header("Camera")]
        [Tooltip("Offset above the PELVIS the camera follow target tracks while ragdolled (free-look stays live). The target returns to its normal spot on recovery; Cinemachine damping eases both moves.")]
        [SerializeField] private Vector3 camFollowOffset = new Vector3(0f, 0.4f, 0f);

        [Header("Debug")]
        [Tooltip("Log ragdoll lifecycle events. Disable for shipping/profiling — each log allocates.")]
        [SerializeField] private bool logRagdollEvents = true;

        private enum Phase { Animated, Ragdolled, Recovering }
        private Phase _phase = Phase.Animated;

        // -- Resolved player pieces (same hierarchy, resolved at runtime) --
        private IControlLock _controlLock;
        private IPlayerMotor _motor;
        private PlayerController _controller;   // forgiving ground check; falls back to cc.isGrounded
        private PlayerCameraRig _cameraRig;
        private Animator _animator;
        private PlayerInput _playerInput;
        private InputHandler _inputHandler;   // move input for the airborne steering
        private Transform _camTarget;

        // -- Ragdoll rig (discovered under the pelvis at Awake) --
        private Rigidbody[] _bones = Array.Empty<Rigidbody>();
        private Collider[] _boneColliders = Array.Empty<Collider>();
        private Rigidbody _pelvisRb;

        private float _ragdollTimer;
        private float _settleTimer;
        private bool _settled;
        private bool _groundTouched;   // latched on first pelvis ground contact — ends the air steering
        private Vector3 _camTargetLocalPos;

        // -- Hard-fall watch (apex-relative, armed only while the motor owns the body) --
        private bool _airborne;
        private float _apexY;

        // -- Recovery blend (settled ragdoll pose → animator), captured FRESH each recovery so
        //    runtime-spawned/destroyed children (held items on the hand bones) can't stale the list --
        private Transform[] _blendBones = Array.Empty<Transform>();
        private Vector3[] _blendPos = Array.Empty<Vector3>();
        private Quaternion[] _blendRot = Array.Empty<Quaternion>();
        private float _blendT;

        private static readonly int _hSpeed = Animator.StringToHash(Constants.ANIM_SPEED);
        private static readonly int _hMotionSpeed = Animator.StringToHash(Constants.ANIM_MOTION_SPEED);
        private static readonly int _hGrounded = Animator.StringToHash(Constants.ANIM_GROUNDED);
        private static readonly int _hJump = Animator.StringToHash(Constants.ANIM_JUMP);
        private static readonly int _hFreeFall = Animator.StringToHash(Constants.ANIM_FREE_FALL);

        // ── IRagdoll ─────────────────────────────────────────────────────────
        public bool IsRagdolled => _phase != Phase.Animated;
        public bool IsSettled => _settled;
        public bool CanRecover => _phase == Phase.Ragdolled && _ragdollTimer >= minRagdollTime &&
                                  (_settled || _ragdollTimer >= recoverAnywayAfter);
        public event Action RagdollStarting;
        public event Action RagdollRecovered;

        private void Awake()
        {
            _controlLock = GetComponentInParent<IControlLock>();
            _motor = GetComponentInParent<IPlayerMotor>();
            _controller = GetComponentInParent<PlayerController>();
            _cameraRig = GetComponentInParent<PlayerCameraRig>();
            _animator = GetComponentInParent<Animator>();
            _playerInput = GetComponentInParent<PlayerInput>();
            _inputHandler = GetComponentInParent<InputHandler>();
            _camTarget = _cameraRig != null ? _cameraRig.CameraTarget : null;

            if (pelvis == null && _animator != null && _animator.isHuman)
                pelvis = _animator.GetBoneTransform(HumanBodyBones.Hips);

            if (_controlLock == null)
                Debug.LogError("[PlayerRagdoll] No IControlLock found on the player hierarchy.");
            if (_motor == null)
                Debug.LogError("[PlayerRagdoll] No IPlayerMotor found on the player hierarchy.");
            if (_playerInput == null)
                Debug.LogWarning("[PlayerRagdoll] No PlayerInput found — the Use recovery input won't work.");

            DiscoverBones();
        }

        /// <summary>Gathers the wizard-created ragdoll rigidbodies under the pelvis and puts the physics
        /// rig to sleep (kinematic, colliders off). The joint filter keeps runtime guests out — a held
        /// item with a Rigidbody parented to a hand bone is NOT part of the ragdoll.</summary>
        private void DiscoverBones()
        {
            if (pelvis == null)
            {
                Debug.LogError("[PlayerRagdoll] No pelvis bone (humanoid Hips) — ragdoll disabled.");
                return;
            }

            Rigidbody[] found = pelvis.GetComponentsInChildren<Rigidbody>();
            int n = 0;
            for (int i = 0; i < found.Length; i++)
                if (found[i].transform == pelvis || found[i].GetComponent<Joint>() != null) n++;

            _bones = new Rigidbody[n];
            _boneColliders = new Collider[n];
            int k = 0;
            for (int i = 0; i < found.Length; i++)
            {
                Rigidbody rb = found[i];
                if (rb.transform != pelvis && rb.GetComponent<Joint>() == null) continue;

                _bones[k] = rb;
                _boneColliders[k] = rb.GetComponent<Collider>();
                // Sleep the rig: kinematic + colliders off (zero cost in normal play, and no fights
                // with the CharacterController capsule). Interpolate because the camera reads the
                // pelvis transform in LateUpdate — raw physics steps would jitter it. Speculative CCD
                // so a hard fall can't tunnel bones through thin ground.
                rb.isKinematic = true;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                if (_boneColliders[k] != null) _boneColliders[k].enabled = false;
                if (rb.transform == pelvis) _pelvisRb = rb;
                k++;
            }

            if (_bones.Length == 0)
                Debug.LogWarning("[PlayerRagdoll] No ragdoll rigidbodies under the pelvis — run the " +
                                 "Ragdoll Wizard on the rig (GameObject > 3D Object > Ragdoll...). Ragdoll disabled.");
        }

        private void OnEnable()
        {
            if (_playerInput != null && _playerInput.actions != null)
            {
                var use = _playerInput.actions["Use"];
                if (use != null) use.performed += OnUseInput;
            }
        }

        private void OnDisable()
        {
            if (_playerInput != null && _playerInput.actions != null)
            {
                var use = _playerInput.actions["Use"];
                if (use != null) use.performed -= OnUseInput;
            }
        }

        /// <summary>Use while downed and settled: get up.</summary>
        private void OnUseInput(InputAction.CallbackContext ctx)
        {
            if (CanRecover) Recover();
        }

        private void Update()
        {
            if (_phase == Phase.Animated)
            {
                WatchForHardFall();
                return;
            }

            // Ragdolled / Recovering: keep free-look live (ExternalControlState froze it on entry;
            // this runs after PlayerController, so the unfreeze wins — a no-op once unfrozen).
            _cameraRig?.SetFrozen(false);

            if (_phase != Phase.Ragdolled) return;
            _ragdollTimer += Time.deltaTime;
            if (!_settled && _pelvisRb != null)
            {
                if (_pelvisRb.linearVelocity.sqrMagnitude < settleSpeed * settleSpeed)
                {
                    _settleTimer += Time.deltaTime;
                    if (_settleTimer >= settleTime) _settled = true;
                }
                else _settleTimer = 0f;
            }
        }

        /// <summary>Apex-relative fall watch: arm at the moment the body leaves the ground, track the
        /// highest point, and ragdoll once the fall below it exceeds hardFallHeight. Covers every fall
        /// under motor control — climb drops (climbing has already released), jump-offs, cliff walk-offs.</summary>
        private void WatchForHardFall()
        {
            if (!ragdollOnHardFall || _bones.Length == 0) return;
            // Only while the motor owns the body — climbing/hookshot takeovers pin or fling the
            // transform in ways that aren't a fall.
            if (_controlLock == null || _controlLock.IsExternalControlActive) { _airborne = false; return; }

            bool grounded = _controller != null ? _controller.IsGrounded
                          : (_motor?.Controller != null && _motor.Controller.isGrounded);
            if (grounded) { _airborne = false; return; }

            float y = transform.position.y;
            if (!_airborne) { _airborne = true; _apexY = y; }
            else if (y > _apexY) _apexY = y;

            if (_motor != null && _motor.VerticalVelocity < 0f && (_apexY - y) > hardFallHeight)
                TriggerRagdoll();
        }

        public void TriggerRagdoll() => TriggerRagdoll(Vector3.zero);

        public void TriggerRagdoll(Vector3 extraVelocity)
        {
            if (_phase != Phase.Animated || _bones.Length == 0 || !isActiveAndEnabled) return;

            // Let current holders clean up FIRST (climbing drops its holds/camera and may call
            // ReleaseExternalControl), THEN take the body — so the two takeovers can't step on
            // each other's control flag.
            RagdollStarting?.Invoke();
            _controlLock?.RequestExternalControl();

            CharacterController cc = _motor?.Controller;
            Vector3 carry = (cc != null && cc.enabled ? cc.velocity : Vector3.zero) + extraVelocity;
            if (cc != null) cc.enabled = false;                 // the capsule must not fight the bone colliders
            if (_animator != null) _animator.enabled = false;   // physics owns the pose now

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_boneColliders[i] != null) _boneColliders[i].enabled = true;
                _bones[i].isKinematic = false;
                _bones[i].linearVelocity = carry;               // the fall/launch momentum carries into the bones
                _bones[i].WakeUp();
            }

            if (_camTarget != null) _camTargetLocalPos = _camTarget.localPosition;

            _phase = Phase.Ragdolled;
            _ragdollTimer = 0f;
            _settleTimer = 0f;
            _settled = false;
            _groundTouched = false;
            _airborne = false;
            if (logRagdollEvents) Debug.Log("[PlayerRagdoll] Ragdoll started.");
        }

        /// <summary>Stand the character back up: freeze the physics rig where it lies, teleport the root
        /// under the body (ground-snapped, yawed to the lie direction) without moving the visible pose,
        /// and start the ragdoll→animator blend. Control returns when the blend completes.</summary>
        private void Recover()
        {
            if (_phase != Phase.Ragdolled) return;
            _phase = Phase.Recovering;

            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i].isKinematic = true;
                if (_boneColliders[i] != null) _boneColliders[i].enabled = false;
            }

            // Root target: the ground under the pelvis, yawed to where the body points.
            Vector3 pelvisPos = pelvis.position;
            Vector3 rootPos = pelvisPos;
            if (Physics.Raycast(pelvisPos + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit, 4f,
                                groundMask, QueryTriggerInteraction.Ignore))
                rootPos = hit.point;
            Vector3 fwd = Vector3.ProjectOnPlane(pelvis.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.05f) fwd = Vector3.ProjectOnPlane(pelvis.up, Vector3.up);   // lying flat: hips forward ≈ vertical
            if (fwd.sqrMagnitude < 1e-4f) fwd = transform.forward;
            Quaternion rootRot = Quaternion.LookRotation(fwd.normalized, Vector3.up);

            // Teleport the root WITHOUT moving the visible pose: capture the skeleton's world pose,
            // move the root (bones ride along as children), write the world pose back.
            _blendBones = pelvis.GetComponentsInChildren<Transform>();
            int count = _blendBones.Length;
            if (_blendPos.Length < count)
            {
                _blendPos = new Vector3[count];
                _blendRot = new Quaternion[count];
            }
            for (int i = 0; i < count; i++)
            {
                _blendPos[i] = _blendBones[i].position;
                _blendRot[i] = _blendBones[i].rotation;
            }
            transform.SetPositionAndRotation(rootPos, rootRot);
            for (int i = 0; i < count; i++)
                _blendBones[i].SetPositionAndRotation(_blendPos[i], _blendRot[i]);
            // Re-capture as LOCAL pose — the blend's "from" (world writes above made these current).
            for (int i = 0; i < count; i++)
            {
                _blendPos[i] = _blendBones[i].localPosition;
                _blendRot[i] = _blendBones[i].localRotation;
            }
            _blendT = 0f;

            // Animator back on, parked in grounded idle so the blend has a sane destination
            // (it was disabled mid-fall — without this it would resume the FreeFall pose).
            if (_animator != null)
            {
                _animator.enabled = true;
                _animator.SetBool(_hGrounded, true);
                _animator.SetBool(_hJump, false);
                _animator.SetBool(_hFreeFall, false);
                _animator.SetFloat(_hSpeed, 0f);
                _animator.SetFloat(_hMotionSpeed, 0f);
            }

            CharacterController cc = _motor?.Controller;
            if (cc != null) cc.enabled = true;   // after the root teleport (a disabled CC teleports safely)
            _motor?.SetVerticalVelocity(0f);

            if (_camTarget != null) _camTarget.localPosition = _camTargetLocalPos;

            if (logRagdollEvents) Debug.Log("[PlayerRagdoll] Recovering — blending back to animation.");
        }

        /// <summary>Airborne ragdoll steering: while the body is still flying, move input nudges the pelvis
        /// (camera-relative) so the player can influence where they land. Latches OFF on the first ground
        /// contact — a downed body can't be crawled around, and the settle isn't fought by input.</summary>
        private void FixedUpdate()
        {
            if (_phase != Phase.Ragdolled || _pelvisRb == null) return;

            if (!_groundTouched && Physics.CheckSphere(pelvis.position, groundTouchRadius, groundMask,
                                                       QueryTriggerInteraction.Ignore))
                _groundTouched = true;   // down for good (stays latched through any bounce)

            if (_groundTouched || airSteerAcceleration <= 0f || _inputHandler == null) return;
            Vector2 move = _inputHandler.MoveInput;
            if (move.sqrMagnitude < 0.01f) return;

            // Camera-relative: the follow target's yaw IS the look yaw (PlayerCameraRig drives it).
            Transform basis = _camTarget != null ? _camTarget : transform;
            Vector3 fwd = Vector3.ProjectOnPlane(basis.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            Vector3 dir = fwd * move.y + right * move.x;
            if (dir.sqrMagnitude > 1f) dir.Normalize();
            _pelvisRb.AddForce(dir * airSteerAcceleration, ForceMode.Acceleration);
        }

        private void LateUpdate()
        {
            if (_phase == Phase.Ragdolled)
            {
                // The follow target rides the pelvis so the camera goes with the body (free-look stays
                // live — PlayerCameraRig only writes the target's rotation, we only write its position).
                if (_camTarget != null && pelvis != null)
                    _camTarget.position = pelvis.position + camFollowOffset;
            }
            else if (_phase == Phase.Recovering)
            {
                TickRecoverBlend(Time.deltaTime);
            }
        }

        /// <summary>Runs after the Animator wrote this frame's pose: pull each bone back toward the
        /// captured ragdoll pose by the remaining weight (1 → 0), so the animator pose fades in.</summary>
        private void TickRecoverBlend(float dt)
        {
            _blendT += dt / Mathf.Max(0.0001f, blendToAnimationTime);
            float w = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_blendT));
            if (w > 0f)
            {
                for (int i = 0; i < _blendBones.Length; i++)
                {
                    Transform b = _blendBones[i];
                    if (b == null) continue;   // e.g. a held item destroyed mid-blend
                    b.localPosition = Vector3.Lerp(b.localPosition, _blendPos[i], w);
                    b.localRotation = Quaternion.Slerp(b.localRotation, _blendRot[i], w);
                }
            }

            if (_blendT >= 1f)
            {
                _phase = Phase.Animated;
                _controlLock?.ReleaseExternalControl();   // FSM resumes (Idle/Move/Jump by situation)
                RagdollRecovered?.Invoke();
                if (logRagdollEvents) Debug.Log("[PlayerRagdoll] Recovered — control returned.");
            }
        }
    }
}
