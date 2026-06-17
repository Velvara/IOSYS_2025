using System.Collections.Generic;
using UnityEngine;

namespace Game.Core.Climbing
{
    /// <summary>
    /// One handhold, expressed in the climbable's local space (the runtime parent transform is
    /// applied when the hold is streamed/placed). Shared contract type living in Game.Core so a
    /// procedural mesh producer (e.g. Flora's TrunkGenerator in Game.Powers) and the consumer
    /// (ClimbableSurface in Game.Climbing) can exchange holds without an assembly cycle.
    /// </summary>
    [System.Serializable]
    public struct ClimbHoldData
    {
        /// <summary>Local-space position relative to the climbable's transform.</summary>
        public Vector3 LocalPosition;

        /// <summary>Local-space orientation: up = surface up, forward = outward (grab) normal.</summary>
        public Quaternion LocalRotation;

        /// <summary>Tumble-roll risk for this hold (0..1). Trunks use ClimbableSurface.fallbackRisk.</summary>
        public float RiskValue;

        /// <summary>Which world-space icon PNG to show for this hold. 0 = none / fallback.</summary>
        public byte IconId;
    }

    /// <summary>
    /// Implemented by a climbable component (e.g. ClimbableSurface) so a procedural mesh producer
    /// can hand it a finished hold set at generation time — e.g. a Flora trunk calling this in its
    /// growth-complete finalize step. Keeps Game.Powers free of any Game.Climbing reference.
    /// </summary>
    public interface IClimbableMeshConsumer
    {
        void ReceiveHolds(IReadOnlyList<ClimbHoldData> holds);
    }
}
