using UnityEngine;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — traversal & hold queries: camera-relative braced hand-over-hand stepping,
    /// free-hang brachiation (turn via arc-steps, then leapfrog), and the linear hold searches
    /// (reach band / anti-cross / progress / same-face filters). The planned spatial grid /
    /// HoldStreamer replaces the linear scans here.
    /// </summary>
    public partial class ClimbController
    {
        /// <summary>
        /// Move-input traversal: when no hand is mid-move, step the trailing hand toward a hold in the
        /// input direction (in the LOCAL surface plane); the body follows via UpdateBodyPose. Surface-
        /// aware (follows curvature); no feet / no sway yet.
        /// </summary>
        private void HandleTraversal(float dt)
        {
            if (_moveCooldown > 0f) _moveCooldown -= dt;
            if (_input == null || _rig.AnyMoving || _moveCooldown > 0f) return;

            Vector2 mv = _input.MoveInput;
            if (mv.sqrMagnitude < minMoveInput * minMoveInput) return;

            // Free hang = brachiation: turn to face the input first, then traverse (separate model).
            if (_freeHang) { HandleFreeHangTraversal(mv); return; }

            // Camera-relative input mapped onto the surface tangent plane: x = screen-right projected
            // onto the surface, y = up the surface. Falls back gracefully on near-horizontal surfaces.
            Vector3 avgOut = AvgOutward();
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;

            // On a trunk, "up" follows the trunk axis (toward the tip) rather than world up.
            Vector3 upRef = (alignTorsoToTrunkAxis && IsTrunk) ? TrunkUp() : Vector3.up;
            Vector3 camRight = _cam != null ? _cam.right : transform.right;
            Vector3 xDir = Vector3.ProjectOnPlane(camRight, avgOut);
            Vector3 yDir = Vector3.ProjectOnPlane(upRef, avgOut);
            if (xDir.sqrMagnitude < 1e-4f) xDir = transform.right;
            if (yDir.sqrMagnitude < 1e-4f) yDir = Vector3.Cross(avgOut, xDir);
            Vector3 traverseDir = xDir.normalized * mv.x + yDir.normalized * mv.y;
            if (traverseDir.sqrMagnitude < 1e-4f) return;
            traverseDir.Normalize();

            Vector3 rhPos = _rig.GetCurrentPosition(ClimbEffector.RightHand);
            Vector3 lhPos = _rig.GetCurrentPosition(ClimbEffector.LeftHand);

            // Try the TRAILING hand first (less advanced along traverseDir, measured RELATIVE to the
            // other hand). If it's blocked — anti-cross, caught up, or no reachable hold — try the
            // OTHER hand instead. That turns the deadlock (trailing hand caught up to the lead but
            // can't cross, so the lead never gets its turn) into a natural shuffle gait.
            bool primaryRight = Vector3.Dot(rhPos - lhPos, traverseDir) <= 0f;
            if (TryStepHand(primaryRight, rhPos, lhPos, traverseDir, avgOut) ||
                TryStepHand(!primaryRight, rhPos, lhPos, traverseDir, avgOut))
                StartBracedTurn();   // begin the per-step rotation tween toward the new facing
            else
                _moveCooldown = traverseRetryInterval;   // stuck — don't rescan every hold every frame
        }

        /// <summary>
        /// Attempts to step one hand to a new hold for the current input direction. Handles the
        /// close-the-gap (over-extended) case. Returns true if a hold was found and the move started.
        /// </summary>
        private bool TryStepHand(bool moveRight, Vector3 rhPos, Vector3 lhPos, Vector3 traverseDir, Vector3 avgOut)
        {
            Vector3 fromPos = moveRight ? rhPos : lhPos;
            Vector3 otherPos = moveRight ? lhPos : rhPos;
            Vector3 bodyRight = transform.right;
            float sideSign = moveRight ? 1f : -1f;

            // Over-extended → close the gap toward the other hand (drop the forward-progress
            // requirement); otherwise leapfrog forward by traverseStep.
            float separation = Vector3.Distance(rhPos, lhPos);
            bool closeGap = separation > maxHandSeparation;
            Vector3 ideal = closeGap ? otherPos : fromPos + traverseDir * traverseStep;
            float minProgress = closeGap ? -1f : progressDot;

            if (!FindReachableHold(_currentSurface, ideal, fromPos, otherPos, traverseDir, avgOut,
                                   bodyRight, sideSign, minProgress, out Vector3 tp, out Quaternion tr))
                return false;

            ClimbEffector hand = moveRight ? ClimbEffector.RightHand : ClimbEffector.LeftHand;
            _rig.SetPoseTarget(hand, tp, tr, traverseMoveDuration);
            if (moveRight) { _rhOutward = tr * Vector3.forward; _rhUp = tr * Vector3.up; }
            else { _lhOutward = tr * Vector3.forward; _lhUp = tr * Vector3.up; }
            _moveCooldown = moveInterval;
            return true;
        }

        /// <summary>
        /// Free-hang traversal = brachiation. The body first TURNS to face the (camera-relative) input
        /// direction, then travels. The turn is realised through hand-steps — one hand pivots on the
        /// other and swings around an arc, rotating the hand pair (and the body facing) a little each
        /// step — so a full re-face takes several holds. No forward progress until the body faces within
        /// freeHangMoveAngle of the input; then the hands leapfrog forward in the facing direction.
        /// </summary>
        private void HandleFreeHangTraversal(Vector2 mv)
        {
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;
            Vector3 camF = Vector3.ProjectOnPlane(_cam != null ? _cam.forward : transform.forward, Vector3.up);
            Vector3 camR = Vector3.ProjectOnPlane(_cam != null ? _cam.right : transform.right, Vector3.up);
            if (camF.sqrMagnitude < 1e-4f) camF = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
            if (camR.sqrMagnitude < 1e-4f) camR = Vector3.ProjectOnPlane(transform.right, Vector3.up);
            if (camF.sqrMagnitude < 1e-4f || camR.sqrMagnitude < 1e-4f) return;

            Vector3 wish = camR.normalized * mv.x + camF.normalized * mv.y;
            if (wish.sqrMagnitude < 1e-4f) return;
            wish.Normalize();

            if (_freeHangFacing.sqrMagnitude < 1e-4f) _freeHangFacing = wish;

            float angleErr = Vector3.SignedAngle(_freeHangFacing, wish, Vector3.up);

            if (Mathf.Abs(angleErr) > freeHangMoveAngle)
            {
                // TURN: rotate the hand pair toward the input — one hand-step at a time, no net travel.
                // Commit the facing rotation only when a hand actually steps, so the turn is paced by holds.
                float stepDeg = Mathf.Clamp(angleErr, -freeHangTurnPerStep, freeHangTurnPerStep);
                bool stepRight = stepDeg < 0f;   // turning clockwise (right) → reach the right hand around first
                if (TryStepHandArc(stepRight, stepDeg) || TryStepHandArc(!stepRight, stepDeg))
                    _freeHangFacing = (Quaternion.AngleAxis(stepDeg, Vector3.up) * _freeHangFacing).normalized;
                else
                    _moveCooldown = traverseRetryInterval;   // no hold to arc to — pause the search
            }
            else
            {
                // Facing the input (within tolerance) → finish the last few degrees and leapfrog forward.
                _freeHangFacing = Vector3.RotateTowards(_freeHangFacing, wish, Mathf.Deg2Rad * freeHangTurnPerStep, 0f);
                Vector3 rhPos = _rig.GetCurrentPosition(ClimbEffector.RightHand);
                Vector3 lhPos = _rig.GetCurrentPosition(ClimbEffector.LeftHand);
                bool primaryRight = Vector3.Dot(rhPos - lhPos, wish) <= 0f;
                if (!TryStepHand(primaryRight, rhPos, lhPos, wish, AvgOutward()) &&
                    !TryStepHand(!primaryRight, rhPos, lhPos, wish, AvgOutward()))
                    _moveCooldown = traverseRetryInterval;   // stuck — pause the search
            }
        }

        /// <summary>
        /// Steps one hand along an arc around the OTHER (pivot) hand by <paramref name="stepDeg"/> about
        /// world up, walking the hand pair around to rotate the body's facing. Returns true if a hold was found.
        /// </summary>
        private bool TryStepHandArc(bool moveRight, float stepDeg)
        {
            ClimbEffector hand = moveRight ? ClimbEffector.RightHand : ClimbEffector.LeftHand;
            ClimbEffector pivotE = moveRight ? ClimbEffector.LeftHand : ClimbEffector.RightHand;
            Vector3 fromPos = _rig.GetCurrentPosition(hand);
            Vector3 pivot = _rig.GetCurrentPosition(pivotE);
            Vector3 ideal = pivot + Quaternion.AngleAxis(stepDeg, Vector3.up) * (fromPos - pivot);

            if (!FindFreeHangHold(ideal, fromPos, pivot, out Vector3 tp, out Quaternion tr))
                return false;

            _rig.SetPoseTarget(hand, tp, tr, traverseMoveDuration);
            if (moveRight) { _rhOutward = tr * Vector3.forward; _rhUp = tr * Vector3.up; }
            else { _lhOutward = tr * Vector3.forward; _lhUp = tr * Vector3.up; }
            _moveCooldown = moveInterval;
            return true;
        }

        /// <summary>
        /// Nearest hold to <paramref name="ideal"/> for a free-hang hand step: within the reach band of
        /// the moving hand, clear of and within max separation of the pivot hand, on the same face. No
        /// anti-cross / progress filters (the body is turning, not leapfrogging a fixed direction).
        /// </summary>
        private bool FindFreeHangHold(Vector3 ideal, Vector3 fromPos, Vector3 pivotPos, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            var s = _currentSurface;
            if (s == null || !s.HoldsReady) return false;

            Transform st = s.transform;
            var holds = s.Holds;
            float minSqr = minStepDistance * minStepDistance;
            float maxSqr = maxStepReach * maxStepReach;
            float clearSqr = handClearance * handClearance;
            float maxSepSqr = maxHandSeparation * maxHandSeparation;
            Vector3 climberOut = AvgOutward();
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);

                float fromSqr = (wp - fromPos).sqrMagnitude;
                if (fromSqr < minSqr || fromSqr > maxSqr) continue;            // reach band of the moving hand

                float pivSqr = (wp - pivotPos).sqrMagnitude;
                if (pivSqr < clearSqr || pivSqr > maxSepSqr) continue;         // clear of + within reach of the pivot hand

                Quaternion wr = st.rotation * holds[i].LocalRotation;
                if (Vector3.Dot(wr * Vector3.forward, climberOut) < facingCoherence) continue;   // same face

                float d = (wp - ideal).sqrMagnitude;
                if (d < best) { best = d; pos = wp; rot = wr; found = true; }
            }
            return found;
        }

        /// <summary>
        /// Best next hold for a traversing hand: within the reach band of the moving hand, clear of the
        /// other hand, lying along the input direction (progress, no back-and-forth), and on the same
        /// face as the climber (outward normal coherent — stops it grabbing the far side of a trunk).
        /// Among those, nearest to the ideal step point.
        /// </summary>
        private bool FindReachableHold(ClimbableSurface s, Vector3 ideal, Vector3 fromPos, Vector3 otherPos,
                                       Vector3 traverseDir, Vector3 climberOut, Vector3 bodyRight, float sideSign,
                                       float minProgress, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (s == null || !s.HoldsReady) return false;

            Transform st = s.transform;
            var holds = s.Holds;
            float minSqr = minStepDistance * minStepDistance;
            float maxSqr = maxStepReach * maxStepReach;
            float clearSqr = handClearance * handClearance;
            float maxSepSqr = maxHandSeparation * maxHandSeparation;
            float best = float.MaxValue;
            bool found = false;

            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);

                Vector3 fromDelta = wp - fromPos;
                float fromSqr = fromDelta.sqrMagnitude;
                if (fromSqr < minSqr || fromSqr > maxSqr) continue;                    // reach band

                Vector3 toOther = wp - otherPos;
                float otherSqr = toOther.sqrMagnitude;
                if (otherSqr < clearSqr) continue;                                     // clear of other hand
                if (otherSqr > maxSepSqr) continue;                                    // cap hand separation
                if (Vector3.Dot(toOther, bodyRight) * sideSign < -crossMargin) continue; // keep hands ~uncrossed (small slack)
                if (Vector3.Dot(fromDelta.normalized, traverseDir) < minProgress) continue; // progress in input dir

                Quaternion wr = st.rotation * holds[i].LocalRotation;
                Vector3 outward = wr * Vector3.forward;
                if (Vector3.Dot(outward, climberOut) < facingCoherence) continue;      // stay on the same face

                float d = (wp - ideal).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pos = wp;
                    rot = wr;
                    found = true;
                }
            }
            return found;
        }

        /// <summary>Nearest hold on a surface to <paramref name="target"/>, excluding one near <paramref name="exclude"/>.</summary>
        private bool FindHoldNear(ClimbableSurface s, Vector3 target, Vector3 exclude, out Vector3 pos, out Quaternion rot)
        {
            pos = Vector3.zero;
            rot = Quaternion.identity;
            if (s == null || !s.HoldsReady) return false;

            Transform st = s.transform;
            var holds = s.Holds;
            float best = float.MaxValue;
            bool found = false;
            for (int i = 0; i < holds.Count; i++)
            {
                Vector3 wp = st.TransformPoint(holds[i].LocalPosition);
                if ((wp - exclude).sqrMagnitude < 0.04f) continue;   // skip the same hold (~0.2 m)
                float d = (wp - target).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    pos = wp;
                    rot = st.rotation * holds[i].LocalRotation;
                    found = true;
                }
            }
            return found;
        }
    }
}
