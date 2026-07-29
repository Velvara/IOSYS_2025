using System.Collections.Generic;
using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// Surface-following ("geodesic") reachability over a surface's hold graph. Starting at an anchor
    /// hold, it hops hold → neighbouring hold — each neighbour within a link radius and facing the same
    /// way — accumulating the straight distance of each hop, and marks every hold whose accumulated
    /// path stays within a length budget. This is the carbine tether's "how far can the rope reach
    /// along the wall": measured PROGRESSIVELY hold to hold (never a sphere / geometry cast), so the
    /// reach wraps around the surface instead of cutting through it, and can't shortcut across a gap
    /// or around a convex corner. One-time per carbine placement; allocates a few working buffers.
    /// </summary>
    public static class HoldGeodesic
    {
        /// <param name="linkRadius">Two holds are adjacent (a single hop) only within this distance —
        /// set to the climber's max hand-step reach, so "adjacent" means "steppable between".</param>
        /// <param name="linkFacingDot">Two holds only link if their outward normals agree by at least
        /// this dot — keeps the path on one face, so it can't wrap around a pillar's far side.</param>
        /// <param name="reachable">Result, sized ≥ grid.Count: true for every reachable hold.</param>
        /// <returns>Count of reachable holds (includes the anchor).</returns>
        public static int ComputeReachable(HoldSpatialGrid grid, int anchor, float budget,
                                           float linkRadius, float linkFacingDot, bool[] reachable)
        {
            if (grid == null || reachable == null) return 0;
            int n = grid.Count;
            for (int i = 0; i < n; i++) reachable[i] = false;
            if ((uint)anchor >= (uint)n || reachable.Length < n) return 0;

            var dist = new float[n];
            for (int i = 0; i < n; i++) dist[i] = float.PositiveInfinity;
            dist[anchor] = 0f;

            var heap = new MinHeap(n);
            heap.Push(anchor, 0f);
            var nbr = new List<int>(64);
            Vector3[] pos = grid.Positions;
            Vector3[] nrm = grid.Normals;
            int reached = 0;

            while (heap.Pop(out int u, out float du))
            {
                if (du > dist[u]) continue;          // stale heap entry (a shorter path already settled u)
                reachable[u] = true;
                reached++;

                Vector3 pu = pos[u];
                Vector3 nu = nrm[u];
                grid.QueryInRadius(pu, linkRadius, nbr);
                for (int k = 0; k < nbr.Count; k++)
                {
                    int v = nbr[k];
                    if (v == u) continue;
                    if (Vector3.Dot(nu, nrm[v]) < linkFacingDot) continue;   // stay on the same face
                    float nd = du + Vector3.Distance(pu, pos[v]);
                    if (nd <= budget && nd < dist[v]) { dist[v] = nd; heap.Push(v, nd); }
                }
            }
            return reached;
        }

        // Compact binary min-heap keyed on float (no System.Collections.Generic.PriorityQueue on this
        // runtime). Entries are (hold index, accumulated distance); duplicates are tolerated and pruned
        // by the stale-entry check above (standard lazy-deletion Dijkstra).
        private struct Node { public int Index; public float Key; }

        private sealed class MinHeap
        {
            private Node[] _a;
            private int _n;

            public MinHeap(int cap) { _a = new Node[Mathf.Max(16, cap)]; _n = 0; }

            public void Push(int index, float key)
            {
                if (_n == _a.Length) System.Array.Resize(ref _a, _a.Length * 2);
                int i = _n++;
                _a[i] = new Node { Index = index, Key = key };
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (_a[p].Key <= _a[i].Key) break;
                    (_a[p], _a[i]) = (_a[i], _a[p]);
                    i = p;
                }
            }

            public bool Pop(out int index, out float key)
            {
                if (_n == 0) { index = -1; key = 0f; return false; }
                index = _a[0].Index;
                key = _a[0].Key;
                _a[0] = _a[--_n];
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, s = i;
                    if (l < _n && _a[l].Key < _a[s].Key) s = l;
                    if (r < _n && _a[r].Key < _a[s].Key) s = r;
                    if (s == i) break;
                    (_a[s], _a[i]) = (_a[i], _a[s]);
                    i = s;
                }
                return true;
            }
        }
    }
}
