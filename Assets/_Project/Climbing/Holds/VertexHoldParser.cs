using System.Collections.Generic;
using UnityEngine;
using Game.Core.Climbing;

namespace Game.Climbing
{
    /// <summary>
    /// Parses a mesh (or a whole cluster of kit-bashed meshes) into climb handholds by making EVERY
    /// (unique) vertex a hold — the generic counterpart to <see cref="HandholdParser"/>'s convex-lip
    /// detection, and the same idea as Flora's per-ring trunk emission (TrunkGenerator.EmitClimbHolds)
    /// generalised to arbitrary geometry. Use it on purpose-built low-poly climb proxies where the
    /// vertex lattice itself defines the grab points (a bumpy cliff proxy, a bouldering wall), not on
    /// dense art meshes.
    ///
    /// It does NOT space holds out (no distance thinning) — one hold per vertex is the contract. It
    /// only WELDS coincident positions so import-split vertices (hard edges / UV seams) — and, when
    /// several meshes are passed, the touching vertices of INTERSECTING kit-bash pieces — collapse to
    /// one hold instead of stacking. It CULLS inward/back-facing and flat-top vertices so the holds sit
    /// on the grabbable outer skin and the climb ends at the top edge (the runtime's existing
    /// AtSurfaceTop + ComputeMantleLanding then owns top-out — no ledges are baked here).
    ///
    /// Passing every piece of a kit-bashed cluster in one call is how a group of intersecting meshes
    /// becomes ONE seamless hold set on a single ClimbableSurface (the shared weld merges the seams and
    /// the facet-normal accumulation averages normals across touching pieces), so runtime traversal —
    /// which climbs one _currentSurface — crosses the whole cluster without a multi-surface handoff.
    ///
    /// Geometry-only: every hold gets RiskValue 0 / IconId 0 (slip risk is painted per scene
    /// instance instead — ClimbableSurface.RiskClassOf / Tools/Climbing/Risk Painter).
    /// Runtime-safe (no UnityEditor types). ClimbBakeWindow is the caller today.
    /// </summary>
    public static class VertexHoldParser
    {
        public struct Settings
        {
            /// <summary>Vertices within this distance (m) weld to one hold, so import-split vertices
            /// (hard edges / UV seams / material boundaries) — and the touching verts of intersecting
            /// kit-bash pieces — don't stack duplicate holds on one point. Exact-duplicate merge, NOT
            /// spatial thinning; set very small to keep near-coincident verts distinct.</summary>
            public float WeldTolerance;

            /// <summary>Cull inward/back-facing vertices: drop a hold whose outward normal faces further
            /// than this from the outward reference (dot &lt; this). 0 ≈ keep the outward-facing hemisphere
            /// and drop the back; set −1 to keep every side; raise toward 1 for a flat wall you only
            /// approach head-on. Applied only when the geometry has a DOMINANT facing (a wall-like sheet):
            /// closed shapes (a free-standing rock, a merged kit-bash cluster) skip this cull automatically —
            /// their area normals cancel, so there is no meaningful inward hemisphere to cull against.</summary>
            public float MinOutwardDot;

            /// <summary>Cull near-flat TOP-surface vertices: drop a hold whose normal faces further UP than
            /// this (dot(normal, up) &gt; this). Keeps the climb on the walls so it ends at the top EDGE and
            /// the runtime mantles onto the surface (no floor-grab holds). 0.85 ≈ drop faces within ~30° of
            /// straight up; set 1.01 to keep flat tops too.</summary>
            public float MaxUpDot;

            /// <summary>Fallback "outward" direction used when the meshes' own dominant (area-weighted)
            /// facing is degenerate (a symmetric shape). Pass the surface's forward.</summary>
            public Vector3 OutwardReference;

            public static Settings Default => new Settings
            {
                WeldTolerance = 0.001f,
                MinOutwardDot = 0f,
                MaxUpDot = 0.85f,
                OutwardReference = Vector3.forward
            };
        }

        /// <summary>Per-stage counts of a parse — makes "fewer holds than expected" attributable to the
        /// stage that dropped them (the bake window logs these).</summary>
        public struct Stats
        {
            public int UniqueVerts;              // after the weld
            public int Isolated;                 // no non-degenerate facet after the weld
            public int CulledInward;             // hemisphere cull (0 when not applied)
            public int CulledTop;                // MaxUpDot cull
            public bool HemisphereCullApplied;   // false = closed/no-dominant-facing geometry, cull skipped
            public int Holds;
        }

        /// <summary>Single-mesh parse — the common case (see the list overload for kit-bash clusters).</summary>
        public static List<ClimbHoldData> Parse(MeshFilter bakeMesh, Transform surfaceSpace, Settings settings)
            => Parse(bakeMesh != null ? new[] { bakeMesh } : System.Array.Empty<MeshFilter>(), surfaceSpace, settings, out _);

        public static List<ClimbHoldData> Parse(IReadOnlyList<MeshFilter> meshes, Transform surfaceSpace, Settings settings)
            => Parse(meshes, surfaceSpace, settings, out _);

        /// <summary>
        /// Parses one or more meshes into per-vertex holds expressed in <paramref name="surfaceSpace"/>'s
        /// local space (ClimbHoldData convention: forward = outward grab normal, up = surface up). All
        /// meshes weld into ONE shared vertex set, so intersecting kit-bash pieces merge at their seams.
        /// Returns an empty list on bad input; the caller should check each mesh's <c>isReadable</c> first
        /// and warn the dev otherwise (unreadable meshes are silently skipped here).
        /// </summary>
        public static List<ClimbHoldData> Parse(IReadOnlyList<MeshFilter> meshes, Transform surfaceSpace, Settings settings, out Stats stats)
        {
            stats = default;
            var result = new List<ClimbHoldData>();
            if (meshes == null || meshes.Count == 0 || surfaceSpace == null) return result;

            // WELD BY POSITION across EVERY mesh into one canonical vertex set. Imported meshes split
            // vertices at hard edges / UV seams / material boundaries, and kit-bashed pieces meet along
            // shared faces — collapsing coincident world positions to one canonical vertex yields exactly
            // one hold per real point and averages the facets meeting there (across pieces too). NOT
            // spatial thinning — only exact duplicates merge.
            float weld = Mathf.Max(1e-5f, settings.WeldTolerance);
            float invWeld = 1f / weld;
            var weldMap = new Dictionary<Vector3Int, int>();
            var cpos = new List<Vector3>();     // canonical world positions
            var tris = new List<int>();         // canonical triangle indices, all meshes concatenated

            for (int m = 0; m < meshes.Count; m++)
            {
                MeshFilter mf = meshes[m];
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                Mesh mesh = mf.sharedMesh;
                Vector3[] localVerts = mesh.vertices;
                int[] meshTris = mesh.triangles;
                if (localVerts.Length == 0 || meshTris.Length < 3) continue;

                Transform mt = mf.transform;
                var localCanon = new int[localVerts.Length];
                for (int i = 0; i < localVerts.Length; i++)
                {
                    Vector3 p = mt.TransformPoint(localVerts[i]);   // world space — the top/back culls need world up
                    var q = new Vector3Int(Mathf.RoundToInt(p.x * invWeld), Mathf.RoundToInt(p.y * invWeld), Mathf.RoundToInt(p.z * invWeld));
                    if (weldMap.TryGetValue(q, out int ci)) localCanon[i] = ci;
                    else { ci = cpos.Count; weldMap[q] = ci; cpos.Add(p); localCanon[i] = ci; }
                }
                for (int k = 0; k < meshTris.Length; k++) tris.Add(localCanon[meshTris[k]]);
            }

            int vertCount = cpos.Count;
            stats.UniqueVerts = vertCount;
            if (vertCount == 0 || tris.Count < 3) return result;

            // Per-vertex outward normal = area-weighted sum of adjacent FACET normals (raw cross, so bigger
            // faces weigh more). Computed from geometry, not mesh.normals, so it's correct regardless of how
            // the mesh was imported. Σ over all faces also gives the cluster's dominant facing for the
            // outward reference.
            var vNormal = new Vector3[vertCount];
            Vector3 areaNormalSum = Vector3.zero;
            float totalArea = 0f;
            int triCount = tris.Count / 3;
            for (int f = 0; f < triCount; f++)
            {
                int i0 = tris[f * 3], i1 = tris[f * 3 + 1], i2 = tris[f * 3 + 2];
                if (i0 == i1 || i1 == i2 || i0 == i2) continue;               // degenerate after weld
                Vector3 n = Vector3.Cross(cpos[i1] - cpos[i0], cpos[i2] - cpos[i0]);
                if (n.sqrMagnitude <= 1e-12f) continue;
                areaNormalSum += n;
                totalArea += n.magnitude;
                vNormal[i0] += n; vNormal[i1] += n; vNormal[i2] += n;         // area-weighted accumulation
            }

            // The hemisphere cull only means something when the geometry has a dominant facing (a wall-like
            // sheet). On CLOSED geometry (a free-standing rock, a merged kit-bash cluster) the area normals
            // cancel (Σ ≈ 0 relative to total area), there is no meaningful "inward", and culling against the
            // near-random residual — or the surface-forward fallback — just slices the hold set in half along
            // an arbitrary axis. Skip the cull then; burial inside neighbouring pieces is the bake's occlusion
            // cull's job, not a direction cull's.
            //
            // HORIZONTAL component only: an OPEN bake mesh (deleted bottom / buried faces — common on
            // kit-bash proxies) leaves a big VERTICAL residual the size of its hole. That residual is
            // meaningless as a wall facing (MaxUpDot owns tops) — and worse, with the reference pointing
            // up every side-face vertex sat EXACTLY on the hemisphere boundary, where float noise culled
            // a pseudo-random half of them (the "random missing holds on a flat face" bake bug). Only a
            // sideways-facing sheet counts as having a dominant facing.
            Vector3 dominantFlat = Vector3.ProjectOnPlane(areaNormalSum, Vector3.up);
            bool hasDominantFacing = dominantFlat.magnitude > 0.15f * totalArea;

            Vector3 reference = hasDominantFacing ? dominantFlat.normalized
                : areaNormalSum.sqrMagnitude > 1e-6f ? areaNormalSum.normalized
                : settings.OutwardReference.sqrMagnitude > 1e-6f ? settings.OutwardReference.normalized
                : Vector3.forward;

            Quaternion invSurf = Quaternion.Inverse(surfaceSpace.rotation);

            stats.HemisphereCullApplied = hasDominantFacing;
            for (int v = 0; v < vertCount; v++)
            {
                if (vNormal[v].sqrMagnitude <= 1e-10f) { stats.Isolated++; continue; }   // isolated vertex, no facing

                Vector3 n = vNormal[v].normalized;

                // Margin below the threshold so verts RIDING the boundary (a face exactly perpendicular
                // to the reference) resolve deterministically to KEEP instead of by float noise.
                if (hasDominantFacing && Vector3.Dot(n, reference) < settings.MinOutwardDot - 1e-3f)
                { stats.CulledInward++; continue; }                                      // inward/back-facing (sheets only)
                if (Vector3.Dot(n, Vector3.up) > settings.MaxUpDot) { stats.CulledTop++; continue; }   // flat top surface

                // Grab basis: horizontalise the normal into an outward grab dir with world-up as the hold up
                // (matches the wall convention in HandholdParser / the trunk). Near-vertical normals (steep
                // over/undershoots that survive the top cull) keep the full normal with any perpendicular up.
                Vector3 outward, upAxis;
                Vector3 horiz = Vector3.ProjectOnPlane(n, Vector3.up);
                if (horiz.sqrMagnitude > 1e-4f)
                {
                    outward = horiz.normalized;
                    upAxis = Vector3.up;
                }
                else
                {
                    outward = n;
                    Vector3 refUp = Vector3.ProjectOnPlane(reference, outward);
                    upAxis = refUp.sqrMagnitude > 1e-4f ? refUp.normalized : Vector3.forward;
                }

                Quaternion worldRot = Quaternion.LookRotation(outward, upAxis);
                result.Add(new ClimbHoldData
                {
                    LocalPosition = surfaceSpace.InverseTransformPoint(cpos[v]),
                    LocalRotation = invSurf * worldRot,
                    RiskValue = 0f,   // unused — slip risk lives in the per-instance paint (ClimbRiskClass)
                    IconId = 0
                });
            }

            stats.Holds = result.Count;
            return result;
        }
    }
}
