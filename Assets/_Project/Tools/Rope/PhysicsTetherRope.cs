using UnityEngine;

/// <summary>
/// The carbine tether, A/B ALTERNATIVE sim: a PhysX chain — small rigidbody links joined by
/// distance-limited ConfigurableJoints, the two ends held by kinematic bodies that follow the
/// carbine / hip slot. Collision comes from the links' own sphere colliders, so the rope drapes
/// and wraps through the regular physics step. Expect it to be springier and more expensive than
/// VerletTetherRope (the default) — this exists so both can be felt in play and compared.
/// Detaching destroys the hip-side kinematic and its joint; the chain then dangles freely.
/// </summary>
public class PhysicsTetherRope : TetherRopeBase
{
    [Header("Chain")]
    [Tooltip("Link density — more = smoother rope, heavier sim.")]
    public float linksPerMeter = 2f;
    public float linkMass = 0.05f;
    public float linkDrag = 0.6f;
    public float linkAngularDrag = 0.6f;
    [Tooltip("Collision radius of each link's sphere collider.")]
    public float linkColliderRadius = 0.035f;

    [Header("Rendering")]
    public int sides = 6;
    public float ropeRadius = 0.02f;

    private Transform _anchorEnd, _hipEnd;
    private Rigidbody _anchorBody, _hipBody;
    private Rigidbody[] _links;
    private readonly System.Collections.Generic.List<ConfigurableJoint> _joints =
        new System.Collections.Generic.List<ConfigurableJoint>();
    private float _appliedSegLen = -1f;   // last per-joint limit written (skip redundant writes)
    private RopeTubeMesh _tube;
    private Vector3[] _centers;   // anchor + links + hip end

    public override float TautAmount =>
        _anchorEnd != null && HipAttached && _hipEnd != null && RopeLength > 0f
            ? Vector3.Distance(_anchorEnd.position, _hipEnd.position) / RopeLength
            : 0f;

    public override Vector3 HipEndPoint =>
        HipAttached && _hipEnd != null ? _hipEnd.position
            : _links != null && _links.Length > 0 ? _links[_links.Length - 1].position
            : transform.position;

    public override float CurrentArcLength
    {
        get
        {
            if (_links == null || _links.Length == 0) return 0f;
            float sum = Vector3.Distance(
                _anchorEnd != null ? _anchorEnd.position : _anchorBody.position, _links[0].position);
            for (int i = 0; i < _links.Length - 1; i++)
                sum += Vector3.Distance(_links[i].position, _links[i + 1].position);
            if (HipAttached && _hipEnd != null)
                sum += Vector3.Distance(_links[_links.Length - 1].position, _hipEnd.position);
            return sum;
        }
    }
    // GetLastContact stays the base's "false": the PhysX links don't track their contacts, so the
    // range checks fall back to anchor-straight distance — corner-limit precision is verlet-only.

    public override void Init(Transform anchorEnd, Transform hipEnd, float ropeLength, Material material,
                              LayerMask collisionMask, Transform ignoreRootA, Transform ignoreRootB)
    {
        _anchorEnd = anchorEnd;
        _hipEnd = hipEnd;
        RopeLength = Mathf.Max(0.5f, ropeLength);
        ActiveLength = RopeLength;   // the controller reels this in right after Init (progressive pay-out)
        HipAttached = true;

        int n = Mathf.Clamp(Mathf.CeilToInt(RopeLength * linksPerMeter), 4, 60);
        float segLen = RopeLength / (n + 1);   // n+1 joints span the chain (anchor→links→hip)
        _links = new Rigidbody[n];

        _anchorBody = MakeKinematicEnd("TetherAnchorBody", anchorEnd.position);
        _hipBody = MakeKinematicEnd("TetherHipBody", hipEnd.position);

        Rigidbody previous = _anchorBody;
        for (int i = 0; i < n; i++)
        {
            float t = (i + 1) / (float)(n + 1);
            var go = new GameObject($"TetherLink{i}");
            go.transform.SetParent(transform, worldPositionStays: true);
            go.transform.position = Vector3.Lerp(anchorEnd.position, hipEnd.position, t);

            var rb = go.AddComponent<Rigidbody>();
            rb.mass = linkMass;
            rb.linearDamping = linkDrag;
            rb.angularDamping = linkAngularDrag;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            var col = go.AddComponent<SphereCollider>();
            col.radius = linkColliderRadius;
            IgnoreHierarchy(col, ignoreRootA);
            IgnoreHierarchy(col, ignoreRootB);

            ConnectWithLimit(rb, previous, segLen);
            _links[i] = rb;
            previous = rb;
        }

        // The hip-side kinematic pins the last link to the player (its own joint, destroyed on release).
        ConnectWithLimit(_hipBody, previous, segLen);

        // World-space tube verts — this GameObject stays at identity.
        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        transform.localScale = Vector3.one;
        _centers = new Vector3[n + 2];
        _tube = new RopeTubeMesh(_centers.Length, sides, ropeRadius, "CarbineTetherMeshPhysX");
        var mf = gameObject.GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
        var mr = gameObject.GetComponent<MeshRenderer>();
        if (mr == null) mr = gameObject.AddComponent<MeshRenderer>();
        mf.mesh = _tube.Mesh;
        mr.material = material;
    }

    public override void SetActiveLength(float length)
    {
        ActiveLength = Mathf.Clamp(length, 0.5f, RopeLength);
        float segLen = ActiveLength / _joints.Count;
        if (Mathf.Abs(segLen - _appliedSegLen) < 0.005f) return;   // skip sub-centimeter rewrites
        _appliedSegLen = segLen;
        var limit = new SoftJointLimit { limit = segLen };
        for (int i = 0; i < _joints.Count; i++)
            if (_joints[i] != null) _joints[i].linearLimit = limit;
    }

    public override void ReleaseHipEnd()
    {
        HipAttached = false;
        _hipEnd = null;
        if (_hipBody != null) Destroy(_hipBody.gameObject);   // takes its joint with it
        _hipBody = null;
    }

    private Rigidbody MakeKinematicEnd(string name, Vector3 position)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, worldPositionStays: true);
        go.transform.position = position;
        var rb = go.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        return rb;
    }

    /// <summary>Distance-limited ball-socket: linear motion limited to the segment length, angular
    /// free — a chain of these behaves rope-like without capsule-orientation bookkeeping. All joints
    /// are registered so SetActiveLength can retarget their limits live (progressive pay-out).</summary>
    private void ConnectWithLimit(Rigidbody body, Rigidbody connectTo, float segLen)
    {
        var joint = body.gameObject.AddComponent<ConfigurableJoint>();
        joint.connectedBody = connectTo;
        joint.autoConfigureConnectedAnchor = false;
        joint.anchor = Vector3.zero;
        joint.connectedAnchor = Vector3.zero;
        joint.xMotion = ConfigurableJointMotion.Limited;
        joint.yMotion = ConfigurableJointMotion.Limited;
        joint.zMotion = ConfigurableJointMotion.Limited;
        joint.linearLimit = new SoftJointLimit { limit = segLen };
        joint.enableCollision = false;
        _joints.Add(joint);
    }

    private static void IgnoreHierarchy(Collider linkCollider, Transform root)
    {
        if (root == null) return;
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            Physics.IgnoreCollision(linkCollider, c, true);
    }

    private void FixedUpdate()
    {
        if (_anchorBody != null && _anchorEnd != null) _anchorBody.MovePosition(_anchorEnd.position);
        if (HipAttached && _hipBody != null && _hipEnd != null) _hipBody.MovePosition(_hipEnd.position);
    }

    private void LateUpdate()
    {
        if (_tube == null || _links == null) return;

        _centers[0] = _anchorEnd != null ? _anchorEnd.position : _anchorBody.position;
        for (int i = 0; i < _links.Length; i++)
            _centers[i + 1] = _links[i].position;
        _centers[_centers.Length - 1] = HipAttached && _hipEnd != null
            ? _hipEnd.position
            : _links[_links.Length - 1].position;   // released: the tube just ends at the last link

        _tube.UpdateCenters(_centers);
    }
}
