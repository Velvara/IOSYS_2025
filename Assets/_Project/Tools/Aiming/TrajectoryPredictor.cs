using System.Collections.Generic;
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

    public void DrawTrajectory(float throwForce, Vector3 aimDirection)
    {
        if (throwAim == null) return;

        Vector3 startPos = throwOrigin.position;
        Vector3 startVelocity = aimDirection * throwForce;

        List<Vector3> points = new List<Vector3>();

        Vector3 currentPosition = startPos;
        Vector3 currentVelocity = startVelocity;

        points.Add(currentPosition);

        for (int i = 0; i < resolution; i++)
        {
            Vector3 nextPosition = currentPosition + currentVelocity * timeStep;
            currentVelocity += Physics.gravity * timeStep;

            // Check for collisions
            if (Physics.Raycast(currentPosition, nextPosition - currentPosition,
                out RaycastHit hit,
                (nextPosition - currentPosition).magnitude,
                collisionLayers))
            {
                points.Add(hit.point);
                if (landingMarkerInstance != null)
                {
                    landingMarkerInstance.transform.position = hit.point;
                    landingMarkerInstance.SetActive(true);
                }
                break;
            }

            currentPosition = nextPosition;
            points.Add(currentPosition);
        }

        // Update LineRenderer
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = points.Count;
            lineRenderer.SetPositions(points.ToArray());
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

