using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

public class ScanAim : AimModeBase
{
    [Header("Input System (injected by AimManager)")]
    public PlayerInput playerInput;
    public StarterAssetsInputs starterInputs;

    public override void EnterMode()
    {
        base.EnterMode();

        var manager = AimManager.Instance;
        if (manager != null)
            manager.RequestCameraTransition(camHeight, camDist, camSide);

        if (animator)
        {
            animator.CrossFade("ScanPose", 0.15f, layer: 2);
            animator.SetBool("IsScanning", true);
        }

        if (playerInput != null && playerInput.actions["Use"] != null)
            playerInput.actions["Use"].performed += HandleUse;
    }

    public override void UpdateMode()
    {
        base.UpdateMode();

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
            animator.SetBool("IsScanning", false);
        }

        if (playerInput != null && playerInput.actions["Use"] != null)
            playerInput.actions["Use"].performed -= HandleUse;
    }

    private void HandleUse(InputAction.CallbackContext ctx)
    {
        if (AimManager.Instance != null && AimManager.Instance.IsAiming && AimManager.Instance.ActiveMode == this)
            Scan();
    }

    private void Scan()
    {
        Debug.Log("Scanning logic goes here.");
        // TODO: implement scan functionality
    }
}
