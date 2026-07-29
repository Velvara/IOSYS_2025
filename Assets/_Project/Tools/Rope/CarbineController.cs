using System.Collections;
using System.Collections.Generic;
using Game.Climbing;
using Game.PlayerV2;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Player-side carbine (carabiner) system — placement + tether + detach + rope-range gate. While
/// CLIMBING with the carbine item selected (CycleItems prefab carrying CarbineItem), a Use press
/// spawns the carbine at the RIGHT hand's hold (consuming one from the inventory) and creates the
/// tether rope carbine → hip slot; pressing Use again while tethered REPLACES it (detach the old,
/// place a fresh one at the new hold, recompute the reachable set — the carbine climbs up with you).
/// If the inventory is empty the press is ignored (the current tether is kept). DETACHING is a Cancel
/// TAP (the rope lets go of the hip, dangles from the carbine, every material exposing the dissolve
/// property fades 0→1, then carbine + rope are destroyed); the climb also auto-detaches when it ends
/// (release / mantle top-out / fall — via ClimbController.ClimbReleased), but a tethered jump-off
/// keeps the rope. While tethered, hand steps are gated to the rope's along-surface reach (a geodesic
/// hold set cached at placement — HoldGeodesic). The tether sim is selectable: VerletTetherRope
/// (default — cheap, wraps around geometry) or PhysicsTetherRope (PhysX joint chain, the A/B compare).
///
/// The airborne yank stop, dangle→reattach/rappel, carbine break checks and multi-carbine routing are
/// the NEXT stage (they read IsTethered / TautAmount / AnchorPoint / RemainingLength from here).
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

    [Header("Yank (airborne rope catch)")]
    [Tooltip("Extra stretch (m) the rope allows PAST its length before the catch is fully hard — the " +
             "elastic 'give' so a jump that runs the rope out snaps taut instead of hitting a wall.")]
    public float yankGiveDistance = 0.3f;
    [Tooltip("How fast (m/s) the stretched rope pulls the body back toward its true length after a yank — " +
             "the elastic snap-back / settle rate. Higher = snappier catch.")]
    public float yankReturnSpeed = 4f;
    [Tooltip("How fast the motor's accumulated fall speed bleeds off while the rope bears the body's weight " +
             "(so a later release doesn't inherit a runaway gravity build-up). Gentle keeps the first swing.")]
    public float yankVelocityDamp = 6f;

    [Header("Debug")]
    [Tooltip("While tethered: draw the pivot chain (yellow — the physical rope path used for pay-out + " +
             "the off-wall clamp) and the cached geodesic reachable-hold set in green (the holds a hand " +
             "may still step to on the rope). Gizmos must be enabled.")]
    public bool showRangeDebug = false;

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
            // Use (with a carbine selected, while climbing) PLACES a carbine — or REPLACES the current
            // one if already tethered (detach the old, place a fresh one, recompute the reachable set).
            _useAction.performed += OnUsePressed;
        }
        else
        {
            Debug.LogWarning("[CarbineController] No 'Use' action found — carbine placement disabled.");
        }

        if (_climb != null)
        {
            // Leaving the wall for good (release / reach-bottom / slide-slip fall / ragdoll / mantle
            // top-out) auto-detaches the tether — a climb jump-off does NOT fire this, so a tethered
            // jump keeps the rope MID-AIR. A clean FEET-DOWN landing after that jump (ClimbJumpLanded)
            // detaches at that point. A "free" Cancel TAP while climbing (not consumed by a BracedReady
            // cancel) detaches on demand.
            _climb.ClimbReleased += OnClimbReleased;
            _climb.CancelTapped += OnCancelTapped;
            _climb.ClimbJumpLanded += OnClimbJumpLanded;
        }
    }

    private void OnDestroy()
    {
        if (_useAction != null)
            _useAction.performed -= OnUsePressed;
        if (_climb != null)
        {
            _climb.ClimbReleased -= OnClimbReleased;
            _climb.CancelTapped -= OnCancelTapped;
            _climb.ClimbJumpLanded -= OnClimbJumpLanded;
            if (IsTethered) _climb.SetReachConstraint(null);
        }
    }

    /// <summary>The climber left the wall for good (or topped out) — drop the tether automatically. A
    /// jump-off never reaches here (the dev wants a tethered jump to keep the rope for the yank stage).</summary>
    private void OnClimbReleased()
    {
        if (IsTethered) DetachRope();
    }

    /// <summary>A "free" Cancel tap while climbing (one the controller didn't spend cancelling a
    /// BracedReady peek) detaches the tether — the climber stays on the wall with full hold range back.</summary>
    private void OnCancelTapped()
    {
        if (IsTethered) DetachRope();
    }

    /// <summary>A tethered jump-off ended with a clean feet-down landing — drop the rope at that point.
    /// (A fall/ragdoll instead detaches via ClimbReleased; a mid-air reattach keeps the rope.)</summary>
    private void OnClimbJumpLanded()
    {
        if (IsTethered) DetachRope();
    }

    private void Update()
    {
        // Progressive pay-out, metered by the PIVOT CHAIN (geometric, noise-free — see UpdatePivots),
        // NOT by the verlet's own arc: a slack verlet chain always carries a little residual stretch,
        // so "pay out when the arc exceeds the paid length" was a ratchet that unspooled the whole
        // rope on its own sag. The chain gives the true required path (around corners included);
        // the sim is purely visual and just receives the resulting length.
        if (IsTethered)
        {
            Vector3 hip = HipPoint.position;
            UpdatePivots(hip);

            // Pay-out length = current extension + slack, but the slack TAPERS from paidSlack down to 0 as
            // the extension nears the reach limit (RopeLength − ropeEndMargin — the same limit the hands are
            // gated to), so the rope pulls taut right as the climber reaches the end of their range instead
            // of hanging with a fixed +slack the reach gate never lets them close. Airborne (a tethered
            // jump, extension past the reach limit) the slack is already 0, so the full rope pays out to
            // RopeLength for the yank — hence the final RopeLength cap.
            float ext = PathLengthTo(hip);
            float reachLimit = Mathf.Max(0f, _tether.RopeLength - ropeEndMargin);
            float slack = Mathf.Min(paidSlack, Mathf.Max(0f, reachLimit - ext));
            float desired = Mathf.Min(ext + slack, _tether.RopeLength);
            float active = _tether.ActiveLength;
            if (desired > active) active = desired;   // pay out instantly — never fight movement/wraps
            else if (desired < active - reelHysteresis)
                active = Mathf.MoveTowards(active, desired, reelSpeed * Time.deltaTime);

            _tether.SetActiveLength(active);
        }
    }

    private void LateUpdate()
    {
        // Range enforcement OFF the wall (tethered, not climbing): keep the body inside the rope's
        // reach, after the motor moved (RopeController's pattern). Covers BOTH cases with one law:
        //   • grounded/walking tethered — a soft leash;
        //   • AIRBORNE after a tethered jump — the YANK: fly free while the rope has slack, then the
        //     moment the body passes the rope's length it's caught (elastic, softened by yankGiveDistance)
        //     and gravity turns the catch into a swing/hang.
        // While CLIMBING the cap is enforced upstream instead (the reach gate refuses hand steps past
        // the range), so the climber never reaches here.
        if (!IsTethered || _motor == null) { EndYank(); return; }
        if (_climb != null && _climb.IsClimbing) { EndYank(); return; }

        // The rope sphere sits at the LAST WRAP PIVOT with only the rope left past it as radius —
        // wrapped around a corner, anchor-straight distance would let the body travel far beyond the
        // rope; unwrapped, the last pivot IS the anchor and this is the plain rope sphere.
        Vector3 center = LastPivot;
        float maxLen = Mathf.Max(0.5f, _tether.RopeLength - _pivotBaseLen);

        Vector3 pos = _motor.Transform.position;
        Vector3 offset = pos - center;
        float dist = offset.magnitude;
        if (dist <= maxLen) { EndYank(); return; }   // within reach — no constraint, no yank

        // Past the rope. SOFTENED GIVE: allow up to yankGiveDistance of extra stretch (hard cap), then
        // reel that stretch back toward the true length at yankReturnSpeed — an elastic snatch that
        // settles taut, not a dead wall. Only the RADIAL component is touched, so the tangential swing
        // (the dangle) is left to gravity.
        Vector3 dir = dist > 1e-4f ? offset / dist : Vector3.up;
        float capped = Mathf.Min(dist, maxLen + Mathf.Max(0f, yankGiveDistance));
        float settled = Mathf.MoveTowards(capped, maxLen, Mathf.Max(0f, yankReturnSpeed) * Time.deltaTime);
        _motor.Controller.Move((center + dir * settled) - pos);

        if (!_yanking)
        {
            _yanking = true;
            if (logEvents) Debug.Log("[CarbineController] Rope YANK — caught at the rope's end.");
        }
        _motor.SuppressAirControl(0.15f);   // re-armed each caught frame; clears shortly after release

        // Bleed the motor's accumulated fall velocity while the rope bears the weight, so a later
        // release doesn't inherit a runaway gravity build-up. Gentle, so the first swing still reads.
        if (_motor.VerticalVelocity < 0f)
            _motor.SetVerticalVelocity(
                Mathf.MoveTowards(_motor.VerticalVelocity, 0f, Mathf.Max(0f, yankVelocityDamp) * Time.deltaTime));
    }

    /// <summary>True while the airborne rope catch (yank) is actively holding the body. Read by the
    /// dangle→reattach/rappel stage.</summary>
    public bool IsYanking => _yanking;
    private bool _yanking;

    private void EndYank() => _yanking = false;

    // ------------------------------------------------------------------ reachable-hold set (geodesic)
    //
    // While climbing, the rope's reach is measured NOT as straight-line distance but PROGRESSIVELY
    // hold → neighbouring hold along the surface (a geodesic flood-fill from the carbine's own hold —
    // HoldGeodesic / HoldSpatialGrid). The anchor (the carbine) and the rope length are both fixed for
    // a placement, so the reachable set is computed ONCE here, cached, and the per-hand-step gate is
    // then an O(1) lookup; it is recomputed only when the carbine is replaced. The pivot chain above
    // still measures the physical 3D rope for the pay-out visual and the off-wall ground clamp.
    private ClimbableSurface _carbineSurface;
    private bool[] _reachable;

    private void ComputeReachableSet()
    {
        _reachable = null;
        _carbineSurface = _climb != null ? _climb.CurrentSurface : null;
        if (_carbineSurface == null || !_carbineSurface.HoldsReady || _tether == null) return;

        var holds = _carbineSurface.Holds;
        float link = Mathf.Max(0.05f, _climb.MaxStepReach);   // "adjacent" = one hand-step apart
        var grid = new HoldSpatialGrid(holds, _carbineSurface.transform, link);
        int anchor = grid.NearestIndex(AnchorPoint);
        float budget = Mathf.Max(0f, _tether.RopeLength - ropeEndMargin);

        _reachable = new bool[holds.Count];
        int reached = HoldGeodesic.ComputeReachable(grid, anchor, budget, link, _climb.FacingCoherence, _reachable);
        if (logEvents)
            Debug.Log($"[CarbineController] Reachable holds within {budget:0.0} m of rope along the surface: " +
                      $"{reached}/{holds.Count} (link {link:0.00} m).");
    }

    /// <summary>Reach gate registered with the ClimbController while tethered: a hand may only step to
    /// a hold inside the cached geodesic set (holds within the rope's along-surface reach from the
    /// carbine). A different surface (climbed onto while tethered) is left unconstrained.</summary>
    private bool AllowHandTargetIndex(ClimbableSurface surface, int holdIndex)
    {
        if (!IsTethered) return true;
        if (surface != _carbineSurface || _reachable == null) return true;
        return (uint)holdIndex < (uint)_reachable.Length && _reachable[holdIndex];
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
        TryPlaceCarbine();                                  // place, or replace when already tethered
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

        if (item.placedPrefab == null)
        {
            Debug.LogWarning("[CarbineController] CarbineItem has no placedPrefab assigned.");
            return;
        }
        if (!_climb.TryGetRightHandHold(out Vector3 holdPos, out Quaternion holdRot))
            return;   // hand not settled on a hold (slide/slip/jump/deferred attach) — no placement point

        // Placing / REPLACING consumes one carbine. If the inventory is empty we do NOTHING — an
        // already-placed tether is kept rather than silently lost (dev's "keep current rope" choice).
        if (_items == null || !_items.TryConsumeCurrent())
            return;

        // Replace: with a carbine already out, drop the old rope first (it dissolves + destroys on its
        // own), then place the fresh one below and recompute the reachable set for the new anchor. The
        // carbine "climbs up" with the player as they re-place it higher.
        bool replacing = IsTethered;
        if (replacing) DetachRope();

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
        ComputeReachableSet();                              // geodesic reachable holds for this placement
        _climb.SetReachConstraint(AllowHandTargetIndex);   // no hand steps past the rope's range

        if (logEvents)
            Debug.Log($"[CarbineController] Carbine {(replacing ? "REPLACED" : "placed")} at the right hand's " +
                      $"hold ({item.ropeLength} m tether, {simulation}).", _carbine);
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
        _reachable = null;
        _carbineSurface = null;
        _yanking = false;
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
    // Yellow lines = the pivot chain (the physical rope path used for pay-out + the off-wall clamp).
    // Green cubes = the cached geodesic reachable-hold set (holds within the rope's along-surface reach
    // from the carbine — the set a hand may still step to). Capped for dense bakes (~21k holds).
    private void OnDrawGizmos()
    {
        if (!showRangeDebug || !Application.isPlaying || !IsTethered) return;

        Gizmos.color = Color.yellow;
        for (int i = 0; i < _pivots.Count - 1; i++)
            Gizmos.DrawLine(_pivots[i], _pivots[i + 1]);

        if (_reachable == null || _carbineSurface == null || !_carbineSurface.HoldsReady) return;
        var holds = _carbineSurface.Holds;
        Transform st = _carbineSurface.transform;
        Gizmos.color = new Color(0.2f, 1f, 0.4f);
        int drawn = 0, count = Mathf.Min(holds.Count, _reachable.Length);
        for (int i = 0; i < count; i++)
        {
            if (!_reachable[i]) continue;
            Gizmos.DrawCube(st.TransformPoint(holds[i].LocalPosition), Vector3.one * 0.05f);
            if (++drawn >= 8000) break;
        }
    }
#endif
}
