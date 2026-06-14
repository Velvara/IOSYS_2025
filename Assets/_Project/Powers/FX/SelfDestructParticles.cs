using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public class SelfDestructParticles : MonoBehaviour
{
    void Start()
    {
        Invoke("DestroyGameObject", GetComponent<ParticleSystem>().main.duration);
    }

    void DestroyGameObject()
    {
        Destroy(gameObject);
    }
}