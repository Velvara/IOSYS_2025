using System.Collections.Generic;
using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing
{
    /// <summary>
    /// Runtime risk overlay: while climbing, draws a camera-facing billboard image over every hold on
    /// any nearby climbable surface, coloured/tinted by that hold's painted risk class. The images fade
    /// from full opacity at the player to zero at the outer ring, and nothing draws past the ring.
    ///
    /// Perf model (this runs every frame, so it favours speed over exactness): holds are gathered by
    /// plain EUCLIDEAN distance from the player (not the carbine's geodesic reach — that's for the yank
    /// gate, which must be exact; this only needs to look right), front-facing holds only, grouped into
    /// ONE mesh per risk class (three draws), drawn with Graphics.DrawMesh — no per-hold GameObjects.
    /// Nearby surfaces are found with a throttled OverlapSphere; the billboard meshes rebuild each frame
    /// (holds + opacity change as the player moves, and the quads must re-face the camera).
    ///
    /// Standalone by design (a visualisation concern, not part of the climb FSM) — drop it on the player
    /// root next to <see cref="ClimbController"/>. It has NO inspector config of its own: every knob
    /// (images, tints, range, size, falloff, caps, mask) and the Black-fallback class it resolves
    /// unpainted holds through live on the global <see cref="ClimbRiskSettings"/> asset
    /// (Tools/Climbing/Risk Settings), so the overlay is tuned next to the risk numbers it visualises.
    /// </summary>
    [DisallowMultipleComponent]
    public class ClimbRiskIndicators : MonoBehaviour
    {
        private ClimbController _climb;
        private Transform _cam;
        private ClimbRiskSettings _cfg;   // the one settings asset — held, so inspector edits apply live

        // Per risk-image class (0 = Green, 1 = Blue, 2 = Red): the class's mesh/material/texture/tint and
        // the reusable build buffers.
        private readonly Mesh[] _mesh = new Mesh[3];
        private readonly Material[] _mat = new Material[3];
        private readonly Texture[] _tex = new Texture[3];
        private readonly Color[] _tint = new Color[3];
        private readonly List<Vector3>[] _v = new List<Vector3>[3];
        private readonly List<Color32>[] _c = new List<Color32>[3];
        private readonly List<Vector2>[] _uv = new List<Vector2>[3];
        private readonly List<int>[] _tri = new List<int>[3];

        private readonly Collider[] _overlap = new Collider[64];
        private readonly List<ClimbableSurface> _surfaces = new List<ClimbableSurface>();
        private float _refreshTimer;
        private bool _warnedCap;

        private static readonly Vector2 UV0 = new Vector2(0f, 0f);
        private static readonly Vector2 UV1 = new Vector2(0f, 1f);
        private static readonly Vector2 UV2 = new Vector2(1f, 1f);
        private static readonly Vector2 UV3 = new Vector2(1f, 0f);

        private void Awake()
        {
            _climb = GetComponentInParent<ClimbController>();
            _cfg = ClimbRiskSettings.Instance;
            for (int i = 0; i < 3; i++)
            {
                _v[i] = new List<Vector3>(256);
                _c[i] = new List<Color32>(256);
                _uv[i] = new List<Vector2>(256);
                _tri[i] = new List<int>(384);
                _mesh[i] = new Mesh
                {
                    name = "~ClimbRiskIndicator" + i,
                    hideFlags = HideFlags.HideAndDontSave,
                    indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
                };
            }
        }

        private void Update()
        {
            if (_cfg == null) _cfg = ClimbRiskSettings.Instance;
            if (!_cfg.ShowIndicators || _climb == null || !_climb.IsClimbing) return;
            if (_cam == null) { Camera c = Camera.main; if (c != null) _cam = c.transform; }
            if (_cam == null) return;

            EnsureMaterials();

            Vector3 center = transform.position + Vector3.up * _cfg.CenterHeightOffset;
            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer <= 0f || _surfaces.Count == 0)
            {
                RefreshSurfaces(center);
                _refreshTimer = Mathf.Max(0.05f, _cfg.SurfaceRefreshInterval);
            }

            BuildAndDraw(center);
        }

        private void RefreshSurfaces(Vector3 center)
        {
            _surfaces.Clear();
            int n = Physics.OverlapSphereNonAlloc(center, _cfg.IndicatorRange, _overlap, _cfg.ClimbableMask,
                                                  QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var s = _overlap[i].GetComponentInParent<ClimbableSurface>();
                if (s != null && s.HoldsReady && !_surfaces.Contains(s)) _surfaces.Add(s);
            }
        }

        private void BuildAndDraw(Vector3 center)
        {
            for (int i = 0; i < 3; i++) { _v[i].Clear(); _c[i].Clear(); _uv[i].Clear(); _tri[i].Clear(); }

            // Everything the hold loop needs, read ONCE out of the settings asset (property calls in a
            // ~21k-iteration loop are pure overhead).
            Vector3 camPos = _cam.position;
            Vector3 r = _cam.right * (_cfg.IndicatorSize * 0.5f);
            Vector3 u = _cam.up * (_cfg.IndicatorSize * 0.5f);
            float range = Mathf.Max(0.01f, _cfg.IndicatorRange);
            float r2 = range * range;
            float offset = _cfg.SurfaceOffset;
            bool drawUnpainted = _cfg.DrawUnpainted;
            int maxIndicators = _cfg.MaxIndicators;
            AnimationCurve opacityFalloff = _cfg.OpacityFalloff;
            int drawn = 0;
            bool capped = false;

            for (int si = 0; si < _surfaces.Count && !capped; si++)
            {
                ClimbableSurface s = _surfaces[si];
                if (s == null || !s.HoldsReady) continue;
                var holds = s.Holds;
                Transform st = s.transform;
                Quaternion sr = st.rotation;

                for (int i = 0; i < holds.Count; i++)
                {
                    Vector3 wp = st.TransformPoint(holds[i].LocalPosition);
                    Vector3 toHold = wp - center;
                    float d2 = toHold.sqrMagnitude;
                    if (d2 > r2) continue;                                  // outside the ring — nothing drawn

                    Vector3 normal = (sr * holds[i].LocalRotation) * Vector3.forward;
                    if (Vector3.Dot(normal, camPos - wp) <= 0f) continue;   // hold faces away — cull the far side

                    ClimbRiskClass raw = s.RiskClassOf(i);
                    if (raw == ClimbRiskClass.Black && !drawUnpainted) continue;
                    int idx = ImageIndex(_cfg.Resolve(raw));
                    if (idx < 0 || _tex[idx] == null || _mat[idx] == null) continue;   // no image for this class

                    float op = Mathf.Clamp01(opacityFalloff.Evaluate(Mathf.Sqrt(d2) / range));
                    Color t = _tint[idx];
                    Color32 col = new Color(t.r, t.g, t.b, t.a * op);
                    AddQuad(idx, wp + normal * offset, r, u, col);   // lift off the surface (anti-clip)

                    if (++drawn >= maxIndicators)
                    {
                        capped = true;
                        if (!_warnedCap)
                        {
                            _warnedCap = true;
                            Debug.LogWarning($"[ClimbRiskIndicators] Hit maxIndicators ({maxIndicators}) — " +
                                             "farther holds skipped this frame. Shrink Indicator Range or raise " +
                                             "the cap in Tools/Climbing/Risk Settings.");
                        }
                        break;
                    }
                }
            }

            for (int i = 0; i < 3; i++)
            {
                Mesh m = _mesh[i];
                m.Clear();
                if (_v[i].Count == 0 || _mat[i] == null) continue;
                m.SetVertices(_v[i]);
                m.SetColors(_c[i]);
                m.SetUVs(0, _uv[i]);
                m.SetTriangles(_tri[i], 0);
                m.RecalculateBounds();
                Graphics.DrawMesh(m, Matrix4x4.identity, _mat[i], gameObject.layer, null, 0, null, false, false, false);
            }
        }

        private void AddQuad(int idx, Vector3 c, Vector3 r, Vector3 u, Color32 col)
        {
            List<Vector3> v = _v[idx];
            int b = v.Count;
            v.Add(c - r - u); v.Add(c - r + u); v.Add(c + r + u); v.Add(c + r - u);
            var cl = _c[idx]; cl.Add(col); cl.Add(col); cl.Add(col); cl.Add(col);
            var uv = _uv[idx]; uv.Add(UV0); uv.Add(UV1); uv.Add(UV2); uv.Add(UV3);
            var t = _tri[idx];
            t.Add(b); t.Add(b + 1); t.Add(b + 2);
            t.Add(b); t.Add(b + 2); t.Add(b + 3);
        }

        private static int ImageIndex(ClimbRiskClass cls)
        {
            switch (cls)
            {
                case ClimbRiskClass.Green: return 0;
                case ClimbRiskClass.Blue: return 1;
                case ClimbRiskClass.Red: return 2;
                default: return -1;
            }
        }

        private void EnsureMaterials()
        {
            _tex[0] = _cfg.GreenImage; _tex[1] = _cfg.BlueImage; _tex[2] = _cfg.RedImage;
            _tint[0] = _cfg.GreenTint; _tint[1] = _cfg.BlueTint; _tint[2] = _cfg.RedTint;

            for (int i = 0; i < 3; i++)
            {
                if (_tex[i] == null) continue;                       // no image → class stays undrawn
                if (_mat[i] == null)
                {
                    Material m = _cfg.BillboardMaterialTemplate != null
                        ? new Material(_cfg.BillboardMaterialTemplate)
                        : NewDefaultMaterial();
                    if (m == null) continue;
                    m.hideFlags = HideFlags.HideAndDontSave;
                    m.color = Color.white;                           // tint rides the vertex colour
                    _mat[i] = m;
                }
                if (_mat[i].mainTexture != _tex[i]) _mat[i].mainTexture = _tex[i];
            }
        }

        private static Material NewDefaultMaterial()
        {
            Shader sh = Shader.Find("Sprites/Default");
            return sh != null ? new Material(sh) : null;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < 3; i++)
            {
                if (_mesh[i] != null) Destroy(_mesh[i]);
                if (_mat[i] != null) Destroy(_mat[i]);
            }
        }
    }
}
