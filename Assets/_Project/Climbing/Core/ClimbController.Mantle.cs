using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — climb exits at the ends of a surface: reach-bottom (auto step-off near the
    /// ground) and the mantle / reach-top pipeline (top detection, landing probes, the scripted
    /// up-and-over move synced to the ClimbUp clip, and the post-mantle get-up fade).
    /// </summary>
    public partial class ClimbController
    {
        /// <summary>
        /// Reach-bottom: if solid ground is within <see cref="reachBottomDistance"/> below the body, step off
        /// (release with zero vertical velocity). Only once the grab has fully faded in, so it never fires
        /// during the grab transition. Returns true if it released this frame.
        /// </summary>
        private bool TryReachBottom()
        {
            if (!enableReachBottom || _mantling || _gettingUp) return false;
            if (_rig.MasterWeight < 0.99f) return false;   // not while the grab is still fading in
            Vector3 origin = transform.position + Vector3.up * 0.1f;
            if (!Physics.Raycast(origin, Vector3.down, reachBottomDistance + 0.1f,
                                 mantleSurfaceMask, QueryTriggerInteraction.Ignore))
                return false;
            BeginRelease();   // zeroes vertical velocity for a clean step-off
            if (logClimbEvents) Debug.Log("[ClimbController] Reach-bottom — stepping off near the ground.");
            return true;
        }

        /// <summary>
        /// Reach-top check: when the hand holds face far enough UP (we're at a near-horizontal top edge)
        /// AND there's clear space above + standing room on top, start the mantle. Automatic (brief §2.8).
        /// Returns true if the mantle took over this frame.
        /// </summary>
        private bool TryMantle()
        {
            if (!enableMantle || _mantling) return false;
            if (IsTrunk && !mantleOnTrunks) return false;   // trunks don't top-out (no standable tip)
            // Two ways to be "at a top": the holds tilt up (trunk tip) OR there's no reachable hold above
            // (a parsed vertical cliff's top holds face HORIZONTALLY, so the orientation test alone misses it).
            bool topByOrientation = Vector3.Dot(AvgOutward(), Vector3.up) >= mantleEnterDot;
            if (!topByOrientation && !AtSurfaceTopCached()) return false;
            if (!ComputeMantleLanding(out Vector3 landing, out Quaternion landRot)) return false;
            BeginMantle(landing, landRot);
            return true;
        }

        /// <summary>Cached <see cref="AtSurfaceTop"/> — the hands only change when a step starts or settles,
        /// so the O(all holds) scan runs on those transitions (see the dirty-marking in TickClimb) instead of
        /// every frame. Matters on authored cliffs with thousands of holds.</summary>
        private bool AtSurfaceTopCached()
        {
            if (_atTopDirty)
            {
                _atTopCache = AtSurfaceTop();
                _atTopDirty = false;
            }
            return _atTopCache;
        }

        /// <summary>
        /// True when no still-climbable hold sits above the hands — i.e. we've run out of up-holds, the
        /// surface-agnostic "reached the top" signal (works for flat-topped parsed cliffs whose top holds
        /// face horizontally). "Up" is the trunk axis on a trunk, world-up otherwise.
        /// </summary>
        private bool AtSurfaceTop()
        {
            if (_currentSurface == null) return false;
            var holds = _currentSurface.Holds;
            if (holds == null || holds.Count == 0) return false;

            Vector3 climbUp = IsTrunk ? TrunkUp() : Vector3.up;
            Vector3 hands = _rig.HandAverage;
            Transform st = _currentSurface.transform;
            float reachSqr = mantleReachAbove * mantleReachAbove;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 delta = st.TransformPoint(holds[i].LocalPosition) - hands;
                if (Vector3.Dot(delta, climbUp) < mantleHoldAboveMargin) continue;  // not above the hands
                if (delta.sqrMagnitude <= reachSqr) return false;                   // a reachable up-hold → keep climbing
            }
            return true;
        }

        /// <summary>
        /// Probes for a valid top-out: clear space above the hands, a near-horizontal surface to stand on
        /// just past the lip, and an unobstructed standing capsule there. All probes use <see cref="mantleSurfaceMask"/>.
        /// </summary>
        private bool ComputeMantleLanding(out Vector3 landing, out Quaternion landRot)
        {
            landing = Vector3.zero;
            landRot = transform.rotation;

            Vector3 hands = _rig.HandAverage;

            // The up-ray starts near/inside the player's own capsule, so disable it for the probes — a
            // self-hit (with a broad mantleSurfaceMask) would otherwise read as "blocked" and veto the mantle.
            CharacterController cc = _motor?.Controller;
            bool ccWasEnabled = cc != null && cc.enabled;
            if (ccWasEnabled) cc.enabled = false;
            try
            {
                // 1) Clear space above the lip — the up-ray must MISS (nothing to climb into).
                if (Physics.Raycast(hands, Vector3.up, mantleClearanceUp, mantleSurfaceMask, QueryTriggerInteraction.Ignore))
                    return false;

                // 2) Find the top surface just past the lip (horizontal "inward" = toward the platform).
                Vector3 inwardFlat = Vector3.ProjectOnPlane(-AvgOutward(), Vector3.up);
                inwardFlat = inwardFlat.sqrMagnitude > 1e-4f
                    ? inwardFlat.normalized
                    : Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                Vector3 probeXZ = hands + inwardFlat * mantleLandingForward;
                Vector3 probeTop = probeXZ + Vector3.up * mantleLandingProbeUp;
                if (!Physics.Raycast(probeTop, Vector3.down, out RaycastHit hit,
                                     mantleLandingProbeUp + mantleLandingProbeDown,
                                     mantleSurfaceMask, QueryTriggerInteraction.Ignore))
                    return false;
                // Reject a near-vertical "top" (we hit a wall, not a ledge surface to stand on).
                if (Vector3.Angle(hit.normal, Vector3.up) > 50f) return false;

                // 3) Standing room: an unobstructed capsule at the landing.
                float r = cc != null ? cc.radius : 0.3f;
                float h = cc != null ? Mathf.Max(cc.height, 2f * r) : 1.8f;
                Vector3 footPt = hit.point + Vector3.up * (r + 0.02f);
                Vector3 headPt = hit.point + Vector3.up * (h - r);
                if (Physics.CheckCapsule(footPt, headPt, r * 0.95f, mantleSurfaceMask, QueryTriggerInteraction.Ignore))
                    return false;

                landing = hit.point;                              // controller pivot sits at the feet
                landRot = Quaternion.LookRotation(inwardFlat, Vector3.up);
            }
            finally
            {
                if (ccWasEnabled) cc.enabled = true;
            }
            return true;
        }

        /// <summary>Starts the scripted top-out: capture start/landing, play ClimbUp, begin fading the IK out.</summary>
        private void BeginMantle(Vector3 landing, Quaternion landRot)
        {
            _mantling = true;
            _mantleTimer = 0f;
            _mantleStart = transform.position;
            _mantleTarget = landing;
            _mantleStartRot = transform.rotation;
            _mantleTargetRot = landRot;

            // Split start→landing into a vertical and a horizontal (forward) leg so each can be shaped
            // independently by its curve (classic up-then-over mantle, sync-able to the ClimbUp clip).
            Vector3 delta = _mantleTarget - _mantleStart;
            _mantleUpAxis = Vector3.up;
            _mantleUpDist = delta.y;
            Vector3 horiz = new Vector3(delta.x, 0f, delta.z);
            _mantleFwdDist = horiz.magnitude;
            _mantleFwdAxis = _mantleFwdDist > 1e-4f ? horiz / _mantleFwdDist : transform.forward;

            // Feet/legs stop driving; the ClimbUp clip owns the pose while we translate.
            _rig.SetEffectorWeight(ClimbEffector.LeftFoot, 0f);
            _rig.SetEffectorWeight(ClimbEffector.RightFoot, 0f);
            _lFootWeight = _rFootWeight = 0f;
            _lFootLocked = _rFootLocked = false;
            if (_animator != null && _climbLegsLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLegsLayerIndex, 0f);

            // The mantle branch early-returns before TickClimb's per-frame SetClimbState, so the
            // stamina system would keep the LAST state (possibly moving=true → phantom drain through
            // the whole mantle). Freeze it at not-moving for the scripted move.
            _stamina?.SetClimbState(true, false);

            // Fade the FBBIK effectors out over the move so the cosmetic ClimbUp clip reads through.
            _masterWeightTarget = 0f;
            if (_animator != null && _climbLayerIndex >= 0)
            {
                if (_animator.HasState(_climbLayerIndex, _hClimbUp))
                    _animator.CrossFade(_hClimbUp, ikFadeOutDuration, _climbLayerIndex, 0f);
                else
                    Debug.LogWarning("[ClimbController] No 'ClimbUp' state found on the 'ClimbingLayer' — " +
                        "the mantle clip won't play (the scripted move still runs). It must be named exactly " +
                        "'ClimbUp' and be a TOP-LEVEL state in the ClimbingLayer (not inside a sub-state-machine, " +
                        "not on another layer). No transitions are needed — CrossFade drives it directly.");
            }

            if (logClimbEvents) Debug.Log($"[ClimbController] Mantle start → landing {landing}.");
        }

        /// <summary>Scripted body move onto the ledge; finalized by OnMantleComplete (or the safety timeout).</summary>
        private void TickMantle(float dt)
        {
            _mantleTimer += dt;
            float t = mantleDuration > 0f ? Mathf.Clamp01(_mantleTimer / mantleDuration) : 1f;

            // Vertical and forward driven by SEPARATE curves so "rise first, then move over" is authorable
            // and the timing matches the ClimbUp clip (mantleDuration = clip length).
            float up = mantleUpCurve.Evaluate(t);
            float fwd = mantleForwardCurve.Evaluate(t);
            transform.position = _mantleStart + _mantleUpAxis * (_mantleUpDist * up)
                                              + _mantleFwdAxis * (_mantleFwdDist * fwd);
            transform.rotation = Quaternion.Slerp(_mantleStartRot, _mantleTargetRot, Mathf.SmoothStep(0f, 1f, t));

            if (_mantleTimer >= mantleSafetyTimeout) FinishMantle();
        }

        /// <summary>Animation event at the end of the ClimbUp clip — hands control back to the FSM, standing on top.</summary>
        public void OnMantleComplete()
        {
            if (_mantling) FinishMantle();
        }

        /// <summary>Completes the top-out: snap to the landing, zero vertical velocity, cool down re-grab, release control.</summary>
        private void FinishMantle()
        {
            transform.position = _mantleTarget;
            transform.rotation = _mantleTargetRot;
            _mantling = false;
            _motor?.SetVerticalVelocity(0f);
            _regrabCooldownTimer = regrabCooldown;

            // Park the BASE layer in grounded idle BEFORE the getup fade reveals it: a climb grabbed
            // mid-air froze the locomotion flags at their in-flight values (no motor ticks under
            // external control), so the base layer would still play the FALLING pose under the fading
            // climb layer until control returns and TickGrounded refreshes the bools.
            if (_animator != null)
            {
                _animator.SetBool(_hBaseGrounded, true);
                _animator.SetBool(_hBaseJump, false);
                _animator.SetBool(_hBaseFreeFall, false);
            }

            // Cross-fade the climb pose (ClimbUp ends crouched) out to the standing base idle instead of
            // dropping the layer instantly — control stays locked (_isClimbing true) until the fade ends.
            if (mantleGetupFade > 0f && _animator != null && _climbLayerIndex >= 0)
            {
                _gettingUp = true;
                _getupTimer = 0f;
                if (logClimbEvents) Debug.Log("[ClimbController] Mantle complete — fading climb pose out to stand.");
            }
            else
            {
                FinishRelease();   // isClimbing=false, layer weights 0, ReleaseExternalControl
                if (logClimbEvents) Debug.Log("[ClimbController] Mantle complete — standing on top.");
            }
        }

        /// <summary>
        /// Post-mantle stop-gap: fades the ClimbingLayer weight 1→0 so the crouched ClimbUp end pose blends
        /// into the standing base idle (no hard pop). Control is held (external control still active) until
        /// the fade completes, then control returns. Superseded later by a real ClimbUp→StandUp clip.
        /// </summary>
        private void TickGetup(float dt)
        {
            _getupTimer += dt;
            float t = mantleGetupFade > 0f ? Mathf.Clamp01(_getupTimer / mantleGetupFade) : 1f;
            if (_animator != null && _climbLayerIndex >= 0)
                _animator.SetLayerWeight(_climbLayerIndex, 1f - t);

            if (t >= 1f)
            {
                _gettingUp = false;
                FinishRelease();   // isClimbing=false, layer weights 0, ReleaseExternalControl
            }
        }

    }
}
