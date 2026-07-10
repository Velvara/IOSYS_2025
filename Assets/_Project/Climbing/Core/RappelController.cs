using System;
using Game.PlayerV2;
using Game.PlayerV2.Systems;
using RootMotion.FinalIK;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Game.Climbing
{
    /// <summary>How a rappel ended — the rope system reacts to each differently.</summary>
    public enum RappelExitKind
    {
        /// <summary>Mantled over the top lip — standing on top, still holding the rope.</summary>
        ToppedOut,
        /// <summary>Reached ground at the bottom of the wall — standing, still holding the rope.</summary>
        SteppedOffBottom,
        /// <summary>Player let go mid-wall (Use) — the rope should be dropped/parked.</summary>
        Detached
    }

    /// <summary>Start data handed over by the rope system (Game.Tools) — primitives only, so
    /// Game.Climbing stays independent of the rope assembly.</summary>
    public struct RappelStart
    {
        public Vector3 WallPoint;    // first wall contact just below the lip
        public Vector3 WallNormal;   // wall face normal (points away from the wall)
        public Vector3 AnchorPoint;  // rope anchor (top of the placed anchor)
        public float RopeLength;     // total rope available
        public float WallDistance;   // body standoff from the wall to rappel at; 0 = use the controller default
    }

    /// <summary>
    /// Wall rappel: takes the body over through the PlayerV2 seam (like ClimbController), presses the
    /// player against the wall below a ledge in a procedural rappel pose (root leaned back, feet
    /// planted on the wall via FBBIK — no clips), and moves it up/down along the wall, transform-driven.
    /// Down extends the rope until it runs out (hard stop); up costs stamina and tops out with the
    /// climb-style ClimbUp mantle; Use detaches (drop + fall). Exits are reported through
    /// <see cref="OnRappelExited"/> so the rope system (which owns the rope itself) can react.
    /// </summary>
    public class RappelController : MonoBehaviour
    {
        [Header("Pose")]
        [Tooltip("Root distance from the wall face.")]
        public float bodyWallDistance = 0.6f;
        [Tooltip("Backward lean of the body away from the wall (degrees). Negative if it leans the wrong way on your rig.")]
        public float leanAngle = 40f;
        [Tooltip("Seconds to blend position/rotation into the rappel pose on entry.")]
        public float enterDuration = 0.35f;
        [Tooltip("How fast the body turns to track wall curvature while moving.")]
        public float rotationLerpSpeed = 8f;

        [Header("Feet (FBBIK)")]
        [Tooltip("Half-distance between the feet on the wall.")]
        public float footSpread = 0.18f;
        [Tooltip("Vertical offset of the planted feet relative to the ROOT — which is the controller " +
                 "pivot at the character's feet, not the hips. Positive = up. ~0.4–0.5 keeps the legs " +
                 "in a climbing-like range under the leaned-back body.")]
        public float footHeightAboveRoot = 0.45f;
        [Tooltip("A foot re-steps when its planted point drifts this far from the desired spot.")]
        public float footStepThreshold = 0.35f;
        public float footStepDuration = 0.18f;
        [Tooltip("Free-hang → wall plant-in only: seconds for the feet to step onto their first holds. Both " +
                 "feet step together, so this is roughly the whole plant-in duration. Separate from " +
                 "footStepDuration so the plant-in can be snappier without affecting normal stepping.")]
        public float plantStepDuration = 0.1f;
        [Tooltip("How much the toes angle to the foot's OWN SIDE in character space (same convention as ClimbController).")]
        public float footToeSide = 0.5f;
        [Tooltip("How far the planted foot sits OFF the wall face along its normal (metres). Raise this if the " +
                 "foot mesh clips into the wall; it moves only the feet, leaving the body/hands pose untouched.")]
        public float footWallOffset = 0.05f;
        [Tooltip("Euler offset on the LEFT planted foot — the rig's foot-bone axis correction. COPY the tuned value " +
                 "from ClimbController's 'Foot Plant Rotation'.")]
        public Vector3 footPlantRotation = Vector3.zero;
        [Tooltip("Per-axis sign multiplier applied to footPlantRotation for the RIGHT foot (copy from ClimbController).")]
        public Vector3 footPlantMirror = new Vector3(-1f, 1f, 1f);
        [Tooltip("Effector-level rotation offset on the planted FOOT bone (same mechanism as ClimbController's " +
                 "'Foot Grip Rotation' — applied at IK write time, on top of the plant convention). Fine-tunes the " +
                 "sole's orientation live without touching the plant math.")]
        public Vector3 footGripRotation = Vector3.zero;
        [Tooltip("Per-axis sign multiplier applied to footGripRotation for the RIGHT foot (mirror the chiral bone).")]
        public Vector3 footGripMirror = new Vector3(-1f, 1f, 1f);
        [Tooltip("Extra DOWNWARD offset of the foot targets while descending, so the feet lead the body " +
                 "down the wall instead of trailing above it (trailing feet also distort the torso lean).")]
        public float footDownLead = 0.35f;
        [Tooltip("Extra UPWARD offset of the foot targets while ascending. Usually small — feet trailing " +
                 "below look natural on the way up.")]
        public float footUpLead = 0.1f;
        [Tooltip("How fast the lead offset blends when the movement direction changes.")]
        public float footLeadBlendSpeed = 4f;
        [Tooltip("FBBIK master weight fade speed (per second) on enter/exit.")]
        public float rigFadeSpeed = 4f;

        [Header("Movement")]
        public float upSpeed = 1.1f;
        public float downSpeed = 2f;
        [Tooltip("Surfaces the wall/ground probes can hit. The player's own layer is stripped automatically.")]
        public LayerMask wallMask = ~0;
        [Tooltip("Max probe distance from the body to the wall before the wall counts as lost.")]
        public float wallProbeDistance = 1.5f;
        [Tooltip("Ground within this distance below the feet while descending = step off at the bottom.")]
        public float reachBottomDistance = 0.45f;

        [Header("Stamina")]
        [Tooltip("Stamina per second while moving UP the wall. Idle and descending are free (design).")]
        public float rappelUpDrainRate = 8f;

        [Header("Free Hang (RopeAlone)")]
        [Tooltip("Full-body RopeAlone blend-tree layer, entered when the wall runs out below with rope left. " +
                 "Its state is driven by the same RopeMoveY param (-1 down / 0 idle / 1 up).")]
        public string ropeLayerName = "RopeLayer";
        public string ropeAloneStateName = "RopeAlone";
        [Tooltip("Reel-in (up) speed while free-hanging (m/s). Reaching the wall again resumes wall rappel.")]
        public float freeHangUpSpeed = 0.9f;
        [Tooltip("Pay-out (down) speed while free-hanging (m/s). Stops hard at the rope's end.")]
        public float freeHangDownSpeed = 1.5f;
        [Tooltip("Constant stamina/sec while free-hanging (holding bodyweight with no wall to brace on). " +
                 "Emptying the tank makes the character lose grip and let go (falls).")]
        public float ropeAloneDrainRate = 6f;
        [Tooltip("Free-hang uses ONE RopeAlone clip: it plays forward while ascending, backward while descending, " +
                 "and freezes when the player stops. RopeStepSpeed (the state's Speed multiplier) is driven " +
                 "+ this value up / − down / 0 frozen. Tune to match the clip's motion to the body speed.")]
        public float ropeAlonePlaySpeed = 1f;
        [Tooltip("Normalized time (0-1) of the frame the step LANDS on — the 'both hands grabbing the rope' pose. " +
                 "When the player releases, the clip keeps playing to this frame in the travel direction, then " +
                 "freezes there (the stepped stop).")]
        [Range(0f, 1f)] public float ropeAloneStepLandFrame = 0f;
        [Tooltip("Safety net — if the land frame is somehow never reached after releasing, the glide ends after " +
                 "this many metres so it can't drift forever. Set generous.")]
        public float ropeAloneMaxStepDistance = 3f;

        [Header("Input")]
        [Tooltip("Seconds the Cancel button must be HELD (mid-wall or free-hang) to let go of the rope and drop. " +
                 "Use grabs a rope; it no longer drops one — matches the climb's hold-Cancel-to-release.")]
        public float rappelReleaseHoldTime = 1.5f;
        [Tooltip("How fast the RopeLayer (full-body RopeAlone pose) fades in on leg-release / out on re-contact.")]
        public float ropeLayerFadeSpeed = 5f;

        [Header("Mantle (top-out, climb-style)")]
        [Tooltip("Height above the root at which the wall must still exist to keep ascending. When the wall " +
                 "runs out at this height (the lip is below the chest), the top-out starts — well before " +
                 "the body slides past the edge.")]
        public float topOutProbeHeight = 1.1f;
        public float mantleDuration = 1.05f;
        public AnimationCurve mantleUpCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        public AnimationCurve mantleForwardCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("How far inward past the lip the landing probe looks.")]
        public float mantleLandingForward = 0.45f;
        [Tooltip("Required clear space above before mantling.")]
        public float mantleClearanceUp = 1.2f;
        [Tooltip("Post-mantle fade of the ClimbingLayer (ClimbUp end pose → standing idle).")]
        public float mantleGetupFade = 0.35f;
        public float mantleSafetyTimeout = 2.5f;

        [Header("Upper Body")]
        [Tooltip("Optional 1D blend tree state on the AimingUpperBody layer (RopeMoveY: -1 down / 0 idle / 1 up). " +
                 "If the state doesn't exist, the current upper-body pose (HoldRope) is left as-is.")]
        public string upperBodyStateName = "RappelUpperBody";
        [Tooltip("Damping for the RopeMoveY blend parameter.")]
        public float upperBodyBlendDamp = 0.15f;

        [Header("Fatigue Tremble")]
        [Tooltip("Growing horizontal jitter on the elbows/knees while moving on the wall as stamina nears empty.")]
        [SerializeField] private FatigueJitter fatigueJitter = new FatigueJitter();
        [Tooltip("Max forced FBBIK bend weight the tremble drives (ramped by strength). Small so it reads as a " +
                 "slight shake on top of the clip/planted-foot pose rather than overriding it.")]
        [Range(0f, 1f)] public float jitterBendWeight = 0.3f;

        [Header("FinalIK")]
        [Tooltip("Full Body Biped IK on the player rig. Auto-found in children if left null.")]
        [SerializeField] private FullBodyBipedIK ik;
        [SerializeField] private AnimationCurve effectorEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Debug")]
        public bool logRappelEvents;

        /// <summary>Fired once per rappel on exit, with the final rope extension (anchor→body distance).</summary>
        public event Action<RappelExitKind, float> OnRappelExited;

        /// <summary>
        /// Fired the moment a top-out LANDS (getup fade just starting, control still locked). The
        /// rope system restores its upper-body pose here so the fading climb layer reveals the
        /// correct pose underneath — waiting for OnRappelExited leaves the rappel blend visible
        /// for the fade duration.
        /// </summary>
        public event Action OnTopOutSettling;

        /// <summary>Fired when the RopeAlone free-hang pose starts (true) and ends (false). The rope
        /// system routes the rope through its between-feet hold only while this is active, so the
        /// dangling remainder reads as passing between the legs.</summary>
        public event Action<bool> OnFreeHangChanged;

        private enum Phase { Inactive, Entering, PlantingFeet, Rappelling, FreeHang, Mantling, GettingUp }

        public bool IsRappelling => _phase != Phase.Inactive;

        /// <summary>The current free-hang line: the vertical line (XZ meaningful) the body hangs on, the
        /// wall normal it braces toward on a reel-up, and the standoff. Valid while free-hanging — the rope
        /// system captures it so a later below-grab re-hangs at the SAME offset the wall→rope-alone
        /// transition produced (rather than snapping onto the rope closer to the wall).</summary>
        public Vector3 FreeHangPoint => _hangXZ;
        public Vector3 FreeHangNormal => _wallNormal;
        public float FreeHangDistance => _bodyWallDistance;

        private bool _freeHangSignal;

        /// <summary>Raises <see cref="OnFreeHangChanged"/> only on an actual change.</summary>
        private void SetFreeHangSignal(bool active)
        {
            if (_freeHangSignal == active) return;
            _freeHangSignal = active;
            OnFreeHangChanged?.Invoke(active);
        }

        // -- Player seams --
        private IControlLock _controlLock;
        private IPlayerMotor _motor;
        private PlayerStamina _stamina;
        private PlayerCameraRig _cameraRig;
        private InputHandler _input;
        private PlayerInput _playerInput;
        private Animator _animator;
        private EffectorRig _rig;
        private int _climbLayerIndex = -1;
        private int _upperBodyLayerIndex = -1;
        private int _ropeLayerIndex = -1;
        private bool _upperBodyStateActive;
        private int _probeMask;
        private InputAction _cancelAction;   // polled: HELD ≥ rappelReleaseHoldTime drops the rope
        private float _cancelHoldTimer;
        private float _ropeLayerWeight, _ropeLayerWeightTarget;   // RopeAlone full-body pose fade
        private Vector3 _hangXZ;                                  // free-hang line: x,z fixed, body moves only in Y
        private bool _ropeMoving;                                 // free-hang: is the single RopeAlone clip playing / body moving
        private int _ropeMoveDir;                                 // +1 up (clip forward), -1 down (clip backward)
        private float _prevRopeNorm;                              // last frame's fractional RopeAlone normalizedTime (land detection)
        private float _ropeStepTravel;                            // metres since the last press (glide safety if the land frame is missed)

        private static readonly int HClimbUp = Animator.StringToHash("ClimbUp");
        private static readonly int HRopeMoveY = Animator.StringToHash("RopeMoveY");
        private static readonly int HRopeStepSpeed = Animator.StringToHash("RopeStepSpeed");
        private static readonly int HAimMoveX = Animator.StringToHash("AimMoveX");
        private static readonly int HAimMoveY = Animator.StringToHash("AimMoveY");
        private static readonly int HGrounded = Animator.StringToHash("Grounded");
        private static readonly int HJump = Animator.StringToHash("Jump");
        private static readonly int HFreeFall = Animator.StringToHash("FreeFall");

        // -- Rappel state --
        private Phase _phase = Phase.Inactive;
        private Vector3 _wallNormal;
        private Vector3 _anchorPoint;
        private float _ropeLength;
        private float _bodyWallDistance;   // active standoff for this rappel (per-rope, falls back to bodyWallDistance)
        private float _masterWeight, _masterWeightTarget;
        private float _footLead;         // smoothed direction-dependent foot height bias
        private bool _wallLost;          // R5 seam: wall ran out below with rope remaining

        // entry blend
        private float _enterTimer;
        private Vector3 _enterStartPos, _enterTargetPos;
        private Quaternion _enterStartRot;

        // free-hang → wall plant-in: the feet step up onto their first holds from where the hang pose has them
        private Transform _footL, _footR;   // animated foot bones — captured as the step's start pose

        // mantle
        private float _mantleTimer, _getupTimer;
        private Vector3 _mantleStart, _mantleTarget, _mantleFwdAxis;
        private Quaternion _mantleStartRot, _mantleTargetRot;
        private float _mantleUpDist, _mantleFwdDist;

        private void Start()
        {
            _controlLock = GetComponentInParent<IControlLock>();
            _motor = GetComponentInParent<IPlayerMotor>();
            _stamina = GetComponentInParent<PlayerStamina>();
            _cameraRig = GetComponentInParent<PlayerCameraRig>();
            _input = GetComponentInParent<InputHandler>();
            _playerInput = GetComponentInParent<PlayerInput>();
            if (_playerInput != null) _cancelAction = _playerInput.actions["Cancel"];
            _animator = GetComponentInParent<Animator>();

            if (ik == null) ik = GetComponentInChildren<FullBodyBipedIK>(true);
            if (ik != null) _rig = new EffectorRig(ik, effectorEase);
            if (_animator != null)
            {
                _climbLayerIndex = _animator.GetLayerIndex("ClimbingLayer");
                _upperBodyLayerIndex = _animator.GetLayerIndex("AimingUpperBody");
                _ropeLayerIndex = _animator.GetLayerIndex(ropeLayerName);
                _footL = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                _footR = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
            }

            if (_controlLock == null) Debug.LogError("[RappelController] No IControlLock on the player hierarchy.");
            if (_motor == null) Debug.LogError("[RappelController] No IPlayerMotor on the player hierarchy.");
        }

        /// <summary>Takes the body over and enters the rappel pose. Returns false if unavailable
        /// (already rappelling, or another system holds control).</summary>
        public bool BeginRappel(in RappelStart start)
        {
            if (IsRappelling || _rig == null || _motor == null) return false;
            if (_controlLock == null || _controlLock.IsExternalControlActive) return false;

            _wallNormal = start.WallNormal.normalized;
            _anchorPoint = start.AnchorPoint;
            _ropeLength = start.RopeLength;
            _bodyWallDistance = start.WallDistance > 0.001f ? start.WallDistance : bodyWallDistance;
            _wallLost = false;
            _footLead = 0f;

            // Own layer must never satisfy the wall/ground probes.
            _probeMask = wallMask & ~(1 << _motor.Transform.gameObject.layer);

            _controlLock.RequestExternalControl();
            _stamina?.SetClimbState(true, false);   // no drain, no regen — up-movement drain is applied explicitly

            _enterStartPos = _motor.Transform.position;
            _enterStartRot = _motor.Transform.rotation;
            _enterTargetPos = start.WallPoint + _wallNormal * _bodyWallDistance;
            _enterTimer = 0f;

            if (ik != null) ik.enabled = true;
            _masterWeight = 0f;
            _masterWeightTarget = 1f;
            _rig.SetMasterWeight(0f);
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 1f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 1f);
            // Hands stay clip-driven (HoldRope pose) and the body is placed by the transform.
            _rig.SetEffectorWeight(ClimbEffector.LeftHand, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightHand, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RootBody, 0f);

            // Plant the feet where they'll belong once the enter blend LANDS (target pose), not
            // relative to the still-on-the-ledge body — otherwise they start in the wrong spot and
            // only catch up after a few steps down the wall.
            Quaternion targetRot = RappelRotation();
            PlantFootImmediate(ClimbEffector.LeftFoot, _enterTargetPos, targetRot);
            PlantFootImmediate(ClimbEffector.RightFoot, _enterTargetPos, targetRot);

            // The aim-strafe floats are stale mid-walk here; zero them so the masked legs don't
            // freeze mid-stride under the fading-in IK.
            if (_animator != null)
            {
                _animator.SetFloat(HAimMoveX, 0f);
                _animator.SetFloat(HAimMoveY, 0f);
            }

            // Dedicated rappel upper-body blend (RopeMoveY), if the dev has set the state up;
            // otherwise the HoldRope pose the rope system left on the layer stays.
            _upperBodyStateActive = false;
            if (_animator != null && _upperBodyLayerIndex >= 0 &&
                _animator.HasState(_upperBodyLayerIndex, Animator.StringToHash(upperBodyStateName)))
            {
                _animator.SetFloat(HRopeMoveY, 0f);
                // Fixed time: normalized crossfade durations scale with the source state's length,
                // which is huge for blend trees with a near-zero-speed idle child.
                _animator.CrossFadeInFixedTime(upperBodyStateName, 0.25f, _upperBodyLayerIndex);
                _upperBodyStateActive = true;
            }

            // Settle the camera onto the character's back over the enter blend. The character faces the
            // wall (forward = −wallNormal), so their back is the +wallNormal side and the camera should
            // look toward the wall (−wallNormal). Advanced in TickEnter so it lands exactly as the body
            // finishes settling; on any earlier orientation (rope-aim look-down) it eases smoothly.
            Vector3 backLook = Vector3.ProjectOnPlane(-_wallNormal, Vector3.up);
            if (backLook.sqrMagnitude > 1e-4f) _cameraRig?.BeginLookOverride(backLook.normalized);

            _phase = Phase.Entering;
            if (logRappelEvents) Debug.Log("[RappelController] Rappel started.");
            return true;
        }

        /// <summary>
        /// Takes the body onto a rope that has NO wall to brace against — grabbed while hanging in open
        /// air, e.g. the dangling tail below a short wall, grabbed from the ground. Snaps under the grab
        /// point and enters the RopeAlone free-hang directly. Reeling up can still re-contact a wall
        /// (<see cref="ExitFreeHangToWall"/>) and resume normal wall rappel. Returns false if unavailable.
        /// </summary>
        public bool BeginFreeHang(in RappelStart start)
        {
            if (IsRappelling || _rig == null || _motor == null) return false;
            if (_controlLock == null || _controlLock.IsExternalControlActive) return false;

            _wallNormal = start.WallNormal.sqrMagnitude > 0.0001f
                ? start.WallNormal.normalized
                : _motor.Transform.forward;
            _anchorPoint = start.AnchorPoint;
            _ropeLength = start.RopeLength;
            _bodyWallDistance = start.WallDistance > 0.001f ? start.WallDistance : bodyWallDistance;
            _wallLost = true;
            _footLead = 0f;
            _probeMask = wallMask & ~(1 << _motor.Transform.gameObject.layer);

            _controlLock.RequestExternalControl();
            _stamina?.SetClimbState(true, false);

            if (ik != null) ik.enabled = true;
            _masterWeight = 0f;
            _masterWeightTarget = 0f;               // no wall, no planted feet
            _rig.SetMasterWeight(0f);
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.LeftHand, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightHand, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RootBody, 0f);

            if (_animator != null)
            {
                _animator.SetFloat(HAimMoveX, 0f);
                _animator.SetFloat(HAimMoveY, 0f);
            }

            // Prime the wall-rappel upper-body pose UNDERNEATH the full-body RopeAlone layer, so if the
            // player reels up and re-contacts a wall, fading RopeLayer out reveals the correct pose.
            _upperBodyStateActive = false;
            if (_animator != null && _upperBodyLayerIndex >= 0 &&
                _animator.HasState(_upperBodyLayerIndex, Animator.StringToHash(upperBodyStateName)))
            {
                _animator.SetFloat(HRopeMoveY, 0f);
                _animator.CrossFadeInFixedTime(upperBodyStateName, 0.25f, _upperBodyLayerIndex);
                _upperBodyStateActive = true;
            }

            // Snap under the grab point (hang straight down from the rope), keeping the current height.
            Transform body = _motor.Transform;
            body.position = new Vector3(start.WallPoint.x, body.position.y, start.WallPoint.z);
            body.rotation = HangRotation();

            EnterFreeHang();
            return true;
        }

        private void Update()
        {
            if (_phase == Phase.Inactive) return;
            float dt = Time.deltaTime;

            // Hold Cancel to drop the rope (may end the rappel this frame — bail before ticking further).
            TickCancelInput(dt);
            if (_phase == Phase.Inactive) return;

            // ExternalControlState froze the look on entry — rappel wants free-look, like climbing.
            _cameraRig?.SetFrozen(false);

            _masterWeight = Mathf.MoveTowards(_masterWeight, _masterWeightTarget, rigFadeSpeed * dt);
            _rig.SetMasterWeight(_masterWeight);

            // Effector-level foot rotation offset (climb's Foot Grip Rotation equivalent) — re-applied
            // each frame so it's live-tunable. Only bites while the feet are weighted (planted rappel).
            _rig.SetRotationOffset(ClimbEffector.LeftFoot, Quaternion.Euler(footGripRotation));
            _rig.SetRotationOffset(ClimbEffector.RightFoot, Quaternion.Euler(Vector3.Scale(footGripRotation, footGripMirror)));

            // RopeAlone full-body pose fades in on leg-release, out on wall re-contact. Only touched
            // while non-zero so it never fights RopeController's own RopeLayer use during placement.
            if (_ropeLayerIndex >= 0 && (_ropeLayerWeight > 0f || _ropeLayerWeightTarget > 0f))
            {
                _ropeLayerWeight = Mathf.MoveTowards(_ropeLayerWeight, _ropeLayerWeightTarget, ropeLayerFadeSpeed * dt);
                _animator.SetLayerWeight(_ropeLayerIndex, _ropeLayerWeight);
            }

            switch (_phase)
            {
                case Phase.Entering: TickEnter(dt); break;
                case Phase.PlantingFeet: TickPlantingFeet(dt); break;
                case Phase.Rappelling: TickRappel(dt); break;
                case Phase.FreeHang: TickFreeHang(dt); break;
                case Phase.Mantling: TickMantle(dt); break;
                case Phase.GettingUp: TickGetup(dt); break;
            }

            // Fatigue tremble: only while moving on the wall (Rappelling + a movement press). Every other
            // phase passes moving=false, which releases the forced bend so nothing leaks into the pose.
            bool rappelMoving = _phase == Phase.Rappelling && _input != null && Mathf.Abs(_input.MoveInput.y) > 0.1f;
            ApplyLimbBendJitter(rappelMoving, dt);

            _rig.Tick(dt);
        }

        // ─────────────────────────────────────────────── enter ──

        private void TickEnter(float dt)
        {
            _enterTimer += dt;
            float t = enterDuration > 0f ? Mathf.Clamp01(_enterTimer / enterDuration) : 1f;
            float eased = Mathf.SmoothStep(0f, 1f, t);

            Transform body = _motor.Transform;
            body.position = Vector3.Lerp(_enterStartPos, _enterTargetPos, eased);
            body.rotation = Quaternion.Slerp(_enterStartRot, RappelRotation(), eased);

            _cameraRig?.SetLookOverrideProgress(eased);   // camera lands on the back as the body settles

            if (t >= 1f)
            {
                _cameraRig?.ReleaseLookOverride();
                _phase = Phase.Rappelling;
            }
        }

        /// <summary>Face the wall, leaned back away from it by <see cref="leanAngle"/>.</summary>
        private Quaternion RappelRotation()
        {
            Quaternion faceWall = Quaternion.LookRotation(-_wallNormal, Vector3.up);
            return Quaternion.AngleAxis(-leanAngle, faceWall * Vector3.right) * faceWall;
        }

        // ─────────────────────────────────────────── rappelling ──

        private void TickRappel(float dt)
        {
            Transform body = _motor.Transform;

            // Completely drained → lose grip and fall (same as free-hang; extended to the wall).
            if (_stamina != null && _stamina.CurrentStamina <= 0f)
            {
                if (logRappelEvents) Debug.Log("[RappelController] Wall rappel: out of stamina — let go.");
                FinishRappel(RappelExitKind.Detached);
                return;
            }

            float move = _input != null ? _input.MoveInput.y : 0f;   // stick up = ascend

            // Up costs stamina; empty tank blocks ascending (descending stays free).
            bool movingUp = move > 0.1f;
            bool movingDown = move < -0.1f;
            if (movingUp)
            {
                if (_stamina != null && _stamina.CurrentStamina <= 0f) movingUp = false;
                else _stamina?.SpendStamina(rappelUpDrainRate * dt);
            }

            // Tentative move along the wall's downhill direction.
            Vector3 wallDown = Vector3.ProjectOnPlane(Vector3.down, _wallNormal).normalized;
            Vector3 delta = Vector3.zero;
            if (movingUp) delta = -wallDown * (upSpeed * dt);
            else if (movingDown && !_wallLost) delta = wallDown * (downSpeed * dt);

            // Hard stop at the rope's end: never move to a point that needs more rope than we have.
            if (movingDown && delta != Vector3.zero &&
                Vector3.Distance(_anchorPoint, body.position + delta) > _ropeLength)
                delta = Vector3.zero;

            Vector3 tentative = body.position + delta;

            // Ascending: the wall must still exist at chest height, otherwise we're at the lip —
            // top out now (root-level probes would let the body slide past the edge first).
            if (movingUp && delta != Vector3.zero)
            {
                Vector3 upperOrigin = tentative + Vector3.up * topOutProbeHeight + _wallNormal * 0.3f;
                if (!Physics.Raycast(upperOrigin, -_wallNormal, wallProbeDistance + 0.3f,
                                     _probeMask, QueryTriggerInteraction.Ignore))
                {
                    if (TryTopOut()) return;
                    // No standable top found — hold instead of climbing into thin air.
                    delta = Vector3.zero;
                    tentative = body.position;
                }
            }

            // Follow the wall: re-probe from the tentative position.
            Vector3 probeOrigin = tentative + _wallNormal * 0.3f;
            if (Physics.Raycast(probeOrigin, -_wallNormal, out RaycastHit wallHit,
                                wallProbeDistance + 0.3f, _probeMask, QueryTriggerInteraction.Ignore))
            {
                _wallNormal = wallHit.normal;
                _wallLost = false;
                body.position = wallHit.point + _wallNormal * _bodyWallDistance;
            }
            else if (movingUp)
            {
                // Above the lip — try to top out; if the landing probes fail, hold position.
                if (TryTopOut()) return;
            }
            else if (movingDown)
            {
                // Wall ran out below with rope remaining. If the ground is right there, step off;
                // otherwise release the legs and hang from the rope (RopeAlone free-hang).
                if (Physics.Raycast(body.position + Vector3.up * 0.1f, Vector3.down,
                                    reachBottomDistance + 0.1f, _probeMask, QueryTriggerInteraction.Ignore))
                    FinishRappel(RappelExitKind.SteppedOffBottom);
                else
                    EnterFreeHang();
                return;
            }

            body.rotation = Quaternion.Slerp(body.rotation, RappelRotation(), rotationLerpSpeed * dt);

            // Feet lead the body in the travel direction (mostly downward — trailing feet above a
            // descending body also drag the torso into extra lean via the IK solve).
            float leadTarget = 0f;
            if (delta != Vector3.zero) leadTarget = movingUp ? footUpLead : -footDownLead;
            _footLead = Mathf.MoveTowards(_footLead, leadTarget, footLeadBlendSpeed * dt);

            // Drive the upper-body blend from the EFFECTIVE movement (0 when blocked/stopped).
            if (_upperBodyStateActive)
            {
                float blend = delta == Vector3.zero ? 0f : (movingUp ? 1f : -1f);
                _animator.SetFloat(HRopeMoveY, blend, upperBodyBlendDamp, dt);
            }

            // Bottom: ground close below while descending → step off, still holding the rope.
            if (movingDown &&
                Physics.Raycast(body.position + Vector3.up * 0.1f, Vector3.down,
                                reachBottomDistance + 0.1f, _probeMask, QueryTriggerInteraction.Ignore))
            {
                FinishRappel(RappelExitKind.SteppedOffBottom);
                return;
            }

            UpdateFeet();
        }

        /// <summary>Steps one foot at a time toward its desired wall point when it drifts too far.</summary>
        private void UpdateFeet()
        {
            if (_rig.IsMoving(ClimbEffector.LeftFoot) || _rig.IsMoving(ClimbEffector.RightFoot)) return;

            // A foot only re-steps onto REAL wall — near the lip its probe finds air, and stepping
            // onto the virtual fallback point would paw at nothing. It stays planted instead.
            float lErr = FootError(ClimbEffector.LeftFoot, out Vector3 lPos, out Quaternion lRot, out bool lOnWall);
            float rErr = FootError(ClimbEffector.RightFoot, out Vector3 rPos, out Quaternion rRot, out bool rOnWall);
            if (!lOnWall) lErr = 0f;
            if (!rOnWall) rErr = 0f;

            if (lErr < footStepThreshold && rErr < footStepThreshold) return;

            if (lErr >= rErr) _rig.SetPoseTarget(ClimbEffector.LeftFoot, lPos, lRot, footStepDuration);
            else _rig.SetPoseTarget(ClimbEffector.RightFoot, rPos, rRot, footStepDuration);
        }

        private float FootError(ClimbEffector foot, out Vector3 target, out Quaternion rot, out bool onWall)
        {
            Transform body = _motor.Transform;
            DesiredFootPoint(foot, body.position, body.rotation, out target, out rot, out onWall);
            return Vector3.Distance(_rig.GetCurrentPosition(foot), target);
        }

        private void DesiredFootPoint(ClimbEffector foot, Vector3 basePos, Quaternion baseRot,
                                      out Vector3 point, out Quaternion rot, out bool onWall)
        {
            float sideSign = foot == ClimbEffector.LeftFoot ? -1f : 1f;
            Vector3 right = Vector3.Cross(Vector3.up, -_wallNormal).normalized;

            float footHeight = footHeightAboveRoot + _footLead;
            Vector3 normal = _wallNormal;
            Vector3 probe = basePos + right * (sideSign * footSpread) + Vector3.up * footHeight + _wallNormal * 0.4f;
            onWall = Physics.Raycast(probe, -_wallNormal, out RaycastHit hit, 1.2f, _probeMask, QueryTriggerInteraction.Ignore);
            if (onWall)
            {
                point = hit.point + hit.normal * footWallOffset;
                normal = hit.normal;
            }
            else
            {
                point = basePos + right * (sideSign * footSpread) + Vector3.up * footHeight - _wallNormal * (_bodyWallDistance - 0.05f);
            }

            // Climb-convention plant: sole ON the wall (foot up = wall normal), toes pointing up the
            // wall with a slight side splay, then the rig's foot-bone euler correction (mirrored right).
            Vector3 toe = baseRot * Vector3.up + baseRot * Vector3.right * (sideSign * footToeSide);
            toe = Vector3.ProjectOnPlane(toe, normal);
            rot = toe.sqrMagnitude > 1e-5f
                ? Quaternion.LookRotation(toe.normalized, normal)
                : Quaternion.LookRotation(-normal, Vector3.up);
            Vector3 off = sideSign < 0f ? footPlantRotation : Vector3.Scale(footPlantRotation, footPlantMirror);
            rot *= Quaternion.Euler(off);
        }

        private void PlantFootImmediate(ClimbEffector foot, Vector3 basePos, Quaternion baseRot)
        {
            DesiredFootPoint(foot, basePos, baseRot, out Vector3 p, out Quaternion r, out _);   // fallback point OK for the initial plant
            _rig.SnapToPose(foot, p, r);
        }

        /// <summary>Fatigue tremble on the four limb bends. Unlike ClimbController (which already drives its
        /// bend directions), rappel leaves them to the clip/derived bend — so this drives them itself only
        /// while fatigued+moving, at a small weight that ramps with strength, and releases (weight 0)
        /// otherwise so the normal pose is untouched.</summary>
        private void ApplyLimbBendJitter(bool moving, float dt)
        {
            if (ik == null || ik.solver == null || !ik.solver.initiated) return;

            float strength = (moving && _stamina != null) ? fatigueJitter.Strength(_stamina.NormalizedStamina) : 0f;
            fatigueJitter.Advance(strength, dt);

            var solver = ik.solver;
            if (strength <= 0f)
            {
                solver.leftArmChain.bendConstraint.weight = 0f;
                solver.rightArmChain.bendConstraint.weight = 0f;
                solver.leftLegChain.bendConstraint.weight = 0f;
                solver.rightLegChain.bendConstraint.weight = 0f;
                return;
            }

            Vector3 right = _motor.Transform.right;
            Vector3 down = Vector3.down;
            Vector3 awayWall = _wallNormal;                 // knees bend away from the wall
            float w = jitterBendWeight * strength;          // small forced bend, ramps with fatigue

            SetBendJitter(solver.leftArmChain.bendConstraint,  (down - right * 0.5f).normalized, right, strength, 0f, w);
            SetBendJitter(solver.rightArmChain.bendConstraint, (down + right * 0.5f).normalized, right, strength, Mathf.PI, w);
            SetBendJitter(solver.leftLegChain.bendConstraint,  (awayWall - right * 0.3f).normalized, right, strength, Mathf.PI * 0.5f, w);
            SetBendJitter(solver.rightLegChain.bendConstraint, (awayWall + right * 0.3f).normalized, right, strength, Mathf.PI * 1.5f, w);
        }

        private void SetBendJitter(IKConstraintBend c, Vector3 baseDir, Vector3 axis, float strength, float phase, float weight)
        {
            c.bendGoal = null;
            c.direction = fatigueJitter.Perturb(baseDir, axis, strength, phase).normalized;
            c.weight = weight;
        }

        // ───────────────────────────────────────────── free hang ──

        /// <summary>Wall ran out below with rope left: release the feet and hang from the rope in the
        /// full-body RopeAlone pose. Up reels toward the wall, down pays out to the rope's end.</summary>
        private void EnterFreeHang()
        {
            _phase = Phase.FreeHang;
            _masterWeightTarget = 0f;                   // feet release from the (now absent) wall
            _hangXZ = _motor.Transform.position;        // hang straight down from here; only Y changes
            _ropeMoving = false;                        // start frozen at the entry pose; a press starts moving
            _ropeMoveDir = 0;
            _prevRopeNorm = 0f;
            _ropeStepTravel = 0f;

            if (_animator != null && _ropeLayerIndex >= 0 &&
                _animator.HasState(_ropeLayerIndex, Animator.StringToHash(ropeAloneStateName)))
            {
                _animator.SetFloat(HRopeMoveY, 0f);
                _animator.SetFloat(HRopeStepSpeed, 0f);   // animation frozen until a step begins (idle = stopped)
                _ropeLayerWeightTarget = 1f;
                // Fixed time: keep the fixed-time crossfade regardless of the tree's child speeds.
                _animator.CrossFadeInFixedTime(ropeAloneStateName, 0.25f, _ropeLayerIndex);
            }

            SetFreeHangSignal(true);   // rope now routes through the between-feet hold
            if (logRappelEvents) Debug.Log("[RappelController] Entered RopeAlone free-hang.");
        }

        private void TickFreeHang(float dt)
        {
            Transform body = _motor.Transform;

            // Constant drain — holding your own weight with no wall to stand on. Spent whether the
            // player is actively pressing or the animation is auto-finishing a step to the next
            // both-hands point. Empty tank → let go.
            if (_stamina != null)
            {
                _stamina.SpendStamina(ropeAloneDrainRate * dt);
                if (_stamina.CurrentStamina <= 0f)
                {
                    if (logRappelEvents) Debug.Log("[RappelController] RopeAlone: out of stamina — let go.");
                    FinishRappel(RappelExitKind.Detached);
                    return;
                }
            }

            int inputDir = ReadStepInput();
            bool stopping = inputDir == 0;
            if (!stopping)
            {
                _ropeMoving = true;
                _ropeMoveDir = inputDir;   // single clip: direction IS the speed sign; reverses smoothly
                _ropeStepTravel = 0f;      // a fresh press resets the glide-safety accumulator
            }

            if (!_ropeMoving)
            {
                // Frozen on the land frame (animation stopped); just keep facing the rope.
                if (_animator != null) _animator.SetFloat(HRopeStepSpeed, 0f);
                body.rotation = Quaternion.Slerp(body.rotation, HangRotation(), rotationLerpSpeed * dt);
                return;
            }

            // Sample the RopeAlone clip time before moving, for land-frame crossing detection.
            float normTime = _prevRopeNorm;
            bool haveTime = _animator != null && _ropeLayerIndex >= 0 &&
                            !_animator.IsInTransition(_ropeLayerIndex);
            if (haveTime)
                normTime = Mathf.Repeat(_animator.GetCurrentAnimatorStateInfo(_ropeLayerIndex).normalizedTime, 1f);

            // Move the body along the hang line in the travel direction.
            bool up = _ropeMoveDir > 0;
            float vy = up ? freeHangUpSpeed : -freeHangDownSpeed;
            Vector3 next = body.position + Vector3.up * (vy * dt);
            if (!up && Vector3.Distance(_anchorPoint, next) > _ropeLength)
            {
                next = body.position;      // out of rope: hard stop, freeze the clip here
                FreezeStepNow();
            }
            next.x = _hangXZ.x;
            next.z = _hangXZ.z;
            _ropeStepTravel += Mathf.Abs(next.y - body.position.y);
            body.position = next;
            body.rotation = Quaternion.Slerp(body.rotation, HangRotation(), rotationLerpSpeed * dt);

            // Reeling up: resume rappel only once the wall exists at BOTH the root height AND the foot-plant
            // height. The root probe gives the body wall to follow so it can keep ascending (probing only at
            // the foot level left the root below the wall's lower edge — the wall-follow probe then missed and
            // the body couldn't climb). The foot probe guarantees the feet coincide with real wall so they
            // plant clean instead of reaching for an in-air fallback (the leg-stretch flash). Both required.
            if (up)
            {
                Vector3 rootOrigin = next + _wallNormal * 0.3f;
                Vector3 footOrigin = next + Vector3.up * footHeightAboveRoot + _wallNormal * 0.3f;
                if (Physics.Raycast(rootOrigin, -_wallNormal, out RaycastHit wall,
                                    wallProbeDistance + 0.3f, _probeMask, QueryTriggerInteraction.Ignore) &&
                    Vector3.Angle(wall.normal, Vector3.up) >= 55f &&
                    Physics.Raycast(footOrigin, -_wallNormal, wallProbeDistance + 0.3f,
                                    _probeMask, QueryTriggerInteraction.Ignore))
                {
                    ExitFreeHangToWall(wall);   // wall = the ROOT-height hit; body is planted at that point
                    return;
                }
            }
            // Descending onto ground close below → step off at the bottom (rope drops, character lands).
            else if (Physics.Raycast(body.position + Vector3.up * 0.1f, Vector3.down,
                                     reachBottomDistance + 0.1f, _probeMask, QueryTriggerInteraction.Ignore))
            {
                FinishRappel(RappelExitKind.SteppedOffBottom);
                return;
            }

            // Drive the SINGLE RopeAlone clip: signed speed = travel direction (forward up, backward down).
            if (_animator != null && _ropeMoving)
                _animator.SetFloat(HRopeStepSpeed, _ropeMoveDir * ropeAlonePlaySpeed);

            // Releasing input glides to the next land frame, then freezes there (the stepped stop). Land
            // is detected by the clip time crossing ropeAloneStepLandFrame; a distance safety ends the
            // glide if the frame is somehow never reached.
            if (stopping && _ropeMoving)
            {
                float land = Mathf.Repeat(ropeAloneStepLandFrame, 1f);
                if ((haveTime && CrossedLand(_prevRopeNorm, normTime, land, up)) ||
                    _ropeStepTravel >= ropeAloneMaxStepDistance)
                    FreezeAtLand(land);
            }

            if (haveTime) _prevRopeNorm = normTime;
        }

        private int ReadStepInput()
        {
            float m = _input != null ? _input.MoveInput.y : 0f;
            return m > 0.1f ? 1 : (m < -0.1f ? -1 : 0);
        }

        /// <summary>Did the looping clip time pass the land frame between prev→cur in the travel
        /// direction (handling the 0/1 wrap)?</summary>
        private static bool CrossedLand(float prev, float cur, float land, bool up)
        {
            if (up)                                          // time increasing
                return cur >= prev ? (prev < land && land <= cur)    // no wrap
                                   : (land > prev || land <= cur);   // wrapped 1→0
            return cur <= prev ? (cur <= land && land < prev)        // decreasing, no wrap
                               : (land < prev || land >= cur);       // wrapped 0→1
        }

        /// <summary>Snap the clip exactly onto the land frame and freeze it there.</summary>
        private void FreezeAtLand(float land)
        {
            _ropeMoving = false;
            _ropeStepTravel = 0f;
            if (_animator != null && _ropeLayerIndex >= 0)
            {
                _animator.Play(ropeAloneStateName, _ropeLayerIndex, land);
                _animator.SetFloat(HRopeStepSpeed, 0f);
                _prevRopeNorm = land;
            }
        }

        /// <summary>Freeze the clip wherever it is (hard stops with no clean land frame, e.g. rope end).</summary>
        private void FreezeStepNow()
        {
            _ropeMoving = false;
            _ropeStepTravel = 0f;
            if (_animator != null) _animator.SetFloat(HRopeStepSpeed, 0f);
        }

        /// <summary>Optional animation-event hook at a both-hands frame. NOT required — the stepped stop
        /// is driven by ropeAloneStepLandFrame polling — but if events are present on the (forward) clip
        /// and the player is releasing, this freezes at the event frame too. Negative-speed playback
        /// (descending) doesn't fire events, which is why polling is the primary path.</summary>
        public void OnRopeAloneSync()
        {
            if (_phase != Phase.FreeHang || !_ropeMoving) return;
            if (ReadStepInput() != 0) return;   // still moving under input — pass through
            FreezeStepNow();
        }

        /// <summary>Face the wall/rope horizontally, upright — no lean, there's nothing to brace on.</summary>
        private Quaternion HangRotation()
        {
            Vector3 faceDir = Vector3.ProjectOnPlane(-_wallNormal, Vector3.up);
            if (faceDir.sqrMagnitude < 1e-4f) return _motor.Transform.rotation;
            return Quaternion.LookRotation(faceDir.normalized, Vector3.up);
        }

        /// <summary>Reeled back up to where the wall exists again: begin the scripted plant-in — the feet step
        /// up onto their first holds (input locked, upper body idle) before wall rappel resumes.</summary>
        private void ExitFreeHangToWall(RaycastHit wall)
        {
            _wallNormal = wall.normal;
            _wallLost = false;

            Transform body = _motor.Transform;
            // Plant the root on the ROOT-height wall hit (the caller confirmed wall at both root and foot
            // heights before calling). Root on wall = the wall-follow probe keeps hitting so the body can
            // ascend; feet (footHeightAboveRoot above) land on the wall confirmed just above.
            body.position = wall.point + _wallNormal * _bodyWallDistance;
            // Lean into the braced rappel pose immediately so the hips sit back/down and the holds are within
            // leg reach as the feet step up (upright, they'd be beyond reach and the legs would stretch straight).
            body.rotation = RappelRotation();

            // Re-enable the foot effectors (free-hang zeroed them) at full weight, and SEED them at the current
            // animated foot bones — where the hang pose has the feet — so the plant-in steps up from there
            // rather than snapping. TickPlantingFeet steps them onto the holds one at a time.
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 1f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 1f);
            Vector3 startPosL = _footL != null ? _footL.position : body.position;
            Vector3 startPosR = _footR != null ? _footR.position : body.position;
            Quaternion startRotL = _footL != null ? _footL.rotation : RappelRotation();
            Quaternion startRotR = _footR != null ? _footR.rotation : RappelRotation();
            _rig.SnapToPose(ClimbEffector.LeftFoot, startPosL, startRotL);
            _rig.SnapToPose(ClimbEffector.RightFoot, startPosR, startRotR);

            _masterWeight = 1f;                // feet fully IK-driven for the whole plant-in
            _masterWeightTarget = 1f;
            _rig.SetMasterWeight(1f);
            _ropeLayerWeightTarget = 0f;       // fade the full-body RopeAlone pose out, revealing the wall pose
            _footLead = footUpLead;            // hold the "moving up" foot lead so the step-in reads as ascending
            _phase = Phase.PlantingFeet;

            SetFreeHangSignal(false);          // back on the wall — tail hangs from the lower hand again
            if (logRappelEvents) Debug.Log("[RappelController] RopeAlone: wall regained — planting feet onto the wall.");
        }

        /// <summary>Scripted plant-in from free-hang onto the wall. Input is ignored and the upper-body
        /// up-climb animation is held idle while the feet step up onto their first two holds one at a time
        /// (the normal foot-stepping). Once both are planted and settled, control returns to the player.</summary>
        private void TickPlantingFeet(float dt)
        {
            Transform body = _motor.Transform;
            body.rotation = Quaternion.Slerp(body.rotation, RappelRotation(), rotationLerpSpeed * dt);

            // Upper body stays idle — no wall-rappel up-climb animation during the plant-in.
            if (_upperBodyStateActive)
                _animator.SetFloat(HRopeMoveY, 0f, upperBodyBlendDamp, dt);

            // Step BOTH feet onto their holds at once (unlike normal one-at-a-time stepping) so the plant-in
            // takes just a single step. Kick the steps while neither foot is moving; once they've settled on
            // (or given up finding) wall, hand control back to the player.
            if (_rig.IsMoving(ClimbEffector.LeftFoot) || _rig.IsMoving(ClimbEffector.RightFoot)) return;

            float lErr = FootError(ClimbEffector.LeftFoot, out Vector3 lPos, out Quaternion lRot, out bool lOnWall);
            float rErr = FootError(ClimbEffector.RightFoot, out Vector3 rPos, out Quaternion rRot, out bool rOnWall);
            bool lStep = lOnWall && lErr >= footStepThreshold;
            bool rStep = rOnWall && rErr >= footStepThreshold;

            if (lStep) _rig.SetPoseTarget(ClimbEffector.LeftFoot, lPos, lRot, plantStepDuration);
            if (rStep) _rig.SetPoseTarget(ClimbEffector.RightFoot, rPos, rRot, plantStepDuration);

            if (!lStep && !rStep) _phase = Phase.Rappelling;   // both planted (or off-wall) → control returns
        }

        // ─────────────────────────────────────────────── top-out ──

        /// <summary>Climb-style mantle probes from the current body position over the lip.</summary>
        private bool TryTopOut()
        {
            Transform body = _motor.Transform;
            // The wall ran out at topOutProbeHeight — the lip is somewhere below that. Reference the
            // probes from there, pushed to the wall side so they start clear of the leaned-back body.
            Vector3 lipRef = body.position + Vector3.up * topOutProbeHeight - _wallNormal * (_bodyWallDistance - 0.1f);

            CharacterController cc = _motor.Controller;
            bool ccWasEnabled = cc != null && cc.enabled;
            if (ccWasEnabled) cc.enabled = false;
            try
            {
                if (Physics.Raycast(lipRef, Vector3.up, mantleClearanceUp, _probeMask, QueryTriggerInteraction.Ignore))
                {
                    if (logRappelEvents) Debug.Log("[RappelController] Top-out veto: no clearance above the lip.");
                    return false;
                }

                Vector3 inward = Vector3.ProjectOnPlane(-_wallNormal, Vector3.up).normalized;
                Vector3 probeTop = lipRef + inward * mantleLandingForward + Vector3.up * 1.2f;
                if (!Physics.Raycast(probeTop, Vector3.down, out RaycastHit hit,
                                     1.2f + topOutProbeHeight + 0.6f, _probeMask, QueryTriggerInteraction.Ignore))
                {
                    if (logRappelEvents) Debug.Log("[RappelController] Top-out veto: no landing surface past the lip.");
                    return false;
                }
                if (Vector3.Angle(hit.normal, Vector3.up) > 50f)
                {
                    if (logRappelEvents) Debug.Log("[RappelController] Top-out veto: landing too steep.");
                    return false;
                }

                float r = cc != null ? cc.radius : 0.3f;
                float h = cc != null ? Mathf.Max(cc.height, 2f * r) : 1.8f;
                if (Physics.CheckCapsule(hit.point + Vector3.up * (r + 0.02f), hit.point + Vector3.up * (h - r),
                                         r * 0.95f, _probeMask, QueryTriggerInteraction.Ignore))
                {
                    if (logRappelEvents) Debug.Log("[RappelController] Top-out veto: no standing room at the landing.");
                    return false;
                }

                BeginTopOutMantle(hit.point, Quaternion.LookRotation(inward, Vector3.up));
                return true;
            }
            finally
            {
                if (ccWasEnabled) cc.enabled = true;
            }
        }

        private void BeginTopOutMantle(Vector3 landing, Quaternion landRot)
        {
            Transform body = _motor.Transform;
            _phase = Phase.Mantling;
            _mantleTimer = 0f;
            _mantleStart = body.position;
            _mantleTarget = landing;
            _mantleStartRot = body.rotation;
            _mantleTargetRot = landRot;

            Vector3 delta = landing - _mantleStart;
            _mantleUpDist = delta.y;
            Vector3 horiz = new Vector3(delta.x, 0f, delta.z);
            _mantleFwdDist = horiz.magnitude;
            _mantleFwdAxis = _mantleFwdDist > 1e-4f ? horiz / _mantleFwdDist : body.forward;

            // Feet release; the ClimbUp clip owns the pose while the transform is scripted.
            _masterWeightTarget = 0f;
            if (_animator != null && _climbLayerIndex >= 0)
            {
                _animator.SetLayerWeight(_climbLayerIndex, 1f);   // rappel never raised it — the mantle clip needs it visible
                if (_animator.HasState(_climbLayerIndex, HClimbUp))
                    _animator.CrossFadeInFixedTime(HClimbUp, 0.15f, _climbLayerIndex, 0f);
            }

            if (logRappelEvents) Debug.Log("[RappelController] Top-out mantle started.");
        }

        private void TickMantle(float dt)
        {
            _mantleTimer += dt;
            float t = mantleDuration > 0f ? Mathf.Clamp01(_mantleTimer / mantleDuration) : 1f;

            Transform body = _motor.Transform;
            body.position = _mantleStart + Vector3.up * (_mantleUpDist * mantleUpCurve.Evaluate(t))
                                         + _mantleFwdAxis * (_mantleFwdDist * mantleForwardCurve.Evaluate(t));
            body.rotation = Quaternion.Slerp(_mantleStartRot, _mantleTargetRot, Mathf.SmoothStep(0f, 1f, t));

            if (_mantleTimer >= mantleSafetyTimeout) CompleteTopOut();
        }

        /// <summary>Animation event at the end of the ClimbUp clip (shared with the climb mantle —
        /// each controller only reacts while its own mantle is running).</summary>
        public void OnMantleComplete()
        {
            if (_phase == Phase.Mantling) CompleteTopOut();
        }

        private void CompleteTopOut()
        {
            Transform body = _motor.Transform;
            body.position = _mantleTarget;
            body.rotation = _mantleTargetRot;

            // Same base-layer restore the climb mantle does: the locomotion bools froze at their
            // pre-rappel values under external control — without this the getup fade can reveal a
            // stale falling/crouched base pose under the fading ClimbUp.
            if (_animator != null)
            {
                _animator.SetBool(HGrounded, true);
                _animator.SetBool(HJump, false);
                _animator.SetBool(HFreeFall, false);
                _animator.CrossFade("Idle Walk Run Blend", 0.2f, 0);
            }

            // Upper body back to the hold pose NOW (under the still-opaque climb layer), so the
            // getup fade reveals the right pose instead of a lingering rappel blend.
            if (_upperBodyStateActive && _animator != null)
            {
                _animator.SetFloat(HRopeMoveY, 0f);
                _upperBodyStateActive = false;
            }
            OnTopOutSettling?.Invoke();

            if (mantleGetupFade > 0f && _animator != null && _climbLayerIndex >= 0)
            {
                _phase = Phase.GettingUp;
                _getupTimer = 0f;
            }
            else
            {
                FinishRappel(RappelExitKind.ToppedOut);
            }
        }

        private void TickGetup(float dt)
        {
            _getupTimer += dt;
            float t = mantleGetupFade > 0f ? Mathf.Clamp01(_getupTimer / mantleGetupFade) : 1f;
            if (_animator != null && _climbLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLayerIndex, 1f - t);

            if (t >= 1f) FinishRappel(RappelExitKind.ToppedOut);
        }

        // ─────────────────────────────────────────────── exits ──

        /// <summary>Cancel (polled): HELD for <see cref="rappelReleaseHoldTime"/> mid-wall or in free-hang
        /// lets go of the rope and drops (a deliberate hold avoids accidental falls). Use no longer drops —
        /// it's the grab button. The scripted mantle/getup must finish, so only the interruptible phases
        /// respond. Mirrors the climb's hold-Cancel-to-release.</summary>
        private void TickCancelInput(float dt)
        {
            if (_cancelAction == null ||
                !(_phase == Phase.Entering || _phase == Phase.Rappelling || _phase == Phase.FreeHang))
            {
                _cancelHoldTimer = 0f;
                return;
            }

            if (_cancelAction.IsPressed())
            {
                _cancelHoldTimer += dt;
                if (_cancelHoldTimer >= rappelReleaseHoldTime)
                {
                    _cancelHoldTimer = 0f;
                    if (logRappelEvents) Debug.Log("[RappelController] Cancel held — dropping the rope.");
                    FinishRappel(RappelExitKind.Detached);
                }
            }
            else
            {
                _cancelHoldTimer = 0f;
            }
        }

        private void FinishRappel(RappelExitKind kind)
        {
            SetFreeHangSignal(false);   // leaving the rope entirely — clear the between-feet routing
            _cameraRig?.ReleaseLookOverride();   // if we exited mid-enter, hand the camera back to free-look
            _cancelHoldTimer = 0f;

            float finalExtension = Vector3.Distance(_anchorPoint, _motor.Transform.position);

            _phase = Phase.Inactive;
            _masterWeight = 0f;
            _masterWeightTarget = 0f;
            _rig.SetMasterWeight(0f);
            ApplyLimbBendJitter(false, 0f);   // release any forced fatigue bend before the solver sleeps
            if (ik != null) ik.enabled = false;
            if (_animator != null && _climbLayerIndex >= 0) _animator.SetLayerWeight(_climbLayerIndex, 0f);
            if (_animator != null && _ropeLayerIndex >= 0) _animator.SetLayerWeight(_ropeLayerIndex, 0f);
            _ropeLayerWeight = 0f;
            _ropeLayerWeightTarget = 0f;

            if (_upperBodyStateActive && _animator != null)
            {
                _animator.SetFloat(HRopeMoveY, 0f);
                _upperBodyStateActive = false;
                // Deliberately NO crossfade here: Unity IGNORES a CrossFade issued while another
                // transition is running on the same layer, so starting one now would eat the
                // HoldRopePose/UpperBodyIdle re-assert the rope system does inside OnRappelExited
                // (same frame). The exit handler is the single writer for the upper-body layer.
            }

            _stamina?.SetClimbState(false, false);
            _motor?.SetVerticalVelocity(0f);
            _controlLock?.ReleaseExternalControl();

            if (logRappelEvents) Debug.Log($"[RappelController] Rappel ended: {kind} (extension {finalExtension:0.0} m).");
            OnRappelExited?.Invoke(kind, finalExtension);
        }
    }
}
