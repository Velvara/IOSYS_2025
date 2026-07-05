using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace StarterAssets
{
    /// <summary>
    /// Horizontal item-carousel HUD widget: the SELECTED item sits in the center, neighbours fan
    /// out left/right with opacity falling off toward the edges, and the strip wraps continuously
    /// as the player cycles. Always renders the full <see cref="VisibleCount"/> slots — when the
    /// inventory is smaller, the remaining ring positions show the NONE slot (empty hands), which
    /// also lives at the loop seam as a selectable stop (CycleItems.IsNoneSelected). No item ever
    /// appears twice.
    ///
    /// The LOOK lives in ItemCarousel.uxml / ItemCarousel.uss (open them in UI Builder — bar
    /// position, slot sizes, spacing, badge styling, the None-slot icon are all USS). This
    /// controller only drives BEHAVIOR: it finds the "item-carousel" container in the HUD
    /// document, pools slot elements, applies dynamic state (icons, wrap window, edge-opacity
    /// falloff, center class, count badges) and slides the strip one slot-pitch on selection
    /// changes (size/opacity smoothing is USS transitions). Event-driven only — no per-frame work
    /// outside an active tween.
    ///
    /// Slot classes: .carousel-slot, .carousel-slot--center, .carousel-slot--none (set its
    /// background-image in USS for the None icon), .carousel-slot--missing (item prefab lacks an
    /// ItemIcon sprite; a .carousel-name label shows the prefab name), .carousel-badge.
    /// </summary>
    public class ItemCarouselController : MonoBehaviour
    {
        [Tooltip("The UIDocument that owns the HUD (source asset must be HUD.uxml, which instances the ItemCarousel template).")]
        [SerializeField] private UIDocument _document;

        [Header("Behavior (the look lives in ItemCarousel.uss)")]
        [Tooltip("How many slots are always visible (center = selected). Even values round UP to the next odd. Ring positions beyond the inventory size show the None slot.")]
        public int VisibleCount = 5;

        [Tooltip("Opacity of the outermost slots; slots in between blend toward the center's 1.")]
        [Range(0f, 1f)]
        public float EdgeOpacity = 0.15f;

        [Tooltip("Seconds the strip slides on a selection change (0 = snap). Slot size/opacity changes are smoothed separately by the USS transition on .carousel-slot.")]
        public float TweenDuration = 0.12f;

        [Tooltip("Show a count badge on items with a finite count (consumables). Infinite items show none.")]
        public bool ShowCounts = true;

        private CycleItems _items;
        private VisualElement _bar;
        private readonly List<VisualElement> _slots = new List<VisualElement>();
        private readonly List<Label> _badges = new List<Label>();
        private readonly List<Label> _names = new List<Label>();

        private int _lastSelectable = -1;            // previous selection on the SELECTABLE ring (items + None)
        private ValueAnimation<float> _slide;

        private void Awake()
        {
            if (_document == null)
            {
                Debug.LogError("ItemCarouselController: no UIDocument assigned.", this);
                return;
            }

            _bar = _document.rootVisualElement.Q<VisualElement>("item-carousel");
            if (_bar == null)
                Debug.LogError("ItemCarouselController: no 'item-carousel' element in the document. " +
                               "Set the UIDocument's Source Asset to HUD.uxml (it instances the " +
                               "ItemCarousel template).", this);
        }

        private void OnDestroy() => Unsubscribe();

        /// <summary>Binds the widget to the player's inventory (called by HUDController).</summary>
        public void Bind(CycleItems items)
        {
            Unsubscribe();
            _items = items;
            if (_items != null)
            {
                _items.OnItemChangedEvent += OnItemChanged;
                _items.OnInventoryChangedEvent += OnInventoryChanged;
            }
            else
            {
                Debug.LogWarning("ItemCarouselController: Bind() called with null CycleItems.", this);
            }
            _lastSelectable = CurrentSelectable();
            Rebuild();
        }

        private void Unsubscribe()
        {
            if (_items == null) return;
            _items.OnItemChangedEvent -= OnItemChanged;
            _items.OnInventoryChangedEvent -= OnInventoryChanged;
        }

        /// <summary>Selection position on the selectable ring: item index, or N for the None stop.</summary>
        private int CurrentSelectable()
        {
            if (_items == null) return 0;
            return _items.IsNoneSelected ? _items.prefabs.Count : _items.CurrentIndex;
        }

        /// <summary>Selection change: re-render, then slide one slot-pitch in the travel direction.</summary>
        private void OnItemChanged(GameObject _)
        {
            int ring = (_items != null ? _items.prefabs.Count : 0) + 1;   // items + the None stop
            int newSel = CurrentSelectable();
            int dir = 0;
            if (_lastSelectable >= 0 && newSel != _lastSelectable && ring > 1)
            {
                int d = ((newSel - _lastSelectable) % ring + ring) % ring;
                if (d > ring / 2) d -= ring;                              // shortest way around the loop
                dir = d > 0 ? 1 : -1;
            }
            _lastSelectable = newSel;
            Rebuild();
            StartSlide(dir);
        }

        /// <summary>Inventory contents changed (consume/remove): re-render without a slide.</summary>
        private void OnInventoryChanged()
        {
            _lastSelectable = CurrentSelectable();
            Rebuild();
        }

        /// <summary>
        /// Re-renders the strip. The display ring = the items plus enough None entries to fill the
        /// window (at least one — None is also the selectable loop-seam stop), so all VisibleCount
        /// slots are always populated and nothing ever duplicates.
        /// </summary>
        private void Rebuild()
        {
            if (_bar == null) return;
            if (_items == null)
            {
                _bar.style.display = DisplayStyle.None;
                return;
            }
            _bar.style.display = DisplayStyle.Flex;

            int itemCount = _items.prefabs.Count;
            int visible = Mathf.Max(1, VisibleCount) | 1;              // even → next odd (center exists)
            int nonePad = Mathf.Max(1, visible - itemCount);           // ring filler; ≥1 = the None stop
            int ringSize = itemCount + nonePad;

            while (_slots.Count < visible) CreateSlot();
            while (_slots.Count > visible)
            {
                _bar.Remove(_slots[_slots.Count - 1]);
                _slots.RemoveAt(_slots.Count - 1);
                _badges.RemoveAt(_badges.Count - 1);
                _names.RemoveAt(_names.Count - 1);
            }

            // Selected ring position: the item, or the middle of the None block (so a selected
            // None sits centered with the last item to its left and the first to its right).
            int selPos = _items.IsNoneSelected ? itemCount + (nonePad - 1) / 2 : _items.CurrentIndex;
            float halfSpan = (visible - 1) * 0.5f;

            for (int i = 0; i < visible; i++)
            {
                int offset = i - (visible - 1) / 2;                    // 0 = center = selected
                int pos = ((selPos + offset) % ringSize + ringSize) % ringSize;
                bool isNone = pos >= itemCount;
                bool isCenter = offset == 0;

                VisualElement slot = _slots[i];
                slot.EnableInClassList("carousel-slot--center", isCenter);
                slot.EnableInClassList("carousel-slot--none", isNone);
                slot.style.opacity = halfSpan > 0f
                    ? Mathf.Lerp(1f, EdgeOpacity, Mathf.Abs(offset) / halfSpan)
                    : 1f;

                Label nameLabel = _names[i];
                Label badge = _badges[i];

                if (isNone)
                {
                    // None slot: the icon comes entirely from the .carousel-slot--none USS class —
                    // so CLEAR the inline background-image (StyleKeyword.Null = defer to USS).
                    // An inline "none" here would override the class image assigned in UI Builder.
                    slot.EnableInClassList("carousel-slot--missing", false);
                    slot.style.backgroundImage = StyleKeyword.Null;
                    nameLabel.style.display = DisplayStyle.None;
                    badge.style.display = DisplayStyle.None;
                    continue;
                }

                GameObject prefab = _items.prefabs[pos];
                ItemIcon icon = prefab != null ? prefab.GetComponent<ItemIcon>() : null;
                bool hasIcon = icon != null && icon.icon != null;
                slot.EnableInClassList("carousel-slot--missing", !hasIcon);
                slot.style.backgroundImage = hasIcon
                    ? new StyleBackground(icon.icon)
                    : new StyleBackground(StyleKeyword.None);

                nameLabel.style.display = hasIcon ? DisplayStyle.None : DisplayStyle.Flex;
                if (!hasIcon) nameLabel.text = prefab != null ? prefab.name : "?";

                int count = _items.GetCount(pos);
                bool showBadge = ShowCounts && count >= 0;
                badge.style.display = showBadge ? DisplayStyle.Flex : DisplayStyle.None;
                if (showBadge) badge.text = count.ToString();
            }
        }

        /// <summary>Slides the (already re-rendered) strip in from one slot-pitch away, so the new
        /// selection appears to glide into the center. Size/opacity morphs ride USS transitions.</summary>
        private void StartSlide(int dir)
        {
            if (dir == 0 || TweenDuration <= 0f || _slots.Count == 0 || _bar == null) return;
            float pitch = _slots[0].resolvedStyle.width
                        + _slots[0].resolvedStyle.marginLeft
                        + _slots[0].resolvedStyle.marginRight;
            if (float.IsNaN(pitch) || pitch <= 0f) return;             // first layout not resolved yet

            if (_slide != null && _slide.isRunning) _slide.Stop();     // pooled — only stop a live one
            _bar.style.translate = new Translate(dir * pitch, 0f);     // start where the old frame was
            _slide = _bar.experimental.animation
                .Start(dir * pitch, 0f, Mathf.Max(1, Mathf.RoundToInt(TweenDuration * 1000f)),
                       (e, v) => e.style.translate = new Translate(v, 0f))
                .Ease(Easing.OutCubic);
        }

        private void CreateSlot()
        {
            var slot = new VisualElement { pickingMode = PickingMode.Ignore };
            slot.AddToClassList("carousel-slot");

            var nameLabel = new Label { pickingMode = PickingMode.Ignore };
            nameLabel.AddToClassList("carousel-name");
            nameLabel.style.display = DisplayStyle.None;
            slot.Add(nameLabel);
            _names.Add(nameLabel);

            var badge = new Label { pickingMode = PickingMode.Ignore };
            badge.AddToClassList("carousel-badge");
            badge.style.display = DisplayStyle.None;
            slot.Add(badge);
            _badges.Add(badge);

            _bar.Add(slot);
            _slots.Add(slot);
        }
    }
}
