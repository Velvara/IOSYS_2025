using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Game.Climbing;
using Game.PlayerV2;

public class CycleItems : MonoBehaviour
{
    public event Action<GameObject> OnItemChangedEvent;

    /// <summary>Fired when the inventory CONTENTS change (an item consumed/removed or a count
    /// changed) — as opposed to OnItemChangedEvent, which fires on selection changes. The HUD
    /// carousel listens to both.</summary>
    public event Action OnInventoryChangedEvent;

    [SerializeField] private int currentIndex = 0;
    public int CurrentIndex => currentIndex;

    [Header("Inventory")]
    public List<GameObject> prefabs = new List<GameObject>();

    [Tooltip("Per-slot item count, parallel to Prefabs. -1 = infinite (normal tools). Consumables " +
             "(e.g. ropes) decrement via TryConsumeCurrent and are removed from the inventory at 0. " +
             "Auto-padded with -1 for any prefab without an entry.")]
    public List<int> counts = new List<int>();

    [Header("Current Selected Item")]
    public GameObject currentPrefab;

    private bool cyclingLocked = false;
    private IControlLock controlLock;
    private ClimbController climb;   // climbing is the one external-control state that allows cycling

    void Start()
    {
        controlLock = GetComponentInParent<IControlLock>();
        climb = GetComponentInParent<ClimbController>();
        EnsureCounts();

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
    /// Cycling is allowed when not explicitly locked AND no external system (cutscene, hookshot
    /// drag, rappel) holds control of the character — EXCEPT while CLIMBING, where cycling stays
    /// available (selecting the carbine mid-climb). Aiming/using tools on the wall stays blocked
    /// by AimManager's own external-control guard; only the selection moves.
    /// </summary>
    private bool CanCycle()
    {
        if (cyclingLocked) return false;
        if (controlLock == null || !controlLock.IsExternalControlActive) return true;
        return climb != null && climb.IsClimbing;
    }

    /// <summary>True while the virtual "None" slot (empty hands, nothing equipped) is selected.
    /// The cycle order is item0 … itemLast, None, item0 … — None sits at the loop seam, so it is
    /// always the "first/last" stop of the list.</summary>
    public bool IsNoneSelected => currentIndex >= prefabs.Count;

    private void CycleNext()
    {
        if (prefabs.Count == 0) return;
        currentIndex = (currentIndex + 1) % (prefabs.Count + 1);   // +1 = the None stop at the seam
        UpdateCurrentPrefab();
    }

    private void CyclePrevious()
    {
        if (prefabs.Count == 0) return;
        currentIndex = (currentIndex - 1 + prefabs.Count + 1) % (prefabs.Count + 1);
        UpdateCurrentPrefab();
    }

    void UpdateCurrentPrefab()
    {
        // None selected → null: AimManager picks no mode, HeldItemHandler spawns nothing.
        currentPrefab = IsNoneSelected ? null : prefabs[currentIndex];
        OnItemChangedEvent?.Invoke(currentPrefab);
    }

    /// <summary>Count for an inventory slot; -1 = infinite (not a consumable).</summary>
    public int GetCount(int index) =>
        index >= 0 && index < counts.Count ? counts[index] : -1;

    /// <summary>
    /// Consumes one unit of the SELECTED item (e.g. placing a rope). Infinite items (-1) succeed
    /// without decrementing. Reaching 0 removes the item from the inventory and selects the next
    /// one. Returns false only when there's nothing valid to consume.
    /// </summary>
    public bool TryConsumeCurrent()
    {
        if (prefabs.Count == 0 || currentPrefab == null) return false;
        EnsureCounts();

        int c = counts[currentIndex];
        if (c < 0) return true;          // infinite — nothing to book
        if (c == 0) return false;        // shouldn't exist (removed at 0) — refuse, don't go negative

        counts[currentIndex] = c - 1;
        if (counts[currentIndex] == 0)
        {
            prefabs.RemoveAt(currentIndex);
            counts.RemoveAt(currentIndex);
            if (prefabs.Count == 0)
            {
                currentPrefab = null;
                currentIndex = 0;
                OnItemChangedEvent?.Invoke(null);   // AimManager handles null (no mode)
            }
            else
            {
                currentIndex %= prefabs.Count;      // removing the last slot wraps to the first
                UpdateCurrentPrefab();
            }
        }
        OnInventoryChangedEvent?.Invoke();
        return true;
    }

    /// <summary>Keeps the counts list parallel to prefabs (missing entries = -1 / infinite).</summary>
    private void EnsureCounts()
    {
        while (counts.Count < prefabs.Count) counts.Add(-1);
        if (counts.Count > prefabs.Count) counts.RemoveRange(prefabs.Count, counts.Count - prefabs.Count);
    }
}


