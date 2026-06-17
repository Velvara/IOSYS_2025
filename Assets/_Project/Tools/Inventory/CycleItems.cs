using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Game.PlayerV2;

public class CycleItems : MonoBehaviour
{
    public event Action<GameObject> OnItemChangedEvent;

    [SerializeField] private int currentIndex = 0;
    public int CurrentIndex => currentIndex;

    [Header("Inventory")]
    public List<GameObject> prefabs = new List<GameObject>();

    [Header("Current Selected Item")]
    public GameObject currentPrefab;

    private bool cyclingLocked = false;
    private IControlLock controlLock;

    void Start()
    {
        controlLock = GetComponentInParent<IControlLock>();

        if (prefabs.Count > 0)
        {
            currentPrefab = prefabs[0];
        }
        else
        {
            Debug.LogWarning("No prefabs assigned to the list.");
        }

        // Hook up PlayerInput actions
        var playerInput = GetComponent<PlayerInput>();
        if (playerInput != null)
        {
            var actions = playerInput.actions;
            if (actions != null)
            {
                if (actions["Next Item"] != null)
                    actions["Next Item"].performed += ctx => { if (CanCycle()) CycleNext(); };
                if (actions["Previous Item"] != null)
                    actions["Previous Item"].performed += ctx => { if (CanCycle()) CyclePrevious(); };
            }
        }
        else
        {
            Debug.LogWarning("PlayerInput component not found on CycleItems GameObject.");
        }

        // initial notification
        OnItemChangedEvent?.Invoke(currentPrefab);
    }

    public void LockCycling(bool locked)
    {
        cyclingLocked = locked;
    }

    /// <summary>
    /// Cycling is allowed when not explicitly locked AND no external system (climbing, cutscene,
    /// hookshot drag) holds control of the character.
    /// </summary>
    private bool CanCycle()
    {
        return !cyclingLocked && (controlLock == null || !controlLock.IsExternalControlActive);
    }

    private void CycleNext()
    {
        if (prefabs.Count == 0) return;
        currentIndex = (currentIndex + 1) % prefabs.Count;
        UpdateCurrentPrefab();
    }

    private void CyclePrevious()
    {
        if (prefabs.Count == 0) return;
        currentIndex = (currentIndex - 1 + prefabs.Count) % prefabs.Count;
        UpdateCurrentPrefab();
    }

    void UpdateCurrentPrefab()
    {
        currentPrefab = prefabs[currentIndex];
        //Debug.Log("Current Prefab: " + currentPrefab.name);
        OnItemChangedEvent?.Invoke(currentPrefab);
    }
}


