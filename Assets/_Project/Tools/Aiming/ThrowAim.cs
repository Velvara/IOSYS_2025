using StarterAssets;
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
    public StarterAssetsInputs starterInputs;
    private float _currentCalculatedThrowForce;

    [Header("Input System (injected by AimManager)")]
    public PlayerInput playerInput;

    private bool isThrowingAnimationPlaying = false;
    private bool isThrowCooldown = false;

    private HeldItemHandler heldHandler = new HeldItemHandler();

    public override void EnterMode()
    {
        base.EnterMode();

        if (animator)
            animator.CrossFade("ThrowPose", 0.2f, animator.GetLayerIndex("AimingUpperbody"));

        if (playerInput != null && playerInput.actions["Use"] != null)
            playerInput.actions["Use"].performed += HandleUse;

        if (!isThrowCooldown)
        {
            trajectory?.DrawTrajectory(_currentCalculatedThrowForce, aimDirection.forward);
            heldHandler.SpawnHeldItem();
        }
    }

    public override void UpdateMode()
    {
        base.UpdateMode();
        // 1. Calculate the final throw force magnitude
        float boostMultiplier = GetThrowForceMultiplier();

        // Additive Boost: Base force + (Base force * Boost Multiplier)
        _currentCalculatedThrowForce = throwForce + (throwForce * boostMultiplier);

        if (!isThrowCooldown)
        {
            trajectory.DrawTrajectory(_currentCalculatedThrowForce, aimDirection.forward);
        }

        Vector2 input = starterInputs != null ? starterInputs.CurrentMoveInput : Vector2.zero;
        if (animator)
        {
            animator.SetFloat("AimMoveX", input.x);
            animator.SetFloat("AimMoveY", input.y);
        }
    }

    public override void ExitMode()
    {
        base.ExitMode();

        if (animator)
        {
            animator.CrossFade("Idle Walk Run Blend", 0.2f, 0);
            animator.CrossFade("UpperBodyIdle", 0.2f, 2);
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

    private float GetThrowForceMultiplier()
    {
        Transform cameraTransform = AimManager.Instance.CameraTransform;
        if (cameraTransform == null)
        {
            return 1.0f; // Return 1.0 (no change) if camera is missing
        }

        float pitchAngle = cameraTransform.localEulerAngles.x;
        float upPitchAngle = 0f;
        float normalizedPitchForCurve = 0f;

        // --- 1. Map 360-270 range to 0-90 pitch magnitude (0=straight, 90=max up) ---
        if (pitchAngle > 180f && pitchAngle <= 360f)
        {
            upPitchAngle = 360f - pitchAngle;
            normalizedPitchForCurve = Mathf.Clamp(upPitchAngle, 0f, 90f);
        }

        // --- 2. Calculate Force Boost Factor ---
        if (normalizedPitchForCurve > 0.001f)
        {
            // Evaluate the curve (0 to 1) based on the upward pitch magnitude (0 to 90)
            float curveFactor = throwForceCurve.Evaluate(normalizedPitchForCurve);

            // The total force multiplier is 1.0 (base) plus the boost derived from the curve
            // and the public multiplier.
            float boostFactor = curveFactor * throwForceLookMultiplier;

            // Since the prompt asks to multiply `throwForce` by the multiplier, 
            // we'll return the full factor, including the base 1.0 force.
            // Wait, the prompt asks to multiply 'throwForce' by 'that number' (the curve value * lookMultiplier).
            // Let's assume the boost is ADDED to the base force for sane physics.
            // If we want to strictly follow the prompt: we only return the boost magnitude.

            // Let's implement this as a PURE boost magnitude (i.e., 0 to N).
            return boostFactor;
        }

        // 3. Return 0 (no additional force) if looking straight or down
        return 0f;
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
