using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

public class ShootAim : AimModeBase
{
    [Header("Input System (injected by AimManager)")]
    public PlayerInput playerInput;

    private HeldItemHandler heldHandler = new HeldItemHandler();

    [Header("Fire Settings")]
    public string fireTriggerName = "Fire";
    private bool isShootingAnimationPlaying;

    [Header("Aim Target Settings")]
    public LayerMask aimLayers = ~0;
    public Transform shootOrigin;
    public GameObject aimTargetPrefab;
    private GameObject aimTargetInstance;
    private Animator aimTargetAnimator;
    private TargetType currentTargetType = TargetType.None;
    public Tags woodTag;

    [Header("Vertical Aim Control")]
    [Tooltip("Multiplier for upward aim exaggeration.")]
    public float lookUpMultiplier = 10f;

    [Tooltip("Curve to control the exaggeration. X-axis is camera pitch (0 to 90), Y-axis is multiplier (0 to 1).")]
    public AnimationCurve lookUpCurve = AnimationCurve.Linear(0f, 0f, 90f, 1f);

    [Header("HookShot Settings")]
    private bool isHookshotFiring = false;

    private enum TargetType { None, Valid, Invalid }

    private GameObject CurrentHeldItem => heldHandler.heldObj;

    // Default aim-camera framing for this mode (applied when the component is added).
    private void Reset()
    {
        camHeight = 0.21f;
        camDist = 0.2f;
        camSide = 0.7f;
        maxVerticalAngle = 75f;
    }

    public override void EnterMode()
    {
        base.EnterMode();

        //Failsafe Reset
        isHookshotFiring = false;
        if (AimManager.Instance != null)
            AimManager.Instance.isHookshotFiring = false;

        var manager = GameObject.FindFirstObjectByType<AimManager>();
        if (manager != null)
            manager.RequestCameraTransition(camHeight, camDist, camSide);

        if (animator)
            animator.CrossFade("ShootPose", 1f, layer: 2);

        heldHandler.SpawnHeldItem();

        if (playerInput != null)
        {
            var useAction = playerInput.actions["Use"];
            if (useAction != null)
                useAction.performed += HandleUse;
        }

        if (aimTargetInstance == null && aimTargetPrefab != null)
        {
            aimTargetInstance = Instantiate(aimTargetPrefab);
            aimTargetAnimator = aimTargetInstance.GetComponent<Animator>();
        }

        if (aimTargetInstance != null)
        {
            aimTargetInstance.SetActive(true);
            currentTargetType = TargetType.None;
            aimTargetAnimator?.SetTrigger("setNA");
        }
    }

    public override void UpdateMode()
    {
        base.UpdateMode();

        Vector2 input = MoveInput;
        animator?.SetFloat("AimMoveX", input.x);
        animator?.SetFloat("AimMoveY", input.y);

        UpdateAimTarget();
    }

    private void UpdateAimTarget()
    {
        var currentItem = CurrentHeldItem;
        if (currentItem == null || aimTargetInstance == null) return;

        var tool = currentItem.GetComponent<ShootingTool>();
        if (tool == null || shootOrigin == null) return;

        Vector3 origin = shootOrigin.position;

        // This includes the exaggerated upward rotation when looking up.
        Vector3 direction = GetModifiedAimDirection();

        bool hitSomething = Physics.Raycast(origin, direction, out RaycastHit hit, tool.shotDistance, aimLayers);

        Vector3 targetPos;
        TargetType newType;

        if (hitSomething)
        {
            targetPos = hit.point;
            var tagManager = hit.collider.GetComponent<TagObjManager>();
            newType = (tagManager != null && tagManager.HasTag(woodTag)) ? TargetType.Valid : TargetType.Invalid;
        }
        else
        {
            targetPos = origin + direction * tool.shotDistance;
            newType = TargetType.None;
        }

        aimTargetInstance.transform.position = targetPos;
        if (newType != currentTargetType)
            TriggerForType(newType, false);
        currentTargetType = newType;
    }

    public Vector3 GetModifiedAimDirection()
    {
 
        Transform cameraTransform = AimManager.Instance.CameraTransform;
        if (cameraTransform == null)
        {
            return Vector3.forward;
        }

        Vector3 rawDirection = cameraTransform.forward;

        // --- 1. Calculate the Camera's Pitch Angle and Map to Curve Domain ---
        float pitchAngle = cameraTransform.localEulerAngles.x;
        float upPitchAngle = 0f;
        float normalizedPitchForCurve = 0f;

        // Check if the rotation is in the "upward" range (360 -> 270)
        if (pitchAngle > 180f && pitchAngle <= 360f)
        {
            upPitchAngle = 360f - pitchAngle;

            if (IsCameraFrozen)
            {
                upPitchAngle = 0f;
            }

            normalizedPitchForCurve = Mathf.Clamp(upPitchAngle, 0f, 90f);
        }
        // If pitchAngle is 0 to 180 (straight or down), normalizedPitchForCurve remains 0.


        // --- 2. Apply the Exaggeration Logic (ONLY when looking UP) ---
        if (normalizedPitchForCurve > 0.001f)
        {
            float curveInput = normalizedPitchForCurve;

            // Evaluate the curve to get the dynamic multiplier factor (Y-axis value)
            float curveFactor = lookUpCurve.Evaluate(curveInput);

            // Calculate the final upward boost by multiplying the curve value by the public multiplier
            float upwardBoost = curveFactor * lookUpMultiplier;

            if (IsCameraFrozen)
            {
                Vector3 finalDirection = rawDirection;
                return finalDirection.normalized;
            }
            else
            {
                // Apply the boost to the world-space up vector
                Vector3 finalDirection = rawDirection + (Vector3.up * upwardBoost);
                // Return the normalized, boosted vector
                return finalDirection.normalized;
            }

        }
        // 3. Return the raw direction if looking straight or down
        return rawDirection;
    }

    private void TriggerForType(TargetType type, bool isInitial)
    {
        if (aimTargetAnimator == null) return;

        switch (type)
        {
            case TargetType.Valid: aimTargetAnimator.SetTrigger("toValid"); break;
            case TargetType.Invalid: aimTargetAnimator.SetTrigger("toInvalid"); break;
            case TargetType.None: aimTargetAnimator.SetTrigger("toNA"); break;
        }
    }

    public override void ExitMode()
    {
        base.ExitMode();

        if (animator)
        {
            animator.CrossFade("Idle Walk Run Blend", 0.2f, 0);
            animator.CrossFade("UpperBodyIdle", 0.2f, 2);
            animator.SetBool("IsShootingAiming", false);
        }

        heldHandler.DestroyHeldItem();

        if (playerInput != null)
        {
            var useAction = playerInput.actions["Use"];
            if (useAction != null)
                useAction.performed -= HandleUse;
        }

        if (aimTargetInstance != null)
        {
            aimTargetAnimator?.SetTrigger("setNA");
            aimTargetInstance.SetActive(false);
        }

        currentTargetType = TargetType.None;
    }

    private void HandleUse(InputAction.CallbackContext ctx)
    {
        if (AimManager.Instance != null && AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
        {
            if (heldHandler.heldObj != null)
            {
                if (heldHandler.heldObj.TryGetComponent<Hookshot>(out var hook))
                    HookshotFire(hook);
                else if (heldHandler.heldObj.TryGetComponent<Gripshot>(out var grip))
                    GripshotFire();
            }
        }
    }

    private void HookshotFire(Hookshot hookshot)
    {
        //Check if tip would be spawned inside wall. Prevent shooting if so
        LayerMask blockingLayers = hookshot.tipCheckCollisionLayers;
        float checkRadius = 0.4f;

        Collider[] overlaps = Physics.OverlapSphere(hookshot.hookSlot.position, checkRadius, blockingLayers);
        if (overlaps.Length > 0)
        {
            Debug.LogWarning("[HookshotFire] Blocked: spawn point inside a collider. Cannot fire hookshot.");
            return;
        }

        //Check conditions for shooting. If any passes, it will prevent shooting and log culprit
        if (isHookshotFiring || hookshot == null || (AimManager.Instance != null && AimManager.Instance.isHookshotFiring || isShootingAnimationPlaying))
        {
            if (isHookshotFiring)
                Debug.Log("[HookshotFire] Blocked: isHookshotFiring was true.");

            if (hookshot == null)
                Debug.Log("[HookshotFire] Blocked: hookshot reference was null.");

            if (AimManager.Instance != null && AimManager.Instance.isHookshotFiring)
                Debug.Log("[HookshotFire] Blocked: AimManager.isHookshotFiring was true.");

            if (isShootingAnimationPlaying)
                Debug.Log("[HookshotFire] Blocked: isShootingAnimationPlaying was true.");

            return;
        }

        isShootingAnimationPlaying = true;

        animator?.SetTrigger(fireTriggerName);

        aimTargetInstance.SetActive(false);

        if (hookshot.dummyTip == null || hookshot.hookshotTip == null || hookshot.hookSlot == null)
        {
            Debug.LogWarning("Hookshot prefab missing dummyTip, hookshotTipPrefab, or hookSlot!");
            return;
        }

        hookshot.dummyTip.gameObject.SetActive(false);

        Vector3 targetPos = aimTargetInstance != null
            ? aimTargetInstance.transform.position
            : (shootOrigin.position + shootOrigin.forward * hookshot.shotDistance);

        GameObject tipInstance = Instantiate(hookshot.hookshotTip, hookshot.hookSlot.position, hookshot.hookSlot.rotation);

        // Initialize tip with ShootAim reference and target
        var tipComp = tipInstance.GetComponent<HookshotTip>();
        tipComp.Init(this, hookshot, targetPos);

        // --- Spawn Rope ---
        GameObject ropeObj = new GameObject("HookshotRope");

        // add required components
        var mf = ropeObj.AddComponent<MeshFilter>();
        var mr = ropeObj.AddComponent<MeshRenderer>();

        // add rope script
        var rope = ropeObj.AddComponent<HookshotRope>();

        // assign material (so it doesn�t show as pink)
        mr.material = hookshot.ropeMaterial;

        // init rope
        rope.Init(hookshot.hookSlot, tipInstance.transform,
                  hookshot.ropeCylinderSides,
                  hookshot.ropeCylinderLoopDistance,
                  hookshot.ropeCylinderWidth);

        // apply per-hookshot wave values
        rope.waveFrequency = hookshot.ropeWaveFrequency;
        rope.waveAmplitude = hookshot.ropeWaveAmplitude;
        rope.waveDampingSpeed = hookshot.ropeWaveDamping;
        rope.hitDampingMultiplier = hookshot.ropeHitDampingMultiplier;

        // Assign rope material if set
        if (hookshot.ropeMaterial != null)
        {
            var ropeRenderer = ropeObj.GetComponent<MeshRenderer>();
            if (ropeRenderer != null)
                ropeRenderer.material = hookshot.ropeMaterial;

            // setup UV scroller
            var scroller = ropeObj.AddComponent<UVScroller>();
            scroller.uvScale = 1f; // tweak this for how stretched the texture is
            scroller.AttachTip(tipInstance.transform);
        }

        // Attach rope properly
        tipComp.AttachRope(rope); // let tip destroy it on return


        isHookshotFiring = true;

        // --- FREEZE CHARACTER MOVEMENT AND INPUT ---
        if (AimManager.Instance != null)
        {
            // 1. Set the central AimManager flag to block OnAimPerformed/Canceled
            AimManager.Instance.isHookshotFiring = true;

            // 2. Lock the character's movement and clear inputs
            AimManager.Instance.characterStateManager?.LockCharacter();
        }
    }

    public void OnHookshotTipCollision(GameObject tipInstance, Collider other, Hookshot hookshot)
    {
        if (!isHookshotFiring || hookshot == null) return;

        isHookshotFiring = false;

        bool valid = CheckTargetValidity(other.gameObject);

        if (valid)
        {
            Debug.Log("hit wood");

            // Attach tip by moving forward and freezing
            tipInstance.transform.position += tipInstance.transform.forward * hookshot.tipAttachDepth;

            Rigidbody rb = tipInstance.GetComponent<Rigidbody>();
            if (rb != null)
                rb.isKinematic = true;
        }
        else
        {
            Debug.Log("hit invalid");

            if (hookshot.invalidFX != null)
                Instantiate(hookshot.invalidFX, tipInstance.transform.position, Quaternion.identity);

            // Return is now handled in HookshotTip
        }
    }

    public bool CheckTargetValidity(GameObject target)
    {
        TagObjManager tagManager = target.GetComponent<TagObjManager>();
        return tagManager != null && tagManager.HasTag(woodTag);
    }

    private void GripshotFire()
    {
        if (isShootingAnimationPlaying) return;
        animator?.SetTrigger(fireTriggerName);
        isShootingAnimationPlaying = true;
        Debug.Log("Gripshot Fired!");
    }

    public void FinishFireAnimation() => isShootingAnimationPlaying = false;

    public override void OnItemChanged(GameObject newItem)
    {
        base.OnItemChanged(newItem);
    }

    public void NotifyHookshotReturned()
    {
        //Debug.Log("notified");
        isHookshotFiring = false;

        if (AimManager.Instance != null)
        {
            AimManager.Instance.isHookshotFiring = false;

            // Check if the aim button is still held
            bool aimHeld = false;

            if (AimManager.Instance.playerInput != null)
            {
                var actions = AimManager.Instance.playerInput.actions;
                if (actions != null && actions["Aim"] != null)
                    aimHeld = actions["Aim"].IsPressed(); // <-- direct hardware state
            }

            if (!aimHeld)
            {
                AimManager.Instance.ForceAimExit();
            }
            else
            {
                //AimManager.Instance.ChooseModeByCurrentItem();
                AimManager.Instance.SetActiveMode(this);
            }
        }

        isShootingAnimationPlaying = false;
    }

    public void TemporarilyDeactivate()
    {
        // Do the cleanup EXCEPT destroying the item

        // Disable visuals
        heldHandler.heldObj.SetActive(false); // Hide the hookshot object

        // Cleanup input
        if (playerInput != null)
        {
            var useAction = playerInput.actions["Use"];
            if (useAction != null)
                useAction.performed -= HandleUse;
        }

        // Cleanup Aim Target
        if (aimTargetInstance != null)
            aimTargetInstance.SetActive(false);

        // Reset animator if necessary, but keep item alive!
        if (animator)
        {
            animator.CrossFade("UpperBodyIdle", 0.2f, 2);
            animator.SetBool("IsShootingAiming", false);
        }
    }
}