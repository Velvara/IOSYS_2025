using UnityEngine;

[RequireComponent(typeof(Rigidbody), typeof(Collider))]
public class Grip : MonoBehaviour
{
    public float attachForceThreshold = 0.75f;
    public float attachDepth = 0.02f;
    private Rigidbody rb;
    private float initialSpeed = 0f;
    private bool attached = false;
    public Tags woodTag;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    public void SetInitialSpeed(float speed)
    {
        initialSpeed = speed;
    }

    void OnCollisionEnter(Collision collision)
    {
        if (attached) return;

        var tagManager = collision.collider.GetComponentInParent<TagObjManager>();
        float currentSpeed = rb != null ? rb.linearVelocity.magnitude : 0f;
        float ratio = (initialSpeed > 0f) ? (currentSpeed / initialSpeed) : 0f;

        if (tagManager != null && tagManager.HasTag(woodTag) && ratio >= attachForceThreshold)
        {
            AttachToCollision(collision);
        }
        else
        {
            // optional: spawn invalid FX or play sound
            Destroy(gameObject, 2f);
        }
    }

    void AttachToCollision(Collision collision)
    {
        attached = true;

        // disable physics
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        ContactPoint cp = collision.contacts[0];
        Transform t = transform;

        // place at contact point with a small inset defined by attachDepth
        t.position = cp.point + cp.normal * (-attachDepth);

        // orient to the surface normal
        t.rotation = Quaternion.LookRotation(-cp.normal, Vector3.up);

        // parent to the collided object so it moves with that object
        t.SetParent(collision.collider.transform, true);
    }
}
