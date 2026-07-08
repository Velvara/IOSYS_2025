using System.Collections;
using Game.Climbing;
using Game.PlayerV2;
using Game.PlayerV2.Systems;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side rope state: runs the anchor placement sequence (full-body PlaceRope on the
/// RopeLayer with movement locked, anchor spawned mid-clip, item consumed) and the
/// subsequent holding state (HoldRope upper-body pose, rope attached to the player,
/// Use = drop). Movement rules while holding (facing the anchor, length clamp, re-grab)
/// arrive in the next increments.
/// </summary>
[RequireComponent(typeof(AimManager))]
public class RopeController : MonoBehaviour
{
    [Header("Placement")]
    [Tooltip("The world anchor spawned at the preview point. Must have a RopeAnchor component.")]
    public GameObject anchorPrefab;
    [Tooltip("Normalized time (0..1) into the PlaceRope clip at which the anchor object appears.")]
    [Range(0f, 1f)] public float anchorSpawnNormalizedTime = 0.5f;
    [Tooltip("Used if the PlaceRope clip length can't be read from the animator.")]
    public float fallbackPlaceDuration = 1.5f;
    [Tooltip("Fade time for the RopeLayer hand-off into the HoldRope pose at the end of placement.")]
    public float layerFadeDuration = 0.25f;

    [Header("Rope Attach")]
    [Tooltip("Where the rope connects to the player while holding. Falls back to AimManager.handSlot.")]
    public Transform ropeHoldPoint;
    [Tooltip("Optional second hand slot: when assigned, the rope runs anchor → hold point → over " +
             "this hand as a second segment.")]
    public Transform ropeHoldPointSecondary;
    [Tooltip("Optional extra rope hold BETWEEN THE FEET, used only in the RopeAlone free-hang pose: " +
             "the rope runs hands → this point → dangling tail, so it reads as passing between the " +
             "legs (abseil). Leave empty to disable.")]
    public Transform ropeFreeHangHold;
    [Tooltip("Toggle the between-feet free-hang hold without unassigning it.")]
    public bool useFreeHangHold = true;

    [Header("Holding (RopeHold state)")]
    [Tooltip("Walk speed cap while aim-placing or holding the rope (sprint and jump are blocked too).")]
    public float ropeWalkSpeed = 2f;
    [Tooltip("How fast the character turns to face the anchor while holding the rope.")]
    public float faceAnchorTurnSpeed = 10f;
    [Tooltip("Extra slack past ropeLength before the hard clamp kicks in (meters).")]
    public float clampSlack = 0.05f;
    [Tooltip("Max distance from a parked rope at which Use re-grabs it (nearest rope point).")]
    public float regrabDistance = 1.5f;
    [Tooltip("If a re-grabbed rope has no wall to rappel but its overhead attachment (ledge pivot / " +
             "anchor) sits at least this far above the player, the grab enters RopeAlone free-hang " +
             "instead of the ground RopeHold — i.e. grabbing a rope that dangles from above.")]
    public float freeHangGrabMinRise = 1f;
    [Tooltip("A grab that would START A RAPPEL is only allowed if the rope's ANCHOR (measured at the " +
             "player's height) lies within this horizontal radius of the player — otherwise the rope is " +
             "too far from the wall and grabbing would leave the player rappelling in mid-air. Out of " +
             "range = the grab is flagged invalid (see IsGrabInvalid) and refused. Pure distance, no raycast.")]
    public float grabAnchorHorizontalRadius = 0.75f;

    [Header("Ledge → Rappel")]
    [Tooltip("How far ahead of the feet the ledge probe looks while walking with the rope.")]
    public float ledgeProbeAhead = 0.45f;
    [Tooltip("No ground within this drop at the probe point = a ledge.")]
    public float ledgeDropHeight = 1.2f;
    [Tooltip("How far below the lip the wall probe looks for a rappel wall face.")]
    public float wallProbeDepth = 0.7f;
    [Tooltip("Surfaces for the ledge/wall probes (player's own layer is stripped automatically).")]
    public LayerMask rappelSurfaceMask = ~0;
    [Tooltip("Object placed on the ledge lip when a rappel starts; the rope routes anchor → this → hands. " +
             "Optional — empty routing point if none.")]
    public GameObject ledgePivotPrefab;
    [Tooltip("How far above the ledge surface the pivot sits.")]
    public float ledgePivotHeight = 0.2f;

    [Header("Anchor → Ledge")]
    [Tooltip("An anchor is only placeable if a rappel-able ledge (drop + wall face) lies within this " +
             "radius — and placement transitions straight into the wall rappel at that ledge.")]
    public float anchorLedgeSearchRadius = 1.5f;
    [Tooltip("Radial directions sampled when searching for the nearest ledge around a point.")]
    public int ledgeSearchDirections = 12;

    [Header("Animator State Names")]
    public string ropeLayerName = "RopeLayer";
    public string placeStateName = "PlaceRope";
    public string holdPoseStateName = "HoldRopePose";

    public bool IsPlacing { get; private set; }
    public bool IsHolding => currentAnchor != null && currentAnchor.IsHeld;
    public RopeAnchor CurrentAnchor => currentAnchor;

    /// <summary>True when the last grab attempt was REFUSED because the rope's anchor was outside
    /// <see cref="grabAnchorHorizontalRadius"/> (too far from the wall — grabbing would rappel in
    /// mid-air). Cleared on the next valid grab. Exposed for a future HUD "invalid grab" indicator.</summary>
    public bool IsGrabInvalid { get; private set; }

    /// <summary>
    /// Rope paid out from the anchor to the player's grip: straight distance at placement,
    /// arc length at the grab point on a re-grab. The rappel increment extends this as the
    /// player descends, up to RopeAnchor.RopeLength.
    /// </summary>
    public float CurrentRopeExtension { get; private set; }

    private static readonly int IsRopeHoldingHash = Animator.StringToHash("IsRopeHolding");
    private static readonly int AimMoveXHash = Animator.StringToHash("AimMoveX");
    private static readonly int AimMoveYHash = Animator.StringToHash("AimMoveY");

    private AimManager aimManager;
    private Animator animator;
    private PlayerInput playerInput;
    private RopeAnchor currentAnchor;
    private IPlayerMotor motor;
    private IControlLock controlLock;
    private InputHandler input;
    private PlayerStamina stamina;   // grabbing a rope is refused while fatigued
    private RappelController rappel;
    private ClimbController climb;   // Use-near-a-rope while climbing hands the body off into rappel
    private bool wasAiming;
    private bool restrictionActive;
    private Vector3 worldMoveDir;   // set by WriteStrafeParams, read by the ledge probe
    private float regrabBlockTimer;   // brief block after a rappel detach so the same Use press can't re-grab
    private float rappelBlockTimer;   // brief block after any rappel exit so it can't re-trigger the same instant

    private void Awake()
    {
        aimManager = GetComponent<AimManager>();
        animator = GetComponent<Animator>();
        // Not read from aimManager.playerInput: AimManager fills that in its own Start,
        // which may run after ours. Same GameObject, same PlayerInput either way.
        playerInput = GetComponent<PlayerInput>();
    }

    private void Start()
    {
        motor = GetComponentInParent<IPlayerMotor>();
        controlLock = GetComponentInParent<IControlLock>();
        input = GetComponentInParent<InputHandler>();
        stamina = GetComponentInParent<PlayerStamina>();

        climb = GetComponentInParent<ClimbController>();

        rappel = GetComponentInParent<RappelController>();
        if (rappel != null)
        {
            rappel.OnRappelExited += HandleRappelExited;
            // Top-out exits to BASIC LOCOMOTION (rope dropped) — restore the idle upper body
            // while the climb layer still covers it, so the getup fade reveals the right pose.
            rappel.OnTopOutSettling += HandleTopOutSettling;
            // Route the rope through the between-feet hold only while free-hanging (RopeAlone).
            rappel.OnFreeHangChanged += HandleFreeHangChanged;
        }

        // Persistent subscription (unlike aim modes, holding isn't entered via EnterMode);
        // HandleUse guards decide when a press means "drop" or "re-grab".
        var actions = playerInput != null ? playerInput.actions : null;
        if (actions != null && actions["Use"] != null)
            actions["Use"].performed += HandleUse;
    }

    private void Update()
    {
        if (regrabBlockTimer > 0f) regrabBlockTimer -= Time.deltaTime;
        if (rappelBlockTimer > 0f) rappelBlockTimer -= Time.deltaTime;
        UpdateMovementRestriction();

        if (!IsHolding || IsPlacing) return;

        bool aiming = aimManager != null && aimManager.IsAiming;

        // Entering any NON-rope aim mode cancels the hold — the rope is dropped (persists in
        // the world). Aiming the rope item itself is the "place another rope" flow and keeps
        // the hold until placement actually starts.
        if (aiming && !(aimManager.ActiveMode is RopeAim))
        {
            DropRope();
            return;
        }

        // A rope-aim session while holding stomps the hold pose (aim modes own the upper-body
        // layer and exit to UpperBodyIdle) — re-assert it the frame the aim ends.
        if (wasAiming && !aiming)
            EnterHoldPose();
        wasAiming = aiming;

        if (aiming) return;
        if (controlLock != null && controlLock.IsExternalControlActive) return;

        if (motor != null)
        {
            motor.RotateOnMove = false;   // rope owns facing; idempotent, re-asserted after aim sessions
            FaceAnchor();
        }
        WriteStrafeParams();
        CheckLedgeForRappel();
    }

    /// <summary>Result of a radial ledge search around a point (anchor/preview).</summary>
    public struct LedgeHit
    {
        public Vector3 LipPoint;     // on the top surface, at the edge
        public Vector3 WallPoint;    // first wall contact below the lip
        public Vector3 WallNormal;
        public float Distance;       // horizontal distance from the search origin
    }

    /// <summary>
    /// Searches radially around <paramref name="origin"/> (a point on the ground) for the nearest
    /// ledge whose face below supports a wall rappel. Used to validate anchor placement and to
    /// pick where the post-placement rappel enters. ~O(directions × steps) raycasts — callers
    /// cache the result while the query point hasn't moved.
    /// </summary>
    public bool FindNearestRappelLedge(Vector3 origin, out LedgeHit result)
    {
        result = default;
        int mask = rappelSurfaceMask & ~(1 << (motor != null ? motor.Transform.gameObject.layer : 31));
        const float step = 0.3f;
        int steps = Mathf.Max(1, Mathf.CeilToInt(anchorLedgeSearchRadius / step));
        int directions = Mathf.Max(4, ledgeSearchDirections);

        bool found = false;
        float bestDist = float.MaxValue;

        for (int d = 0; d < directions; d++)
        {
            Vector3 dir = Quaternion.Euler(0f, d * (360f / directions), 0f) * Vector3.forward;

            for (int s = 1; s <= steps; s++)
            {
                float outDist = s * step;
                Vector3 sample = origin + dir * outDist;

                // Ground still continues here → march on.
                if (Physics.Raycast(sample + Vector3.up * 0.3f, Vector3.down, 0.3f + ledgeDropHeight,
                                    mask, QueryTriggerInteraction.Ignore))
                    continue;

                // Ledge crossed in this direction — is there a wall face below the lip?
                Vector3 wallProbeOrigin = sample + dir * 0.15f + Vector3.down * wallProbeDepth;
                if (Physics.Raycast(wallProbeOrigin, -dir, out RaycastHit wall, outDist + 0.35f,
                                    mask, QueryTriggerInteraction.Ignore) &&
                    Vector3.Angle(wall.normal, Vector3.up) >= 55f)
                {
                    Vector3 lip = new Vector3(wall.point.x, origin.y, wall.point.z);
                    float dist = Vector3.Distance(origin, lip);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        result.LipPoint = lip;
                        result.WallPoint = wall.point;
                        result.WallNormal = wall.normal;
                        result.Distance = dist;
                        found = true;
                    }
                }
                break;   // this direction is resolved (ledge found or rejected)
            }
        }
        return found;
    }

    /// <summary>
    /// Walking (roped) toward an edge where the next step would fall: if a wall face exists
    /// below the lip, hand the body to the rappel controller. No wall = no rappel — the edge
    /// behaves normally.
    /// </summary>
    private void CheckLedgeForRappel()
    {
        if (rappel == null || rappel.IsRappelling || motor == null || currentAnchor == null) return;
        if (rappelBlockTimer > 0f) return;
        if (worldMoveDir.sqrMagnitude < 0.1f || !motor.Controller.isGrounded) return;

        int mask = rappelSurfaceMask & ~(1 << motor.Transform.gameObject.layer);
        Vector3 dir = worldMoveDir.normalized;
        Vector3 feet = motor.Transform.position;

        // Ground continues at the next step → no ledge.
        Vector3 ahead = feet + dir * ledgeProbeAhead + Vector3.up * 0.3f;
        if (Physics.Raycast(ahead, Vector3.down, 0.3f + ledgeDropHeight, mask, QueryTriggerInteraction.Ignore))
            return;

        // Ledge ahead — look for a wall face below the lip by casting back toward the player.
        Vector3 wallProbeOrigin = feet + dir * (ledgeProbeAhead + 0.15f) + Vector3.down * wallProbeDepth;
        if (!Physics.Raycast(wallProbeOrigin, -dir, out RaycastHit wall,
                             ledgeProbeAhead + 0.35f, mask, QueryTriggerInteraction.Ignore))
            return;
        if (Vector3.Angle(wall.normal, Vector3.up) < 55f) return;   // slope, not a wall face

        bool started = rappel.BeginRappel(new RappelStart
        {
            WallPoint = wall.point,
            WallNormal = wall.normal,
            AnchorPoint = currentAnchor.StartPoint,
            RopeLength = currentAnchor.RopeLength
        });

        if (started)
        {
            // Ledge pivot on the lip: the wall hit gives the edge's horizontal position, the
            // player's feet the top-surface height. Rope now runs anchor → pivot → hands.
            Vector3 lip = new Vector3(wall.point.x, feet.y, wall.point.z) + Vector3.up * ledgePivotHeight;
            currentAnchor.SetWaypoint(lip, ledgePivotPrefab);
            // Save the wall point, orientation AND standoff so a later re-grab rappels identically.
            currentAnchor.SetRappelEntry(wall.point, wall.normal, rappel.bodyWallDistance);
            currentAnchor.SetOnWall(true);                            // on the wall now — reveal the tail
        }
    }

    private void HandleRappelExited(RappelExitKind kind, float finalExtension)
    {
        CurrentRopeExtension = finalExtension;
        rappelBlockTimer = 0.5f;

        if (kind == RappelExitKind.Detached)
        {
            regrabBlockTimer = 0.4f;   // the detach press must not double as a re-grab
            DropRope();                // the player let go mid-wall and falls; the tail hangs from the pivot
            return;
        }

        // Topped out OR reached the ground: either way the rope is dropped and the player returns
        // to basic locomotion. The whole below-pivot length becomes the organic tail (draped down
        // the wall from the ledge pivot). A brief re-grab block so the same exit can't re-grab.
        regrabBlockTimer = 0.4f;
        DropRope();
    }

    private void LateUpdate()
    {
        // Hard clamp at rope's end — after the motor has moved for this frame.
        if (!IsHolding || IsPlacing || motor == null) return;
        if (controlLock != null && controlLock.IsExternalControlActive) return;

        Vector3 anchor = currentAnchor.StartPoint;
        Vector3 offset = motor.Transform.position - anchor;
        float maxLen = currentAnchor.RopeLength + clampSlack;
        if (offset.sqrMagnitude > maxLen * maxLen)
        {
            Vector3 clamped = anchor + offset * (maxLen / offset.magnitude);
            motor.Controller.Move(clamped - motor.Transform.position);
        }
    }

    private void FaceAnchor()
    {
        Vector3 toAnchor = currentAnchor.StartPoint - motor.Transform.position;
        toAnchor.y = 0f;
        if (toAnchor.sqrMagnitude < 0.01f) return;

        Quaternion target = Quaternion.LookRotation(toAnchor);
        motor.Transform.rotation = Quaternion.Slerp(
            motor.Transform.rotation, target, Time.deltaTime * faceAnchorTurnSpeed);
    }

    /// <summary>
    /// Strafe blend relative to the CHARACTER's facing (the anchor), not the camera — unlike
    /// aim modes, where the two coincide because the character faces the camera forward.
    /// </summary>
    private void WriteStrafeParams()
    {
        if (animator == null) return;

        Vector2 moveInput = input != null ? input.MoveInput : Vector2.zero;
        Transform cam = aimManager != null ? aimManager.CameraTransform : null;

        Vector3 world = Vector3.zero;
        if (cam != null && moveInput.sqrMagnitude > 0.0001f)
        {
            Vector3 fwd = cam.forward; fwd.y = 0f; fwd.Normalize();
            Vector3 right = cam.right; right.y = 0f; right.Normalize();
            world = fwd * moveInput.y + right * moveInput.x;
        }
        worldMoveDir = world;

        Vector3 local = motor != null ? motor.Transform.InverseTransformDirection(world) : world;
        animator.SetFloat(AimMoveXHash, local.x);
        animator.SetFloat(AimMoveYHash, local.z);
    }

    /// <summary>
    /// Slower walk + no sprint/jump while the rope is aim-placed or held. Polled so every
    /// enter/exit path (aim start/abort, placement, drop, re-grab, cancel-by-other-aim)
    /// converges on the same single writer.
    /// </summary>
    private void UpdateMovementRestriction()
    {
        if (motor == null) return;

        bool ropeAiming = aimManager != null && aimManager.IsAiming && aimManager.ActiveMode is RopeAim;
        bool shouldRestrict = ropeAiming || IsHolding;
        if (shouldRestrict == restrictionActive) return;

        restrictionActive = shouldRestrict;
        if (shouldRestrict) motor.SetMovementRestriction(ropeWalkSpeed, blockSprint: true, blockJump: true);
        else motor.ClearMovementRestriction();
    }

    private void EnterHoldPose()
    {
        if (animator == null) return;

        animator.SetBool(IsRopeHoldingHash, true);
        int upper = animator.GetLayerIndex("AimingUpperBody");
        // FIXED time, not CrossFade: normalized durations scale with the SOURCE state's length,
        // and the rappel/rope-alone blend trees contain a near-zero-speed idle child that makes
        // their state length enormous — a normalized 0.2 out of them runs for minutes and shows
        // as a half-blended "stuck" rappel pose.
        if (upper >= 0) animator.CrossFadeInFixedTime(holdPoseStateName, 0.2f, upper);
    }

    private void OnDestroy()
    {
        var actions = playerInput != null ? playerInput.actions : null;
        if (actions != null && actions["Use"] != null)
            actions["Use"].performed -= HandleUse;

        if (rappel != null)
        {
            rappel.OnRappelExited -= HandleRappelExited;
            rappel.OnTopOutSettling -= HandleTopOutSettling;
            rappel.OnFreeHangChanged -= HandleFreeHangChanged;
        }
    }

    /// <summary>The rappel controller entered/left the RopeAlone free-hang pose: route the held rope
    /// through the between-feet hold while hanging (so the remainder dangles between the legs,
    /// abseil-style), straight from the hands otherwise.</summary>
    private void HandleFreeHangChanged(bool active)
    {
        if (currentAnchor == null) return;
        bool on = active && useFreeHangHold && ropeFreeHangHold != null;
        currentAnchor.SetFreeHangHold(on ? ropeFreeHangHold : null);

        // Capture the hang line the FIRST time this rope free-hangs (the natural wall→rope-alone
        // transition, or a ground grab). A later below-grab replays it so the hang distance matches.
        if (active && rappel != null && !currentAnchor.HasFreeHangEntry)
            currentAnchor.SetFreeHangEntry(rappel.FreeHangPoint, rappel.FreeHangNormal, rappel.FreeHangDistance);
    }

    /// <summary>Under the still-opaque climb layer at the end of a top-out: prep plain
    /// locomotion (the rope is dropped when the rappel reports its exit).</summary>
    private void HandleTopOutSettling()
    {
        if (animator == null) return;

        animator.SetBool(IsRopeHoldingHash, false);
        animator.SetFloat(AimMoveXHash, 0f);
        animator.SetFloat(AimMoveYHash, 0f);
        int upper = animator.GetLayerIndex("AimingUpperBody");
        if (upper >= 0) animator.CrossFadeInFixedTime("UpperBodyIdle", 0.2f, upper);
    }

    /// <summary>Kicks off the placement sequence. Called by RopeAim on a valid Use press.</summary>
    public void BeginPlacement(Vector3 point, Quaternion rotation, float ropeLength)
    {
        if (IsPlacing) return;
        StartCoroutine(PlacementRoutine(point, rotation, ropeLength));
    }

    private IEnumerator PlacementRoutine(Vector3 point, Quaternion rotation, float ropeLength)
    {
        IsPlacing = true;

        // Only one rope in hand at a time — placing a new one lets go of the old (it persists).
        if (IsHolding) DropRope();

        // Lock movement for the full-body clip. AimManager sees the external control and
        // cancels the aim itself (preview + held item cleaned up by RopeAim.ExitMode).
        aimManager.characterStateManager?.LockCharacter();

        // Consume only after AimManager.Update has cancelled the aim: when the last rope is
        // consumed, CycleItems auto-selects the next item and fires OnItemChanged — with the
        // aim still active that would EnterMode the next item's aim mode for one frame.
        yield return null;
        aimManager.cycleItems?.TryConsumeCurrent();

        int ropeLayer = animator != null ? animator.GetLayerIndex(ropeLayerName) : -1;
        if (ropeLayer >= 0)
        {
            animator.SetLayerWeight(ropeLayer, 1f);
            animator.CrossFade(placeStateName, 0.1f, ropeLayer, 0f);
        }

        // State info is valid one frame after the crossfade starts.
        yield return null;
        float duration = fallbackPlaceDuration;
        if (ropeLayer >= 0)
        {
            var info = animator.IsInTransition(ropeLayer)
                ? animator.GetNextAnimatorStateInfo(ropeLayer)
                : animator.GetCurrentAnimatorStateInfo(ropeLayer);
            if (info.length > 0.05f && info.length < 20f)
                duration = info.length;
        }

        float spawnAt = duration * anchorSpawnNormalizedTime;
        float elapsed = 0f;
        bool spawned = false;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            if (!spawned && elapsed >= spawnAt)
            {
                SpawnAnchor(point, rotation, ropeLength);
                spawned = true;
            }
            yield return null;
        }
        if (!spawned) SpawnAnchor(point, rotation, ropeLength);

        // Hand-off: control back, then STRAIGHT into the wall rappel at the ledge nearest the
        // anchor (placement was only valid near one) — RopeHold is skipped on entry. The rappel's
        // enter blend keeps control locked while the body settles onto the wall, then returns it.
        aimManager.characterStateManager?.UnlockCharacter();

        bool rappelStarted = false;
        if (currentAnchor != null && rappel != null &&
            FindNearestRappelLedge(currentAnchor.StartPoint, out LedgeHit ledge))
        {
            currentAnchor.SetWaypoint(ledge.LipPoint + Vector3.up * ledgePivotHeight, ledgePivotPrefab);
            // Save wall point, orientation AND standoff so a later re-grab rappels identically.
            currentAnchor.SetRappelEntry(ledge.WallPoint, ledge.WallNormal, rappel.bodyWallDistance);
            rappelStarted = rappel.BeginRappel(new RappelStart
            {
                WallPoint = ledge.WallPoint,
                WallNormal = ledge.WallNormal,
                AnchorPoint = currentAnchor.StartPoint,
                RopeLength = currentAnchor.RopeLength
            });
        }

        if (currentAnchor != null) currentAnchor.SetOnWall(rappelStarted);   // tail shows only once on the wall
        if (!rappelStarted)
        {
            // Safety net (ledge vanished between aim-validation and now): classic RopeHold.
            EnterHoldPose();
            if (motor != null) motor.RotateOnMove = false;
        }

        if (ropeLayer >= 0)
        {
            float fade = 0f;
            while (fade < layerFadeDuration)
            {
                fade += Time.deltaTime;
                animator.SetLayerWeight(ropeLayer, 1f - Mathf.Clamp01(fade / layerFadeDuration));
                yield return null;
            }
            animator.SetLayerWeight(ropeLayer, 0f);
        }

        IsPlacing = false;
    }

    private void SpawnAnchor(Vector3 point, Quaternion rotation, float ropeLength)
    {
        if (anchorPrefab == null)
        {
            Debug.LogError("RopeController: no anchorPrefab assigned.");
            return;
        }

        var anchorObj = Instantiate(anchorPrefab, point, rotation);
        currentAnchor = anchorObj.GetComponent<RopeAnchor>();
        if (currentAnchor == null)
        {
            Debug.LogError("RopeController: anchorPrefab has no RopeAnchor component.");
            return;
        }

        currentAnchor.Init(ropeLength);
        currentAnchor.AttachTo(HoldPoint, ropeHoldPointSecondary);
        CurrentRopeExtension = motor != null
            ? Vector3.Distance(currentAnchor.StartPoint, motor.Transform.position)
            : 0f;
    }

    /// <summary>Let go of the held rope; it persists in the world at its anchor. The whole
    /// below-pivot length becomes the organic free tail (RopeAnchor.Detach), which falls and
    /// settles on its own.</summary>
    public void DropRope()
    {
        if (currentAnchor == null) return;

        currentAnchor.Detach();
        currentAnchor = null;
        CurrentRopeExtension = 0f;

        bool aiming = aimManager != null && aimManager.IsAiming;
        if (animator != null)
        {
            animator.SetBool(IsRopeHoldingHash, false);
            if (!aiming)   // while aiming, the active aim mode owns these
            {
                animator.SetFloat(AimMoveXHash, 0f);
                animator.SetFloat(AimMoveYHash, 0f);
                int upper = animator.GetLayerIndex("AimingUpperBody");
                // Fixed time — see EnterHoldPose (slowed blend-tree source states stall normalized fades).
                if (upper >= 0) animator.CrossFadeInFixedTime("UpperBodyIdle", 0.2f, upper);
            }
        }
        if (motor != null && !aiming) motor.RotateOnMove = true;
    }

    /// <summary>Use in locomotion near a parked rope: grab it at its nearest point → wall rappel, free-hang,
    /// or (a ground-level rope with nothing overhead) the walk-around RopeHold.</summary>
    private void TryRegrabNearestRope()
    {
        if (regrabBlockTimer > 0f) return;
        if (stamina != null && stamina.IsFatigued) return;   // too tired to grab a rope
        if (motor == null || !motor.Controller.isGrounded) return;

        RopeAnchor best = FindNearestRope(motor.Transform.position, out float along, out Vector3 point);
        if (best == null) return;

        // Decide the rappel from geometry BEFORE attaching, so an invalid grab is refused cleanly.
        RappelStart start = default;
        bool freeHang = false;
        bool rappelApplies = rappel != null && ComputeRappelStart(best, point, out start, out freeHang);

        // A rappel grab is only valid if the anchor is roughly overhead — else the rope is too far
        // from the wall and grabbing would rappel in mid-air. Flag invalid and refuse (RopeHold grabs
        // of a ground-level rope are unaffected — no rappel applies there).
        if (rappelApplies && !AnchorWithinGrabRadius(best))
        {
            IsGrabInvalid = true;
            return;
        }
        IsGrabInvalid = false;

        currentAnchor = best;
        best.AttachTo(HoldPoint, ropeHoldPointSecondary);
        CurrentRopeExtension = along;   // grab length = arc distance to the grab point

        if (rappelApplies && StartRappel(start, freeHang))
        {
            best.SetOnWall(true);   // on the wall / hanging — reveal the organic tail
            return;
        }

        EnterHoldPose();
        best.SetOnWall(false);   // grabbed onto flat ground / no wall — no dangling tail while held
        if (motor != null) motor.RotateOnMove = false;
    }

    /// <summary>The rope's anchor, measured at the player's height, lies within
    /// <see cref="grabAnchorHorizontalRadius"/> horizontally — i.e. the rope hangs close enough to the
    /// player to grab into a rappel without swinging them into mid-air. Pure distance, no physics.</summary>
    private bool AnchorWithinGrabRadius(RopeAnchor anchor)
    {
        if (motor == null) return true;
        Vector3 a = anchor.StartPoint;
        Vector3 p = motor.Transform.position;
        float dx = a.x - p.x;
        float dz = a.z - p.z;
        return dx * dx + dz * dz <= grabAnchorHorizontalRadius * grabAnchorHorizontalRadius;
    }

    /// <summary>Nearest un-held parked rope within regrabDistance of <paramref name="pos"/>, plus the
    /// nearest world point on it and the arc length to that point.</summary>
    private RopeAnchor FindNearestRope(Vector3 pos, out float along, out Vector3 point)
    {
        along = 0f;
        point = pos;
        RopeAnchor best = null;
        float bestDist = regrabDistance;

        for (int i = 0; i < RopeAnchor.Placed.Count; i++)
        {
            RopeAnchor anchor = RopeAnchor.Placed[i];
            if (anchor == null || anchor.IsHeld) continue;

            Vector3 nearest = anchor.NearestPointOnRope(pos, out float a);
            float dist = Vector3.Distance(pos, nearest);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = anchor;
                along = a;
                point = nearest;
            }
        }
        return best;
    }

    /// <summary>
    /// Decides how grabbing <paramref name="best"/> at <paramref name="grabPoint"/> enters a rappel — pure
    /// geometry, no takeover (so a caller can validate before dropping a climb). Returns false when neither
    /// a wall rappel nor a free-hang applies (a ground-level rope → the caller uses RopeHold).
    ///   • Wall rappel: TOP side replays the lip entry; below the lip probes the wall at the grab point.
    ///   • Free-hang: no wall, but the overhead attachment sits above the player → hang on the rope.
    /// </summary>
    private bool ComputeRappelStart(RopeAnchor best, Vector3 grabPoint, out RappelStart start, out bool freeHang)
    {
        start = default;
        freeHang = false;
        if (motor == null) return false;

        if (best.HasRappelEntry)
        {
            bool topSide = best.WaypointPosition is Vector3 lip &&
                           motor.Transform.position.y >= lip.y - 0.75f;

            bool haveEntry;
            Vector3 wallPoint, wallNormal;
            if (topSide)
            {
                wallPoint = best.RappelWallPoint;
                wallNormal = best.RappelWallNormal;
                haveEntry = true;
            }
            else
            {
                // Fresh wall POINT at the grab height, but reuse the SAVED orientation so the re-grab
                // rappels at the same facing the rope was placed with (dev request).
                haveEntry = ResolveWallAtGrab(grabPoint, best.RappelWallNormal, out wallPoint, out _);
                wallNormal = best.RappelWallNormal;
            }

            if (haveEntry)
            {
                start = new RappelStart
                {
                    WallPoint = wallPoint,
                    WallNormal = wallNormal,
                    AnchorPoint = best.StartPoint,
                    RopeLength = best.RopeLength,
                    WallDistance = best.RappelBodyDistance   // rappel at the saved standoff
                };
                freeHang = false;
                return true;
            }
        }

        // No wall — but if the rope hangs from above (overhead pivot/anchor higher than the player),
        // grab it and free-hang. Reeling up can still re-contact a wall and resume wall rappel.
        Vector3 overhead = best.WaypointPosition is Vector3 pivot ? pivot : best.StartPoint;
        if (overhead.y > motor.Transform.position.y + freeHangGrabMinRise)
        {
            if (best.HasFreeHangEntry)
            {
                // Replay the stored hang line so the body hangs at the SAME distance from the wall as the
                // original wall→rope-alone transition (BeginFreeHang keeps the current Y, uses the XZ).
                start = new RappelStart
                {
                    WallPoint = best.FreeHangPoint,
                    WallNormal = best.FreeHangNormal,
                    AnchorPoint = best.StartPoint,
                    RopeLength = best.RopeLength,
                    WallDistance = best.FreeHangDistance
                };
            }
            else
            {
                Vector3 hangNormal;
                if (best.HasRappelEntry)
                {
                    hangNormal = best.RappelWallNormal;   // real wall orientation, so a reel-up re-contacts it
                }
                else
                {
                    Vector3 toAnchor = best.StartPoint - motor.Transform.position;
                    toAnchor.y = 0f;
                    hangNormal = toAnchor.sqrMagnitude > 0.01f ? -toAnchor.normalized : motor.Transform.forward;
                }

                start = new RappelStart
                {
                    WallPoint = grabPoint,        // grab point on the rope = the vertical hang line
                    WallNormal = hangNormal,
                    AnchorPoint = best.StartPoint,
                    RopeLength = best.RopeLength,
                    WallDistance = best.RappelBodyDistance   // used if a reel-up re-contacts the wall
                };
            }
            freeHang = true;
            return true;
        }

        return false;
    }

    /// <summary>Executes the rappel decided by <see cref="ComputeRappelStart"/>. Requires external control
    /// to be free (the caller releases any climb first).</summary>
    private bool StartRappel(in RappelStart start, bool freeHang) =>
        freeHang ? rappel.BeginFreeHang(start) : rappel.BeginRappel(start);

    /// <summary>Resolves the wall face for a below-the-lip re-grab. The grab point sits on the tail,
    /// which drapes against the wall, so we probe just off it (using the rope's stored normal for
    /// orientation) and cast back into the face. Returns false when there's genuinely no wall there —
    /// the caller then falls through to a free-hang grab (a rope dangling in open air).</summary>
    private bool ResolveWallAtGrab(Vector3 grabPoint, Vector3 storedWallNormal, out Vector3 wallPoint, out Vector3 wallNormal)
    {
        wallPoint = default;
        wallNormal = default;

        Vector3 n = storedWallNormal.sqrMagnitude > 0.0001f ? storedWallNormal.normalized : Vector3.forward;
        int mask = rappelSurfaceMask & ~(1 << motor.Transform.gameObject.layer);

        Vector3 origin = grabPoint + n * 0.4f;   // just off the face, casting back into it
        if (Physics.Raycast(origin, -n, out RaycastHit hit, 0.9f, mask, QueryTriggerInteraction.Ignore) &&
            Vector3.Angle(hit.normal, Vector3.up) >= 55f)
        {
            wallPoint = hit.point;
            wallNormal = hit.normal;
            return true;
        }
        return false;
    }

    private Transform HoldPoint =>
        ropeHoldPoint != null
            ? ropeHoldPoint
            : (aimManager != null && aimManager.handSlot != null ? aimManager.handSlot.transform : transform);

    private void HandleUse(InputAction.CallbackContext ctx)
    {
        // While aiming, Use belongs to the active aim mode (throw/place/fire).
        if (IsPlacing) return;
        if (aimManager != null && aimManager.IsAiming) return;

        if (controlLock != null && controlLock.IsExternalControlActive)
        {
            // One exception to the external-control block: while CLIMBING, Use near a rope hands the
            // body off from the climb into rappel (wall or free-hang). Any other external control
            // (rappel itself, cutscene) still owns Use.
            if (climb != null && climb.IsClimbing && !IsHolding)
                TryGrabRopeFromClimb();
            return;
        }

        if (IsHolding) DropRope();
        else TryRegrabNearestRope();
    }

    /// <summary>Climbing a wall and pressing Use near a rope: if a rappel (wall or free-hang) would
    /// apply on that rope, release the climb and take the body over into it. If no rope is near or
    /// none applies, the climb is left untouched (we don't drop the player for nothing).</summary>
    private void TryGrabRopeFromClimb()
    {
        if (rappel == null || motor == null) return;
        if (stamina != null && stamina.IsFatigued) return;   // too tired to grab a rope

        RopeAnchor best = FindNearestRope(motor.Transform.position, out float along, out Vector3 point);
        if (best == null) return;

        // Decide the rappel FIRST (pure geometry, no takeover) so we only drop the climb if it applies.
        if (!ComputeRappelStart(best, point, out RappelStart start, out bool freeHang)) return;

        // Too far from the wall to grab into a rappel → flag invalid and stay climbing (no mid-air rappel).
        if (!AnchorWithinGrabRadius(best))
        {
            IsGrabInvalid = true;
            return;
        }
        IsGrabInvalid = false;

        // Hand off: release the climb (control returned this frame) so the rappel can take it, then start.
        if (!climb.ReleaseForHandoff()) return;

        currentAnchor = best;
        best.AttachTo(HoldPoint, ropeHoldPointSecondary);
        CurrentRopeExtension = along;

        if (StartRappel(start, freeHang))
        {
            best.SetOnWall(true);
            return;
        }

        // Rappel refused after the release (shouldn't happen — geometry already validated): fall back to
        // a hold rather than leaving the player in limbo.
        EnterHoldPose();
        best.SetOnWall(false);
        if (motor != null) motor.RotateOnMove = false;
    }
}
