using UnityEngine;

public class Gripshot : MonoBehaviour
{
    [Header("Gripshot Settings")]
    public float shotDistance = 20f;
    public float gripShotSpeed = 40f;
    public float gripReloadTime = 1.0f;
    public GameObject gripPrefab;        // the real physics Grip projectile
    public GameObject dummyGripPrefab;   // the visual while held
    public Transform gripShotOrigin;     // where dummyGrip sits and real grips spawn from

    [Header("Attach Settings")]
    [Range(0f, 1f)] public float attachForceThreshold = 0.75f;
    public float attachDepth = 0.02f;

    private void Reset()
    {
        if (gripShotOrigin == null)
            gripShotOrigin = transform;
    }
}
