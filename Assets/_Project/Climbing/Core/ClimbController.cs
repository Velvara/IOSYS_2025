using UnityEngine;
using UnityEngine.InputSystem;
using RootMotion.FinalIK;
using Game.PlayerV2;
using Game.PlayerV2.Systems;

namespace Game.Climbing
{
    /// <summary>
    /// The climbing subsystem brain. Lives on the player root, owns the procedural pieces
    /// (<see cref="EffectorRig"/>, <see cref="OscillatorBank"/>, <see cref="TwoMassPendulum"/>), and
    /// takes the body over through the PlayerV2 seam: <see cref="IControlLock.RequestExternalControl"/>
    /// → FSM enters ExternalControlState (locomotion relinquished, look frozen, locomotion animator
    /// zeroed); climbing then re-enables free-look and drives the transform + FinalIK directly.
    ///
    /// SKELETON STAGE: only the entry/exit takeover handshake is wired (debug-key toggle). Grab
    /// detection, hold selection, effector targeting, root offset, and the dynamics tick come next.
    /// Runs after PlayerController (DefaultExecutionOrder) so the free-look re-enable sticks the same
    /// frame the FSM freezes look on entry.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class ClimbController : MonoBehaviour
    {
        [Header("FinalIK")]
        [Tooltip("Full Body Biped IK on the player rig. Auto-found in children if left null.")]
        [SerializeField] private FullBodyBipedIK ik;
        [Tooltip("Ease curve applied to effector moves (LAST → NEXT).")]
        [SerializeField] private AnimationCurve effectorEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Header("Grab / Hang (tune to your character scale)")]
        [Tooltip("Seconds to fade FBBIK weight in on grab.")]
        [SerializeField] private float ikFadeInDuration = 0.15f;
        [Tooltip("Seconds to fade FBBIK weight out on release.")]
        [SerializeField] private float ikFadeOutDuration = 0.15f;
        [Tooltip("Horizontal gap between the two hand holds.")]
        [SerializeField] private float shoulderWidth = 0.45f;
        [Tooltip("How far the body sits back from the wall (along the into-wall direction).")]
        [SerializeField] private float rootForwardOffset = 0.35f;
        [Tooltip("How far the player transform sits below the hands while hanging.")]
        [SerializeField] private float rootDownOffset = 1.4f;
        [Tooltip("Braced body rotation tweens to its new facing over (traverseMoveDuration × this) on each hand step, so it turns smoothly across the hand move and slower traversal yields gentler turns. Higher = slower/smoother (the turn carries into the next step); lower = snappier.")]
        [SerializeField] private float bodyTurnDurationScale = 1.25f;
        [Tooltip("Euler offset for the LEFT hand effector rotation (palm against the hold). Often ~ (0,180,0).")]
        [SerializeField] private Vector3 leftHandGripRotation = new Vector3(0f, 180f, 0f);
        [Tooltip("Euler offset for the RIGHT hand effector rotation — mirror of the left; tune to keep the elbow natural.")]
        [SerializeField] private Vector3 rightHandGripRotation = new Vector3(0f, 180f, 0f);
        [Tooltip("Forces each arm's elbow toward an explicit down/out bend via FBBIK bend constraints, independent of hand rotation (fixes the mirrored right elbow). 1 = full control.")]
        [Range(0f, 1f)]
        [SerializeField] private float elbowBendWeight = 1f;
        [Tooltip("How far the elbows point outward vs straight down (0 = straight down).")]
        [SerializeField] private float elbowOutward = 0.5f;

        [Header("Traversal")]
        [Tooltip("Ideal reach distance for one hand move in the input direction.")]
        [SerializeField] private float traverseStep = 0.4f;
        [Tooltip("Seconds a hand takes to move to a new hold.")]
        [SerializeField] private float traverseMoveDuration = 0.28f;
        [Tooltip("Minimum Move-input magnitude to start a traverse.")]
        [SerializeField] private float minMoveInput = 0.5f;
        [Tooltip("Minimum gap (seconds) between hand moves.")]
        [SerializeField] private float moveInterval = 0.08f;
        [Tooltip("A new hold must be at least this far from the moving hand (so it actually moves).")]
        [SerializeField] private float minStepDistance = 0.15f;
        [Tooltip("A new hold can be at most this far from the moving hand (reach limit).")]
        [SerializeField] private float maxStepReach = 0.8f;
        [Tooltip("Keep the moving hand at least this far from the other hand.")]
        [SerializeField] private float handClearance = 0.18f;
        [Tooltip("A new hold must face within this dot of the climber's outward normal — keeps you on the same face of a curved surface. 1 = identical, 0 = perpendicular.")]
        [SerializeField] private float facingCoherence = 0.35f;
        [Tooltip("A new hold must lie at least this much along the input direction from the moving hand — prevents back-and-forth. 1 = exactly forward.")]
        [SerializeField] private float progressDot = 0.25f;
        [Tooltip("How far a hand may cross past the other hand before a hold is rejected (anti-cross slack). 0 = strict no-cross.")]
        [SerializeField] private float crossMargin = 0.12f;
        [Tooltip("Max distance between the two hands. When exceeded, the trailing hand closes the gap toward the other hand instead of reaching further; holds beyond this from the other hand are also rejected.")]
        [SerializeField] private float maxHandSeparation = 0.7f;

        [Header("Detection")]
        [Tooltip("Layers searched for climbable colliders.")]
        [SerializeField] private LayerMask climbableLayers = ~0;
        [Tooltip("Radius around the chest to search for grabbable holds.")]
        [SerializeField] private float detectRadius = 2.5f;
        [Tooltip("A hold farther than this from the chest can't be grabbed.")]
        [SerializeField] private float maxReach = 2.0f;
        [Tooltip("Height above the player transform to search from (≈ chest).")]
        [SerializeField] private float detectHeightOffset = 1.0f;
        [Tooltip("Max angle between the character's forward and the into-wall direction to allow a grab.")]
        [SerializeField] private float maxGrabAngle = 80f;
        [Tooltip("Min angle of the grab normal from world up — keeps floors/ceilings ungrabbable. 90 = vertical wall.")]
        [SerializeField] private float minWallAngle = 45f;

        [Header("Climb Pose (animator ClimbingLayer + ClimbLegsLayer)")]
        [Tooltip("How fast braced↔free-hang blends the leg-layer weight, foot-IK weight and standoff toward 0 in free hang. Roughly match your animator transition-clip length.")]
        [SerializeField] private float freeHangBlendSpeed = 4f;
        [Tooltip("Enter FREE HANG when Dot(outwardNormal, up) drops BELOW this (surface above you / overhang). Negative.")]
        [SerializeField] private float freeHangEnterDot = -0.5f;
        [Tooltip("Return to BRACED when Dot(outwardNormal, up) rises ABOVE this. Kept above the enter value for hysteresis (no flicker). Negative.")]
        [SerializeField] private float freeHangExitDot = -0.4f;

        [Header("Free Hang (brachiation — turn to face, then traverse)")]
        [Tooltip("How far the body hangs straight DOWN below the hand-midpoint in free hang. Independent of rootForwardOffset/rootDownOffset — the body sits directly under the middle of the two hand holds.")]
        [SerializeField] private float freeHangDrop = 1.4f;
        [Tooltip("Degrees the body's facing rotates per hand-step while re-facing the input direction in free hang. A full ~180° turn takes ~(180/this) hand holds. Lower = the turn spreads over more holds.")]
        [SerializeField] private float freeHangTurnPerStep = 36f;
        [Tooltip("The body must face within this angle (deg) of the input direction before free-hang traversal advances; beyond it, the hands only turn the body (no travel).")]
        [SerializeField] private float freeHangMoveAngle = 25f;
        [Tooltip("How fast the body yaw eases toward the per-step facing between hand-steps (visual smoothing of the turn). Higher = snappier.")]
        [SerializeField] private float freeHangTurnSmooth = 8f;

        [Header("Feet / Bracing (own tuning — legs reach further than arms)")]
        [Tooltip("Hip drop below the hand-average (the foot anchor reference). Pendulum replaces this later.")]
        [SerializeField] private float hipDropFromHands = 0.9f;
        [Tooltip("How far the hip sits OUT from the surface along the outward normal — keeps the hip and foot anchors OUTSIDE curved/irregular geometry instead of clipping into it.")]
        [SerializeField] private float hipForwardOffset = 0.35f;
        [Tooltip("How far below the hip each foot reaches for its ideal plant point.")]
        [SerializeField] private float footDrop = 0.55f;
        [Tooltip("Sideways offset of each foot from the hip (left foot left, right foot right).")]
        [SerializeField] private float footSide = 0.16f;
        [Tooltip("Max distance from the hip to a foot-hold. Beyond this the leg would overstretch → that foot free-hangs instead.")]
        [SerializeField] private float legReach = 1.0f;
        [Tooltip("A foot-hold must be at least this far below the hand-average (keeps feet off hand-level holds).")]
        [SerializeField] private float footBelowHands = 0.3f;
        [Tooltip("A foot-hold must clear each hand and the other foot by at least this distance.")]
        [SerializeField] private float footHoldClearance = 0.2f;
        [Tooltip("How far a foot-hold may sit past the body centerline toward the other side before it's rejected (anti-cross).")]
        [SerializeField] private float footCrossMargin = 0.1f;
        [Tooltip("Seconds a foot takes to step to a new plant point.")]
        [SerializeField] private float footMoveDuration = 0.18f;
        [Tooltip("Minimum gap (seconds) between foot steps.")]
        [SerializeField] private float footStepInterval = 0.08f;
        [Tooltip("A planted foot KEEPS its hold until the ideal plant point drifts this far from it (stickiness — prevents flip-flopping between two near-equal holds). Larger = stickier.")]
        [SerializeField] private float footStickRadius = 0.4f;
        [Tooltip("How fast each foot's IK weight fades in (plant) / out (dangle), per second.")]
        [SerializeField] private float footWeightFadeSpeed = 6f;
        [Tooltip("Euler grip offset for the LEFT foot effector (sole against the surface). The right foot mirrors it per footGripMirror. Tune to the rig.")]
        [SerializeField] private Vector3 footGripRotation = Vector3.zero;
        [Tooltip("Per-axis sign multiplier applied to footGripRotation for the RIGHT foot, so the grip is chiral: keep an axis (1) or mirror it (-1). Default mirrors lateral (X), keeps vertical (Y) and roll (Z).")]
        [SerializeField] private Vector3 footGripMirror = new Vector3(-1f, 1f, 1f);
        [Tooltip("Forces each knee toward an explicit away-from-wall / out bend via FBBIK leg bend constraints (the mirror-image fix, like the elbows). 1 = full control.")]
        [Range(0f, 1f)]
        [SerializeField] private float kneeBendWeight = 1f;
        [Tooltip("How far the knees point outward vs straight away from the wall (0 = straight out from wall).")]
        [SerializeField] private float kneeOutward = 0.3f;

        [Header("Torso Standoff (keep the body off the surface)")]
        [Tooltip("Forward-probe the surface and push the body out so the torso never clips into a bulging/irregular trunk (hands & feet stay pinned to their holds).")]
        [SerializeField] private bool enableStandoff = true;
        [Tooltip("Height above the player transform of the CHEST probe (≈ between the shoulders).")]
        [SerializeField] private float chestProbeHeight = 1.4f;
        [Tooltip("Gap to keep between the CHEST and the surface.")]
        [SerializeField] private float chestStandoff = 0.3f;
        [Tooltip("Height above the player transform of the HIP probe.")]
        [SerializeField] private float hipProbeHeight = 1.0f;
        [Tooltip("Gap to keep between the HIPS and the surface.")]
        [SerializeField] private float hipStandoff = 0.3f;
        [Tooltip("Radius of each torso probe (≈ body half-thickness).")]
        [SerializeField] private float standoffRadius = 0.2f;
        [Tooltip("How far the probe origin is backed OUT along the normal before casting in — must exceed the worst torso penetration so the cast starts outside the geometry.")]
        [SerializeField] private float standoffBackup = 0.8f;
        [Tooltip("Maximum outward push the standoff may apply (clamp against bad readings / arm overstretch).")]
        [SerializeField] private float maxStandoffPush = 0.6f;
        [Tooltip("How fast the standoff push eases in/out (higher = snappier).")]
        [SerializeField] private float standoffSpeed = 10f;

        [Header("Animated Legs (masked climb clip + foot-smear IK) — EXPERIMENTAL")]
        [Tooltip("Drive the legs from a masked lower-body climb blend and only IK-correct the feet to the surface, instead of the procedural foot stepping. Needs the ClimbLegsLayer + 2D blend tree set up. Off = procedural feet.")]
        [SerializeField] private bool useAnimatedLegs = false;
        [Tooltip("How fast the leg-blend params (ClimbMoveX/Y) ease toward the movement direction.")]
        [SerializeField] private float climbMoveSmooth = 6f;
        [Tooltip("Legs keep animating for this long after the last hand step, so they don't stutter between steps; once it elapses with no stepping (stuck / no holds ahead) the legs return to the idle braced pose. 0 = freeze the instant stepping stops.")]
        [SerializeField] private float climbLegStopDelay = 0.2f;
        [Tooltip("Radius of the foot-smear probe.")]
        [SerializeField] private float footSmearRadius = 0.08f;
        [Tooltip("How far the foot-smear probe origin is backed OUT along the normal before casting in.")]
        [SerializeField] private float footSmearBackup = 0.4f;
        [Tooltip("Max distance the foot-smear probe casts into the surface from the backed-out origin.")]
        [SerializeField] private float footSmearMaxDist = 0.6f;
        [Tooltip("Lift the IK'd foot this far off the surface along its normal.")]
        [SerializeField] private float footSmearSurfaceOffset = 0.02f;
        [Tooltip("Contact distance (animated foot → surface) at/under which the foot is fully PLANTED (pinned).")]
        [SerializeField] private float footContactNear = 0.04f;
        [Tooltip("Contact distance at/over which the foot is fully SWING (follows the clip, IK off).")]
        [SerializeField] private float footContactFar = 0.22f;
        [Tooltip("Max foot POSITION IK weight when planted.")]
        [Range(0f, 1f)]
        [SerializeField] private float footIKWeight = 1f;
        [Tooltip("Max foot ROTATION IK weight when planted (lower = keep more of the clip's foot rotation).")]
        [Range(0f, 1f)]
        [SerializeField] private float footSmearRotWeight = 0.5f;
        [Tooltip("How much the toes angle to the foot's OWN SIDE in character space (left foot left, right foot right). 0 = straight up the character, higher = more sideways.")]
        [SerializeField] private float footToeSide = 0.5f;
        [Tooltip("Euler offset on the LEFT planted foot rotation — corrects the rig's foot-bone axis convention / fine-tunes the plant angle.")]
        [SerializeField] private Vector3 footPlantRotation = Vector3.zero;
        [Tooltip("Per-axis sign multiplier applied to footPlantRotation for the RIGHT foot (mirror-image foot bones, like footGripMirror).")]
        [SerializeField] private Vector3 footPlantMirror = new Vector3(-1f, 1f, 1f);
        [Tooltip("Estimated trunk radius — the foot cast aims at a point this far behind the hand surface (≈ the trunk axis), so feet splayed around the curve still cast INTO the trunk instead of past it.")]
        [SerializeField] private float trunkAxisDepth = 0.5f;
        [Tooltip("Lock a planted foot in world space through its stance — no skating, no re-twist. Off = re-derive every frame (previous behaviour).")]
        [SerializeField] private bool enableFootLock = true;
        [Tooltip("Stance above which a planted foot LOCKS in world space.")]
        [Range(0f, 1f)]
        [SerializeField] private float footLockEnter = 0.6f;
        [Tooltip("Stance below which a locked foot UNLOCKS into swing. Keep below footLockEnter for hysteresis.")]
        [Range(0f, 1f)]
        [SerializeField] private float footLockExit = 0.35f;
        [Tooltip("A locked foot also UNLOCKS once the animated (clip) foot has moved this far from the lock — so feet re-plant as the body climbs past instead of pinning for a long time. LOWER = feet step more often.")]
        [SerializeField] private float footLockBreak = 0.35f;
        [Tooltip("How fast each foot eases toward its IK target — LOWER = smoother (less snappy plant/lift/re-plant), HIGHER = snappier/more exact. The main dial for snappy feet.")]
        [SerializeField] private float footSmoothSpeed = 15f;

        [Header("Two-Mass Pendulum (EXPERIMENT — body hang/swing)")]
        [Tooltip("Replace the rigid body drop with a two-mass pendulum hanging from the hands, so the body hangs and swings as you move. OFF = rigid placement (restore-point behaviour).")]
        [SerializeField] private bool usePendulum = true;
        [Tooltip("How much the pendulum replaces the rigid body position (0 = rigid, 1 = full pendulum swing).")]
        [Range(0f, 1f)]
        [SerializeField] private float pendulumWeight = 1f;
        [Tooltip("Pendulum spring stiffness (higher = stiffer, snaps back faster).")]
        [SerializeField] private float pendulumStiffness = 160f;
        [Tooltip("Pendulum damping (higher = settles faster, less swing). Raise if it jitters or blows up.")]
        [SerializeField] private float pendulumDamping = 6f;
        [Tooltip("Fixed sub-step rate (Hz) for the pendulum sim — makes the swing frame-rate independent and stable. Higher = finer.")]
        [SerializeField] private float pendulumStepHz = 120f;
        [Tooltip("Chest/upper-spine bone the pendulum's upper-mass swing leans. Leave empty to auto-use the humanoid Chest (then Spine).")]
        [SerializeField] private Transform spineBone;
        [Tooltip("How much the chest/spine leans with the pendulum's upper-mass swing (article uses ~0.5). 0 = no spine twist.")]
        [Range(0f, 1f)]
        [SerializeField] private float spineSwingWeight = 0.5f;
        [Tooltip("Optional second (lower) spine bone to spread the lean across for a smoother bend. Leave empty to auto-use the humanoid Spine; none = single-bone twist.")]
        [SerializeField] private Transform spineBoneLower;
        [Tooltip("Share of the lean applied at the LOWER spine bone vs the chest (0.5 = even). Only used when a lower spine bone is set.")]
        [Range(0f, 1f)]
        [SerializeField] private float spineLowerShare = 0.5f;

        /// <summary>How the head picks its look point.</summary>
        public enum HeadLookTarget { MidpointToLead, MovingHand }

        [Header("Head Look")]
        [Tooltip("How the head picks its look target. MidpointToLead = between the hand-midpoint and the reaching hand (idle eases toward neutral via Ahead Bias). MovingHand = look straight at the currently moving hand (idle returns to the animation default).")]
        [SerializeField] private HeadLookTarget headLookTarget = HeadLookTarget.MidpointToLead;
        [Tooltip("How strongly the head turns toward the look target (0 = keep the pure animation pose).")]
        [Range(0f, 1f)]
        [SerializeField] private float headLookWeight = 0.6f;
        [Tooltip("Head/face bone. Auto-resolves to the humanoid Head if left empty.")]
        [SerializeField] private Transform headBone;
        [Tooltip("The head bone's local FORWARD (face) axis — tune to the rig (often +Z, sometimes +Y).")]
        [SerializeField] private Vector3 headForwardAxis = Vector3.forward;
        [Tooltip("While a hand is reaching: look this far from the hand-midpoint toward the LEAD (moving) hand (0 = the midpoint, 1 = the hand).")]
        [Range(0f, 1f)]
        [SerializeField] private float headLookLeadBias = 0.5f;
        [Tooltip("When idle: look this far from the hand-midpoint toward the head's default (animation) forward (0 = the midpoint, 1 = keep the animation pose).")]
        [Range(0f, 1f)]
        [SerializeField] private float headLookAheadBias = 0.5f;
        [Tooltip("How fast the head eases toward its look target (higher = snappier, lower = smoother). Smooths the jump when the lead hand switches or the head returns to neutral.")]
        [SerializeField] private float headLookSmooth = 8f;

        [Header("Debug (skeleton test)")]
        [Tooltip("Allow toggling a climb takeover with the debug key — for verifying enter/exit only.")]
        [SerializeField] private bool enableDebugToggle = true;
        [SerializeField] private Key debugToggleKey = Key.C;

        // -- Resolved player interfaces (runtime, never inspector-linked) --
        private IControlLock _controlLock;
        private IPlayerMotor _motor;
        private PlayerStamina _stamina;
        private PlayerCameraRig _cameraRig;
        private InputHandler _input;
        private Transform _cam;   // gameplay camera, for camera-relative traversal input

        // -- Animator (climb pose layer) --
        private Animator _animator;
        private int _climbLayerIndex = -1;
        private bool _freeHang;   // current pose state (hysteresis-driven)
        private static readonly int _hIsClimbing = Animator.StringToHash("isClimbing");
        private static readonly int _hClimbHang = Animator.StringToHash("ClimbHang");
        private static readonly int _hFreeHang = Animator.StringToHash("FreeHang");
        private static readonly int _hIsFreeHang = Animator.StringToHash("IsFreeHang");
        private float _bracedWeight = 1f;   // 1 = braced (legs/foot-IK/standoff active), 0 = free hang
        private Vector3 _freeHangFacing;            // horizontal direction the body faces in free hang (turn target)
        private Quaternion _freeHangBodyRot = Quaternion.identity;   // eased free-hang body yaw (between hand-steps)
        private Quaternion _bracedBodyRot = Quaternion.identity;     // eased braced facing (smooths the rotation snap around curved surfaces)
        private int _climbLegsLayerIndex = -1;
        private Vector2 _legBlend;
        private float _legMoveTimer;   // legs animate while > 0 (topped up while stepping; decays when stuck)
        private Vector3 _headLookPoint;   // eased head look target (smooths lead-hand switch / return to neutral)
        private bool _headLookInit;
        private bool _smearHooked;
        private bool _lFootLocked, _rFootLocked;
        private Vector3 _lFootLockPos, _rFootLockPos;
        private Quaternion _lFootLockRot = Quaternion.identity, _rFootLockRot = Quaternion.identity;
        private readonly Vector3[] _footSmoothPos = new Vector3[2];
        private readonly Quaternion[] _footSmoothRot = { Quaternion.identity, Quaternion.identity };
        private readonly bool[] _footSmoothInit = new bool[2];
        private static readonly int _hClimbMoveX = Animator.StringToHash("ClimbMoveX");
        private static readonly int _hClimbMoveY = Animator.StringToHash("ClimbMoveY");
        private static readonly int _hClimbLegsSpeed = Animator.StringToHash("ClimbLegsSpeed");

        // -- Owned subsystems --
        private EffectorRig _rig;
        private OscillatorBank _oscillators;
        private TwoMassPendulum _pendulum;

        private bool _isClimbing;
        public bool IsClimbing => _isClimbing;
        /// <summary>The surface currently being climbed (null when not climbing). Read <c>.Source</c> to tell a Flora trunk (Procedural) from a parsed cliff (Authored).</summary>
        public ClimbableSurface CurrentSurface => _isClimbing ? _currentSurface : null;

        // -- Current grab candidate (while not climbing) --
        private bool _hasCandidate;
        private Vector3 _candidatePos;
        private Quaternion _candidateRot;
        private ClimbableSurface _candidateSurface;

        // -- Climb lifecycle --
        private bool _releasing;
        private float _masterWeightTarget;
        private Vector3 _rhOutward = Vector3.forward;  // outward normal of the right-hand hold
        private Vector3 _lhOutward = Vector3.forward;  // outward normal of the left-hand hold
        private ClimbableSurface _currentSurface;     // surface being climbed
        private float _moveCooldown;

        // -- Feet (per-foot IK weight, faded plant↔dangle; one-at-a-time step gate; current hold index) --
        private float _lFootWeight, _rFootWeight;
        private float _footCooldown;
        private int _lFootHoldIdx = -1, _rFootHoldIdx = -1;

        // -- Torso standoff (smoothed outward push to keep the body off the surface) --
        private float _standoffPush;
        private float _pendulumAccumulator;   // fixed-step accumulator for the body pendulum sim

        // -- Braced rotation tween (per hand step; duration tied to traverse speed) --
        private Quaternion _rotTweenFrom = Quaternion.identity;
        private Quaternion _rotTweenTo = Quaternion.identity;
        private float _rotTweenT = 1f;         // normalized progress; 1 = settled
        private float _rotTweenDur = 0.0001f;

        private void Awake()
        {
            _controlLock = GetComponentInParent<IControlLock>();
            _motor = GetComponentInParent<IPlayerMotor>();
            _stamina = GetComponentInParent<PlayerStamina>();
            _cameraRig = GetComponentInParent<PlayerCameraRig>();
            _input = GetComponentInParent<InputHandler>();
            if (Camera.main != null) _cam = Camera.main.transform;

            _animator = GetComponentInParent<Animator>();
            if (_animator != null)
            {
                _climbLayerIndex = _animator.GetLayerIndex("ClimbingLayer");
                if (_climbLayerIndex < 0)
                    Debug.LogError("[ClimbController] Animator has no 'ClimbingLayer' — climb pose won't show.");
                _climbLegsLayerIndex = _animator.GetLayerIndex("ClimbLegsLayer");   // optional (animated legs)
                if (spineBone == null && _animator.isHuman)
                    spineBone = _animator.GetBoneTransform(HumanBodyBones.Chest)
                                ?? _animator.GetBoneTransform(HumanBodyBones.Spine);
                if (spineBoneLower == null && _animator.isHuman)
                    spineBoneLower = _animator.GetBoneTransform(HumanBodyBones.Spine);
                if (spineBoneLower == spineBone) spineBoneLower = null;   // need two distinct bones to spread
                if (headBone == null && _animator.isHuman)
                    headBone = _animator.GetBoneTransform(HumanBodyBones.Head);
            }
            else
            {
                Debug.LogError("[ClimbController] No Animator found on the player hierarchy.");
            }

            if (ik == null) ik = GetComponentInChildren<FullBodyBipedIK>(true);

            if (_controlLock == null)
                Debug.LogError("[ClimbController] No IControlLock found on the player hierarchy.");
            if (_motor == null)
                Debug.LogError("[ClimbController] No IPlayerMotor found on the player hierarchy.");
            if (ik == null)
                Debug.LogError("[ClimbController] No FullBodyBipedIK found on the player — assign it or add one.");

            _oscillators = new OscillatorBank();
            _pendulum = new TwoMassPendulum();
            if (ik != null) _rig = new EffectorRig(ik, effectorEase);
        }

        // Smear the feet onto the surface right BEFORE FinalIK solves, so we read the animator's posed
        // leg/foot positions (the animator runs before LateUpdate, the solver in LateUpdate).
        private void OnEnable()
        {
            if (!_smearHooked && ik != null && ik.solver != null)
            {
                ik.solver.OnPreUpdate += SmearFeet;     // read animated feet → cast → set effectors
                ik.solver.OnPostUpdate += TwistSpine;   // lean the chest after the solve (final override)
                ik.solver.OnPostUpdate += HeadLook;     // turn the head after the spine twist (final override)
                _smearHooked = true;
            }
        }

        private void OnDisable()
        {
            if (_smearHooked && ik != null && ik.solver != null)
            {
                ik.solver.OnPreUpdate -= SmearFeet;
                ik.solver.OnPostUpdate -= TwistSpine;
                ik.solver.OnPostUpdate -= HeadLook;
                _smearHooked = false;
            }
        }

        private void Update()
        {
            // Look for a grab candidate while free (detection-only for now — no grab yet).
            if (!_isClimbing)
                _hasCandidate = TryFindCandidate(out _candidatePos, out _candidateRot, out _candidateSurface);
            else
                _hasCandidate = false;

            if (enableDebugToggle && Keyboard.current != null &&
                Keyboard.current[debugToggleKey].wasPressedThisFrame)
            {
                if (_isClimbing) { if (!_releasing) BeginRelease(); }
                else if (_hasCandidate) Grab();
            }

            if (_isClimbing) TickClimb(Time.deltaTime);
        }

        /// <summary>
        /// Finds the best grab hold near the chest: nearest hold within reach, on a wall-like surface
        /// (normal not too vertical) that the character roughly faces. Placeholder spatial query —
        /// iterates each nearby surface's holds directly; the HoldStreamer will cull these later.
        /// </summary>
        private bool TryFindCandidate(out Vector3 pos, out Quaternion rot, out ClimbableSurface surface)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            surface = null;

            Vector3 origin = transform.position + Vector3.up * detectHeightOffset;
            Collider[] hits = Physics.OverlapSphere(origin, detectRadius, climbableLayers, QueryTriggerInteraction.Ignore);

            float best = float.MaxValue;
            for (int h = 0; h < hits.Length; h++)
            {
                ClimbableSurface s = hits[h].GetComponentInParent<ClimbableSurface>();
                if (s == null || !s.HoldsReady) continue;

                Transform st = s.transform;
                var holds = s.Holds;
                for (int i = 0; i < holds.Count; i++)
                {
                    Vector3 wp = st.TransformPoint(holds[i].LocalPosition);
                    float d = Vector3.Distance(origin, wp);
                    if (d > maxReach || d >= best) continue;

                    Quaternion wr = st.rotation * holds[i].LocalRotation;
                    Vector3 outward = wr * Vector3.forward;   // hold forward = outward grab normal

                    // Reject floors/ceilings: the grab normal must be roughly horizontal.
                    if (Vector3.Angle(outward, Vector3.up) < minWallAngle) continue;
                    // Must be roughly facing into the wall.
                    if (Vector3.Angle(transform.forward, -outward) > maxGrabAngle) continue;

                    best = d;
                    pos = wp;
                    rot = wr;
                    surface = s;
                }
            }

            return surface != null;
        }

        private void OnDrawGizmos()
        {
            if (!_hasCandidate) return;
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(_candidatePos, 0.08f);
            Gizmos.color = Color.cyan;
            Gizmos.DrawRay(_candidatePos, (_candidateRot * Vector3.forward) * 0.4f);  // grab normal
        }

        /// <summary>
        /// Snap-grab onto the current candidate: take the body over, face the wall, snap both hands to
        /// holds (legs/body stay IK-free for a free-hang), position the body below the hands, and fade
        /// FBBIK weight in. Traversal, feet/braced, slide and dynamics come in later increments.
        /// </summary>
        private void Grab()
        {
            if (_isClimbing || _controlLock == null || _rig == null || _candidateSurface == null) return;

            Vector3 rightPos = _candidatePos;
            Quaternion rightRot = _candidateRot;
            Vector3 rightOut = rightRot * Vector3.forward;        // hold forward = outward grab normal

            // Into-surface direction (flattened) for the shoulder-width offset of the second hand.
            Vector3 intoFlat = Vector3.ProjectOnPlane(-rightOut, Vector3.up);
            intoFlat = intoFlat.sqrMagnitude > 1e-4f ? intoFlat.normalized : -rightOut;
            Vector3 right = Vector3.Cross(Vector3.up, intoFlat).normalized;

            Vector3 leftTarget = rightPos - right * shoulderWidth;
            if (!FindHoldNear(_candidateSurface, leftTarget, rightPos, out Vector3 leftPos, out Quaternion leftRot))
            {
                leftPos = leftTarget;        // no distinct hold nearby — place beside the right hand
                leftRot = rightRot;
            }
            _rhOutward = rightOut;
            _lhOutward = leftRot * Vector3.forward;

            // Take over.
            _controlLock.RequestExternalControl();
            _isClimbing = true;
            _releasing = false;
            _currentSurface = _candidateSurface;
            _moveCooldown = 0f;
            _oscillators?.ResetAll();
            _stamina?.SetClimbState(true, false);

            // Snap hands (pure hold rotation; grip offset applied live at write time). Feet + body
            // stay IK-free (free-hang).
            _rig.SnapToPose(ClimbEffector.RightHand, rightPos, rightRot);
            _rig.SnapToPose(ClimbEffector.LeftHand, leftPos, leftRot);
            _rig.SetEffectorWeight(ClimbEffector.RightHand, 1f);
            _rig.SetEffectorWeight(ClimbEffector.LeftHand, 1f);
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RootBody, 0f);
            _lFootWeight = _rFootWeight = 0f;   // feet start dangling; UpdateFeet plants them
            _footCooldown = 0f;
            _lFootHoldIdx = _rFootHoldIdx = -1;
            _standoffPush = 0f;
            _headLookInit = false;
            _legBlend = Vector2.zero;
            _lFootLocked = _rFootLocked = false;
            _footSmoothInit[0] = _footSmoothInit[1] = false;
            _pendulumAccumulator = 0f;
            ApplyGripOffset();

            _freeHangFacing = intoFlat.sqrMagnitude > 1e-4f ? intoFlat.normalized : transform.forward;
            _freeHangBodyRot = Quaternion.LookRotation(_freeHangFacing, Vector3.up);

            ConfigurePendulum();
            _pendulum?.Reset(_rig.HandAverage);
            UpdateBodyPose(instant: true);

            // Seed the feet under the hips (dangling, weight 0) so the first plant steps in from a sane
            // spot instead of swooping from the world origin.
            Vector3 hipG = HipPosition;
            Vector3 brG = transform.right;
            _rig.SnapToPose(ClimbEffector.LeftFoot, hipG - Vector3.up * footDrop - brG * footSide, transform.rotation);
            _rig.SnapToPose(ClimbEffector.RightFoot, hipG - Vector3.up * footDrop + brG * footSide, transform.rotation);

            // Animator: enter the climb pose layer and pick the initial braced/free pose by orientation.
            if (_animator != null) _animator.SetBool(_hIsClimbing, true);
            _freeHang = Vector3.Dot(AvgOutward(), Vector3.up) < freeHangEnterDot;
            PlayPose(_freeHang, instant: true);

            _rig.SetMasterWeight(0f);
            _masterWeightTarget = 1f;
            Debug.Log($"[ClimbController] Grab on {_currentSurface.Source} surface ({_currentSurface.name}).");
        }

        private void BeginRelease()
        {
            _releasing = true;
            _masterWeightTarget = 0f;
            Debug.Log("[ClimbController] Release (fading out).");
        }

        private void FinishRelease()
        {
            _isClimbing = false;
            _releasing = false;
            _rig.SetMasterWeight(0f);
            SetArmBendDirections(0f);            // hand the arm bend back to FBBIK's default
            SetLegBendDirections(0f);            // hand the knee bend back too
            _lFootWeight = _rFootWeight = 0f;
            if (_animator != null) _animator.SetBool(_hIsClimbing, false);
            if (_animator != null && _climbLayerIndex >= 0) _animator.SetLayerWeight(_climbLayerIndex, 0f);
            if (_animator != null && _climbLegsLayerIndex >= 0) _animator.SetLayerWeight(_climbLegsLayerIndex, 0f);
            _stamina?.SetClimbState(false, false);
            _controlLock?.ReleaseExternalControl();   // FSM hands control back to Jump/Move/Idle
            Debug.Log("[ClimbController] Released — control returned.");
        }

        /// <summary>Per-frame climb update: free-look, IK weight fade, effector tick, body follow.</summary>
        private void TickClimb(float dt)
        {
            // Keep free-look on (ExternalControlState froze it on entry this frame).
            _cameraRig?.SetFrozen(false);

            // Fade FBBIK weight toward target (in on grab, out on release).
            float dur = _masterWeightTarget > _rig.MasterWeight ? ikFadeInDuration : ikFadeOutDuration;
            float step = dur > 0f ? dt / dur : 1f;
            _rig.SetMasterWeight(Mathf.MoveTowards(_rig.MasterWeight, _masterWeightTarget, step));

            // Drive the climb pose layer in lockstep with the FinalIK weight so the pose appears/clears
            // as the IK fades (the layer WEIGHT reveals climbing — the isClimbing bool alone won't).
            if (_animator != null && _climbLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLayerIndex, _rig.MasterWeight);

            // Braced↔free-hang blend: fades the leg layer, foot IK and standoff toward 0 in free hang.
            _bracedWeight = Mathf.MoveTowards(_bracedWeight, _freeHang ? 0f : 1f, freeHangBlendSpeed * dt);

            ApplyGripOffset();
            SetArmBendDirections(elbowBendWeight);
            if (!_releasing)
            {
                HandleTraversal(dt);
                UpdateFeet(dt);          // plant/dangle each foot for this frame's Tick
                UpdatePoseSwitch();
            }
            SetLegBendDirections(Mathf.Max(_lFootWeight, _rFootWeight) * kneeBendWeight);

            _rig.Tick(dt);
            StepPendulum(dt);
            UpdateBodyPose();

            _stamina?.SetClimbState(true, _rig.AnyMoving);

            if (_releasing && _rig.MasterWeight <= 0.001f)
                FinishRelease();
        }

        /// <summary>Average outward normal of the two hand holds (away from the surface, climber's side).</summary>
        private Vector3 AvgOutward()
        {
            Vector3 o = _rhOutward + _lhOutward;
            return o.sqrMagnitude > 1e-4f ? o.normalized : _rhOutward;
        }

        /// <summary>
        /// Positions and orients the body each frame. BRACED: face the surface (yaw only) via the per-step
        /// rotation tween, and sit a standoff out from the surface below the hands. FREE HANG: yaw to the
        /// turn-driven facing and hang straight down from the hand-midpoint. The two are blended by
        /// <see cref="_bracedWeight"/>; rotation is snapped on grab (<paramref name="instant"/>).
        /// </summary>
        private void UpdateBodyPose(bool instant = false)
        {
            Vector3 avgOut = AvgOutward();

            // ---- Rotation ----
            // BRACED facing turns via a per-step TWEEN (set in StartBracedTurn on each hand step) from the
            // current rotation to the new target over a traverse-speed-tied duration — so the body rotates
            // smoothly ACROSS the hand move instead of snapping when avgOut jumps a ring-angle.
            if (instant)
            {
                _bracedBodyRot = ComputeBracedTarget();   // snap on grab
                _rotTweenT = 1f;
            }
            else if (_rotTweenT < 1f)
            {
                _rotTweenT = Mathf.Min(1f, _rotTweenT + Time.deltaTime / _rotTweenDur);
                _bracedBodyRot = Quaternion.Slerp(_rotTweenFrom, _rotTweenTo, Mathf.SmoothStep(0f, 1f, _rotTweenT));
            }
            // else: tween settled — hold _bracedBodyRot until the next hand step.

            // FREE-HANG target: yaw to the turn-driven facing, upright — the torso never pitches toward
            // the hands. Eased between the discrete per-hand-step facing changes so the turn reads smooth.
            Quaternion freeRot = _freeHangFacing.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(_freeHangFacing, Vector3.up)
                : _bracedBodyRot;
            _freeHangBodyRot = instant
                ? freeRot
                : Quaternion.Slerp(_freeHangBodyRot, freeRot, 1f - Mathf.Exp(-freeHangTurnSmooth * Time.deltaTime));

            transform.rotation = Quaternion.Slerp(_freeHangBodyRot, _bracedBodyRot, _bracedWeight);

            // ---- Position ----
            // BRACED: rigid drop (restore-point) vs pendulum hang (lower mass swings below the moving hands).
            // The standoff offset uses the SMOOTHED facing's outward (from the rotation tween), NOT the raw
            // avgOut — avgOut jumps instantly when a hand grabs, which would pop the body ~rootForwardOffset·
            // sin(step angle) sideways around the trunk each step (a snap the camera follows, independent of
            // rotation/pendulum). Tying it to _bracedBodyRot makes the standoff follow the same smooth turn.
            Vector3 facingOut = -(_bracedBodyRot * Vector3.forward);
            Vector3 rigidPos = _rig.HandAverage + facingOut * rootForwardOffset - Vector3.up * rootDownOffset;
            Vector3 bracedPos = usePendulum && _pendulum != null
                ? Vector3.Lerp(rigidPos, _pendulum.LowerPos + facingOut * rootForwardOffset, pendulumWeight)
                : rigidPos;

            // FREE-HANG: hang straight DOWN from the hand-midpoint — independent of rootForwardOffset /
            // rootDownOffset (body sits directly below the middle of the two hand holds). Sideways
            // liveliness comes from the spine lean (TwistSpine), not a positional sway.
            Vector3 freePos = _rig.HandAverage - Vector3.up * freeHangDrop;

            transform.position = Vector3.Lerp(freePos, bracedPos, _bracedWeight);

            // Torso standoff push — gated by `enableStandoff` and scaled by _bracedWeight (no push in free hang).
            ApplyStandoff(avgOut, instant);
        }

        /// <summary>Sets the pendulum's tuning + segment lengths so its rest matches the rigid body drop.</summary>
        private void ConfigurePendulum()
        {
            if (_pendulum == null) return;
            _pendulum.Stiffness = pendulumStiffness;
            _pendulum.Damping = pendulumDamping;
            _pendulum.AnchorToUpper = _pendulum.UpperToLower = rootDownOffset * 0.5f;  // rest = rigid drop
        }

        /// <summary>Advances the body pendulum one step, anchored to the live hand-average.</summary>
        private void StepPendulum(float dt)
        {
            if (!usePendulum || _pendulum == null) return;
            ConfigurePendulum();                 // live-tunable
            _pendulum.SetAnchor(_rig.HandAverage);

            // Fixed-timestep sub-stepping so the swing is frame-rate independent + stable (better than a
            // literal FixedUpdate, which would quantize the visual). Safety cap avoids a spiral of death.
            float fixedStep = 1f / Mathf.Max(1f, pendulumStepHz);
            _pendulumAccumulator += dt;
            int safety = 0;
            while (_pendulumAccumulator >= fixedStep && safety++ < 8)
            {
                _pendulum.Step(fixedStep);
                _pendulumAccumulator -= fixedStep;
            }
        }

        /// <summary>
        /// Leans the chest/spine bone with the pendulum's upper-mass swing (article's mass2 → spine).
        /// Runs as FinalIK's POST-solve callback so it overrides the final pose. The upper segment's
        /// deviation from straight-down is applied to the spine as a world-space lean, scaled by weight
        /// and the climb fade. At rest the swing is identity (no change).
        /// </summary>
        private void TwistSpine()
        {
            if (!_isClimbing || !usePendulum || _pendulum == null || spineBone == null || _rig == null) return;
            float w = spineSwingWeight * _rig.MasterWeight;
            if (w <= 0.001f) return;

            // In FREE HANG the torso must not pitch toward the arms — keep only the LATERAL (sideways)
            // part of the swing and drop the fore/aft component along the body's forward axis. Braced
            // keeps the full swing. (_bracedWeight: 1 = braced/full, 0 = free/lateral-only.)
            Vector3 dir = _pendulum.UpperDir;
            Vector3 lateral = Vector3.ProjectOnPlane(dir, transform.forward);
            dir = lateral.sqrMagnitude > 1e-5f
                ? Vector3.Slerp(lateral.normalized, dir, _bracedWeight)
                : Vector3.Slerp(Vector3.down, dir, _bracedWeight);   // pure fore/aft swing → straight down in free hang

            Quaternion swing = Quaternion.FromToRotation(Vector3.down, dir);

            if (spineBoneLower != null)
            {
                // Spread the lean across two joints for a smoother bend. Apply the lower bone FIRST — the
                // chest inherits it, so the two partial leans compose to ~the full swing, distributed.
                spineBoneLower.rotation = Quaternion.Slerp(Quaternion.identity, swing, w * spineLowerShare) * spineBoneLower.rotation;
                spineBone.rotation = Quaternion.Slerp(Quaternion.identity, swing, w * (1f - spineLowerShare)) * spineBone.rotation;
            }
            else
            {
                spineBone.rotation = Quaternion.Slerp(Quaternion.identity, swing, w) * spineBone.rotation;
            }
        }

        /// <summary>
        /// Turns the head toward a look point, applied AFTER the solve + spine twist (final override). While
        /// a hand is reaching, the point sits between the hand-midpoint and the LEAD (moving) hand, so the
        /// character glances toward the hold it's going for; when idle it sits between the hand-midpoint and
        /// the head's own default (animation) forward, so the head eases back to its neutral pose. Uses the
        /// head's CURRENT forward (no rig-axis guess beyond <see cref="headForwardAxis"/>) so the turn is a
        /// minimal rotation, scaled by weight × climb fade.
        /// </summary>
        private void HeadLook()
        {
            if (!_isClimbing || headBone == null || _rig == null) return;
            float w = headLookWeight * _rig.MasterWeight;
            if (w <= 0.001f) return;

            Vector3 headPos = headBone.position;
            Vector3 forward = headBone.rotation * (headForwardAxis.sqrMagnitude > 1e-6f ? headForwardAxis.normalized : Vector3.forward);
            Vector3 handMid = _rig.HandAverage;

            bool rMoving = _rig.IsMoving(ClimbEffector.RightHand);
            bool lMoving = _rig.IsMoving(ClimbEffector.LeftHand);
            Vector3 desiredPoint;
            if (rMoving || lMoving)
            {
                Vector3 lead = _rig.GetCurrentPosition(rMoving ? ClimbEffector.RightHand : ClimbEffector.LeftHand);
                desiredPoint = headLookTarget == HeadLookTarget.MovingHand
                    ? lead                                                  // look straight at the moving hand
                    : Vector3.Lerp(handMid, lead, headLookLeadBias);        // between mid and the reaching hand
            }
            else
            {
                desiredPoint = headLookTarget == HeadLookTarget.MovingHand
                    ? headPos + forward                                     // animation default (neutral)
                    : Vector3.Lerp(handMid, headPos + forward, headLookAheadBias);   // between mid and the default forward
            }

            // Ease the look POINT toward its target — smooths the jump when the lead hand switches or the
            // head returns to neutral, instead of snapping the gaze.
            if (!_headLookInit) { _headLookPoint = desiredPoint; _headLookInit = true; }
            else _headLookPoint = Vector3.Lerp(_headLookPoint, desiredPoint, 1f - Mathf.Exp(-headLookSmooth * Time.deltaTime));

            Vector3 desired = _headLookPoint - headPos;
            if (desired.sqrMagnitude < 1e-6f) return;
            Quaternion delta = Quaternion.FromToRotation(forward, desired.normalized);
            headBone.rotation = Quaternion.Slerp(Quaternion.identity, delta, w) * headBone.rotation;
        }

        /// <summary>
        /// Forward-probes the surface from the torso and pushes the whole body OUT along the normal when
        /// the trunk is closer than <see cref="desiredStandoff"/> (or the torso is already inside it) —
        /// so the body never clips a bulging/irregular surface. Hands and feet are world-pinned IK
        /// effectors, so they stay on their holds while the torso clears; the push is clamped and eased.
        /// The cast origin is backed out along the normal so it starts OUTSIDE the geometry even when the
        /// torso is penetrating.
        /// </summary>
        private void ApplyStandoff(Vector3 bodyNormal, bool instant)
        {
            float push = 0f;
            if (enableStandoff)
            {
                float chestPush = ProbePush(bodyNormal, chestProbeHeight, chestStandoff);
                float hipPush = ProbePush(bodyNormal, hipProbeHeight, hipStandoff);
                // Pure translation can only satisfy one distance — honour whichever needs the most
                // clearance so neither the chest nor the hips clip. (Holding DIFFERENT chest/hip gaps at
                // once needs the lean back on; the two probes can later drive that tilt from their delta.)
                push = Mathf.Max(chestPush, hipPush);
            }
            push *= _bracedWeight;   // no torso standoff in free hang

            float t = instant ? 1f : 1f - Mathf.Exp(-standoffSpeed * Time.deltaTime);
            _standoffPush = Mathf.Lerp(_standoffPush, push, t);
            transform.position += bodyNormal * _standoffPush;
        }

        /// <summary>
        /// Forward SphereCast at one torso height; returns the outward push needed to hold its standoff
        /// (0 if already clear). Origin is backed out along the normal so it starts outside the geometry
        /// even when that point is penetrating.
        /// </summary>
        private float ProbePush(Vector3 bodyNormal, float height, float standoff)
        {
            Vector3 p = transform.position + Vector3.up * height;
            Vector3 origin = p + bodyNormal * standoffBackup;
            if (Physics.SphereCast(origin, standoffRadius, -bodyNormal, out RaycastHit hit,
                                   standoffBackup + maxStandoffPush, climbableLayers, QueryTriggerInteraction.Ignore))
            {
                float surfaceDist = hit.distance - standoffBackup;   // point → surface (negative = penetrating)
                return Mathf.Clamp(standoff - surfaceDist, 0f, maxStandoffPush);
            }
            return 0f;
        }

        /// <summary>
        /// The braced facing rotation (upright, yaw only) from the hold-normal average — flatten the
        /// into-surface direction to horizontal. Sampled once per hand step (the tween target).
        /// </summary>
        private Quaternion ComputeBracedTarget()
        {
            Vector3 intoFlat = Vector3.ProjectOnPlane(-AvgOutward(), Vector3.up);
            return intoFlat.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(intoFlat.normalized, Vector3.up)
                : _bracedBodyRot;
        }

        /// <summary>
        /// Starts a braced body-rotation tween from the current facing to the new target, over a duration
        /// tied to the hand-move time (traverseMoveDuration × bodyTurnDurationScale). Called on each
        /// successful braced hand step, so the body turns smoothly across the move and slower traversal
        /// produces gentler turns. Chains naturally if a step starts before the previous tween finishes.
        /// </summary>
        private void StartBracedTurn()
        {
            _rotTweenFrom = _bracedBodyRot;
            _rotTweenTo = ComputeBracedTarget();
            _rotTweenDur = Mathf.Max(0.0001f, traverseMoveDuration * bodyTurnDurationScale);
            _rotTweenT = 0f;
        }

        /// <summary>
        /// Picks the braced vs free-hang pose from surface orientation. A single scalar —
        /// Dot(outwardNormal, up) — captures it: ≈0 = vertical wall (braced), strongly negative =
        /// overhang above you / chest faces up (free hang). Hysteresis (enter vs exit thresholds)
        /// stops braced↔free flicker at the boundary; the cross-fade smooths the switch so the body
        /// doesn't pop. (Strongly POSITIVE = lying on a near-flat top = the future mantle zone — left
        /// braced for now until mantle exists.)
        /// </summary>
        private void UpdatePoseSwitch()
        {
            float d = Vector3.Dot(AvgOutward(), Vector3.up);
            if (!_freeHang && d < freeHangEnterDot) PlayPose(true, instant: false);
            else if (_freeHang && d > freeHangExitDot) PlayPose(false, instant: false);
        }

        /// <summary>Switches the ClimbingLayer to the braced or free-hang state (snap on entry, cross-fade otherwise).</summary>
        private void PlayPose(bool free, bool instant)
        {
            _freeHang = free;
            if (free)
            {
                _lFootLocked = _rFootLocked = false;   // drop foot locks when entering free hang
                // Seed the free-hang facing from the current body yaw so the brace→free switch doesn't snap.
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                if (fwd.sqrMagnitude > 1e-4f) _freeHangFacing = fwd.normalized;
            }
            if (_animator == null) return;

            // The animator plays the braced↔hang TRANSITION clips off this bool. Code only toggles intent.
            _animator.SetBool(_hIsFreeHang, free);
            if (instant)
            {
                _bracedWeight = free ? 0f : 1f;
                if (_climbLayerIndex >= 0)
                    _animator.Play(free ? _hFreeHang : _hClimbHang, _climbLayerIndex, 0f);
            }
        }

        /// <summary>
        /// Reference hip point the feet anchor from: a fixed drop below the hand-average for now.
        /// The two-mass pendulum will repoint this at its lower mass later (single seam, one line).
        /// </summary>
        private Vector3 HipPosition =>
            _rig.HandAverage + AvgOutward() * hipForwardOffset - Vector3.up * hipDropFromHands;

        /// <summary>
        /// Plants or dangles each foot. In free-hang orientation both feet dangle (IK off, pose
        /// shows the dangle). Otherwise each foot probes its OWN anchor (down + to its side of the
        /// hip), SphereCasts into the surface, and — if it hits within leg reach — plants there;
        /// a miss or an over-reach leaves that foot free. One foot steps at a time (the other foot
        /// and the same-side hand must be settled), so there are always 3+ contact points.
        /// </summary>
        private void UpdateFeet(float dt)
        {
            if (_footCooldown > 0f) _footCooldown -= dt;

            if (useAnimatedLegs)
            {
                // Legs come from the masked climb clip; feet are corrected to the surface in SmearFeet
                // (FinalIK pre-solve). Zero the procedural foot effectors so EffectorRig doesn't fight it.
                _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
                _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
                _lFootWeight = _rFootWeight = 0f;
                UpdateLegBlend(dt);
                return;
            }

            if (_freeHang)
            {
                FadeFootWeight(ClimbEffector.LeftFoot, 0f, dt);
                FadeFootWeight(ClimbEffector.RightFoot, 0f, dt);
                return;
            }

            UpdateFoot(ClimbEffector.LeftFoot, ClimbEffector.LeftHand, -1f, dt);
            UpdateFoot(ClimbEffector.RightFoot, ClimbEffector.RightHand, +1f, dt);
        }

        /// <summary>
        /// Drives the lower-body climb blend: ClimbMoveX/Y follow the movement direction (eased), and the
        /// ClimbLegsLayer weight tracks the climb fade. The 2D blend's centre (0,0) should be the idle
        /// braced-legs pose; +Y up, −Y down, ±X traverse.
        /// </summary>
        private void UpdateLegBlend(float dt)
        {
            if (_animator == null) return;

            Vector2 mv = _input != null ? _input.MoveInput : Vector2.zero;
            if (mv.sqrMagnitude < minMoveInput * minMoveInput) mv = Vector2.zero;

            // Legs animate only while the character is actually advancing between holds — not when input is
            // held but traversal is stuck (no reachable hold). A hand step tops up a short hold timer so the
            // legs keep moving through the gaps between steps; when stepping stops the legs FREEZE where they
            // were (the blend param holds + ClimbLegsSpeed → 0), instead of returning to the idle pose.
            if (_rig.AnyMoving) _legMoveTimer = climbLegStopDelay;
            else _legMoveTimer = Mathf.Max(0f, _legMoveTimer - dt);
            bool legsMoving = _legMoveTimer > 0f;

            if (legsMoving)
                _legBlend = Vector2.MoveTowards(_legBlend, mv, climbMoveSmooth * dt);
            // else: hold _legBlend at its last value — legs stay in their stride pose.

            _animator.SetFloat(_hClimbMoveX, _legBlend.x);
            _animator.SetFloat(_hClimbMoveY, _legBlend.y);
            // Pause the leg clip when frozen so looping stride clips don't keep cycling in place. Hook this
            // float to the leg blend-tree state's Speed Multiplier in the animator (no-op if not wired).
            _animator.SetFloat(_hClimbLegsSpeed, legsMoving ? 1f : 0f);
            if (_climbLegsLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLegsLayerIndex, _rig.MasterWeight * _bracedWeight);
        }

        /// <summary>
        /// Foot-smear IK (runs as the FinalIK pre-solve callback). For each foot it reads the animator's
        /// posed foot position, casts to the surface, and pins the effector there — with a weight derived
        /// from how close the animated foot is to the surface, so a clip-lifted foot (swing) follows the
        /// animation while a clip-planted foot snaps to the real geometry. No-op unless climbing with
        /// animated legs.
        /// </summary>
        private void SmearFeet()
        {
            if (!_isClimbing || !useAnimatedLegs || _rig == null || ik == null || ik.solver == null) return;
            SmearFoot(ik.solver.leftFootEffector, -1f, ref _lFootLocked, ref _lFootLockPos, ref _lFootLockRot);
            SmearFoot(ik.solver.rightFootEffector, +1f, ref _rFootLocked, ref _rFootLockPos, ref _rFootLockRot);
        }

        private void SmearFoot(IKEffector eff, float sideSign, ref bool locked, ref Vector3 lockPos, ref Quaternion lockRot)
        {
            if (eff == null || eff.bone == null) return;
            int idx = sideSign < 0f ? 0 : 1;

            Vector3 outward = AvgOutward();
            Vector3 footPos = eff.bone.position;                 // animator-posed foot (pre-solve)
            float w = _rig.MasterWeight * footIKWeight * _bracedWeight;   // foot IK fades out in free hang

            // Curvature-aware inward direction: aim the cast at the trunk-axis estimate (a point
            // trunkAxisDepth behind the hand surface), so a foot splayed around the curve still casts
            // INTO the trunk instead of shooting past it along the body's fixed radial.
            Vector3 axis = _rig.HandAverage - outward * trunkAxisDepth;
            axis.y = footPos.y;
            Vector3 toAxis = axis - footPos;
            Vector3 inward = toAxis.sqrMagnitude > 1e-4f ? toAxis.normalized : -outward;
            Vector3 origin = footPos - inward * footSmearBackup;

            bool hasHit = Physics.SphereCast(origin, footSmearRadius, inward, out RaycastHit hit,
                                             footSmearBackup + footSmearMaxDist, climbableLayers, QueryTriggerInteraction.Ignore);
            float contactDist = hasHit ? hit.distance - footSmearBackup : float.MaxValue;
            float stance = 1f - Mathf.Clamp01(Mathf.InverseLerp(footContactNear, footContactFar, contactDist));

            // Pick this frame's target pose + weights: hold the lock, take a fresh plant, or follow the clip.
            Vector3 targetPos;
            Quaternion targetRot;
            float posW, rotW;

            bool holdingLock = enableFootLock && locked && stance >= footLockExit &&
                               (eff.bone.position - lockPos).sqrMagnitude < footLockBreak * footLockBreak;
            if (holdingLock)
            {
                targetPos = lockPos;
                targetRot = lockRot;
                posW = w;
                rotW = w * footSmearRotWeight;
            }
            else
            {
                locked = false;   // foot lifted (clip) or body climbed past → re-plant
                if (hasHit)
                {
                    targetPos = hit.point + hit.normal * footSmearSurfaceOffset;
                    targetRot = PlantRotation(hit.normal, sideSign, eff.bone.rotation);
                    posW = w * stance;
                    rotW = w * stance * footSmearRotWeight;
                    if (enableFootLock && stance > footLockEnter) { locked = true; lockPos = targetPos; lockRot = targetRot; }
                }
                else
                {
                    targetPos = _footSmoothPos[idx];   // hold last (weight 0 → no visible effect / no swoop)
                    targetRot = _footSmoothRot[idx];
                    posW = 0f;
                    rotW = 0f;
                }
            }

            // Ease the IK target so plant / lift / lock-break / re-plant blend instead of snapping.
            if (!_footSmoothInit[idx]) { _footSmoothPos[idx] = targetPos; _footSmoothRot[idx] = targetRot; _footSmoothInit[idx] = true; }
            float s = footSmoothSpeed > 0f ? 1f - Mathf.Exp(-footSmoothSpeed * Time.deltaTime) : 1f;
            _footSmoothPos[idx] = Vector3.Lerp(_footSmoothPos[idx], targetPos, s);
            _footSmoothRot[idx] = Quaternion.Slerp(_footSmoothRot[idx], targetRot, s);

            eff.position = _footSmoothPos[idx];
            eff.rotation = _footSmoothRot[idx];
            eff.positionWeight = posW;
            eff.rotationWeight = rotW;
        }

        /// <summary>
        /// Character-relative plant rotation: sole on the surface (up = normal), toes up + out to the
        /// foot's own side in character space, + the rig-convention euler offset (mirrored for the right foot).
        /// </summary>
        private Quaternion PlantRotation(Vector3 normal, float sideSign, Quaternion fallback)
        {
            Vector3 toe = transform.up + transform.right * (sideSign * footToeSide);
            toe = Vector3.ProjectOnPlane(toe, normal);
            Quaternion rot = toe.sqrMagnitude > 1e-5f ? Quaternion.LookRotation(toe.normalized, normal) : fallback;
            Vector3 off = sideSign < 0f ? footPlantRotation : Vector3.Scale(footPlantRotation, footPlantMirror);
            return rot * Quaternion.Euler(off);
        }

        private void UpdateFoot(ClimbEffector foot, ClimbEffector sameSideHand, float sideSign, float dt)
        {
            Vector3 hip = HipPosition;
            Vector3 avgOut = AvgOutward();
            Vector3 handAvg = _rig.HandAverage;
            Vector3 bodyRight = transform.right;
            Vector3 desired = hip - Vector3.up * footDrop + bodyRight * (sideSign * footSide);
            ClimbEffector other = foot == ClimbEffector.LeftFoot ? ClimbEffector.RightFoot : ClimbEffector.LeftFoot;

            // STICKINESS: keep the current foot-hold unless the body has drifted far from it. Re-picking
            // nearest-to-desired every frame made feet flip-flop between two near-equal holds (worsened by
            // the body-rotation feedback loop); staying put unless there's a real reason removes the jitter.
            int curIdx = FootHoldIndex(foot);
            bool curValid = curIdx >= 0 && FootHoldValid(curIdx, hip, handAvg, avgOut);
            if (curValid && (HoldWorldPos(curIdx) - desired).sqrMagnitude <= footStickRadius * footStickRadius)
            {
                FadeFootWeight(foot, 1f, dt);   // happy where it is — no search, no step
                return;
            }

            // Want to move (dangling, drifted, or current hold invalid). Find the best hold for `desired`.
            bool found = FindFootHold(desired, hip, bodyRight, sideSign, handAvg, avgOut, other,
                                      out int idx, out Vector3 hp, out Quaternion hr);
            if (found)
            {
                // One foot at a time: step only when settled and the same-side hand / other foot aren't moving.
                bool canStep = _footCooldown <= 0f && !_rig.IsMoving(foot)
                               && !_rig.IsMoving(other) && !_rig.IsMoving(sameSideHand);
                if (canStep)
                {
                    _rig.SetPoseTarget(foot, hp, hr, footMoveDuration);   // interpolate — no harsh snap
                    SetFootHoldIndex(foot, idx);
                    _footCooldown = footStepInterval;
                    FadeFootWeight(foot, 1f, dt);
                }
                else
                {
                    // Gate closed (another limb mid-move): stay weighted if we have a usable hold, else wait.
                    FadeFootWeight(foot, (curValid || _rig.IsMoving(foot)) ? 1f : 0f, dt);
                }
            }
            else if (curValid)
            {
                FadeFootWeight(foot, 1f, dt);   // no better hold found — keep the (drifted) current one
            }
            else
            {
                // Truly nothing reachable (gap / overhang) → dangle under the hip, IK off.
                SetFootHoldIndex(foot, -1);
                if (!_rig.IsMoving(foot)) _rig.SnapToPose(foot, desired, transform.rotation);
                FadeFootWeight(foot, 0f, dt);
            }
        }

        private int FootHoldIndex(ClimbEffector foot) =>
            foot == ClimbEffector.LeftFoot ? _lFootHoldIdx : _rFootHoldIdx;

        private void SetFootHoldIndex(ClimbEffector foot, int idx)
        {
            if (foot == ClimbEffector.LeftFoot) _lFootHoldIdx = idx; else _rFootHoldIdx = idx;
        }

        private Vector3 HoldWorldPos(int idx) =>
            _currentSurface.transform.TransformPoint(_currentSurface.Holds[idx].LocalPosition);

        private Quaternion HoldWorldRot(int idx) =>
            _currentSurface.transform.rotation * _currentSurface.Holds[idx].LocalRotation;

        /// <summary>A foot's current hold is still usable: in leg reach, below the hands, and on the same face.</summary>
        private bool FootHoldValid(int idx, Vector3 hip, Vector3 handAvg, Vector3 avgOut)
        {
            if (_currentSurface == null || !_currentSurface.HoldsReady || idx >= _currentSurface.Holds.Count)
                return false;
            Vector3 wp = HoldWorldPos(idx);
            if ((wp - hip).sqrMagnitude > legReach * legReach) return false;
            if (Vector3.Dot(wp - handAvg, Vector3.up) > -footBelowHands) return false;
            if (Vector3.Dot(HoldWorldRot(idx) * Vector3.forward, avgOut) < facingCoherence) return false;
            return true;
        }

        /// <summary>
        /// Best foot-hold (by index) nearest the desired plant point: within leg reach of the hip, below
        /// the hands, on the foot's own side (anti-cross), clear of both hands and the other foot, same face.
        /// </summary>
        private bool FindFootHold(Vector3 desired, Vector3 hip, Vector3 bodyRight, float sideSign,
                                  Vector3 handAvg, Vector3 avgOut, ClimbEffector other,
                                  out int index, out Vector3 pos, out Quaternion rot)
        {
            index = -1;
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var s = _currentSurface;
            if (s == null || !s.HoldsReady) return false;

            Vector3 lh = _rig.GetCurrentPosition(ClimbEffector.LeftHand);
            Vector3 rh = _rig.GetCurrentPosition(ClimbEffector.RightHand);
            Vector3 of = _rig.GetCurrentPosition(other);

            Transform st = s.transform;
            var holds = s.Holds;
            float legSqr = legReach * legReach;
            float clearSqr = footHoldClearance * footHoldClearance;
            float best = float.MaxValue;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);

                if ((wp - hip).sqrMagnitude > legSqr) continue;                               // within leg reach
                if (Vector3.Dot(wp - handAvg, Vector3.up) > -footBelowHands) continue;         // below the hands
                if (Vector3.Dot(wp - hip, bodyRight) * sideSign < -footCrossMargin) continue;  // own side (anti-cross)
                if ((wp - lh).sqrMagnitude < clearSqr) continue;                              // clear of hands + other foot
                if ((wp - rh).sqrMagnitude < clearSqr) continue;
                if ((wp - of).sqrMagnitude < clearSqr) continue;

                Quaternion wr = st.rotation * holds[i].LocalRotation;
                if (Vector3.Dot(wr * Vector3.forward, avgOut) < facingCoherence) continue;     // same face

                float d = (wp - desired).sqrMagnitude;
                if (d < best) { best = d; index = i; pos = wp; rot = wr; }
            }
            return index >= 0;
        }

        private float FootWeight(ClimbEffector foot) =>
            foot == ClimbEffector.LeftFoot ? _lFootWeight : _rFootWeight;

        private void FadeFootWeight(ClimbEffector foot, float target, float dt)
        {
            float w = Mathf.MoveTowards(FootWeight(foot), target, footWeightFadeSpeed * dt);
            if (foot == ClimbEffector.LeftFoot) _lFootWeight = w; else _rFootWeight = w;
            _rig.SetEffectorWeight(foot, w);
        }

        /// <summary>
        /// Forces each knee toward an explicit away-from-wall / out bend via FBBIK leg bend
        /// constraints — the same mirror-image fix the elbows need (the legs are reflections, so a
        /// shared foot rotation would flip one knee). Weight scales with how planted the feet are.
        /// </summary>
        private void SetLegBendDirections(float weight)
        {
            if (ik == null || ik.solver == null || !ik.solver.initiated) return;
            var solver = ik.solver;

            Vector3 bodyRight = transform.right;
            Vector3 awayFromWall = -transform.forward;                                  // body faces into the wall
            Vector3 leftDir = (awayFromWall - bodyRight * kneeOutward).normalized;      // left knee: out-from-wall + left
            Vector3 rightDir = (awayFromWall + bodyRight * kneeOutward).normalized;     // right knee: out-from-wall + right

            var lc = solver.leftLegChain.bendConstraint;
            lc.bendGoal = null; lc.direction = leftDir; lc.weight = weight;
            var rc = solver.rightLegChain.bendConstraint;
            rc.bendGoal = null; rc.direction = rightDir; rc.weight = weight;
        }

        /// <summary>Pushes the live-tunable per-hand grip offsets onto the hand effectors (applied at write time).</summary>
        private void ApplyGripOffset()
        {
            _rig.SetRotationOffset(ClimbEffector.LeftHand, Quaternion.Euler(leftHandGripRotation));
            _rig.SetRotationOffset(ClimbEffector.RightHand, Quaternion.Euler(rightHandGripRotation));
            _rig.SetRotationOffset(ClimbEffector.LeftFoot, Quaternion.Euler(footGripRotation));
            _rig.SetRotationOffset(ClimbEffector.RightFoot, Quaternion.Euler(Vector3.Scale(footGripRotation, footGripMirror)));
        }

        /// <summary>
        /// Forces each arm's elbow to bend toward an explicit down/out direction via FBBIK bend
        /// constraints, INDEPENDENT of hand rotation. Without this FinalIK derives the elbow bend
        /// from the hand effector rotation (IKConstraintBend.GetDir), so the 180° grip that faces the
        /// palm flips the elbow — and since the arms are mirror images, it flips the right but not the
        /// left. Weight 1 overrides that, so both palms AND both elbows come out correct.
        /// </summary>
        private void SetArmBendDirections(float weight)
        {
            if (ik == null || ik.solver == null || !ik.solver.initiated) return;
            var solver = ik.solver;

            Vector3 bodyRight = transform.right;
            Vector3 leftDir = (Vector3.down - bodyRight * elbowOutward).normalized;   // left elbow: down + left
            Vector3 rightDir = (Vector3.down + bodyRight * elbowOutward).normalized;  // right elbow: down + right

            var lc = solver.leftArmChain.bendConstraint;
            lc.bendGoal = null; lc.direction = leftDir; lc.weight = weight;
            var rc = solver.rightArmChain.bendConstraint;
            rc.bendGoal = null; rc.direction = rightDir; rc.weight = weight;
        }

        /// <summary>
        /// Move-input traversal: when no hand is mid-move, step the trailing hand toward a hold in the
        /// input direction (in the LOCAL surface plane); the body follows via UpdateBodyPose. Surface-
        /// aware (follows curvature); no feet / no sway yet.
        /// </summary>
        private void HandleTraversal(float dt)
        {
            if (_moveCooldown > 0f) _moveCooldown -= dt;
            if (_input == null || _rig.AnyMoving || _moveCooldown > 0f) return;

            Vector2 mv = _input.MoveInput;
            if (mv.sqrMagnitude < minMoveInput * minMoveInput) return;

            // Free hang = brachiation: turn to face the input first, then traverse (separate model).
            if (_freeHang) { HandleFreeHangTraversal(mv); return; }

            // Camera-relative input mapped onto the surface tangent plane: x = screen-right projected
            // onto the surface, y = up the surface. Falls back gracefully on near-horizontal surfaces.
            Vector3 avgOut = AvgOutward();
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;

            Vector3 camRight = _cam != null ? _cam.right : transform.right;
            Vector3 xDir = Vector3.ProjectOnPlane(camRight, avgOut);
            Vector3 yDir = Vector3.ProjectOnPlane(Vector3.up, avgOut);
            if (xDir.sqrMagnitude < 1e-4f) xDir = transform.right;
            if (yDir.sqrMagnitude < 1e-4f) yDir = Vector3.Cross(avgOut, xDir);
            Vector3 traverseDir = xDir.normalized * mv.x + yDir.normalized * mv.y;
            if (traverseDir.sqrMagnitude < 1e-4f) return;
            traverseDir.Normalize();

            Vector3 rhPos = _rig.GetCurrentPosition(ClimbEffector.RightHand);
            Vector3 lhPos = _rig.GetCurrentPosition(ClimbEffector.LeftHand);

            // Try the TRAILING hand first (less advanced along traverseDir, measured RELATIVE to the
            // other hand). If it's blocked — anti-cross, caught up, or no reachable hold — try the
            // OTHER hand instead. That turns the deadlock (trailing hand caught up to the lead but
            // can't cross, so the lead never gets its turn) into a natural shuffle gait.
            bool primaryRight = Vector3.Dot(rhPos - lhPos, traverseDir) <= 0f;
            if (TryStepHand(primaryRight, rhPos, lhPos, traverseDir, avgOut) ||
                TryStepHand(!primaryRight, rhPos, lhPos, traverseDir, avgOut))
                StartBracedTurn();   // begin the per-step rotation tween toward the new facing
        }

        /// <summary>
        /// Attempts to step one hand to a new hold for the current input direction. Handles the
        /// close-the-gap (over-extended) case. Returns true if a hold was found and the move started.
        /// </summary>
        private bool TryStepHand(bool moveRight, Vector3 rhPos, Vector3 lhPos, Vector3 traverseDir, Vector3 avgOut)
        {
            Vector3 fromPos = moveRight ? rhPos : lhPos;
            Vector3 otherPos = moveRight ? lhPos : rhPos;
            Vector3 bodyRight = transform.right;
            float sideSign = moveRight ? 1f : -1f;

            // Over-extended → close the gap toward the other hand (drop the forward-progress
            // requirement); otherwise leapfrog forward by traverseStep.
            float separation = Vector3.Distance(rhPos, lhPos);
            bool closeGap = separation > maxHandSeparation;
            Vector3 ideal = closeGap ? otherPos : fromPos + traverseDir * traverseStep;
            float minProgress = closeGap ? -1f : progressDot;

            if (!FindReachableHold(_currentSurface, ideal, fromPos, otherPos, traverseDir, avgOut,
                                   bodyRight, sideSign, minProgress, out Vector3 tp, out Quaternion tr))
                return false;

            ClimbEffector hand = moveRight ? ClimbEffector.RightHand : ClimbEffector.LeftHand;
            _rig.SetPoseTarget(hand, tp, tr, traverseMoveDuration);
            if (moveRight) _rhOutward = tr * Vector3.forward;
            else _lhOutward = tr * Vector3.forward;
            _moveCooldown = moveInterval;
            return true;
        }

        /// <summary>
        /// Free-hang traversal = brachiation. The body first TURNS to face the (camera-relative) input
        /// direction, then travels. The turn is realised through hand-steps — one hand pivots on the
        /// other and swings around an arc, rotating the hand pair (and the body facing) a little each
        /// step — so a full re-face takes several holds. No forward progress until the body faces within
        /// freeHangMoveAngle of the input; then the hands leapfrog forward in the facing direction.
        /// </summary>
        private void HandleFreeHangTraversal(Vector2 mv)
        {
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            Vector3 camF = Vector3.ProjectOnPlane(_cam != null ? _cam.forward : transform.forward, Vector3.up);
            Vector3 camR = Vector3.ProjectOnPlane(_cam != null ? _cam.right : transform.right, Vector3.up);
            if (camF.sqrMagnitude < 1e-4f) camF = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (camR.sqrMagnitude < 1e-4f) camR = Vector3.ProjectOnPlane(transform.right, Vector3.up);
            if (camF.sqrMagnitude < 1e-4f || camR.sqrMagnitude < 1e-4f) return;

            Vector3 wish = camR.normalized * mv.x + camF.normalized * mv.y;
            if (wish.sqrMagnitude < 1e-4f) return;
            wish.Normalize();

            if (_freeHangFacing.sqrMagnitude < 1e-4f) _freeHangFacing = wish;

            float angleErr = Vector3.SignedAngle(_freeHangFacing, wish, Vector3.up);

            if (Mathf.Abs(angleErr) > freeHangMoveAngle)
            {
                // TURN: rotate the hand pair toward the input — one hand-step at a time, no net travel.
                // Commit the facing rotation only when a hand actually steps, so the turn is paced by holds.
                float stepDeg = Mathf.Clamp(angleErr, -freeHangTurnPerStep, freeHangTurnPerStep);
                bool stepRight = stepDeg < 0f;   // turning clockwise (right) → reach the right hand around first
                if (TryStepHandArc(stepRight, stepDeg) || TryStepHandArc(!stepRight, stepDeg))
                    _freeHangFacing = (Quaternion.AngleAxis(stepDeg, Vector3.up) * _freeHangFacing).normalized;
            }
            else
            {
                // Facing the input (within tolerance) → finish the last few degrees and leapfrog forward.
                _freeHangFacing = Vector3.RotateTowards(_freeHangFacing, wish, Mathf.Deg2Rad * freeHangTurnPerStep, 0f);
                Vector3 rhPos = _rig.GetCurrentPosition(ClimbEffector.RightHand);
                Vector3 lhPos = _rig.GetCurrentPosition(ClimbEffector.LeftHand);
                bool primaryRight = Vector3.Dot(rhPos - lhPos, wish) <= 0f;
                if (!TryStepHand(primaryRight, rhPos, lhPos, wish, AvgOutward()))
                    TryStepHand(!primaryRight, rhPos, lhPos, wish, AvgOutward());
            }
        }

        /// <summary>
        /// Steps one hand along an arc around the OTHER (pivot) hand by <paramref name="stepDeg"/> about
        /// world up, walking the hand pair around to rotate the body's facing. Returns true if a hold was found.
        /// </summary>
        private bool TryStepHandArc(bool moveRight, float stepDeg)
        {
            ClimbEffector hand = moveRight ? ClimbEffector.RightHand : ClimbEffector.LeftHand;
            ClimbEffector pivotE = moveRight ? ClimbEffector.LeftHand : ClimbEffector.RightHand;
            Vector3 fromPos = _rig.GetCurrentPosition(hand);
            Vector3 pivot = _rig.GetCurrentPosition(pivotE);
            Vector3 ideal = pivot + Quaternion.AngleAxis(stepDeg, Vector3.up) * (fromPos - pivot);

            if (!FindFreeHangHold(ideal, fromPos, pivot, out Vector3 tp, out Quaternion tr))
                return false;

            _rig.SetPoseTarget(hand, tp, tr, traverseMoveDuration);
            if (moveRight) _rhOutward = tr * Vector3.forward; else _lhOutward = tr * Vector3.forward;
            _moveCooldown = moveInterval;
            return true;
        }

        /// <summary>
        /// Nearest hold to <paramref name="ideal"/> for a free-hang hand step: within the reach band of
        /// the moving hand, clear of and within max separation of the pivot hand, on the same face. No
        /// anti-cross / progress filters (the body is turning, not leapfrogging a fixed direction).
        /// </summary>
        private bool FindFreeHangHold(Vector3 ideal, Vector3 fromPos, Vector3 pivotPos, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var s = _currentSurface;
            if (s == null || !s.HoldsReady) return false;

            Transform st = s.transform;
            var holds = s.Holds;
            float minSqr = minStepDistance * minStepDistance;
            float maxSqr = maxStepReach * maxStepReach;
            float clearSqr = handClearance * handClearance;
            float maxSepSqr = maxHandSeparation * maxHandSeparation;
            Vector3 climberOut = AvgOutward();
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);

                float fromSqr = (wp - fromPos).sqrMagnitude;
                if (fromSqr < minSqr || fromSqr > maxSqr) continue;            // reach band of the moving hand

                float pivSqr = (wp - pivotPos).sqrMagnitude;
                if (pivSqr < clearSqr || pivSqr > maxSepSqr) continue;         // clear of + within reach of the pivot hand

                Quaternion wr = st.rotation * holds[i].LocalRotation;
                if (Vector3.Dot(wr * Vector3.forward, climberOut) < facingCoherence) continue;   // same face

                float d = (wp - ideal).sqrMagnitude;
                if (d < best) { best = d; pos = wp; rot = wr; found = true; }
            }
            return found;
        }

        /// <summary>
        /// Best next hold for a traversing hand: within the reach band of the moving hand, clear of the
        /// other hand, lying along the input direction (progress, no back-and-forth), and on the same
        /// face as the climber (outward normal coherent — stops it grabbing the far side of a trunk).
        /// Among those, nearest to the ideal step point.
        /// </summary>
        private bool FindReachableHold(ClimbableSurface s, Vector3 ideal, Vector3 fromPos, Vector3 otherPos,
                                       Vector3 traverseDir, Vector3 climberOut, Vector3 bodyRight, float sideSign,
                                       float minProgress, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (s == null || !s.HoldsReady) return false;

            Transform st = s.transform;
            var holds = s.Holds;
            float minSqr = minStepDistance * minStepDistance;
            float maxSqr = maxStepReach * maxStepReach;
            float clearSqr = handClearance * handClearance;
            float maxSepSqr = maxHandSeparation * maxHandSeparation;
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);

                Vector3 fromDelta = wp - fromPos;
                float fromSqr = fromDelta.sqrMagnitude;
                if (fromSqr < minSqr || fromSqr > maxSqr) continue;                    // reach band

                Vector3 toOther = wp - otherPos;
                float otherSqr = toOther.sqrMagnitude;
                if (otherSqr < clearSqr) continue;                                     // clear of other hand
                if (otherSqr > maxSepSqr) continue;                                    // cap hand separation
                if (Vector3.Dot(toOther, bodyRight) * sideSign < -crossMargin) continue; // keep hands ~uncrossed (small slack)
                if (Vector3.Dot(fromDelta.normalized, traverseDir) < minProgress) continue; // progress in input dir

                Quaternion wr = st.rotation * holds[i].LocalRotation;
                Vector3 outward = wr * Vector3.forward;
                if (Vector3.Dot(outward, climberOut) < facingCoherence) continue;      // stay on the same face

                float d = (wp - ideal).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pos = wp;
                    rot = wr;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>Nearest hold on a surface to <paramref name="target"/>, excluding one near <paramref name="exclude"/>.</summary>
        private bool FindHoldNear(ClimbableSurface s, Vector3 target, Vector3 exclude, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (s == null || !s.HoldsReady) return false;

            Transform st = s.transform;
            var holds = s.Holds;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);
                if ((wp - exclude).sqrMagnitude < 0.04f) continue;   // skip the same hold (~0.2 m)
                float d = (wp - target).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pos = wp;
                    rot = st.rotation * holds[i].LocalRotation;
                    found = true;
                }
            }
            return found;
        }
    }
}
