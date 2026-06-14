using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class TreeTrunkMeshGenerator : MonoBehaviour
{
    public GameObject trunkParent; // Parent object containing all TrunkPieces
    public int radialSegments = 8; // Number of segments around the trunk's circumference

    public void GenerateTrunkMesh()
    {
        if (trunkParent == null)
        {
            Debug.LogError("Trunk parent is not assigned!");
            return;
        }

        // Collect all TrunkPiece transforms
        List<Transform> trunkPieces = new List<Transform>();
        foreach (Transform child in trunkParent.transform)
        {
            trunkPieces.Add(child);
        }

        if (trunkPieces.Count < 2)
        {
            Debug.LogWarning("Not enough TrunkPieces to generate a mesh.");
            return;
        }

        // Prepare mesh data
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();
        List<Vector2> uvs = new List<Vector2>();

        float totalHeight = 0f;

        for (int i = 0; i < trunkPieces.Count; i++)
        {
            Transform piece = trunkPieces[i];
            Vector3 position = piece.position;
            float radius = piece.localScale.x / 2f; // Assuming uniform scale for simplicity

            // Generate vertices for this segment
            for (int j = 0; j < radialSegments; j++)
            {
                float angle = (j / (float)radialSegments) * Mathf.PI * 2f;
                Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                vertices.Add(position + offset);

                // UV mapping (optional)
                uvs.Add(new Vector2(j / (float)radialSegments, totalHeight));
            }

            totalHeight += piece.localScale.y; // Increment height for UV mapping

            // Generate triangles to connect this segment to the previous one
            if (i > 0)
            {
                int baseIndex = vertices.Count - radialSegments * 2;
                for (int j = 0; j < radialSegments; j++)
                {
                    int current = baseIndex + j;
                    int next = baseIndex + (j + 1) % radialSegments;

                    // First triangle
                    triangles.Add(current);
                    triangles.Add(next);
                    triangles.Add(current + radialSegments);

                    // Second triangle
                    triangles.Add(next);
                    triangles.Add(next + radialSegments);
                    triangles.Add(current + radialSegments);
                }
            }
        }

        // Create the mesh
        Mesh mesh = new Mesh();
        mesh.vertices = vertices.ToArray();
        mesh.triangles = triangles.ToArray();
        mesh.uv = uvs.ToArray();
        mesh.RecalculateNormals();

        // Assign the mesh to the MeshFilter
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        meshFilter.mesh = mesh;
    }
}