using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — body pose & dynamics: braced / free-hang orientation and position (per-step
    /// rotation tween, two-mass pendulum hang, torso standoff), spine twist + head look (FinalIK
    /// post-solve overrides), braced↔free pose switching, and the live grip / elbow-bend offsets.
    /// </summary>
    public partial class ClimbController
    {
        /// <summary>Average outward normal of the two hand holds (away from the surface, climber's side).</summary>
        private Vector3 AvgOutward()
        {
            Vector3 o = _rhOutward + _lhOutward;
            return o.sqrMagnitude > 1e-4f ? o.normalized : _rhOutward;
        }

        /// <summary>True while climbing a procedural surface (a Flora trunk), where holds carry a trunk-axis up.</summary>
        private bool IsTrunk =>
            _currentSurface != null && _currentSurface.Source == ClimbableSurface.ClimbHoldSource.Procedural;

        /// <summary>Average "up" of the two hand holds — the trunk axis toward the tip (used for trunk-aligned orientation).</summary>
        private Vector3 TrunkUp()
        {
            Vector3 u = _rhUp + _lhUp;
            return u.sqrMagnitude > 1e-4f ? u.normalized : Vector3.up;
        }

        /// <summary>
        /// Positions and orients the body each frame. BRACED: face the surface (yaw only) via the per-step
        /// rotation tween, and sit a standoff out from the surface below the hands. FREE HANG: yaw to the
        /// turn-driven facing and hang straight down from the hand-midpoint. The two are blended by
        /// <see cref="_bracedWeight"/>; rotation is snapped on grab (<paramref name="instant"/>).
        /// </summary>
        private void UpdateBodyPose(bool instant = false)
        {
            Vector3 avgOut = AvgOutward();

            // ---- Rotation ----
            // BRACED facing turns via a per-step TWEEN (set in StartBracedTurn on each hand step) from the
            // current rotation to the new target over a traverse-speed-tied duration — so the body rotates
            // smoothly ACROSS the hand move instead of snapping when avgOut jumps a ring-angle.
            if (instant)
            {
                _bracedBodyRot = ComputeBracedTarget();   // snap on grab
                _rotTweenT = 1f;
            }
            else if (_rotTweenT < 1f)
            {
                _rotTweenT = Mathf.Min(1f, _rotTweenT + Time.deltaTime / _rotTweenDur);
                _bracedBodyRot = Quaternion.Slerp(_rotTweenFrom, _rotTweenTo, Mathf.SmoothStep(0f, 1f, _rotTweenT));
            }
            // else: tween settled — hold _bracedBodyRot until the next hand step.

            // FREE-HANG target: yaw to the turn-driven facing, upright — the torso never pitches toward
            // the hands. Eased between the discrete per-hand-step facing changes so the turn reads smooth.
            Quaternion freeRot = _freeHangFacing.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(_freeHangFacing, Vector3.up)
                : _bracedBodyRot;
            _freeHangBodyRot = instant
                ? freeRot
                : Quaternion.Slerp(_freeHangBodyRot, freeRot, 1f - Mathf.Exp(-freeHangTurnSmooth * Time.deltaTime));

            transform.rotation = Quaternion.Slerp(_freeHangBodyRot, _bracedBodyRot, _bracedWeight);

            // BracedReady peek: yaw the WHOLE BODY toward the released side as a ROOT rotation (PRE-solve) so
            // FBBIK keeps the gripping hand ON its world-space hold — a post-solve bone turn drags it off.
            // Keep this angle modest (the arms must still reach the holds); the rest of the "look away from the
            // wall" comes from the head turn (ApplyBracedReadyTurn).
            if (_mode == ClimbMode.BracedReady && _readyT > 0.001f)
            {
                float sign = bracedReadyTurnInvert ? -_readySign : _readySign;
                transform.rotation = Quaternion.AngleAxis(bracedReadyTorsoAngle * _readyT * sign, transform.up) * transform.rotation;
            }

            // ---- Position ----
            // BRACED: rigid drop (restore-point) vs pendulum hang (lower mass swings below the moving hands).
            // The standoff offset uses the SMOOTHED facing's outward (from the rotation tween), NOT the raw
            // avgOut — avgOut jumps instantly when a hand grabs, which would pop the body ~rootForwardOffset·
            // sin(step angle) sideways around the trunk each step (a snap the camera follows, independent of
            // rotation/pendulum). Tying it to _bracedBodyRot makes the standoff follow the same smooth turn.
            Vector3 facingOut = -(_bracedBodyRot * Vector3.forward);
            Vector3 hangDir = (alignTorsoToTrunkAxis && IsTrunk) ? -TrunkUp() : Vector3.down;
            Vector3 rigidPos = _rig.HandAverage + facingOut * rootForwardOffset + hangDir * rootDownOffset;
            Vector3 bracedPos = usePendulum && _pendulum != null
                ? Vector3.Lerp(rigidPos, _pendulum.LowerPos + facingOut * rootForwardOffset, pendulumWeight)
                : rigidPos;

            // FREE-HANG: hang straight DOWN from the hand-midpoint — independent of rootForwardOffset /
            // rootDownOffset (body sits directly below the middle of the two hand holds). Sideways
            // liveliness comes from the spine lean (TwistSpine), not a positional sway.
            Vector3 freePos = _rig.HandAverage - Vector3.up * freeHangDrop;

            transform.position = Vector3.Lerp(freePos, bracedPos, _bracedWeight);

            // Torso standoff push — gated by `enableStandoff` and scaled by _bracedWeight (no push in free hang).
            ApplyStandoff(avgOut, instant);
        }

        /// <summary>Sets the pendulum's tuning + segment lengths so its rest matches the rigid body drop.</summary>
        private void ConfigurePendulum()
        {
            if (_pendulum == null) return;
            _pendulum.Stiffness = pendulumStiffness;
            _pendulum.Damping = pendulumDamping;
            _pendulum.AnchorToUpper = _pendulum.UpperToLower = rootDownOffset * 0.5f;  // rest = rigid drop

            // Hang along the TRUNK AXIS when braced on a bent trunk (so the body sits alongside a horizontal
            // limb instead of dropping straight down through it); blend back to world-down in free hang
            // (where real gravity should hang you below an overhang). Non-trunks: always world-down.
            Vector3 trunkDown = (alignTorsoToTrunkAxis && IsTrunk) ? -TrunkUp() : Vector3.down;
            _pendulum.GravityDir = Vector3.Slerp(Vector3.down, trunkDown, _bracedWeight);
        }

        /// <summary>Advances the body pendulum one step, anchored to the live hand-average.</summary>
        private void StepPendulum(float dt)
        {
            if (!usePendulum || _pendulum == null || _pendulumFrozen) return;   // frozen at rest in BracedReady
            ConfigurePendulum();                 // live-tunable
            _pendulum.SetAnchor(_rig.HandAverage);

            // Fixed-timestep sub-stepping so the swing is frame-rate independent + stable (better than a
            // literal FixedUpdate, which would quantize the visual). Safety cap avoids a spiral of death.
            float fixedStep = 1f / Mathf.Max(1f, pendulumStepHz);
            _pendulumAccumulator += dt;
            int safety = 0;
            while (_pendulumAccumulator >= fixedStep && safety++ < 8)
            {
                _pendulum.Step(fixedStep);
                _pendulumAccumulator -= fixedStep;
            }
        }

        /// <summary>
        /// Leans the chest/spine bone with the pendulum's upper-mass swing (article's mass2 → spine).
        /// Runs as FinalIK's POST-solve callback so it overrides the final pose. The upper segment's
        /// deviation from straight-down is applied to the spine as a world-space lean, scaled by weight
        /// and the climb fade. At rest the swing is identity (no change).
        /// </summary>
        private void TwistSpine()
        {
            if (!_isClimbing || !usePendulum || _pendulum == null || spineBone == null || _rig == null) return;
            float w = spineSwingWeight * _rig.MasterWeight;
            if (w <= 0.001f) return;

            // In FREE HANG the torso must not pitch toward the arms — keep only the LATERAL (sideways)
            // part of the swing and drop the fore/aft component along the body's forward axis. Braced
            // keeps the full swing. (_bracedWeight: 1 = braced/full, 0 = free/lateral-only.)
            // Measure the swing from the pendulum's REST (hang) direction, not world-down — on a bent trunk
            // the hang is along the trunk axis, so this keeps rest = identity (no bogus static spine lean).
            Vector3 restDir = _pendulum.GravityDir.sqrMagnitude > 1e-6f ? _pendulum.GravityDir.normalized : Vector3.down;
            Vector3 dir = _pendulum.UpperDir;
            Vector3 lateral = Vector3.ProjectOnPlane(dir, transform.forward);
            dir = lateral.sqrMagnitude > 1e-5f
                ? Vector3.Slerp(lateral.normalized, dir, _bracedWeight)
                : Vector3.Slerp(restDir, dir, _bracedWeight);   // pure fore/aft swing → rest (hang) dir in free hang

            Quaternion swing = Quaternion.FromToRotation(restDir, dir);

            if (spineBoneLower != null)
            {
                // Spread the lean across two joints for a smoother bend. Apply the lower bone FIRST — the
                // chest inherits it, so the two partial leans compose to ~the full swing, distributed.
                spineBoneLower.rotation = Quaternion.Slerp(Quaternion.identity, swing, w * spineLowerShare) * spineBoneLower.rotation;
                spineBone.rotation = Quaternion.Slerp(Quaternion.identity, swing, w * (1f - spineLowerShare)) * spineBone.rotation;
            }
            else
            {
                spineBone.rotation = Quaternion.Slerp(Quaternion.identity, swing, w) * spineBone.rotation;
            }
        }

        /// <summary>
        /// Turns the head toward a look point, applied AFTER the solve + spine twist (final override). While
        /// a hand is reaching, the point sits between the hand-midpoint and the LEAD (moving) hand, so the
        /// character glances toward the hold it's going for; when idle it sits between the hand-midpoint and
        /// the head's own default (animation) forward, so the head eases back to its neutral pose. Uses the
        /// head's CURRENT forward (no rig-axis guess beyond <see cref="headForwardAxis"/>) so the turn is a
        /// minimal rotation, scaled by weight × climb fade.
        /// </summary>
        private void HeadLook()
        {
            if (!_isClimbing || headBone == null || _rig == null) return;
            // Fade the look-at out as the BracedReady peek turns in (ApplyBracedReadyTurn owns the head then).
            float readyFade = _mode == ClimbMode.BracedReady ? (1f - _readyT) : 1f;
            float w = headLookWeight * _rig.MasterWeight * readyFade;
            if (w <= 0.001f) return;

            Vector3 headPos = headBone.position;
            Vector3 forward = headBone.rotation * (headForwardAxis.sqrMagnitude > 1e-6f ? headForwardAxis.normalized : Vector3.forward);
            Vector3 handMid = _rig.HandAverage;

            bool rMoving = _rig.IsMoving(ClimbEffector.RightHand);
            bool lMoving = _rig.IsMoving(ClimbEffector.LeftHand);
            Vector3 desiredPoint;
            if (rMoving || lMoving)
            {
                Vector3 lead = _rig.GetCurrentPosition(rMoving ? ClimbEffector.RightHand : ClimbEffector.LeftHand);
                desiredPoint = headLookTarget == HeadLookTarget.MovingHand
                    ? lead                                                  // look straight at the moving hand
                    : Vector3.Lerp(handMid, lead, headLookLeadBias);        // between mid and the reaching hand
            }
            else
            {
                desiredPoint = headLookTarget == HeadLookTarget.MovingHand
                    ? headPos + forward                                     // animation default (neutral)
                    : Vector3.Lerp(handMid, headPos + forward, headLookAheadBias);   // between mid and the default forward
            }

            // Ease the look POINT toward its target — smooths the jump when the lead hand switches or the
            // head returns to neutral, instead of snapping the gaze.
            if (!_headLookInit) { _headLookPoint = desiredPoint; _headLookInit = true; }
            else _headLookPoint = Vector3.Lerp(_headLookPoint, desiredPoint, 1f - Mathf.Exp(-headLookSmooth * Time.deltaTime));

            Vector3 desired = _headLookPoint - headPos;
            if (desired.sqrMagnitude < 1e-6f) return;
            Quaternion delta = Quaternion.FromToRotation(forward, desired.normalized);
            headBone.rotation = Quaternion.Slerp(Quaternion.identity, delta, w) * headBone.rotation;
        }

        /// <summary>
        /// Forward-probes the surface from the torso and pushes the whole body OUT along the normal when
        /// the trunk is closer than <see cref="desiredStandoff"/> (or the torso is already inside it) —
        /// so the body never clips a bulging/irregular surface. Hands and feet are world-pinned IK
        /// effectors, so they stay on their holds while the torso clears; the push is clamped and eased.
        /// The cast origin is backed out along the normal so it starts OUTSIDE the geometry even when the
        /// torso is penetrating.
        /// </summary>
        private void ApplyStandoff(Vector3 bodyNormal, bool instant)
        {
            float push = 0f;
            if (enableStandoff)
            {
                float chestPush = ProbePush(bodyNormal, chestProbeHeight, chestStandoff);
                float hipPush = ProbePush(bodyNormal, hipProbeHeight, hipStandoff);
                // Pure translation can only satisfy one distance — honour whichever needs the most
                // clearance so neither the chest nor the hips clip. (Holding DIFFERENT chest/hip gaps at
                // once needs the lean back on; the two probes can later drive that tilt from their delta.)
                push = Mathf.Max(chestPush, hipPush);
            }
            push *= _bracedWeight;   // no torso standoff in free hang

            float t = instant ? 1f : 1f - Mathf.Exp(-standoffSpeed * Time.deltaTime);
            _standoffPush = Mathf.Lerp(_standoffPush, push, t);
            transform.position += bodyNormal * _standoffPush;
        }

        /// <summary>
        /// Forward SphereCast at one torso height; returns the outward push needed to hold its standoff
        /// (0 if already clear). Origin is backed out along the normal so it starts outside the geometry
        /// even when that point is penetrating.
        /// </summary>
        private float ProbePush(Vector3 bodyNormal, float height, float standoff)
        {
            Vector3 p = transform.position + Vector3.up * height;
            Vector3 origin = p + bodyNormal * standoffBackup;
            if (Physics.SphereCast(origin, standoffRadius, -bodyNormal, out RaycastHit hit,
                                   standoffBackup + maxStandoffPush, climbableLayers, QueryTriggerInteraction.Ignore))
            {
                float surfaceDist = hit.distance - standoffBackup;   // point → surface (negative = penetrating)
                return Mathf.Clamp(standoff - surfaceDist, 0f, maxStandoffPush);
            }
            return 0f;
        }

        /// <summary>
        /// The braced facing rotation, sampled once per hand step (the tween target). Normally upright
        /// (yaw only) facing the flattened into-surface direction. On a TRUNK (when alignTorsoToTrunkAxis),
        /// the torso's vertical is aligned to the trunk axis instead, so "up" heads toward the tip however
        /// the trunk bends.
        /// </summary>
        private Quaternion ComputeBracedTarget()
        {
            Vector3 avgOut = AvgOutward();

            if (alignTorsoToTrunkAxis && IsTrunk)
            {
                Vector3 up = TrunkUp();
                Vector3 into = Vector3.ProjectOnPlane(-avgOut, up);   // face the trunk, around its axis
                if (into.sqrMagnitude > 1e-4f)
                    return Quaternion.LookRotation(into.normalized, up);
            }

            Vector3 intoFlat = Vector3.ProjectOnPlane(-avgOut, Vector3.up);
            return intoFlat.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(intoFlat.normalized, Vector3.up)
                : _bracedBodyRot;
        }

        /// <summary>
        /// Starts a braced body-rotation tween from the current facing to the new target, over a duration
        /// tied to the hand-move time (traverseMoveDuration × bodyTurnDurationScale). Called on each
        /// successful braced hand step, so the body turns smoothly across the move and slower traversal
        /// produces gentler turns. Chains naturally if a step starts before the previous tween finishes.
        /// </summary>
        private void StartBracedTurn()
        {
            _rotTweenFrom = _bracedBodyRot;
            _rotTweenTo = ComputeBracedTarget();
            _rotTweenDur = Mathf.Max(0.0001f, traverseMoveDuration * bodyTurnDurationScale);
            _rotTweenT = 0f;
        }

        /// <summary>
        /// Picks the braced vs free-hang pose from surface orientation. A single scalar —
        /// Dot(outwardNormal, up) — captures it: ≈0 = vertical wall (braced), strongly negative =
        /// overhang above you / chest faces up (free hang). Hysteresis (enter vs exit thresholds)
        /// stops braced↔free flicker at the boundary; the cross-fade smooths the switch so the body
        /// doesn't pop. (Strongly POSITIVE = lying on a near-flat top = the future mantle zone — left
        /// braced for now until mantle exists.)
        /// </summary>
        private void UpdatePoseSwitch()
        {
            float d = Vector3.Dot(AvgOutward(), Vector3.up);
            if (!_freeHang && d < freeHangEnterDot) PlayPose(true, instant: false);
            else if (_freeHang && d > freeHangExitDot) PlayPose(false, instant: false);
        }

        /// <summary>Switches the ClimbingLayer to the braced or free-hang state (snap on entry, cross-fade otherwise).</summary>
        private void PlayPose(bool free, bool instant)
        {
            _freeHang = free;
            if (free)
            {
                _lFootLocked = _rFootLocked = false;   // drop foot locks when entering free hang
                // Seed the free-hang facing from the current body yaw so the brace→free switch doesn't snap.
                Vector3 fwd = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                if (fwd.sqrMagnitude > 1e-4f) _freeHangFacing = fwd.normalized;
            }
            if (_animator == null) return;

            // The animator plays the braced↔hang TRANSITION clips off this bool. Code only toggles intent.
            _animator.SetBool(_hIsFreeHang, free);
            if (instant)
            {
                _bracedWeight = free ? 0f : 1f;
                if (_climbLayerIndex >= 0)
                    _animator.Play(free ? _hFreeHang : _hClimbHang, _climbLayerIndex, 0f);
            }
        }

        /// <summary>Pushes the live-tunable per-hand grip offsets onto the hand effectors (applied at write time).</summary>
        private void ApplyGripOffset()
        {
            _rig.SetRotationOffset(ClimbEffector.LeftHand, Quaternion.Euler(leftHandGripRotation));
            _rig.SetRotationOffset(ClimbEffector.RightHand, Quaternion.Euler(rightHandGripRotation));
            _rig.SetRotationOffset(ClimbEffector.LeftFoot, Quaternion.Euler(footGripRotation));
            _rig.SetRotationOffset(ClimbEffector.RightFoot, Quaternion.Euler(Vector3.Scale(footGripRotation, footGripMirror)));

            _rig.SetPositionOffset(ClimbEffector.LeftHand, handHoldOffset);    // wrist→fingers shift at the hold
            _rig.SetPositionOffset(ClimbEffector.RightHand, handHoldOffset);
        }

        /// <summary>
        /// Forces each arm's elbow to bend toward an explicit down/out direction via FBBIK bend
        /// constraints, INDEPENDENT of hand rotation. Without this FinalIK derives the elbow bend
        /// from the hand effector rotation (IKConstraintBend.GetDir), so the 180° grip that faces the
        /// palm flips the elbow — and since the arms are mirror images, it flips the right but not the
        /// left. Weight 1 overrides that, so both palms AND both elbows come out correct.
        /// </summary>
        private void SetArmBendDirections(float weight)
        {
            if (ik == null || ik.solver == null || !ik.solver.initiated) return;
            var solver = ik.solver;

            Vector3 bodyRight = transform.right;
            Vector3 leftDir = (Vector3.down - bodyRight * elbowOutward).normalized;   // left elbow: down + left
            Vector3 rightDir = (Vector3.down + bodyRight * elbowOutward).normalized;  // right elbow: down + right

            // Fatigue tremble: a slight horizontal jitter added to each elbow bend as stamina nears empty.
            leftDir = fatigueJitter.Perturb(leftDir, bodyRight, _jitterStrength, 0f).normalized;
            rightDir = fatigueJitter.Perturb(rightDir, bodyRight, _jitterStrength, Mathf.PI).normalized;

            var lc = solver.leftArmChain.bendConstraint;
            lc.bendGoal = null; lc.direction = leftDir; lc.weight = weight;
            var rc = solver.rightArmChain.bendConstraint;
            rc.bendGoal = null; rc.direction = rightDir; rc.weight = weight;
        }

    }
}
