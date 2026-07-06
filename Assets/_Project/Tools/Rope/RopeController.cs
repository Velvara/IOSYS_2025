using System.Collections;
using Game.Climbing;
using Game.PlayerV2;
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

    [Header("Holding (RopeHold state)")]
    [Tooltip("Walk speed cap while aim-placing or holding the rope (sprint and jump are blocked too).")]
    public float ropeWalkSpeed = 2f;
    [Tooltip("How fast the character turns to face the anchor while holding the rope.")]
    public float faceAnchorTurnSpeed = 10f;
    [Tooltip("Extra slack past ropeLength before the hard clamp kicks in (meters).")]
    public float clampSlack = 0.05f;
    [Tooltip("Max distance from a parked rope at which Use re-grabs it (nearest rope point).")]
    public float regrabDistance = 1.5f;

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

    [Header("Animator State Names")]
    public string ropeLayerName = "RopeLayer";
    public string placeStateName = "PlaceRope";
    public string holdPoseStateName = "HoldRopePose";

    public bool IsPlacing { get; private set; }
    public bool IsHolding => currentAnchor != null && currentAnchor.IsHeld;
    public RopeAnchor CurrentAnchor => currentAnchor;

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
    private RappelController rappel;
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

        rappel = GetComponentInParent<RappelController>();
        if (rappel != null)
        {
            rappel.OnRappelExited += HandleRappelExited;
            // Top-out: restore the hold pose while the climb layer still covers it, so the getup
            // fade reveals the correct pose (OnRappelExited only fires after the fade).
            rappel.OnTopOutSettling += EnterHoldPose;
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
        }
    }

    private void HandleRappelExited(RappelExitKind kind, float finalExtension)
    {
        CurrentRopeExtension = finalExtension;
        rappelBlockTimer = 0.5f;

        if (kind == RappelExitKind.Detached)
        {
            regrabBlockTimer = 0.4f;   // the detach press must not double as a re-grab
            DropRope();                // parks the rope end where the player let go; the player falls
            return;
        }

        // Back on top: the rope no longer runs over the edge — remove the ledge pivot. It stays
        // for SteppedOffBottom/Detached, where the rope really is still draped over the lip.
        if (kind == RappelExitKind.ToppedOut && currentAnchor != null)
            currentAnchor.ClearWaypoint();

        // ToppedOut / SteppedOffBottom: still holding — back to the RopeHold ground state.
        EnterHoldPose();
        if (motor != null) motor.RotateOnMove = false;
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
            rappel.OnTopOutSettling -= EnterHoldPose;
        }
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

        // Hand-off: control back, HoldRope pose + strafe locomotion in, full-body layer out.
        aimManager.characterStateManager?.UnlockCharacter();
        EnterHoldPose();
        if (motor != null) motor.RotateOnMove = false;

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

    /// <summary>Let go of the held rope; it persists in the world at its anchor.</summary>
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

    /// <summary>Use in locomotion near a parked rope: grab it at its nearest point.</summary>
    private void TryRegrabNearestRope()
    {
        if (regrabBlockTimer > 0f) return;
        if (motor == null || !motor.Controller.isGrounded) return;

        Vector3 pos = motor.Transform.position;
        RopeAnchor best = null;
        float bestDist = regrabDistance;
        float bestAlong = 0f;

        for (int i = 0; i < RopeAnchor.Placed.Count; i++)
        {
            RopeAnchor anchor = RopeAnchor.Placed[i];
            if (anchor == null || anchor.IsHeld) continue;

            Vector3 nearest = anchor.NearestPointOnRope(pos, out float along);
            float dist = Vector3.Distance(pos, nearest);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = anchor;
                bestAlong = along;
            }
        }

        if (best == null) return;

        currentAnchor = best;
        best.AttachTo(HoldPoint, ropeHoldPointSecondary);
        CurrentRopeExtension = bestAlong;   // grab length = arc distance to the grab point
        EnterHoldPose();
        if (motor != null) motor.RotateOnMove = false;
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
        if (controlLock != null && controlLock.IsExternalControlActive) return;

        if (IsHolding) DropRope();
        else TryRegrabNearestRope();
    }
}
