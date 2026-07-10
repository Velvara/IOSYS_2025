using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Aim mode for the rope anchor item. While aiming, shows a placement preview ahead of
/// the player dropped onto the ground, tinted valid/invalid via the MeshPreview shader's
/// _Pulse_Color. Placement itself (PlaceRope animation + anchor spawn) is handled in the
/// next increment; this mode only owns framing, the held item, and the preview.
/// </summary>
[RequireComponent(typeof(AimManager))]
public class RopeAim : AimModeBase
{
    [Header("Placement Preview")]
    public GameObject previewPrefab;
    [Tooltip("How far ahead of the player the preview probe starts (meters).")]
    public float previewDistance = 1.5f;
    [Tooltip("Ground may be at most this far ABOVE the player's feet to be valid.")]
    public float maxHeightAbove = 0.9f;
    [Tooltip("Ground may be at most this far BELOW the player's feet to be valid.")]
    public float maxDepthBelow = 0.9f;
    [Tooltip("How far below the player's feet the probe keeps searching for ground just to " +
             "SHOW the (red) preview. No ground within this depth hides the preview entirely.")]
    public float previewProbeDepth = 4f;
    [Tooltip("Everything the downward probe can land the preview on.")]
    public LayerMask groundMask = ~0;
    [Tooltip("Layers where anchor placement is actually allowed.")]
    public LayerMask validPlacementLayers;

    [Header("Preview Colors")]
    public Color validColor = new Color(0.2f, 0.5f, 1f, 1f);
    public Color invalidColor = new Color(1f, 0.12f, 0.08f, 1f);

    [Header("Camera")]
    [Tooltip("While rope-aiming, the camera locks to this pitch (degrees) and looks down at the ground where " +
             "the anchor will drop. Horizontal look stays free; vertical look is frozen. Tune live — more " +
             "negative looks further down into the ground on this rig; flip the sign if it tilts the wrong way.")]
    public float cameraPitchAngle = -45f;

    [Header("Input System (injected by AimManager)")]
    public PlayerInput playerInput;

    private static readonly int PulseColorId = Shader.PropertyToID("_Pulse_Color");

    private GameObject previewInstance;
    private Renderer[] previewRenderers;
    private MaterialPropertyBlock propertyBlock;
    private bool lastAppliedValid;
    private bool colorApplied;

    private bool placementValid;
    private Vector3 placementPoint;
    private Quaternion placementRotation = Quaternion.identity;

    // The radial ledge search costs ~100 rays — cache it and re-run only when the preview moves.
    private bool ledgeCheckDone;
    private bool ledgeCheckResult;
    private Vector3 ledgeCheckPos;

    /// <summary>Current probe result — read on the Use press to place the anchor.</summary>
    public bool PlacementValid => placementValid;
    public Vector3 PlacementPoint => placementPoint;

    private HeldItemHandler heldHandler = new HeldItemHandler();
    private RopeController ropeController;

    private void Awake()
    {
        ropeController = GetComponent<RopeController>();
    }

    // Default aim-camera framing for this mode (applied when the component is added).
    private void Reset()
    {
        camHeight = 0.21f;
        camDist = 1.22f;
        camSide = 0.9f;
        maxVerticalAngle = 75f;
    }

    public override void EnterMode()
    {
        base.EnterMode();

        // Tilt the camera down to look at the ground where the anchor drops; horizontal look stays free.
        CameraState?.SetPitchLock(true, cameraPitchAngle);

        if (animator)
            animator.CrossFade("ThrowPose", 0.2f, animator.GetLayerIndex("AimingUpperBody"));

        if (playerInput != null && playerInput.actions["Use"] != null)
        {
            // -= before += : idempotent even if EnterMode ever runs twice without an ExitMode.
            playerInput.actions["Use"].performed -= HandleUse;
            playerInput.actions["Use"].performed += HandleUse;
        }

        heldHandler.SpawnHeldItem();
        EnsurePreviewInstance();
        ledgeCheckDone = false;   // fresh session — re-validate the ledge at the first preview spot
        UpdatePreview();
    }

    public override void UpdateMode()
    {
        base.UpdateMode();

        Vector2 input = MoveInput;
        if (animator)
        {
            animator.SetFloat(AimMoveXHash, input.x);
            animator.SetFloat(AimMoveYHash, input.y);
        }

        UpdatePreview();
    }

    public override void ExitMode()
    {
        base.ExitMode();

        CameraState?.SetPitchLock(false, 0f);   // release the look-down; eases back to free-look pitch

        if (animator)
        {
            animator.CrossFade("Idle Walk Run Blend", 0.2f, 0);
            animator.CrossFade("UpperBodyIdle", 0.2f, animator.GetLayerIndex("AimingUpperBody"));
        }

        if (playerInput != null && playerInput.actions["Use"] != null)
            playerInput.actions["Use"].performed -= HandleUse;

        if (previewInstance != null)
            previewInstance.SetActive(false);
        placementValid = false;

        heldHandler.DestroyHeldItem();
    }

    private void HandleUse(InputAction.CallbackContext ctx)
    {
        if (AimManager.Instance == null || !AimManager.Instance.IsAiming || AimManager.Instance.ActiveMode != this)
            return;
        if (!placementValid || ropeController == null || ropeController.IsPlacing)
            return;

        // Rope length comes from the item being consumed (read before TryConsumeCurrent runs).
        var cycleItems = AimManager.Instance.cycleItems;
        var item = cycleItems != null && cycleItems.currentPrefab != null
            ? cycleItems.currentPrefab.GetComponent<RopeItem>()
            : null;
        float ropeLength = item != null ? item.ropeLength : 20f;

        ropeController.BeginPlacement(placementPoint, placementRotation, ropeLength);
    }

    public override void OnItemChanged(GameObject newItem)
    {
        base.OnItemChanged(newItem);

        if (AimManager.Instance != null && AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
        {
            heldHandler.DestroyHeldItem();
            heldHandler.SpawnHeldItem();
        }
    }

    private void EnsurePreviewInstance()
    {
        if (previewInstance != null || previewPrefab == null) return;

        previewInstance = Instantiate(previewPrefab);
        previewInstance.SetActive(false);
        previewRenderers = previewInstance.GetComponentsInChildren<Renderer>(true);
        propertyBlock ??= new MaterialPropertyBlock();

        // The preview is purely visual — a live collider would block the probe ray
        // and the player capsule.
        foreach (Collider col in previewInstance.GetComponentsInChildren<Collider>(true))
            col.enabled = false;
    }

    private void UpdatePreview()
    {
        if (previewInstance == null || playerTransform == null) return;

        Vector3 feet = playerTransform.position;
        Vector3 forward = playerTransform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) return;
        forward.Normalize();

        // Probe: start above the highest valid ground, cast down far enough to still show
        // an (invalid, red) preview on ground below the valid window.
        Vector3 probeStart = feet + forward * previewDistance + Vector3.up * (maxHeightAbove + 0.1f);
        float probeLength = maxHeightAbove + 0.1f + previewProbeDepth;

        if (Physics.Raycast(probeStart, Vector3.down, out RaycastHit hit, probeLength, groundMask,
                            QueryTriggerInteraction.Ignore))
        {
            placementPoint = hit.point;

            float heightDelta = hit.point.y - feet.y;
            bool layerOk = ((1 << hit.collider.gameObject.layer) & validPlacementLayers) != 0;
            placementValid = layerOk
                             && heightDelta <= maxHeightAbove
                             && heightDelta >= -maxDepthBelow;

            // Anchors only make sense next to a rappel-able ledge now — placement leads straight
            // into the wall rappel. Search radius/probes live on RopeController.
            if (placementValid && ropeController != null)
            {
                if (!ledgeCheckDone || (hit.point - ledgeCheckPos).sqrMagnitude > 0.04f)
                {
                    ledgeCheckResult = ropeController.FindNearestRappelLedge(hit.point, out _);
                    ledgeCheckPos = hit.point;
                    ledgeCheckDone = true;
                }
                placementValid &= ledgeCheckResult;
            }

            placementRotation = Quaternion.LookRotation(forward, Vector3.up);
            previewInstance.transform.SetPositionAndRotation(hit.point, placementRotation);

            if (!previewInstance.activeSelf) previewInstance.SetActive(true);
            ApplyPreviewColor(placementValid);
        }
        else
        {
            // Nothing to stand the anchor on (chasm / too far down) — no preview at all.
            placementValid = false;
            if (previewInstance.activeSelf) previewInstance.SetActive(false);
        }
    }

    private void ApplyPreviewColor(bool valid)
    {
        if (previewRenderers == null || (colorApplied && lastAppliedValid == valid)) return;

        propertyBlock.SetColor(PulseColorId, valid ? validColor : invalidColor);
        for (int i = 0; i < previewRenderers.Length; i++)
            previewRenderers[i].SetPropertyBlock(propertyBlock);

        lastAppliedValid = valid;
        colorApplied = true;
    }
}
