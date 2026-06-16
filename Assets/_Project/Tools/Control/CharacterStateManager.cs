using UnityEngine;
using Game.PlayerV2;

/// <summary>
/// Thin control arbiter that locks/unlocks the player for tool actions (e.g. firing the
/// hookshot). Its old responsibilities — freezing movement/camera, zeroing input and the
/// locomotion animator — are now owned by the PlayerV2 ExternalControl state, reached via
/// <see cref="IControlLock"/>. This component only forwards the lock and pauses item cycling.
/// </summary>
public class CharacterStateManager : MonoBehaviour
{
    private IControlLock controlLock;
    private CycleItems cycleItems;

    private void Awake()
    {
        controlLock = GetComponentInParent<IControlLock>();
        cycleItems = GetComponent<CycleItems>();

        if (controlLock == null)
            Debug.LogError("CharacterStateManager requires a PlayerV2 controller implementing IControlLock on the player hierarchy.");
    }

    /// Hand control to an external system (enters ExternalControl) and pause item cycling.
    public void LockCharacter()
    {
        controlLock?.RequestExternalControl();
        if (cycleItems != null)
            cycleItems.LockCycling(true);
    }

    /// Return control to the player and resume item cycling.
    public void UnlockCharacter()
    {
        controlLock?.ReleaseExternalControl();
        if (cycleItems != null)
            cycleItems.LockCycling(false);
    }
}
