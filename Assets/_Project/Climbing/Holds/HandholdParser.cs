using System.Collections.Generic;
using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing
{
    /// <summary>
    /// Parses a mesh into climb handholds by finding convex LEDGE EDGES — the grabbable lips. A lip is
    /// an edge shared by one up-facing face (the ledge TOP) and one near-vertical face (the WALL that
    /// drops away below it), folding CONVEXLY (sticking out, not a concave inside corner). See the
    /// article's C1 (convex ledge → accept) / C2 (concave corner → reject). Long ledges are subdivided
    /// at <see cref="Settings.MinHoldDistance"/>; near-duplicate holds are merged on a hash grid.
    ///
    /// Geometry-only — every hold gets RiskValue 0 / IconId 0 (resolves to the surface's fallback risk).
    /// Vertex-paint risk/icon ids are SHELVED (see SHELVED_IDEAS.md), not pursued for now. Runtime-safe
    /// (no UnityEditor types) so the same parser can run at bake time or, if ever needed, on spawn
    /// (the mesh must be Read/Write enabled in that case). The editor ClimbBakeWindow is the caller.
    /// </summary>
    public static class HandholdParser
    {
        public struct Settings
        {
            /// <summary>Subdivision spacing along a ledge edge + dedup radius (the article's MIN_HH_DIST).</summary>
            public float MinHoldDistance;
            /// <summary>A face counts as a ledge TOP when dot(faceNormal, up) ≥ this.</summary>
            public float TopFaceMinUpDot;
            /// <summary>A face counts as a WALL when |dot(faceNormal, up)| ≤ this.</summary>
            public float WallMaxUpDot;
            /// <summary>Edge is convex when dot(topNormal, wallFar − edgeMid) ≤ this (≈0; the wall folds away below the lip).</summary>
            public float ConvexBias;
            /// <summary>Vertices within this distance (m) are welded to one canonical index, so split
            /// vertices from import (hard edges / UV seams) don't break edge adjacency.</summary>
            public float WeldTolerance;
            /// <summary>Fallback "outward" direction used to prefer front-facing ledges over the side
            /// edges of protrusions, when the mesh's own dominant facing is degenerate (e.g. a symmetric
            /// box). Pass the surface's forward.</summary>
            public Vector3 OutwardReference;
            /// <summary>Drop holds whose grab normal faces further than this from the outward reference
            /// (dot &lt; this). 0.25 ≈ reject pure side/back edges; set −1 to keep every ledge.</summary>
            public float MinOutwardDot;

            public static Settings Default => new Settings
            {
                MinHoldDistance = 0.35f,
                TopFaceMinUpDot = 0.5f,
                WallMaxUpDot = 0.5f,
                ConvexBias = -0.001f,
                WeldTolerance = 0.001f,
                OutwardReference = Vector3.forward,
                MinOutwardDot = 0.25f
            };
        }

        private struct EdgeRec { public int va, vb, f0, f1; }
        private struct WorldHold { public Vector3 pos, outward, up; }

        /// <summary>
        /// Parses <paramref name="bakeMesh"/> into holds expressed in <paramref name="surfaceSpace"/>'s
        /// local space (ClimbHoldData convention: forward = outward grab normal, up = surface up). Ledge
        /// detection runs in world space (it needs gravity/up). Returns an empty list on bad input; the
        /// caller should check <c>bakeMesh.sharedMesh.isReadable</c> first and warn the dev otherwise.
        /// </summary>
        public static List<ClimbHoldData> Parse(MeshFilter bakeMesh, Transform surfaceSpace, Settings settings)
        {
            var result = new List<ClimbHoldData>();
            if (bakeMesh == null || bakeMesh.sharedMesh == null || surfaceSpace == null) return result;

            Mesh mesh = bakeMesh.sharedMesh;
            Vector3[] localVerts = mesh.vertices;
            int[] rawTris = mesh.triangles;
            if (localVerts.Length == 0 || rawTris.Length < 3) return result;

            // World-space vertices — ledge detection compares face normals against world up.
            Transform mt = bakeMesh.transform;
            var rawVerts = new Vector3[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++) rawVerts[i] = mt.TransformPoint(localVerts[i]);

            // WELD BY POSITION. Imported meshes split vertices at hard edges, UV seams and material
            // boundaries — and a ledge corner IS a hard edge — so index-based adjacency would treat both
            // sides of a lip as unrelated boundary edges and find nothing. Collapsing coincident positions
            // to one canonical index makes adjacency work regardless of how the mesh was authored (single
            // solid, many disconnected islands, or fully un-welded triangle soup).
            float weld = Mathf.Max(1e-5f, settings.WeldTolerance);
            float invWeld = 1f / weld;
            var weldMap = new Dictionary<Vector3Int, int>(rawVerts.Length);
            var canon = new int[rawVerts.Length];
            var cpos = new List<Vector3>(rawVerts.Length);
            for (int i = 0; i < rawVerts.Length; i++)
            {
                Vector3 p = rawVerts[i];
                var q = new Vector3Int(Mathf.RoundToInt(p.x * invWeld), Mathf.RoundToInt(p.y * invWeld), Mathf.RoundToInt(p.z * invWeld));
                if (weldMap.TryGetValue(q, out int ci)) canon[i] = ci;
                else { ci = cpos.Count; weldMap[q] = ci; cpos.Add(p); canon[i] = ci; }
            }
            Vector3[] verts = cpos.ToArray();                  // canonical (welded) positions
            int[] tris = new int[rawTris.Length];
            for (int k = 0; k < rawTris.Length; k++) tris[k] = canon[rawTris[k]];

            // Facet normals from the cross product (NOT smoothed mesh normals). Degenerate faces (slivers
            // collapsed by the weld) are skipped so they can't masquerade as up-facing ledge tops.
            int triCount = tris.Length / 3;
            var faceNormal = new Vector3[triCount];
            var faceValid = new bool[triCount];
            Vector3 areaNormalSum = Vector3.zero;   // Σ raw cross = area-weighted normal → dominant facing
            for (int f = 0; f < triCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;    // collapsed → degenerate
                Vector3 n = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                if (n.sqrMagnitude <= 1e-12f) continue;
                areaNormalSum += n;                 // raw cross magnitude = 2·area (area-weighted)
                faceNormal[f] = n.normalized;
                faceValid[f] = true;
            }

            // Edge → up to two adjacent faces (hash by sorted canonical vertex pair, O(n)). f1 == -2 = non-manifold.
            var edges = new Dictionary<long, EdgeRec>(triCount * 3);
            for (int f = 0; f < triCount; f++)
            {
                if (!faceValid[f]) continue;
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                AddEdge(edges, i0, i1, f);
                AddEdge(edges, i1, i2, f);
                AddEdge(edges, i2, i0, f);
            }

            float minDist = Mathf.Max(0.01f, settings.MinHoldDistance);
            var candidates = new List<WorldHold>();

            foreach (var kv in edges)
            {
                EdgeRec e = kv.Value;
                if (e.f0 < 0 || e.f1 < 0) continue;   // boundary or non-manifold edge

                Vector3 nA = faceNormal[e.f0];
                Vector3 nB = faceNormal[e.f1];
                float aUp = Vector3.Dot(nA, Vector3.up);
                float bUp = Vector3.Dot(nB, Vector3.up);

                // One face must be a ledge TOP (up-facing), the other a WALL (near-vertical).
                int topF, wallF;
                Vector3 topN, wallN;
                if (aUp >= settings.TopFaceMinUpDot && Mathf.Abs(bUp) <= settings.WallMaxUpDot)
                { topF = e.f0; wallF = e.f1; topN = nA; wallN = nB; }
                else if (bUp >= settings.TopFaceMinUpDot && Mathf.Abs(aUp) <= settings.WallMaxUpDot)
                { topF = e.f1; wallF = e.f0; topN = nB; wallN = nA; }
                else continue;

                Vector3 pa = verts[e.va], pb = verts[e.vb];
                Vector3 edgeMid = (pa + pb) * 0.5f;
                Vector3 wallFar = verts[FarVertex(tris, wallF, e.va, e.vb)];

                // Convexity (accept lip, reject inside corner): the wall's far vertex sits behind the top
                // face's normal (the wall folds away below the lip).
                if (Vector3.Dot(topN, wallFar - edgeMid) > settings.ConvexBias) continue;
                // Top-lip check: the wall actually descends from the edge (rejects underside / bottom lips).
                if (wallFar.y > edgeMid.y) continue;

                // Grab normal = the (horizontalised) wall normal; up = world up.
                Vector3 outward = Vector3.ProjectOnPlane(wallN, Vector3.up);
                outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : wallN;

                // Subdivide the edge so long ledges carry several holds.
                float len = Vector3.Distance(pa, pb);
                int n = Mathf.Max(1, Mathf.RoundToInt(len / minDist));
                for (int i = 0; i < n; i++)
                    candidates.Add(new WorldHold { pos = Vector3.Lerp(pa, pb, (i + 0.5f) / n), outward = outward, up = Vector3.up });
            }

            // Outward reference = the mesh's dominant (area-weighted) facing, so front-facing ledges are
            // preferred over the SIDE edges of protrusions (e.g. extruded bricks, whose top has a front
            // lip facing out plus two side lips facing along the wall). Falls back to the surface forward
            // when the average is degenerate (a symmetric box).
            Vector3 reference = areaNormalSum.sqrMagnitude > 1e-6f
                ? areaNormalSum.normalized
                : (settings.OutwardReference.sqrMagnitude > 1e-6f ? settings.OutwardReference.normalized : Vector3.forward);

            // Greedy dedup by outward score: best-aligned candidate wins a conflict, so each feature keeps
            // its FRONT lip deterministically instead of an arbitrary side edge. MinOutwardDot additionally
            // drops holds facing too far from the reference (removes stray side/back-facing holds).
            candidates.Sort((a, b) => Vector3.Dot(b.outward, reference).CompareTo(Vector3.Dot(a.outward, reference)));

            var grid = new Dictionary<Vector3Int, List<Vector3>>();
            var worldHolds = new List<WorldHold>(candidates.Count);
            for (int c = 0; c < candidates.Count; c++)
            {
                if (Vector3.Dot(candidates[c].outward, reference) < settings.MinOutwardDot) continue;
                if (TooClose(grid, candidates[c].pos, minDist)) continue;
                AddToGrid(grid, candidates[c].pos, minDist);
                worldHolds.Add(candidates[c]);
            }

            // Convert to the surface's local space (ClimbHoldData convention).
            Quaternion invSurf = Quaternion.Inverse(surfaceSpace.rotation);
            for (int i = 0; i < worldHolds.Count; i++)
            {
                WorldHold wh = worldHolds[i];
                Quaternion worldRot = Quaternion.LookRotation(wh.outward, wh.up);
                result.Add(new ClimbHoldData
                {
                    LocalPosition = surfaceSpace.InverseTransformPoint(wh.pos),
                    LocalRotation = invSurf * worldRot,
                    RiskValue = 0f,   // vertex-paint risk SHELVED (SHELVED_IDEAS.md); 0 → surface fallbackRisk
                    IconId = 0
                });
            }
            return result;
        }

        private static void AddEdge(Dictionary<long, EdgeRec> edges, int a, int b, int f)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (!edges.TryGetValue(key, out EdgeRec e))
                edges[key] = new EdgeRec { va = a, vb = b, f0 = f, f1 = -1 };
            else if (e.f1 == -1 && e.f0 != f) { e.f1 = f; edges[key] = e; }
            else { e.f1 = -2; edges[key] = e; }   // 3rd+ face → non-manifold, skip
        }

        private static int FarVertex(int[] tris, int f, int va, int vb)
        {
            int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
            if (i0 != va && i0 != vb) return i0;
            if (i1 != va && i1 != vb) return i1;
            return i2;
        }

        private static Vector3Int Cell(Vector3 p, float cell) =>
            new Vector3Int(Mathf.FloorToInt(p.x / cell), Mathf.FloorToInt(p.y / cell), Mathf.FloorToInt(p.z / cell));

        private static bool TooClose(Dictionary<Vector3Int, List<Vector3>> grid, Vector3 p, float minDist)
        {
            Vector3Int c = Cell(p, minDist);
            float minSqr = minDist * minDist;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    for (int z = -1; z <= 1; z++)
                        if (grid.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out List<Vector3> bucket))
                            for (int i = 0; i < bucket.Count; i++)
                                if ((bucket[i] - p).sqrMagnitude < minSqr) return true;
            return false;
        }

        private static void AddToGrid(Dictionary<Vector3Int, List<Vector3>> grid, Vector3 p, float minDist)
        {
            Vector3Int c = Cell(p, minDist);
            if (!grid.TryGetValue(c, out List<Vector3> bucket)) { bucket = new List<Vector3>(); grid[c] = bucket; }
            bucket.Add(p);
        }
    }
}
