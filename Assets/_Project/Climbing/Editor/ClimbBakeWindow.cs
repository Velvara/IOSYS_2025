using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing.Editor
{
    /// <summary>
    /// One-click handhold bake. For each <see cref="ClimbableSurface"/> (selected, or all in the open
    /// scene) it parses the surface's bake mesh via <see cref="HandholdParser"/>, writes a per-piece
    /// <see cref="HoldDataSO"/> (created/overwritten + auto-assigned to the surface), and logs the count.
    /// Bake in the ASSEMBLED scene once neighbour-culling lands (C3); for now it's geometry-only.
    ///
    /// Select a surface afterwards to preview the holds (ClimbableSurface draws them as gizmos).
    /// </summary>
    public class ClimbBakeWindow : EditorWindow
    {
        /// <summary>How a surface's bake mesh becomes holds. LedgeEdges = convex-lip detection
        /// (HandholdParser, for authored cliffs); EveryVertex = one hold per vertex (VertexHoldParser,
        /// for purpose-built low-poly climb proxies — the generic form of the trunk emission).</summary>
        private enum BakeMode { LedgeEdges, EveryVertex }

        private BakeMode _mode = BakeMode.LedgeEdges;

        // LedgeEdges (HandholdParser) params.
        private float _minHoldDistance = 0.35f;
        private float _topFaceMinUpDot = 0.5f;
        private float _wallMaxUpDot = 0.5f;
        private float _minOutwardDot = 0.25f;

        // EveryVertex (VertexHoldParser) params.
        private float _vtxMinOutwardDot = 0f;
        private float _vtxMaxUpDot = 0.85f;

        // Shared.
        private bool _combineChildMeshes = false;
        private bool _cullOccluded = false;
        private float _occlusionSkin = 0.03f;
        private float _weldTolerance = 0.001f;
        private bool _selectedOnly = true;
        private string _outputFolder = "Assets/_Project/Climbing/Holds/Baked";

        [MenuItem("Tools/Climbing/Bake Handholds")]
        private static void Open() => GetWindow<ClimbBakeWindow>("Climb Bake");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Handhold Bake (C1 — geometry only)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Parses each ClimbableSurface's bake mesh into a HoldDataSO. The bake mesh must have " +
                "Read/Write enabled on its import. Select a surface after baking to preview the holds.",
                MessageType.Info);

            EditorGUILayout.Space();
            _mode = (BakeMode)EditorGUILayout.EnumPopup(
                new GUIContent("Mode", "Ledge Edges = convex-lip detection for authored cliffs. " +
                "Every Vertex = one hold per mesh vertex for purpose-built low-poly climb proxies."), _mode);

            EditorGUILayout.Space();
            if (_mode == BakeMode.LedgeEdges)
            {
                _minHoldDistance = EditorGUILayout.FloatField(
                    new GUIContent("Min Hold Distance", "Spacing along a ledge + dedup radius (MIN_HH_DIST)."), _minHoldDistance);
                _topFaceMinUpDot = EditorGUILayout.Slider(
                    new GUIContent("Top Face Min Up", "A face is a ledge TOP when dot(normal, up) ≥ this."), _topFaceMinUpDot, 0f, 1f);
                _wallMaxUpDot = EditorGUILayout.Slider(
                    new GUIContent("Wall Max Up", "A face is a WALL when |dot(normal, up)| ≤ this."), _wallMaxUpDot, 0f, 1f);
                _minOutwardDot = EditorGUILayout.Slider(
                    new GUIContent("Min Outward Dot", "Prefer/keep ledges facing OUT (away from the wall). Drops " +
                    "the side edges of protrusions (extruded bricks). 0.25 ≈ drop pure side/back edges; 0 = keep " +
                    "side edges but still prefer front ones in conflicts; lower for curved cliffs."), _minOutwardDot, -1f, 1f);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "One hold per vertex — no spacing/thinning. Best on low-poly climb proxies; a dense art " +
                    "mesh yields thousands of holds. Top-out is handled at runtime (no ledges baked).",
                    MessageType.None);
                _vtxMinOutwardDot = EditorGUILayout.Slider(
                    new GUIContent("Min Outward Dot", "Cull inward/back-facing vertices. 0 ≈ keep the outward " +
                    "hemisphere and drop the back; −1 keeps every side (a pillar you go around); raise toward 1 " +
                    "for a flat wall approached head-on."), _vtxMinOutwardDot, -1f, 1f);
                _vtxMaxUpDot = EditorGUILayout.Slider(
                    new GUIContent("Max Up Dot", "Cull flat top-surface vertices so the climb ends at the top " +
                    "EDGE and the runtime mantles. 0.85 ≈ drop faces within ~30° of straight up; 1.01 keeps tops."),
                    _vtxMaxUpDot, 0f, 1.01f);
            }

            _combineChildMeshes = EditorGUILayout.Toggle(
                new GUIContent("Combine Child Meshes", "KIT-BASH: bake EVERY readable MeshFilter under the " +
                "ClimbableSurface into one merged hold set (intersecting pieces weld at their seams), so a " +
                "cluster of separate meshes climbs as one seamless surface. Off = bake only the single " +
                "resolved bake mesh (authored-cliff flow). Every Vertex mode welds across pieces; Ledge Edges " +
                "mode parses each piece and concatenates."), _combineChildMeshes);
            _cullOccluded = EditorGUILayout.Toggle(
                new GUIContent("Cull Occluded Holds", "KIT-BASH: after baking, drop every hold whose point is " +
                "INSIDE another piece's geometry (a face of one piece embedded inside another). Stops the climber " +
                "grabbing holds inside solid geometry / clipping through an intersecting piece. Tested against the " +
                "exact bake meshes via temporary colliders built during the bake — no collider setup needed for " +
                "the cull itself (runtime climbing still needs colliders on the visual pieces). Leave off for " +
                "normal single surfaces."), _cullOccluded);
            if (_cullOccluded)
                _occlusionSkin = EditorGUILayout.FloatField(
                    new GUIContent("  Occlusion Skin (m)", "Holds whose nearest crossing of another piece is within " +
                    "this distance count as AT THE SEAM (kept, not buried). Raise to keep more holds along " +
                    "inner-corner seams; lower if barely-buried holds survive."), _occlusionSkin);
            _weldTolerance = EditorGUILayout.FloatField(
                new GUIContent("Weld Tolerance", "Vertices within this distance (m) merge to one index so " +
                "split vertices from import (and touching kit-bash seams) don't stack duplicate holds / break ledge detection. Raise only if a known ledge bakes nothing."), _weldTolerance);
            _selectedOnly = EditorGUILayout.Toggle(
                new GUIContent("Selected Only", "Bake only selected ClimbableSurfaces (off = all in the open scene)."), _selectedOnly);
            _outputFolder = EditorGUILayout.TextField(
                new GUIContent("Output Folder", "Where new HoldDataSO assets are created."), _outputFolder);

            EditorGUILayout.Space();
            if (GUILayout.Button(_selectedOnly ? "Bake Selected" : "Bake All In Scene", GUILayout.Height(28)))
                Bake();
        }

        private void Bake()
        {
            ClimbableSurface[] surfaces = CollectSurfaces();
            if (surfaces.Length == 0)
            {
                Debug.LogWarning("[ClimbBake] No ClimbableSurface found to bake.");
                return;
            }

            EnsureFolder(_outputFolder);

            var ledgeSettings = new HandholdParser.Settings
            {
                MinHoldDistance = _minHoldDistance,
                TopFaceMinUpDot = _topFaceMinUpDot,
                WallMaxUpDot = _wallMaxUpDot,
                ConvexBias = -0.001f,
                WeldTolerance = _weldTolerance,
                MinOutwardDot = _minOutwardDot,
                OutwardReference = Vector3.forward   // per-surface below
            };
            var vertexSettings = new VertexHoldParser.Settings
            {
                WeldTolerance = _weldTolerance,
                MinOutwardDot = _vtxMinOutwardDot,
                MaxUpDot = _vtxMaxUpDot,
                OutwardReference = Vector3.forward   // per-surface below
            };

            int baked = 0;
            foreach (ClimbableSurface surface in surfaces)
            {
                List<MeshFilter> meshes = GatherBakeMeshes(surface);
                if (meshes.Count == 0) continue;   // GatherBakeMeshes already warned why

                List<ClimbHoldData> holds;
                if (_mode == BakeMode.EveryVertex)
                {
                    // One shared weld across every piece → intersecting kit-bash seams merge.
                    vertexSettings.OutwardReference = surface.transform.forward;
                    holds = VertexHoldParser.Parse(meshes, surface.transform, vertexSettings);
                }
                else
                {
                    // Ledge detection is per-mesh (edge adjacency doesn't span pieces) — parse each and concat.
                    ledgeSettings.OutwardReference = surface.transform.forward;   // fallback ref when the mesh facing is degenerate
                    holds = new List<ClimbHoldData>();
                    foreach (MeshFilter mf in meshes)
                        holds.AddRange(HandholdParser.Parse(mf, surface.transform, ledgeSettings));
                }
                int rawCount = holds.Count;
                if (_cullOccluded && holds.Count > 0)
                {
                    holds = CullOccluded(surface, meshes, holds, out int dropped);
                    Debug.Log($"[ClimbBake] '{surface.name}' occlusion cull: dropped {dropped} buried hold(s), kept {holds.Count}/{rawCount}.", surface);
                }
                if (holds.Count == 0)
                {
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' produced 0 holds — " + (
                        _cullOccluded && rawCount > 0 ? "occlusion culled everything; check the pieces' colliders match their meshes, or disable Cull Occluded Holds." :
                        _mode == BakeMode.EveryVertex ? "every vertex was culled; lower Min Outward Dot / raise Max Up Dot."
                        : "check the bake mesh has grabbable ledges and tune Top/Wall thresholds."), surface);
                    continue;
                }

                HoldDataSO so = GetOrCreateAsset(surface);
                so.holds = holds.ToArray();
                EditorUtility.SetDirty(so);
                AssignHoldData(surface, so);
                EditorUtility.SetDirty(surface);
                baked++;
                Debug.Log($"[ClimbBake] '{surface.name}' → {holds.Count} holds → {AssetDatabase.GetAssetPath(so)}", surface);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ClimbBake] Done — baked {baked}/{surfaces.Length} surface(s).");
        }

        /// <summary>
        /// The mesh(es) to bake for one surface. Combine off = the single resolved bake mesh (authored-cliff
        /// flow). Combine on = every readable MeshFilter under the surface (kit-bash cluster). Warns for each
        /// unreadable/empty mesh and returns an empty list (with a warning) if nothing bakeable remains.
        /// </summary>
        private List<MeshFilter> GatherBakeMeshes(ClimbableSurface surface)
        {
            var result = new List<MeshFilter>();

            if (!_combineChildMeshes)
            {
                MeshFilter bakeMesh = surface.ResolveBakeMesh();
                if (bakeMesh == null || bakeMesh.sharedMesh == null)
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' has no bake mesh (assign Bake Mesh Filter, " +
                                     "add an EditorOnly-tagged child MeshFilter, or enable Combine Child Meshes). Skipped.", surface);
                else if (!bakeMesh.sharedMesh.isReadable)
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' bake mesh '{bakeMesh.sharedMesh.name}' is not " +
                                     "Read/Write enabled — enable it on the mesh import to bake. Skipped.", surface);
                else
                    result.Add(bakeMesh);
                return result;
            }

            // Combine mode honours the same visual/bake split as ResolveBakeMesh: when any piece under the
            // surface is tagged EditorOnly, ONLY those are baked (they're the dedicated hold-creator meshes —
            // stripped from builds; disable their renderers). With no EditorOnly pieces, every readable
            // MeshFilter bakes (visual = bake, the simple flow).
            int skipped = 0;
            var editorOnly = new List<MeshFilter>();
            foreach (MeshFilter mf in surface.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (!mf.sharedMesh.isReadable)
                {
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' → piece '{mf.name}' mesh '{mf.sharedMesh.name}' " +
                                     "is not Read/Write enabled — enable it on the import. Skipped this piece.", mf);
                    skipped++;
                    continue;
                }
                if (mf.gameObject.CompareTag("EditorOnly")) editorOnly.Add(mf);
                result.Add(mf);
            }
            if (editorOnly.Count > 0)
            {
                Debug.Log($"[ClimbBake] '{surface.name}' — using {editorOnly.Count} EditorOnly bake piece(s), " +
                          $"ignoring {result.Count - editorOnly.Count} visual piece(s).", surface);
                return editorOnly;
            }
            if (result.Count == 0)
                Debug.LogWarning($"[ClimbBake] '{surface.name}' has no readable child MeshFilter to combine " +
                                 $"({skipped} skipped for Read/Write). Skipped.", surface);
            return result;
        }

        /// <summary>
        /// Drops holds buried inside ANOTHER piece of the cluster (neighbour-culling): a hold survives only
        /// if its point is not INSIDE any piece's geometry. Tested against TEMPORARY MeshColliders built from
        /// the exact bake meshes (created for the query, destroyed after) — never the runtime low-poly
        /// colliders — so the verdict matches the geometry the holds came from and needs no collider setup.
        ///
        /// Inside-ness per piece is decided by crossing PARITY, majority-voted across THREE spread rays: each
        /// ray counts every surface crossing of each piece (backface queries on, so entries AND exits count);
        /// an odd count means "inside". One ray alone grazes surfaces nearly edge-on at INNER CORNERS and
        /// mis-counts — three diverse directions can't all graze the same face, so the vote (≥2 odd) is
        /// robust there. A hold whose nearest crossing of a piece (over all rays) lies within
        /// <see cref="_occlusionSkin"/> sits ON that piece's surface / at the seam and is never culled by it
        /// — this both exempts the hold's own piece and keeps the holds lining an intersection seam.
        /// Editor-only.
        /// </summary>
        private List<ClimbHoldData> CullOccluded(ClimbableSurface surface, List<MeshFilter> pieces,
                                                 List<ClimbHoldData> holds, out int dropped)
        {
            dropped = 0;
            var tempGOs = new List<GameObject>(pieces.Count);
            var tempCols = new HashSet<Collider>();
            Bounds bounds = default;
            try
            {
                // Exact-geometry temp colliders, one per bake piece (hidden, never saved).
                foreach (MeshFilter mf in pieces)
                {
                    var go = new GameObject("~ClimbBakeOcclusion") { hideFlags = HideFlags.HideAndDontSave };
                    go.transform.SetPositionAndRotation(mf.transform.position, mf.transform.rotation);
                    go.transform.localScale = mf.transform.lossyScale;
                    var mc = go.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    tempGOs.Add(go);
                    tempCols.Add(mc);
                    bounds = tempGOs.Count == 1 ? mc.bounds : Encapsulated(bounds, mc.bounds);
                }
                Physics.SyncTransforms();   // make the fresh colliders queryable before the physics step

                float rayLen = bounds.size.magnitude + 1f;   // from anywhere in the cluster, past all of it
                float skin = Mathf.Max(0.005f, _occlusionSkin);
                Transform st = surface.transform;
                var kept = new List<ClimbHoldData>(holds.Count);
                var verdict = new Dictionary<Collider, (int oddVotes, float minFirst)>();
                var rayCross = new Dictionary<Collider, (int count, float first)>();

                bool prevBackfaces = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;   // parity needs EVERY crossing, exits (back faces) included
                try
                {
                    for (int i = 0; i < holds.Count; i++)
                    {
                        Vector3 p = st.TransformPoint(holds[i].LocalPosition);
                        Vector3 n = (st.rotation * holds[i].LocalRotation) * Vector3.forward;
                        Vector3 t = Vector3.Cross(n, Vector3.up);
                        if (t.sqrMagnitude < 1e-4f) t = Vector3.Cross(n, Vector3.right);
                        t.Normalize();

                        verdict.Clear();
                        for (int r = 0; r < 3; r++)
                        {
                            // Outward normal + two skewed companions — diverse enough that an inner-corner
                            // face can graze at most one of them.
                            Vector3 dir = r == 0 ? n
                                        : r == 1 ? (n + t * 0.8f + Vector3.up * 0.5f).normalized
                                                 : (n - t * 0.7f - Vector3.up * 0.6f).normalized;

                            RaycastHit[] hits = Physics.RaycastAll(p, dir, rayLen, ~0, QueryTriggerInteraction.Ignore);
                            rayCross.Clear();
                            for (int k = 0; k < hits.Length; k++)
                            {
                                Collider c = hits[k].collider;
                                if (!tempCols.Contains(c)) continue;   // unrelated scene geometry
                                rayCross[c] = rayCross.TryGetValue(c, out var e)
                                    ? (e.count + 1, Mathf.Min(e.first, hits[k].distance))
                                    : (1, hits[k].distance);
                            }
                            foreach (var kv in rayCross)
                            {
                                var v = verdict.TryGetValue(kv.Key, out var w) ? w : (0, float.MaxValue);
                                verdict[kv.Key] = (v.Item1 + ((kv.Value.count & 1) == 1 ? 1 : 0),
                                                   Mathf.Min(v.Item2, kv.Value.first));
                            }
                        }

                        // A ray that misses a piece entirely contributes no entry = an implicit "outside" vote.
                        bool buried = false;
                        foreach (var kv in verdict)
                        {
                            if (kv.Value.minFirst <= skin) continue;   // on this piece's surface / at the seam
                            if (kv.Value.oddVotes >= 2) { buried = true; break; }   // majority say inside
                        }

                        if (buried) dropped++; else kept.Add(holds[i]);
                    }
                }
                finally { Physics.queriesHitBackfaces = prevBackfaces; }

                return kept;
            }
            finally
            {
                foreach (GameObject go in tempGOs)
                    if (go != null) DestroyImmediate(go);
            }
        }

        private static Bounds Encapsulated(Bounds b, Bounds add) { b.Encapsulate(add); return b; }

        private ClimbableSurface[] CollectSurfaces()
        {
            if (_selectedOnly)
            {
                var found = new List<ClimbableSurface>();
                foreach (GameObject go in Selection.gameObjects)
                {
                    if (go == null) continue;
                    found.AddRange(go.GetComponentsInChildren<ClimbableSurface>(true));
                }
                return found.ToArray();
            }
#if UNITY_2023_1_OR_NEWER
            return Object.FindObjectsByType<ClimbableSurface>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            return Object.FindObjectsOfType<ClimbableSurface>(true);
#endif
        }

        /// <summary>Reuse the surface's existing HoldDataSO (overwrite) or create a new per-piece asset.</summary>
        private HoldDataSO GetOrCreateAsset(ClimbableSurface surface)
        {
            var so = new SerializedObject(surface);
            HoldDataSO existing = so.FindProperty("holdData").objectReferenceValue as HoldDataSO;
            if (existing != null) return existing;

            HoldDataSO created = CreateInstance<HoldDataSO>();
            string path = AssetDatabase.GenerateUniqueAssetPath($"{_outputFolder}/{Sanitize(surface.name)}_Holds.asset");
            AssetDatabase.CreateAsset(created, path);
            return created;
        }

        private void AssignHoldData(ClimbableSurface surface, HoldDataSO so)
        {
            var serialized = new SerializedObject(surface);
            serialized.FindProperty("holdData").objectReferenceValue = so;
            serialized.ApplyModifiedProperties();
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            string leaf = Path.GetFileName(folder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
