using UnityEngine;

/// <summary>
/// Reusable tube-mesh builder for rope strands — the same construction VerletRopeTail renders
/// with, factored out so both tether sims (verlet / PhysX) share one renderer. Vertices are
/// WORLD-space: keep the owning GameObject at identity (position zero, no rotation, scale one).
/// </summary>
public class RopeTubeMesh
{
    private readonly Mesh mesh;
    private readonly int sides;
    private readonly float radius;
    private readonly int rings;
    private readonly Vector3[] verts;

    public Mesh Mesh => mesh;

    public RopeTubeMesh(int rings, int sides, float radius, string name)
    {
        this.rings = Mathf.Max(2, rings);
        this.sides = Mathf.Max(3, sides);
        this.radius = radius;

        mesh = new Mesh { name = name };
        int vertsPerRing = this.sides + 1;
        verts = new Vector3[this.rings * vertsPerRing];
        var uvs = new Vector2[verts.Length];
        var triangles = new int[(this.rings - 1) * this.sides * 6];

        for (int r = 0; r < this.rings; r++)
            for (int s = 0; s <= this.sides; s++)
                uvs[r * vertsPerRing + s] = new Vector2((float)s / this.sides, r / (this.rings - 1f));

        int ti = 0;
        for (int r = 0; r < this.rings - 1; r++)
        {
            for (int s = 0; s < this.sides; s++)
            {
                int curr = r * vertsPerRing + s;
                int next = curr + vertsPerRing;
                triangles[ti++] = curr;
                triangles[ti++] = curr + 1;
                triangles[ti++] = next;
                triangles[ti++] = curr + 1;
                triangles[ti++] = next + 1;
                triangles[ti++] = next;
            }
        }

        mesh.vertices = verts;   // placeholder until the first UpdateCenters
        mesh.uv = uvs;
        mesh.triangles = triangles;
    }

    /// <summary>Rebuilds the tube around the given centerline (must be exactly the ring count).</summary>
    public void UpdateCenters(Vector3[] centers)
    {
        int vertsPerRing = sides + 1;
        for (int r = 0; r < rings; r++)
        {
            Vector3 tangent = r == 0 ? centers[1] - centers[0]
                            : r == rings - 1 ? centers[r] - centers[r - 1]
                            : centers[r + 1] - centers[r - 1];
            if (tangent.sqrMagnitude < 1e-8f) tangent = Vector3.down;
            tangent.Normalize();

            Vector3 side1 = Vector3.Cross(tangent, Vector3.up);
            if (side1.sqrMagnitude < 1e-4f) side1 = Vector3.Cross(tangent, Vector3.right);
            side1.Normalize();
            Vector3 side2 = Vector3.Cross(tangent, side1);

            for (int s = 0; s <= sides; s++)
            {
                float angle = s * (2f * Mathf.PI / sides);
                verts[r * vertsPerRing + s] = centers[r] +
                    (side1 * Mathf.Cos(angle) + side2 * Mathf.Sin(angle)) * radius;
            }
        }

        mesh.vertices = verts;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }
}
