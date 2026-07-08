using UnityEngine;

/// <summary>
/// The rope's UNUSED remainder: a free-hanging verlet strand pinned at one end (last hand hold
/// while held, the ledge pivot when dropped). Carries no tension — one end free — which is the
/// case verlet handles perfectly: it just dangles, drapes and piles on the ground.
///
/// Performance contract: simulates while held/settling, freezes completely after a drop settles
/// (BeginSettle), so parked ropes cost nothing per frame. Rest length changes live (rope pays
/// out/reels as the player descends/ascends); at ~0 the tail hides entirely.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class VerletRopeTail : MonoBehaviour
{
    [Header("Simulation")]
    public int particleCount = 24;
    public int constraintIterations = 4;
    [Range(0.8f, 1f)] public float damping = 0.97f;
    public float gravityScale = 1f;
    public float particleRadius = 0.05f;
    public LayerMask collisionMask = ~0;
    [Range(0f, 1f)] public float contactFriction = 0.6f;
    [Tooltip("How strongly the strand is pulled back toward hanging STRAIGHT DOWN under its pin (per " +
             "second), weighted toward the free tip — as if a weight hung on the end. Keeps a tail that " +
             "brushes geometry from drifting away without bound; it drapes and settles downward instead. " +
             "0 = pure verlet (may creep along surfaces forever).")]
    public float hangReturn = 2f;

    [Header("Rendering")]
    public int sides = 6;
    public float ropeRadius = 0.03f;

    private Transform pin;
    private Transform ignoreRootA, ignoreRootB;
    private Vector3[] positions;
    private Vector3[] previous;
    private float restLength;
    private bool frozen;
    private float settleTimer = -1f;

    private Mesh mesh;
    private Vector3[] verts;
    private MeshRenderer meshRenderer;

    private static readonly Collider[] OverlapBuffer = new Collider[4];

    // ------------------------------------------------------------

    public void Init(Transform pinPoint, Material material)
    {
        pin = pinPoint;
        meshRenderer = GetComponent<MeshRenderer>();
        meshRenderer.material = material;

        particleCount = Mathf.Max(4, particleCount);
        positions = new Vector3[particleCount];
        previous = new Vector3[particleCount];
        for (int i = 0; i < particleCount; i++)
        {
            positions[i] = pinPoint.position + Vector3.down * (i * 0.02f);
            previous[i] = positions[i];
        }

        BuildMesh();
        frozen = false;
    }

    public void SetPin(Transform pinPoint) => pin = pinPoint;

    /// <summary>True while the tail has meaningful length to render/grab.</summary>
    public bool HasLength => restLength > 0.05f && positions != null;

    /// <summary>The free (unpinned) end of the strand — the rope's dangling tip.</summary>
    public Vector3 FreeEnd => positions != null
        ? positions[positions.Length - 1]
        : (pin != null ? pin.position : transform.position);

    /// <summary>Colliders under these roots are ignored (the anchor's pieces, the player).</summary>
    public void SetIgnoreRoots(Transform a, Transform b)
    {
        ignoreRootA = a;
        ignoreRootB = b;
    }

    /// <summary>Unused rope hanging from the pin. ~0 hides the tail (rope fully paid out).</summary>
    public void SetRestLength(float length)
    {
        restLength = Mathf.Max(0f, length);
        bool visible = restLength > 0.05f;
        if (meshRenderer != null && meshRenderer.enabled != visible)
            meshRenderer.enabled = visible;
    }

    public void WakeUp()
    {
        frozen = false;
        settleTimer = -1f;
    }

    /// <summary>Rope dropped: keep simulating this long so the tail falls into place, then freeze.</summary>
    public void BeginSettle(float seconds)
    {
        frozen = false;
        settleTimer = Mathf.Max(0.1f, seconds);
    }

    /// <summary>Closest point on the strand + arc length from the pinned end (re-grab queries).</summary>
    public Vector3 NearestPoint(Vector3 point, out float arcFromPin)
    {
        Vector3 best = positions[0];
        float bestSqr = float.MaxValue;
        arcFromPin = 0f;
        float accumulated = 0f;

        for (int i = 0; i < positions.Length - 1; i++)
        {
            Vector3 a = positions[i];
            Vector3 ab = positions[i + 1] - a;
            float sqrLen = ab.sqrMagnitude;
            float t = sqrLen > 0.0001f ? Mathf.Clamp01(Vector3.Dot(point - a, ab) / sqrLen) : 0f;
            Vector3 p = a + ab * t;
            float segLen = Mathf.Sqrt(sqrLen);

            float sqr = (point - p).sqrMagnitude;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                best = p;
                arcFromPin = accumulated + t * segLen;
            }
            accumulated += segLen;
        }
        return best;
    }

    // ------------------------------------------------------------

    private void LateUpdate()
    {
        if (frozen || pin == null || positions == null || restLength <= 0.05f) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Simulate(dt);
        UpdateMesh();

        if (settleTimer > 0f)
        {
            settleTimer -= dt;
            if (settleTimer <= 0f) frozen = true;   // parked pile baked — zero cost from here
        }
    }

    private void Simulate(float dt)
    {
        int n = positions.Length;
        Vector3 gravityStep = Physics.gravity * (gravityScale * dt * dt);

        // Integrate everything but the pinned first particle. The last is FREE — no tension.
        for (int i = 1; i < n; i++)
        {
            Vector3 current = positions[i];
            positions[i] += (current - previous[i]) * damping + gravityStep;
            previous[i] = current;
        }
        positions[0] = pin.position;

        float segmentRest = restLength / (n - 1);
        for (int iter = 0; iter < constraintIterations; iter++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 delta = positions[i + 1] - positions[i];
                float dist = delta.magnitude;
                if (dist < 0.0001f) continue;
                float correction = (dist - segmentRest) / dist * 0.5f;
                Vector3 offset = delta * correction;
                if (i != 0) positions[i] += offset;
                positions[i + 1] -= offset;
            }
            positions[0] = pin.position;
        }

        // "Weight on the tip": spring each particle toward the vertical plumb line under the pin,
        // stronger toward the free end. This restores the tail toward hanging straight down and
        // cancels the outward velocity a collision pushout imparts — so a tail that touches
        // something drapes and settles downward instead of drifting away without bound.
        if (hangReturn > 0f)
        {
            Vector3 plumb = positions[0];
            float k = 1f - Mathf.Exp(-hangReturn * dt);
            for (int i = 1; i < n; i++)
            {
                float w = k * (i / (float)(n - 1));   // 0 at the pin → full at the tip
                positions[i].x = Mathf.Lerp(positions[i].x, plumb.x, w);
                positions[i].z = Mathf.Lerp(positions[i].z, plumb.z, w);
            }
        }

        // One pushout pass per frame is plenty for a slack strand (it piles rather than presses).
        for (int i = 1; i < n; i++)
            PushOutOfColliders(i);
    }

    private void PushOutOfColliders(int index)
    {
        Vector3 p = positions[index];
        int count = Physics.OverlapSphereNonAlloc(p, particleRadius, OverlapBuffer,
                                                  collisionMask, QueryTriggerInteraction.Ignore);
        bool touched = false;
        for (int c = 0; c < count; c++)
        {
            Collider col = OverlapBuffer[c];
            Transform t = col.transform;
            if (ignoreRootA != null && t.IsChildOf(ignoreRootA)) continue;
            if (ignoreRootB != null && t.IsChildOf(ignoreRootB)) continue;

            Vector3 closest = col.ClosestPoint(p);
            Vector3 away = p - closest;
            float d = away.magnitude;
            if (d < 0.0001f)
            {
                away = p - col.bounds.center;
                d = away.magnitude;
                if (d < 0.0001f) continue;
                p = closest + away / d * particleRadius;
                touched = true;
            }
            else if (d < particleRadius)
            {
                p = closest + away / d * particleRadius;
                touched = true;
            }
        }

        if (touched)
        {
            positions[index] = p;
            // Friction on contact: the strand settles into a pile instead of creeping around.
            previous[index] = Vector3.Lerp(previous[index], p, contactFriction);
        }
    }

    // ------------------------------------------------------------ mesh

    private void BuildMesh()
    {
        mesh = new Mesh { name = "RopeTailMesh" };
        GetComponent<MeshFilter>().mesh = mesh;

        // World-space vertices — this GameObject stays at identity.
        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        transform.localScale = Vector3.one;

        int rings = positions.Length;
        int vertsPerRing = sides + 1;
        verts = new Vector3[rings * vertsPerRing];
        var uvs = new Vector2[verts.Length];
        var triangles = new int[(rings - 1) * sides * 6];

        for (int r = 0; r < rings; r++)
            for (int s = 0; s <= sides; s++)
                uvs[r * vertsPerRing + s] = new Vector2((float)s / sides, r / (rings - 1f));

        int ti = 0;
        for (int r = 0; r < rings - 1; r++)
        {
            for (int s = 0; s < sides; s++)
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

        UpdateRingVertices();
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = triangles;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private void UpdateMesh()
    {
        UpdateRingVertices();
        mesh.vertices = verts;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
    }

    private void UpdateRingVertices()
    {
        int rings = positions.Length;
        int vertsPerRing = sides + 1;

        for (int r = 0; r < rings; r++)
        {
            Vector3 tangent = r == 0 ? positions[1] - positions[0]
                            : r == rings - 1 ? positions[r] - positions[r - 1]
                            : positions[r + 1] - positions[r - 1];
            if (tangent.sqrMagnitude < 1e-8f) tangent = Vector3.down;
            tangent.Normalize();

            Vector3 side1 = Vector3.Cross(tangent, Vector3.up);
            if (side1.sqrMagnitude < 1e-4f) side1 = Vector3.Cross(tangent, Vector3.right);
            side1.Normalize();
            Vector3 side2 = Vector3.Cross(tangent, side1);

            for (int s = 0; s <= sides; s++)
            {
                float angle = s * (2f * Mathf.PI / sides);
                verts[r * vertsPerRing + s] = positions[r] +
                    (side1 * Mathf.Cos(angle) + side2 * Mathf.Sin(angle)) * ropeRadius;
            }
        }
    }
}
