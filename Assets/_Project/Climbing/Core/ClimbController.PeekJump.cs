using UnityEngine;
using Unity.Cinemachine;

namespace Game.Climbing
{
    /// <summary>
    /// ClimbController — BracedReady peek sub-state + climb jump-off: hand release / torso-head turn,
    /// latched side-swap, the jump-arc orbit camera (driven from CinemachineCore.CameraUpdatedEvent,
    /// AFTER the brain — a LateUpdate write would be re-damped), the trajectory preview, wind-up →
    /// leave-wall launch, the post-launch airborne hold, and the buffered mid-air wall catch.
    /// </summary>
    public partial class ClimbController
    {
        /// <summary>Cancel while braced: release the LOWEST hand (visual only — body pose keeps using BOTH holds),
        /// turn the upper torso/head toward it (gaze ~away from the wall), freeze the pendulum. Movement locks
        /// until Use (return) / Cancel (exit) / Jump (later increment).</summary>
        private void EnterBracedReady()
        {
            // Capture the wall-perpendicular launch direction NOW, while squared (before the peek turn) — the
            // cast is clean here, and the saved value is reused by the jump-off AND the trajectory line.
            _bracedReadyJumpDir = ComputeWallOutward();

            // Side = inverse of the camera look direction (dev preference): looking toward body-right → peek
            // LEFT (release left hand); looking left or straight → peek RIGHT.
            Vector3 lookFwd = _cam != null ? _cam.forward : transform.forward;
            bool releaseLeft = Vector3.Dot(lookFwd, transform.right) > 0f;
            _readySign = releaseLeft ? +1 : -1;
            _mode = ClimbMode.BracedReady;
            _readyReturning = false;
            _pendingSwap = false;
            _pendulumFrozen = true;
            _pendulum?.Reset(_rig.HandAverage);   // settle the pendulum so the body holds still
            // Announce the jump cost: the stamina bar flashes the chunk that WOULD be spent.
            // Nothing drains here — it's only paid if the jump actually fires (BeginClimbJump).
            _stamina?.SetPendingStaminaCost(_stamina.ClimbJumpStaminaCost);
            SnapshotBracedReadyCamera();          // compute the arc + freeze the rig + engage the arc camera
            if (logClimbEvents) Debug.Log($"[ClimbController] BracedReady — releasing {(releaseLeft ? "LEFT" : "RIGHT")} hand.");
        }

        /// <summary>BracedReady per-frame: ease the turn in/out, fade the released hand's IK pin, keep feet planted.</summary>
        private void TickBracedReady(float dt)
        {
            // Side-swap: once the peek is settled, pushing look PAST the opposite-side threshold un-turns this
            // side then re-peeks the other (latched; ignored mid-transition so a turn can't be interrupted). The
            // camera arc angle follows _readySign automatically (sweeps 0↔180 through 90).
            if (!_readyReturning && _readyT >= 0.999f && _input != null)
            {
                float lx = _input.LookInput.x;
                bool swap = (_readySign > 0 && lx > sideToggleThreshold) || (_readySign < 0 && lx < -sideToggleThreshold);
                if (swap) { _readyReturning = true; _pendingSwap = true; }
            }

            float target = _readyReturning ? 0f : 1f;
            _readyT = Mathf.MoveTowards(_readyT, target, dt / Mathf.Max(0.0001f, bracedReadyTurnDuration));

            // The loosened hand stays IK-driven but eases to an AUTHORED offset from its hold (composed on top
            // of the per-hand grip offset), so you control exactly where the let-go hand sits. HandAverage
            // still reads the un-offset effector targets, so the body pose is unaffected.
            ClimbEffector released = _readySign > 0 ? ClimbEffector.LeftHand : ClimbEffector.RightHand;
            bool isRight = released == ClimbEffector.RightHand;
            // Chiral: the loosened offset + rotation are authored for the LEFT hand; mirror them for the right.
            Vector3 looseOff = isRight ? Vector3.Scale(loosenedHandOffset, loosenedHandOffsetMirror) : loosenedHandOffset;
            Vector3 looseRotEuler = isRight ? Vector3.Scale(loosenedHandRotation, loosenedHandRotationMirror) : loosenedHandRotation;
            Vector3 gripRotEuler = isRight ? rightHandGripRotation : leftHandGripRotation;
            Quaternion loosenedRot = Quaternion.Slerp(Quaternion.identity, Quaternion.Euler(looseRotEuler), _readyT);
            Quaternion rotOff = Quaternion.Euler(gripRotEuler) * loosenedRot;

            if (loosenedHandAnchorHead && headBone != null)
            {
                // Position relative to the HEAD bone (loosenedHandOffset = head-local point), eased from the
                // on-hold pose. WriteEffector does holdPos + finalRot·posOffset, so feed the offset that lands
                // the hand on the head target — keeps HandAverage/cleanup on the same channel. (handHoldOffset
                // un-applied here; the loosened hand is leaving the hold anyway.)
                Quaternion finalRot = _rig.GetCurrentRotation(released) * rotOff;
                Vector3 holdPos = _rig.GetCurrentPosition(released);
                Vector3 onHold = holdPos + finalRot * handHoldOffset;
                Vector3 headTarget = headBone.TransformPoint(looseOff);
                Vector3 worldTarget = Vector3.Lerp(onHold, headTarget, _readyT);
                _rig.SetPositionOffset(released, Quaternion.Inverse(finalRot) * (worldTarget - holdPos));
            }
            else
            {
                // Hold-relative (default): offset in the grip frame, decoupled from the wrist twist via
                // Inverse(loosened)·offset so re-angling the wrist twists in place without sliding the hand.
                Vector3 baseOffset = handHoldOffset + looseOff * _readyT;
                _rig.SetPositionOffset(released, Quaternion.Inverse(loosenedRot) * baseOffset);
            }
            _rig.SetRotationOffset(released, rotOff);

            DriveBracedReadyCamera();   // root yaw + shoulder + side, lerped by _readyT toward the current side
            UpdateFeet(dt);             // feet stay planted; legs idle via the legs-only-when-moving logic

            if (_readyReturning && _readyT <= 0f)
            {
                if (_pendingSwap)
                {
                    _readySign = -_readySign;    // squared → flip to the other side, then ease back up
                    _pendingSwap = false;
                    _readyReturning = false;
                }
                else
                {
                    RestoreBracedReadyCamera();  // square + unfreeze the rig (resyncs from the now-squared root)
                    _mode = ClimbMode.Climbing;
                    _readyReturning = false;
                    _pendulumFrozen = false;
                    _stamina?.SetPendingStaminaCost(0f);   // backed out — the announced cost is never paid
                }
            }
        }

        /// <summary>Engage the jump-arc orbit camera: compute the arc from the current jump trajectory, freeze
        /// PlayerCameraRig, and start blending the camera onto the arc (applied in the after-brain event).</summary>
        private void SnapshotBracedReadyCamera()
        {
            if (_cam == null && Camera.main != null) _cam = Camera.main.transform;   // safety re-resolve
            ComputeArc();
            _arcActive = true;
            _arcBlend = 0f;
            _arcAngle = _readySign >= 0 ? 0f : 180f;   // start at the entry side's arc end
            _jumpCamHold = false;
            _reattachBlend = false;
            _cameraOverridden = true;
            _cameraRig?.SetFrozen(true);
        }

        /// <summary>Per-frame arc STATE update (the after-brain event does the actual camera move): sweep the angle toward the
        /// DESTINATION side's end (0 or 180, through 90 on a swap) and ease the blend in — out when returning to climb.
        /// During a swap the destination is the OTHER side already while _pendingSwap is set (before _readySign flips at
        /// the squared-up point), so the camera sweep spans the character's whole un-turn + re-turn — arcSwapDuration can
        /// use that full window and still arrive with the body.</summary>
        private void DriveBracedReadyCamera()
        {
            if (!_arcActive) return;
            int destSign = _pendingSwap ? -_readySign : _readySign;
            float target = destSign >= 0 ? 0f : 180f;
            _arcAngle = Mathf.MoveTowards(_arcAngle, target, (180f / Mathf.Max(0.0001f, arcSwapDuration)) * Time.deltaTime);
            bool returningToClimb = _readyReturning && !_pendingSwap;
            _arcBlend = Mathf.MoveTowards(_arcBlend, returningToClimb ? 0f : 1f, Time.deltaTime / Mathf.Max(0.0001f, arcBlendDuration));
        }

        /// <summary>Hand the camera back: capture Camera.main's current pose, unfreeze the rig, and blend the camera
        /// back to free-look over camReattachRestoreTime (after-brain event). Used for Use-return, exit, and mid-jump reattach.</summary>
        private void RestoreBracedReadyCamera()
        {
            if (!_cameraOverridden) return;
            if (_cam != null) { _reattachFromPos = _cam.position; _reattachFromRot = _cam.rotation; }
            _reattachBlend = true;
            _reattachT = 0f;
            _arcActive = false;
            _jumpCamHold = false;
            _cameraOverridden = false;
            _cameraRig?.SetFrozen(false);   // free-look resumes; the after-brain event blends the camera to it
        }

        /// <summary>Computes the jump-arc: center = midpoint of the wall-hit (initial) and the trajectory end;
        /// radius = half the horizontal distance (× arcRadiusScale, clamped); axes = jump dir (90°) + wall tangent
        /// (0°/180°). Far/90° height dips to the raw midpoint unless orbitAtCharacterHeight; arcHeightOffset raises
        /// the camera (look-at stays on the middle point).</summary>
        private void ComputeArc()
        {
            Vector3 initial = _bracedReadyJumpHit;
            int count = FillTrajectory();
            Vector3 end = count > 0 ? _trajPoints[count - 1] : initial + _bracedReadyJumpDir * 3f;
            Vector3 mid = (initial + end) * 0.5f;
            float baseInitialY = initial.y;
            float baseFarY = orbitAtCharacterHeight ? initial.y : mid.y;
            _arcInitialY = baseInitialY + arcHeightOffset;
            _arcFarY = baseFarY + arcHeightOffset;
            _arcCenter = new Vector3(mid.x, 0f, mid.z);
            float baseRadius = Vector3.Distance(new Vector3(initial.x, 0f, initial.z), new Vector3(end.x, 0f, end.z)) * 0.5f;
            _arcRadius = Mathf.Clamp(baseRadius * arcRadiusScale, arcRadiusClamp.x, arcRadiusClamp.y);
            _arcJumpDir = _bracedReadyJumpDir;
            _arcWallDir = Vector3.Cross(Vector3.up, _arcJumpDir).normalized;
            _arcLookAt = new Vector3(mid.x, baseFarY, mid.z);

            // Ellipse mode's crossing point: the predicted jump END pulled back toward the character
            // (along −jumpDir, so the camera can't clip the target climbable), at camera height.
            Vector3 pulled = end - _arcJumpDir * ellipseMidPullback;
            _ellipseMid = new Vector3(pulled.x, _arcFarY, pulled.z);
        }

        /// <summary>Camera position along the peek path at angle a (deg). Arc mode: the circular orbit
        /// around the middle point. Ellipse mode (cameraPathEllipse): a quadratic curve with the SAME
        /// side endpoints whose crossing point (at 90°) is the predicted jump end pulled back toward
        /// the character (<see cref="_ellipseMid"/>) — the camera sweeps out toward the target wall and
        /// back instead of orbiting. Settled peeks frame identically either way.</summary>
        private Vector3 CameraPathPos(float aDeg)
        {
            if (!cameraPathEllipse) return ArcCameraPos(aDeg);
            float t = Mathf.Clamp01(aDeg / 180f);
            Vector3 p0 = ArcCameraPos(0f);
            Vector3 p2 = ArcCameraPos(180f);
            // Quadratic Bézier whose control point is placed so the curve passes THROUGH _ellipseMid at t=0.5.
            Vector3 c = 2f * _ellipseMid - 0.5f * (p0 + p2);
            float u = 1f - t;
            return u * u * p0 + 2f * u * t * c + t * t * p2;
        }

        /// <summary>Camera position on the arc at angle a (deg): 0/180 = ±wall tangent (initial height), 90 = jump
        /// dir (far height). Horizontal circle of radius R around the trajectory midpoint.</summary>
        private Vector3 ArcCameraPos(float aDeg)
        {
            float a = aDeg * Mathf.Deg2Rad;
            Vector3 h = _arcCenter + _arcRadius * (Mathf.Cos(a) * _arcWallDir + Mathf.Sin(a) * _arcJumpDir);
            return new Vector3(h.x, Mathf.Lerp(_arcInitialY, _arcFarY, Mathf.Sin(a)), h.z);
        }

        /// <summary>Applies the climb camera override to the brain's output camera, fired from
        /// CinemachineCore.CameraUpdatedEvent so it runs AFTER the brain writes the transform (a LateUpdate write
        /// would be re-damped back toward free-look). Drives the peek arc (blended), the jump-hold (follow the
        /// character), or the reattach/exit blend back to free-look.</summary>
        private void OnCameraUpdated(CinemachineBrain brain)
        {
            if (_cam == null && Camera.main != null) { _mainCam = Camera.main; _cam = _mainCam.transform; }
            if (_cam == null) return;
            if (brain == null || brain.OutputCamera != _mainCam) return;   // only the camera we own
            if (_arcActive)
            {
                Vector3 arcPos = CameraPathPos(_arcAngle);
                Vector3 lookTarget = _arcLookAt;
                if (cameraPathEllipse)
                {
                    // Sweeping past/through the middle region: hand the gaze to the CHARACTER around
                    // the crossing, easing back onto the middle point toward either side — so a
                    // settled peek frames exactly like arc mode and only the swap's gaze differs.
                    float w = 1f - Mathf.Abs(_arcAngle - 90f) / 90f;   // 0 at the sides, 1 at the crossing
                    w = Mathf.SmoothStep(0f, 1f, w);
                    Vector3 chest = transform.position + Vector3.up * detectHeightOffset;
                    lookTarget = Vector3.Lerp(_arcLookAt, chest, w);
                }
                Vector3 toC = lookTarget - arcPos;
                Quaternion arcRot = toC.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(toC.normalized, Vector3.up) : _cam.rotation;
                _cam.SetPositionAndRotation(Vector3.Lerp(_cam.position, arcPos, _arcBlend),
                                            Quaternion.Slerp(_cam.rotation, arcRot, _arcBlend));
            }
            else if (_jumpCamHold)
            {
                // Parked viewpoint: stay at the arc spot and turn to keep the character (chest) framed.
                Vector3 toChar = (transform.position + Vector3.up * detectHeightOffset) - _jumpCamPos;
                Quaternion look = toChar.sqrMagnitude > 1e-6f
                    ? Quaternion.LookRotation(toChar.normalized, Vector3.up)
                    : _cam.rotation;
                // Ease the initial turn from the arc's look-at onto the character (no snap on the Jump press);
                // the target keeps moving during the ease, so this blends toward the LIVE look each frame.
                if (_jumpCamTurnT < 1f)
                {
                    _jumpCamTurnT = Mathf.Min(1f, _jumpCamTurnT + Time.deltaTime / Mathf.Max(0.0001f, jumpCamTurnDuration));
                    look = Quaternion.Slerp(_jumpCamFromRot, look, Mathf.SmoothStep(0f, 1f, _jumpCamTurnT));
                }
                _cam.SetPositionAndRotation(_jumpCamPos, look);
            }
            else if (_reattachBlend)
            {
                _reattachT = Mathf.Min(1f, _reattachT + Time.deltaTime / Mathf.Max(0.0001f, camReattachRestoreTime));
                float t = Mathf.SmoothStep(0f, 1f, _reattachT);
                _cam.SetPositionAndRotation(Vector3.Lerp(_reattachFromPos, _cam.position, t),
                                            Quaternion.Slerp(_reattachFromRot, _cam.rotation, t));
                if (_reattachT >= 1f) _reattachBlend = false;
            }
        }

        /// <summary>Fills _trajPoints with the predicted jump arc (motor integration), truncated at the first
        /// collision; returns the point count (endpoint = _trajPoints[count-1]).</summary>
        private int FillTrajectory()
        {
            if (_motor == null) return 0;
            int n = Mathf.Max(2, trajectoryPoints);
            if (_trajPoints == null || _trajPoints.Length != n) _trajPoints = new Vector3[n];
            Vector3 start = transform.position + Vector3.up * detectHeightOffset;   // ~chest launch origin
            _motor.PredictLaunchArc(start, _bracedReadyJumpDir * climbJumpOutward, climbJumpDecay, climbJumpUp,
                                    trajectoryTimeStep, _trajPoints);
            int count = _trajPoints.Length;
            for (int i = 1; i < _trajPoints.Length; i++)
                if (Physics.Linecast(_trajPoints[i - 1], _trajPoints[i], out RaycastHit hit,
                                     trajectoryCollisionMask, QueryTriggerInteraction.Ignore))
                { _trajPoints[i] = hit.point; count = i + 1; break; }
            return count;
        }

        /// <summary>Draws the predicted jump arc into the LineRenderer (matches the real jump via the motor sim).</summary>
        private void UpdateTrajectory()
        {
            if (peekTrajectoryLine == null) return;
            int count = FillTrajectory();
            if (count < 1) { HideTrajectory(); return; }
            peekTrajectoryLine.positionCount = count;
            for (int i = 0; i < count; i++) peekTrajectoryLine.SetPosition(i, _trajPoints[i]);
            if (!peekTrajectoryLine.enabled) peekTrajectoryLine.enabled = true;
        }

        private void HideTrajectory()
        {
            if (peekTrajectoryLine != null && peekTrajectoryLine.enabled) peekTrajectoryLine.enabled = false;
        }

        /// <summary>The flattened, wall-PERPENDICULAR "straight back" direction: raycast the chest into the wall
        /// (along −AvgOutward) and use the real surface normal — independent of hold-normal asymmetry. Falls back
        /// to the hold-normal average on a miss. Captured squared at BracedReady enter so the peek turn can't
        /// incline it; reused by the jump-off and the trajectory line.</summary>
        private Vector3 ComputeWallOutward()
        {
            Vector3 outward = Vector3.ProjectOnPlane(AvgOutward(), Vector3.up);
            outward = outward.sqrMagnitude > 1e-4f
                ? outward.normalized
                : Vector3.ProjectOnPlane(-transform.forward, Vector3.up).normalized;
            Vector3 chest = transform.position + Vector3.up * detectHeightOffset;
            if (Physics.Raycast(chest, -outward, out RaycastHit wallHit, maxReach, climbableLayers, QueryTriggerInteraction.Ignore))
            {
                Vector3 n = Vector3.ProjectOnPlane(wallHit.normal, Vector3.up);
                if (n.sqrMagnitude > 1e-4f) outward = n.normalized;
                _bracedReadyJumpHit = wallHit.point;   // arc "initial point"
            }
            else _bracedReadyJumpHit = chest;          // fallback
            return outward;
        }

        /// <summary>Jump from BracedReady: wind-up (square up + ClimbJump clip, hands still IK-pinned) until the
        /// clip's "leave wall" animation event (OnClimbJumpLeaveWall) — or the safety timeout — fires the launch.</summary>
        private void BeginClimbJump()
        {
            _climbJumping = true;
            _jumpTimer = 0f;
            _pendingSwap = false;
            _readyReturning = false;
            // Pay the announced jump cost (clamped at zero — an empty bar still jumps, but lands
            // fatigued) and stop the HUD preview flash.
            if (_stamina != null)
            {
                _stamina.SpendStamina(_stamina.ClimbJumpStaminaCost);
                _stamina.SetPendingStaminaCost(0f);
            }
            // Use the wall-perpendicular direction captured (squared, pre-turn) at BracedReady enter — so the
            // body's peek turn never inclines the launch. Recompute only if it's somehow unset.
            _jumpOutwardDir = _bracedReadyJumpDir.sqrMagnitude > 1e-4f ? _bracedReadyJumpDir : ComputeWallOutward();
            // Park the camera where the arc left it — it stays PUT through the jump, turning to track
            // the character (until reattach, landing, or the jumpCamFallRelease fall height). The turn
            // from the arc's look-at onto the character is eased over jumpCamTurnDuration.
            if (_cam != null)
            {
                _jumpCamPos = _cam.position;
                _jumpCamFromRot = _cam.rotation;
                _jumpCamTurnT = 0f;
            }
            _arcActive = false;
            _jumpCamHold = true;
            if (_animator != null && _climbLayerIndex >= 0)
            {
                if (_animator.HasState(_climbLayerIndex, _hClimbJump))
                    _animator.CrossFade(_hClimbJump, 0.1f, _climbLayerIndex, 0f);
                else
                    Debug.LogWarning("[ClimbController] No 'ClimbJump' state on ClimbingLayer — jump uses the timed fallback only.");
            }
            if (logClimbEvents) Debug.Log("[ClimbController] Climb jump-off — wind-up.");
        }

        /// <summary>Jump wind-up: square the body + camera back up (peek → neutral) while the ClimbJump clip plays
        /// and the hands stay pinned. The launch fires from OnClimbJumpLeaveWall (or the safety timeout).</summary>
        private void TickClimbJump(float dt)
        {
            _jumpTimer += dt;
            _readyT = Mathf.MoveTowards(_readyT, 0f, dt / Mathf.Max(0.0001f, bracedReadyTurnDuration));  // square the body up
            // Camera is held (following the character) by the _jumpCamHold branch in LateUpdate.
            UpdateFeet(dt);
            if (_jumpTimer >= climbJumpSafetyTimeout) FinishClimbJump();
        }

        /// <summary>Animation event on the ClimbJump clip — the frame the character leaves the wall.</summary>
        public void OnClimbJumpLeaveWall()
        {
            if (_climbJumping) FinishClimbJump();
        }

        /// <summary>Leaves the wall: apply the launch, hand control to the motor (airborne), and enter the airborne
        /// HOLD — the ClimbJump last frame + the camera viewpoint stay held until the character lands, reattaches,
        /// or falls past jumpRagdollFallHeight. Re-grab is blocked until the post-jump 180° turn completes.</summary>
        private void FinishClimbJump()
        {
            _climbJumping = false;
            _jumpStartY = transform.position.y;
            _motor?.AddLaunchVelocity(_jumpOutwardDir * climbJumpOutward, climbJumpDecay);
            _motor?.SetVerticalVelocity(climbJumpUp);
            _motor?.SuppressAirControl(climbJumpNoAirControlTime);   // clean arc — no input steering/bending
            _controlLock?.ReleaseExternalControl();   // motor flies the body from here (airborne)
            _isClimbing = false;       // motor drives + grab/catch detection re-enables; the hold runs in Update
            _jumpAirborne = true;      // hold ClimbJump's last frame + camera until land / catch / fall
            _stamina?.SetClimbState(false, false);
            _stamina?.HoldStaminaUntilGrounded();   // no regen mid-arc — resumes on landing or reattach
            // Turn 180° to face the jump direction over jumpTurnDuration; reattach is blocked until it completes.
            _jumpTurnFrom = transform.rotation;
            _jumpTurnTo = _jumpOutwardDir.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(_jumpOutwardDir, Vector3.up)
                : transform.rotation;
            _jumpTurnT = 0f;
            _jumpReattachBlocked = true;
            if (logClimbEvents) Debug.Log("[ClimbController] Climb jump-off — launched (airborne hold).");
        }

        /// <summary>Airborne hold (motor flies the body): fade the FBBIK out (hands lerp off the holds onto the clip)
        /// but keep the climb layer FULL so the ClimbJump pose (held last frame) shows — not the locomotion fall —
        /// and hold the camera viewpoint. Ends on land, on a fall past jumpRagdollFallHeight, or when
        /// PlayerRagdoll's hard-fall monitor ragdolls the body (RagdollStarting → OnRagdollStarting).
        /// Reattaching is the separate buffered-Use grab path (Update), which resets this hold.</summary>
        private void TickJumpAirborne(float dt)
        {
            // Turn the body 180° to face the jump direction (cosmetic — the launch is world-space). Air control
            // is suppressed during this, so the motor won't fight the rotation. Unblocks reattach when done.
            if (_jumpTurnT < 1f)
            {
                _jumpTurnT = Mathf.Min(1f, _jumpTurnT + dt / Mathf.Max(0.0001f, jumpTurnDuration));
                transform.rotation = Quaternion.Slerp(_jumpTurnFrom, _jumpTurnTo, Mathf.SmoothStep(0f, 1f, _jumpTurnT));
                if (_jumpTurnT >= 1f) _jumpReattachBlocked = false;
            }

            if (_rig != null)
            {
                _rig.SetMasterWeight(Mathf.MoveTowards(_rig.MasterWeight, 0f, climbJumpIkFade > 0f ? dt / climbJumpIkFade : 1f));
                _rig.Tick(dt);
            }
            if (_animator != null && _climbLayerIndex >= 0) _animator.SetLayerWeight(_climbLayerIndex, 1f);
            // Camera parked at the arc spot, tracking the character (the _jumpCamHold branch in OnCameraUpdated).

            // Release the parked viewpoint early on a real fall — the camera eases back to normal
            // free-look while the ClimbJump pose hold continues (land / reattach / ragdoll height
            // still end the hold itself).
            if (_cameraOverridden && (_jumpStartY - transform.position.y) > jumpCamFallRelease)
                RestoreBracedReadyCamera();

            CharacterController cc = _motor?.Controller;
            // Land only once descending, so the launch frame's stale isGrounded doesn't end the hold instantly.
            bool landed = cc != null && cc.isGrounded && _motor.VerticalVelocity <= 0f;
            bool fellTooFar = (_jumpStartY - transform.position.y) > jumpRagdollFallHeight;
            if (landed || fellTooFar)
            {
                // Hard falls ragdoll via PlayerRagdoll's own apex monitor (its RagdollStarting event
                // ends this hold too) — this branch is the fallback that drops the pose/camera hold
                // when the ragdoll system is absent, disabled, or tuned to a greater height.
                EndJumpAirborne();
                if (landed) ClimbJumpLanded?.Invoke();   // feet-down after a jump — tethered tools drop off here
            }
        }

        /// <summary>Ends the airborne hold — drop the ClimbJump pose + camera back to locomotion.</summary>
        private void EndJumpAirborne()
        {
            _jumpAirborne = false;
            _jumpReattachBlocked = false;
            _rig?.SetMasterWeight(0f);
            if (_animator != null)
            {
                _animator.SetBool(_hIsClimbing, false);
                if (_climbLayerIndex >= 0) _animator.SetLayerWeight(_climbLayerIndex, 0f);
                if (_climbLegsLayerIndex >= 0) _animator.SetLayerWeight(_climbLegsLayerIndex, 0f);
            }
            if (_cameraOverridden) RestoreBracedReadyCamera();
            if (ik != null) ik.enabled = false;   // solver back to sleep (no reattach happened)
            if (logClimbEvents) Debug.Log("[ClimbController] Jump airborne hold ended (land / fall).");
        }

        /// <summary>Post-solve head turn: the body ROOT already yawed by the torso angle (grip-preserving, in
        /// UpdateBodyPose); add the EXTRA head yaw here so the gaze lands ~away from the wall. The head isn't
        /// IK-driven, so rotating it post-solve drags no hand. Scaled by the eased <see cref="_readyT"/>.</summary>
        private void ApplyBracedReadyTurn()
        {
            if (_mode != ClimbMode.BracedReady || _rig == null || headBone == null) return;
            float w = _readyT * _rig.MasterWeight;
            if (w <= 0.001f) return;
            float sign = bracedReadyTurnInvert ? -_readySign : _readySign;
            headBone.rotation = Quaternion.AngleAxis(bracedReadyHeadAngle * w * sign, transform.up) * headBone.rotation;
        }

    }
}
