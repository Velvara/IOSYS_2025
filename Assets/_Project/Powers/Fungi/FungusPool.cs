using UnityEngine;
using System.Collections.Generic;

public class FungusPool : MonoBehaviour
{
    [Header("Pooling Settings")]
    public List<GameObject> fungiObjects;  // list of different fungus prefabs
    public int maxPoolObjects = 1000;      // global pool budget

    // One queue per prefab
    private Dictionary<GameObject, Queue<GameObject>> pools = new Dictionary<GameObject, Queue<GameObject>>();
    private Dictionary<GameObject, int> poolSizes = new Dictionary<GameObject, int>();
    private Dictionary<GameObject, int> activeCounts = new Dictionary<GameObject, int>();

    public static FungusPool Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        InitializePools();
    }

    private void InitializePools()
    {
        if (fungiObjects == null || fungiObjects.Count == 0)
        {
            Debug.LogError("[FungusPool] No fungiObjects assigned!");
            return;
        }

        int perPrefab = Mathf.CeilToInt((float)maxPoolObjects / fungiObjects.Count);

        foreach (var prefab in fungiObjects)
        {
            var q = new Queue<GameObject>(perPrefab);
            pools[prefab] = q;
            poolSizes[prefab] = perPrefab;
            activeCounts[prefab] = 0;

            for (int i = 0; i < perPrefab; i++)
            {
                var obj = Instantiate(prefab, transform);
                obj.SetActive(false);
                q.Enqueue(obj);
            }
        }
    }

    public GameObject Get(GameObject prefab, Vector3 pos, Quaternion rot, Transform parent)
    {
        if (!pools.ContainsKey(prefab))
        {
            Debug.LogWarning($"[FungusPool] Prefab {prefab.name} not found in pools.");
            return null;
        }

        var q = pools[prefab];

        GameObject obj;
        if (q.Count > 0)
        {
            obj = q.Dequeue();
        }
        else
        {
            // Expand if under budget
            if (activeCounts[prefab] < poolSizes[prefab])
            {
                obj = Instantiate(prefab, transform);
            }
            else
            {
                Debug.LogWarning($"[FungusPool] Pool for {prefab.name} is exhausted!");
                return null;
            }
        }

        obj.transform.SetPositionAndRotation(pos, rot);
        obj.transform.SetParent(parent);
        obj.SetActive(true);

        activeCounts[prefab]++;
        return obj;
    }

    public void Return(GameObject prefab, GameObject obj)
    {
        if (prefab == null || obj == null) return;

        obj.SetActive(false);
        obj.transform.SetParent(transform);

        if (pools.ContainsKey(prefab))
        {
            pools[prefab].Enqueue(obj);
            activeCounts[prefab] = Mathf.Max(0, activeCounts[prefab] - 1);
        }
        else
        {
            Destroy(obj); // fallback if somehow not tracked
        }
    }

    // Allow other scripts to get the full prefab list
    public List<GameObject> GetFungiList()
    {
        return fungiObjects;
    }
    public List<GameObject> GetAvailablePrefabs()
    {
        List<GameObject> available = new List<GameObject>();
        foreach (var kvp in pools)
        {
            if (kvp.Value.Count > 0) // pool has something left
                available.Add(kvp.Key);
        }
        return available;
    }
}

