using System.Collections.Generic;
using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing
{
    /// <summary>
    /// Marks a GameObject as climbable and holds the per-surface config + hold source. Authored
    /// cliffs reference a baked <see cref="HoldDataSO"/>; procedural surfaces (e.g. Flora trunks)
    /// push holds in at generation time via <see cref="IClimbableMeshConsumer.ReceiveHolds"/>.
    ///
    /// Runtime physics/casts use the (low-poly) collider on this object; the high-poly painted
    /// bake mesh is editor-only and stripped from builds. Hold detection finds surfaces by
    /// looking for this component, so its presence is what makes geometry climbable.
    /// </summary>
    [DisallowMultipleComponent]
    public class ClimbableSurface : MonoBehaviour, IClimbableMeshConsumer
    {
        [Header("Hold Data")]
        [Tooltip("Baked hold set for authored surfaces. Leave null for procedural surfaces that " +
                 "push holds at runtime (e.g. Flora trunks).")]
        [SerializeField] private HoldDataSO holdData;

        [Header("Risk (used by the bake / tumble roll)")]
        [Tooltip("Risk used for a hold that has no vertex-paint information.")]
        [SerializeField] private float fallbackRisk = 0.05f;
        [Tooltip("Overrides the global wet state — for indoor/sheltered surfaces (weather, later).")]
        [SerializeField] private bool alwaysDry = false;
        [Tooltip("Tumble risk for fully red-painted vertices.")]
        [SerializeField] private float redRisk = 0.5f;
        [Tooltip("Tumble risk for fully green-painted vertices.")]
        [SerializeField] private float greenRisk = 0.1f;
        [Tooltip("Tumble risk for fully blue-painted vertices.")]
        [SerializeField] private float blueRisk = 0.25f;

        [Header("Streaming")]
        [Tooltip("Holds within this radius of the player stream in around the character.")]
        [SerializeField] private float searchRadius = 12f;

        // Runtime hold set: from holdData (authored) or pushed via ReceiveHolds (procedural).
        private IReadOnlyList<ClimbHoldData> _holds = System.Array.Empty<ClimbHoldData>();
        private bool _holdsReady;

        // -- Public read surface (consumed by the streamer / controller / bake) --
        public float FallbackRisk => fallbackRisk;
        public bool AlwaysDry => alwaysDry;
        public float RedRisk => redRisk;
        public float GreenRisk => greenRisk;
        public float BlueRisk => blueRisk;
        public float SearchRadius => searchRadius;
        public IReadOnlyList<ClimbHoldData> Holds => _holds;
        public bool HoldsReady => _holdsReady;

        private void Awake()
        {
            if (holdData != null && holdData.Count > 0)
            {
                _holds = holdData.holds;
                _holdsReady = true;
            }
        }

        /// <summary>
        /// <see cref="IClimbableMeshConsumer"/>: a procedural mesh producer (e.g. TrunkGenerator on
        /// growth-complete) hands us a finished hold set. Stores it and marks the surface ready.
        /// </summary>
        public void ReceiveHolds(IReadOnlyList<ClimbHoldData> holds)
        {
            _holds = holds ?? System.Array.Empty<ClimbHoldData>();
            _holdsReady = _holds.Count > 0;
        }
    }
}
