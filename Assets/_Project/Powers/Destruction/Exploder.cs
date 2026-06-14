using UnityEngine;

public class Exploder : MonoBehaviour
{
    [Header("Explosion Settings")]
    public float explosionMagnitude = 5f;
    public GameObject explosionFX;

    [Header("Tags")]
    public Tags breakableTag; // assign the "breakable" ScriptableObject in inspector

    [Header("Physics Filtering")]
    public LayerMask explosionMask = ~0; // default: everything

    private void Start()
    {
        Explode();
    }

    private void Explode()
    {
        // spawn FX (optional)
        if (explosionFX != null)
            Instantiate(explosionFX, transform.position, Quaternion.identity);

        // find colliders in range
        Collider[] colliders = Physics.OverlapSphere(transform.position, explosionMagnitude, explosionMask);

        foreach (Collider col in colliders)
        {
            TagObjManager tagManager = col.GetComponent<TagObjManager>();
            if (tagManager != null && breakableTag != null && tagManager.HasTag(breakableTag))
            {
                BreakObject breaker = col.GetComponent<BreakObject>();
                if (breaker != null)
                {
                    breaker.Break(transform.position);
                }
            }
        }

        Destroy(gameObject);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, explosionMagnitude);
    }
}
