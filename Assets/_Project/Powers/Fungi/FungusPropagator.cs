using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// Attach to the empty "Fungus" prefab. Spawns on a collided "deadSource" surface,
/// generates surface Poisson points independent of mesh vertex density,
/// then instantiates fungus objects in outward "rings" with overlap.
/// </summary>
public class FungusPropagator : MonoBehaviour
{
    [Header("Propagation")]
    public float fungusDensity = 0.2f;            // minimum spacing on surface
    public int maxPropagationPoints = 250;        // hard cap
    public List<GameObject> fungusObjects;        // source prefabs

    [Header("Growth")]
    public float fungusGrowTime = 0.5f;           // base time to grow to default scale
    [Range(0f, 1f)] public float instantiateOverlap = 0.5f; // 0=start next batch immediately, 1=after previous finish
    [Range(0.5f, 1.5f)] public float perPointGrowTimeNoiseRange = 1.5f; // max factor; min is 2 - this value (i.e., 0.5..1.5)
    public float perlinNoiseScale = 1.0f;         // spatial scale for noise variation
    public int perlinSeed = 12345;

    [Header("Tagging")]
    public Tags tagToAdd;

    [Header("Particles")]
    public ParticleSystem fungusParticle;         // prefab with Shape=Box recommended
    public bool alignParticleToSourceRotation = true;

    // Input from throwable
    public Vector3 fungusOrigin { get; private set; }
    public GameObject deadSource { get; private set; }

    // Outputs
    public Vector3[] propagationPoints { get; private set; }
    public Vector3[] propagationNormals { get; private set; }

    // Internals
    private Transform _sourceTransform;
    private Mesh _bakedMesh;
    //private bool _ready;
    private List<List<int>> propagationBatches;

    void Start()
    {
        fungusObjects = FungusPool.Instance.GetFungiList();
    }

    // --- Entry point called by Throwable ---
    public void Initialize(Vector3 origin, GameObject source)
    {
        fungusOrigin = origin;
        deadSource = source;
        _sourceTransform = source.transform;

        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        // 1) Bake/grab mesh
        if (!TryGetSourceMesh(deadSource, out _bakedMesh))
        {
            Debug.LogWarning("[Fungus] No mesh found on deadSource. Aborting.");
            yield break;
        }

        // 2) Generate Poisson disk points on mesh surface
        GenerateSurfacePoissonPoints();

        // Build concentric batches from the generated points (so SpawnWave has lists to iterate)
        BuildPropagationBatches();

        // 3) Wave spawn with overlap + per-point growth time noise
        if (propagationBatches != null)
        {
            foreach (var batch in propagationBatches)
            {
                yield return StartCoroutine(SpawnWave(batch));
            }
        }

        // 4) Tagging
        var tagMgr = deadSource.GetComponent<TagObjManager>();
        if (tagMgr != null)
        {
            tagMgr.ClearTags();
            if (tagToAdd != null) tagMgr.AddTags(tagToAdd);
        }

        // 5) Particles: bound the source
        InstantiateAndFitParticle();
    }

    #region Mesh access

    private bool TryGetSourceMesh(GameObject go, out Mesh mesh)
    {
        mesh = null;

        // Skinned?
        if (go.TryGetComponent<SkinnedMeshRenderer>(out var skinned))
        {
            var baked = new Mesh();
            skinned.BakeMesh(baked, true);
            mesh = baked;
            return true;
        }

        // Static?
        if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null)
        {
            mesh = mf.sharedMesh;
            return true;
        }

        return false;
    }

    #endregion

    #region Poisson on surface (triangle-area sampling + spatial hashing)

    private struct Tri
    {
        public Vector3 a, b, c;   // world-space
        public Vector3 n;         // face normal (unit)
        public float area;        // world-space area
    }

    private List<Tri> _tris;
    private float[] _cdf; // cumulative distribution by area

    // Cell hashing for neighbor checks (blue-noise dart throwing)
    private readonly Dictionary<Vector3Int, List<int>> _grid = new();
    private float _cellSize;

    private void GenerateSurfacePoissonPoints()
    {
        BuildWorldTriangles(_bakedMesh, _sourceTransform);
        BuildCDF();

        var pts = new List<Vector3>(maxPropagationPoints);
        var norms = new List<Vector3>(maxPropagationPoints);

        // snap fungusOrigin to nearest surface point (raycast fallback)
        var start = ProjectPointToSurface(fungusOrigin, out var startNormal);
        pts.Add(start);
        norms.Add(startNormal);
        StampToGrid(start, 0);

        _cellSize = fungusDensity / Mathf.Sqrt(3f); // safe cell length for 3D neighbor pruning

        // Dart throwing with attempts budget
        int attempts = maxPropagationPoints * 25; // generous budget
        while (pts.Count < maxPropagationPoints && attempts-- > 0)
        {
            // 1) pick a triangle by area
            var triIndex = SampleTriangleByArea();
            var tri = _tris[triIndex];

            // 2) sample within that triangle (barycentric)
            var (p, n) = SamplePointOnTriangle(tri);

            // 3) Poisson accept: check neighbor cells for spacing
            if (IsFarEnough(p, n, pts, norms))
            {
                pts.Add(p);
                norms.Add(n);
                StampToGrid(p, pts.Count - 1);
            }
        }

        propagationPoints = pts.ToArray();
        propagationNormals = norms.ToArray();
        //_ready = true;
    }

    private void BuildWorldTriangles(Mesh mesh, Transform xform)
    {
        _tris = new List<Tri>(mesh.triangles.Length / 3);

        var verts = mesh.vertices;
        var tris = mesh.triangles;
        var norms = mesh.normals;
        bool hasNormals = norms != null && norms.Length == verts.Length;

        for (int i = 0; i < tris.Length; i += 3)
        {
            var i0 = tris[i];
            var i1 = tris[i + 1];
            var i2 = tris[i + 2];

            var a = xform.TransformPoint(verts[i0]);
            var b = xform.TransformPoint(verts[i1]);
            var c = xform.TransformPoint(verts[i2]);

            Vector3 n;
            if (hasNormals)
            {
                // average vertex normals, transform to world and renormalize
                var na = xform.TransformDirection(norms[i0]);
                var nb = xform.TransformDirection(norms[i1]);
                var nc = xform.TransformDirection(norms[i2]);
                n = (na + nb + nc).normalized;
            }
            else
            {
                n = Vector3.Cross(b - a, c - a).normalized;
            }

            var area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;

            if (area > 1e-8f)
            {
                _tris.Add(new Tri { a = a, b = b, c = c, n = n, area = area });
            }
        }
    }

    private void BuildCDF()
    {
        _cdf = new float[_tris.Count];
        float sum = 0f;
        for (int i = 0; i < _tris.Count; i++)
        {
            sum += _tris[i].area;
            _cdf[i] = sum;
        }
        // normalize
        if (sum > 0f)
        {
            for (int i = 0; i < _cdf.Length; i++) _cdf[i] /= sum;
        }
    }

    private int SampleTriangleByArea()
    {
        float r = Random.value;
        // binary search
        int lo = 0, hi = _cdf.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (r <= _cdf[mid]) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    private static (Vector3 p, Vector3 n) SamplePointOnTriangle(Tri t)
    {
        // sqrt trick for uniform area sampling
        float r1 = Mathf.Sqrt(Random.value);
        float r2 = Random.value;
        float w0 = 1f - r1;
        float w1 = r1 * (1f - r2);
        float w2 = r1 * r2;
        var p = t.a * w0 + t.b * w1 + t.c * w2;
        return (p, t.n);
    }

    private Vector3 ProjectPointToSurface(Vector3 guess, out Vector3 normal)
    {
        // Cast from slightly above towards surface; fallback to closest tri
        normal = Vector3.up;
        if (Physics.Raycast(guess + Vector3.up * 0.5f, Vector3.down, out var hit, 5f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (hit.collider.gameObject == deadSource || hit.collider.transform.IsChildOf(_sourceTransform))
            {
                normal = hit.normal;
                return hit.point;
            }
        }

        // fallback: closest triangle center
        float best = float.PositiveInfinity;
        Vector3 bestP = guess;
        Vector3 bestN = Vector3.up;
        foreach (var t in _tris)
        {
            var center = (t.a + t.b + t.c) / 3f;
            float d = (center - guess).sqrMagnitude;
            if (d < best)
            {
                best = d;
                bestP = center;
                bestN = t.n;
            }
        }
        normal = bestN;
        return bestP;
    }

    private Vector3Int HashCell(Vector3 p)
    {
        return new Vector3Int(
            Mathf.FloorToInt(p.x / _cellSize),
            Mathf.FloorToInt(p.y / _cellSize),
            Mathf.FloorToInt(p.z / _cellSize));
    }

    private void StampToGrid(Vector3 p, int index)
    {
        var h = HashCell(p);
        if (!_grid.TryGetValue(h, out var list))
        {
            list = new List<int>(4);
            _grid[h] = list;
        }
        list.Add(index);
    }

    // Prevents points on opposite, very-close surfaces from blocking each other:
    // We add a normal-similarity check when enforcing the radius.
    private bool IsFarEnough(Vector3 p, Vector3 n, List<Vector3> pts, List<Vector3> norms)
    {
        var h = HashCell(p);
        var r = fungusDensity;
        var r2 = r * r;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var nh = new Vector3Int(h.x + dx, h.y + dy, h.z + dz);
                    if (!_grid.TryGetValue(nh, out var indices)) continue;
                    for (int i = 0; i < indices.Count; i++)
                    {
                        int idx = indices[i];
                        // Skip if surface orientation differs a lot (likely different side)
                        if (Vector3.Dot(n, norms[idx]) < 0.25f) continue;

                        var d2 = (pts[idx] - p).sqrMagnitude;
                        if (d2 < r2) return false;
                    }
                }
        return true;
    }

    #endregion

    #region Wave spawning

    private IEnumerator SpawnWave(List<int> indices)
    {
        // Build available prefab list once for this wave
        var available = FungusPool.Instance.GetAvailablePrefabs();
        if (available.Count == 0)
        {
            Debug.LogWarning("[FungusPropagator] No prefabs available in pool for this wave.");
            yield break;
        }

        // Kick off all SpawnOneCo coroutines in this wave
        foreach (var idx in indices)
        {
            StartCoroutine(SpawnOneCo(idx, available));
        }

        // Wait for overlap pacing before next wave
        float overlapWait = Mathf.Clamp01(instantiateOverlap) * fungusGrowTime;
        if (overlapWait > 0f)
            yield return new WaitForSeconds(overlapWait);
    }

    private IEnumerator SpawnOneCo(int idx, List<GameObject> availablePrefabs)
    {
        var p = propagationPoints[idx];
        var n = propagationNormals[idx];
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, n);

        // Pick from available only
        var prefab = availablePrefabs[Random.Range(0, availablePrefabs.Count)];
        var go = FungusPool.Instance.Get(prefab, p, rot, transform);

        if (go == null)
        {
            Debug.LogWarning("[FungusPropagator] Pool exhausted mid-wave, skipping point.");
            yield break;
        }

        var tr = go.transform;

        // Reset scale logic
        Vector3 targetScale = prefab.transform.localScale;
        tr.localScale = (idx == 0) ? Vector3.one * 0.1f : Vector3.zero;

        // Perlin noise grow time
        float noise = Mathf.PerlinNoise(
            p.x * perlinNoiseScale + perlinSeed * 0.123f,
            p.z * perlinNoiseScale + perlinSeed * 0.987f);
        float minFactor = 2f - perPointGrowTimeNoiseRange;
        float maxFactor = perPointGrowTimeNoiseRange;
        float factor = Mathf.Lerp(minFactor, maxFactor, noise);

        float t = 0f;
        float duration = fungusGrowTime * factor;

        while (t < duration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            float ease = 1f - Mathf.Pow(1f - k, 3f);
            tr.localScale = Vector3.LerpUnclamped(tr.localScale, targetScale, ease);
            yield return null;
        }

        tr.localScale = targetScale;
    }

    private void BuildPropagationBatches()
    {
        propagationBatches = new List<List<int>>();

        if (propagationPoints == null || propagationPoints.Length == 0)
            return;

        var origin = propagationPoints[0];
        int count = propagationPoints.Length;

        // Order indices by distance from origin (closest first)
        var order = new List<int>(count);
        for (int i = 0; i < count; i++) order.Add(i);

        order.Sort((i, j) =>
        {
            float di = (propagationPoints[i] - origin).sqrMagnitude;
            float dj = (propagationPoints[j] - origin).sqrMagnitude;
            return di.CompareTo(dj);
        });

        // Bucket points into rings � bucket size tuned to fungusDensity
        float bucketSize = Mathf.Max(0.75f * fungusDensity, 0.01f);
        float currentEdge = 0f;
        var currentBatch = new List<int>();

        foreach (var idx in order)
        {
            float d = Vector3.Distance(propagationPoints[idx], origin);
            if (d > currentEdge + bucketSize && currentBatch.Count > 0)
            {
                propagationBatches.Add(currentBatch);
                currentBatch = new List<int>();
                // advance edge near this distance (keeps buckets stable)
                currentEdge = d - (d % bucketSize);
            }
            currentBatch.Add(idx);
        }

        if (currentBatch.Count > 0) propagationBatches.Add(currentBatch);
    }

    #endregion

    #region Particles fit

    private void InstantiateAndFitParticle()
    {
        if (fungusParticle == null || deadSource == null) return;

        var ps = Instantiate(fungusParticle, transform);
        var psTr = ps.transform;

        // Compute bounds
        Bounds b;
        if (deadSource.TryGetComponent<SkinnedMeshRenderer>(out var smr))
        {
            // baked bounds (world-aligned)
            b = smr.bounds;
            if (alignParticleToSourceRotation)
            {
                // Better fit: use local bounds + rotation
                var baked = new Mesh();
                smr.BakeMesh(baked);
                var localB = baked.bounds;
                psTr.position = _sourceTransform.TransformPoint(localB.center);
                psTr.rotation = _sourceTransform.rotation;
                SetParticleBox(ps, localB.size);
                return;
            }
        }
        else if (deadSource.TryGetComponent<MeshRenderer>(out var mr))
        {
            b = mr.bounds;
        }
        else
        {
            b = new Bounds(deadSource.transform.position, Vector3.one * fungusDensity * 4f);
        }

        psTr.position = b.center;
        psTr.rotation = alignParticleToSourceRotation ? _sourceTransform.rotation : Quaternion.identity;

        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = b.size;
    }

    private static void SetParticleBox(ParticleSystem ps, Vector3 size)
    {
        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.scale = size;
    }

    #endregion
}

