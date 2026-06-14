using UnityEngine;

public class ThrowableObject : MonoBehaviour
{
    public GameObject trunkOriginator;
    public GameObject fungusOriginator;
    public GameObject explosionOriginator;
    public GameObject crystalOriginator;
    public Tags plantTag;
    public Tags soilTag;
    public Tags aliveTag;
    public Tags crystalTag;
    public Tags deadTag;
    public Tags etherealTag;
    public Tags fungusTag;
    public Tags mineralTag;
    public Tags organicTag;
    public Tags rockTag;
    public Tags substanceTag;
    public Tags woodTag;
    public Tags breakableTag;
    public Tags explosiveTag;

    public void Launch(Vector3 force)
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.useGravity = true;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
            rb.linearVelocity = force;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        // Ignore collisions with player
        if (collision.collider.CompareTag("Player"))
        {
            return;
        }

        // Check against tags
        var selfTags = GetComponent<TagObjManager>();
        var hitTags = collision.gameObject.GetComponent<TagObjManager>();

        //Generate a Trunk
        if (selfTags != null && hitTags != null && selfTags.HasTag(plantTag) && hitTags.HasTag(soilTag))
        {
            Vector3 hitPoint = collision.contacts[0].point;
            Vector3 hitNormal = collision.contacts[0].normal;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, hitNormal);

            Instantiate(trunkOriginator, hitPoint, rot);
        }

        //Generate a Fungus
        if (selfTags != null && hitTags != null && selfTags.HasTag(fungusTag) && hitTags.HasTag(organicTag) && hitTags.HasTag(deadTag))
        {
            var contact = collision.GetContact(0);
            var hitPoint = contact.point;
            var hitGo = collision.gameObject;

            var fungus = Instantiate(fungusOriginator, hitPoint, Quaternion.identity);

            // Hand off origin & dead source
            if (fungus.TryGetComponent<FungusPropagator>(out var prop))
            {
                prop.Initialize(hitPoint, hitGo);
            }
        }

        //Generate an Explosion
        if (selfTags != null && hitTags != null && selfTags.HasTag(explosiveTag))
        {
            Vector3 hitPoint = collision.contacts[0].point;

            Instantiate(explosionOriginator, hitPoint, Quaternion.identity);
        }

        //Generate a Crystal
        if (selfTags != null && hitTags != null && selfTags.HasTag(crystalTag))
        {
            Vector3 hitPoint = collision.contacts[0].point;

            Instantiate(crystalOriginator, hitPoint, Quaternion.identity);
        }

        // Destroy the projectile
        Destroy(gameObject);
    }
}