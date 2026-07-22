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

        [Header("Risk (slip roll — painted per instance, Free surfaces)")]
        [Tooltip("Slip chance (0..1) rolled when a hand LEAVES a green-painted hold.")]
        [SerializeField] private float greenRisk = 0.05f;
        [Tooltip("Slip chance (0..1) rolled when a hand LEAVES a blue-painted hold.")]
        [SerializeField] private float blueRisk = 0.25f;
        [Tooltip("Slip chance (0..1) rolled when a hand LEAVES a red-painted hold.")]
        [SerializeField] private float redRisk = 0.5f;
        [Tooltip("Class an unpainted (black) hold resolves to at runtime. Black here counts as Green.")]
        [SerializeField] private ClimbRiskClass blackFallback = ClimbRiskClass.Green;
        [Tooltip("Overrides the global wet state — for indoor/sheltered surfaces (weather, later).")]
        [SerializeField] private bool alwaysDry = false;

        // LEGACY per-instance paint storage — superseded by the ClimbRiskPaint sibling component
        // (a big array field HERE explodes into one prefab-override entry per element on painted
        // prefab instances, which stalled/crashed the editor on ~21k-hold surfaces; see
        // ClimbRiskPaint). Still read as a fallback so un-migrated scenes keep working; the risk
        // painter migrates + clears it on first contact. Delete once every painted scene is re-saved.
        [SerializeField, HideInInspector] private byte[] holdRiskClasses;

        private ClimbRiskPaint _paint;   // per-instance paint component (may be absent), cached in Awake

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
        public bool AlwaysDry => alwaysDry;
        public float RedRisk => redRisk;
        public float GreenRisk => greenRisk;
        public float BlueRisk => blueRisk;
        public ClimbRiskClass BlackFallback => blackFallback;
        public float SearchRadius => searchRadius;
        public IReadOnlyList<ClimbHoldData> Holds => _holds;
        public bool HoldsReady => _holdsReady;
        /// <summary>Authored = parsed/baked cliff; Procedural = pushed at runtime (e.g. a Flora trunk).</summary>
        public ClimbHoldSource Source => _source;

        /// <summary>How this surface climbs: Trunk (procedural Flora), Free (vertex-per-hold bake) or
        /// Ledge (edge/protrusion bake). Trunks classify by hold source; authored surfaces read the
        /// type stamped into their baked <see cref="HoldDataSO"/>. Per-type rules land later — this
        /// is the identity they'll hang off (the entry slide already keys on Free).</summary>
        public ClimbType Type => _source == ClimbHoldSource.Procedural
            ? ClimbType.Trunk
            : (holdData != null ? holdData.surfaceType : ClimbType.Free);

        /// <summary>Painted risk class of a hold (index into <see cref="Holds"/>). Black when this
        /// instance is unpainted or the paint no longer matches the hold set (stale re-bake).
        /// Reads the ClimbRiskPaint sibling component, falling back to the legacy field.</summary>
        public ClimbRiskClass RiskClassOf(int holdIndex)
        {
            byte[] arr = _paint != null ? _paint.Classes : holdRiskClasses;
            if (arr == null || arr.Length != _holds.Count || (uint)holdIndex >= (uint)arr.Length)
                return ClimbRiskClass.Black;
            return (ClimbRiskClass)arr[holdIndex];
        }

        /// <summary>Slip chance (0..1) of a hold — Black resolved through <see cref="BlackFallback"/>.</summary>
        public float Risk01(int holdIndex) => Risk01(RiskClassOf(holdIndex));

        /// <summary>Slip chance (0..1) for a class; Black resolves through the fallback (Green when
        /// the fallback itself was left on Black).</summary>
        public float Risk01(ClimbRiskClass riskClass)
        {
            if (riskClass == ClimbRiskClass.Black)
                riskClass = blackFallback == ClimbRiskClass.Black ? ClimbRiskClass.Green : blackFallback;
            switch (riskClass)
            {
                case ClimbRiskClass.Blue: return blueRisk;
                case ClimbRiskClass.Red: return redRisk;
                default: return greenRisk;
            }
        }

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
            TryGetComponent(out _paint);
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
        /// <summary>EDITOR — the baked SO (edit-mode hold source for the bake / risk-paint tools).</summary>
        public HoldDataSO EditorHoldData => holdData;

        /// <summary>EDITOR — LEGACY paint field access. Only the risk painter uses it, as the
        /// migration source: it moves the data onto the ClimbRiskPaint component and clears this.</summary>
        public byte[] EditorRiskClasses
        {
            get => holdRiskClasses;
            set => holdRiskClasses = value;
        }

        // Hold preview (select the surface). Per-hold immediate gizmos can't survive dense vertex
        // bakes (~21k holds froze the editor), and silently thinning them read as "the bake lost
        // holds" — so the preview is ONE batched mesh instead: a small quad per hold facing the
        // hold's outward normal, vertex-coloured by risk class (yellow = unpainted authored,
        // magenta = procedural/trunk), drawn in a single DrawMeshNow. GPU backface culling + the
        // scene depth test hide the far side of the surface, so only the holds actually facing the
        // camera render — and EVERY hold can always be drawn (2 tris each). The mesh is local-space
        // and rebuilt only when the hold set or the paint changes (ClimbRiskPaint.PaintVersion, so
        // strokes recolour live while painting); moving the surface costs nothing.
        private Mesh _previewMesh;
        private Material _previewMat;
        private object _previewHoldsRef;
        private object _previewPaintRef;
        private int _previewPaintVersion = -1;

        private const float PreviewQuadHalf = 0.03f;    // quad half-size (m)
        private const float PreviewQuadLift = 0.008f;   // sits this far off the surface (z-fighting)

        /// <summary>EDITOR — draws the batched hold-preview mesh. Called from OnDrawGizmosSelected
        /// AND by the risk painter every scene repaint (so the preview shows while painting even when
        /// the surface isn't the selection). Only call from draw-time contexts (gizmos / scene-view
        /// Repaint events) — it issues an immediate draw.</summary>
        public void EditorDrawHoldPreview()
        {
            bool live = Application.isPlaying && _holdsReady;
            IReadOnlyList<ClimbHoldData> holds = live ? _holds : (holdData != null ? holdData.holds : null);
            if (holds == null || holds.Count == 0) return;

            byte[] paintArr = null;
            TryGetComponent(out ClimbRiskPaint pc);
            if (pc != null && pc.Classes != null && pc.Classes.Length == holds.Count)
                paintArr = pc.Classes;
            else if (holdRiskClasses != null && holdRiskClasses.Length == holds.Count)
                paintArr = holdRiskClasses;   // legacy storage, pre-migration

            int paintVersion = pc != null ? pc.PaintVersion : 0;
            if (_previewMesh == null || !ReferenceEquals(_previewHoldsRef, holds) ||
                !ReferenceEquals(_previewPaintRef, paintArr) || _previewPaintVersion != paintVersion)
            {
                RebuildPreviewMesh(holds, paintArr, live);
                _previewHoldsRef = holds;
                _previewPaintRef = paintArr;
                _previewPaintVersion = paintVersion;
            }

            if (_previewMat == null)
            {
                Shader s = Shader.Find("Hidden/Climb/HoldPreview");
                if (s == null) s = Shader.Find("Sprites/Default");   // fallback: no backface cull, colours still right
                if (s == null) return;
                _previewMat = new Material(s) { hideFlags = HideFlags.HideAndDontSave };
            }
            _previewMat.SetPass(0);
            Graphics.DrawMeshNow(_previewMesh, transform.localToWorldMatrix);
        }

        private void OnDrawGizmosSelected()
        {
            EditorDrawHoldPreview();

            bool live = Application.isPlaying && _holdsReady;
            IReadOnlyList<ClimbHoldData> holds = live ? _holds : (holdData != null ? holdData.holds : null);
            if (holds == null || holds.Count == 0) return;

            // Orientation rays (cyan = outward grab normal, green = up) stay per-hold immediate
            // draws — only viable on sparse hold sets (authored ledges, trunks).
            if (holds.Count <= 1500)
            {
                for (int i = 0; i < holds.Count; i++)
                {
                    Vector3 wp = transform.TransformPoint(holds[i].LocalPosition);
                    Quaternion wr = transform.rotation * holds[i].LocalRotation;
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawRay(wp, (wr * Vector3.forward) * 0.2f);
                    Gizmos.color = Color.green;
                    Gizmos.DrawRay(wp, (wr * Vector3.up) * 0.15f);
                }
            }

            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.3f, $"{holds.Count} holds");
        }

        private void RebuildPreviewMesh(IReadOnlyList<ClimbHoldData> holds, byte[] paintArr, bool live)
        {
            if (_previewMesh == null)
            {
                _previewMesh = new Mesh
                {
                    name = "~ClimbHoldPreview",
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32   // 4 verts/hold beats 65k fast
                };
            }

            int n = holds.Count;
            var verts = new Vector3[n * 4];
            var cols = new Color32[n * 4];
            var tris = new int[n * 6];
            Color32 unpainted = (live && _source == ClimbHoldSource.Procedural)
                ? new Color32(255, 90, 255, 255)    // procedural/trunk
                : new Color32(255, 220, 40, 255);   // authored, no paint

            for (int i = 0; i < n; i++)
            {
                Quaternion r = holds[i].LocalRotation;
                Vector3 c = holds[i].LocalPosition + r * new Vector3(0f, 0f, PreviewQuadLift);
                Vector3 rt = r * new Vector3(PreviewQuadHalf, 0f, 0f);
                Vector3 up = r * new Vector3(0f, PreviewQuadHalf, 0f);

                int v = i * 4;
                verts[v] = c - rt - up;       // BL
                verts[v + 1] = c - rt + up;   // TL
                verts[v + 2] = c + rt + up;   // TR
                verts[v + 3] = c + rt - up;   // BR

                Color32 col = paintArr != null
                    ? (Color32)ClimbRiskClassUtil.DisplayColor((ClimbRiskClass)paintArr[i])
                    : unpainted;
                cols[v] = cols[v + 1] = cols[v + 2] = cols[v + 3] = col;

                int t = i * 6;   // wound to FACE the outward normal (local +Z)
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
            }

            _previewMesh.Clear();
            _previewMesh.vertices = verts;
            _previewMesh.colors32 = cols;
            _previewMesh.triangles = tris;
            _previewMesh.RecalculateBounds();
        }

        private void OnDestroy()
        {
            if (_previewMesh != null) DestroyImmediate(_previewMesh);
            if (_previewMat != null) DestroyImmediate(_previewMat);
        }
#endif
    }
}
