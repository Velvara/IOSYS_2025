using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TrunkPiece : MonoBehaviour
{
    [Header("Gizmo Debugging")]
    public bool showGizmos = true;
    public Color detectionSphereColor = Color.yellow;
    public Color connectionLineColor = Color.green;
    public Color spawnPointColor = Color.cyan;

    [Header("Spacing")]
    [Range(0.1f, 2f)]
    public float spawnSpacingMultiplier = 1f;

    [Header("Growth Settings")]
    public float pieceGrowTime = 1f;
    public float timeBetweenGrowth = 0.3f;

    [Header("Collision Checking")]
    public LayerMask trunkPieceLayerMask;
    public LayerMask lightSourceLayerMask;
    public bool respectCollisions = true;

    [Header("Growth")]
    [SerializeField] private float fadeTime = 2f;
    [SerializeField] private float initialScale = 0.1f;

    [Header("Light Detection")]
    public Tags lightSourceTag;
    public float lightDetectionRadius = 5f;
    public LayerMask detectionLayer;

    [Header("Recursive Growth")]
    public GameObject trunkPiecePrefab;
    public int spawnCount = 5;
    [Range(0.1f, 0.5f)] public float endThickness = 0.3f;
    public float noiseScale = 1f;
    public float noiseInfluence = 0.5f;
    public float spawnRadius = 0.2f;

    [Header("Sine Wave Influence")]
    [Tooltip("The sine wave's influence at the start of the spawning process.")]
    public float sineWaveStartInfluence = 0.5f;

    [Tooltip("The sine wave's influence at the end of the spawning process.")]
    public float sineWaveEndInfluence = 0.1f;

    [Header("Trunk Container")]
    [Tooltip("Prefab for the Trunk container object.")]
    public GameObject trunkPrefab;

    private Vector3 grownScale;
    private float timeToGrow;
    private GameObject detectedLightSource;
    private Transform trunkContainer;
    private bool isLastPiece = false;

    // ---------- PUBLIC ENTRY POINT ----------
    public static void SpawnTrunkRoot(GameObject trunkPiecePrefab, GameObject trunkPrefab, Vector3 position)
    {
        // Create the trunk container from the prefab
        GameObject trunkContainer = Instantiate(trunkPrefab, position, Quaternion.identity);
        trunkContainer.name = "Trunk";

        // Assign the trunkContainer to the TreeTrunkMeshGenerator's parentTrunk field
        TreeTrunkMeshGenerator meshGenerator = trunkContainer.GetComponent<TreeTrunkMeshGenerator>();
        if (meshGenerator != null)
        {
            meshGenerator.trunkParent = trunkContainer; // Set the parentTrunk field
        }
        else
        {
            Debug.LogError("TreeTrunkMeshGenerator script is missing on the trunkPrefab!");
        }

        // Spawn the first piece under the trunk container
        GameObject firstPieceObj = Instantiate(trunkPiecePrefab, position, Quaternion.identity, trunkContainer.transform);
        TrunkPiece firstPiece = firstPieceObj.GetComponent<TrunkPiece>();

        // Initialize the first piece
        firstPiece.Initialize(firstPieceObj.transform.localScale, firstPiece.pieceGrowTime, trunkContainer.transform, null, false);
    }

    // ---------- INITIALIZATION ----------
    public void Initialize(Vector3 targetScale, float growTime, Transform parentContainer, GameObject lightSource, bool isLast)
    {
        grownScale = targetScale;
        timeToGrow = growTime;
        detectedLightSource = lightSource; // Assign the light source
        this.isLastPiece = isLast; // Assign whether this is the last piece

        if (parentContainer != null)
            trunkContainer = parentContainer;

        transform.localScale = Vector3.one * initialScale;

        if (lightSource == null)
            StartCoroutine(GrowAndDetectLight());
        else
            StartCoroutine(GrowAndFinish(targetScale));
    }

    // ---------- GROWTH + LIGHT DETECTION ----------
    private IEnumerator GrowAndDetectLight()
    {
        float timer = 0f;

        while (timer < timeToGrow)
        {
            transform.localScale = Vector3.Lerp(Vector3.one * initialScale, grownScale, timer / timeToGrow);
            timer += Time.deltaTime;
            yield return null;
        }

        transform.localScale = grownScale;
        OnFullyGrown();
    }

    private void OnFullyGrown()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, lightDetectionRadius, lightSourceLayerMask);

        foreach (var hit in hits)
        {
            TagObjManager tagManager = hit.GetComponent<TagObjManager>();
            if (tagManager != null && tagManager.HasTag(lightSourceTag))
            {
                detectedLightSource = hit.gameObject;
                StartCoroutine(SpawnTrunkPieces());
                return;
            }
        }

        StartCoroutine(DarkenAndDestroy());
    }

    // ---------- FADE TO BLACK ----------
    private IEnumerator DarkenAndDestroy()
    {
        Renderer rend = GetComponent<Renderer>();
        if (rend == null) yield break;

        Material mat = rend.material;
        Color startColor = mat.color;
        float t = 0f;

        while (t < fadeTime)
        {
            mat.color = Color.Lerp(startColor, Color.black, t / fadeTime);
            t += Time.deltaTime;
            yield return null;
        }

        Destroy(gameObject);
    }

    // ---------- RECURSIVE GROWTH ----------
    private IEnumerator SpawnTrunkPieces()
    {
        if (detectedLightSource == null || trunkPiecePrefab == null || trunkContainer == null)
            yield break;

        Vector3 direction = (detectedLightSource.transform.position - transform.position).normalized;
        Vector3 previousPoint = transform.position;
        Vector3 previousScale = grownScale;

        List<GameObject> spawnedPieces = new List<GameObject>();

        for (int i = 1; i <= spawnCount; i++)
        {
            float t = (float)i / spawnCount;

            // Calculate scale for this piece
            Vector3 newScale = Vector3.Lerp(grownScale, grownScale * endThickness, t);
            float spacing = (GetRadiusFromScale(newScale) + GetRadiusFromScale(previousScale)) * spawnSpacingMultiplier;

            Vector3 basePoint = previousPoint + direction * spacing;

            // Calculate the diminishing sine wave influence
            float sineWaveInfluence = Mathf.Lerp(sineWaveStartInfluence, sineWaveEndInfluence, t);
            float sineOffset = Mathf.Sin(i * Mathf.PI / spawnCount) * sineWaveInfluence;
            Vector3 sineWaveOffset = new Vector3(sineOffset, 0f, 0f); // Apply offset in the X-axis (or any axis you prefer)

            // Perlin noise offset
            float noiseX = Mathf.PerlinNoise(Time.time + i * noiseScale, 0f);
            float noiseY = Mathf.PerlinNoise(0f, Time.time + i * noiseScale);
            Vector3 right = Vector3.Cross(direction, Vector3.up).normalized;
            Vector3 up = Vector3.Cross(direction, right).normalized;
            Vector3 noiseOffset = (right * (noiseX - 0.5f) + up * (noiseY - 0.5f)) * noiseInfluence;

            Vector3 spawnPoint = basePoint + sineWaveOffset + noiseOffset;

            // Collision check (optional)
            if (respectCollisions)
            {
                Collider[] hits = Physics.OverlapSphere(spawnPoint, GetRadiusFromScale(newScale), trunkPieceLayerMask);
                bool blocked = false;
                foreach (var hit in hits)
                {
                    if (hit != null && hit.gameObject != this.gameObject && !hit.isTrigger && !spawnedPieces.Contains(hit.gameObject))
                    {
                        blocked = true;
                        break;
                    }
                }
                if (blocked) continue;
            }

            // Spawn the piece
            GameObject newPiece = Instantiate(trunkPiecePrefab, spawnPoint, Quaternion.identity, trunkContainer);
            spawnedPieces.Add(newPiece);

            TrunkPiece pieceScript = newPiece.GetComponent<TrunkPiece>();
            bool isLast = i == spawnCount;

            // Initialize with recursive growth
            pieceScript.Initialize(newScale, pieceGrowTime, trunkContainer, detectedLightSource, isLast);

            yield return new WaitForSeconds(timeBetweenGrowth);

            // Update for next spawn
            previousPoint = spawnPoint;
            previousScale = newScale;
        }

        // Trigger the mesh generator after all pieces are spawned
        TreeTrunkMeshGenerator meshGenerator = trunkContainer.GetComponent<TreeTrunkMeshGenerator>();
        if (meshGenerator != null)
        {
            meshGenerator.GenerateTrunkMesh();
        }
        else
        {
            Debug.LogError("TreeTrunkMeshGenerator is missing on the trunk container!");
        }
    }

    // ---------- SIMPLE GROWTH FOR RECURSIVE PIECES ----------
    private IEnumerator GrowAndFinish(Vector3 targetScale)
    {
        Vector3 startScale = transform.localScale;
        float elapsed = 0f;

        while (elapsed < timeToGrow)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / timeToGrow);
            transform.localScale = Vector3.Lerp(startScale, targetScale, t);
            yield return null;
        }

        transform.localScale = targetScale;
    }

    // ---------- UTILITY ----------
    private float GetRadiusFromScale(Vector3 scale)
    {
        Collider col = GetComponent<Collider>();
        if (col != null)
            return Vector3.Scale(col.bounds.extents, scale).magnitude;

        Renderer rend = GetComponent<Renderer>();
        if (rend != null)
            return Vector3.Scale(rend.bounds.extents, scale).magnitude;

        return scale.magnitude * 0.5f;
    }
}