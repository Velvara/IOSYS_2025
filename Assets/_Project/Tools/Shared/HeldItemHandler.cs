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
