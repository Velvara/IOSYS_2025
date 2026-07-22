using UnityEngine;

/// <summary>
/// The carbine tether, DEFAULT sim: verlet particles pinned at BOTH ends (carbine ↔ hip slot) with
/// per-particle collision pushout. Slack rope sags, drapes and WRAPS around geometry between the
/// ends (a cylindrical rock deforms the rope around itself); a taut rope pulls straight through the
/// constraint passes. Two pinned ends need more constraint iterations than the free-hanging tail
/// (VerletRopeTail) — that one-end case stays untouched; this is its own component.
/// Detaching unpins the hip end, leaving the easy one-end dangle for the dissolve.
/// (PhysicsTetherRope is the A/B alternative behind CarbineController's toggle.)
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class VerletTetherRope : TetherRopeBase
{
    [Header("Simulation")]
    [Tooltip("Particle density — more = smoother drape/wrap, more constraint cost.")]
    public float particlesPerMeter = 4f;
    [Tooltip("Constraint passes per frame. Two pinned ends need more than a free tail; raise if a taut rope looks rubbery.")]
    public int constraintIterations = 10;
    [Tooltip("Collision pushout passes INTERLEAVED with the constraint iterations (plus one final pass). " +
             "One pass at the end let a taut rope get dragged through corner edges between frames — " +
             "interleaving keeps the wrap pressed OUTSIDE the geometry while the constraints pull.")]
    public int collisionPasses = 3;
    [Range(0.8f, 1f)] public float damping = 0.98f;
    public float gravityScale = 1f;
    [Tooltip("Collision radius per particle (keep ≥ ropeRadius so the visual never clips).")]
    public float particleRadius = 0.05f;
    [Range(0f, 1f)] public float contactFriction = 0.6f;

    [Header("Rendering")]
    public int sides = 6;
    public float ropeRadius = 0.02f;

    private Transform _anchorEnd, _hipEnd;
    private LayerMask _collisionMask;
    private Transform _ignoreA, _ignoreB;
    private Vector3[] _positions, _previous;
    private bool[] _touched;   // per-particle geometry contact this frame (wrap-pivot detection)
    private RopeTubeMesh _tube;

    private static readonly Collider[] OverlapBuffer = new Collider[4];

    public override float TautAmount =>
        _anchorEnd != null && HipAttached && _hipEnd != null && RopeLength > 0f
            ? Vector3.Distance(_anchorEnd.position, _hipEnd.position) / RopeLength
            : 0f;

    public override Vector3 HipEndPoint =>
        _positions != null ? _positions[_positions.Length - 1] : transform.position;

    public override void Init(Transform anchorEnd, Transform hipEnd, float ropeLength, Material material,
                              LayerMask collisionMask, Transform ignoreRootA, Transform ignoreRootB)
    {
        _anchorEnd = anchorEnd;
        _hipEnd = hipEnd;
        RopeLength = Mathf.Max(0.5f, ropeLength);
        ActiveLength = RopeLength;   // the controller reels this in right after Init (progressive pay-out)
        _collisionMask = collisionMask;
        _ignoreA = ignoreRootA;
        _ignoreB = ignoreRootB;
        HipAttached = true;

        // Particle count sized for the MAX length (fixed arrays); a shorter ActiveLength just means
        // shorter rest segments between the same particles.
        int count = Mathf.Clamp(Mathf.CeilToInt(RopeLength * particlesPerMeter), 8, 96);
        _positions = new Vector3[count];
        _previous = new Vector3[count];
        _touched = new bool[count];
        for (int i = 0; i < count; i++)
        {
            float t = i / (count - 1f);
            _positions[i] = Vector3.Lerp(anchorEnd.position, hipEnd.position, t);
            _previous[i] = _positions[i];
        }

        // World-space tube verts — this GameObject stays at identity.
        transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        transform.localScale = Vector3.one;
        _tube = new RopeTubeMesh(count, sides, ropeRadius, "CarbineTetherMesh");
        GetComponent<MeshFilter>().mesh = _tube.Mesh;
        GetComponent<MeshRenderer>().material = material;
    }

    public override void SetActiveLength(float length)
    {
        ActiveLength = Mathf.Clamp(length, 0.5f, RopeLength);
    }

    public override float CurrentArcLength
    {
        get
        {
            if (_positions == null) return 0f;
            float sum = 0f;
            for (int i = 0; i < _positions.Length - 1; i++)
                sum += Vector3.Distance(_positions[i], _positions[i + 1]);
            return sum;
        }
    }

    public override bool GetLastContact(out Vector3 point, out float arcFromAnchor)
    {
        point = default;
        arcFromAnchor = 0f;
        if (_positions == null || _touched == null) return false;

        int idx = -1;
        for (int i = _positions.Length - 1; i >= 1; i--)
            if (_touched[i]) { idx = i; break; }
        if (idx < 0) return false;

        float sum = 0f;
        for (int i = 0; i < idx; i++)
            sum += Vector3.Distance(_positions[i], _positions[i + 1]);
        point = _positions[idx];
        arcFromAnchor = sum;
        return true;
    }

    public override void ReleaseHipEnd()
    {
        HipAttached = false;
        _hipEnd = null;
    }

    private void LateUpdate()
    {
        if (_positions == null || _anchorEnd == null) return;
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Simulate(dt);
        _tube.UpdateCenters(_positions);
    }

    private void Simulate(float dt)
    {
        int n = _positions.Length;
        Vector3 gravityStep = Physics.gravity * (gravityScale * dt * dt);
        bool hipPinned = HipAttached && _hipEnd != null;

        // Integrate the interior; the two pins are written outright each pass.
        int last = n - 1;
        for (int i = 1; i < n; i++)
        {
            if (i == last && hipPinned) break;
            Vector3 current = _positions[i];
            _positions[i] += (current - _previous[i]) * damping + gravityStep;
            _previous[i] = current;
        }
        _positions[0] = _anchorEnd.position;
        if (hipPinned) _positions[last] = _hipEnd.position;

        float segmentRest = ActiveLength / (n - 1);
        System.Array.Clear(_touched, 0, _touched.Length);
        int passEvery = Mathf.Max(1, constraintIterations / Mathf.Max(1, collisionPasses));
        for (int iter = 0; iter < constraintIterations; iter++)
        {
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 delta = _positions[i + 1] - _positions[i];
                float dist = delta.magnitude;
                if (dist < 0.0001f) continue;
                float correction = (dist - segmentRest) / dist;
                Vector3 offset = delta * correction;

                bool pinA = i == 0;
                bool pinB = i + 1 == last && hipPinned;
                if (pinA && pinB) continue;
                if (pinA) _positions[i + 1] -= offset;
                else if (pinB) _positions[i] += offset;
                else { _positions[i] += offset * 0.5f; _positions[i + 1] -= offset * 0.5f; }
            }
            _positions[0] = _anchorEnd.position;
            if (hipPinned) _positions[last] = _hipEnd.position;

            // Pushout INTERLEAVED with the solve: a taut rope pulled around a corner is re-ejected
            // every few iterations, so the constraints can't drag it through the edge (the clipping).
            if ((iter + 1) % passEvery == 0) CollisionPass(hipPinned, last);
        }

        // Final guarantee pass — combined with the constraints this is what makes slack rope wrap
        // around a rock between the two ends instead of cutting through.
        CollisionPass(hipPinned, last);
    }

    private void CollisionPass(bool hipPinned, int last)
    {
        for (int i = 1; i < _positions.Length; i++)
        {
            if (i == last && hipPinned) continue;
            PushOutOfColliders(i);
        }
    }

    private void PushOutOfColliders(int index)
    {
        Vector3 p = _positions[index];
        int count = Physics.OverlapSphereNonAlloc(p, particleRadius, OverlapBuffer,
                                                  _collisionMask, QueryTriggerInteraction.Ignore);
        bool touched = false;
        for (int c = 0; c < count; c++)
        {
            Collider col = OverlapBuffer[c];
            Transform t = col.transform;
            if (_ignoreA != null && t.IsChildOf(_ignoreA)) continue;
            if (_ignoreB != null && t.IsChildOf(_ignoreB)) continue;

            Vector3 closest = col.ClosestPoint(p);
            Vector3 away = p - closest;
            float d = away.magnitude;
            if (d < 0.0001f)
            {
                away = p - col.bounds.center;
                d = away.magnitude;
                if (d < 0.0001f) continue;
                p = closest + away / d * particleRadius;
                touched = true;
            }
            else if (d < particleRadius)
            {
                p = closest + away / d * particleRadius;
                touched = true;
            }
        }

        if (touched)
        {
            _positions[index] = p;
            _previous[index] = Vector3.Lerp(_previous[index], p, contactFriction);
            _touched[index] = true;   // wrap-pivot candidate for the range checks
        }
    }
}
