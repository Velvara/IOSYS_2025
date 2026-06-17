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

        // -- Owned subsystems --
        private EffectorRig _rig;
        private OscillatorBank _oscillators;
        private TwoMassPendulum _pendulum;

        private bool _isClimbing;
        public bool IsClimbing => _isClimbing;

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

        private void Awake()
        {
            _controlLock = GetComponentInParent<IControlLock>();
            _motor = GetComponentInParent<IPlayerMotor>();
            _stamina = GetComponentInParent<PlayerStamina>();
            _cameraRig = GetComponentInParent<PlayerCameraRig>();
            _input = GetComponentInParent<InputHandler>();
            if (Camera.main != null) _cam = Camera.main.transform;

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
            ApplyGripOffset();

            UpdateBodyPose();
            _pendulum?.Reset(_rig.HandAverage);

            _rig.SetMasterWeight(0f);
            _masterWeightTarget = 1f;
            Debug.Log("[ClimbController] Grab.");
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

            ApplyGripOffset();
            SetArmBendDirections(elbowBendWeight);
            if (!_releasing) HandleTraversal(dt);

            _rig.Tick(dt);
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
        /// Positions and faces the body from the CURRENT hand normals: outside the surface along the
        /// average outward normal, below the hands, facing into the surface. Surface-aware, so it
        /// follows curvature (e.g. wrapping around a trunk) instead of a fixed grab facing.
        /// </summary>
        private void UpdateBodyPose()
        {
            Vector3 avgOut = AvgOutward();

            Vector3 intoFlat = Vector3.ProjectOnPlane(-avgOut, Vector3.up);
            if (intoFlat.sqrMagnitude > 1e-4f)
                transform.rotation = Quaternion.LookRotation(intoFlat.normalized, Vector3.up);

            transform.position = _rig.HandAverage + avgOut * rootForwardOffset - Vector3.up * rootDownOffset;
        }

        /// <summary>Pushes the live-tunable per-hand grip offsets onto the hand effectors (applied at write time).</summary>
        private void ApplyGripOffset()
        {
            _rig.SetRotationOffset(ClimbEffector.LeftHand, Quaternion.Euler(leftHandGripRotation));
            _rig.SetRotationOffset(ClimbEffector.RightHand, Quaternion.Euler(rightHandGripRotation));
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
            if (TryStepHand(primaryRight, rhPos, lhPos, traverseDir, avgOut)) return;
            TryStepHand(!primaryRight, rhPos, lhPos, traverseDir, avgOut);
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
