using UnityEngine;

[RequireComponent(typeof(Renderer))]
public class UVScroller : MonoBehaviour
{
    [Tooltip("UV tiling factor along rope length (higher = slower texture repeat).")]
    public float uvScale = 1f;

    private Renderer rend;
    private Material mat;
    private Vector2 uvOffset;

    private Vector3 lastTipPos;
    private Transform tipTransform;

    private void Awake()
    {
        rend = GetComponent<Renderer>();
        mat = rend.material; // unique instance
        uvOffset = Vector2.zero;
    }

    private void Update()
    {
        if (tipTransform == null) return;

        // Measure movement along rope direction
        float delta = Vector3.Distance(tipTransform.position, lastTipPos);
        uvOffset.y -= delta * uvScale;

        // Wrap to avoid float drift
        uvOffset.y = Mathf.Repeat(uvOffset.y, 1f);

        mat.mainTextureOffset = uvOffset;

        lastTipPos = tipTransform.position;
    }

    /// <summary>
    /// Call once when rope is spawned, passing the tip transform.
    /// </summary>
    public void AttachTip(Transform tip)
    {
        tipTransform = tip;
        lastTipPos = tip.position;
    }

    private void OnDestroy()
    {
        if (mat != null)
            mat.mainTextureOffset = Vector2.zero;
    }
}
