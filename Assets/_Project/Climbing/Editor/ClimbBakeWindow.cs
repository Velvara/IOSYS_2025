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
        private float _minHoldDistance = 0.35f;
        private float _topFaceMinUpDot = 0.5f;
        private float _wallMaxUpDot = 0.5f;
        private float _weldTolerance = 0.001f;
        private float _minOutwardDot = 0.25f;
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
            _minHoldDistance = EditorGUILayout.FloatField(
                new GUIContent("Min Hold Distance", "Spacing along a ledge + dedup radius (MIN_HH_DIST)."), _minHoldDistance);
            _topFaceMinUpDot = EditorGUILayout.Slider(
                new GUIContent("Top Face Min Up", "A face is a ledge TOP when dot(normal, up) ≥ this."), _topFaceMinUpDot, 0f, 1f);
            _wallMaxUpDot = EditorGUILayout.Slider(
                new GUIContent("Wall Max Up", "A face is a WALL when |dot(normal, up)| ≤ this."), _wallMaxUpDot, 0f, 1f);
            _weldTolerance = EditorGUILayout.FloatField(
                new GUIContent("Weld Tolerance", "Vertices within this distance (m) merge to one index so " +
                "split vertices from import don't break ledge detection. Raise only if a known ledge bakes nothing."), _weldTolerance);
            _minOutwardDot = EditorGUILayout.Slider(
                new GUIContent("Min Outward Dot", "Prefer/keep ledges facing OUT (away from the wall). Drops " +
                "the side edges of protrusions (extruded bricks). 0.25 ≈ drop pure side/back edges; 0 = keep " +
                "side edges but still prefer front ones in conflicts; lower for curved cliffs."), _minOutwardDot, -1f, 1f);
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

            var settings = new HandholdParser.Settings
            {
                MinHoldDistance = _minHoldDistance,
                TopFaceMinUpDot = _topFaceMinUpDot,
                WallMaxUpDot = _wallMaxUpDot,
                ConvexBias = -0.001f,
                WeldTolerance = _weldTolerance,
                MinOutwardDot = _minOutwardDot,
                OutwardReference = Vector3.forward   // per-surface below
            };

            int baked = 0;
            foreach (ClimbableSurface surface in surfaces)
            {
                MeshFilter bakeMesh = surface.ResolveBakeMesh();
                if (bakeMesh == null || bakeMesh.sharedMesh == null)
                {
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' has no bake mesh (assign Bake Mesh Filter " +
                                     "or add an EditorOnly-tagged child MeshFilter). Skipped.", surface);
                    continue;
                }
                if (!bakeMesh.sharedMesh.isReadable)
                {
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' bake mesh '{bakeMesh.sharedMesh.name}' is not " +
                                     "Read/Write enabled — enable it on the mesh import to bake. Skipped.", surface);
                    continue;
                }

                settings.OutwardReference = surface.transform.forward;   // fallback ref when the mesh facing is degenerate
                List<ClimbHoldData> holds = HandholdParser.Parse(bakeMesh, surface.transform, settings);
                if (holds.Count == 0)
                {
                    Debug.LogWarning($"[ClimbBake] '{surface.name}' produced 0 holds — check the bake mesh has " +
                                     "grabbable ledges and tune Top/Wall thresholds.", surface);
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
