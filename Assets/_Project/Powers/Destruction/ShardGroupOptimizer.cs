using UnityEngine;
using System.Collections.Generic;

public class ShardGroupOptimizer : MonoBehaviour
{
    [Header("Sleep Settings")]
    public float velocityThreshold = 0.05f;        // Min speed to count as moving
    public float angularVelocityThreshold = 1f;    // Min spin to count as moving
    public float stillTimeRequired = 3f;           // How long shard must stay still

    private List<Rigidbody> shardBodies = new List<Rigidbody>();
    private Dictionary<Rigidbody, float> stillTimers = new Dictionary<Rigidbody, float>();

    void Awake()
    {
        // Collect all rigidbodies in children
        shardBodies.AddRange(GetComponentsInChildren<Rigidbody>());

        foreach (var rb in shardBodies)
        {
            if (!stillTimers.ContainsKey(rb))
                stillTimers.Add(rb, 0f);
        }
    }

    void FixedUpdate()
    {
        for (int i = shardBodies.Count - 1; i >= 0; i--)
        {
            Rigidbody rb = shardBodies[i];
            if (rb == null) continue;

            // Check velocity thresholds
            if (rb.linearVelocity.sqrMagnitude < velocityThreshold * velocityThreshold &&
                rb.angularVelocity.sqrMagnitude < angularVelocityThreshold * angularVelocityThreshold)
            {
                stillTimers[rb] += Time.fixedDeltaTime;

                if (stillTimers[rb] >= stillTimeRequired)
                {
                    // Remove Rigidbody, leave collider
                    Destroy(rb);
                    shardBodies.RemoveAt(i);
                    stillTimers.Remove(rb);
                }
            }
            else
            {
                // Reset timer if it moves again
                stillTimers[rb] = 0f;
            }
        }

        // If all shards are static, no need to keep running
        if (shardBodies.Count == 0)
            Destroy(this);
    }
}
