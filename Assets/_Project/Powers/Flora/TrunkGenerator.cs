using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Game.Core.Climbing;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class TrunkGenerator : MonoBehaviour
{
    [Header("Trunk Settings")]
    public GameObject trunkBaseObj;
    public LayerMask searchIgnoreLayers;
    public float lightSearchRadius = 20f;
    public float timeToRot = 2f;

    [Header("Segment Calculation Settings")]
    public float ringRadius = 1f;
    public float segmentLength = 3f;
    public int segmentRetries = 6;
    public int segmentAmount = 10;

    [Header("Ring Calculation Settings")]
    public int ringVertices = 8;
    public float minRingRadius = 0.1f;
    public AnimationCurve taperCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("Segment Offset Settings")]
    [Tooltip("how far points spiral out")]
    [Range(0f, 1f)] public float screwStrength = 0.2f;
    [Tooltip("max number of full rotations along the trunk")]
    public float maxScrewTurns = 2f;                     
    [Tooltip("how often the noise repeats")]
    public float noiseFrequency = 0.5f;   
    [Tooltip("how strong the offset is")]
    public float noiseStrength = 0.5f;
    [Tooltip("how stiff the growth is at the base")]
    [Range(0f, 1f)] public float stiffInfluence = 0.5f;

    [Header("Mesh Settings")]
    public Material trunkMaterial;

    [Header("Animation Settings")]
    private Mesh mesh;
    private Vector3[] originalVerts;
    private Vector3[] workingVerts;
    public float growthDuration = 3f;
    [Range(0.1f, 5f)] public float growthEase = 1f;
    public AnimationCurve growthCurve = AnimationCurve.Linear(0, 0, 1, 1);

    [Header("Growth Collision Settings")]
    public bool useGrowthColliders = true;
    private List<CapsuleCollider> growthColliders = new List<CapsuleCollider>();

    [Header("Climbing")]
    [Tooltip("Climb holds are NOT emitted on rings thinner than this radius (m), so the player " +
             "can't grab the twiggy taper near the tip. Only used if a ClimbableSurface (or any " +
             "IClimbableMeshConsumer) is present on this object.")]
    public float minClimbableRingRadius = 0.25f;

    [Header("Custom Tag System")]
    public Tags lightSourceTag;

    private GameObject trunkBaseInstance;
    private GameObject trunkLightSource;
    private readonly List<Vector3> segmentPoints = new List<Vector3>();
    private readonly List<Vector3[]> segmentRings = new List<Vector3[]>();
    private Vector3 endVertex;
    private Vector3[][] ringLocalOffsets; // store per-ring local coordinates used during animation


    private void Start()
    {
        trunkBaseInstance = Instantiate(trunkBaseObj, transform);
        StartCoroutine(SearchForLightSource());
    }

    private IEnumerator SearchForLightSource()
    {
        trunkLightSource = null;
        float closestDistance = Mathf.Infinity;

        Collider[] hits = Physics.OverlapSphere(transform.position, lightSearchRadius, ~searchIgnoreLayers);
        foreach (var hit in hits)
        {
            TagObjManager tagManager = hit.GetComponent<TagObjManager>();
            if (tagManager != null && tagManager.HasTag(lightSourceTag))
            {
                float distance = Vector3.Distance(transform.position, hit.transform.position);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    trunkLightSource = hit.gameObject;
                }
            }
        }

        if (trunkLightSource != null)
        {
            segmentPoints.Add(transform.position);
            StartCoroutine(SegmentCalculations());
        }
        else
        {
            StartCoroutine(RotAndDestroy());
        }

        yield return null;
    }

    private IEnumerator SegmentCalculations()
    {
        for (int i = 0; i < segmentAmount; i++)
        {
            Vector3 start = segmentPoints[segmentPoints.Count - 1];
            Vector3 direction = (trunkLightSource.transform.position - start).normalized;

            // Apply stiffness influence (strong at start, fades with progress)
            float progress = (float)(segmentPoints.Count - 1) / (float)segmentAmount;  // 0..1 along trunk
            float influence = Mathf.Lerp(stiffInfluence, 0f, progress);               // fade influence to 0
            direction = Vector3.Slerp(direction, transform.up, influence).normalized;

            bool foundValid = false;
            Vector3 endPoint = Vector3.zero;

            for (int retry = 0; retry < segmentRetries; retry++)
            {
                Vector3 offset = Random.insideUnitSphere * 0.2f;
                Vector3 castDir = (direction + offset).normalized;

                //check if last segment. if so, use half segmentLength
                float castLength = segmentLength;
                if (i == segmentAmount - 1)
                    castLength *= 0.5f; // last one only

                if (Physics.SphereCast(start, ringRadius, castDir, out RaycastHit hit, segmentLength, ~searchIgnoreLayers))
                {
                    if (hit.collider.gameObject == trunkLightSource)
                    {
                        Debug.Log($"[Segment {i}] Hit LightSource ({hit.collider.name}), stopping segments.");
                        StartCoroutine(RingCalculations());
                        yield break;
                    }

                    //Debug.Log($"[Segment {i}, Retry {retry + 1}] Hit: {hit.collider.gameObject.name}");
                    Debug.DrawRay(start, castDir * segmentLength, Color.magenta, 5f);
                }
                else
                {
                    endPoint = start + castDir * segmentLength;
                    foundValid = true;
                    Debug.DrawRay(start, castDir * segmentLength, Color.yellow, 5f);
                    break;
                }
            }

            if (!foundValid)
            {
                if (segmentPoints.Count >= 3)
                {
                    StartCoroutine(RingCalculations());
                }
                yield break;
            }

            segmentPoints.Add(endPoint);

            AddSrewPattern();
            //AddPerlinNoisePattern(endPoint);

            yield return null;
        }

        StartCoroutine(RingCalculations());
    }

    private void AddPerlinNoisePattern(Vector3 endPoint)
    {
        // Apply Perlin noise offset
        Vector3 noiseOffset = Vector3.zero;
        noiseOffset.x = (Mathf.PerlinNoise(endPoint.x * noiseFrequency, endPoint.y * noiseFrequency) - 0.5f) * 2f;
        noiseOffset.y = (Mathf.PerlinNoise(endPoint.y * noiseFrequency, endPoint.z * noiseFrequency) - 0.5f) * 2f;
        noiseOffset.z = (Mathf.PerlinNoise(endPoint.z * noiseFrequency, endPoint.x * noiseFrequency) - 0.5f) * 2f;

        segmentPoints[segmentPoints.Count - 1] += noiseOffset * noiseStrength;
    }

    private void AddSrewPattern()
    {
        // Apply screw offset
        float t = (float)segmentPoints.Count / segmentAmount;   // 0..1 along trunk
        float angle = t * maxScrewTurns * Mathf.PI * 2f;        // how far around the spiral
        Vector3 toLight = (trunkLightSource.transform.position - transform.position).normalized;

        // Find any perpendicular vector to act as the spiral plane
        Vector3 perp = Vector3.Cross(toLight, Vector3.up);
        if (perp.sqrMagnitude < 0.001f) perp = Vector3.Cross(toLight, Vector3.right);
        perp.Normalize();

        // Build a rotation basis
        Quaternion rot = Quaternion.AngleAxis(Mathf.Rad2Deg * angle, toLight);

        // Apply screw offset
        Vector3 screwOffset = rot * perp * screwStrength;
        segmentPoints[segmentPoints.Count - 1] += screwOffset;
    }

    private IEnumerator RingCalculations()
    {
        segmentRings.Clear();

        int totalRings = Mathf.Max(1, segmentPoints.Count - 1);

        for (int i = 0; i < segmentPoints.Count - 1; i++)
        {
            Vector3 start = segmentPoints[i];
            Vector3 end = segmentPoints[i + 1];
            Vector3 direction = (end - start).normalized;

            Vector3[] ring = GenerateRingVertices(start, direction, i, totalRings);
            segmentRings.Add(ring);

            for (int j = 0; j < ring.Length; j++)
            {
                Vector3 a = ring[j];
                Vector3 b = ring[(j + 1) % ring.Length];
                Debug.DrawLine(a, b, Color.cyan, 5f);
                Debug.DrawLine(start, a, Color.green, 5f);
            }

            yield return null;
        }

        endVertex = segmentPoints[segmentPoints.Count - 1];
        GenerateMesh();
        StartCoroutine(AnimateGrowth());
    }

    private Vector3[] GenerateRingVertices(Vector3 center, Vector3 direction, int ringIndex, int totalRings)
    {
        Vector3[] ring = new Vector3[ringVertices];

        direction = direction.sqrMagnitude > 0f ? direction.normalized : Vector3.up;
        Quaternion rot = Quaternion.FromToRotation(Vector3.up, direction);

        float t = (totalRings <= 1) ? 0f : (float)ringIndex / (float)(totalRings - 1);
        float radius = Mathf.Lerp(ringRadius, minRingRadius, taperCurve.Evaluate(t));

        for (int i = 0; i < ringVertices; i++)
        {
            float angle = (2 * Mathf.PI / ringVertices) * i;
            Vector3 local = new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
            ring[i] = center + rot * local;
        }

        return ring;
    }

    private void GenerateMesh()
    {
        if (ringVertices < 3 || segmentRings.Count < 1) return;

        Mesh mesh = new Mesh { name = "TrunkMesh" };
        List<Vector3> vertsLocal = new List<Vector3>();
        List<int> tris = new List<int>();

        // compute ringCount once
        int ringCount = segmentRings.Count;

        // --- 1) Build and store final local verts (final mesh state) ---
        List<Vector3> finalVertsLocal = new List<Vector3>();
        for (int r = 0; r < ringCount; r++)
        {
            for (int i = 0; i < ringVertices; i++)
            {
                finalVertsLocal.Add(transform.InverseTransformPoint(segmentRings[r][i]));
            }
        }
        // final tip (local)
        finalVertsLocal.Add(transform.InverseTransformPoint(endVertex));
        // store final (local-space) - used to restore final mesh at the end
        originalVerts = finalVertsLocal.ToArray();

        // --- 2) Precompute ringLocalOffsets (canonical local coords for each ring) ---
        ringLocalOffsets = new Vector3[ringCount][];
        for (int r = 0; r < ringCount; r++)
        {
            ringLocalOffsets[r] = new Vector3[ringVertices];

            // compute the original rotation used when the ring was created
            // if there's no next point (shouldn't happen for rings) fall back to up
            Vector3 origDir = Vector3.up;
            if (r < segmentPoints.Count - 1)
            {
                origDir = (segmentPoints[r + 1] - segmentPoints[r]);
                if (origDir.sqrMagnitude <= Mathf.Epsilon) origDir = Vector3.up;
                else origDir = origDir.normalized;
            }
            Quaternion origRot = Quaternion.FromToRotation(Vector3.up, origDir);

            for (int i = 0; i < ringVertices; i++)
            {
                // world-space offset from ring center
                Vector3 worldOffset = segmentRings[r][i] - segmentPoints[r];
                // convert to ring-local coords so we can reapply different rotations later
                ringLocalOffsets[r][i] = Quaternion.Inverse(origRot) * worldOffset;
            }
        }

        // --- 3) Build collapsed start vertices: all rings collapse to basePoint (same center & rotation) ---
        Vector3 basePoint = segmentPoints[0];
        for (int r = 0; r < ringCount; r++)
        {
            for (int i = 0; i < ringVertices; i++)
            {
                vertsLocal.Add(transform.InverseTransformPoint(basePoint));
            }
        }
        // collapsed tip at base as well
        vertsLocal.Add(transform.InverseTransformPoint(basePoint));
        int tipIndex = vertsLocal.Count - 1;

        // --- 4) Build triangle indices (unchanged) ---
        for (int r = 0; r < ringCount - 1; r++)
        {
            int ringStart = r * ringVertices;
            int nextRingStart = (r + 1) * ringVertices;

            for (int i = 0; i < ringVertices; i++)
            {
                int a = ringStart + i;
                int b = ringStart + (i + 1) % ringVertices;
                int c = nextRingStart + i;
                int d = nextRingStart + (i + 1) % ringVertices;

                tris.Add(a); tris.Add(c); tris.Add(b);
                tris.Add(b); tris.Add(c); tris.Add(d);
            }
        }

        // cap last ring to tip
        int lastRingStart = (ringCount - 1) * ringVertices;
        for (int i = 0; i < ringVertices; i++)
        {
            int a = lastRingStart + i;
            int b = lastRingStart + (i + 1) % ringVertices;
            tris.Add(a); tris.Add(tipIndex); tris.Add(b);
        }

        // --- 5) Apply to mesh and store references ---
        mesh.SetVertices(vertsLocal);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var mf = GetComponent<MeshFilter>();
        var mr = GetComponent<MeshRenderer>();
        mf.sharedMesh = mesh;
        if (trunkMaterial != null) mr.sharedMaterial = trunkMaterial;

        this.mesh = mesh;

        // UVs (you already have GenerateUVs)
        Vector2[] uvs = GenerateUVs(vertsLocal, ringCount);
        mesh.uv = uvs;
    }

    private Vector2[] GenerateUVs(List<Vector3> vertsLocal, int ringCount)
    {
        Vector2[] uvs = new Vector2[vertsLocal.Count];

        // 1. Compute cumulative length along the trunk
        float[] lengths = new float[segmentPoints.Count];
        lengths[0] = 0f;
        for (int i = 1; i < segmentPoints.Count; i++)
        {
            lengths[i] = lengths[i - 1] + Vector3.Distance(segmentPoints[i - 1], segmentPoints[i]);
        }
        float totalLength = lengths[lengths.Length - 1];

        // 2. Assign UVs per ring
        for (int r = 0; r < ringCount; r++)
        {
            float v = (totalLength > 0f) ? lengths[r] / totalLength : 0f;

            for (int i = 0; i < ringVertices; i++)
            {
                int vertIndex = r * ringVertices + i;
                float u = i / (float)ringVertices; // evenly around
                uvs[vertIndex] = new Vector2(u, v);
            }
        }

        // 3. Cap vertex (tip)
        int tipIndex = vertsLocal.Count - 1;
        uvs[tipIndex] = new Vector2(0.5f, 1f);

        return uvs;
    }

    private IEnumerator AnimateGrowth()
    {
        if (mesh == null) yield break;

        Vector3[] verts = new Vector3[mesh.vertexCount];
        int loopCount = segmentRings.Count;
        int tipIndex = verts.Length - 1;
        float totalSegments = segmentPoints.Count - 1;

        // Track spawned colliders so we can clean them up
        List<CapsuleCollider> tempColliders = new List<CapsuleCollider>();
        int lastColliderSeg = -1;

        float elapsed = 0f;
        while (elapsed < growthDuration)
        {
            float t = elapsed / growthDuration;
            float easedT = growthCurve.Evaluate(t);

            float progress = easedT * totalSegments;
            int completedSegments = Mathf.FloorToInt(progress);
            float segT = progress - completedSegments;

            // --- TIP placement ---
            if (completedSegments >= segmentPoints.Count - 1)
            {
                verts[tipIndex] = transform.InverseTransformPoint(segmentPoints[segmentPoints.Count - 1]);
            }
            else
            {
                Vector3 tipWorld = Vector3.Lerp(segmentPoints[completedSegments],
                                                segmentPoints[completedSegments + 1],
                                                segT);
                verts[tipIndex] = transform.InverseTransformPoint(tipWorld);
            }

            // --- LOOP placement ---
            for (int loop = 0; loop < loopCount; loop++)
            {
                if (loop == 0)
                {
                    Vector3 baseCenter = segmentPoints[0];
                    Quaternion baseRot = Quaternion.FromToRotation(Vector3.up,
                        (segmentPoints[1] - segmentPoints[0]).normalized);

                    for (int i = 0; i < ringVertices; i++)
                    {
                        int vertIndex = loop * ringVertices + i;
                        verts[vertIndex] = transform.InverseTransformPoint(baseCenter + baseRot * ringLocalOffsets[loop][i]);
                    }
                    continue;
                }

                int delay = (loopCount - loop);

                if (completedSegments < delay)
                {
                    Vector3 baseCenter = segmentPoints[0];
                    Quaternion baseRot = Quaternion.FromToRotation(Vector3.up,
                        (segmentPoints[1] - segmentPoints[0]).normalized);

                    for (int i = 0; i < ringVertices; i++)
                    {
                        int vertIndex = loop * ringVertices + i;
                        verts[vertIndex] = transform.InverseTransformPoint(baseCenter + baseRot * ringLocalOffsets[loop][i]);
                    }
                }
                else
                {
                    int ringSegStart = completedSegments - delay;
                    int ringSegEnd = ringSegStart + 1;

                    ringSegStart = Mathf.Clamp(ringSegStart, 0, segmentPoints.Count - 1);
                    ringSegEnd = Mathf.Clamp(ringSegEnd, 0, segmentPoints.Count - 1);

                    Vector3 ringPos = Vector3.Lerp(segmentPoints[ringSegStart],
                                                   segmentPoints[ringSegEnd],
                                                   segT);

                    Vector3 dir = (segmentPoints[ringSegEnd] - segmentPoints[ringSegStart]).normalized;
                    Quaternion ringRot = Quaternion.FromToRotation(Vector3.up, dir);

                    for (int i = 0; i < ringVertices; i++)
                    {
                        int vertIndex = loop * ringVertices + i;
                        verts[vertIndex] = transform.InverseTransformPoint(ringPos + ringRot * ringLocalOffsets[loop][i]);
                    }
                }
            }

            // --- Temporary collider spawning ---
            if (completedSegments > lastColliderSeg && completedSegments > 0 && completedSegments < segmentPoints.Count)
            {
                // Create capsule aligned along this segment
                Vector3 p0 = segmentPoints[completedSegments - 1];
                Vector3 p1 = segmentPoints[completedSegments];
                Vector3 center = (p0 + p1) / 2f;
                Vector3 dir = (p1 - p0);
                float length = dir.magnitude;

                CapsuleCollider cap = gameObject.AddComponent<CapsuleCollider>();
                cap.radius = ringRadius;
                cap.height = length + (ringRadius * 2f);
                cap.direction = 1; // Y-axis

                // Transform alignment
                cap.center = transform.InverseTransformPoint(center);
                // We'll align via rotation by putting collider in local space
                // So instead, parent colliders stay under same GameObject

                tempColliders.Add(cap);
                lastColliderSeg = completedSegments;
            }

            mesh.vertices = verts;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            elapsed += Time.deltaTime;
            yield return null;
        }

        // --- Finalize exactly at target ---
        Vector3[] finalVerts = new Vector3[originalVerts.Length];
        for (int i = 0; i < originalVerts.Length; i++)
            finalVerts[i] = originalVerts[i];
        mesh.vertices = finalVerts;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        // Remove temporary colliders
        foreach (var cap in tempColliders)
        {
            if (cap != null) Destroy(cap);
        }
        tempColliders.Clear();

        // Add final mesh collider
        MeshCollider mc = gameObject.AddComponent<MeshCollider>();
        mc.sharedMesh = mesh;

        //Add Rigidbody
        Rigidbody rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // Mark as static for batching/lightmaps
        gameObject.isStatic = true;

        // Emit climb holds from the final ring lattice (only if this trunk is climbable, i.e. has
        // a ClimbableSurface / IClimbableMeshConsumer). Must run BEFORE Destroy(this).
        EmitClimbHolds();

        //Destroy Script
        Destroy(this);
    }

    /// <summary>
    /// Builds climb holds from the final ring lattice and pushes them to a ClimbableSurface on this
    /// object via the Game.Core <see cref="IClimbableMeshConsumer"/> contract (no Game.Climbing
    /// reference). A trunk is a smooth tube, so the ledge parser finds nothing — instead each ring
    /// vertex above <see cref="minClimbableRingRadius"/> becomes a hold (outward radial = grab
    /// normal, along-trunk = up). No-op if the object has no climbable consumer.
    /// </summary>
    private void EmitClimbHolds()
    {
        var consumer = GetComponent<IClimbableMeshConsumer>();
        if (consumer == null) return;
        if (segmentRings == null || segmentRings.Count == 0) return;

        int ringCount = segmentRings.Count;
        var holds = new List<ClimbHoldData>(ringCount * Mathf.Max(1, ringVertices));

        for (int r = 0; r < ringCount; r++)
        {
            Vector3[] ring = segmentRings[r];
            if (ring == null || ring.Length == 0) continue;
            Vector3 center = segmentPoints[r];

            // Skip the twiggy taper near the tip so the player can't grab a sliver.
            float radius = (ring[0] - center).magnitude;
            if (radius < minClimbableRingRadius) continue;

            // "Up" = along the trunk at this ring (toward the next segment).
            Vector3 up;
            if (r < segmentPoints.Count - 1) up = segmentPoints[r + 1] - segmentPoints[r];
            else if (r > 0) up = segmentPoints[r] - segmentPoints[r - 1];
            else up = transform.up;
            up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;

            for (int i = 0; i < ring.Length; i++)
            {
                Vector3 worldPos = ring[i];
                Vector3 outward = worldPos - center;
                outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : transform.forward;

                Quaternion worldRot = Quaternion.LookRotation(outward, up);

                holds.Add(new ClimbHoldData
                {
                    LocalPosition = transform.InverseTransformPoint(worldPos),
                    LocalRotation = Quaternion.Inverse(transform.rotation) * worldRot,
                    RiskValue = 0f,   // trunks carry no vertex paint; resolves to fallback risk later
                    IconId = 0
                });
            }
        }

        consumer.ReceiveHolds(holds);
        Debug.Log($"[TrunkGenerator] Emitted {holds.Count} climb holds from {ringCount} rings.");
    }

    private void CreateGrowthColliders()
    {
        if (!useGrowthColliders) return;

        // Destroy previous collider GameObjects (each capsule lives on its own child GameObject)
        foreach (var col in growthColliders)
        {
            if (col != null && col.gameObject != null)
                Destroy(col.gameObject);
        }
        growthColliders.Clear();

        for (int i = 0; i < segmentPoints.Count - 1; i++)
        {
            var start = segmentPoints[i];
            var end = segmentPoints[i + 1];
            Vector3 dir = end - start;
            float length = dir.magnitude;
            if (length <= Mathf.Epsilon) continue;
            dir /= length;

            // Create a child GameObject to hold the capsule so we do NOT rotate the trunk's transform
            GameObject capGO = new GameObject($"TempCapsule_{i}");
            capGO.transform.SetParent(transform, true);
            capGO.transform.position = (start + end) * 0.5f;
            // Align the child's local up to the segment direction; keep the trunk transform untouched
            capGO.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            capGO.transform.localScale = Vector3.one;

            CapsuleCollider capsule = capGO.AddComponent<CapsuleCollider>();
            capsule.direction = 1; // local Y axis
            capsule.center = Vector3.zero;
            capsule.height = length + ringRadius * 2f;
            capsule.radius = ringRadius;
            capsule.isTrigger = false;

            growthColliders.Add(capsule);
        }
    }

    private IEnumerator RotAndDestroy()
    {
        if (trunkBaseInstance != null)
        {
            TagObjManager tagManager = GetComponent<TagObjManager>();
            if (tagManager != null) tagManager.ClearTags();

            Renderer renderer = trunkBaseInstance.GetComponent<Renderer>();
            if (renderer != null)
            {
                Color originalColor = renderer.material.color;
                float elapsedTime = 0f;

                while (elapsedTime < timeToRot)
                {
                    renderer.material.color = Color.Lerp(originalColor, Color.black, elapsedTime / timeToRot);
                    elapsedTime += Time.deltaTime;
                    yield return null;
                }

                renderer.material.color = Color.black;
            }
        }

        Destroy(trunkBaseInstance);
        Destroy(gameObject);
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.yellow;
        for (int i = 0; i < segmentPoints.Count; i++)
        {
            Gizmos.DrawSphere(segmentPoints[i], 0.1f);
            if (i > 0) Gizmos.DrawLine(segmentPoints[i - 1], segmentPoints[i]);
        }

        Gizmos.color = Color.cyan;
        foreach (var ring in segmentRings)
        {
            for (int i = 0; i < ring.Length; i++)
            {
                Vector3 a = ring[i];
                Vector3 b = ring[(i + 1) % ring.Length];
                Gizmos.DrawSphere(a, 0.05f);
                Gizmos.DrawLine(a, b);
            }
        }

        if (endVertex != Vector3.zero)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(endVertex, 0.15f);
        }
    }
}
