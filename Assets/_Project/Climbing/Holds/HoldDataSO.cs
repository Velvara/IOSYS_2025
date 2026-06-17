using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing
{
    /// <summary>
    /// Baked, serialized hold set for one climbable surface — one asset per <see cref="ClimbableSurface"/>
    /// (see CLIMB_SURFACE_CREATION_SETUP). Produced by the editor bake for authored cliffs and
    /// auto-assigned to the surface. Holds are stored in the climbable's local space; the runtime
    /// parent transform is applied when a hold is streamed/placed.
    ///
    /// Procedural surfaces (e.g. Flora trunks) do NOT use this asset — they push holds directly at
    /// generation time via <see cref="IClimbableMeshConsumer.ReceiveHolds"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "ClimbHolds", menuName = "Climbing/Hold Data")]
    public class HoldDataSO : ScriptableObject
    {
        [Tooltip("Baked handholds in the climbable's local space.")]
        public ClimbHoldData[] holds = System.Array.Empty<ClimbHoldData>();

        public int Count => holds != null ? holds.Length : 0;
    }
}
