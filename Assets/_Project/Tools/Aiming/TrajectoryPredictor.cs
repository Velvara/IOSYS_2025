using UnityEngine;

public class TrajectoryPredictor : MonoBehaviour
{
    [Header("Prediction Settings")]
    public LineRenderer lineRenderer;
    public int resolution = 30;
    public float timeStep = 0.1f;
    public LayerMask collisionLayers;

    [Header("Throw Settings")]
    public Transform throwOrigin;
    //public Transform aimDirection;
    public ThrowAim throwAim;

    [Header("Landing Marker")]
    public GameObject landingMarkerPrefab;
    private GameObject landingMarkerInstance;

    private void Awake()
    {
        if (landingMarkerPrefab != null)
            landingMarkerInstance = Instantiate(landingMarkerPrefab);

        if (landingMarkerInstance != null)
            landingMarkerInstance.SetActive(false);

        // Try to auto-link ThrowAim
        if (throwAim == null)
            throwAim = GameObject.FindFirstObjectByType<ThrowAim>();
    }

    // Reused sample buffer — DrawTrajectory runs every frame while aiming, so it must not allocate.
    private Vector3[] _points;

    public void DrawTrajectory(float throwForce, Vector3 aimDirection)
    {
        if (throwAim == null || throwOrigin == null) return;

        int capacity = resolution + 1;
        if (_points == null || _points.Length < capacity)
            _points = new Vector3[capacity];

        Vector3 currentPosition = throwOrigin.position;
        Vector3 currentVelocity = aimDirection * throwForce;

        _points[0] = currentPosition;
        int count = 1;

        for (int i = 0; i < resolution; i++)
        {
            Vector3 nextPosition = currentPosition + currentVelocity * timeStep;
            currentVelocity += Physics.gravity * timeStep;

            // Check for collisions
            Vector3 segment = nextPosition - currentPosition;
            if (Physics.Raycast(currentPosition, segment, out RaycastHit hit,
                segment.magnitude, collisionLayers))
            {
                _points[count++] = hit.point;
                if (landingMarkerInstance != null)
                {
                    landingMarkerInstance.transform.position = hit.point;
                    landingMarkerInstance.SetActive(true);
                }
                break;
            }

            currentPosition = nextPosition;
            _points[count++] = currentPosition;
        }

        // Update LineRenderer (per-point writes — SetPositions would need an exact-size array = garbage)
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = count;
            for (int i = 0; i < count; i++)
                lineRenderer.SetPosition(i, _points[i]);
        }
    }

    public void HideTrajectory()
    {
        if (lineRenderer != null)
            lineRenderer.positionCount = 0;

        if (landingMarkerInstance != null)
            landingMarkerInstance.SetActive(false);
    }
}

