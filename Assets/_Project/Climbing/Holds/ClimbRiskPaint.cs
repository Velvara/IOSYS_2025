using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// Per-instance slip-risk paint storage: one <see cref="ClimbRiskClass"/> byte per hold of the
    /// surface's baked SO. This is a SEPARATE component (added to the scene instance by the risk
    /// painter) rather than a field on ClimbableSurface on purpose: ClimbableSurface ships inside
    /// prefabs, and a large array field on a prefab INSTANCE serializes as one PropertyModification
    /// override PER ELEMENT — on ~21k-hold kit-bash surfaces that override list stalled and crashed
    /// the editor on every stroke commit and scene save. An added component serializes wholesale in
    /// the scene file instead (a single added-object entry). For the same reason, do NOT apply this
    /// component to the prefab asset — let the painter add it per placement (it warns otherwise).
    /// </summary>
    [DisallowMultipleComponent]
    public class ClimbRiskPaint : MonoBehaviour
    {
        [SerializeField, HideInInspector] private byte[] classes;

        /// <summary>One ClimbRiskClass byte per hold (parallel to the surface's hold list). The
        /// risk painter owns creating/resizing this; ClimbableSurface only reads it.</summary>
        public byte[] Classes
        {
            get => classes;
            set => classes = value;
        }

        /// <summary>EDITOR — bumped by the risk painter after every change to <see cref="Classes"/>
        /// (the array is mutated in place, so reference checks can't see strokes). The surface's
        /// selected-preview mesh watches it to recolour live while painting. Not serialized.</summary>
        [System.NonSerialized] public int PaintVersion;
    }
}
