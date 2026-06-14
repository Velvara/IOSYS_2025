using UnityEngine;
using UnityEngine.UI;
using System.Collections.Generic;

public class InventoryBarUI : MonoBehaviour
{
    public CycleItems cycleItems;
    public RectTransform slotContainer; // parent object to hold slots (HorizontalLayoutGroup)
    public GameObject slotPrefab; // prefab: an Image with an Outline or Highlight child
    private List<GameObject> slots = new List<GameObject>();

    void Start()
    {
        if (cycleItems == null) return;
        BuildSlots();
        cycleItems.OnItemChangedEvent += OnItemChanged;
    }

    void BuildSlots()
    {
        // clear
        foreach (Transform t in slotContainer) Destroy(t.gameObject);
        slots.Clear();

        for (int i = 0; i < cycleItems.prefabs.Count; i++)
        {
            GameObject s = Instantiate(slotPrefab, slotContainer);
            // slot prefab should have an Image component to show the icon.
            var img = s.GetComponent<Image>();
            // Optionally, get a sprite from a component on the prefab (e.g., ItemIcon sprite)
            var item = cycleItems.prefabs[i];
            var iconComp = item != null ? item.GetComponent<ItemIcon>() : null;
            if (iconComp != null && iconComp.icon != null)
                img.sprite = iconComp.icon;
            slots.Add(s);
        }
        UpdateHighlight();
    }

    void OnItemChanged(GameObject newItem)
    {
        UpdateHighlight();
    }

    void UpdateHighlight()
    {
        if (cycleItems == null) return;
        for (int i = 0; i < slots.Count; i++)
        {
            var outline = slots[i].GetComponent<Outline>(); // or a custom highlighter
            if (i == cycleItems.CurrentIndex) // make currentIndex public if needed; otherwise compare prefabs
                outline.enabled = true;
            else
                outline.enabled = false;
        }
    }
}
