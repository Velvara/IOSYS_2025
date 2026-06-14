using StarterAssets;
using UnityEngine;

public class CharacterStateManager : MonoBehaviour
{
    private StarterAssets.ThirdPersonController thirdPersonController;
    private StarterAssetsInputs starterInputs;
    private float frozenCameraPitch;
    private CycleItems cycleItems;

    private void Awake()
    {
        // Get the required components on this GameObject (or its children/parent)
        thirdPersonController =
            GetComponent<StarterAssets.ThirdPersonController>();
        starterInputs = GetComponent<StarterAssetsInputs>();
        cycleItems = GetComponent<CycleItems>();

        if (thirdPersonController == null)
            Debug.LogError("CharacterStateManager requires a ThirdPersonController.");
        if (starterInputs == null)
            Debug.LogError("CharacterStateManager requires StarterAssetsInputs.");
    }

    /// Disables TPC movement and clears input buffers.
    /// This should be called the moment the hookshot is fired or the player is in a locked state.
    public void LockCharacter()
    {
        if (thirdPersonController != null)
        {
            // freeze movement and camera
            thirdPersonController.FreezeCharacter(true, true);
            thirdPersonController.enabled = false;
        }

        if (starterInputs != null)
        {
            starterInputs.MoveInput(Vector2.zero);
            starterInputs.LookInput(Vector2.zero);
            starterInputs.JumpInput(false);
            starterInputs.SprintInput(false);
            starterInputs.AimInput(false);
        }

        var animator = GetComponent<Animator>();
        if (animator != null)
        {
            animator.SetFloat("Speed", 0f);
            animator.SetFloat("MotionSpeed", 0f);
            animator.SetFloat("AimMoveX", 0f);
            animator.SetFloat("AimMoveY", 0f);
        }

        if (cycleItems != null)
            cycleItems.LockCycling(true);
    }

    public void UnlockCharacter()
    {
        if (thirdPersonController != null)
        {
            thirdPersonController.enabled = true;
            thirdPersonController.FreezeCharacter(false, false);
        }
        if (starterInputs != null && AimManager.Instance != null && AimManager.Instance.playerInput != null)
        {
            var actions = AimManager.Instance.playerInput.actions;
            if (actions != null && actions["Move"] != null)
            {
                Vector2 currentMove = actions["Move"].ReadValue<Vector2>();
                starterInputs.MoveInput(currentMove);  // restore the real stick/keys state
            }

            if (actions != null && actions["Sprint"] != null)
            {
                bool sprintHeld = actions["Sprint"].IsPressed();
                starterInputs.SprintInput(sprintHeld);
            }

        }

        if (cycleItems != null)
            cycleItems.LockCycling(false);
    }
}

