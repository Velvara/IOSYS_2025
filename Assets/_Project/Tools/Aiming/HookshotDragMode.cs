using StarterAssets;
using UnityEngine;

public class HookshotDragMode : AimModeBase
{
    [Header("Drag Settings")]
    public float dragSpeed = 10f;
    public float stopDistance = 1f;

    [Header("Animation Curve & Rotation")]
    [Tooltip("The curve to apply to drag speed over the duration of the movement.")]
    public AnimationCurve dragSpeedCurve = AnimationCurve.EaseInOut(0, 1, 1, 0.1f);
    [Tooltip("The amount to scale the curve for rotation. 0 = no rotation, 1 = full rotation.")]
    [Range(0.0f, 1.0f)]
    public float dragRotationAmount = 1.0f;

    [Header("Failsafe Settings")]
    [Tooltip("Time in seconds after drag ends before the tip is forced to be destroyed.")]
    public float failsafeDelay = 1.0f;

    private Hookshot activeHookshot;
    private Transform tipTransform;
    private bool isDragging = false;
    public float cameraDragLag = 5.0f;
    public float rotationSpeed = 5f;

    private bool hasLoggedInitialCheck = false;
    private float initialDistance;
    private Quaternion startRotation;

    private void Awake()
    {
        // Find TPC (inherited 'controller' field)
        if (tpcController == null)
            tpcController = GetComponentInParent<StarterAssets.ThirdPersonController>();

        if (tpcController == null)
            Debug.LogError("HookshotDragMode could not find ThirdPersonController on activation path.");
    }

    public override void EnterMode()
    {
        base.EnterMode();

        if (tpcController != null)
        {
            tpcController.IsExternalControlActive = true;
            startRotation = tpcController.transform.rotation;
        }

        // **Crucial:** Clear input to stop default movement/jumping instantly
        if (tpcController.TryGetComponent<StarterAssetsInputs>(out var inputs))
        {
            inputs.MoveInput(Vector2.zero);
            inputs.JumpInput(false);
            inputs.SprintInput(false);
        }

        // Adjust camera distance to create a "lagging" effect
        if (AimManager.Instance != null && AimManager.Instance.cameraFollow != null)
        {
            var follow = AimManager.Instance.cameraFollow;

            // Use the current aim distance (from AimModeBase) and add the drag lag
            float targetDistance = this.camDist + cameraDragLag;

            // Request the transition to the new, lagged distance
            AimManager.Instance.RequestCameraTransition(
                this.camHeight,
                targetDistance, // Use the lagged distance
                this.camSide
            );
        }

        // Trigger animator state
        if (animator != null)
            animator.SetBool("IsHookshotDragging", true);
    }

    // CRITICAL FIX: Override and disable the rotation from AimModeBase
    public override void UpdateMode()
    {
        // Do nothing. This prevents the rotation logic in AimModeBase.UpdateMode()
        // from running and conflicting with our manual rotation in FixedUpdate().
    }

    public void BeginDrag(Hookshot hookshot, Transform tip)
    {
        Debug.Log("drag begin");
        activeHookshot = hookshot;
        tipTransform = tip;
        isDragging = true;

        hasLoggedInitialCheck = false; // <-- Reset on START
        initialDistance = (tipTransform.position - tpcController.transform.position).magnitude;
    }

    private void FixedUpdate()
    {
        bool isDraggingOK = isDragging;
        bool activeHookshotOK = activeHookshot != null;
        bool tipTransformOK = tipTransform != null;
        bool controllerOK = tpcController != null;
        CharacterController tpcCharacterController = tpcController?.GetComponent<CharacterController>();

        if (!isDragging)
            return; // Basic check to skip when not dragging

        bool checkFailed = (activeHookshot == null || tipTransform == null || tpcController == null || tpcCharacterController == null);

        if (checkFailed)
        {
            if (!hasLoggedInitialCheck)
            {
                Debug.LogError("FATAL DRAG CHECK FAILED! Analyzing culprits:");
                Debug.Log($"  isDragging: {isDragging}"); // Should be True
                Debug.Log($"  activeHookshot: {activeHookshot != null}");
                Debug.Log($"  tipTransform: {tipTransform != null}");
                Debug.Log($"  TPC controller (AimModeBase): {tpcController != null}");
                Debug.Log($"  CharacterController (Local): {tpcCharacterController != null}");

                if (tpcController != null)
                {
                    Debug.Log($"  TPC Script Enabled: {tpcController.enabled}");
                }

                hasLoggedInitialCheck = true; // Prevents logging this again
            }

            return; // Always return if the check fails
        }

        // Use the player's root transform for distance calculation
        Transform playerTransform = tpcController.transform;

        // 1. Calculate the movement direction (a vector *to* the tip)
        Vector3 dragDirection = tipTransform.position - playerTransform.position;
        float distance = dragDirection.magnitude;

        // --- NEW LOGIC: Use an animation curve for speed and rotation ---

        // Calculate a normalized value from 0 (start) to 1 (end)
        float dragProgress = 1.0f - (distance / initialDistance);

        // Sample the curve at the current progress to get a speed multiplier
        float speedMultiplier = dragSpeedCurve.Evaluate(dragProgress);

        // Apply the speed multiplier
        float currentDragSpeed = dragSpeed * speedMultiplier;

        // Get the target rotation from the tip
        Quaternion tipRotation = tipTransform.rotation;
        Quaternion rollOffset = Quaternion.Euler(0, 0, -90f);
        Quaternion targetRotation = tipRotation * rollOffset;

        // Sample the curve again for rotation blending, with a new dragRotationAmount variable
        float rotationMultiplier = dragSpeedCurve.Evaluate(dragProgress) * dragRotationAmount;

        // Slerp from the start rotation to the target rotation based on the curve value
        playerTransform.rotation = Quaternion.Slerp(
            startRotation,
            targetRotation,
            rotationMultiplier
        );

        // --- END OF NEW LOGIC ---

        // 3. Determine the step size and clamp it so you don't overshoot the target
        float step = currentDragSpeed * Time.fixedDeltaTime;
        Vector3 movementVector = dragDirection.normalized * Mathf.Min(step, distance);

        // 4. Use the CharacterController.Move for the actual movement!
        tpcCharacterController.Move(movementVector);

        // Stop drag when close enough (use the distance calculated earlier)
        if (distance < stopDistance)
        {
            EndDrag();
        }
    }

    public void EndDrag()
    {
        Debug.Log("drag ending");
        if (!isDragging) return;

        isDragging = false;

        // Check if the tip object still exists
        if (tipTransform != null)
        {
            HookshotTip tip = tipTransform.GetComponent<HookshotTip>();
            if (tip != null)
            {
                tip.StartReturn();
            }

            // NEW: Start the failsafe timer to force-destroy the tip
            // This is a failsafe in case StartReturn() doesn't work for some reason.
            Invoke("FailsafeDestroyTip", failsafeDelay);
        }

        // Set a default, level rotation before exiting drag mode.
        // This is vital to prevent the TPC from snapping/glitching upon re-enable.
        if (tpcController != null)
        {
            Transform playerTransform = tpcController.transform;

            // Reset the rotation to horizontal, facing the current forward direction.
            playerTransform.rotation = Quaternion.LookRotation(
            Vector3.ProjectOnPlane(playerTransform.forward, Vector3.up).normalized,
            Vector3.up
            );
        }

        if (tpcController != null)
        {
            tpcController.RotateOnMove = true;
            tpcController.IsExternalControlActive = false; // <-- Reset the flag LAST
        }

        if (animator != null)
        {
            animator.SetBool("IsHookshotDragging", false);

            // Ensure upper body returns to locomotion state
            animator.CrossFade("Idle Walk Run Blend", 0.2f, 0);
            animator.CrossFade("UpperBodyIdle", 0.2f, 2);
        }

        if (AimManager.Instance != null)
        {
            AimManager manager = AimManager.Instance;

            manager.ForceAimExit();
        }

        activeHookshot = null;
        tipTransform = null;
    }

    private void FailsafeDestroyTip()
    {
        if (tipTransform != null)
        {
            Debug.LogWarning("Failsafe activated: Hookshot tip was not destroyed. Forcing destruction.");
            Destroy(tipTransform.gameObject);
        }
    }
}