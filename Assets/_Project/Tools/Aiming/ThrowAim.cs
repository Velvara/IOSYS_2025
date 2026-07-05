using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(AimManager))]
public class ThrowAim : AimModeBase
{
    [Header("Throw-specific")]
    public TrajectoryPredictor trajectory;
    public Transform aimDirection;
    public float throwForce = 15f;
    public float throwForceLookMultiplier = 1.5f;
    public AnimationCurve throwForceCurve = AnimationCurve.Linear(0f, 0f, 90f, 1f);
    public string throwTriggerName = "Throw";
    private float _currentCalculatedThrowForce;

    [Header("Input System (injected by AimManager)")]
    public PlayerInput playerInput;

    private bool isThrowingAnimationPlaying = false;
    private bool isThrowCooldown = false;

    private HeldItemHandler heldHandler = new HeldItemHandler();

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

        if (animator)
            animator.CrossFade("ThrowPose", 0.2f, animator.GetLayerIndex("AimingUpperBody"));

        if (playerInput != null && playerInput.actions["Use"] != null)
        {
            // -= before += : idempotent even if EnterMode ever runs twice without an ExitMode.
            playerInput.actions["Use"].performed -= HandleUse;
            playerInput.actions["Use"].performed += HandleUse;
        }

        if (!isThrowCooldown)
        {
            UpdateCalculatedForce();   // don't draw the first frame with a stale (0) force
            trajectory?.DrawTrajectory(_currentCalculatedThrowForce, aimDirection.forward);
            heldHandler.SpawnHeldItem();
        }
    }

    public override void UpdateMode()
    {
        base.UpdateMode();

        UpdateCalculatedForce();

        if (!isThrowCooldown)
        {
            trajectory?.DrawTrajectory(_currentCalculatedThrowForce, aimDirection.forward);
        }

        Vector2 input = MoveInput;
        if (animator)
        {
            animator.SetFloat(AimMoveXHash, input.x);
            animator.SetFloat(AimMoveYHash, input.y);
        }
    }

    /// <summary>Base force plus the additive look-up boost: force × (1 + curve(pitch) × multiplier).</summary>
    private void UpdateCalculatedForce()
    {
        _currentCalculatedThrowForce = throwForce + (throwForce * GetThrowForceMultiplier());
    }

    public override void ExitMode()
    {
        base.ExitMode();

        if (animator)
        {
            animator.CrossFade("Idle Walk Run Blend", 0.2f, 0);
            animator.CrossFade("UpperBodyIdle", 0.2f, animator.GetLayerIndex("AimingUpperBody"));
        }

        trajectory?.HideTrajectory();

        if (playerInput != null && playerInput.actions["Use"] != null)
            playerInput.actions["Use"].performed -= HandleUse;

        isThrowCooldown = false;
        isThrowingAnimationPlaying = false;

        heldHandler.DestroyHeldItem();
    }

    private void HandleUse(InputAction.CallbackContext ctx)
    {
        if (AimManager.Instance != null && AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
            Throw();
    }

    public void Throw()
    {
        if (isThrowingAnimationPlaying) return;
        if (animator) animator.SetTrigger(throwTriggerName);
        isThrowingAnimationPlaying = true;
    }

    // Animation event
    public void ReleaseProjectile()
    {
        isThrowCooldown = true;
        trajectory?.HideTrajectory();

        // ... (existing checks and setup) ...

        GameObject prefab = AimManager.Instance.cycleItems.currentPrefab;
        GameObject handSlot = AimManager.Instance.handSlot;
        if (prefab == null || handSlot == null) return;

        GameObject obj = Object.Instantiate(prefab, handSlot.transform.position, Quaternion.identity);

        if (obj.GetComponent<Rigidbody>() == null)
            obj.AddComponent<Rigidbody>();

        // CRITICAL CHANGE: Use the force calculated in UpdateMode()
        Vector3 direction = aimDirection.forward;
        Vector3 force = direction * _currentCalculatedThrowForce; // <-- USE STORED FORCE

        var throwable = obj.GetComponent<ThrowableObject>();
        if (throwable != null)
            throwable.Launch(force);
        else
            Debug.LogWarning($"{obj.name} does not have a ThrowableObject component!");

        // Remove old hand-held object
        heldHandler.DestroyHeldItem();

        // Respawn held object if still in ThrowAim mode
        if (AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
            heldHandler.SpawnHeldItem();
    }

    // Animation event
    public void FinishedThrowAnimation()
    {
        isThrowCooldown = false;
        isThrowingAnimationPlaying = false;

        if (AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
            trajectory?.DrawTrajectory(_currentCalculatedThrowForce, aimDirection.forward);
    }

    /// <summary>
    /// Additive boost factor (0..throwForceLookMultiplier) from the camera's UPWARD pitch:
    /// 0 when looking level or down, curve(0..90°) × multiplier when looking up — so lobbed
    /// throws get extra force without touching flat throws.
    /// </summary>
    private float GetThrowForceMultiplier()
    {
        Transform cameraTransform = AimManager.Instance != null ? AimManager.Instance.CameraTransform : null;
        if (cameraTransform == null) return 0f;   // no camera → no boost

        // Euler X: looking up reads as 360→270; map that to a 0..90 up-pitch magnitude.
        float pitchAngle = cameraTransform.localEulerAngles.x;
        if (pitchAngle <= 180f) return 0f;        // level or looking down

        float upPitch = Mathf.Clamp(360f - pitchAngle, 0f, 90f);
        return throwForceCurve.Evaluate(upPitch) * throwForceLookMultiplier;
    }
    public override void OnItemChanged(GameObject newItem)
    {
        base.OnItemChanged(newItem);

        // just respawn based on cycleItems
        if (AimManager.Instance != null && AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
        {
            heldHandler.DestroyHeldItem();
            heldHandler.SpawnHeldItem();
        }
    }
}
