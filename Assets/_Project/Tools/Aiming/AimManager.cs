using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Cinemachine;
using System.Collections;
using Game.PlayerV2;

[RequireComponent(typeof(PlayerInput))]
public class AimManager : MonoBehaviour
{
    [Header("Camera Defaults")]
    private float defaultCamHeight;
    private float defaultCamDist;
    private float defaultCamSide;
    private bool defaultsCaptured = false;
    public float dragCamResetSpeed = 0.5f;

    public GameObject handSlot;
    private Transform hookshotTip;
    private bool isHookshotDragging = false;
    [HideInInspector] public bool isHookshotFiring = false;

    [Header("References")]
    public PlayerInput playerInput;
    public CycleItems cycleItems;
    public Unity.Cinemachine.CinemachineThirdPersonFollow cameraFollow;
    public HeldItemHandler heldHandler;
    public CharacterStateManager characterStateManager;

    [Header("Aim Modes")]
    public ThrowAim throwAimMode;
    public ShootAim shootAimMode;
    public ScanAim scanAimMode;
    public HookshotDragMode hookshotDragMode;

    // ===== Head & Hand IK =====
    [Header("IK Settings")]
    public bool enableHeadLook = true;
    public bool enableShootHandIK = true;
    public Transform rightHandTargetHint; // optional, can leave null
    [Range(0f, 1f)] public float headLookWeight = 1.0f;
    [Range(0f, 1f)] public float handIKWeight = 1.0f;
    public bool enableShootShoulderAim = true;
    public Transform rightShoulderBone; // assign in inspector
    public float shoulderAimWeight = 1f;
    public float shoulderRotationSpeed = 1f;

    private Animator animator;
    private Transform cameraTransform;
    public Transform CameraTransform => cameraTransform;

    private AimModeBase activeMode = null;
    public AimModeBase ActiveMode => activeMode; // expose for checks
    private bool isAiming = false;

    // Resolved from the player hierarchy. While an external system (climbing, cutscene) holds
    // control, aiming is blocked / dropped so tool IK doesn't fight the external system.
    private IControlLock controlLock;

    // transition system
    private Coroutine transitionCoroutine;
    [SerializeField] private float cameraTransitionDuration = 1f;

    public float GetDefaultCamHeight() => defaultCamHeight;
    public float GetDefaultCamDist() => defaultCamDist;
    public float GetDefaultCamSide() => defaultCamSide;

    public static AimManager Instance { get; private set; }
    public bool IsAiming => isAiming;

    public bool IsInHookshotDragMode
    {
        get { return activeMode is HookshotDragMode; }
    }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        hookshotDragMode = GetComponent<HookshotDragMode>();

        controlLock = GetComponentInParent<IControlLock>();

        if (playerInput == null)
            playerInput = GetComponent<PlayerInput>();

        animator = GetComponent<Animator>();
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Capture defaults
        var follow = FindFirstObjectByType<Unity.Cinemachine.CinemachineThirdPersonFollow>();
        if (follow != null && !defaultsCaptured)
        {
            defaultCamHeight = follow.ShoulderOffset.y;
            defaultCamDist = follow.CameraDistance;
            defaultCamSide = follow.CameraSide;
            defaultsCaptured = true;
        }

        // Inject PlayerInput into all aim modes
        if (throwAimMode != null) throwAimMode.playerInput = playerInput;
        if (shootAimMode != null) shootAimMode.playerInput = playerInput;
        if (scanAimMode != null) scanAimMode.playerInput = playerInput;

        // Subscribe to Aim input
        var actions = playerInput.actions;
        if (actions != null && actions["Aim"] != null)
        {
            actions["Aim"].performed += ctx => OnAimPerformed();
            actions["Aim"].canceled += ctx => OnAimCanceled();
        }

        // Listen to item cycling
        if (cycleItems != null)
            cycleItems.OnItemChangedEvent += OnItemChanged;

        SetActiveMode(null);
    }

    void Update()
    {
        // If an external system (climbing, cutscene) took control while aiming, drop aim — but
        // NOT for the hookshot drag, which is itself the external controller. Does not release
        // the control lock (the external system owns it).
        if (isAiming && activeMode != hookshotDragMode && !isHookshotFiring &&
            controlLock != null && controlLock.IsExternalControlActive)
        {
            CancelAimForExternalControl();
            return;
        }

        if (isAiming && activeMode != null)
            activeMode.UpdateMode();
    }

    private void OnAimPerformed()
    {
        if (activeMode == hookshotDragMode || isHookshotFiring)
        {
            Debug.Log("Aim Performed ignored during drag.");
            return;
        }

        // Block starting aim while an external system holds control (climbing, cutscene).
        if (controlLock != null && controlLock.IsExternalControlActive)
            return;

        isAiming = true;
        ChooseModeByCurrentItem();
        activeMode?.EnterMode();

        if (activeMode != null)
            RequestCameraTransition(activeMode.camHeight, activeMode.camDist, activeMode.camSide);
    }

    private void OnAimCanceled()
    {
        if (activeMode == hookshotDragMode || isHookshotFiring)
        {
            Debug.Log("cancel override");
            return;
        }

        // If not in drag mode, run the normal exit logic
        isAiming = false;
        activeMode?.ExitMode();
        activeMode = null;

        RequestCameraTransition(defaultCamHeight, defaultCamDist, defaultCamSide);
    }

    public void ForceAimExit()
    {
        // The camera transition is requested by EndDrag(), but we request it again 
        // here to ensure the duration is applied in the right context.
        RequestCameraTransition(defaultCamHeight, defaultCamDist, defaultCamSide, dragCamResetSpeed);

        // 1. Clear HookshotDrag state first (ensures IK is immediately off)
        StopHookshotDrag();

        // 2. Reset the input state
        isAiming = false;

        // 3. Exit the now-deactivated active mode
        activeMode?.ExitMode();
        activeMode = null;

        characterStateManager?.UnlockCharacter();
    }

    /// <summary>
    /// Drops the active aim mode because an external system (e.g. climbing) has taken control.
    /// Unlike <see cref="ForceAimExit"/>, it does NOT release the control lock or unlock cycling —
    /// the external system owns the lock and will release it on exit.
    /// </summary>
    private void CancelAimForExternalControl()
    {
        isAiming = false;
        activeMode?.ExitMode();
        activeMode = null;
        RequestCameraTransition(defaultCamHeight, defaultCamDist, defaultCamSide);
    }


    public void OnItemChanged(GameObject newItem)
    {
        AimModeBase newMode = null;
        if (newItem != null)
        {
            if (newItem.GetComponent<ThrowableObject>() != null) newMode = throwAimMode;
            else if (newItem.GetComponent<ShootingTool>() != null) newMode = shootAimMode;
            else if (newItem.GetComponent<ScanTool>() != null) newMode = scanAimMode;
        }

        if (isAiming)
        {
            if (activeMode == newMode)
            {
                activeMode?.OnItemChanged(newItem);
            }
            else
            {
                activeMode?.ExitMode();
                activeMode = newMode;
                activeMode?.EnterMode();
                activeMode?.OnItemChanged(newItem);

                if (activeMode != null)
                    RequestCameraTransition(activeMode.camHeight, activeMode.camDist, activeMode.camSide);
            }
        }
        else
        {
            activeMode = newMode;
        }
    }

    public void ChooseModeByCurrentItem()
    {
        GameObject current = cycleItems != null ? cycleItems.currentPrefab : null;
        ChooseModeByItem(current);
    }

    private void ChooseModeByItem(GameObject item)
    {
        if (item == null) { SetActiveMode(null); return; }

        if (item.GetComponent<ThrowableObject>() != null) { SetActiveMode(throwAimMode); return; }
        if (item.GetComponent<ShootingTool>() != null) { SetActiveMode(shootAimMode); return; }
        if (item.GetComponent<ScanTool>() != null) { SetActiveMode(scanAimMode); return; }

        SetActiveMode(null);
    }

    public void SetActiveMode(AimModeBase mode)
    {
        if (activeMode != null)
        {
            // Check if the current mode is ShootAim and the new mode is HookshotDragMode
            if (activeMode == shootAimMode && mode == hookshotDragMode)
            {
                // Do NOT call ExitMode on ShootAim. 
                // We need to keep the Hookshot item alive for the drag!
                // Instead, just clear its input binding and disable its visuals.
                shootAimMode.TemporarilyDeactivate();
            }
            else
            {
                activeMode.ExitMode(); // Normal Exit
            }
        }

        activeMode = mode;

        if (activeMode != null && isAiming)
            activeMode.EnterMode();
    }

    public void RequestCameraTransition(float targetHeight, float targetDist, float targetSide, float? duration = null)
    {
        if (cameraFollow == null) return;

        float transitionDuration = duration.GetValueOrDefault(cameraTransitionDuration);

        if (transitionCoroutine != null)
            StopCoroutine(transitionCoroutine);

        transitionCoroutine = StartCoroutine(SmoothCameraTransition(cameraFollow, targetHeight, targetDist, targetSide));
    }

    private IEnumerator SmoothCameraTransition(CinemachineThirdPersonFollow follow,
                                              float targetHeight, float targetDist, float targetSide)
    {
        float startHeight = follow.ShoulderOffset.y;
        float startDist = follow.CameraDistance;
        float startSide = follow.CameraSide;

        float elapsed = 0f;
        while (elapsed < cameraTransitionDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, elapsed / cameraTransitionDuration);

            Vector3 shoulder = follow.ShoulderOffset;
            shoulder.y = Mathf.Lerp(startHeight, targetHeight, t);
            follow.ShoulderOffset = shoulder;

            follow.CameraDistance = Mathf.Lerp(startDist, targetDist, t);
            follow.CameraSide = Mathf.Lerp(startSide, targetSide, t);

            yield return null;
        }

        Vector3 finalShoulder = follow.ShoulderOffset;
        finalShoulder.y = targetHeight;
        follow.ShoulderOffset = finalShoulder;
        follow.CameraDistance = targetDist;
        follow.CameraSide = targetSide;

        transitionCoroutine = null;
    }

    // IK function remains unchanged
    void OnAnimatorIK(int layerIndex)
    {
        // Ensure all necessary components are available
        if (animator == null || cameraTransform == null || heldHandler == null) return;

        // Check if any IK is active (either aiming or dragging)
        if (IsAiming || isHookshotDragging)
        {
            Vector3 aimDirection;
            Vector3 ikTargetPosition;

            // --- 1. Determine the Master Aim Direction/Target ---
            if (isHookshotDragging && hookshotTip != null)
            {
                // DRAG MODE: Look and aim towards the physical hookshot tip
                ikTargetPosition = hookshotTip.position;
                aimDirection = (ikTargetPosition - cameraTransform.position).normalized;
            }
            else if (activeMode == shootAimMode && shootAimMode != null)
            {
                if (shootAimMode.IsCameraFrozen)
                {
                    // While camera is frozen > no exaggerated aim
                    aimDirection = cameraTransform.forward;
                }
                else
                {
                    aimDirection = shootAimMode.GetModifiedAimDirection();
                }

                ikTargetPosition = cameraTransform.position + aimDirection * 100f;
            }

            else
            {
                // Fallback (Aiming but no specific mode, use raw camera direction)
                aimDirection = cameraTransform.forward;
                ikTargetPosition = cameraTransform.position + aimDirection * 100f;
            }


            // --- 2. Head Look (Uses the calculated ikTargetPosition) ---
            if (enableHeadLook)
            {
                // Head should look at the IK target position
                animator.SetLookAtWeight(headLookWeight, 0f, 1f, 1f, 0.7f);
                animator.SetLookAtPosition(ikTargetPosition); // <--- Uses the calculated target
            }
            else
            {
                animator.SetLookAtWeight(0f);
            }


            // --- 3. Right Hand IK (Uses the calculated aimDirection/ikTargetPosition) ---
            if (enableShootHandIK && (activeMode == shootAimMode || isHookshotDragging))
            {
                // The position used for the hand IK is the same as the Head Look target, 
                // but brought closer (10f) for better articulation if not dragging.
                Vector3 handTargetPos = isHookshotDragging && hookshotTip != null
                    ? hookshotTip.position
                    : cameraTransform.position + aimDirection * 10f; // <--- Uses modified direction

                animator.SetIKPositionWeight(AvatarIKGoal.RightHand, handIKWeight);
                animator.SetIKRotationWeight(AvatarIKGoal.RightHand, handIKWeight);

                animator.SetIKPosition(AvatarIKGoal.RightHand, handTargetPos);

                // Calculate the rotation to face the target position from the hand bone's perspective
                animator.SetIKRotation(AvatarIKGoal.RightHand, Quaternion.LookRotation(handTargetPos - animator.GetBoneTransform(HumanBodyBones.RightHand).position));

                if (rightHandTargetHint != null)
                {
                    animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, handIKWeight);
                    animator.SetIKHintPosition(AvatarIKHint.RightElbow, rightHandTargetHint.position);
                }
            }
            else
            {
                animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
                animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
                animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
            }

            // --- 4. Shoulder Rotation (Uses the calculated aimDirection) ---
            if (enableShootShoulderAim && rightShoulderBone != null && (activeMode == shootAimMode || isHookshotDragging))
            {
                Vector3 targetDir = (isHookshotDragging && hookshotTip != null)
                    ? (hookshotTip.position - rightShoulderBone.position)
                    : aimDirection; // <--- Uses modified direction

                Quaternion targetWorldRot = Quaternion.LookRotation(targetDir, Vector3.up);
                Quaternion localTargetRot = Quaternion.Inverse(rightShoulderBone.parent.rotation) * targetWorldRot;

                rightShoulderBone.localRotation = Quaternion.Slerp(
                    rightShoulderBone.localRotation,
                    localTargetRot,
                    Time.deltaTime * shoulderRotationSpeed * shoulderAimWeight
                );
            }
        }
        else
        {
            // --- 5. IK Cleanup (If not aiming or dragging) ---
            animator.SetLookAtWeight(0f);
            animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0f);
            animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0f);
            animator.SetIKHintPositionWeight(AvatarIKHint.RightElbow, 0f);
        }
    }

    public void StartHookshotDrag(Transform tip)
    {
        isHookshotFiring = false;
        hookshotTip = tip;
        isHookshotDragging = true;
        animator.SetBool("IsHookshotDragging", true);
    }

    public void StopHookshotDrag()
    {
        hookshotTip = null;
        isHookshotDragging = false;
        animator.SetBool("IsHookshotDragging", false);
    }
}