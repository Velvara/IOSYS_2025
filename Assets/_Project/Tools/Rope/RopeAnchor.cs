using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A placed rope anchor in the world. Owns its rope visual (a static-taut HookshotRope
/// from the anchor to either the holding player or a parked drop point) and persists
/// after the player lets go — multiple anchors can exist at once (DS-style).
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

    /// <summary>Total usable rope length, set from the consumed RopeItem at placement.</summary>
    public float RopeLength { get; private set; } = 20f;

    public bool IsHeld => holder != null;

    /// <summary>Rope start point in world space (for length/nearest-point math).</summary>
    public Vector3 StartPoint => (ropeStart != null ? ropeStart : transform).position;

    /// <summary>Current far end of the rope in world space.</summary>
    public Vector3 EndPoint
    {
        get
        {
            if (holder != null) return holder.position;
            if (parkedEnd != null) return parkedEnd.position;
            return StartPoint;
        }
    }

    private HookshotRope rope;        // anchor → waypoint (if any) or first hand / parked end
    private HookshotRope ropeWaypointSeg; // waypoint → first hand / parked end (only while a waypoint is set)
    private HookshotRope ropeSecond;  // first hand → second hand (only while held with two points)
    private Transform holder;         // player attach point while held
    private Transform parkedEnd;      // dummy transform holding the end where the rope was dropped
    private Transform handA, handB;   // the two hand points as given; routing order is dynamic
    private bool handOrderSwapped;    // true = handB is currently the FIRST (higher) hand
    private Transform waypoint;       // ledge pivot the rope runs over (rappel edge object)

    private void OnEnable() { Placed.Add(this); }
    private void OnDisable() { Placed.Remove(this); }

    public void Init(float ropeLength)
    {
        RopeLength = ropeLength;
    }

    /// <summary>
    /// Closest point on the rope path to a world position. <paramref name="lengthAlongRope"/>
    /// is the arc distance from the anchor to that point — the effective grab length when the
    /// player re-grabs the rope there. With a ledge pivot, both segments are considered.
    /// </summary>
    public Vector3 NearestPointOnRope(Vector3 position, out float lengthAlongRope)
    {
        Vector3 a = StartPoint;
        if (waypoint == null)
            return NearestOnSegment(a, EndPoint, position, 0f, out lengthAlongRope);

        Vector3 w = waypoint.position;
        Vector3 p1 = NearestOnSegment(a, w, position, 0f, out float len1);
        Vector3 p2 = NearestOnSegment(w, EndPoint, position, Vector3.Distance(a, w), out float len2);

        if ((position - p1).sqrMagnitude <= (position - p2).sqrMagnitude)
        {
            lengthAlongRope = len1;
            return p1;
        }
        lengthAlongRope = len2;
        return p2;
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
        RebuildRouting();
    }

    /// <summary>
    /// Places (or moves) the ledge pivot the rope runs over — e.g. the edge object spawned when a
    /// wall rappel starts. The rope then routes anchor → pivot → hands (or parked end). Owned by
    /// the anchor so a dropped rope stays correctly draped over the edge.
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
        RebuildRouting();
    }

    private void LateUpdate()
    {
        // Keep the rope routed anchor → higher hand → lower hand while both hands hold it.
        if (holder == null || handB == null) return;

        Transform first = handOrderSwapped ? handB : handA;
        Transform second = handOrderSwapped ? handA : handB;
        if (second.position.y > first.position.y + handSwapHysteresis)
        {
            handOrderSwapped = !handOrderSwapped;
            RebuildRouting();
        }
    }

    /// <summary>Points every segment at the current chain: anchor → [waypoint] → first hand or
    /// parked end → [second hand]. Segments are created on demand and toggled off when unused.</summary>
    private void RebuildRouting()
    {
        // Far target after the optional waypoint: the (higher) hand while held, the parked end otherwise.
        Transform target = holder != null
            ? (handOrderSwapped && handB != null ? handB : handA)
            : parkedEnd;
        if (target == null) return;   // nothing to render yet

        Transform startT = ropeStart != null ? ropeStart : transform;
        if (rope == null) rope = CreateSegment("Rope", startT, target);

        if (waypoint != null)
        {
            rope.SetEnd(waypoint);
            if (ropeWaypointSeg == null) ropeWaypointSeg = CreateSegment("RopeLedgeSegment", waypoint, target);
            ropeWaypointSeg.SetStart(waypoint);
            ropeWaypointSeg.SetEnd(target);
            ropeWaypointSeg.gameObject.SetActive(true);
        }
        else
        {
            rope.SetEnd(target);
            if (ropeWaypointSeg != null) ropeWaypointSeg.gameObject.SetActive(false);
        }

        if (holder != null && handB != null)
        {
            Transform first = handOrderSwapped ? handB : handA;
            Transform second = handOrderSwapped ? handA : handB;
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

    /// <summary>Player let go: park the rope end where it was and keep the rope in the world.</summary>
    public void Detach()
    {
        if (holder == null) return;

        if (parkedEnd == null)
        {
            var go = new GameObject("RopeEnd");
            go.transform.SetParent(transform, worldPositionStays: true);
            parkedEnd = go.transform;
        }
        parkedEnd.position = holder.position;

        holder = null;
        handA = handB = null;
        handOrderSwapped = false;
        RebuildRouting();   // rope (and waypoint segment, if any) re-targets the parked end; hand segment off
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
