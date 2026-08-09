using System.Collections;
using System.Collections.Generic;
using Game.Climbing;
using Game.PlayerV2;
using Game.PlayerV2.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side carbine (carabiner) system — placement + tether + detach + rope-range gate. While
/// CLIMBING with the carbine item selected (CycleItems prefab carrying CarbineItem), a Use press
/// spawns the carbine at the RIGHT hand's hold (consuming one from the inventory) and creates the
/// tether rope carbine → hip slot; pressing Use again while tethered REPLACES it (detach the old,
/// place a fresh one at the new hold, recompute the reachable set — the carbine climbs up with you).
/// If the inventory is empty the press is ignored (the current tether is kept). DETACHING is a Cancel
/// TAP (the rope lets go of the hip, dangles from the carbine, every material exposing the dissolve
/// property fades 0→1, then carbine + rope are destroyed); the climb also auto-detaches when it ends
/// somewhere — a deliberate release, a step-off, a mantle top-out (ClimbController.ClimbReleased) — but
/// a FALL keeps the rope, and so does a tethered jump-off. While tethered, hand steps are gated to the rope's along-surface reach (a geodesic
/// hold set cached at placement — HoldGeodesic). The tether sim is selectable: VerletTetherRope
/// (default — cheap, wraps around geometry) or PhysicsTetherRope (PhysX joint chain, the A/B compare).
///
/// Off the wall the rope is a leash: a gentle overrun is clamped elastically, but a HARD catch (radial
/// speed past yankRagdollSpeed) hands the body to physics — the pelvis is jointed to the rope's end and
/// the fall speed stays in the limbs, so the body is whipped, then hangs and swings until it settles.
/// Cancel drops the rope; Use-to-reattach (wall climb / rope-alone) and the carbine break check are the
/// remaining increments (they read IsRopeHanging / IsRopeHangSettled / AnchorPoint / RemainingLength).
/// </summary>
public class CarbineController : MonoBehaviour
{
    public enum TetherSim { VerletCollision, PhysXJoints }

    [Header("Attach Points")]
    [Tooltip("Child of the hip bone the rope ties to (create 'hipSlot' under the hip). Falls back to " +
             "the humanoid Hips bone, then this transform.")]
    public Transform hipSlot;

    [Header("Placement")]
    [Tooltip("Lift off the hold along its outward normal so the carbine mesh sits ON the rock, not in it.")]
    public float carbineSurfaceOffset = 0.03f;

    [Header("Tether Rope")]
    [Tooltip("Which simulation drives the tether. Verlet+collision is the default (cheap, stable, wraps " +
             "around geometry); PhysX joints is the A/B alternative to feel out. Applies to the NEXT " +
             "placement — safe to toggle between placements.")]
    public TetherSim simulation = TetherSim.VerletCollision;
    public Material ropeMaterial;
    [Tooltip("What the rope collides with / drapes over (the player and the carbine are excluded automatically).")]
    public LayerMask ropeCollisionMask = ~0;
    [Tooltip("PROGRESSIVE PAY-OUT: the simulated rope is only ever carbine→hip distance PLUS this " +
             "slack — it pays out as the player moves away and reels back in approaching, never past " +
             "the item's max rope length (the rest stays 'on the spool').")]
    public float paidSlack = 1f;
    [Tooltip("Range enforcement while CLIMBING: hand steps to holds farther than (max rope length − " +
             "this margin) from the carbine are refused — at rope's end the climber can't advance " +
             "without detaching. The margin accounts for the hip hanging below the hands.")]
    public float ropeEndMargin = 0.3f;
    [Tooltip("How fast spare rope reels back in (m/s) as the player moves toward the carbine. Paying " +
             "OUT is instant (movement away / a corner wrap must never be fought by the rope).")]
    public float reelSpeed = 3f;
    [Tooltip("Spare rope below this stays paid out (hysteresis) — avoids pay/reel flicker as the " +
             "needed length wobbles with the body.")]
    public float reelHysteresis = 0.2f;
    [Tooltip("How far off the surface a wrap pivot sits (the measuring chain's corner points).")]
    public float pivotSkin = 0.06f;

    [Header("Yank (airborne rope catch)")]
    [Tooltip("Extra stretch (m) the rope allows PAST its length before the catch is fully hard — the " +
             "elastic 'give' so a jump that runs the rope out snaps taut instead of hitting a wall.")]
    public float yankGiveDistance = 0.3f;
    [Tooltip("How fast (m/s) the stretched rope pulls the body back toward its true length after a yank — " +
             "the elastic snap-back / settle rate. Higher = snappier catch.")]
    public float yankReturnSpeed = 4f;
    [Tooltip("How fast the motor's accumulated fall speed bleeds off while the rope bears the body's weight " +
             "(so a later release doesn't inherit a runaway gravity build-up). Gentle keeps the first swing.")]
    public float yankVelocityDamp = 6f;

    [Header("Yank Ragdoll (hard catches)")]
    [Tooltip("Speed INTO the rope (m/s, measured along the rope) above which the catch RAGDOLLS the body " +
             "onto the tether instead of the soft clamp above: the hips are held at the rope's end and the " +
             "limbs are thrown by the fall. Below it a gentle step-off keeps the in-control elastic clamp.")]
    public float yankRagdollSpeed = 6f;
    [Tooltip("Fraction of the pre-yank velocity the PELVIS keeps at the catch. 0 = the hips stop dead on the " +
             "rope (the whole fall speed becomes limb whip); higher lets the hips carry through first.")]
    [Range(0f, 1f)] public float yankPelvisVelocityScale = 0f;
    [Tooltip("Extra velocity (m/s) along the fall direction handed to the LIMBS at the catch, on top of the " +
             "real fall speed — dials the whip up without changing how hard the rope stops the hips.")]
    public float yankWhipBoost = 2f;
    [Tooltip("Stiffness of the rope joint holding the hips while hanging. 0 = a rigid rope (no stretch); a " +
             "value here makes it springy, with yankJointDamper absorbing the bounce.")]
    public float yankJointSpring = 0f;
    [Tooltip("Damping of the rope joint's spring — only meaningful when yankJointSpring is non-zero.")]
    public float yankJointDamper = 0f;
    [Tooltip("AIR DRAG applied to the WHOLE ragdoll for the entire rope hang. Without it a rope hang never " +
             "ends: a point-constrained body settles into a CONICAL swing (circling the rope at near-constant " +
             "speed) that no velocity threshold ever sees the end of. Drag is proportional, so the violent " +
             "first swing still reads — it just doesn't last forever. 0 = none (the body may circle for ever).")]
    public float hangAirDrag = 0.6f;
    [Tooltip("Angular drag applied to the whole ragdoll while hanging — this is the one that kills the spin " +
             "around the rope. Raise it if the body keeps turning on the spot.")]
    public float hangAngularDrag = 2f;
    [Tooltip("SETTLE ASSIST — below this pelvis speed (m/s) the last of the swing is bled off directly, on top " +
             "of the drag above, so the body actually stops instead of creeping.")]
    public float hangDampBelowSpeed = 1f;
    [Tooltip("How hard that leftover swing is bled off (per second). Higher = the dangle comes to rest sooner. " +
             "0 disables the assist entirely (pure physics — the body may then take a long time to settle).")]
    public float hangDampRate = 3f;
    [Tooltip("SAFETY NET: seconds of hanging after which the reattach is offered even if the body never reads " +
             "as settled, so a stubborn swing can never leave the player with no way to act. 0 = wait for the " +
             "settle only.")]
    public float hangSettleTimeout = 4f;
    [Tooltip("REATTACH: how far from the settled hanging body a hold on the carbine's own surface may be for " +
             "Use to climb back onto the wall. Beyond it there is no wall to take, and Use takes the rope instead.")]
    public float reattachReach = 1.2f;
    [Tooltip("Seconds the ragdoll pose blends into the climbing pose on a wall reattach. Short reads as a " +
             "grab; long reads as slowly pulling yourself back on.")]
    public float reattachBlendTime = 0.3f;
    [Tooltip("Height above the feet the wall search is centred on while climbing the rope (chest height) — " +
             "the point that decides 'the wall is within reach, grab it instead of rappelling it'.")]
    public float reattachHeightOffset = 1.1f;

    [Header("Debug")]
    [Tooltip("While tethered: draw the pivot chain (yellow — the physical rope path used for pay-out + " +
             "the off-wall clamp) and the cached geodesic reachable-hold set in green (the holds a hand " +
             "may still step to on the rope). Gizmos must be enabled.")]
    public bool showRangeDebug = false;

    [Header("Detach Dissolve")]
    [Tooltip("Material property driven 0→1 on detach, on every renderer of the carbine AND the rope " +
             "that exposes it. Must match the shader's exposed reference name (e.g. \"dissolveTime\" " +
             "or \"_DissolveTime\").")]
    public string dissolveProperty = "dissolveTime";
    [Tooltip("Seconds the dissolve takes before carbine + rope are destroyed.")]
    public float dissolveDuration = 1.5f;

    [Tooltip("Log placement/detach events to the Console.")]
    public bool logEvents = true;

    // -- Read by the fall/yank stage --
    public bool IsTethered => _tether != null && _tether.HipAttached;
    public TetherRopeBase Tether => _tether;
    /// <summary>The tethering carbine's rope point (the anchor of a future yank/rappel).</summary>
    public Vector3 AnchorPoint => _ropePoint != null ? _ropePoint.position : transform.position;
    /// <summary>Rope still on the spool (max − paid out). The break formula reads paid-out length.</summary>
    public float RemainingLength => IsTethered ? _tether.RopeLength - _tether.ActiveLength : 0f;

    private ClimbController _climb;
    private CycleItems _items;
    private Animator _animator;
    private PlayerInput _playerInput;
    private InputAction _useAction;
    private InputAction _cancelAction;   // polled while hanging on the rope (drop the tether)
    private IPlayerMotor _motor;
    private IRagdoll _ragdoll;
    private IControlLock _controlLock;   // reattach handoffs (release before another system claims the body)
    private RappelController _rappel;    // rope-alone: the tether drives the existing free-hang rappel
    private Transform _ropeHands;        // where the rope is held in rope-alone (RopeController's hold point)
    private Transform _ropeFeet;         // between-the-feet slot the rope passes on its way down (rope mode)
    private Transform _tetherEnd;        // the rope's live player-side end (hip, or the hands in rope mode)
    private bool _ropeMode;              // the tether is currently driving a rope-alone rappel

    // Wall snapshot, captured at placement — where this rope's wall is and which way it faces.
    private Vector3 _anchorWallPoint;
    private Vector3 _anchorWallNormal;
    private bool _hasWallSnapshot;

    private GameObject _carbine;       // placed world carbine (tethered one only)
    private Transform _ropePoint;
    private TetherRopeBase _tether;

    private void Start()
    {
        _climb = GetComponentInParent<ClimbController>();
        _items = GetComponentInParent<CycleItems>();
        _animator = GetComponentInParent<Animator>();
        _playerInput = GetComponentInParent<PlayerInput>();
        _motor = GetComponentInParent<IPlayerMotor>();
        _ragdoll = GetComponentInParent<IRagdoll>();   // optional — no rig means the soft clamp always runs
        _controlLock = GetComponentInParent<IControlLock>();
        _rappel = GetComponentInParent<RappelController>();
        var ropeCtrl = GetComponentInParent<RopeController>();
        _ropeHands = ropeCtrl != null ? ropeCtrl.ropeHoldPoint : null;      // shared with the anchor-rope system
        _ropeFeet = ropeCtrl != null ? ropeCtrl.ropeFreeHangHold : null;    // the between-the-feet slot

        var actions = _playerInput != null ? _playerInput.actions : null;
        _useAction = actions != null ? actions["Use"] : null;
        _cancelAction = actions != null ? actions["Cancel"] : null;
        if (_useAction != null)
        {
            // Use (with a carbine selected, while climbing) PLACES a carbine — or REPLACES the current
            // one if already tethered (detach the old, place a fresh one, recompute the reachable set).
            _useAction.performed += OnUsePressed;
        }
        else
        {
            Debug.LogWarning("[CarbineController] No 'Use' action found — carbine placement disabled.");
        }

        if (_climb != null)
        {
            // Leaving the wall for good (release / reach-bottom / slide-slip fall / ragdoll / mantle
            // top-out) auto-detaches the tether — a climb jump-off does NOT fire this, so a tethered
            // jump keeps the rope MID-AIR. A clean FEET-DOWN landing after that jump (ClimbJumpLanded)
            // detaches at that point. A "free" Cancel TAP while climbing (not consumed by a BracedReady
            // cancel) detaches on demand.
            _climb.ClimbReleased += OnClimbReleased;
            _climb.CancelTapped += OnCancelTapped;
            _climb.ClimbJumpLanded += OnClimbJumpLanded;
        }

        if (_ragdoll != null) _ragdoll.RagdollRecovered += OnRagdollRecovered;
        if (_rappel != null) _rappel.OnRappelExited += OnRappelExited;
    }

    private void OnDestroy()
    {
        if (_useAction != null)
            _useAction.performed -= OnUsePressed;
        if (_climb != null)
        {
            _climb.ClimbReleased -= OnClimbReleased;
            _climb.CancelTapped -= OnCancelTapped;
            _climb.ClimbJumpLanded -= OnClimbJumpLanded;
            if (IsTethered) _climb.SetReachConstraint(null);
        }
        if (_hangingOnRope) EndRopeHang(false);   // never leave the ragdoll's recovery input suppressed
        if (_ragdoll != null) _ragdoll.RagdollRecovered -= OnRagdollRecovered;
        if (_rappel != null) _rappel.OnRappelExited -= OnRappelExited;
        DestroyPelvisJoint();
    }

    /// <summary>
    /// The climb ended. What that means for the rope depends entirely on HOW it ended:
    ///   • Fell (failed slip QTE, slide off the end of the wall, grip lost to empty stamina, hard fall) —
    ///     KEEP the rope. Catching exactly this is what a carabiner is for: the fall runs the tether out
    ///     and the yank takes it. To come off the wall deliberately while roped, tap Cancel first.
    ///   • Released (let go on purpose, stepped off near the bottom) / Landed (mantled on top, slid to the
    ///     floor) / HandedOff (the rope system took the body into a rappel) — the climber has arrived
    ///     somewhere, so the tether is dropped.
    /// A yank ragdoll is separately exempt: the climb does end, but the rope is the thing holding the body.
    /// A jump-off never reaches here at all (it keeps the body in the jump flow), so a tethered jump keeps
    /// its rope by construction.
    /// </summary>
    private void OnClimbReleased(ClimbEndKind kind)
    {
        if (!IsTethered || _hangingOnRope) return;
        if (kind == ClimbEndKind.Fell)
        {
            if (logEvents) Debug.Log("[CarbineController] Fell off the wall while tethered — the rope stays on.");
            return;
        }
        DetachRope();
    }

    /// <summary>The downed body stood back up. After a yank that ended ON THE GROUND that stand-up is the
    /// end of the whole tethered fall, so the rope goes with it. (Any other recovery while the rope still
    /// held the body is a safety seam — recovery is suppressed while actually hanging.)</summary>
    private void OnRagdollRecovered()
    {
        if (_hangingOnRope) EndRopeHang(_hangAwaitingUse);
    }

    /// <summary>A "free" Cancel tap while climbing (one the controller didn't spend cancelling a
    /// BracedReady peek) detaches the tether — the climber stays on the wall with full hold range back.</summary>
    private void OnCancelTapped()
    {
        if (IsTethered) DetachRope();
    }

    /// <summary>A tethered jump-off ended with a clean feet-down landing — drop the rope at that point.
    /// (A fall/ragdoll instead detaches via ClimbReleased; a mid-air reattach keeps the rope.)</summary>
    private void OnClimbJumpLanded()
    {
        if (IsTethered) DetachRope();
    }

    private void Update()
    {
        // Progressive pay-out, metered by the PIVOT CHAIN (geometric, noise-free — see UpdatePivots),
        // NOT by the verlet's own arc: a slack verlet chain always carries a little residual stretch,
        // so "pay out when the arc exceeds the paid length" was a ratchet that unspooled the whole
        // rope on its own sag. The chain gives the true required path (around corners included);
        // the sim is purely visual and just receives the resulting length.
        if (IsTethered)
        {
            // Meter to wherever the rope actually ENDS — the hip normally, the hands while they are
            // climbing the tether in rope-alone.
            Vector3 hip = TetherEndPoint.position;
            UpdatePivots(hip);

            // Pay-out length = current extension + slack, but the slack TAPERS from paidSlack down to 0 as
            // the extension nears the reach limit (RopeLength − ropeEndMargin — the same limit the hands are
            // gated to), so the rope pulls taut right as the climber reaches the end of their range instead
            // of hanging with a fixed +slack the reach gate never lets them close. Airborne (a tethered
            // jump, extension past the reach limit) the slack is already 0, so the full rope pays out to
            // RopeLength for the yank — hence the final RopeLength cap.
            float ext = PathLengthTo(hip);
            float reachLimit = Mathf.Max(0f, _tether.RopeLength - ropeEndMargin);
            float slack = Mathf.Min(paidSlack, Mathf.Max(0f, reachLimit - ext));
            float desired = Mathf.Min(ext + slack, _tether.RopeLength);
            float active = _tether.ActiveLength;
            if (desired > active) active = desired;   // pay out instantly — never fight movement/wraps
            else if (desired < active - reelHysteresis)
                active = Mathf.MoveTowards(active, desired, reelSpeed * Time.deltaTime);

            _tether.SetActiveLength(active);

            // Rope-alone runs on a tether whose effective anchor MOVES (wraps and unwraps over ledges as
            // they descend), so keep the rappel's length limits pointed at the live pivot.
            if (_ropeMode && _rappel != null)
            {
                _rappel.UpdateAnchor(LastPivot, Mathf.Max(0.5f, _tether.RopeLength - _pivotBaseLen));
                TickRopeModeWallCatch();   // drifted into reach of the wall → climb it, don't wall-rappel it
            }
        }

        if (_hangingOnRope) TickRopeHang();
    }

    private void LateUpdate()
    {
        // Range enforcement OFF the wall (tethered, not climbing): keep the body inside the rope's
        // reach, after the motor moved (RopeController's pattern). Covers BOTH cases with one law:
        //   • grounded/walking tethered — a soft leash;
        //   • AIRBORNE after a tethered jump — the YANK: fly free while the rope has slack, then the
        //     moment the body passes the rope's length it's caught (elastic, softened by yankGiveDistance)
        //     and gravity turns the catch into a swing/hang.
        // While CLIMBING the cap is enforced upstream instead (the reach gate refuses hand steps past
        // the range), so the climber never reaches here.
        if (_hangingOnRope) return;   // the tethered ragdoll owns the body now (the CharacterController is off)
        if (!IsTethered || _motor == null) { EndYank(); return; }
        if (_climb != null && _climb.IsClimbing) { EndYank(); return; }
        // Rope-alone: the rappel drives the transform along the rope and enforces its own length limit —
        // a leash clamp on top would fight it every frame.
        if (_rappel != null && _rappel.IsRappelling) { EndYank(); return; }

        // A fall can ALREADY be in ragdoll when the rope runs out — PlayerRagdoll's own hard-fall watch
        // fires at hardFallHeight, which a long tethered drop passes before reaching the rope's end. The
        // rope must still catch that body, so the measurement follows the PELVIS there (the root
        // transform is stale while physics owns the pose) and the catch just adds the constraint.
        bool alreadyRagdolled = _ragdoll != null && _ragdoll.IsRagdolled;
        if (alreadyRagdolled && _ragdoll.PelvisBody == null) { EndYank(); return; }

        // The rope sphere sits at the LAST WRAP PIVOT with only the rope left past it as radius —
        // wrapped around a corner, anchor-straight distance would let the body travel far beyond the
        // rope; unwrapped, the last pivot IS the anchor and this is the plain rope sphere.
        Vector3 center = LastPivot;
        float maxLen = Mathf.Max(0.5f, _tether.RopeLength - _pivotBaseLen);

        Vector3 pos = alreadyRagdolled ? _ragdoll.PelvisBody.position : _motor.Transform.position;
        Vector3 offset = pos - center;
        float dist = offset.magnitude;
        if (dist <= maxLen) { EndYank(); return; }   // within reach — no constraint, no yank

        Vector3 dir = dist > 1e-4f ? offset / dist : Vector3.up;

        // Rope caught a body that is already loose in physics: hang it, no whip to apply (the fall
        // speed is already in the bones — the joint arrests the hips and the limbs carry on by themselves).
        if (alreadyRagdolled)
        {
            BeginRopeHang(Vector3.zero, true);
            return;
        }

        // HARD CATCH → tethered ragdoll. What matters is the speed the rope has to arrest, i.e. the
        // component ALONG the rope: a body swinging sideways at the end of a taut rope isn't being
        // caught by anything. Above the threshold the hips are handed to the rope and physics takes
        // the body (see BeginRopeHang); below it, the in-control elastic clamp below still runs.
        if (_ragdoll != null && yankRagdollSpeed > 0f)
        {
            CharacterController cc = _motor.Controller;
            Vector3 vel = cc != null && cc.enabled ? cc.velocity : Vector3.zero;
            if (Vector3.Dot(vel, dir) >= yankRagdollSpeed)
            {
                BeginRopeHang(vel, false);
                return;
            }
        }

        // Past the rope. SOFTENED GIVE: allow up to yankGiveDistance of extra stretch (hard cap), then
        // reel that stretch back toward the true length at yankReturnSpeed — an elastic snatch that
        // settles taut, not a dead wall. Only the RADIAL component is touched, so the tangential swing
        // (the dangle) is left to gravity.
        float capped = Mathf.Min(dist, maxLen + Mathf.Max(0f, yankGiveDistance));
        float settled = Mathf.MoveTowards(capped, maxLen, Mathf.Max(0f, yankReturnSpeed) * Time.deltaTime);
        _motor.Controller.Move((center + dir * settled) - pos);

        if (!_yanking)
        {
            _yanking = true;
            if (logEvents) Debug.Log("[CarbineController] Rope YANK — caught at the rope's end.");
        }
        _motor.SuppressAirControl(0.15f);   // re-armed each caught frame; clears shortly after release

        // Bleed the motor's accumulated fall velocity while the rope bears the weight, so a later
        // release doesn't inherit a runaway gravity build-up. Gentle, so the first swing still reads.
        if (_motor.VerticalVelocity < 0f)
            _motor.SetVerticalVelocity(
                Mathf.MoveTowards(_motor.VerticalVelocity, 0f, Mathf.Max(0f, yankVelocityDamp) * Time.deltaTime));
    }

    /// <summary>True while the airborne rope catch (yank) is actively holding the body. Read by the
    /// dangle→reattach/rappel stage.</summary>
    public bool IsYanking => _yanking;
    private bool _yanking;

    private void EndYank() => _yanking = false;

    // ------------------------------------------------------------------ tethered ragdoll (hard catch)
    //
    // A hard yank doesn't stop the character — it stops their HIPS. The pelvis is handed to the rope
    // (a spherical joint limit at the rope's last wrap pivot) while every other bone keeps the fall
    // speed, so arms, legs, spine and head are thrown past the arrested hips; the body then hangs and
    // swings on the rope under plain gravity, colliding with whatever it meets, until it settles.
    // The CharacterController is off for the whole hang — the soft clamp above is skipped.

    private bool _hangingOnRope;   // the tethered ragdoll owns the body
    private bool _hangSettled;     // the swing has come to rest (the reattach window)
    private float _hangTime;       // seconds hanging — backs the settle timeout
    private bool _hangAwaitingUse; // settled on the GROUND: the ragdoll's own Use stand-up finishes the fall
    private GameObject _pelvisAnchorGO;
    private Rigidbody _pelvisAnchorRb;
    private ConfigurableJoint _pelvisJoint;

    /// <summary>True while the body hangs on the rope in ragdoll after a hard yank.</summary>
    public bool IsRopeHanging => _hangingOnRope;
    /// <summary>True once the hanging body has stopped swinging — the point a reattach becomes meaningful.</summary>
    public bool IsRopeHangSettled => _hangingOnRope && _hangSettled;

    /// <summary>Rope left past the last wrap pivot — the radius the hips hang at.</summary>
    private float CurrentHangRadius =>
        _tether != null ? Mathf.Max(0.5f, _tether.RopeLength - _pivotBaseLen) : 0.5f;

    /// <param name="alreadyRagdolled">The body was already loose in physics when the rope ran out — only
    /// the constraint is added; the fall speed is in the bones and whips the limbs on its own.</param>
    private void BeginRopeHang(Vector3 velocity, bool alreadyRagdolled)
    {
        if (_ragdoll == null || _ragdoll.PelvisBody == null) return;   // no ragdoll rig — keep the soft clamp

        // Set BEFORE the trigger: the ragdoll fires RagdollStarting synchronously, and a climb ending
        // on that event would auto-detach the very rope that is about to hold the body.
        _hangingOnRope = true;
        _hangSettled = false;
        _yanking = true;
        _ragdoll.SetRecoverySuppressed(true);   // Use belongs to the reattach now, not the stand-up
        _ragdoll.SetAirSteerEnabled(false);     // a body on a rope hangs passively (and never lands to latch off)

        if (!alreadyRagdolled)
        {
            Vector3 whip = velocity.sqrMagnitude > 1e-4f
                ? velocity.normalized * Mathf.Max(0f, yankWhipBoost)
                : Vector3.zero;
            _ragdoll.TriggerRagdoll(whip, yankPelvisVelocityScale, false);

            if (!_ragdoll.IsRagdolled)   // refused (no rig) — fall back to the elastic clamp
            {
                _hangingOnRope = false;
                _yanking = false;
                _ragdoll.SetRecoverySuppressed(false);
                return;
            }
        }

        CreatePelvisJoint();
        _hangTime = 0f;
        // Air resistance for the whole rig, for the whole hang — the only thing that ends a conical swing.
        _ragdoll.SetBoneDamping(hangAirDrag, hangAngularDrag);
        if (logEvents)
            Debug.Log(alreadyRagdolled
                ? "[CarbineController] Rope caught a falling ragdoll — hanging."
                : $"[CarbineController] Hard yank at {velocity.magnitude:0.0} m/s — ragdoll on the rope.");
    }

    /// <summary>Hangs the pelvis off the rope. The joint lives on a throwaway kinematic anchor body
    /// parked at the rope's last wrap pivot rather than on the player's skeleton — destroying that one
    /// object removes the constraint whole, with no component churn on the rig. All three linear axes
    /// limited = a spherical limit, i.e. a rope; angular free so the body tumbles as it swings.</summary>
    private void CreatePelvisJoint()
    {
        DestroyPelvisJoint();
        Rigidbody pelvis = _ragdoll?.PelvisBody;
        if (pelvis == null) return;

        _pelvisAnchorGO = new GameObject("CarbineRopeHangAnchor");
        _pelvisAnchorGO.transform.position = LastPivot;
        _pelvisAnchorRb = _pelvisAnchorGO.AddComponent<Rigidbody>();
        _pelvisAnchorRb.isKinematic = true;
        _pelvisAnchorRb.useGravity = false;

        _pelvisJoint = _pelvisAnchorGO.AddComponent<ConfigurableJoint>();
        _pelvisJoint.connectedBody = pelvis;
        _pelvisJoint.autoConfigureConnectedAnchor = false;
        _pelvisJoint.anchor = Vector3.zero;
        _pelvisJoint.connectedAnchor = Vector3.zero;
        _pelvisJoint.xMotion = ConfigurableJointMotion.Limited;
        _pelvisJoint.yMotion = ConfigurableJointMotion.Limited;
        _pelvisJoint.zMotion = ConfigurableJointMotion.Limited;
        _pelvisJoint.angularXMotion = ConfigurableJointMotion.Free;
        _pelvisJoint.angularYMotion = ConfigurableJointMotion.Free;
        _pelvisJoint.angularZMotion = ConfigurableJointMotion.Free;
        _pelvisJoint.linearLimit = new SoftJointLimit
        {
            limit = CurrentHangRadius,
            bounciness = 0f,
            contactDistance = 0.02f
        };
        _pelvisJoint.linearLimitSpring = new SoftJointLimitSpring
        {
            spring = Mathf.Max(0f, yankJointSpring),   // 0 = a rigid rope
            damper = Mathf.Max(0f, yankJointDamper)
        };
        // A whole ragdoll's weight hanging on one limit visibly stretches it without projection.
        _pelvisJoint.projectionMode = JointProjectionMode.PositionAndRotation;
        _pelvisJoint.projectionDistance = 0.05f;
        _pelvisJoint.enablePreprocessing = false;
    }

    private void DestroyPelvisJoint()
    {
        if (_pelvisAnchorGO != null) Destroy(_pelvisAnchorGO);
        _pelvisAnchorGO = null;
        _pelvisAnchorRb = null;
        _pelvisJoint = null;
    }

    private void FixedUpdate()
    {
        if (!_hangingOnRope) return;

        // SETTLE ASSIST. A body on a rope is a pendulum with nothing to lose energy to — no ground
        // friction, and PhysX won't sleep a body a joint keeps nudging — so the swing can decay to a
        // slow drift that hovers just above the settle threshold forever, and the reattach never opens.
        // Once the swing is small enough to be finished, bleed the remainder off. Gated by speed so the
        // big first swing (the part that reads as the yank) is never touched.
        Rigidbody pelvis = _ragdoll?.PelvisBody;
        if (pelvis != null && !_hangSettled && hangDampRate > 0f &&
            pelvis.linearVelocity.sqrMagnitude < hangDampBelowSpeed * hangDampBelowSpeed)
        {
            float keep = Mathf.Clamp01(1f - hangDampRate * Time.fixedDeltaTime);
            pelvis.linearVelocity *= keep;
            pelvis.angularVelocity *= keep;   // a slow spin keeps the body 'alive' too
        }

        if (_pelvisAnchorRb == null || _pelvisJoint == null) return;

        // The rope can wrap and unwrap while the body swings, so the hang sphere follows the live pivot
        // chain: centre = the last wrap pivot, radius = the rope left past it.
        _pelvisAnchorRb.position = LastPivot;
        float radius = CurrentHangRadius;
        SoftJointLimit lim = _pelvisJoint.linearLimit;
        if (Mathf.Abs(lim.limit - radius) > 0.005f)   // joint writes aren't free — ignore sub-5mm churn
        {
            lim.limit = radius;
            _pelvisJoint.linearLimit = lim;
        }
    }

    /// <summary>Has the hang come to rest? Normally the ragdoll's own settle test, but with a hard time
    /// limit behind it: that test reads the pelvis's SPEED, and a body circling the rope in a conical
    /// swing holds a near-constant speed, so a stubborn swing must never be able to leave the player
    /// hanging with no way to act.</summary>
    private bool HangHasSettled()
    {
        if (_ragdoll != null && _ragdoll.IsSettled) return true;
        if (hangSettleTimeout <= 0f || _hangTime < hangSettleTimeout) return false;

        if (logEvents)
            Debug.Log("[CarbineController] Hang never settled — offering the reattach anyway (timeout).");
        return true;
    }

    private void TickRopeHang()
    {
        _hangTime += Time.deltaTime;

        if (!_hangSettled && HangHasSettled())
        {
            _hangSettled = true;

            // Came to rest ON THE GROUND rather than dangling: the fall is over, so hand the body back
            // to the ragdoll's own "Use to get up" — the rope has nothing left to hold, and the tether
            // is dropped when that stand-up completes (OnRagdollRecovered).
            if (_ragdoll != null && _ragdoll.IsPelvisGrounded)
            {
                if (logEvents)
                    Debug.Log("[CarbineController] Yank settled on the ground — Use to get up (the tether drops with it).");
                _hangAwaitingUse = true;
                DestroyPelvisJoint();                    // nothing left to hang from
                _ragdoll.SetRecoverySuppressed(false);   // the standard stand-up owns Use again
                return;
            }

            if (logEvents) Debug.Log("[CarbineController] Hanging body settled — Use to reattach.");
        }

        // Cancel while hanging: let go of the rope. The body becomes an ordinary ragdoll mid-fall and
        // the standard Use recovery stands it back up.
        if (_cancelAction != null && _cancelAction.WasPressedThisFrame())
        {
            if (logEvents) Debug.Log("[CarbineController] Cancel while hanging — rope let go, falling free.");
            EndRopeHang(true);
        }
    }

    private void EndRopeHang(bool detachTether)
    {
        if (!_hangingOnRope) return;
        _hangingOnRope = false;
        _hangSettled = false;
        _hangAwaitingUse = false;
        _hangTime = 0f;
        _yanking = false;
        DestroyPelvisJoint();
        _ragdoll?.ResetBoneDamping();   // give the rig its authored damping back — the air drag was ours
        _ragdoll?.SetRecoverySuppressed(false);
        if (detachTether && IsTethered) DetachRope();   // callers that keep the rope pass false
    }

    // ------------------------------------------------------------------ reachable-hold set (geodesic)
    //
    // While climbing, the rope's reach is measured NOT as straight-line distance but PROGRESSIVELY
    // hold → neighbouring hold along the surface (a geodesic flood-fill from the carbine's own hold —
    // HoldGeodesic / HoldSpatialGrid). The anchor (the carbine) and the rope length are both fixed for
    // a placement, so the reachable set is computed ONCE here, cached, and the per-hand-step gate is
    // then an O(1) lookup; it is recomputed only when the carbine is replaced. The pivot chain above
    // still measures the physical 3D rope for the pay-out visual and the off-wall ground clamp.
    private ClimbableSurface _carbineSurface;
    private bool[] _reachable;

    private void ComputeReachableSet()
    {
        _reachable = null;
        _carbineSurface = _climb != null ? _climb.CurrentSurface : null;
        if (_carbineSurface == null || !_carbineSurface.HoldsReady || _tether == null) return;

        var holds = _carbineSurface.Holds;
        float link = Mathf.Max(0.05f, _climb.MaxStepReach);   // "adjacent" = one hand-step apart
        var grid = new HoldSpatialGrid(holds, _carbineSurface.transform, link);
        int anchor = grid.NearestIndex(AnchorPoint);
        float budget = Mathf.Max(0f, _tether.RopeLength - ropeEndMargin);

        _reachable = new bool[holds.Count];
        int reached = HoldGeodesic.ComputeReachable(grid, anchor, budget, link, _climb.FacingCoherence, _reachable);
        if (logEvents)
            Debug.Log($"[CarbineController] Reachable holds within {budget:0.0} m of rope along the surface: " +
                      $"{reached}/{holds.Count} (link {link:0.00} m).");
    }

    /// <summary>Reach gate registered with the ClimbController while tethered: a hand may only step to
    /// a hold inside the cached geodesic set (holds within the rope's along-surface reach from the
    /// carbine). A different surface (climbed onto while tethered) is left unconstrained.</summary>
    private bool AllowHandTargetIndex(ClimbableSurface surface, int holdIndex)
    {
        if (!IsTethered) return true;
        if (surface != _carbineSurface || _reachable == null) return true;
        return (uint)holdIndex < (uint)_reachable.Length && _reachable[holdIndex];
    }

    // ------------------------------------------------------------------ rope path measurement
    //
    // The MEASURING model of the rope: a chain of wrap pivots (anchor → corner bends → hip),
    // maintained by raycast — the segment to the hip gets blocked → bend the rope at the
    // obstruction (wrap); the pivot before the last sees the hip again → drop the last bend
    // (unwrap). Purely geometric, so it carries none of the verlet sim's stretch noise, ignores
    // ground drape, and gives the exact "how much rope does this position require" the pay-out,
    // the reach constraint and the ground clamp all share.

    private readonly List<Vector3> _pivots = new List<Vector3>();
    private float _pivotBaseLen;   // Σ segment lengths between pivots (excludes lastPivot→hip)
    private const int MaxPivots = 16;
    private static readonly RaycastHit[] RayHits = new RaycastHit[16];

    private Vector3 LastPivot => _pivots.Count > 0 ? _pivots[_pivots.Count - 1] : AnchorPoint;

    /// <summary>Rope required to reach <paramref name="point"/>: the wrapped base + the last straight leg.</summary>
    private float PathLengthTo(Vector3 point) => _pivotBaseLen + Vector3.Distance(LastPivot, point);

    private void ResetPivots()
    {
        _pivots.Clear();
        _pivots.Add(AnchorPoint);
        _pivotBaseLen = 0f;
    }

    private void UpdatePivots(Vector3 hip)
    {
        if (_pivots.Count == 0) ResetPivots();

        // UNWRAP: the pivot before the last sees the hip directly → the last bend is gone.
        while (_pivots.Count > 1)
        {
            if (RopeRayBlocked(_pivots[_pivots.Count - 2], hip, out _)) break;
            _pivotBaseLen -= Vector3.Distance(_pivots[_pivots.Count - 2], _pivots[_pivots.Count - 1]);
            _pivots.RemoveAt(_pivots.Count - 1);
        }
        if (_pivots.Count == 1) _pivotBaseLen = 0f;   // numeric drift guard

        // WRAP: the segment to the hip is blocked → bend at the obstruction (a few per frame max).
        for (int guard = 0; guard < 4 && _pivots.Count < MaxPivots; guard++)
        {
            Vector3 from = _pivots[_pivots.Count - 1];
            if (!RopeRayBlocked(from, hip, out RaycastHit hit)) break;
            Vector3 p = hit.point + hit.normal * pivotSkin;
            if ((p - from).sqrMagnitude < 0.02f * 0.02f) break;   // degenerate — retry next frame
            _pivots.Add(p);
            _pivotBaseLen += Vector3.Distance(from, p);
        }
    }

    /// <summary>Nearest blocking hit between two rope points (endpoints slightly shortened so a
    /// pivot sitting ON a surface / the hip near the body never self-hit; player + carbine ignored).</summary>
    private bool RopeRayBlocked(Vector3 from, Vector3 to, out RaycastHit hit)
    {
        hit = default;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.12f) return false;
        d /= len;

        int n = Physics.RaycastNonAlloc(new Ray(from + d * 0.02f, d), RayHits, len - 0.1f,
                                        ropeCollisionMask & ~(1 << gameObject.layer),
                                        QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        int bi = -1;
        for (int i = 0; i < n; i++)
        {
            Transform t = RayHits[i].transform;
            if (t.IsChildOf(transform.root)) continue;
            if (_carbine != null && t.IsChildOf(_carbine.transform)) continue;
            if (RayHits[i].distance < best) { best = RayHits[i].distance; bi = i; }
        }
        if (bi < 0) return false;
        hit = RayHits[bi];
        return true;
    }

    private void OnUsePressed(InputAction.CallbackContext ctx)
    {
        if (_hangingOnRope) { OnHangUsePressed(); return; }  // dangling after a yank — Use reattaches
        if (_climb == null || !_climb.IsClimbing) return;    // carbine interactions are otherwise climb-only
        TryPlaceCarbine();                                   // place, or replace when already tethered
    }

    /// <summary>Use while hanging on the rope: get back on the wall if the carbine's own surface still has
    /// a hold in reach, otherwise (next increment) take the rope itself. Ignored until the swing settles —
    /// and the GROUND case isn't ours at all: there the ragdoll's own stand-up owns the press.</summary>
    private void OnHangUsePressed()
    {
        if (!_hangSettled || _hangAwaitingUse) return;
        if (TryReattachToWall()) return;
        if (TryEnterRopeMode()) return;

        if (logEvents)
            Debug.Log("[CarbineController] Nothing to reattach to — no hold in reach and the rope handoff was refused.");
    }

    /// <summary>
    /// No wall in reach: take the ROPE itself. The player is already at the end of the tether (that is
    /// what caused the yank), so this is the rope-alone free hang the rappel system already implements —
    /// climb the tether up or down, re-contact a wall, top out. RappelController is deliberately free of
    /// any RopeAnchor/RopeController knowledge (it takes anchor + length as plain values), so the tether
    /// can drive it directly. The anchor handed over is the last WRAP PIVOT with the rope left past it,
    /// so a tether bent over a ledge measures honestly; CarbineController keeps it live as they descend.
    /// </summary>
    private bool TryEnterRopeMode()
    {
        if (_rappel == null || _ragdoll == null || _tether == null || _controlLock == null) return false;
        if (_rappel.IsRappelling) return false;
        Rigidbody pelvis = _ragdoll.PelvisBody;
        if (pelvis == null) return false;

        // Use the WALL SNAPSHOT taken when the carbine was placed rather than reading anything off the
        // limp hanging body: this rope belongs to a known wall, facing a known way.
        Vector3 wallNormal = _hasWallSnapshot ? _anchorWallNormal : Vector3.zero;
        if (wallNormal.sqrMagnitude < 1e-4f)
            wallNormal = Vector3.ProjectOnPlane(pelvis.position - AnchorPoint, Vector3.up);
        if (wallNormal.sqrMagnitude < 1e-4f) wallNormal = transform.forward;
        wallNormal.Normalize();

        // Hang on the line the wall's own standoff puts the body on, not straight down the rope — the
        // rope is anchored ON the wall, so dropping onto the raw anchor XZ would hang them inside it.
        float standoff = _rappel.bodyWallDistance;
        Vector3 hangPoint = (_hasWallSnapshot ? _anchorWallPoint : LastPivot) + wallNormal * standoff;

        var start = new RappelStart
        {
            WallPoint = hangPoint,     // only the XZ is read: the line the body hangs on
            WallNormal = wallNormal,
            AnchorPoint = LastPivot,
            RopeLength = Mathf.Max(0.5f, _tether.RopeLength - _pivotBaseLen),
            WallDistance = 0f          // 0 = the rappel's own standoff default
        };

        // Hand the body back facing the rock, then give control up for the moment the rappel claims it —
        // the same release-then-begin handoff the climb→rappel transition uses (both calls in one frame,
        // so no FSM tick can slip in between and take the body).
        DestroyPelvisJoint();   // the rope stops holding the hips before the bones go kinematic
        _ragdoll.RecoverInto(pelvis.position, Quaternion.LookRotation(-wallNormal, Vector3.up), reattachBlendTime);
        _controlLock.ReleaseExternalControl();

        if (!_rappel.BeginFreeHang(start))
        {
            // Control is already released, so the body simply finishes its blend and the motor takes
            // over — a normal fall, with the rope dropped rather than left in a half-state.
            Debug.LogWarning("[CarbineController] Rope-alone handoff refused — dropping the tether instead.");
            EndRopeHang(true);
            return false;
        }

        _ropeMode = true;
        // Route the rope the way a held rope actually runs: down from the carbine into the HANDS, past
        // the FOOT slot, and only then to the hip.
        SetTetherChain(_ropeHands, _ropeFeet, HipPoint);
        if (_ropeHands == null)
            Debug.LogWarning("[CarbineController] RopeController.ropeHoldPoint is unassigned — the tether will " +
                             "run straight to the hip instead of through the hands while climbing the rope.");
        EndRopeHang(false);                                 // the rope is still very much attached
        if (logEvents) Debug.Log("[CarbineController] Rope hang → rope-alone: climbing the tether.");
        return true;
    }

    /// <summary>The rope-alone rappel ended. Standing on top of the wall or at its foot means they are off
    /// the rope for good, so the tether goes; letting go mid-wall keeps it — that fall runs the rope out
    /// and gets yanked again, which is exactly what a tether is for.</summary>
    private void OnRappelExited(RappelExitKind kind, float extension)
    {
        if (!_ropeMode) return;
        _ropeMode = false;
        SetTetherChain(HipPoint);   // the hip carries the rope again
        if (kind != RappelExitKind.Detached && IsTethered)
        {
            if (logEvents) Debug.Log($"[CarbineController] Off the rope ({kind}) — tether dropped.");
            DetachRope();
        }
    }

    /// <summary>Routes the rope's player-side tail through the given slots (farthest from the carbine
    /// last) and keeps the measuring chain in step: the pay-out and the pivot chain must meter to where
    /// the rope actually MEETS the player — the hands in rope mode — not to where it ends.</summary>
    private void SetTetherChain(params Transform[] chain)
    {
        if (chain == null || chain.Length == 0) return;
        _tether?.SetEndChain(chain);

        // The first real slot is where the rope stops being free rope, so that is what the pay-out and
        // the pivot chain measure to.
        for (int i = 0; i < chain.Length; i++)
            if (chain[i] != null) { _tetherEnd = chain[i]; return; }
    }

    /// <summary>
    /// Nearest hold on the CARBINE'S OWN surface within <see cref="reattachReach"/> of a point — the rope
    /// is anchored to that surface, so it is by definition the climbable the player is roped to, and its
    /// holds and reachable set are already cached from the placement (no new probe). Holds outside the
    /// rope's range are skipped: grabbing one would put the climber where the hand-step gate immediately
    /// refuses to let them leave.
    /// </summary>
    private bool TryFindTetherHold(Vector3 center, out Vector3 holdPos, out Quaternion holdRot)
    {
        holdPos = Vector3.zero;
        holdRot = Quaternion.identity;
        if (_carbineSurface == null || !_carbineSurface.HoldsReady) return false;

        Transform st = _carbineSurface.transform;
        var holds = _carbineSurface.Holds;
        float best = reattachReach * reattachReach;
        int bestIndex = -1;
        for (int i = 0; i < holds.Count; i++)
        {
            if (_reachable != null && i < _reachable.Length && !_reachable[i]) continue;

            float d2 = (st.TransformPoint(holds[i].LocalPosition) - center).sqrMagnitude;
            if (d2 >= best) continue;
            best = d2;
            bestIndex = i;
        }
        if (bestIndex < 0) return false;

        holdPos = st.TransformPoint(holds[bestIndex].LocalPosition);
        holdRot = st.rotation * holds[bestIndex].LocalRotation;
        return true;
    }

    /// <summary>
    /// Rope-alone drifting within reach of the wall: take the WALL, as a climber, rather than letting the
    /// rappel brace into its wall-rappel pose. The player is roped to a climbable surface, so arriving at
    /// it should put them back on it — tethered, with the rope's range gate live again.
    /// </summary>
    private void TickRopeModeWallCatch()
    {
        if (_climb == null || _rappel == null || _climb.IsClimbing) return;
        if (!_rappel.IsRappelling || _motor == null) return;

        Vector3 chest = _motor.Transform.position + Vector3.up * reattachHeightOffset;
        if (!TryFindTetherHold(chest, out Vector3 holdPos, out Quaternion holdRot)) return;

        if (!_rappel.ReleaseForHandoff()) return;   // ends the rappel + returns control THIS frame
        // (OnRappelExited has already fired: rope mode is off and the rope is back on the hip.)

        if (!_climb.TryGrabAt(_carbineSurface, holdPos, holdRot))
        {
            // Control is already back with the motor, so this is just a fall — and still tethered, so
            // the yank catches it. Nothing to unwind.
            if (logEvents) Debug.Log("[CarbineController] Wall catch refused by the climb — falling on the rope.");
            return;
        }

        if (logEvents) Debug.Log("[CarbineController] Rope-alone reached the wall — braced climb, still tethered.");
    }

    /// <summary>
    /// Reattach to the wall out of the hanging ragdoll. The wall in question is the carbine's OWN surface
    /// (the rope is anchored to it, so it is by definition the climbable the player is roped to) — its
    /// holds are already gathered and its reachable set already cached from the placement, so this is a
    /// nearest-hold scan with no new probe. Sequence matters: the ragdoll must hand the body back
    /// (RecoverInto — placed, animatable, still under external control) BEFORE the climb grabs.
    /// </summary>
    private bool TryReattachToWall()
    {
        if (_climb == null || _ragdoll == null) return false;
        Rigidbody pelvis = _ragdoll.PelvisBody;
        if (pelvis == null) return false;
        if (!TryFindTetherHold(pelvis.position, out Vector3 holdPos, out Quaternion holdRot)) return false;

        Vector3 center = pelvis.position;

        // Face into the wall, as a grab from the ground would.
        Vector3 into = Vector3.ProjectOnPlane(-(holdRot * Vector3.forward), Vector3.up);
        Quaternion rootRot = into.sqrMagnitude > 1e-4f
            ? Quaternion.LookRotation(into.normalized, Vector3.up)
            : transform.rotation;

        // Hand the body back where it hangs (the climb repositions it under the holds itself) and keep
        // external control — then grab. The ragdoll pose blends out over the takeover instead of popping.
        DestroyPelvisJoint();   // the rope stops holding the hips before the bones go kinematic
        _ragdoll.RecoverInto(center, rootRot, reattachBlendTime);

        if (!_climb.TryGrabAt(_carbineSurface, holdPos, holdRot))
        {
            // Shouldn't happen (we validated the surface), but a body left under external control with
            // nobody driving it is unrecoverable — hand it back to the player.
            Debug.LogWarning("[CarbineController] Reattach grab refused — releasing control to the motor.");
            _controlLock?.ReleaseExternalControl();
            EndRopeHang(true);
            return false;
        }

        EndRopeHang(false);   // back on the wall STILL ROPED — the tether and its range gate carry on
        if (logEvents) Debug.Log("[CarbineController] Reattached to the wall from the rope hang.");
        return true;
    }

    // ------------------------------------------------------------------ placement

    private CarbineItem SelectedCarbineItem()
    {
        GameObject prefab = _items != null ? _items.currentPrefab : null;
        return prefab != null ? prefab.GetComponent<CarbineItem>() : null;
    }

    private void TryPlaceCarbine()
    {
        CarbineItem item = SelectedCarbineItem();
        if (item == null) return;   // carbine not selected — the press belongs to other systems

        if (item.placedPrefab == null)
        {
            Debug.LogWarning("[CarbineController] CarbineItem has no placedPrefab assigned.");
            return;
        }
        if (!_climb.TryGetRightHandHold(out Vector3 holdPos, out Quaternion holdRot))
            return;   // hand not settled on a hold (slide/slip/jump/deferred attach) — no placement point

        // Placing / REPLACING consumes one carbine. If the inventory is empty we do NOTHING — an
        // already-placed tether is kept rather than silently lost (dev's "keep current rope" choice).
        if (_items == null || !_items.TryConsumeCurrent())
            return;

        // Replace: with a carbine already out, drop the old rope first (it dissolves + destroys on its
        // own), then place the fresh one below and recompute the reachable set for the new anchor. The
        // carbine "climbs up" with the player as they re-place it higher.
        bool replacing = IsTethered;
        if (replacing) DetachRope();

        // WALL SNAPSHOT, taken here at placement — the same idea as the rope system storing a rappel entry
        // on its anchor. The carbine sits on a hold, and a hold's forward IS the outward wall normal, so
        // this is a clean record of "which wall this rope belongs to and which way it faces". Any later
        // dangle can then drop into rope-alone already aligned to the wall above, instead of having to
        // work it out from a limp body hanging in open air.
        _anchorWallPoint = holdPos;
        _anchorWallNormal = Vector3.ProjectOnPlane(holdRot * Vector3.forward, Vector3.up);
        if (_anchorWallNormal.sqrMagnitude < 1e-4f) _anchorWallNormal = -transform.forward;   // ceiling-ish hold
        _anchorWallNormal.Normalize();
        _hasWallSnapshot = true;

        _carbine = Instantiate(item.placedPrefab,
                               holdPos + (holdRot * Vector3.forward) * carbineSurfaceOffset, holdRot);
        _ropePoint = ResolveRopePoint(_carbine.transform, item.ropePointChildName);

        var tetherGO = new GameObject(simulation == TetherSim.VerletCollision
            ? "CarbineTether (verlet)" : "CarbineTether (physx)");
        _tether = simulation == TetherSim.VerletCollision
            ? tetherGO.AddComponent<VerletTetherRope>()
            : (TetherRopeBase)tetherGO.AddComponent<PhysicsTetherRope>();
        _tether.Init(_ropePoint, HipPoint, item.ropeLength, ropeMaterial,
                     ropeCollisionMask & ~(1 << gameObject.layer),
                     _carbine.transform, transform.root);
        _tetherEnd = HipPoint;
        ResetPivots();
        _tether.SetActiveLength(Vector3.Distance(AnchorPoint, HipPoint.position) + paidSlack);
        ComputeReachableSet();                              // geodesic reachable holds for this placement
        _climb.SetReachConstraint(AllowHandTargetIndex);   // no hand steps past the rope's range

        if (logEvents)
            Debug.Log($"[CarbineController] Carbine {(replacing ? "REPLACED" : "placed")} at the right hand's " +
                      $"hold ({item.ropeLength} m tether, {simulation}).", _carbine);
    }

    private static Transform ResolveRopePoint(Transform carbine, string childName)
    {
        if (!string.IsNullOrEmpty(childName))
        {
            Transform t = FindDeep(carbine, childName);
            if (t != null) return t;
        }
        return carbine;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>The rope's live player-side end: the hip slot normally, the hands in rope-alone mode.</summary>
    private Transform TetherEndPoint => _tetherEnd != null ? _tetherEnd : HipPoint;

    private Transform HipPoint
    {
        get
        {
            if (hipSlot != null) return hipSlot;
            Transform hips = _animator != null && _animator.isHuman
                ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            return hips != null ? hips : transform;
        }
    }

    // ------------------------------------------------------------------ detach + dissolve

    private void DetachRope()
    {
        if (_tether == null) return;
        // Losing the rope while it was carrying the body (a break, a scripted detach) must free the
        // pelvis first — false, or this would call straight back into here.
        if (_hangingOnRope) EndRopeHang(false);
        if (logEvents) Debug.Log("[CarbineController] Rope detached — dangling from the carbine, dissolving.");

        _tether.ReleaseHipEnd();
        StartCoroutine(DissolveAndDestroy(_carbine, _tether.gameObject));

        _carbine = null;
        _ropePoint = null;
        _tether = null;
        _tetherEnd = null;
        _ropeMode = false;
        _hasWallSnapshot = false;
        _pivots.Clear();
        _pivotBaseLen = 0f;
        _reachable = null;
        _carbineSurface = null;
        _yanking = false;
        _climb?.SetReachConstraint(null);   // full climbing range back
    }

    /// <summary>Drives the dissolve property 0→1 on every renderer material (carbine + rope) that
    /// exposes it, then destroys both objects. Materials are instanced by the access — fine, they
    /// die with the objects right after.</summary>
    private IEnumerator DissolveAndDestroy(GameObject carbine, GameObject rope)
    {
        var mats = new List<Material>();
        CollectDissolveMaterials(carbine, mats);
        CollectDissolveMaterials(rope, mats);
        if (mats.Count == 0 && logEvents)
            Debug.LogWarning($"[CarbineController] No material exposes '{dissolveProperty}' — destroying " +
                             "without a fade (check the property's exact shader reference name).");

        float t = 0f;
        float dur = Mathf.Max(0.05f, dissolveDuration);
        while (t < dur)
        {
            t += Time.deltaTime;
            float v = Mathf.Clamp01(t / dur);
            for (int i = 0; i < mats.Count; i++)
                if (mats[i] != null) mats[i].SetFloat(dissolveProperty, v);
            yield return null;
        }

        if (carbine != null) Destroy(carbine);
        if (rope != null) Destroy(rope);
    }

    private void CollectDissolveMaterials(GameObject root, List<Material> into)
    {
        if (root == null) return;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            Material m = r.material;
            if (m != null && m.HasProperty(dissolveProperty)) into.Add(m);
        }
    }

#if UNITY_EDITOR
    // ------------------------------------------------------------------ range debug (showRangeDebug)
    // Yellow lines = the pivot chain (the physical rope path used for pay-out + the off-wall clamp).
    // Green cubes = the cached geodesic reachable-hold set (holds within the rope's along-surface reach
    // from the carbine — the set a hand may still step to). Capped for dense bakes (~21k holds).
    private void OnDrawGizmos()
    {
        if (!showRangeDebug || !Application.isPlaying || !IsTethered) return;

        Gizmos.color = Color.yellow;
        for (int i = 0; i < _pivots.Count - 1; i++)
            Gizmos.DrawLine(_pivots[i], _pivots[i + 1]);

        if (_reachable == null || _carbineSurface == null || !_carbineSurface.HoldsReady) return;
        var holds = _carbineSurface.Holds;
        Transform st = _carbineSurface.transform;
        Gizmos.color = new Color(0.2f, 1f, 0.4f);
        int drawn = 0, count = Mathf.Min(holds.Count, _reachable.Length);
        for (int i = 0; i < count; i++)
        {
            if (!_reachable[i]) continue;
            Gizmos.DrawCube(st.TransformPoint(holds[i].LocalPosition), Vector3.one * 0.05f);
            if (++drawn >= 8000) break;
        }
    }
#endif
}
