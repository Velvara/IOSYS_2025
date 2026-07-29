using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
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
    /// PARTIAL CLASS — one state machine split across files by subsystem (ALL fields live HERE, so
    /// the inspector layout and serialized data are untouched):
    ///   ClimbController.cs            lifecycle: fields, setup, input, Update, candidate scan, grab/release
    ///   ClimbController.Traversal.cs  hand-over-hand stepping, brachiation, hold queries
    ///   ClimbController.Feet.cs       animated-legs smear/lock + procedural foot stepping, knee bends
    ///   ClimbController.BodyPose.cs   body orientation/position, pendulum, standoff, spine/head, grip offsets
    ///   ClimbController.PeekJump.cs   BracedReady peek, jump-off, arc camera, trajectory preview
    ///   ClimbController.Mantle.cs     reach-top mantle + get-up, reach-bottom step-off
    ///   ClimbController.Slide.cs      entry slide (falling grab on FREE surfaces), attach prompt, enter animation
    ///
    /// Runs after PlayerController (DefaultExecutionOrder) so the free-look re-enable sticks the same
    /// frame the FSM freezes look on entry.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public partial class ClimbController : MonoBehaviour
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
        [Tooltip("On TRUNKS only: align the torso's vertical with the trunk axis (the along-trunk direction at the hands) so 'up' always heads toward the tip however the trunk bends. Off / non-trunk surfaces stay world-up upright.")]
        [SerializeField] private bool alignTorsoToTrunkAxis = true;
        [Tooltip("Euler offset for the LEFT hand effector rotation (palm against the hold). Often ~ (0,180,0).")]
        [SerializeField] private Vector3 leftHandGripRotation = new Vector3(0f, 180f, 0f);
        [Tooltip("Euler offset for the RIGHT hand effector rotation — mirror of the left; tune to keep the elbow natural.")]
        [SerializeField] private Vector3 rightHandGripRotation = new Vector3(0f, 180f, 0f);
        [Tooltip("Local position offset of the hand effector at the hold, in the grip frame — shifts the wrist back so the FINGERS sit on the hold instead of the wrist. Applied to both hands.")]
        [SerializeField] private Vector3 handHoldOffset = Vector3.zero;
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
        [Tooltip("After a traversal search finds NO reachable hold (stuck against a dead end), wait this long before searching again — avoids rescanning every hold every frame while the input stays held.")]
        [SerializeField] private float traverseRetryInterval = 0.15f;

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
        [Tooltip("How often (Hz) to scan for a grab candidate while free. The scan runs every frame while a buffered Use press or the post-jump catch window is live, so grab responsiveness is unaffected.")]
        [SerializeField] private float candidateScanHz = 10f;

        [Header("Entry Slide (FREE surfaces — a falling grab slides down the wall)")]
        [Tooltip("Master toggle. A grab with DOWNWARD velocity on a Free-type surface slides down the wall before the hands attach; rising or grounded grabs never slide. Trunks and Ledge surfaces grab exactly as before.")]
        [SerializeField] private bool enableSlide = true;
        [Tooltip("Downward entry speed (m/s, x) → total slide distance (m, y). Left of the first key = no slide; keep the first key ABOVE 2 m/s (the motor reports −2 m/s while grounded). More speed = longer slide.")]
        [SerializeField] private AnimationCurve slideDistanceByVelocity = AnimationCurve.Linear(3f, 0f, 12f, 6f);
        [Tooltip("Normalized slide shape: x = 0..1 of slideDuration → y = 0..1 of the total distance. Authored EASE-OUT (steep start, flat end) so the slide starts fast and decelerates to a stop.")]
        [SerializeField] private AnimationCurve slideProgressCurve = new AnimationCurve(new Keyframe(0f, 0f, 0f, 2f), new Keyframe(1f, 1f, 0f, 0f));
        [Tooltip("Seconds a slide takes start→stop (same for every slide — a longer distance just slides faster, preserving the momentum feel).")]
        [SerializeField] private float slideDuration = 0.9f;
        [Tooltip("Brake testing toggle — off = holding Use never slows the slide (the grab press can't accidentally brake).")]
        [SerializeField] private bool enableSlideBrake = true;
        [Tooltip("While Use is HELD the slide advances at this fraction of its normal speed (0.5 = half, 0 = holding stops it dead). Releasing returns to full speed instantly; distance not travelled while braking is simply never covered (the slide still ends on the same clock).")]
        [Range(0f, 1f)]
        [SerializeField] private float brakeSpeedFactor = 0.5f;
        [Tooltip("The slide needs a hold within this radius of the chest to keep going — past the last holds (an overhang lip / the surface's edge) the climber falls immediately.")]
        [SerializeField] private float slideHoldRadius = 1.2f;
        [Tooltip("Max wall-probe distance (chest → wall) while sliding; the wall counts as lost beyond it (→ fall).")]
        [SerializeField] private float slideWallProbe = 1.2f;
        [Tooltip("Height above the player root of the feet-wall probe at slide end: wall there = braced entry; air (hands-only, e.g. the bottom lip of a wall) = free-hang entry, held until the feet find wall.")]
        [SerializeField] private float slideFeetProbeHeight = 0.35f;
        [Tooltip("Optional particle systems (e.g. on the hands, world-space distance emission) played while sliding: Play on slide start, Stop (emission only) at slide end — live particles fade out naturally. PERF TIP: set each system's main-module Stop Action to Disable so the GameObject deactivates itself once the last particle dies; this code re-activates it on the next slide.")]
        [SerializeField] private ParticleSystem[] slideParticles;

        [Header("Slip (risk-paint grip roll on hand steps — FREE surfaces)")]
        [Tooltip("Master toggle. Each hand step on a Free-type surface rolls against the RELEASED hold's painted risk class (ClimbableSurface green/blue/red percentages); a failed roll slides the climber down the wall with a Use-press recovery window. Trunks and Ledge surfaces never slip.")]
        [SerializeField] private bool enableSlip = true;
        [Tooltip("Constant downward speed (m/s) of a slip slide (also the fall speed carried into a failed recovery).")]
        [SerializeField] private float slipSpeed = 3.5f;
        [Tooltip("Max distance (m) a slip can slide; reaching it without a successful recovery press = fall.")]
        [SerializeField] private float slipMaxDistance = 3f;
        [Tooltip("Seconds the recovery prompt stays pressable once it appears. Use INSIDE the window = slide ends, control returns; Use BEFORE the prompt or missing the window = immediate fall.")]
        [SerializeField] private float slipPromptWindow = 0.25f;
        [Tooltip("Where in the slide the prompt appears, randomized between x and y as a fraction of the LATEST possible time (the window always fits before the slide's max distance).")]
        [SerializeField] private Vector2 slipPromptRange = new Vector2(0.2f, 0.8f);
        [Tooltip("Seconds after a slip FALL during which the climber can't re-grab (the exposed 'time of reattach').")]
        [SerializeField] private float slipReattachCooldown = 1.5f;
        [Tooltip("Prompt prefab showing the Use button icon (billboarded by code). Leave null for the placeholder white square.")]
        [SerializeField] private GameObject slipPromptPrefab;
        [Tooltip("World size (m) of the slip prompt.")]
        [SerializeField] private float slipPromptSize = 0.25f;
        [Tooltip("Local offset from the character root where the prompt appears (default: above the head).")]
        [SerializeField] private Vector3 slipPromptOffset = new Vector3(0f, 2f, 0f);

        [Header("Attach Prompt (world-space grab marker)")]
        [Tooltip("Show a world-space marker at the hold the character would attach to if Use were pressed (normal locomotion + airborne), updated every frame while a candidate exists.")]
        [SerializeField] private bool showAttachPrompt = true;
        [Tooltip("Optional marker prefab (billboarded to the camera by code). Leave null for the placeholder white square.")]
        [SerializeField] private GameObject attachPromptPrefab;
        [Tooltip("World size (m) of the marker.")]
        [SerializeField] private float attachPromptSize = 0.15f;
        [Tooltip("Lift off the surface along the hold normal so the marker never z-fights the wall.")]
        [SerializeField] private float attachPromptSurfaceOffset = 0.06f;

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

        [Header("Mantle / Reach-Top (automatic top-out)")]
        [Tooltip("Master toggle for the automatic mantle. Off = the body just stays braced at a near-horizontal top.")]
        [SerializeField] private bool enableMantle = true;
        [Tooltip("Allow mantling on Flora tree-trunk (procedural) surfaces. Off = trunks never top-out (you can't stand on a trunk tip); only authored cliffs mantle.")]
        [SerializeField] private bool mantleOnTrunks = false;
        [Tooltip("Arm the mantle when Dot(outwardNormal, up) rises ABOVE this — the hand holds face up enough that you're at a near-horizontal TOP edge (e.g. a trunk tip). Positive (≈0.5). NOTE: parsed vertical cliffs have HORIZONTAL hold normals (d≈0), so they top-out via the 'no reachable hold above' test instead.")]
        [SerializeField] private float mantleEnterDot = 0.5f;
        [Tooltip("A hold counts as 'above' (still climbable) only if it sits at least this far up the climb axis from the hands. Filters same-row neighbours so reaching a flat top rim still tops out.")]
        [SerializeField] private float mantleHoldAboveMargin = 0.2f;
        [Tooltip("If a hold 'above' the hands is within this distance, the climber keeps going (not yet at the top). Beyond it = nothing climbable above → topped out. Keep ≥ your traverse reach.")]
        [SerializeField] private float mantleReachAbove = 1.0f;
        [Tooltip("Layers treated as solid world geometry for the mantle probes (the ground you'll stand on + anything that could block standing room). Usually your environment/ground layers.")]
        [SerializeField] private LayerMask mantleSurfaceMask = ~0;
        [Tooltip("Up-raycast distance from the hands; this must be CLEAR (nothing overhead at the lip) or the mantle won't fire.")]
        [SerializeField] private float mantleClearanceUp = 1.2f;
        [Tooltip("How far horizontally PAST the lip (toward the top platform) to look for the landing surface.")]
        [SerializeField] private float mantleLandingForward = 0.5f;
        [Tooltip("Start the landing down-probe this far ABOVE the hand height.")]
        [SerializeField] private float mantleLandingProbeUp = 1.5f;
        [Tooltip("How far DOWN the landing probe reaches to find the top surface to stand on.")]
        [SerializeField] private float mantleLandingProbeDown = 3f;
        [Tooltip("Seconds to scripted-interpolate the body up and over onto the ledge. Set this to your ClimbUp CLIP LENGTH to sync the move with the animation.")]
        [SerializeField] private float mantleDuration = 0.6f;
        [Tooltip("Normalized VERTICAL progress over the mantle: x = 0..1 time → y = 0..1 of the total up move. Shape to your ClimbUp clip's upward root motion (e.g. rise early). Should pass through (0,0) and (1,1).")]
        [SerializeField] private AnimationCurve mantleUpCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("Normalized FORWARD progress (horizontal, toward the ledge): x = 0..1 time → y = 0..1 of the total forward move. Shape to your clip (e.g. hold ~0 during the rise, then move over). Should pass through (0,0) and (1,1).")]
        [SerializeField] private AnimationCurve mantleForwardCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
        [Tooltip("Hard cap: finalize the mantle even if the OnMantleComplete animation event never fires (guards a missing/mis-placed event from soft-locking the player). Keep ≥ your ClimbUp clip length.")]
        [SerializeField] private float mantleSafetyTimeout = 1.5f;
        [Tooltip("After a mantle or a drop, suppress new grab detection for this long so the climber doesn't instantly re-grab.")]
        [SerializeField] private float regrabCooldown = 0.5f;
        [Tooltip("On top-out, cross-fade the climb pose (whose ClimbUp clip ends crouched) out to the standing base idle over this many seconds instead of dropping it instantly — avoids a crouch→stand pop. Control stays locked through the fade. 0 = instant (old behaviour). Replace with a real ClimbUp→StandUp clip later for a proper stand-up.")]
        [SerializeField] private float mantleGetupFade = 0.2f;

        [Header("Reach-Bottom (auto step-off near ground)")]
        [Tooltip("Auto-release the climb when the body gets within reachBottomDistance of solid ground (stepping off at the bottom). Probes downward against mantleSurfaceMask.")]
        [SerializeField] private bool enableReachBottom = true;
        [Tooltip("Release when ground is within this distance below the body. Only checked once the grab has fully faded in (won't fire mid-grab).")]
        [SerializeField] private float reachBottomDistance = 0.5f;

        [Header("BracedReady (Jump peek)")]
        [Tooltip("Seconds the Cancel button must be HELD (from any climbing state) to release the climb entirely. " +
                 "A short TAP of Cancel instead just cancels BracedReady (back to climbing).")]
        [SerializeField] private float climbReleaseHoldTime = 1.5f;
        [Tooltip("Body yaw (deg) toward the released-hand side — applied as a grip-preserving ROOT rotation. Keep MODEST: the gripping arm must still reach its hold (too high over-stretches the arm). The head angle carries the rest of the 'look away'.")]
        [SerializeField] private float bracedReadyTorsoAngle = 30f;
        [Tooltip("Additional HEAD yaw (deg) on top of the body turn (post-solve, drags no hand — can be large to land the gaze away from the wall).")]
        [SerializeField] private float bracedReadyHeadAngle = 75f;
        [Tooltip("Seconds to ease into / out of the BracedReady turn.")]
        [SerializeField] private float bracedReadyTurnDuration = 0.3f;
        [Tooltip("Flip if the torso/head turn goes the wrong way for your rig (rotation axis convention).")]
        [SerializeField] private bool bracedReadyTurnInvert = false;
        [Tooltip("Local position offset (hold grip frame) eased onto the LOOSENED hand in BracedReady — author where the let-go hand sits (e.g. pulled back/down off the wall). Zero = stays on the hold.")]
        [SerializeField] private Vector3 loosenedHandOffset = Vector3.zero;
        [Tooltip("Local rotation offset (euler, grip frame) eased onto the LOOSENED hand in BracedReady — relax/rotate the let-go hand. Twists the wrist IN PLACE (decoupled from loosenedHandOffset, so re-angling won't slide the hand). Authored for the LEFT hand; mirrored for the right via loosenedHandMirror.")]
        [SerializeField] private Vector3 loosenedHandRotation = Vector3.zero;
        [Tooltip("Per-axis mirror (×) applied to the loosened POSITION offset for the RIGHT hand (values authored for the left). Default mirrors X (left↔right).")]
        [SerializeField] private Vector3 loosenedHandOffsetMirror = new Vector3(-1f, 1f, 1f);
        [Tooltip("Per-axis mirror (×) applied to the loosened ROTATION euler for the RIGHT hand (values authored for the left). Tuned SEPARATELY from the position mirror — a proper rotation mirror usually needs different signs (e.g. (1,-1,-1)).")]
        [SerializeField] private Vector3 loosenedHandRotationMirror = new Vector3(1f, -1f, -1f);
        [Tooltip("Anchor the loosened-hand POSITION relative to the HEAD bone (loosenedHandOffset becomes a head-LOCAL point) instead of the hold — the hand follows the head/upper body as it turns, instead of staying at the wall hold. Rotation is unaffected.")]
        [SerializeField] private bool loosenedHandAnchorHead = false;

        [Header("BracedReady Camera (jump-arc orbit)")]
        [Tooltip("Look-X magnitude past the OPPOSITE side that swaps the peek side (latched; ignored while a turn is mid-transition).")]
        [SerializeField] private float sideToggleThreshold = 0.5f;
        [Tooltip("Orbit the middle point at the CHARACTER's height (flat arc). Off = orbit the raw trajectory-midpoint height (the arc dips at the far/90° point).")]
        [SerializeField] private bool orbitAtCharacterHeight = true;
        [Tooltip("A/B TEST: on a side swap the camera sweeps an ELLIPTICAL curve whose crossing point is the predicted jump END pulled back toward the character (ellipseMidPullback, height-corrected) — instead of the circular orbit around the trajectory midpoint. Endpoints identical in both modes; the gaze hands off to the CHARACTER around the crossing and eases back onto the middle point toward either side. Safe to toggle live mid-peek.")]
        [SerializeField] private bool cameraPathEllipse = false;
        [Tooltip("Ellipse mode: how far (m) the curve's crossing point is pulled back from the predicted jump END toward the character — keeps the camera off the target climbable's surface.")]
        [SerializeField] private float ellipseMidPullback = 1.5f;
        [Tooltip("Multiplier on the arc radius. The base radius is auto-derived as half the horizontal jump distance (so it grows with jump length); this scales it. 1 = as computed, >1 pulls the camera further out.")]
        [SerializeField] private float arcRadiusScale = 1f;
        [Tooltip("Final arc radius clamp in metres, applied after arcRadiusScale (x = min, y = max) — keeps the camera from getting too tight on short jumps or too far on long ones.")]
        [SerializeField] private Vector2 arcRadiusClamp = new Vector2(0.5f, 20f);
        [Tooltip("Vertical offset (m) added to the CAMERA height on the arc (the look-at stays on the middle point, so this tilts the view down/up). 0 = level with the orbit height.")]
        [SerializeField] private float arcHeightOffset = 0f;
        [Tooltip("Seconds to blend the camera between free-look and the arc (peek enter / return to climb / exit).")]
        [SerializeField] private float arcBlendDuration = 0.3f;
        [Tooltip("Seconds to sweep the camera around the arc (0↔180, through the jump line at 90°) on a side swap.")]
        [SerializeField] private float arcSwapDuration = 0.3f;
        [Tooltip("Seconds to ease the camera from the held arc/jump pose back to free-look on exit, reattach, or landing.")]
        [SerializeField] private float camReattachRestoreTime = 0.3f;
        [Tooltip("While airborne after a jump-off, once the character has FALLEN this many metres below the launch height the parked camera stops tracking and eases back to normal free-look (the ClimbJump pose hold itself continues until land / reattach / the ragdoll fall height).")]
        [SerializeField] private float jumpCamFallRelease = 2f;
        [Tooltip("Seconds the parked jump camera takes to TURN from the arc's look-at (the trajectory midpoint) onto the character when the jump starts — eases the snap on the Jump press.")]
        [SerializeField] private float jumpCamTurnDuration = 0.5f;

        [Header("Climb Jump-Off (Jump from BracedReady)")]
        [Tooltip("Outward (away-from-wall) launch speed of the jump-off (decaying horizontal, blends to normal air control).")]
        [SerializeField] private float climbJumpOutward = 4f;
        [Tooltip("Upward launch speed of the jump-off.")]
        [SerializeField] private float climbJumpUp = 5f;
        [Tooltip("How fast the outward launch velocity decays back to normal air control (per second).")]
        [SerializeField] private float climbJumpDecay = 3f;
        [Tooltip("Seconds the FBBIK fades out after the jump leaves the wall (hands lerp off the holds onto the ClimbJump clip).")]
        [SerializeField] private float climbJumpIkFade = 0.15f;
        [Tooltip("Hard cap: launch even if the ClimbJump 'leave wall' animation event never fires (guards a missing event).")]
        [SerializeField] private float climbJumpSafetyTimeout = 1f;
        [Tooltip("After the jump leaves the wall, seconds to rotate the character 180° to face the jump direction. Reattaching to walls is blocked until this turn completes.")]
        [SerializeField] private float jumpTurnDuration = 0.5f;

        [Header("Jump Trajectory Line (BracedReady preview)")]
        [Tooltip("LineRenderer drawing the predicted jump arc while in BracedReady (set Use World Space). Optional — leave null to disable.")]
        [SerializeField] private LineRenderer peekTrajectoryLine;
        [Tooltip("Show the predicted jump arc during BracedReady.")]
        [SerializeField] private bool showTrajectory = true;
        [Tooltip("Number of sample points along the predicted arc (more = longer preview).")]
        [SerializeField] private int trajectoryPoints = 40;
        [Tooltip("Seconds between arc samples — total preview time = points × this.")]
        [SerializeField] private float trajectoryTimeStep = 0.05f;
        [Tooltip("Layers the arc preview stops at (climbables + ground/environment).")]
        [SerializeField] private LayerMask trajectoryCollisionMask = ~0;
        [Tooltip("Buffer window: a Use press within this time before touching a climbable still grabs (lets you catch a wall mid-jump).")]
        [SerializeField] private float useBufferTime = 0.2f;
        [Tooltip("Seconds after a jump-off during which air-control input is ignored, so the launch arc flies clean (no steering/bending). Set ~ the arc length you want uncontrolled.")]
        [SerializeField] private float climbJumpNoAirControlTime = 0.8f;
        [Tooltip("While airborne after a jump-off, the ClimbJump pose + camera are held until the character lands, reattaches, or falls THIS many metres below the launch height (drops the hold back to locomotion). PlayerRagdoll's own hard-fall monitor ragdolls around the same point and ends the hold itself — keep the two heights in the same ballpark.")]
        [SerializeField] private float jumpRagdollFallHeight = 5f;

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

        [Header("Fatigue Tremble")]
        [Tooltip("Growing horizontal jitter on the elbows/knees while moving as stamina nears empty.")]
        [SerializeField] private FatigueJitter fatigueJitter = new FatigueJitter();
        private float _jitterStrength;   // this frame's tremble strength (0 = none), computed in TickClimb

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
        [Tooltip("Log climb lifecycle events (grab/release/mantle/jump) to the Console. Disable for shipping/profiling — each log allocates.")]
        [SerializeField] private bool logClimbEvents = true;

        // -- Resolved player interfaces (runtime, never inspector-linked) --
        private IControlLock _controlLock;
        private IPlayerMotor _motor;
        private PlayerStamina _stamina;
        private IRagdoll _ragdoll;   // hard-fall ragdoll — gates grabs + tears the climb down via RagdollStarting
        private PlayerCameraRig _cameraRig;
        private InputHandler _input;
        private PlayerInput _playerInput;   // for direct Use/Cancel/Jump action subscriptions (climb-specific)
        private Transform _cam;   // gameplay camera, for camera-relative traversal input
        private Camera _mainCam;  // the camera the CinemachineBrain drives (guards the after-brain override)

        // -- Animator (climb pose layer) --
        private Animator _animator;
        private int _climbLayerIndex = -1;
        private bool _freeHang;   // current pose state (hysteresis-driven)
        private static readonly int _hIsClimbing = Animator.StringToHash("isClimbing");
        private static readonly int _hClimbHang = Animator.StringToHash("ClimbHang");
        private static readonly int _hFreeHang = Animator.StringToHash("FreeHang");
        private static readonly int _hClimbUp = Animator.StringToHash("ClimbUp");
        private static readonly int _hIsFreeHang = Animator.StringToHash("IsFreeHang");
        // Base-layer locomotion flags — parked grounded at mantle end (see FinishMantle). A climb
        // grabbed mid-air freezes these at their in-flight values for the whole climb (no motor
        // ticks under external control), and the getup fade would reveal the stale FALLING pose.
        private static readonly int _hBaseGrounded = Animator.StringToHash(Constants.ANIM_GROUNDED);
        private static readonly int _hBaseJump = Animator.StringToHash(Constants.ANIM_JUMP);
        private static readonly int _hBaseFreeFall = Animator.StringToHash(Constants.ANIM_FREE_FALL);
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

        /// <summary>Max distance a hand may step to a new hold — the natural "adjacency" radius for
        /// hold-graph tools (e.g. the carbine tether's geodesic reach): two holds within this are one
        /// hand-step apart.</summary>
        public float MaxStepReach => maxStepReach;

        /// <summary>Minimum outward-normal dot for a hold to count as "the same face" during traversal.
        /// Reused by hold-graph tools to keep a geodesic path on one face (no wrapping around corners).</summary>
        public float FacingCoherence => facingCoherence;

        /// <summary>Raised when the climb genuinely ENDS and control returns to locomotion: a wall release
        /// (Cancel-hold / reach-bottom step-off), a slide or slip fall, a ragdoll takeover, or a completed
        /// mantle top-out — every path routes through FinishRelease. NOT raised on a climb jump-off (that
        /// keeps the body in the jump flow, never touching FinishRelease), so a tethered tool that must
        /// persist through a jump can treat this as "the climber left the wall for good".</summary>
        public event System.Action ClimbReleased;

        /// <summary>Raised on a "free" Cancel TAP while climbing — a tap the controller did NOT spend
        /// cancelling a BracedReady peek (that always takes the tap first) and that wasn't a hold-to-
        /// release. Lets a tethered tool (the carbine) detach on a Cancel tap without the controller
        /// knowing about it. Only fires while actively climbing (not during a scripted mantle/jump/slide).</summary>
        public event System.Action CancelTapped;

        /// <summary>Raised when a climb JUMP-OFF ends with the character back on their feet (a grounded
        /// landing while descending — not a mid-air reattach, and not a fall past the ragdoll height,
        /// which routes through the ragdoll → ClimbReleased instead). Lets a tethered tool drop the rope
        /// on a clean landing.</summary>
        public event System.Action ClimbJumpLanded;

        // External hand-reach constraint (e.g. the carbine tether's rope range): when set, traversal
        // hold searches skip candidates the predicate rejects — the climber simply can't reach past
        // it, no clamping/fighting the body pose. Null = unconstrained. Registered by tool systems
        // (systems adapt to the controller); cleared by the owner when its restriction ends. The
        // predicate takes (surface, holdIndex) so the owner can key a precomputed per-hold set (the
        // carbine caches a geodesic reachable set at placement) — an O(1) lookup per candidate.
        private System.Func<ClimbableSurface, int, bool> _reachConstraint;

        /// <summary>Install (or clear, with null) a predicate limiting which holds the hands may step
        /// to, keyed by (surface, hold index). Used by the carbine tether to cap movement at the rope's
        /// along-surface range (see HoldGeodesic).</summary>
        public void SetReachConstraint(System.Func<ClimbableSurface, int, bool> allowHandTarget)
            => _reachConstraint = allowHandTarget;

        /// <summary>The RIGHT hand's current hold — the carbine placement point (CarbineController).
        /// False while the hand isn't SETTLED on a hold: not climbing, sliding/slipping, the
        /// post-slide dormant hang (deferred/per-limb attach), mantling, or a climb jump.</summary>
        public bool TryGetRightHandHold(out Vector3 pos, out Quaternion rot)
        {
            pos = default;
            rot = Quaternion.identity;
            if (!_isClimbing || _releasing || _sliding || _slipping || _handsPending ||
                _mantling || _gettingUp || _climbJumping || !_rHandAttached)
                return false;

            pos = _rig.GetCurrentPosition(ClimbEffector.RightHand);
            rot = _rhOutward.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(_rhOutward, _rhUp.sqrMagnitude > 1e-4f ? _rhUp : Vector3.up)
                : transform.rotation;
            return true;
        }

        // -- Current grab candidate (while not climbing) --
        private bool _hasCandidate;
        private Vector3 _candidatePos;
        private Quaternion _candidateRot;
        private ClimbableSurface _candidateSurface;
        private readonly Collider[] _candidateHits = new Collider[16];   // non-alloc overlap buffer
        private float _candidateScanTimer;

        // -- Cached "no climbable hold above the hands" (AtSurfaceTop is O(all holds) — only recompute
        //    when the hands actually change, i.e. when a step starts or settles) --
        private bool _atTopCache;
        private bool _atTopDirty = true;
        private bool _wasAnyMoving;

        // -- Climb lifecycle --
        private bool _releasing;
        private float _masterWeightTarget;

        // -- Entry slide (falling grab on a FREE surface: the body rides the wall down before the hands attach) --
        private bool _sliding;
        private float _slideElapsed;
        private float _slideTotalDist;         // from slideDistanceByVelocity(entry speed)
        private float _slideDistSoFar;         // distance already applied
        private float _slideSpeed;             // current m/s — handed back to the motor if the slide ends in a fall
        private bool _slideBraking;            // Use currently held — this frame advances at brakeSpeedFactor
        private float _slideBasePos;           // un-braked curve position (per-frame integration reference)
        private bool _handsPending;            // slide stopped, holds found but NOT attached — hands stay animation-driven until input
        private Vector3 _pendingRightPos;      // remembered right-hand hold for the deferred reach (Jump path)
        private Quaternion _pendingRightRot = Quaternion.identity;
        private bool _rHandAttached = true;    // per-limb lazy attach after a slide: a hand keeps following the
        private bool _lHandAttached = true;    // animation (effector weight 0) until ITS first step targets a hold
        private Vector3 _attachPinPos;         // reach attach: the slide's stop position — body held HERE until the first hand step
        private bool _attachOffsetHold;        // body pinned at _attachPinPos (overrides the hang formula)
        private Vector3 _attachBodyOffset;     // pin − formula, captured at release; decays across the first step
        private float _attachOffsetT = 1f;     // decay progress once released (1 = fully decayed / inactive)
        private float _attachOffsetDur = 0.0001f;
        private Vector3 _slideWallNormal = Vector3.forward;  // outward wall normal, re-probed while sliding
        private bool _postSlideFreeHang;       // slide ended hands-only (no wall at the feet) → hold free hang until the feet find wall

        // -- Slip (failed grip roll on a hand step: the body slides down the wall; Use inside the
        //    prompt window recovers into the slide-stop pin, anything else falls) --
        private bool _slipping;
        private float _slipElapsed;
        private float _slipDistSoFar;
        private float _slipPromptTime;         // seconds into the slip when the prompt appears
        private bool _slipPromptShown;
        private bool _slipResolved;            // the QTE consumed a press (recover started / early-press fall)
        private Transform _slipPrompt;         // Use-icon prompt (separate from the attach prompt)

        // -- Enter animation (ClimbEnter clip on every grab; freezes at the OnClimbEnterHold event while sliding) --
        private bool _entering;                // ClimbEnter currently owns the layer (until the clip completes)
        private bool _enterFrozen;             // frozen at the event frame (slide in progress)
        private float _enterResumeFixedTime;   // fixed seconds into the clip to resume from after a brake detour
        private float _enterLayerFloor;        // keeps the ClimbingLayer visible while the IK weight is still 0 (slide)
        private bool _hasClimbEnterState, _hasSlideBrakeState, _hasEnterSpeedParam;   // authored animator pieces present?
        private InputAction _useAction;        // polled for the brake (hold-Use); grabs stay event-driven
        private static readonly int _hClimbEnter = Animator.StringToHash("ClimbEnter");
        private static readonly int _hClimbSlideBrake = Animator.StringToHash("ClimbSlideBrake");
        private static readonly int _hClimbEnterSpeed = Animator.StringToHash("ClimbEnterSpeed");

        // -- Attach prompt (world-space marker at the current grab candidate) --
        private Transform _prompt;
        private bool _promptOnCharacter;       // parented to the body for the slide's brake window

        // -- Mantle / reach-top (scripted body move onto the ledge, finalized by OnMantleComplete) --
        private bool _mantling;
        private float _mantleTimer;
        private Vector3 _mantleStart, _mantleTarget;
        private Quaternion _mantleStartRot = Quaternion.identity, _mantleTargetRot = Quaternion.identity;
        private Vector3 _mantleUpAxis = Vector3.up, _mantleFwdAxis = Vector3.forward;
        private float _mantleUpDist, _mantleFwdDist;   // signed vertical + horizontal legs of the start→landing move
        private bool _gettingUp;                       // post-mantle: cross-fading the climb pose out to standing
        private float _getupTimer;
        private float _regrabCooldownTimer;

        // -- Cancel button: tap = cancel BracedReady, hold ≥ climbReleaseHoldTime = release the climb --
        private InputAction _cancelAction;
        private float _cancelHoldTimer;
        private bool _cancelWasPressed;

        // -- BracedReady (Jump peek sub-state) --
        private enum ClimbMode { Climbing, BracedReady }
        private ClimbMode _mode = ClimbMode.Climbing;
        private int _readySign;             // +1 = LEFT hand released (turn left); -1 = RIGHT hand released (turn right)
        private float _readyT;              // 0 = squared (climbing), 1 = fully turned into BracedReady
        private bool _readyReturning;       // easing _readyT back to 0 (return to climbing OR un-turn for a side-swap)
        private bool _pendingSwap;          // the un-turn is a side-swap → flip side + re-peek at _readyT==0
        private bool _pendulumFrozen;       // body pendulum held at rest (BracedReady)
        private bool _cameraOverridden;     // climb owns the camera (rig frozen) — arc peek or jump-hold
        // -- BracedReady jump-arc orbit camera (drives the brain's output camera from the after-brain event) --
        private bool _arcActive;            // peek arc engaged
        private float _arcAngle;            // 0..180 along the arc (0/180 = the two peek sides, 90 = down the jump line)
        private float _arcBlend;            // 0 = free-look, 1 = full arc
        private Vector3 _arcCenter;         // trajectory-midpoint (horizontal, y=0)
        private float _arcRadius, _arcInitialY, _arcFarY;
        private Vector3 _arcWallDir = Vector3.right, _arcJumpDir = Vector3.forward, _arcLookAt;
        private Vector3 _ellipseMid;        // ellipse mode: the swap curve's crossing point (jump end − pullback, height-corrected)
        private bool _jumpCamHold;          // jump/airborne: camera parked at its arc spot, turning to track the character
        private Vector3 _jumpCamPos;        // the parked world position (captured at BeginClimbJump)
        private Quaternion _jumpCamFromRot = Quaternion.identity;   // rotation at the Jump press (arc look-at)
        private float _jumpCamTurnT = 1f;   // 0→1 ease from the arc look-at onto the character
        private bool _reattachBlend;        // easing Camera.main back to free-look after exit/reattach
        private float _reattachT;
        private Vector3 _reattachFromPos;
        private Quaternion _reattachFromRot = Quaternion.identity;

        // -- Climb jump-off (from BracedReady) --
        private bool _climbJumping;         // wind-up: squaring up + ClimbJump clip, IK still pinned, awaiting leave-wall
        private bool _jumpAirborne;         // post-launch: airborne, holding ClimbJump's last frame + camera, fading IK out
        private float _jumpStartY;          // body Y at launch (for the fall-distance → ragdoll check)
        private Quaternion _jumpTurnFrom = Quaternion.identity, _jumpTurnTo = Quaternion.identity;
        private float _jumpTurnT = 1f;      // 0→1 over jumpTurnDuration; the post-jump 180° turn to face the jump
        private bool _jumpReattachBlocked;  // can't re-grab until the post-jump turn completes
        private float _jumpTimer;
        private Vector3 _jumpOutwardDir = Vector3.forward;
        private Vector3 _bracedReadyJumpDir = Vector3.forward;   // wall-perpendicular launch dir, cast at peek ENTER (pre-turn); reused by the jump + trajectory + arc
        private Vector3 _bracedReadyJumpHit = Vector3.zero;      // wall-ray HIT point = arc "initial point"
        private Vector3[] _trajPoints;      // reused buffer for the predicted-arc preview
        private float _useBufferTimer;      // buffered Use press (airborne wall-catch)
        private static readonly int _hClimbJump = Animator.StringToHash("ClimbJump");

        private Vector3 _rhOutward = Vector3.forward;  // outward normal of the right-hand hold
        private Vector3 _lhOutward = Vector3.forward;  // outward normal of the left-hand hold
        private Vector3 _rhUp = Vector3.up;            // up (≈ trunk axis toward the tip) of the right-hand hold
        private Vector3 _lhUp = Vector3.up;            // up (≈ trunk axis toward the tip) of the left-hand hold
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
            _ragdoll = GetComponentInParent<IRagdoll>();   // optional — climbing works without it
            _cameraRig = GetComponentInParent<PlayerCameraRig>();
            _input = GetComponentInParent<InputHandler>();
            _playerInput = GetComponentInParent<PlayerInput>();
            _mainCam = Camera.main;
            if (_mainCam != null) _cam = _mainCam.transform;

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
            if (_playerInput == null)
                Debug.LogWarning("[ClimbController] No PlayerInput found — Use/Cancel climb inputs won't work (debug key still does).");

            _oscillators = new OscillatorBank();
            _pendulum = new TwoMassPendulum();
            if (ik != null) _rig = new EffectorRig(ik, effectorEase);
        }

        // FBBIK is only needed while climbing: a full-body solve every idle frame costs CPU and its
        // per-frame bone handling can poison animation-event bone reads (the thrown-projectile spawn
        // bug — events fire before the animator writes the frame's pose). This runs AFTER FinalIK's
        // own Start ([DefaultExecutionOrder(100)]), so the solver is already initiated; Grab()
        // re-enables the component for the duration of a climb.
        private void Start()
        {
            if (ik != null) ik.enabled = false;

            // Enter-animation setup (optional until authored): a ClimbEnter state on the ClimbingLayer
            // whose speed Multiplier is the ClimbEnterSpeed float (that's what freezes the clip at the
            // slide-hold frame), plus a ClimbSlideBrake state for the brake pose. Missing pieces degrade
            // gracefully — no enter clip / no freeze / no brake pose — so the slide works regardless.
            if (_animator != null && _climbLayerIndex >= 0)
            {
                _hasClimbEnterState = _animator.HasState(_climbLayerIndex, _hClimbEnter);
                _hasSlideBrakeState = _animator.HasState(_climbLayerIndex, _hClimbSlideBrake);
                foreach (var p in _animator.parameters)
                    if (p.nameHash == _hClimbEnterSpeed) { _hasEnterSpeedParam = true; break; }
                if (!_hasClimbEnterState)
                    Debug.LogWarning("[ClimbController] ClimbingLayer has no 'ClimbEnter' state — grabs snap straight to the hang pose (no enter animation).");
                else if (!_hasEnterSpeedParam)
                    Debug.LogWarning("[ClimbController] Animator has no 'ClimbEnterSpeed' float — the enter clip can't freeze at the slide-hold frame (add the param and set it as the ClimbEnter state's speed Multiplier).");
            }
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
                ik.solver.OnPostUpdate += ApplyBracedReadyTurn;   // peek turn on top of everything
                _smearHooked = true;
            }

            // Drive the climb camera override AFTER the CinemachineBrain writes the transform (the brain updates
            // via a PlayerLoop insertion that runs after MonoBehaviour.LateUpdate, so a LateUpdate write gets
            // re-damped back toward free-look). This event fires right after the brain — our write is final.
            CinemachineCore.CameraUpdatedEvent.AddListener(OnCameraUpdated);

            // Climb-specific inputs straight off PlayerInput (InputHandler is locomotion-only).
            //   Use   = grab a wall when free (the rope system handles Use-near-rope while climbing).
            //   Jump  = enter BracedReady from a braced climb, or launch the jump-off from BracedReady.
            //   Cancel= POLLED (not an event): tap cancels BracedReady, hold ≥ climbReleaseHoldTime releases the climb.
            if (_playerInput != null && _playerInput.actions != null)
            {
                var use = _playerInput.actions["Use"];
                if (use != null) use.performed += OnUseInput;
                _useAction = use;   // also POLLED while sliding (hold-Use = brake)
                var jump = _playerInput.actions["Jump"];
                if (jump != null) jump.performed += OnJumpInput;
                _cancelAction = _playerInput.actions["Cancel"];
            }

            // A ragdoll (hard fall / external shove) takes the body — drop whatever climbing still holds.
            if (_ragdoll != null) _ragdoll.RagdollStarting += OnRagdollStarting;
        }

        private void OnDisable()
        {
            if (_smearHooked && ik != null && ik.solver != null)
            {
                ik.solver.OnPreUpdate -= SmearFeet;
                ik.solver.OnPostUpdate -= TwistSpine;
                ik.solver.OnPostUpdate -= HeadLook;
                ik.solver.OnPostUpdate -= ApplyBracedReadyTurn;
                _smearHooked = false;
            }

            CinemachineCore.CameraUpdatedEvent.RemoveListener(OnCameraUpdated);

            if (_playerInput != null && _playerInput.actions != null)
            {
                var use = _playerInput.actions["Use"];
                if (use != null) use.performed -= OnUseInput;
                var jump = _playerInput.actions["Jump"];
                if (jump != null) jump.performed -= OnJumpInput;
            }
            _cancelAction = null;
            _useAction = null;

            if (_ragdoll != null) _ragdoll.RagdollStarting -= OnRagdollStarting;
        }

        /// <summary>Use: grab a climbable when free. While climbing, Use is owned by the rope system
        /// (Use-near-a-rope hands the body off into rappel) — nothing to do here.</summary>
        private void OnUseInput(InputAction.CallbackContext ctx)
        {
            if (_slipping) { OnSlipUsePressed(); return; }   // the slip QTE owns Use (a performed callback is always a FRESH press)
            if (_isClimbing) { if (logClimbEvents) Debug.Log("[ClimbDiag] Use ignored — already climbing."); return; }                        // BracedReady is on Jump now; rope handoff is RopeController's
            if (_input != null && _input.AimHeld) { if (logClimbEvents) Debug.Log("[ClimbDiag] Use ignored — AimHeld (tool fires)."); return; }   // Use fires a tool while aiming — don't also grab
            if (_ragdoll != null && _ragdoll.IsRagdolled) { if (logClimbEvents) Debug.Log("[ClimbDiag] Use ignored — ragdolled."); return; }   // that press is the ragdoll recovery, not a grab
            _useBufferTimer = useBufferTime;                // buffered: Update grabs when a candidate is in reach (incl. mid-jump catch)
            if (logClimbEvents) Debug.Log("[ClimbDiag] Use pressed — buffered; waiting for a candidate in reach.");
        }

        /// <summary>Jump: enter BracedReady from a braced climb; press again in BracedReady to launch the
        /// jump-off (wind-up → ClimbJump clip → leave-wall event → launch).</summary>
        private void OnJumpInput(InputAction.CallbackContext ctx)
        {
            if (!_isClimbing || _releasing || _mantling || _gettingUp || _sliding || _slipping) return;

            // Post-slide dormant hang: one Jump press attaches both hands AND enters the peek — the
            // hands finish their reach while the BracedReady turn eases in (the peek only needs the
            // wall ray + hand-average, and the loosened-hand offset rides the tweening effector).
            if (_handsPending)
            {
                BeginDeferredReach();
                if (!_freeHang && !_climbJumping) EnterBracedReady();
                return;
            }

            if (_mode == ClimbMode.BracedReady)
            {
                if (!_climbJumping) BeginClimbJump();        // second press → launch
            }
            else if (!_freeHang && !_climbJumping)
            {
                EnterBracedReady();                          // braced → peek/aim
            }
        }

        /// <summary>Cancel (polled): a short TAP cancels BracedReady (back to climbing); HELD for
        /// climbReleaseHoldTime releases the climb entirely from any climbing state.</summary>
        private void TickCancelInput(float dt)
        {
            if (_cancelAction == null || !_isClimbing || _sliding || _slipping ||
                _releasing || _mantling || _gettingUp || _climbJumping || _jumpAirborne)
            {
                _cancelHoldTimer = 0f;
                _cancelWasPressed = false;
                return;
            }

            bool pressed = _cancelAction.IsPressed();
            if (pressed)
            {
                _cancelHoldTimer += dt;
                if (_cancelHoldTimer >= climbReleaseHoldTime)
                {
                    BeginRelease();                          // held long enough → drop the whole climb
                    _cancelWasPressed = false;
                    _cancelHoldTimer = 0f;
                    return;
                }
            }
            else
            {
                // Released before the hold threshold = a TAP. In BracedReady the tap cancels the peek
                // (first priority); otherwise it's a "free" tap handed to tools — the carbine tether
                // detaches on it (so from a peek it takes a second tap: cancel peek, then detach).
                if (_cancelWasPressed && _cancelHoldTimer < climbReleaseHoldTime)
                {
                    if (_mode == ClimbMode.BracedReady && !_readyReturning)
                        _readyReturning = true;
                    else
                        CancelTapped?.Invoke();
                }
                _cancelHoldTimer = 0f;
            }
            _cancelWasPressed = pressed;
        }

        /// <summary>Immediately releases the climb (external control returned THIS frame) for a handoff to
        /// another system — the rope system taking the body over into rappel. Unlike Cancel/BeginRelease
        /// this doesn't fade or reset velocity; the taking-over system repositions the body. Returns false
        /// if the climb can't be released right now (a scripted mantle/jump is running).</summary>
        public bool ReleaseForHandoff()
        {
            if (!_isClimbing || _releasing || _mantling || _gettingUp || _climbJumping || _jumpAirborne || _sliding)
                return false;
            _rig?.SetMasterWeight(0f);
            FinishRelease();   // control released, layers zeroed, IK asleep — rappel re-enables its own IK
            return true;
        }

        private void Update()
        {
            if (_regrabCooldownTimer > 0f) _regrabCooldownTimer -= Time.deltaTime;
            if (_useBufferTimer > 0f) _useBufferTimer -= Time.deltaTime;

            // Cancel is polled (not an event) so we can distinguish a tap (cancel BracedReady) from a
            // hold (release the climb).
            TickCancelInput(Time.deltaTime);

            // Post-jump airborne hold: motor flies the body; hold the ClimbJump pose + camera until land/catch/fall.
            if (_jumpAirborne) TickJumpAirborne(Time.deltaTime);

            // Look for a grab candidate while free (suppressed after a mantle/drop, and during the post-jump
            // turn). Throttled to candidateScanHz — but scanned every frame while a buffered Use press or the
            // airborne catch window is live, so grabs and mid-jump wall-catches stay frame-accurate.
            // Don't scan/grab while ANOTHER system owns the body (e.g. the rope system took us into
            // rappel from a climb) — otherwise a Use press meant to detach the rope could re-grab a
            // nearby climbable. The post-jump airborne window has already released external control, so
            // the mid-jump wall catch is unaffected.
            bool otherSystemOwnsBody = _controlLock != null && _controlLock.IsExternalControlActive && !_isClimbing;
            if (!_isClimbing && !otherSystemOwnsBody && _regrabCooldownTimer <= 0f && !_jumpReattachBlocked &&
                (_ragdoll == null || !_ragdoll.IsRagdolled))
            {
                _candidateScanTimer -= Time.deltaTime;
                // Also every frame while a candidate is LIVE: the attach prompt sits on the candidate
                // and must track it frame-accurately (10 Hz only paces the idle discovery scan).
                if (_useBufferTimer > 0f || _jumpAirborne || _hasCandidate || _candidateScanTimer <= 0f)
                {
                    _hasCandidate = TryFindCandidate(out _candidatePos, out _candidateRot, out _candidateSurface);
                    _candidateScanTimer = candidateScanHz > 0f ? 1f / candidateScanHz : 0f;
                }
            }
            else
                _hasCandidate = false;

            // Buffered Use → grab as soon as a candidate is in reach (real entry, or catching a wall mid-jump).
            if (!_isClimbing && _hasCandidate && _useBufferTimer > 0f && (_input == null || !_input.AimHeld))
            {
                Grab();
                _useBufferTimer = 0f;
            }

            // World-space attach prompt at the current candidate (a grab this frame re-routes it:
            // slide entries parent it to the body, calm grabs hide it).
            UpdateAttachPrompt();

            if (enableDebugToggle && Keyboard.current != null &&
                Keyboard.current[debugToggleKey].wasPressedThisFrame)
            {
                if (_isClimbing) { if (!_releasing) BeginRelease(); }
                else if (_hasCandidate) Grab();
            }

            if (_isClimbing) TickClimb(Time.deltaTime);

            // Jump-arc preview — only while peeking (BracedReady, not mid-jump).
            if (showTrajectory && peekTrajectoryLine != null && _isClimbing && _mode == ClimbMode.BracedReady && !_climbJumping)
                UpdateTrajectory();
            else
                HideTrajectory();
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
            int hitCount = Physics.OverlapSphereNonAlloc(origin, detectRadius, _candidateHits,
                                                         climbableLayers, QueryTriggerInteraction.Ignore);

            // --- TEMP DIAGNOSTIC (remove once the grab issue is resolved) ---
            bool diag = logClimbEvents && _useBufferTimer > 0f;
            int dSurfaces = 0, dNoReady = 0, dOutReach = 0, dTooVertical = 0, dNotFacing = 0, dAccepted = 0;
            float dNearest = float.MaxValue, dNearestFace = 0f;
            var dHits = diag ? new System.Text.StringBuilder() : null;

            float best = float.MaxValue;
            for (int h = 0; h < hitCount; h++)
            {
                Collider col = _candidateHits[h];
                ClimbableSurface s = col.GetComponentInParent<ClimbableSurface>();
                _candidateHits[h] = null;   // don't pin dead colliders across scans
                if (diag) dHits.Append($"  [{col.name}] layer={LayerMask.LayerToName(col.gameObject.layer)}({col.gameObject.layer}) climbable={(s != null ? s.name : "NONE")}\n");
                if (s == null) continue;
                if (!s.HoldsReady) { if (diag) dNoReady++; continue; }
                if (diag) dSurfaces++;

                Transform st = s.transform;
                var holds = s.Holds;
                for (int i = 0; i < holds.Count; i++)
                {
                    Vector3 wp = st.TransformPoint(holds[i].LocalPosition);
                    float d = Vector3.Distance(origin, wp);
                    if (diag && d < dNearest) { dNearest = d; dNearestFace = Vector3.Angle(transform.forward, -((st.rotation * holds[i].LocalRotation) * Vector3.forward)); }
                    if (d > maxReach) { if (diag) dOutReach++; continue; }
                    if (d >= best) continue;

                    Quaternion wr = st.rotation * holds[i].LocalRotation;
                    Vector3 outward = wr * Vector3.forward;   // hold forward = outward grab normal

                    // Reject floors/ceilings: the grab normal must be roughly horizontal.
                    if (Vector3.Angle(outward, Vector3.up) < minWallAngle) { if (diag) dTooVertical++; continue; }
                    // Must be roughly facing into the wall.
                    if (Vector3.Angle(transform.forward, -outward) > maxGrabAngle) { if (diag) dNotFacing++; continue; }

                    if (diag) dAccepted++;
                    best = d;
                    pos = wp;
                    rot = wr;
                    surface = s;
                }
            }

            if (diag && surface == null)
                Debug.Log($"[ClimbDiag] No grab. OverlapSphere colliders={hitCount}, climbableSurfaces(ready)={dSurfaces}, notReady={dNoReady}. " +
                          $"Holds rejected: outOfReach(>{maxReach}m)={dOutReach}, tooVertical(<{minWallAngle}°)={dTooVertical}, notFacing(>{maxGrabAngle}°)={dNotFacing}. " +
                          $"Nearest hold {(dNearest < float.MaxValue ? dNearest.ToString("0.00") + "m, facing " + dNearestFace.ToString("0") + "°" : "none")}.\n" +
                          $"Colliders in range (climbableLayers mask):\n{dHits}");
            // --- END DIAGNOSTIC ---

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
        /// Grab onto the current candidate: take the body over, then either attach immediately (snap
        /// both hands to holds, fade FBBIK in) or — falling onto a FREE-type surface — enter the entry
        /// slide first, attaching only where the slide stops (ClimbController.Slide.cs owns that phase).
        /// </summary>
        private void Grab()
        {
            if (_isClimbing || _controlLock == null || _rig == null || _candidateSurface == null) return;

            // Reattaching mid-jump: blend the camera back to free-look (over the new climb). Prime the
            // free-look root to face INTO the new wall first, so the blend settles in the normal
            // braced-hang framing (behind the climber) instead of wherever the jump viewpoint left it.
            if (_cameraOverridden)
            {
                // Prime free-look to face INTO the new wall through the RIG's internal yaw/pitch —
                // writing the target transform directly gets rewritten (unfrozen) or dragged by the
                // instant body turn below before the unfreeze-resync reads it (frozen: on a mid-air
                // grab ExternalControlState re-enters and freezes the rig), losing the prime.
                Vector3 intoWall = Vector3.ProjectOnPlane(-(_candidateRot * Vector3.forward), Vector3.up);
                if (_cameraRig != null && intoWall.sqrMagnitude > 1e-4f)
                    _cameraRig.SetLookDirection(intoWall.normalized);
                RestoreBracedReadyCamera();   // blends Camera.main back to the (primed) free-look
            }

            // Take over.
            _controlLock.RequestExternalControl();
            _isClimbing = true;
            _releasing = false;
            _currentSurface = _candidateSurface;
            _moveCooldown = 0f;
            _oscillators?.ResetAll();
            _stamina?.SetClimbState(true, false);
            _mode = ClimbMode.Climbing;
            _readyT = 0f;
            _readyReturning = false;
            _pendingSwap = false;
            _pendulumFrozen = false;
            _arcActive = false;
            _climbJumping = false;
            _jumpAirborne = false;
            _jumpReattachBlocked = false;
            _jumpTurnT = 1f;
            _postSlideFreeHang = false;
            _enterLayerFloor = 0f;
            if (_animator != null) _animator.SetBool(_hIsClimbing, true);

            // ENTRY SLIDE: a grab while FALLING onto a Free-type surface rides the wall down first —
            // the hands only attach where the slide stops. Rising/grounded grabs (vy ≥ 0 / −2 stick
            // velocity, below the curve's first key) and Trunk/Ledge surfaces attach immediately.
            float vy = _motor != null ? _motor.VerticalVelocity : 0f;
            if (enableSlide && vy < 0f && _candidateSurface.Type == ClimbType.Free)
            {
                float dist = slideDistanceByVelocity.Evaluate(-vy);
                if (dist > 0.05f)
                {
                    BeginEntrySlide(dist, vy);
                    return;
                }
            }

            HidePrompt();
            AttachHands(_candidatePos, _candidateRot);

            // Animator: enter the climb pose layer and pick the initial braced/free pose by orientation;
            // the ClimbEnter clip (when authored) overrides the pose state and blends into it on finish.
            _freeHang = Vector3.Dot(AvgOutward(), Vector3.up) < freeHangEnterDot;
            PlayPose(_freeHang, instant: true);
            PlayEnterAnim();

            _rig.SetMasterWeight(0f);
            _masterWeightTarget = 1f;
            _atTopDirty = true;
            _wasAnyMoving = false;
            if (logClimbEvents) Debug.Log($"[ClimbController] Grab on {_currentSurface.Source} surface ({_currentSurface.name}).");
        }

        /// <summary>
        /// Shared attach: wake FBBIK, put both hands on holds around the right-hand hold, reset the
        /// feet/pendulum/standoff bookkeeping, and place the body for the new hands. Two modes:
        /// SNAP (normal grab — hands lock to the holds, body repositions instantly under them) and
        /// REACH (<paramref name="reachFromCurrent"/>, the slide's deferred attach — the body must NOT
        /// move: the hand effectors seed at the exact midpoint that makes the body-pose formula
        /// reproduce the current transform, then TWEEN to the real holds; the body follows the moving
        /// hand-average continuously and the visible hands blend in over the IK master-weight fade).
        /// </summary>
        private void AttachHands(Vector3 rightPos, Quaternion rightRot, bool reachFromCurrent = false)
        {
            // The FBBIK solver sleeps while not climbing (perf + clean animation-event bone reads) — wake it.
            if (ik != null && !ik.enabled)
            {
                if (!ik.solver.initiated) ik.GetIKSolver().Initiate(ik.transform);   // belt & braces (Start order covers this)
                ik.enabled = true;
            }

            Vector3 rightOut = rightRot * Vector3.forward;        // hold forward = outward grab normal

            // Into-surface direction (flattened) for the shoulder-width offset of the second hand.
            Vector3 intoFlat = Vector3.ProjectOnPlane(-rightOut, Vector3.up);
            intoFlat = intoFlat.sqrMagnitude > 1e-4f ? intoFlat.normalized : -rightOut;
            Vector3 right = Vector3.Cross(Vector3.up, intoFlat).normalized;

            Vector3 leftTarget = rightPos - right * shoulderWidth;
            if (!FindHoldNear(_currentSurface, leftTarget, rightPos, out Vector3 leftPos, out Quaternion leftRot))
            {
                leftPos = leftTarget;        // no distinct hold nearby — place beside the right hand
                leftRot = rightRot;
            }
            _rhOutward = rightOut;
            _lhOutward = leftRot * Vector3.forward;
            _rhUp = rightRot * Vector3.up;
            _lhUp = leftRot * Vector3.up;

            if (reachFromCurrent)
            {
                // REACH: seed both hand effectors at the ACTUAL animated hand bones (SeedHandEffector
                // compensates the write-time grip offsets, so the written pose EQUALS the bone pose) —
                // the visible hands stay exactly where the animation left them while the IK weight
                // fades in, then tween to the holds (staggered, hand-over-hand). The mismatch this
                // leaves in the hang formula is bridged by the body pin captured by the caller.
                _bracedBodyRot = transform.rotation;    // facingOut base while the braced turn tweens
                _freeHangBodyRot = transform.rotation;
                SeedHandEffector(true);
                SeedHandEffector(false);
                _rig.SetPoseTarget(ClimbEffector.RightHand, rightPos, rightRot, traverseMoveDuration);
                _rig.SetPoseTarget(ClimbEffector.LeftHand, leftPos, leftRot, traverseMoveDuration,
                                   traverseMoveDuration * 0.5f);
            }
            else
            {
                // SNAP (pure hold rotation; grip offset applied live at write time). Feet + body
                // stay IK-free (free-hang).
                _rig.SnapToPose(ClimbEffector.RightHand, rightPos, rightRot);
                _rig.SnapToPose(ClimbEffector.LeftHand, leftPos, leftRot);
            }
            _rig.SetEffectorWeight(ClimbEffector.RightHand, 1f);
            _rig.SetEffectorWeight(ClimbEffector.LeftHand, 1f);
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RootBody, 0f);
            _rHandAttached = _lHandAttached = true;   // both hands are on (or reaching) holds
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
            if (!reachFromCurrent)
                _freeHangBodyRot = Quaternion.LookRotation(_freeHangFacing, Vector3.up);

            ConfigurePendulum();
            _pendulum?.Reset(_rig.HandAverage);   // reach mode: rest below the seeded mid == current body pos

            if (reachFromCurrent)
            {
                // No reposition AT ALL — the body PINS to the slide's stop position (UpdateBodyPose
                // writes _attachPinPos instead of the hang formula) while the hands reach the holds
                // and for as long as the player gives no input. The first hand step releases the pin
                // (ReleaseAttachOffset): the pin-to-formula difference is captured there and decays
                // across that step, where the body is moving anyway.
                _attachPinPos = transform.position;
                _attachOffsetHold = true;
                _attachOffsetT = 1f;

                // Ease the facing from the slide yaw onto the holds' braced facing.
                StartBracedTurn();

                // Seed the feet at their ACTUAL bone positions (weight 0 — the first plant then tweens
                // from where the animation really left them, instead of teleporting under new hips).
                Transform lfB = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.LeftFoot) : null;
                Transform rfB = _animator != null && _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.RightFoot) : null;
                Vector3 hipR = HipPosition;
                Vector3 brR = transform.right;
                _rig.SnapToPose(ClimbEffector.LeftFoot,
                    lfB != null ? lfB.position : hipR - Vector3.up * footDrop - brR * footSide, transform.rotation);
                _rig.SnapToPose(ClimbEffector.RightFoot,
                    rfB != null ? rfB.position : hipR - Vector3.up * footDrop + brR * footSide, transform.rotation);
                return;
            }

            UpdateBodyPose(instant: true);

            // Seed the feet under the hips (dangling, weight 0) so the first plant steps in from a sane
            // spot instead of swooping from the world origin.
            Vector3 hipG = HipPosition;
            Vector3 brG = transform.right;
            _rig.SnapToPose(ClimbEffector.LeftFoot, hipG - Vector3.up * footDrop - brG * footSide, transform.rotation);
            _rig.SnapToPose(ClimbEffector.RightFoot, hipG - Vector3.up * footDrop + brG * footSide, transform.rotation);
        }

        /// <summary>
        /// Snaps one hand's effector onto its LIVE animated bone, compensated for the write-time grip
        /// offsets (current = bone ∘ inverse(offsets), so WriteEffector reproduces the bone pose
        /// exactly) — the visible hand doesn't move when the effector's weight comes up, and any tween
        /// started from here departs from the true animated hand. Fallback (no humanoid bone): the
        /// shoulder point in front of the chest.
        /// </summary>
        private void SeedHandEffector(bool right)
        {
            ClimbEffector e = right ? ClimbEffector.RightHand : ClimbEffector.LeftHand;
            Transform bone = _animator != null && _animator.isHuman
                ? _animator.GetBoneTransform(right ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand)
                : null;
            if (bone != null)
            {
                Quaternion grip = Quaternion.Euler(right ? rightHandGripRotation : leftHandGripRotation);
                _rig.SnapToPose(e, bone.position - bone.rotation * handHoldOffset,
                                bone.rotation * Quaternion.Inverse(grip));
            }
            else
            {
                Vector3 p = transform.position + transform.forward * rootForwardOffset + Vector3.up * rootDownOffset
                            + transform.right * ((right ? 1f : -1f) * shoulderWidth * 0.5f);
                _rig.SnapToPose(e, p, transform.rotation);
            }
        }

        private void BeginRelease()
        {
            if (_cameraOverridden) RestoreBracedReadyCamera();   // exiting from a peek → hand the camera back
            _stamina?.SetPendingStaminaCost(0f);                 // a Cancel-exit from the peek never pays the cost
            _releasing = true;
            _masterWeightTarget = 0f;
            _enterLayerFloor = 0f;   // let the layer follow the fading IK weight down
            _regrabCooldownTimer = regrabCooldown;
            // Hand back to gravity from REST — the motor froze _verticalVelocity at grab time (often a fast
            // fall value), so without this the drop resumes mid-fall instead of ramping like a ledge step-off.
            _motor?.SetVerticalVelocity(0f);
            if (logClimbEvents) Debug.Log("[ClimbController] Release (fading out).");
        }

        private void FinishRelease()
        {
            _isClimbing = false;
            _releasing = false;
            _sliding = false;                    // slide + enter-anim state never survives a release
            _slipping = false;                   // nor a slip's
            HideSlipPrompt();
            _postSlideFreeHang = false;
            _handsPending = false;               // drop any un-consumed deferred attach
            _rHandAttached = _lHandAttached = true;
            _attachOffsetHold = false;           // kill any live reach-attach bridge offset
            _attachOffsetT = 1f;
            _entering = false;
            _enterFrozen = false;
            _enterLayerFloor = 0f;
            SetEnterSpeed(1f);
            HidePrompt();                        // un-parents from the body if the slide had claimed it
            StopSlideParticles();                // safety: a ragdoll/handoff mid-slide must not leave FX emitting
            if (_cameraOverridden) RestoreBracedReadyCamera();   // safety: never leave the rig frozen
            _mode = ClimbMode.Climbing;          // clear any BracedReady sub-state
            _readyT = 0f;
            _readyReturning = false;
            _pendingSwap = false;
            _pendulumFrozen = false;
            _arcActive = false;
            _climbJumping = false;
            _jumpAirborne = false;
            _jumpReattachBlocked = false;
            _rig.SetMasterWeight(0f);
            SetArmBendDirections(0f);            // hand the arm bend back to FBBIK's default
            SetLegBendDirections(0f);            // hand the knee bend back too
            _lFootWeight = _rFootWeight = 0f;
            if (_animator != null) _animator.SetBool(_hIsClimbing, false);
            if (_animator != null && _climbLayerIndex >= 0) _animator.SetLayerWeight(_climbLayerIndex, 0f);
            if (_animator != null && _climbLegsLayerIndex >= 0) _animator.SetLayerWeight(_climbLegsLayerIndex, 0f);
            _stamina?.SetClimbState(false, false);
            _stamina?.SetPendingStaminaCost(0f);      // safety: no exit path may leave the preview flashing
            _controlLock?.ReleaseExternalControl();   // FSM hands control back to Jump/Move/Idle
            if (ik != null) ik.enabled = false;       // solver back to sleep until the next grab
            if (logClimbEvents) Debug.Log("[ClimbController] Released — control returned.");
            ClimbReleased?.Invoke();                  // tethered tools (carbine) drop off the wall here
        }

        /// <summary>The player is about to ragdoll (hard fall or an external trigger): tear down whatever
        /// climbing still holds — the post-jump pose/camera hold or the climb itself — so the physics
        /// takeover starts from a clean body. Fired BEFORE PlayerRagdoll takes control, so the
        /// FinishRelease here can still ReleaseExternalControl without stepping on the ragdoll's own
        /// request.</summary>
        private void OnRagdollStarting()
        {
            _useBufferTimer = 0f;                    // a buffered Use press must not grab out of the ragdoll
            if (_jumpAirborne) EndJumpAirborne();    // drop the held ClimbJump pose + parked camera
            if (_isClimbing)
            {
                _mantling = false;                   // FinishRelease doesn't clear the scripted-move flags
                _gettingUp = false;
                _rig?.SetMasterWeight(0f);
                FinishRelease();                     // holds off, layers zeroed, control released, IK asleep
            }
        }

        /// <summary>Per-frame climb update: free-look, IK weight fade, effector tick, body follow.</summary>
        private void TickClimb(float dt)
        {
            // Keep free-look on (ExternalControlState froze it on entry) — EXCEPT while the climb owns the camera
            // (arc peek / jump-hold), where the rig is frozen and Camera.main is driven in LateUpdate.
            if (!_cameraOverridden) _cameraRig?.SetFrozen(false);

            // Post-mantle get-up: cross-fade the climb pose out to the standing base idle (control still locked).
            if (_gettingUp) { TickGetup(dt); return; }

            // Entry slide: the body rides the wall down with no IK and no traversal — its own tick.
            if (_sliding) { TickSlide(dt); return; }

            // Slip (failed grip roll): the body slides down the wall while the Use-QTE runs — own tick.
            if (_slipping) { TickSlip(dt); return; }

            // Fade FBBIK weight toward target (in on grab, out on release).
            float dur = _masterWeightTarget > _rig.MasterWeight ? ikFadeInDuration : ikFadeOutDuration;
            float step = dur > 0f ? dt / dur : 1f;
            _rig.SetMasterWeight(Mathf.MoveTowards(_rig.MasterWeight, _masterWeightTarget, step));

            if (_mantling)
            {
                // Keep the climb pose layer FULL so the ClimbUp clip plays through while the FBBIK
                // effector weight fades the hand-pins out; the mantle owns the body transform.
                if (_animator != null && _climbLayerIndex >= 0)
                    _animator.SetLayerWeight(_climbLayerIndex, 1f);
                _rig.Tick(dt);   // push the (now fading) master weight onto the effectors so the hands release
                TickMantle(dt);
                return;
            }

            // Drive the climb pose layer in lockstep with the FinalIK weight so the pose appears/clears
            // as the IK fades (the layer WEIGHT reveals climbing — the isClimbing bool alone won't).
            // _enterLayerFloor keeps the layer up after a slide attach (the slide already faded it to 1
            // while the IK weight was still 0); BeginRelease zeroes the floor so fade-outs still work.
            if (_animator != null && _climbLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLayerIndex, Mathf.Max(_rig.MasterWeight, _enterLayerFloor));

            TickEnterAnim();   // blend ClimbEnter into the hang pose when the clip completes

            // Braced↔free-hang blend: fades the leg layer, foot IK and standoff toward 0 in free hang.
            _bracedWeight = Mathf.MoveTowards(_bracedWeight, _freeHang ? 0f : 1f, freeHangBlendSpeed * dt);

            // Fatigue tremble: only while actually moving between holds, growing as stamina nears empty.
            _jitterStrength = (_stamina != null && _rig.AnyMoving) ? fatigueJitter.Strength(_stamina.NormalizedStamina) : 0f;
            fatigueJitter.Advance(_jitterStrength, dt);

            ApplyGripOffset();
            SetArmBendDirections(elbowBendWeight);
            if (!_releasing)
            {
                if (_climbJumping)
                    TickClimbJump(dt);     // jump wind-up: square up + ClimbJump clip, hands stay pinned
                else if (_mode == ClimbMode.BracedReady)
                    TickBracedReady(dt);   // peek: movement locked, ease the turn, fade the released hand
                else
                {
                    HandleTraversal(dt);
                    // Dormant post-slide hang: feet stay with the animation too (planting them while
                    // the IK sleeps would pre-latch weights/holds and snap on wake).
                    if (!_handsPending)
                        UpdateFeet(dt);      // plant/dangle each foot for this frame's Tick
                    UpdatePoseSwitch();
                }
            }
            SetLegBendDirections(Mathf.Max(_lFootWeight, _rFootWeight) * kneeBendWeight);

            _rig.Tick(dt);
            StepPendulum(dt);
            UpdateBodyPose();

            // The hands changed if a step started or settled this frame — refresh the cached
            // "at the top" answer on those transitions only (it's an O(all holds) scan).
            bool anyMoving = _rig.AnyMoving;
            if (anyMoving != _wasAnyMoving)
            {
                _atTopDirty = true;
                _wasAnyMoving = anyMoving;
            }

            // Trigger the mantle only AFTER UpdateBodyPose has placed the body for the current hands —
            // capturing _mantleStart before this froze a stale, pre-hand-step position, so the body snapped
            // down ~rootDownOffset at the start of the move. Now _mantleStart matches the visible body.
            if (!_releasing && _mode == ClimbMode.Climbing && TryMantle()) return; // reached a top → top-out
            if (!_releasing && _mode == ClimbMode.Climbing && TryReachBottom()) return; // near ground → step off

            _stamina?.SetClimbState(true, _rig.AnyMoving);

            // Completely drained → lose grip and drop off the wall (documented auto-tumble; mirrors the
            // rope free-hang let-go). Only from actual climbing — never mid-mantle/getup/jump/release.
            if (_stamina != null && _stamina.CurrentStamina <= 0f &&
                _mode == ClimbMode.Climbing && !_mantling && !_gettingUp && !_climbJumping && !_releasing)
            {
                if (logClimbEvents) Debug.Log("[ClimbController] Out of stamina — losing grip.");
                LoseGripToFall();
                return;
            }

            if (_releasing && _rig.MasterWeight <= 0.001f)
                FinishRelease();
        }

        /// <summary>Stamina hit zero: the climber loses grip and drops off the wall. Normalized with the
        /// rope wall-rappel / free-hang let-go — release into gravity (vertical velocity zeroed), and let
        /// PlayerRagdoll's own hard-fall monitor turn it into a tumble if the drop is far enough. Same
        /// outcome as the rope modes' Detached: a short drop lands, a long one ragdolls.</summary>
        private void LoseGripToFall()
        {
            _motor?.SetVerticalVelocity(0f);
            FinishRelease();
        }

    }
}
