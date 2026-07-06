using UnityEngine;

/// <summary>
/// Marker + per-item config for the rope anchor inventory item. AimManager routes the
/// current item to RopeAim when this component is present on the held prefab (same
/// pattern as ThrowableObject → ThrowAim / ScanTool → ScanAim).
/// </summary>
[DisallowMultipleComponent]
public class RopeItem : MonoBehaviour
{
    [Tooltip("Total rope length available once this anchor is placed (meters).")]
    public float ropeLength = 20f;
}
