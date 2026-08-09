using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Climbing.Editor
{
    /// <summary>
    /// Scene-view brush that paints slip-risk classes (Green/Blue/Red; Black = erase) onto a
    /// ClimbableSurface's baked holds. The paint is stored OUTSIDE the mesh, per SCENE INSTANCE,
    /// on a <see cref="ClimbRiskPaint"/> component this window adds to the instance — NOT as a
    /// field inside the prefab: a big array field on a prefab instance serializes as one override
    /// entry per element, which stalled/crashed the editor on ~21k-hold kit-bash surfaces. Legacy
    /// paint stored the old way is migrated automatically on first contact. Because the brush
    /// writes hold classes directly there is no vertex-color intermediate: no preview material,
    /// no extraction pass, no dominant-colour resolution — a hold IS its class.
    ///
    /// Usage: select a baked surface → Start Painting → LMB paints the holds under the brush (the
    /// ray hits the surface's own colliders, so the hidden EditorOnly bake meshes stay hidden).
    /// While painting: G/B/R pick a colour, X = black (erase), [ and ] resize the brush, Esc stops;
    /// Alt-orbit navigation works as usual.
    /// </summary>
    public class ClimbRiskPaintWindow : EditorWindow
    {
        private ClimbableSurface _target;
        private ClimbRiskPaint _paint;   // per-instance storage component (added on demand)
        private bool _painting;
        private ClimbRiskClass _brush = ClimbRiskClass.Green;
        private float _radius = 0.5f;

        // Cached world positions of the target's holds (rebuilt when the target/its transform changes).
        private Vector3[] _world;
        private ClimbableSurface _cachedFor;
        private Matrix4x4 _cachedMatrix;

        private Vector3 _brushPos;
        private Vector3 _brushNormal = Vector3.up;
        private bool _brushValid;
        private bool _strokeOpen;

        private static readonly RaycastHit[] RayHits = new RaycastHit[128];

        [MenuItem("Tools/Climbing/Risk Painter")]
        private static void Open() => GetWindow<ClimbRiskPaintWindow>("Climb Risk");

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += Repaint;
            if (_target == null) PickFromSelection();
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= Repaint;
            _painting = false;
        }

        // ------------------------------------------------------------------ window GUI

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Slip-Risk Paint (stored per scene instance)", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _target = (ClimbableSurface)EditorGUILayout.ObjectField("Surface", _target, typeof(ClimbableSurface), true);
                if (GUILayout.Button("Use Selection", GUILayout.Width(100f))) PickFromSelection();
            }

            if (_target == null)
            {
                EditorGUILayout.HelpBox("Select a baked ClimbableSurface (scene instance).", MessageType.Info);
                StopPainting();
                return;
            }

            HoldDataSO data = _target.EditorHoldData;
            int count = data != null ? data.Count : 0;
            if (count == 0)
            {
                EditorGUILayout.HelpBox("This surface has no baked holds — bake it first " +
                                        "(Tools/Climbing/Bake Handholds).", MessageType.Warning);
                StopPainting();
                return;
            }

            MigrateLegacyIfNeeded();
            _paint = _target.GetComponent<ClimbRiskPaint>();
            byte[] paint = _paint != null ? _paint.Classes : null;

            if (_paint != null && PrefabUtility.IsPartOfPrefabInstance(_paint) &&
                !PrefabUtility.IsAddedComponentOverride(_paint))
            {
                EditorGUILayout.HelpBox(
                    "ClimbRiskPaint is part of the PREFAB ASSET here — per-instance paint on it serializes " +
                    "as one override entry per hold (the slow path that crashes on big bakes). Remove the " +
                    "component from the prefab and let the painter add it per scene instance.",
                    MessageType.Warning);
            }

            if (paint != null && paint.Length > 0 && paint.Length != count)
            {
                EditorGUILayout.HelpBox(
                    $"Paint ({paint.Length} holds) no longer matches the bake ({count} holds) — the surface " +
                    "was re-baked after painting. Painting is disabled until the paint is reset.",
                    MessageType.Error);
                if (GUILayout.Button("Reset Paint (clears the stale paint)"))
                {
                    Undo.RegisterCompleteObjectUndo(_paint, "Reset Climb Risk Paint");
                    _paint.Classes = new byte[count];
                    Commit();
                }
                StopPainting();
                return;
            }

            if (Application.isPlaying)
                EditorGUILayout.HelpBox("Play mode — paint strokes here will NOT be saved.", MessageType.Warning);

            EditorGUILayout.Space();
            GUI.color = _painting ? new Color(1f, 0.75f, 0.6f) : Color.white;
            if (GUILayout.Button(_painting ? "Stop Painting  (Esc)" : "Start Painting", GUILayout.Height(28f)))
            {
                if (_painting) StopPainting();
                else StartPainting();
            }
            GUI.color = Color.white;

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                ClassButton(ClimbRiskClass.Green, "Green (G)");
                ClassButton(ClimbRiskClass.Blue, "Blue (B)");
                ClassButton(ClimbRiskClass.Red, "Red (R)");
                ClassButton(ClimbRiskClass.Black, "Black (X)");
            }
            _radius = EditorGUILayout.Slider(
                new GUIContent("Brush Radius", "Also [ and ] while painting."), _radius, 0.05f, 3f);

            // Class tally — one pass over a byte array, cheap enough per GUI repaint.
            int g = 0, b = 0, r = 0, k = 0;
            if (paint != null && paint.Length == count)
            {
                for (int i = 0; i < paint.Length; i++)
                {
                    switch ((ClimbRiskClass)paint[i])
                    {
                        case ClimbRiskClass.Green: g++; break;
                        case ClimbRiskClass.Blue: b++; break;
                        case ClimbRiskClass.Red: r++; break;
                        default: k++; break;
                    }
                }
            }
            else k = count;
            EditorGUILayout.LabelField($"Holds: {count}    Green {g} · Blue {b} · Red {r} · Black {k}");

            if (GUILayout.Button("Reset All To Black"))
            {
                EnsurePaint(count);
                Undo.RegisterCompleteObjectUndo(_paint, "Reset Climb Risk Paint");
                _paint.Classes = new byte[count];
                Commit();
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "LMB paints the holds under the brush (the ray needs the surface's colliders — the same " +
                "ones climbing uses). What each class COSTS, and what Black resolves to, are global — " +
                "edit them in Tools/Climbing/Risk Settings.",
                MessageType.None);

            if (GUILayout.Button("Open Risk Settings (global)"))
                EditorApplication.ExecuteMenuItem("Tools/Climbing/Risk Settings");
        }

        private void ClassButton(ClimbRiskClass riskClass, string label)
        {
            Color prev = GUI.backgroundColor;
            Color col = ClimbRiskClassUtil.DisplayColor(riskClass);
            GUI.backgroundColor = _brush == riskClass ? col : Color.Lerp(col, Color.gray, 0.6f);
            if (GUILayout.Toggle(_brush == riskClass, label, "Button") && _brush != riskClass)
            {
                _brush = riskClass;
                SceneView.RepaintAll();
            }
            GUI.backgroundColor = prev;
        }

        private void PickFromSelection()
        {
            GameObject go = Selection.activeGameObject;
            _target = go != null ? go.GetComponentInParent<ClimbableSurface>() : null;
            _paint = _target != null ? _target.GetComponent<ClimbRiskPaint>() : null;
        }

        private void StartPainting()
        {
            _painting = true;
            Tools.current = Tool.None;   // keep the move/rotate gizmo out of the brush's way
            SceneView.RepaintAll();
        }

        private void StopPainting()
        {
            if (!_painting) return;
            _painting = false;
            _strokeOpen = false;
            SceneView.RepaintAll();
            Repaint();
        }

        // ------------------------------------------------------------------ storage

        /// <summary>Moves paint out of the legacy ClimbableSurface array field onto the
        /// ClimbRiskPaint component, and clears the field — recording the cleared state drops the
        /// per-element prefab-override list that made big painted instances crash the editor.</summary>
        private void MigrateLegacyIfNeeded()
        {
            byte[] legacy = _target.EditorRiskClasses;
            if (legacy == null || legacy.Length == 0) return;

            Undo.IncrementCurrentGroup();
            ClimbRiskPaint comp = _target.GetComponent<ClimbRiskPaint>();
            if (comp == null) comp = Undo.AddComponent<ClimbRiskPaint>(_target.gameObject);
            Undo.RegisterCompleteObjectUndo(comp, "Migrate Climb Risk Paint");
            Undo.RegisterCompleteObjectUndo(_target, "Migrate Climb Risk Paint");

            if (comp.Classes == null || comp.Classes.Length == 0) comp.Classes = legacy;
            _target.EditorRiskClasses = null;

            EditorUtility.SetDirty(comp);
            EditorUtility.SetDirty(_target);
            PrefabUtility.RecordPrefabInstancePropertyModifications(_target);
            if (!Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(_target.gameObject.scene);
            Debug.Log($"[ClimbRiskPaint] '{_target.name}': migrated legacy paint ({legacy.Length} holds) " +
                      "onto the ClimbRiskPaint component (fixes the prefab-override bloat). Save the scene.", _target);
        }

        private void EnsurePaint(int holdCount)
        {
            if (_paint == null) _paint = _target.GetComponent<ClimbRiskPaint>();
            if (_paint == null) _paint = Undo.AddComponent<ClimbRiskPaint>(_target.gameObject);
            if (_paint.Classes == null || _paint.Classes.Length != holdCount)
                _paint.Classes = new byte[holdCount];   // real (stale) mismatches never reach here — OnGUI blocks painting
        }

        /// <summary>Persist the paint (SetDirty + scene dirty; the component is instance-added, so
        /// there is no per-element override list to record — that was the crash).</summary>
        private void Commit()
        {
            if (_paint != null)
            {
                EditorUtility.SetDirty(_paint);
                PrefabUtility.RecordPrefabInstancePropertyModifications(_paint);   // no-op when instance-added
            }
            if (_target != null && !Application.isPlaying)
                EditorSceneManager.MarkSceneDirty(_target.gameObject.scene);
            Repaint();
        }

        // ------------------------------------------------------------------ scene-view brush

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!_painting || _target == null) return;
            HoldDataSO data = _target.EditorHoldData;
            if (data == null || data.Count == 0) return;
            byte[] paint = _paint != null ? _paint.Classes : null;
            if (paint != null && paint.Length > 0 && paint.Length != data.Count) return;   // stale — the window offers the reset

            Event e = Event.current;
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));   // LMB must not re-select

            if (e.type == EventType.KeyDown && HandleHotkey(e)) return;

            RefreshWorldCache(data);

            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            _brushValid = RaycastTarget(ray, out _brushPos, out _brushNormal);

            bool paintEvent = _brushValid && !e.alt && e.button == 0 &&
                              (e.type == EventType.MouseDown || e.type == EventType.MouseDrag);
            if (paintEvent)
            {
                if (!_strokeOpen)
                {
                    // One undo step per stroke (registered before the first change captures the array).
                    EnsurePaint(data.Count);
                    Undo.RegisterCompleteObjectUndo(_paint, "Paint Climb Risk");
                    _strokeOpen = true;
                }
                PaintAt(_brushPos);
                e.Use();
            }
            if (_strokeOpen && e.rawType == EventType.MouseUp && e.button == 0)
            {
                _strokeOpen = false;
                Commit();
            }

            if (e.type == EventType.Repaint) DrawOverlay();
            if (e.type == EventType.MouseMove || e.type == EventType.MouseDrag) sceneView.Repaint();
        }

        /// <summary>Brush hotkeys (only while painting). Returns true when the key was consumed.</summary>
        private bool HandleHotkey(Event e)
        {
            switch (e.keyCode)
            {
                case KeyCode.G: _brush = ClimbRiskClass.Green; break;
                case KeyCode.B: _brush = ClimbRiskClass.Blue; break;
                case KeyCode.R: _brush = ClimbRiskClass.Red; break;
                case KeyCode.X: _brush = ClimbRiskClass.Black; break;
                case KeyCode.LeftBracket: _radius = Mathf.Max(0.05f, _radius / 1.2f); break;
                case KeyCode.RightBracket: _radius = Mathf.Min(3f, _radius * 1.2f); break;
                case KeyCode.Escape: StopPainting(); e.Use(); return true;
                default: return false;
            }
            e.Use();
            Repaint();
            SceneView.RepaintAll();
            return true;
        }

        /// <summary>Nearest ray hit on the TARGET's own colliders (other scene geometry is ignored,
        /// so painting works even when another cliff sits between the camera and the brush).</summary>
        private bool RaycastTarget(Ray ray, out Vector3 point, out Vector3 normal)
        {
            point = Vector3.zero;
            normal = Vector3.up;
            int n = Physics.RaycastNonAlloc(ray, RayHits, 2000f, ~0, QueryTriggerInteraction.Ignore);
            Transform root = _target.transform;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                if (!RayHits[i].transform.IsChildOf(root) || RayHits[i].distance >= best) continue;
                best = RayHits[i].distance;
                point = RayHits[i].point;
                normal = RayHits[i].normal;
                found = true;
            }
            return found;
        }

        private void PaintAt(Vector3 center)
        {
            byte[] arr = _paint.Classes;
            float sqr = _radius * _radius;
            byte v = (byte)_brush;
            bool changed = false;
            for (int i = 0; i < _world.Length; i++)
            {
                if (arr[i] == v || (_world[i] - center).sqrMagnitude > sqr) continue;
                arr[i] = v;
                changed = true;
            }
            if (changed) _paint.PaintVersion++;   // the selected-preview mesh recolours live
        }

        private void RefreshWorldCache(HoldDataSO data)
        {
            Matrix4x4 m = _target.transform.localToWorldMatrix;
            if (_cachedFor == _target && _world != null && _world.Length == data.Count && m == _cachedMatrix)
                return;
            _world = new Vector3[data.Count];
            for (int i = 0; i < _world.Length; i++)
                _world[i] = m.MultiplyPoint3x4(data.holds[i].LocalPosition);
            _cachedFor = _target;
            _cachedMatrix = m;
        }

        private void DrawOverlay()
        {
            // ALL hold visuals come from the surface's batched preview mesh (one DrawMeshNow, every
            // hold, risk colours, recoloured live via PaintVersion). Per-hold handle dots are gone —
            // an unbounded "draw everything near the brush" loop crashed the editor on dense bakes
            // the moment the brush ray first hit a 21k-hold surface.
            _target.EditorDrawHoldPreview();

            if (_brushValid)
            {
                Color bc = ClimbRiskClassUtil.DisplayColor(_brush);
                Handles.color = bc;
                Handles.DrawWireDisc(_brushPos, _brushNormal, _radius);
                Handles.color = new Color(bc.r, bc.g, bc.b, 0.08f);
                Handles.DrawSolidDisc(_brushPos, _brushNormal, _radius);
            }
        }
    }
}
