using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class HookshotRope : MonoBehaviour
{
    private Transform start;
    private Transform end;

    private Mesh mesh;
    private Vector3[] baseVertices;
    private Vector3[] deformedVertices;

    // --- Rope generation parameters ---
    private int cylinderSides;
    private float loopDistance;
    private float ropeRadius;

    // --- Wave Parameters ---
    [Header("Wave Settings")]
    public float waveFrequency = 5f;
    public float waveAmplitude = 0.2f;
    public float waveDampingSpeed = 2f;
    public float hitDampingMultiplier = 5f;

    private float currentAmplitude;
    private float elapsedTime;
    private bool hasHit = false;

    // ------------------------------------------------------------

    public void Init(Transform start, Transform end, int sides, float loopDist, float ropeWidth)
    {
        this.start = start;
        this.end = end;
        this.cylinderSides = Mathf.Max(3, sides);
        this.loopDistance = loopDist;
        this.ropeRadius = ropeWidth * 0.5f;

        MeshFilter mf = GetComponent<MeshFilter>();
        mesh = new Mesh { name = "HookshotRopeMesh" };
        mf.mesh = mesh;

        GenerateCylinder();

        baseVertices = mesh.vertices;
        deformedVertices = new Vector3[baseVertices.Length];

        currentAmplitude = waveAmplitude;
    }

    // ------------------------------------------------------------

    private void LateUpdate()
    {
        if (mesh == null || start == null || end == null) return;

        elapsedTime += Time.deltaTime;

        float damping = hasHit ? waveDampingSpeed * hitDampingMultiplier : waveDampingSpeed;
        currentAmplitude = Mathf.MoveTowards(currentAmplitude, 0f, damping * Time.deltaTime);

        UpdateTransform();
        ApplyWaveDeformation();
    }

    private void UpdateTransform()
    {
        // Position rope at start
        transform.position = start.position;

        // Rotate to face end
        Vector3 dir = (end.position - start.position).normalized;
        if (dir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

        // Scale along Z to reach end
        float length = Vector3.Distance(start.position, end.position);
        transform.localScale = new Vector3(1f, 1f, length);
    }

    private void ApplyWaveDeformation()
    {
        for (int i = 0; i < baseVertices.Length; i++)
        {
            Vector3 v = baseVertices[i];

            // along = normalized Z position (since mesh runs 0..1 in Z)
            float along = v.z;

            if (along <= 0f || along >= 1f)
            {
                deformedVertices[i] = v;
                continue;
            }

            float wave = Mathf.Sin(elapsedTime * waveFrequency + along * Mathf.PI * 2f);
            // offset in X direction (local space)
            Vector3 offset = Vector3.right * (wave * currentAmplitude);

            deformedVertices[i] = v + offset;
        }

        mesh.vertices = deformedVertices;
        mesh.RecalculateNormals();
    }

    // ------------------------------------------------------------
    // Mesh Generator: simple cylinder along +Z from 0..1
    private void GenerateCylinder()
    {
        int loops = Mathf.Max(2, 16); // fixed resolution for now
        int vertsPerLoop = cylinderSides + 1;
        int vertCount = loops * vertsPerLoop;

        Vector3[] vertices = new Vector3[vertCount];
        Vector2[] uvs = new Vector2[vertCount];
        int[] triangles = new int[(loops - 1) * cylinderSides * 6];

        float angleStep = 2 * Mathf.PI / cylinderSides;

        for (int loop = 0; loop < loops; loop++)
        {
            float z = (float)loop / (loops - 1); // 0..1
            for (int side = 0; side <= cylinderSides; side++)
            {
                float angle = side * angleStep;
                float x = Mathf.Cos(angle) * ropeRadius;
                float y = Mathf.Sin(angle) * ropeRadius;
                int idx = loop * vertsPerLoop + side;

                vertices[idx] = new Vector3(x, y, z);
                uvs[idx] = new Vector2((float)side / cylinderSides, z);
            }
        }

        int triIndex = 0;
        for (int loop = 0; loop < loops - 1; loop++)
        {
            for (int side = 0; side < cylinderSides; side++)
            {
                int curr = loop * vertsPerLoop + side;
                int next = curr + vertsPerLoop;

                triangles[triIndex++] = curr;
                triangles[triIndex++] = curr + 1;
                triangles[triIndex++] = next;

                triangles[triIndex++] = curr + 1;
                triangles[triIndex++] = next + 1;
                triangles[triIndex++] = next;
            }
        }

        mesh.Clear();
        mesh.vertices = vertices;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
    }

    // ------------------------------------------------------------

    public void OnHookshotHit()
    {
        hasHit = true;
    }
}
