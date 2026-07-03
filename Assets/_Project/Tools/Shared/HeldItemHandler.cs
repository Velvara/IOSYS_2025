using UnityEngine;

[System.Serializable]
public class HeldItemHandler
{
    public GameObject heldObj;

    public void SpawnHeldItem()
    {
        DestroyHeldItem();

        if (AimManager.Instance == null || AimManager.Instance.cycleItems == null) return;

        GameObject prefab = AimManager.Instance.cycleItems.currentPrefab;
        GameObject origin = AimManager.Instance.handSlot;

        if (prefab != null && origin != null)
        {
            heldObj = Object.Instantiate(prefab, origin.transform);

            // The held copy is purely cosmetic. Its colliders must be disabled: the real
            // projectile is instantiated exactly on top of it (handSlot) at the release
            // frame, and a live collider there makes the projectile hit it and self-destruct
            // (ThrowableObject.OnCollisionEnter destroys on any non-Player contact).
            foreach (Collider col in heldObj.GetComponentsInChildren<Collider>(true))
                col.enabled = false;
        }
    }

    public void DestroyHeldItem()
    {
        if (heldObj != null)
        {
            Object.Destroy(heldObj);
            heldObj = null;
        }

        // also clear children in case any leftovers remain
        GameObject origin = AimManager.Instance != null ? AimManager.Instance.handSlot : null;
        if (origin != null)
        {
            for (int i = origin.transform.childCount - 1; i >= 0; i--)
                Object.Destroy(origin.transform.GetChild(i).gameObject);
        }
    }
}
