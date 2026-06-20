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

        [Header("Bake (editor only)")]
        [Tooltip("High-poly painted bake mesh (the EditorOnly child) the bake tool parses into holds. " +
                 "Leave null to auto-find a child MeshFilter tagged EditorOnly. Unused at runtime " +
                 "(the EditorOnly child is stripped from builds).")]
        [SerializeField] private MeshFilter bakeMeshFilter;

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

        /// <summary>
        /// The bake mesh the editor bake tool parses. Resolution order: explicit <see cref="bakeMeshFilter"/>
        /// → a child tagged <c>EditorOnly</c> (the real pipeline) → a MeshFilter on this object → the first
        /// child MeshFilter (convenience for simple/test surfaces). Editor-only.
        /// </summary>
        public MeshFilter ResolveBakeMesh()
        {
            if (bakeMeshFilter != null) return bakeMeshFilter;

            var filters = GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
                if (filters[i] != null && filters[i].gameObject.CompareTag("EditorOnly")) return filters[i];

            MeshFilter self = GetComponent<MeshFilter>();
            if (self != null) return self;

            return filters.Length > 0 ? filters[0] : null;
        }

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

#if UNITY_EDITOR
        // Preview the baked holds in the Scene view (select the surface) — verify a bake without play mode.
        // Yellow dot = hold, cyan ray = outward grab normal, green ray = surface up.
        private void OnDrawGizmosSelected()
        {
            if (holdData == null || holdData.holds == null) return;
            var hs = holdData.holds;
            for (int i = 0; i < hs.Length; i++)
            {
                Vector3 wp = transform.TransformPoint(hs[i].LocalPosition);
                Quaternion wr = transform.rotation * hs[i].LocalRotation;
                Gizmos.color = Color.yellow;
                Gizmos.DrawSphere(wp, 0.04f);
                Gizmos.color = Color.cyan;
                Gizmos.DrawRay(wp, (wr * Vector3.forward) * 0.2f);
                Gizmos.color = Color.green;
                Gizmos.DrawRay(wp, (wr * Vector3.up) * 0.15f);
            }
        }
#endif
    }
}
