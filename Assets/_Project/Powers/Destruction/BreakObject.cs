using UnityEngine;

public class BreakObject : MonoBehaviour
{
    [Header("Broken Version Prefab")]
    public GameObject brokenPrefab;

    [Header("Explosion Force Settings")]
    public float explosionForce = 10f;

    [Tooltip("Extra impulse applied to all shards. This vector is LOCAL to the brokenPrefab's transform (parent of the shards).")]
    public Vector3 additionalForce = Vector3.zero;

    private bool isBroken = false;

    public void Break(Vector3 explosionOrigin)
    {
        if (isBroken) return;
        isBroken = true;

        if (brokenPrefab == null)
        {
            Debug.LogWarning($"BreakObject on '{gameObject.name}' has no brokenPrefab assigned.");
            Destroy(gameObject);
            return;
        }

        // instantiate broken version as a root (parent of shards)
        GameObject broken = Instantiate(brokenPrefab, transform.position, transform.rotation);

        // convert additionalForce (local) to world using the broken prefab's transform
        Vector3 additionalWorldForce = broken.transform.TransformDirection(additionalForce);

        // apply to every child rigidbody
        Rigidbody[] rbs = broken.GetComponentsInChildren<Rigidbody>();
        foreach (Rigidbody rb in rbs)
        {
            // compute radial direction (fallback if shard sits exactly at explosion origin)
            Vector3 toShard = rb.worldCenterOfMass - explosionOrigin;
            float dist = toShard.magnitude;

            Vector3 radialDir;
            if (dist > 1e-4f)
                radialDir = toShard / dist; // normalized
            else
                radialDir = broken.transform.forward; // fallback direction

            Vector3 radialImpulse = radialDir * explosionForce;

            // final impulse = radial outward impulse + additional (local->world) impulse
            Vector3 finalImpulse = radialImpulse + additionalWorldForce;

            rb.AddForce(finalImpulse, ForceMode.Impulse);
            //rb.AddForceAtPosition(finalImpulse, explosionOrigin, ForceMode.Impulse);
        }

        // destroy the intact object
        Destroy(gameObject);
    }
}

