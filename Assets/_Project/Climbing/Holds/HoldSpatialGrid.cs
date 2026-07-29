using System.Collections.Generic;
using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing
{
    /// <summary>
    /// Uniform spatial hash over ONE surface's holds in WORLD space, for radius neighbour queries.
    /// Built once (e.g. when a carbine is placed) and thrown away — it snapshots world positions, so
    /// it does NOT track a moving surface. Cell size is set to the query radius, so a radius query
    /// only ever touches a 3×3×3 block of cells. Used by <see cref="HoldGeodesic"/> to expand the
    /// tether's reachable-hold set without any per-pair O(N²) scan.
    /// </summary>
    public sealed class HoldSpatialGrid
    {
        private readonly float _cell;
        private readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();
        private readonly Vector3[] _pos;
        private readonly Vector3[] _normal;

        /// <summary>World positions, index-parallel to the source hold list.</summary>
        public Vector3[] Positions => _pos;
        /// <summary>World outward (grab) normals, index-parallel to the source hold list.</summary>
        public Vector3[] Normals => _normal;
        public int Count => _pos.Length;

        public HoldSpatialGrid(IReadOnlyList<ClimbHoldData> holds, Transform surface, float cellSize)
        {
            _cell = Mathf.Max(0.05f, cellSize);
            int n = holds.Count;
            _pos = new Vector3[n];
            _normal = new Vector3[n];
            Quaternion sr = surface.rotation;
            for (int i = 0; i < n; i++)
            {
                Vector3 wp = surface.TransformPoint(holds[i].LocalPosition);
                _pos[i] = wp;
                _normal[i] = (sr * holds[i].LocalRotation) * Vector3.forward;
                Vector3Int c = CellOf(wp);
                if (!_cells.TryGetValue(c, out var list)) { list = new List<int>(); _cells[c] = list; }
                list.Add(i);
            }
        }

        private Vector3Int CellOf(Vector3 p) => new Vector3Int(
            Mathf.FloorToInt(p.x / _cell), Mathf.FloorToInt(p.y / _cell), Mathf.FloorToInt(p.z / _cell));

        /// <summary>Fills <paramref name="result"/> (cleared first) with the indices of all holds within
        /// <paramref name="radius"/> of <paramref name="center"/>. Handles radius &gt; cell by widening
        /// the scanned block, but is cheapest when radius ≈ the cell size passed to the constructor.</summary>
        public void QueryInRadius(Vector3 center, float radius, List<int> result)
        {
            result.Clear();
            float r2 = radius * radius;
            Vector3Int c = CellOf(center);
            int span = Mathf.Max(1, Mathf.CeilToInt(radius / _cell));
            for (int x = -span; x <= span; x++)
                for (int y = -span; y <= span; y++)
                    for (int z = -span; z <= span; z++)
                    {
                        if (!_cells.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list)) continue;
                        for (int k = 0; k < list.Count; k++)
                        {
                            int idx = list[k];
                            if ((_pos[idx] - center).sqrMagnitude <= r2) result.Add(idx);
                        }
                    }
        }

        /// <summary>Index of the hold nearest a world point (linear — used once, for the anchor).</summary>
        public int NearestIndex(Vector3 p)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < _pos.Length; i++)
            {
                float d = (_pos[i] - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }
    }
}
