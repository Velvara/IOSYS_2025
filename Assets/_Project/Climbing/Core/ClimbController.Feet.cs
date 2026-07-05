using UnityEngine;
using RootMotion.FinalIK;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — feet: the animated-legs path (masked ClimbLegsLayer blend + foot-smear IK
    /// with world-space foot locking, FinalIK pre-solve) and the procedural hold-stepping fallback,
    /// plus the knee bend-direction constraints.
    /// </summary>
    public partial class ClimbController
    {
        /// <summary>
        /// Reference hip point the feet anchor from: a fixed drop below the hand-average for now.
        /// The two-mass pendulum will repoint this at its lower mass later (single seam, one line).
        /// </summary>
        private Vector3 HipPosition =>
            _rig.HandAverage + AvgOutward() * hipForwardOffset - Vector3.up * hipDropFromHands;

        /// <summary>
        /// Plants or dangles each foot. In free-hang orientation both feet dangle (IK off, pose
        /// shows the dangle). Otherwise each foot probes its OWN anchor (down + to its side of the
        /// hip), SphereCasts into the surface, and — if it hits within leg reach — plants there;
        /// a miss or an over-reach leaves that foot free. One foot steps at a time (the other foot
        /// and the same-side hand must be settled), so there are always 3+ contact points.
        /// </summary>
        private void UpdateFeet(float dt)
        {
            if (_footCooldown > 0f) _footCooldown -= dt;

            if (useAnimatedLegs)
            {
                // Legs come from the masked climb clip; feet are corrected to the surface in SmearFeet
                // (FinalIK pre-solve). Zero the procedural foot effectors so EffectorRig doesn't fight it.
                _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
                _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
                _lFootWeight = _rFootWeight = 0f;
                UpdateLegBlend(dt);
                return;
            }

            if (_freeHang)
            {
                FadeFootWeight(ClimbEffector.LeftFoot, 0f, dt);
                FadeFootWeight(ClimbEffector.RightFoot, 0f, dt);
                return;
            }

            UpdateFoot(ClimbEffector.LeftFoot, ClimbEffector.LeftHand, -1f, dt);
            UpdateFoot(ClimbEffector.RightFoot, ClimbEffector.RightHand, +1f, dt);
        }

        /// <summary>
        /// Drives the lower-body climb blend: ClimbMoveX/Y follow the movement direction (eased), and the
        /// ClimbLegsLayer weight tracks the climb fade. The 2D blend's centre (0,0) should be the idle
        /// braced-legs pose; +Y up, −Y down, ±X traverse.
        /// </summary>
        private void UpdateLegBlend(float dt)
        {
            if (_animator == null) return;

            Vector2 mv = _input != null ? _input.MoveInput : Vector2.zero;
            if (mv.sqrMagnitude < minMoveInput * minMoveInput) mv = Vector2.zero;

            // Legs animate only while the character is actually advancing between holds — not when input is
            // held but traversal is stuck (no reachable hold). A hand step tops up a short hold timer so the
            // legs keep moving through the gaps between steps; when stepping stops the legs FREEZE where they
            // were (the blend param holds + ClimbLegsSpeed → 0), instead of returning to the idle pose.
            if (_rig.AnyMoving) _legMoveTimer = climbLegStopDelay;
            else _legMoveTimer = Mathf.Max(0f, _legMoveTimer - dt);
            bool legsMoving = _legMoveTimer > 0f;

            if (legsMoving)
                _legBlend = Vector2.MoveTowards(_legBlend, mv, climbMoveSmooth * dt);
            // else: hold _legBlend at its last value — legs stay in their stride pose.

            _animator.SetFloat(_hClimbMoveX, _legBlend.x);
            _animator.SetFloat(_hClimbMoveY, _legBlend.y);
            // Pause the leg clip when frozen so looping stride clips don't keep cycling in place. Hook this
            // float to the leg blend-tree state's Speed Multiplier in the animator (no-op if not wired).
            _animator.SetFloat(_hClimbLegsSpeed, legsMoving ? 1f : 0f);
            if (_climbLegsLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLegsLayerIndex, _rig.MasterWeight * _bracedWeight);
        }

        /// <summary>
        /// Foot-smear IK (runs as the FinalIK pre-solve callback). For each foot it reads the animator's
        /// posed foot position, casts to the surface, and pins the effector there — with a weight derived
        /// from how close the animated foot is to the surface, so a clip-lifted foot (swing) follows the
        /// animation while a clip-planted foot snaps to the real geometry. No-op unless climbing with
        /// animated legs.
        /// </summary>
        private void SmearFeet()
        {
            if (!_isClimbing || !useAnimatedLegs || _rig == null || ik == null || ik.solver == null) return;
            SmearFoot(ik.solver.leftFootEffector, -1f, ref _lFootLocked, ref _lFootLockPos, ref _lFootLockRot);
            SmearFoot(ik.solver.rightFootEffector, +1f, ref _rFootLocked, ref _rFootLockPos, ref _rFootLockRot);
        }

        private void SmearFoot(IKEffector eff, float sideSign, ref bool locked, ref Vector3 lockPos, ref Quaternion lockRot)
        {
            if (eff == null || eff.bone == null) return;
            int idx = sideSign < 0f ? 0 : 1;

            Vector3 outward = AvgOutward();
            Vector3 footPos = eff.bone.position;                 // animator-posed foot (pre-solve)
            float w = _rig.MasterWeight * footIKWeight * _bracedWeight;   // foot IK fades out in free hang

            // Curvature-aware inward direction: aim the cast at the trunk-axis estimate (a point
            // trunkAxisDepth behind the hand surface), so a foot splayed around the curve still casts
            // INTO the trunk instead of shooting past it along the body's fixed radial.
            Vector3 axis = _rig.HandAverage - outward * trunkAxisDepth;
            axis.y = footPos.y;
            Vector3 toAxis = axis - footPos;
            Vector3 inward = toAxis.sqrMagnitude > 1e-4f ? toAxis.normalized : -outward;
            Vector3 origin = footPos - inward * footSmearBackup;

            bool hasHit = Physics.SphereCast(origin, footSmearRadius, inward, out RaycastHit hit,
                                             footSmearBackup + footSmearMaxDist, climbableLayers, QueryTriggerInteraction.Ignore);
            float contactDist = hasHit ? hit.distance - footSmearBackup : float.MaxValue;
            float stance = 1f - Mathf.Clamp01(Mathf.InverseLerp(footContactNear, footContactFar, contactDist));

            // Pick this frame's target pose + weights: hold the lock, take a fresh plant, or follow the clip.
            Vector3 targetPos;
            Quaternion targetRot;
            float posW, rotW;

            bool holdingLock = enableFootLock && locked && stance >= footLockExit &&
                               (eff.bone.position - lockPos).sqrMagnitude < footLockBreak * footLockBreak;
            if (holdingLock)
            {
                targetPos = lockPos;
                targetRot = lockRot;
                posW = w;
                rotW = w * footSmearRotWeight;
            }
            else
            {
                locked = false;   // foot lifted (clip) or body climbed past → re-plant
                if (hasHit)
                {
                    targetPos = hit.point + hit.normal * footSmearSurfaceOffset;
                    targetRot = PlantRotation(hit.normal, sideSign, eff.bone.rotation);
                    posW = w * stance;
                    rotW = w * stance * footSmearRotWeight;
                    if (enableFootLock && stance > footLockEnter) { locked = true; lockPos = targetPos; lockRot = targetRot; }
                }
                else
                {
                    targetPos = _footSmoothPos[idx];   // hold last (weight 0 → no visible effect / no swoop)
                    targetRot = _footSmoothRot[idx];
                    posW = 0f;
                    rotW = 0f;
                }
            }

            // Ease the IK target so plant / lift / lock-break / re-plant blend instead of snapping.
            if (!_footSmoothInit[idx]) { _footSmoothPos[idx] = targetPos; _footSmoothRot[idx] = targetRot; _footSmoothInit[idx] = true; }
            float s = footSmoothSpeed > 0f ? 1f - Mathf.Exp(-footSmoothSpeed * Time.deltaTime) : 1f;
            _footSmoothPos[idx] = Vector3.Lerp(_footSmoothPos[idx], targetPos, s);
            _footSmoothRot[idx] = Quaternion.Slerp(_footSmoothRot[idx], targetRot, s);

            eff.position = _footSmoothPos[idx];
            eff.rotation = _footSmoothRot[idx];
            eff.positionWeight = posW;
            eff.rotationWeight = rotW;
        }

        /// <summary>
        /// Character-relative plant rotation: sole on the surface (up = normal), toes up + out to the
        /// foot's own side in character space, + the rig-convention euler offset (mirrored for the right foot).
        /// </summary>
        private Quaternion PlantRotation(Vector3 normal, float sideSign, Quaternion fallback)
        {
            Vector3 toe = transform.up + transform.right * (sideSign * footToeSide);
            toe = Vector3.ProjectOnPlane(toe, normal);
            Quaternion rot = toe.sqrMagnitude > 1e-5f ? Quaternion.LookRotation(toe.normalized, normal) : fallback;
            Vector3 off = sideSign < 0f ? footPlantRotation : Vector3.Scale(footPlantRotation, footPlantMirror);
            return rot * Quaternion.Euler(off);
        }

        private void UpdateFoot(ClimbEffector foot, ClimbEffector sameSideHand, float sideSign, float dt)
        {
            Vector3 hip = HipPosition;
            Vector3 avgOut = AvgOutward();
            Vector3 handAvg = _rig.HandAverage;
            Vector3 bodyRight = transform.right;
            Vector3 desired = hip - Vector3.up * footDrop + bodyRight * (sideSign * footSide);
            ClimbEffector other = foot == ClimbEffector.LeftFoot ? ClimbEffector.RightFoot : ClimbEffector.LeftFoot;

            // STICKINESS: keep the current foot-hold unless the body has drifted far from it. Re-picking
            // nearest-to-desired every frame made feet flip-flop between two near-equal holds (worsened by
            // the body-rotation feedback loop); staying put unless there's a real reason removes the jitter.
            int curIdx = FootHoldIndex(foot);
            bool curValid = curIdx >= 0 && FootHoldValid(curIdx, hip, handAvg, avgOut);
            if (curValid && (HoldWorldPos(curIdx) - desired).sqrMagnitude <= footStickRadius * footStickRadius)
            {
                FadeFootWeight(foot, 1f, dt);   // happy where it is — no search, no step
                return;
            }

            // Want to move (dangling, drifted, or current hold invalid). Find the best hold for `desired`.
            bool found = FindFootHold(desired, hip, bodyRight, sideSign, handAvg, avgOut, other,
                                      out int idx, out Vector3 hp, out Quaternion hr);
            if (found)
            {
                // One foot at a time: step only when settled and the same-side hand / other foot aren't moving.
                bool canStep = _footCooldown <= 0f && !_rig.IsMoving(foot)
                               && !_rig.IsMoving(other) && !_rig.IsMoving(sameSideHand);
                if (canStep)
                {
                    _rig.SetPoseTarget(foot, hp, hr, footMoveDuration);   // interpolate — no harsh snap
                    SetFootHoldIndex(foot, idx);
                    _footCooldown = footStepInterval;
                    FadeFootWeight(foot, 1f, dt);
                }
                else
                {
                    // Gate closed (another limb mid-move): stay weighted if we have a usable hold, else wait.
                    FadeFootWeight(foot, (curValid || _rig.IsMoving(foot)) ? 1f : 0f, dt);
                }
            }
            else if (curValid)
            {
                FadeFootWeight(foot, 1f, dt);   // no better hold found — keep the (drifted) current one
            }
            else
            {
                // Truly nothing reachable (gap / overhang) → dangle under the hip, IK off.
                SetFootHoldIndex(foot, -1);
                if (!_rig.IsMoving(foot)) _rig.SnapToPose(foot, desired, transform.rotation);
                FadeFootWeight(foot, 0f, dt);
            }
        }

        private int FootHoldIndex(ClimbEffector foot) =>
            foot == ClimbEffector.LeftFoot ? _lFootHoldIdx : _rFootHoldIdx;

        private void SetFootHoldIndex(ClimbEffector foot, int idx)
        {
            if (foot == ClimbEffector.LeftFoot) _lFootHoldIdx = idx; else _rFootHoldIdx = idx;
        }

        private Vector3 HoldWorldPos(int idx) =>
            _currentSurface.transform.TransformPoint(_currentSurface.Holds[idx].LocalPosition);

        private Quaternion HoldWorldRot(int idx) =>
            _currentSurface.transform.rotation * _currentSurface.Holds[idx].LocalRotation;

        /// <summary>A foot's current hold is still usable: in leg reach, below the hands, and on the same face.</summary>
        private bool FootHoldValid(int idx, Vector3 hip, Vector3 handAvg, Vector3 avgOut)
        {
            if (_currentSurface == null || !_currentSurface.HoldsReady || idx >= _currentSurface.Holds.Count)
                return false;
            Vector3 wp = HoldWorldPos(idx);
            if ((wp - hip).sqrMagnitude > legReach * legReach) return false;
            if (Vector3.Dot(wp - handAvg, Vector3.up) > -footBelowHands) return false;
            if (Vector3.Dot(HoldWorldRot(idx) * Vector3.forward, avgOut) < facingCoherence) return false;
            return true;
        }

        /// <summary>
        /// Best foot-hold (by index) nearest the desired plant point: within leg reach of the hip, below
        /// the hands, on the foot's own side (anti-cross), clear of both hands and the other foot, same face.
        /// </summary>
        private bool FindFootHold(Vector3 desired, Vector3 hip, Vector3 bodyRight, float sideSign,
                                  Vector3 handAvg, Vector3 avgOut, ClimbEffector other,
                                  out int index, out Vector3 pos, out Quaternion rot)
        {
            index = -1;
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var s = _currentSurface;
            if (s == null || !s.HoldsReady) return false;

            Vector3 lh = _rig.GetCurrentPosition(ClimbEffector.LeftHand);
            Vector3 rh = _rig.GetCurrentPosition(ClimbEffector.RightHand);
            Vector3 of = _rig.GetCurrentPosition(other);

            Transform st = s.transform;
            var holds = s.Holds;
            float legSqr = legReach * legReach;
            float clearSqr = footHoldClearance * footHoldClearance;
            float best = float.MaxValue;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);

                if ((wp - hip).sqrMagnitude > legSqr) continue;                               // within leg reach
                if (Vector3.Dot(wp - handAvg, Vector3.up) > -footBelowHands) continue;         // below the hands
                if (Vector3.Dot(wp - hip, bodyRight) * sideSign < -footCrossMargin) continue;  // own side (anti-cross)
                if ((wp - lh).sqrMagnitude < clearSqr) continue;                              // clear of hands + other foot
                if ((wp - rh).sqrMagnitude < clearSqr) continue;
                if ((wp - of).sqrMagnitude < clearSqr) continue;

                Quaternion wr = st.rotation * holds[i].LocalRotation;
                if (Vector3.Dot(wr * Vector3.forward, avgOut) < facingCoherence) continue;     // same face

                float d = (wp - desired).sqrMagnitude;
                if (d < best) { best = d; index = i; pos = wp; rot = wr; }
            }
            return index >= 0;
        }

        private float FootWeight(ClimbEffector foot) =>
            foot == ClimbEffector.LeftFoot ? _lFootWeight : _rFootWeight;

        private void FadeFootWeight(ClimbEffector foot, float target, float dt)
        {
            float w = Mathf.MoveTowards(FootWeight(foot), target, footWeightFadeSpeed * dt);
            if (foot == ClimbEffector.LeftFoot) _lFootWeight = w; else _rFootWeight = w;
            _rig.SetEffectorWeight(foot, w);
        }

        /// <summary>
        /// Forces each knee toward an explicit away-from-wall / out bend via FBBIK leg bend
        /// constraints — the same mirror-image fix the elbows need (the legs are reflections, so a
        /// shared foot rotation would flip one knee). Weight scales with how planted the feet are.
        /// </summary>
        private void SetLegBendDirections(float weight)
        {
            if (ik == null || ik.solver == null || !ik.solver.initiated) return;
            var solver = ik.solver;

            Vector3 bodyRight = transform.right;
            Vector3 awayFromWall = -transform.forward;                                  // body faces into the wall
            Vector3 leftDir = (awayFromWall - bodyRight * kneeOutward).normalized;      // left knee: out-from-wall + left
            Vector3 rightDir = (awayFromWall + bodyRight * kneeOutward).normalized;     // right knee: out-from-wall + right

            var lc = solver.leftLegChain.bendConstraint;
            lc.bendGoal = null; lc.direction = leftDir; lc.weight = weight;
            var rc = solver.rightLegChain.bendConstraint;
            rc.bendGoal = null; rc.direction = rightDir; rc.weight = weight;
        }

    }
}
