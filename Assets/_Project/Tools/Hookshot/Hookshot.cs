using UnityEngine;

public class Hookshot : MonoBehaviour
{
    [Header("Hookshot Settings")]
    public float shotDistance = 20f;
    public float hookshotSpeed = 40f;
    public float hookReturnSpeed = 30f;
    public float dragSpeed = 12f;
    public float dragStopDistance = 1.2f;
    public float tipAttachDepth = 0f;
    public ParticleSystem invalidFX;
    public LayerMask tipCheckCollisionLayers;

    [Header("Rope Settings")]
    [Range(0.01f, 0.5f)] public float ropeRadius = 0.05f;
    public int ropeCylinderSides = 8;
    public float ropeCylinderWidth = 0.05f;
    public float ropeCylinderLoopDistance = 0.5f;
    public Material ropeMaterial;

    [Header("Rope Wave Settings")]
    public float ropeWaveFrequency = 5f;
    public float ropeWaveAmplitude = 0.2f;
    public float ropeWaveDamping = 2f;
    public float ropeHitDampingMultiplier = 5f;

    [Header("Tip (child)")]
    public GameObject hookshotTip;
    public GameObject dummyTip;// assign the child tip here in inspector
    public Transform hookSlot;

    private void Reset()
    {
        // attempt to auto-find a HookshotTip child
        if (hookshotTip == null)
        {
            var tip = GetComponent<HookshotTip>();
            if (tip != null)
                hookshotTip = tip.gameObject;
        }
    }
}
