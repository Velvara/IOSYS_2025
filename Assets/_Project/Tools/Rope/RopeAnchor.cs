using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A placed rope anchor in the world. Owns its rope visual and persists after the player lets
/// go — multiple anchors can exist at once (DS-style). The rope is drawn in two parts:
///   • the USED portion — straight taut segments (HookshotRope) running anchor → [ledge pivot]
///     → hand → hand (or anchor → pivot when dropped),
///   • the free TAIL — the remaining rope length dangling from the last used point as a
///     one-end-pinned verlet strand (<see cref="VerletRopeTail"/>). No tension there, so verlet
///     drapes/piles it correctly. The tail shrinks as the player descends (more rope taut) and
///     grows as they ascend; when dropped, everything below the ledge pivot becomes tail.
/// Used-arc + tail-length always equals <see cref="RopeLength"/>.
/// </summary>
public class RopeAnchor : MonoBehaviour
{
    /// <summary>All placed anchors in the scene, for nearest-rope re-grab queries.</summary>
    public static readonly List<RopeAnchor> Placed = new List<RopeAnchor>();

    [Header("Rope Visual")]
    [Tooltip("Where the rope leaves the anchor (child at the top of the mesh). Falls back to the anchor root.")]
    public Transform ropeStart;
    public Material ropeMaterial;
    public int ropeCylinderSides = 8;
    public float ropeWidth = 0.03f;
    [Tooltip("With two hand points, the rope routes anchor → HIGHER hand → lower hand, re-ordering " +
             "live as the animation moves the hands. This is the height difference needed to swap " +
             "(hysteresis, so it doesn't flicker while the hands cross).")]
    public float handSwapHysteresis = 0.06f;

    [Header("Free Tail (verlet)")]
    [Tooltip("Particles in the dangling tail. More = smoother drape, slightly more cost while it simulates.")]
    public int tailParticles = 72;
    [Tooltip("Seconds the dropped tail keeps simulating (falling/settling) before it freezes to zero cost.")]
    public float dropSettleSeconds = 2.5f;
    [Tooltip("Surfaces the tail collides with (its own anchor pieces and the player are ignored by hierarchy).")]
    public LayerMask tailCollisionMask = ~0;

    /// <summary>Total usable rope length, set from the consumed RopeItem at placement.</summary>
    public float RopeLength { get; private set; } = 20f;

    public bool IsHeld => holder != null;

    /// <summary>Rope start point in world space (for length/nearest-point math).</summary>
    public Vector3 StartPoint => (ropeStart != null ? ropeStart : transform).position;

    /// <summary>Current far end of the rope in world space (the dangling tip when not held).</summary>
    public Vector3 EndPoint
    {
        get
        {
            if (holder != null) return holder.position;
            if (tail != null && tail.HasLength) return tail.FreeEnd;
            return StartPoint;
        }
    }

    private HookshotRope rope;            // anchor → ledge pivot (if any) or first hand
    private HookshotRope ropeWaypointSeg; // ledge pivot → first hand (only while held over a pivot)
    private HookshotRope ropeSecond;      // first hand → second hand (only while held with two points)
    private VerletRopeTail tail;          // free remainder hanging from the last used point
    private Transform holder;             // player attach point while held
    private Transform handA, handB;       // the two hand points as given; routing order is dynamic
    private bool handOrderSwapped;        // true = handB is currently the FIRST (higher) hand
    private Transform waypoint;           // ledge pivot the rope runs over (rappel edge object)
    private Transform playerRoot;         // player hierarchy root, so the tail ignores the player's colliders
    private bool onWall;                  // while held, the tail only shows once the body is on the wall (rappelling)

    private readonly Vector3[] chainBuf = new Vector3[4];   // reused for nearest-point over the straight chain

    /// <summary>World position of the ledge pivot, if one exists (rope system reads it to pick
    /// where a top-out drop parks the rope).</summary>
    public Vector3? WaypointPosition => waypoint != null ? waypoint.position : (Vector3?)null;

    /// <summary>Wall entry captured when the rappel pivot was placed — a top-side re-grab of
    /// this rope re-enters the wall rappel at the same point.</summary>
    public bool HasRappelEntry { get; private set; }
    public Vector3 RappelWallPoint { get; private set; }
    public Vector3 RappelWallNormal { get; private set; }

    public void SetRappelEntry(Vector3 wallPoint, Vector3 wallNormal)
    {
        RappelWallPoint = wallPoint;
        RappelWallNormal = wallNormal;
        HasRappelEntry = true;
    }

    /// <summary>While the rope is held, the organic tail only shows once the body is on the wall
    /// (rappelling) — not during the placement animation or a ground-level hold. A dropped rope
    /// always shows its tail regardless.</summary>
    public void SetOnWall(bool value)
    {
        if (onWall == value) return;
        onWall = value;
        UpdateTail();
    }

    private void OnEnable() { Placed.Add(this); }
    private void OnDisable() { Placed.Remove(this); }

    public void Init(float ropeLength)
    {
        RopeLength = ropeLength;
    }

    /// <summary>
    /// Closest point on the whole rope (straight used chain + dangling tail) to a world position.
    /// <paramref name="lengthAlongRope"/> is the arc distance from the anchor to that point — the
    /// effective grab length when the player re-grabs the rope there.
    /// </summary>
    public Vector3 NearestPointOnRope(Vector3 position, out float lengthAlongRope)
    {
        int n = 0;
        chainBuf[n++] = StartPoint;
        if (waypoint != null) chainBuf[n++] = waypoint.position;
        if (holder != null)
        {
            Transform first = handOrderSwapped && handB != null ? handB : handA;
            Transform second = handB != null ? (handOrderSwapped ? handA : handB) : null;
            if (first != null) chainBuf[n++] = first.position;
            if (second != null && n < chainBuf.Length) chainBuf[n++] = second.position;
        }

        Vector3 best = chainBuf[0];
        float bestSqr = (position - best).sqrMagnitude;
        lengthAlongRope = 0f;

        float acc = 0f;
        for (int i = 0; i < n - 1; i++)
        {
            Vector3 p = NearestOnSegment(chainBuf[i], chainBuf[i + 1], position, acc, out float len);
            float sqr = (position - p).sqrMagnitude;
            if (sqr < bestSqr) { bestSqr = sqr; best = p; lengthAlongRope = len; }
            acc += Vector3.Distance(chainBuf[i], chainBuf[i + 1]);
        }

        // The dangling tail (pinned at the last chain point) is grabbable too.
        if (tail != null && tail.HasLength)
        {
            Vector3 tp = tail.NearestPoint(position, out float tArc);
            if ((position - tp).sqrMagnitude < bestSqr) { best = tp; lengthAlongRope = acc + tArc; }
        }
        return best;
    }

    private static Vector3 NearestOnSegment(Vector3 a, Vector3 b, Vector3 position,
                                            float arcLengthAtA, out float lengthAlongRope)
    {
        Vector3 ab = b - a;
        float sqrLen = ab.sqrMagnitude;
        if (sqrLen < 0.0001f)
        {
            lengthAlongRope = arcLengthAtA;
            return a;
        }

        float t = Mathf.Clamp01(Vector3.Dot(position - a, ab) / sqrLen);
        lengthAlongRope = arcLengthAtA + t * Mathf.Sqrt(sqrLen);
        return a + ab * t;
    }

    /// <summary>Rope end follows the given transform (the player's hold point). Optional
    /// second point routes the rope onward over the other hand as a second segment.</summary>
    public void AttachTo(Transform holdPoint, Transform secondHoldPoint = null)
    {
        holder = holdPoint;
        handA = holdPoint;
        handB = secondHoldPoint;
        handOrderSwapped = false;
        onWall = false;   // hidden until the rope system reports the body is on the wall
        playerRoot = holdPoint != null ? holdPoint.root : playerRoot;

        if (tail != null) tail.WakeUp();   // re-grabbed: the tail simulates live from the hand again
        RebuildRouting();
    }

    /// <summary>
    /// Places (or moves) the ledge pivot the rope runs over — e.g. the edge object spawned when a
    /// wall rappel starts. The rope then routes anchor → pivot → hands (or anchor → pivot → tail
    /// when dropped). Owned by the anchor so a dropped rope stays correctly draped over the edge.
    /// </summary>
    public void SetWaypoint(Vector3 position, GameObject visualPrefab)
    {
        if (waypoint == null)
        {
            GameObject go = visualPrefab != null
                ? Instantiate(visualPrefab, position, Quaternion.identity)
                : new GameObject("RopeLedgePivot");
            go.name = "RopeLedgePivot";
            go.transform.SetParent(transform, worldPositionStays: true);
            waypoint = go.transform;
        }
        waypoint.position = position;
        RebuildRouting();
    }

    /// <summary>Removes the ledge pivot (e.g. after topping back out) — rope runs direct again.</summary>
    public void ClearWaypoint()
    {
        if (waypoint == null) return;
        Destroy(waypoint.gameObject);
        waypoint = null;
        HasRappelEntry = false;   // entry data lives and dies with the pivot
        RebuildRouting();
    }

    private void LateUpdate()
    {
        if (holder == null) return;   // dropped: the tail settles on its own, nothing to drive here

        // Keep the rope routed anchor → higher hand → lower hand while both hands hold it.
        if (handB != null)
        {
            Transform first = handOrderSwapped ? handB : handA;
            Transform second = handOrderSwapped ? handA : handB;
            if (second.position.y > first.position.y + handSwapHysteresis)
            {
                handOrderSwapped = !handOrderSwapped;
                RebuildRouting();
                return;
            }
        }

        // Hands move every frame while rappelling/holding — the taut arc grows/shrinks, so the
        // tail's rest length tracks it (used-arc + tail = RopeLength).
        UpdateTail();
    }

    /// <summary>Points every straight segment at the current chain and hangs the tail off its end:
    /// anchor → [pivot] → first hand → [second hand] while held, anchor → [pivot] → tail when dropped.</summary>
    private void RebuildRouting()
    {
        Transform startT = ropeStart != null ? ropeStart : transform;

        if (holder != null)
        {
            Transform first = handOrderSwapped && handB != null ? handB : handA;
            Transform second = handB != null ? (handOrderSwapped ? handA : handB) : null;
            if (first == null) return;

            if (rope == null) rope = CreateSegment("Rope", startT, first);
            rope.SetStart(startT);

            if (waypoint != null)
            {
                rope.SetEnd(waypoint);
                if (ropeWaypointSeg == null) ropeWaypointSeg = CreateSegment("RopeLedgeSegment", waypoint, first);
                ropeWaypointSeg.SetStart(waypoint);
                ropeWaypointSeg.SetEnd(first);
                ropeWaypointSeg.gameObject.SetActive(true);
            }
            else
            {
                rope.SetEnd(first);
                if (ropeWaypointSeg != null) ropeWaypointSeg.gameObject.SetActive(false);
            }

            if (second != null)
            {
                if (ropeSecond == null) ropeSecond = CreateSegment("RopeHandSegment", first, second);
                ropeSecond.SetStart(first);
                ropeSecond.SetEnd(second);
                ropeSecond.gameObject.SetActive(true);
            }
            else if (ropeSecond != null)
            {
                ropeSecond.gameObject.SetActive(false);
            }
        }
        else
        {
            // Dropped: only the anchor → pivot span stays straight; everything below is the tail.
            if (waypoint != null)
            {
                if (rope == null) rope = CreateSegment("Rope", startT, waypoint);
                rope.SetStart(startT);
                rope.SetEnd(waypoint);
                rope.gameObject.SetActive(true);
            }
            else if (rope != null)
            {
                rope.gameObject.SetActive(false);   // no pivot: the whole rope is tail from the anchor
            }
            if (ropeWaypointSeg != null) ropeWaypointSeg.gameObject.SetActive(false);
            if (ropeSecond != null) ropeSecond.gameObject.SetActive(false);
        }

        UpdateTail();
    }

    /// <summary>The transform the tail hangs from (last used point) and the straight arc length
    /// up to it. Held: the lower hand. Dropped: the ledge pivot (or the anchor if none).</summary>
    private void GetTailPin(out Transform pin, out float usedArc)
    {
        Vector3 a = StartPoint;
        if (holder != null)
        {
            Transform first = handOrderSwapped && handB != null ? handB : handA;
            Transform second = handB != null ? (handOrderSwapped ? handA : handB) : null;
            pin = second != null ? second : first;

            usedArc = 0f;
            Vector3 prev = a;
            if (waypoint != null) { usedArc += Vector3.Distance(prev, waypoint.position); prev = waypoint.position; }
            if (first != null) { usedArc += Vector3.Distance(prev, first.position); prev = first.position; }
            if (second != null) usedArc += Vector3.Distance(prev, second.position);
        }
        else
        {
            pin = waypoint != null ? waypoint : (ropeStart != null ? ropeStart : transform);
            usedArc = waypoint != null ? Vector3.Distance(a, waypoint.position) : 0f;
        }
    }

    private void UpdateTail()
    {
        GetTailPin(out Transform pin, out float usedArc);
        if (pin == null) return;

        EnsureTail(pin);
        tail.SetPin(pin);

        // Dropped ropes always drape; a held rope only shows its tail once it's on the wall.
        bool visible = holder == null || onWall;
        tail.SetRestLength(visible ? Mathf.Max(0f, RopeLength - usedArc) : 0f);
    }

    private void EnsureTail(Transform pin)
    {
        if (tail != null) return;

        var go = new GameObject("RopeTail");
        go.AddComponent<MeshFilter>();
        go.AddComponent<MeshRenderer>();
        tail = go.AddComponent<VerletRopeTail>();
        tail.particleCount = tailParticles;
        tail.ropeRadius = ropeWidth * 0.5f;
        tail.collisionMask = tailCollisionMask;
        tail.Init(pin, ropeMaterial);
        tail.SetIgnoreRoots(transform, playerRoot);
        // Kept at world identity for its world-space verts; parented for lifetime, not transform.
        go.transform.SetParent(transform, worldPositionStays: true);
    }

    /// <summary>Player let go: the used portion collapses to anchor → pivot, and the entire
    /// remaining length below the pivot becomes the free tail, which falls and settles once.</summary>
    public void Detach()
    {
        if (holder == null) return;

        holder = null;
        handA = handB = null;
        handOrderSwapped = false;
        RebuildRouting();   // straight anchor → pivot; tail re-pinned to the pivot, rest length grown

        if (tail != null)
        {
            tail.SetIgnoreRoots(transform, playerRoot);
            tail.WakeUp();
            tail.BeginSettle(dropSettleSeconds);
        }
    }

    private HookshotRope CreateSegment(string name, Transform start, Transform end)
    {
        var ropeObj = new GameObject(name);
        ropeObj.transform.SetParent(transform, worldPositionStays: true);

        ropeObj.AddComponent<MeshFilter>();
        var mr = ropeObj.AddComponent<MeshRenderer>();
        mr.material = ropeMaterial;

        var segment = ropeObj.AddComponent<HookshotRope>();
        segment.waveAmplitude = 0f;   // static taut line — no swing/wave (locked design decision)
        segment.Init(start, end, ropeCylinderSides, 0.5f, ropeWidth);
        return segment;
    }
}
