using System.Collections;
using System.Collections.Generic;
using Game.Climbing;
using Game.PlayerV2;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side carbine (carabiner) system — Stage 3 of the risk/rope batch: placement + tether +
/// detach. While CLIMBING with the carbine item selected (CycleItems prefab carrying CarbineItem),
/// a SHORT Use press (place-on-release) spawns the carbine at the RIGHT hand's hold (consuming one
/// from the inventory) and creates the tether rope carbine → hip slot. While climbing TETHERED,
/// HOLDING Use past holdDetachTime detaches instead: the rope lets go of the hip, dangles from the
/// carbine, every material exposing the dissolve property fades 0→1, and carbine + rope are
/// destroyed. The tether sim is selectable: VerletTetherRope (default — cheap, wraps around
/// geometry) or PhysicsTetherRope (PhysX joint chain, the A/B comparison).
///
/// This stage is placement/visual only — rope-length clamping mid-jump, the yank stop, dangling,
/// carbine break checks and multi-carbine routing are the NEXT stage (they read IsTethered /
/// TautAmount / AnchorPoint from here).
/// </summary>
public class CarbineController : MonoBehaviour
{
    public enum TetherSim { VerletCollision, PhysXJoints }

    [Header("Attach Points")]
    [Tooltip("Child of the hip bone the rope ties to (create 'hipSlot' under the hip). Falls back to " +
             "the humanoid Hips bone, then this transform.")]
    public Transform hipSlot;

    [Header("Placement")]
    [Tooltip("Lift off the hold along its outward normal so the carbine mesh sits ON the rock, not in it.")]
    public float carbineSurfaceOffset = 0.03f;

    [Header("Input")]
    [Tooltip("Seconds Use must be HELD (while climbing tethered) to detach the rope. A press RELEASED " +
             "before this places a carbine instead (place-on-release, so a detach never also places).")]
    public float holdDetachTime = 0.5f;

    [Header("Tether Rope")]
    [Tooltip("Which simulation drives the tether. Verlet+collision is the default (cheap, stable, wraps " +
             "around geometry); PhysX joints is the A/B alternative to feel out. Applies to the NEXT " +
             "placement — safe to toggle between placements.")]
    public TetherSim simulation = TetherSim.VerletCollision;
    public Material ropeMaterial;
    [Tooltip("What the rope collides with / drapes over (the player and the carbine are excluded automatically).")]
    public LayerMask ropeCollisionMask = ~0;
    [Tooltip("PROGRESSIVE PAY-OUT: the simulated rope is only ever carbine→hip distance PLUS this " +
             "slack — it pays out as the player moves away and reels back in approaching, never past " +
             "the item's max rope length (the rest stays 'on the spool').")]
    public float paidSlack = 1f;
    [Tooltip("Range enforcement while CLIMBING: hand steps to holds farther than (max rope length − " +
             "this margin) from the carbine are refused — at rope's end the climber can't advance " +
             "without detaching. The margin accounts for the hip hanging below the hands.")]
    public float ropeEndMargin = 0.3f;
    [Tooltip("How fast spare rope reels back in (m/s) as the player moves toward the carbine. Paying " +
             "OUT is instant (movement away / a corner wrap must never be fought by the rope).")]
    public float reelSpeed = 3f;
    [Tooltip("Spare rope below this stays paid out (hysteresis) — avoids pay/reel flicker as the " +
             "needed length wobbles with the body.")]
    public float reelHysteresis = 0.2f;
    [Tooltip("How far off the surface a wrap pivot sits (the measuring chain's corner points).")]
    public float pivotSkin = 0.06f;

    [Header("Debug")]
    [Tooltip("While tethered: draw the live range boundary (wire sphere from the last wrap pivot), " +
             "the pivot chain, and the current surface's holds near the boundary coloured by " +
             "reachability (green = still steppable, red = past the rope). Gizmos must be enabled.")]
    public bool showRangeDebug = false;
    [Tooltip("Only holds within this distance of the range boundary draw (dense bakes hold ~21k).")]
    public float rangeDebugBand = 1.5f;

    [Header("Detach Dissolve")]
    [Tooltip("Material property driven 0→1 on detach, on every renderer of the carbine AND the rope " +
             "that exposes it. Must match the shader's exposed reference name (e.g. \"dissolveTime\" " +
             "or \"_DissolveTime\").")]
    public string dissolveProperty = "dissolveTime";
    [Tooltip("Seconds the dissolve takes before carbine + rope are destroyed.")]
    public float dissolveDuration = 1.5f;

    [Tooltip("Log placement/detach events to the Console.")]
    public bool logEvents = true;

    // -- Read by the fall/yank stage --
    public bool IsTethered => _tether != null && _tether.HipAttached;
    public TetherRopeBase Tether => _tether;
    /// <summary>The tethering carbine's rope point (the anchor of a future yank/rappel).</summary>
    public Vector3 AnchorPoint => _ropePoint != null ? _ropePoint.position : transform.position;
    /// <summary>Rope still on the spool (max − paid out). The break formula reads paid-out length.</summary>
    public float RemainingLength => IsTethered ? _tether.RopeLength - _tether.ActiveLength : 0f;

    private ClimbController _climb;
    private CycleItems _items;
    private Animator _animator;
    private PlayerInput _playerInput;
    private InputAction _useAction;
    private IPlayerMotor _motor;

    private GameObject _carbine;       // placed world carbine (tethered one only)
    private Transform _ropePoint;
    private TetherRopeBase _tether;

    private bool _usePressed;
    private float _useDownTime;
    private bool _detachConsumed;      // the hold already detached — the coming release must not place

    private void Start()
    {
        _climb = GetComponentInParent<ClimbController>();
        _items = GetComponentInParent<CycleItems>();
        _animator = GetComponentInParent<Animator>();
        _playerInput = GetComponentInParent<PlayerInput>();
        _motor = GetComponentInParent<IPlayerMotor>();

        var actions = _playerInput != null ? _playerInput.actions : null;
        _useAction = actions != null ? actions["Use"] : null;
        if (_useAction != null)
        {
            _useAction.performed += OnUsePressed;
            _useAction.canceled += OnUseReleased;
        }
        else
        {
            Debug.LogWarning("[CarbineController] No 'Use' action found — carbine placement disabled.");
        }
    }

    private void OnDestroy()
    {
        if (_useAction != null)
        {
            _useAction.performed -= OnUsePressed;
            _useAction.canceled -= OnUseReleased;
        }
        if (_climb != null && IsTethered) _climb.SetReachConstraint(null);
    }

    private void Update()
    {
        // Hold-to-detach: fires the moment the threshold passes (not on release), so the player
        // gets immediate feedback and the release is inert afterwards.
        if (_usePressed && !_detachConsumed && IsTethered &&
            _climb != null && _climb.IsClimbing &&
            Time.unscaledTime - _useDownTime >= holdDetachTime)
        {
            _detachConsumed = true;
            DetachRope();
        }

        // Progressive pay-out, metered by the PIVOT CHAIN (geometric, noise-free — see UpdatePivots),
        // NOT by the verlet's own arc: a slack verlet chain always carries a little residual stretch,
        // so "pay out when the arc exceeds the paid length" was a ratchet that unspooled the whole
        // rope on its own sag. The chain gives the true required path (around corners included);
        // the sim is purely visual and just receives the resulting length.
        if (IsTethered)
        {
            Vector3 hip = HipPoint.position;
            UpdatePivots(hip);

            float desired = Mathf.Min(PathLengthTo(hip) + paidSlack, _tether.RopeLength);
            float active = _tether.ActiveLength;
            if (desired > active) active = desired;   // pay out instantly — never fight movement/wraps
            else if (desired < active - reelHysteresis)
                active = Mathf.MoveTowards(active, desired, reelSpeed * Time.deltaTime);

            _tether.SetActiveLength(active);
        }
    }

    private void LateUpdate()
    {
        // Range enforcement OFF the wall (tethered but not climbing — e.g. topped out and walking):
        // hard-clamp the body to the rope sphere, after the motor moved (RopeController's pattern).
        // While CLIMBING the cap is enforced upstream instead — hand steps past the range are refused
        // (ClimbController reach constraint), so the climber never gets out here. Airborne overshoot
        // (jump past rope's end) is the NEXT stage's yank — deliberately untouched.
        if (!IsTethered || _motor == null) return;
        if (_climb != null && _climb.IsClimbing) return;
        if (!_motor.Controller.isGrounded) return;

        // The clamp sphere sits at the LAST WRAP PIVOT with only the rope left past it as radius —
        // wrapped around a corner, anchor-straight distance would let the player walk far beyond
        // the rope; unwrapped, the last pivot IS the anchor and this is the plain rope sphere.
        Vector3 center = LastPivot;
        float maxLen = Mathf.Max(0.5f, _tether.RopeLength - _pivotBaseLen);

        Vector3 offset = _motor.Transform.position - center;
        if (offset.sqrMagnitude > maxLen * maxLen)
        {
            Vector3 clamped = center + offset * (maxLen / offset.magnitude);
            _motor.Controller.Move(clamped - _motor.Transform.position);
        }
    }

    /// <summary>Reach constraint registered with the ClimbController while tethered: a hand may only
    /// step to holds the rope can still reach — measured along the PIVOT CHAIN (arc to the last wrap
    /// pivot + straight to the hold), so corner wraps meter correctly, minus the hip-below-hands margin.</summary>
    private bool AllowHandTarget(Vector3 holdPos)
    {
        if (!IsTethered) return true;
        return PathLengthTo(holdPos) <= _tether.RopeLength - ropeEndMargin;
    }

    // ------------------------------------------------------------------ rope path measurement
    //
    // The MEASURING model of the rope: a chain of wrap pivots (anchor → corner bends → hip),
    // maintained by raycast — the segment to the hip gets blocked → bend the rope at the
    // obstruction (wrap); the pivot before the last sees the hip again → drop the last bend
    // (unwrap). Purely geometric, so it carries none of the verlet sim's stretch noise, ignores
    // ground drape, and gives the exact "how much rope does this position require" the pay-out,
    // the reach constraint and the ground clamp all share.

    private readonly List<Vector3> _pivots = new List<Vector3>();
    private float _pivotBaseLen;   // Σ segment lengths between pivots (excludes lastPivot→hip)
    private const int MaxPivots = 16;
    private static readonly RaycastHit[] RayHits = new RaycastHit[16];

    private Vector3 LastPivot => _pivots.Count > 0 ? _pivots[_pivots.Count - 1] : AnchorPoint;

    /// <summary>Rope required to reach <paramref name="point"/>: the wrapped base + the last straight leg.</summary>
    private float PathLengthTo(Vector3 point) => _pivotBaseLen + Vector3.Distance(LastPivot, point);

    private void ResetPivots()
    {
        _pivots.Clear();
        _pivots.Add(AnchorPoint);
        _pivotBaseLen = 0f;
    }

    private void UpdatePivots(Vector3 hip)
    {
        if (_pivots.Count == 0) ResetPivots();

        // UNWRAP: the pivot before the last sees the hip directly → the last bend is gone.
        while (_pivots.Count > 1)
        {
            if (RopeRayBlocked(_pivots[_pivots.Count - 2], hip, out _)) break;
            _pivotBaseLen -= Vector3.Distance(_pivots[_pivots.Count - 2], _pivots[_pivots.Count - 1]);
            _pivots.RemoveAt(_pivots.Count - 1);
        }
        if (_pivots.Count == 1) _pivotBaseLen = 0f;   // numeric drift guard

        // WRAP: the segment to the hip is blocked → bend at the obstruction (a few per frame max).
        for (int guard = 0; guard < 4 && _pivots.Count < MaxPivots; guard++)
        {
            Vector3 from = _pivots[_pivots.Count - 1];
            if (!RopeRayBlocked(from, hip, out RaycastHit hit)) break;
            Vector3 p = hit.point + hit.normal * pivotSkin;
            if ((p - from).sqrMagnitude < 0.02f * 0.02f) break;   // degenerate — retry next frame
            _pivots.Add(p);
            _pivotBaseLen += Vector3.Distance(from, p);
        }
    }

    /// <summary>Nearest blocking hit between two rope points (endpoints slightly shortened so a
    /// pivot sitting ON a surface / the hip near the body never self-hit; player + carbine ignored).</summary>
    private bool RopeRayBlocked(Vector3 from, Vector3 to, out RaycastHit hit)
    {
        hit = default;
        Vector3 d = to - from;
        float len = d.magnitude;
        if (len < 0.12f) return false;
        d /= len;

        int n = Physics.RaycastNonAlloc(new Ray(from + d * 0.02f, d), RayHits, len - 0.1f,
                                        ropeCollisionMask & ~(1 << gameObject.layer),
                                        QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        int bi = -1;
        for (int i = 0; i < n; i++)
        {
            Transform t = RayHits[i].transform;
            if (t.IsChildOf(transform.root)) continue;
            if (_carbine != null && t.IsChildOf(_carbine.transform)) continue;
            if (RayHits[i].distance < best) { best = RayHits[i].distance; bi = i; }
        }
        if (bi < 0) return false;
        hit = RayHits[bi];
        return true;
    }

    private void OnUsePressed(InputAction.CallbackContext ctx)
    {
        if (_climb == null || !_climb.IsClimbing) return;   // carbine interactions are climb-only (this stage)
        _usePressed = true;
        _useDownTime = Time.unscaledTime;
        _detachConsumed = false;
    }

    private void OnUseReleased(InputAction.CallbackContext ctx)
    {
        if (!_usePressed) return;
        _usePressed = false;
        if (_detachConsumed) return;                        // this press already detached
        if (Time.unscaledTime - _useDownTime >= holdDetachTime) return;   // long hold without a tether — not a place
        if (_climb == null || !_climb.IsClimbing) return;   // e.g. a rope handoff consumed the press mid-hold
        TryPlaceCarbine();
    }

    // ------------------------------------------------------------------ placement

    private CarbineItem SelectedCarbineItem()
    {
        GameObject prefab = _items != null ? _items.currentPrefab : null;
        return prefab != null ? prefab.GetComponent<CarbineItem>() : null;
    }

    private void TryPlaceCarbine()
    {
        CarbineItem item = SelectedCarbineItem();
        if (item == null) return;   // carbine not selected — the press belongs to other systems

        if (IsTethered)
        {
            // Multi-carbine (the rope threading through each placed carbine as a via point) is the
            // NEXT stage — until then one tether at a time.
            if (logEvents) Debug.Log("[CarbineController] Already tethered — multi-carbine routing arrives next stage.");
            return;
        }
        if (item.placedPrefab == null)
        {
            Debug.LogWarning("[CarbineController] CarbineItem has no placedPrefab assigned.");
            return;
        }
        if (!_climb.TryGetRightHandHold(out Vector3 holdPos, out Quaternion holdRot))
            return;   // hand not settled on a hold (slide/slip/jump/deferred attach) — no placement point

        if (_items == null || !_items.TryConsumeCurrent())
            return;

        _carbine = Instantiate(item.placedPrefab,
                               holdPos + (holdRot * Vector3.forward) * carbineSurfaceOffset, holdRot);
        _ropePoint = ResolveRopePoint(_carbine.transform, item.ropePointChildName);

        var tetherGO = new GameObject(simulation == TetherSim.VerletCollision
            ? "CarbineTether (verlet)" : "CarbineTether (physx)");
        _tether = simulation == TetherSim.VerletCollision
            ? tetherGO.AddComponent<VerletTetherRope>()
            : (TetherRopeBase)tetherGO.AddComponent<PhysicsTetherRope>();
        _tether.Init(_ropePoint, HipPoint, item.ropeLength, ropeMaterial,
                     ropeCollisionMask & ~(1 << gameObject.layer),
                     _carbine.transform, transform.root);
        ResetPivots();
        _tether.SetActiveLength(Vector3.Distance(AnchorPoint, HipPoint.position) + paidSlack);
        _climb.SetReachConstraint(AllowHandTarget);   // no hand steps past the rope's range

        if (logEvents)
            Debug.Log($"[CarbineController] Carbine placed at the right hand's hold ({item.ropeLength} m " +
                      $"tether, {simulation}).", _carbine);
    }

    private static Transform ResolveRopePoint(Transform carbine, string childName)
    {
        if (!string.IsNullOrEmpty(childName))
        {
            Transform t = FindDeep(carbine, childName);
            if (t != null) return t;
        }
        return carbine;
    }

    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    private Transform HipPoint
    {
        get
        {
            if (hipSlot != null) return hipSlot;
            Transform hips = _animator != null && _animator.isHuman
                ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            return hips != null ? hips : transform;
        }
    }

    // ------------------------------------------------------------------ detach + dissolve

    private void DetachRope()
    {
        if (_tether == null) return;
        if (logEvents) Debug.Log("[CarbineController] Rope detached — dangling from the carbine, dissolving.");

        _tether.ReleaseHipEnd();
        StartCoroutine(DissolveAndDestroy(_carbine, _tether.gameObject));

        _carbine = null;
        _ropePoint = null;
        _tether = null;
        _pivots.Clear();
        _pivotBaseLen = 0f;
        _climb?.SetReachConstraint(null);   // full climbing range back
    }

    /// <summary>Drives the dissolve property 0→1 on every renderer material (carbine + rope) that
    /// exposes it, then destroys both objects. Materials are instanced by the access — fine, they
    /// die with the objects right after.</summary>
    private IEnumerator DissolveAndDestroy(GameObject carbine, GameObject rope)
    {
        var mats = new List<Material>();
        CollectDissolveMaterials(carbine, mats);
        CollectDissolveMaterials(rope, mats);
        if (mats.Count == 0 && logEvents)
            Debug.LogWarning($"[CarbineController] No material exposes '{dissolveProperty}' — destroying " +
                             "without a fade (check the property's exact shader reference name).");

        float t = 0f;
        float dur = Mathf.Max(0.05f, dissolveDuration);
        while (t < dur)
        {
            t += Time.deltaTime;
            float v = Mathf.Clamp01(t / dur);
            for (int i = 0; i < mats.Count; i++)
                if (mats[i] != null) mats[i].SetFloat(dissolveProperty, v);
            yield return null;
        }

        if (carbine != null) Destroy(carbine);
        if (rope != null) Destroy(rope);
    }

    private void CollectDissolveMaterials(GameObject root, List<Material> into)
    {
        if (root == null) return;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
        {
            Material m = r.material;
            if (m != null && m.HasProperty(dissolveProperty)) into.Add(m);
        }
    }

#if UNITY_EDITOR
    // ------------------------------------------------------------------ range debug (showRangeDebug)
    // Orange wire sphere = the live search boundary (centered on the LAST wrap pivot, radius = rope
    // left past it); yellow lines = the pivot chain; cubes = current-surface holds near the boundary,
    // green = still steppable / red = past the rope. Band-limited + stride-sampled — never judge
    // completeness by cube density (dense bakes hold ~21k).
    private void OnDrawGizmos()
    {
        if (!showRangeDebug || !Application.isPlaying || !IsTethered) return;

        float max = _tether.RopeLength - ropeEndMargin;
        Vector3 pivot = LastPivot;
        float remaining = Mathf.Max(0f, max - _pivotBaseLen);

        Gizmos.color = new Color(1f, 0.6f, 0.1f);
        Gizmos.DrawWireSphere(pivot, remaining);
        Gizmos.color = Color.yellow;
        for (int i = 0; i < _pivots.Count - 1; i++)
            Gizmos.DrawLine(_pivots[i], _pivots[i + 1]);

        var surface = _climb != null ? _climb.CurrentSurface : null;
        if (surface == null || !surface.HoldsReady) return;
        var holds = surface.Holds;
        Transform st = surface.transform;
        int stride = Mathf.Max(1, holds.Count / 4000);
        int drawn = 0;
        for (int i = 0; i < holds.Count; i += stride)
        {
            Vector3 wp = st.TransformPoint(holds[i].LocalPosition);
            float path = _pivotBaseLen + Vector3.Distance(pivot, wp);
            if (Mathf.Abs(path - max) > rangeDebugBand) continue;
            Gizmos.color = path <= max ? new Color(0.2f, 1f, 0.4f) : new Color(1f, 0.25f, 0.2f);
            Gizmos.DrawCube(wp, Vector3.one * 0.06f);
            if (++drawn >= 2500) break;
        }
    }
#endif
}
