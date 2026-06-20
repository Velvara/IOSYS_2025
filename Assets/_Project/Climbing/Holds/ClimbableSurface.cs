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

        /// <summary>Where this surface's holds came from — lets systems tell a procedural trunk from a parsed cliff.</summary>
        public enum ClimbHoldSource { None, Authored, Procedural }

        // Runtime hold set: from holdData (authored) or pushed via ReceiveHolds (procedural).
        private IReadOnlyList<ClimbHoldData> _holds = System.Array.Empty<ClimbHoldData>();
        private bool _holdsReady;
        private ClimbHoldSource _source = ClimbHoldSource.None;

        // -- Public read surface (consumed by the streamer / controller / bake) --
        public float FallbackRisk => fallbackRisk;
        public bool AlwaysDry => alwaysDry;
        public float RedRisk => redRisk;
        public float GreenRisk => greenRisk;
        public float BlueRisk => blueRisk;
        public float SearchRadius => searchRadius;
        public IReadOnlyList<ClimbHoldData> Holds => _holds;
        public bool HoldsReady => _holdsReady;
        /// <summary>Authored = parsed/baked cliff; Procedural = pushed at runtime (e.g. a Flora trunk).</summary>
        public ClimbHoldSource Source => _source;

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
                _source = ClimbHoldSource.Authored;
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
            _source = ClimbHoldSource.Procedural;
        }

#if UNITY_EDITOR
        // Preview holds in the Scene view (select the surface). In PLAY mode it draws the LIVE hold set, so
        // procedural Flora trunks (pushed via ReceiveHolds at growth-complete) show too; in EDIT mode it
        // falls back to the baked SO so an authored bake previews without entering play. Dot colour = source
        // (yellow = authored/parsed, magenta = procedural/trunk); cyan ray = outward grab normal, green = up.
        private void OnDrawGizmosSelected()
        {
            bool live = Application.isPlaying && _holdsReady;
            IReadOnlyList<ClimbHoldData> holds = live ? _holds : (holdData != null ? holdData.holds : null);
            if (holds == null) return;

            Color dot = (live && _source == ClimbHoldSource.Procedural) ? new Color(1f, 0.35f, 1f) : Color.yellow;
            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = transform.TransformPoint(holds[i].LocalPosition);
                Quaternion wr = transform.rotation * holds[i].LocalRotation;
                Gizmos.color = dot;
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
