using UnityEngine;

/// <summary>
/// Marks an inventory prefab (CycleItems slot) as the CARBINE consumable and carries its config.
/// Item selection is prefab-based, so CarbineController recognises "carbine selected" by finding
/// this component on <c>CycleItems.currentPrefab</c>. Give the inventory entry a finite count —
/// placement consumes one via TryConsumeCurrent.
/// </summary>
public class CarbineItem : MonoBehaviour
{
    [Tooltip("World prefab spawned at the right hand's hold on placement (the visible carbine). Its " +
             "renderer materials should expose the dissolve property (CarbineController.dissolveProperty) " +
             "for the detach fade-out.")]
    public GameObject placedPrefab;

    [Tooltip("Rope length (m) of the tether running from this carbine to the player's hip slot.")]
    public float ropeLength = 15f;

    [Tooltip("Name of the child on placedPrefab where the rope attaches. Empty or missing = the prefab root.")]
    public string ropePointChildName = "RopePoint";
}
